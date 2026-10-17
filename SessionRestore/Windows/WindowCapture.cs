using System.Diagnostics;
using System.Runtime.InteropServices;
using static DisplayLullaby.SessionRestore.SessionNative;

namespace DisplayLullaby.SessionRestore;

/// <summary>Enumerates real, user-visible top-level windows and the processes that own them.</summary>
internal static unsafe class WindowCapture
{
    public static SessionSnapshot Capture(string reason, IgnoreMatcher ignore)
    {
        using var dpiScope = DpiAwareness.PerMonitorV2();
        var monitors = GetMonitors();
        var byProcess = new Dictionary<uint, (ProcessDetails Details, List<WindowSnapshot> Windows)>();
        var ownPid = (uint)Environment.ProcessId;

        foreach (var hwnd in EnumerateAppWindows())
        {
            GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == 0 || pid == ownPid)
            {
                continue;
            }

            if (!byProcess.TryGetValue(pid, out var entry))
            {
                var details = ProcessDetails.Query(pid);
                if (details is null || ignore.IsIgnored(details.ProcessName, details.ExePath, details.AppUserModelId))
                {
                    continue;
                }

                entry = (details, new List<WindowSnapshot>());
                byProcess[pid] = entry;
            }

            if (TryReadWindow(hwnd, monitors, out var window))
            {
                entry.Windows.Add(window);
            }
        }

        var apps = byProcess.Values
            .Where(static e => e.Windows.Count > 0)
            .Select(static e => new AppSnapshot(
                e.Details.ProcessName,
                e.Details.ExePath,
                e.Details.CommandLine,
                WorkingDirectory: e.Details.ExePath is null ? null : Path.GetDirectoryName(e.Details.ExePath),
                e.Details.AppUserModelId,
                e.Details.AppUserModelId is not null,
                e.Windows))
            .ToList();

        return new SessionSnapshot(SessionSnapshot.CurrentVersion, DateTimeOffset.Now, Environment.MachineName, reason, apps, monitors);
    }

    /// <summary>
    /// Live monitors in physical pixels, ordered primary first then left-to-right, top-to-bottom,
    /// so <see cref="MonitorSnapshot.Index"/> is stable when device names change.
    /// </summary>
    public static List<MonitorSnapshot> GetMonitors()
    {
        var raw = new List<(string Device, WindowRect Bounds, WindowRect Work, uint Dpi, bool Primary)>();
        EnumDisplayMonitors(0, 0, (monitor, _, _, _) =>
        {
            var info = new MonitorInfoEx { Size = (uint)sizeof(MonitorInfoEx) };
            if (GetMonitorInfo(monitor, ref info))
            {
                var dpi = GetDpiForMonitor(monitor, MdtEffectiveDpi, out var dpiX, out _) == 0 ? dpiX : 96u;
                raw.Add((new string(info.Device), ToRect(info.Monitor), ToRect(info.Work), dpi,
                    (info.Flags & MonitorInfoPrimary) != 0));
            }

            return true;
        }, 0);

        return raw
            .OrderByDescending(static m => m.Primary)
            .ThenBy(static m => m.Bounds.Left)
            .ThenBy(static m => m.Bounds.Top)
            .Select(static (m, i) => new MonitorSnapshot(i, m.Device, m.Bounds, m.Work, m.Dpi, m.Primary))
            .ToList();
    }

    private static WindowRect ToRect(Rect r) => new(r.Left, r.Top, r.Right, r.Bottom);

    public static List<RunningProcessInfo> GetRunningProcesses()
    {
        var result = new List<RunningProcessInfo>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                var details = ProcessDetails.Query((uint)process.Id, includeCommandLine: false);
                result.Add(details is null
                    ? new RunningProcessInfo(process.ProcessName, null, null)
                    : new RunningProcessInfo(details.ProcessName, details.ExePath, details.AppUserModelId));
            }
        }

        return result;
    }

    public static List<LiveWindow> GetWindowsForProcesses(Func<uint, bool> pidFilter)
    {
        var result = new List<LiveWindow>();
        foreach (var hwnd in EnumerateAppWindows())
        {
            GetWindowThreadProcessId(hwnd, out var pid);
            if (pidFilter(pid))
            {
                result.Add(new LiveWindow(hwnd, ReadText(hwnd, title: true), ReadText(hwnd, title: false)));
            }
        }

        return result;
    }

    public static List<nint> EnumerateAppWindows()
    {
        var handles = new List<nint>();
        EnumWindows((hwnd, _) =>
        {
            if (IsAppWindow(hwnd))
            {
                handles.Add(hwnd);
            }

            return true;
        }, 0);
        return handles;
    }

    /// <summary>Same heuristic the taskbar uses: visible, not cloaked, unowned (or WS_EX_APPWINDOW), no tool windows.</summary>
    private static bool IsAppWindow(nint hwnd)
    {
        if (!IsWindowVisible(hwnd))
        {
            return false;
        }

        var exStyle = (long)GetWindowLongPtr(hwnd, GwlExStyle);
        if ((exStyle & WsExToolWindow) != 0 || (exStyle & WsExNoActivate) != 0)
        {
            return false;
        }

        if (GetWindow(hwnd, GwOwner) != 0 && (exStyle & WsExAppWindow) == 0)
        {
            return false;
        }

        // Cloaked = hidden UWP frames, windows on other virtual desktops are also cloaked (accepted limitation).
        if (DwmGetWindowAttribute(hwnd, DwmwaCloaked, out var cloaked, sizeof(int)) == 0 && cloaked != 0)
        {
            return false;
        }

        return ReadText(hwnd, title: true).Length > 0;
    }

    private static bool TryReadWindow(nint hwnd, IReadOnlyList<MonitorSnapshot> monitors, out WindowSnapshot window)
    {
        var placement = new WindowPlacement { Length = (uint)sizeof(WindowPlacement) };
        if (!GetWindowPlacement(hwnd, ref placement))
        {
            window = null!;
            return false;
        }

        var state = placement.ShowCmd switch
        {
            SwShowMaximized => WindowShowState.Maximized,
            SwShowMinimized or SwShowMinNoActive or 6 => WindowShowState.Minimized,
            _ => WindowShowState.Normal
        };

        // A minimized window that was maximized before minimizing should come back maximized.
        if (state == WindowShowState.Minimized && (placement.Flags & WpfRestoreToMaximized) != 0)
        {
            state = WindowShowState.Maximized;
        }

        var r = placement.NormalPosition;
        var normal = new WindowRect(r.Left, r.Top, r.Right, r.Bottom);

        // Borderless/fullscreen-style apps (ImageGlass) stay SW_SHOWNORMAL while covering a monitor.
        // WINDOWPLACEMENT is in workspace coordinates, so compare the real screen rect instead.
        FillMonitorRef? fills = null;
        if (state == WindowShowState.Normal && GetWindowRect(hwnd, out var screen))
        {
            fills = MonitorFill.Detect(ToRect(screen), monitors);
        }

        window = new WindowSnapshot(
            ReadText(hwnd, title: true),
            ReadText(hwnd, title: false),
            normal,
            state,
            GetMonitorDevice(hwnd),
            fills);
        return true;
    }

    private static string? GetMonitorDevice(nint hwnd)
    {
        var info = new MonitorInfoEx { Size = (uint)sizeof(MonitorInfoEx) };
        var monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        return monitor != 0 && GetMonitorInfo(monitor, ref info) ? new string(info.Device) : null;
    }

    private static string ReadText(nint hwnd, bool title)
    {
        var buffer = stackalloc char[512];
        var length = title ? GetWindowText(hwnd, buffer, 512) : GetClassName(hwnd, buffer, 512);
        return length > 0 ? new string(buffer, 0, length) : string.Empty;
    }
}

internal sealed unsafe record ProcessDetails(string ProcessName, string? ExePath, string? CommandLine, string? AppUserModelId)
{
    /// <summary>
    /// Returns null for processes we cannot (or should not) relaunch: protected/system processes,
    /// and elevated processes when we are not elevated (token cannot be opened, or TokenElevation set).
    /// </summary>
    public static ProcessDetails? Query(uint pid, bool includeCommandLine = true)
    {
        var handle = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (handle == 0)
        {
            return null;
        }

        try
        {
            var exePath = QueryImagePath(handle);
            if (exePath is null)
            {
                return null;
            }

            var name = Path.GetFileNameWithoutExtension(exePath);
            if (includeCommandLine && IsElevatedOrInaccessible(handle) && !Environment.IsPrivilegedProcess)
            {
                return null;
            }

            return new ProcessDetails(
                name,
                exePath,
                includeCommandLine ? QueryCommandLine(handle) : null,
                QueryAppUserModelId(handle));
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    private static string? QueryImagePath(nint handle)
    {
        var buffer = stackalloc char[1024];
        uint size = 1024;
        return QueryFullProcessImageName(handle, 0, buffer, ref size) ? new string(buffer, 0, (int)size) : null;
    }

    /// <summary>ProcessCommandLineInformation (Windows 8.1+) avoids WMI and manual PEB reads.</summary>
    private static string? QueryCommandLine(nint handle)
    {
        NtQueryInformationProcess(handle, ProcessCommandLineInformation, null, 0, out var needed);
        if (needed == 0 || needed > 64 * 1024)
        {
            return null;
        }

        var buffer = NativeMemory.Alloc(needed);
        try
        {
            if (NtQueryInformationProcess(handle, ProcessCommandLineInformation, buffer, needed, out _) != 0)
            {
                return null;
            }

            var unicode = (UnicodeString*)buffer;
            return unicode->Buffer == null ? null : new string(unicode->Buffer, 0, unicode->Length / 2);
        }
        finally
        {
            NativeMemory.Free(buffer);
        }
    }

    private static string? QueryAppUserModelId(nint handle)
    {
        uint length = 256;
        var buffer = stackalloc char[256];
        return GetApplicationUserModelId(handle, ref length, buffer) == 0 && length > 1
            ? new string(buffer, 0, (int)length - 1)
            : null;
    }

    private static bool IsElevatedOrInaccessible(nint handle)
    {
        if (!OpenProcessToken(handle, TokenQuery, out var token))
        {
            return true;
        }

        try
        {
            int elevated;
            return !GetTokenInformation(token, TokenElevation, &elevated, sizeof(int), out _) || elevated != 0;
        }
        finally
        {
            CloseHandle(token);
        }
    }
}

/// <summary>Scoped per-monitor-v2 DPI awareness for the calling thread, so coordinates are physical pixels.</summary>
internal readonly partial struct DpiAwareness : IDisposable
{
    private static readonly nint PerMonitorAwareV2 = -4;
    private readonly nint _previous;

    private DpiAwareness(nint previous) => _previous = previous;

    public static DpiAwareness PerMonitorV2() => new(SetThreadDpiAwarenessContext(PerMonitorAwareV2));

    public void Dispose()
    {
        if (_previous != 0)
        {
            SetThreadDpiAwarenessContext(_previous);
        }
    }

    [System.Runtime.InteropServices.LibraryImport("user32.dll")]
    private static partial nint SetThreadDpiAwarenessContext(nint context);
}

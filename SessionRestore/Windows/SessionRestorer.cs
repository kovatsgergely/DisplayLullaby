using System.Diagnostics;
using static DisplayLullaby.SessionRestore.SessionNative;

namespace DisplayLullaby.SessionRestore;

internal sealed record RestoreResult(int Launched, int Placed, int Skipped, IReadOnlyList<string> Errors);

/// <summary>Executes a restore plan: launch missing apps, wait for their windows, apply placements.</summary>
internal sealed unsafe class SessionRestorer(SessionRestoreSettings settings)
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);

    public RestoreResult Restore(SessionSnapshot snapshot, CancellationToken cancellationToken)
    {
        // Physical pixels on every monitor, regardless of the calling thread's default awareness.
        using var dpiScope = DpiAwareness.PerMonitorV2();
        var monitors = WindowCapture.GetMonitors();
        var running = WindowCapture.GetRunningProcesses();
        var plan = RestorePlanner.Plan(
            snapshot,
            running,
            new IgnoreMatcher(settings.Ignore),
            settings.SelfRestoring.ToArray(),
            Process.GetCurrentProcess().ProcessName);

        int launched = 0, placed = 0, skipped = 0;
        var errors = new List<string>();
        var usedWindows = new HashSet<nint>();

        foreach (var item in plan)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (item.Action == RestoreAction.Skip)
            {
                skipped++;
                continue;
            }

            try
            {
                var existingPids = PidsFor(item.App);
                if (item.Action == RestoreAction.LaunchAndPlace)
                {
                    Launch(item.App);
                    launched++;
                }

                var selfRestoring = RestorePlanner.IsSelfRestoring(item.App, settings.SelfRestoring.ToArray());
                var windows = WaitForWindows(item.App, item.Action == RestoreAction.LaunchAndPlace ? existingPids : null, usedWindows, cancellationToken);
                foreach (var (saved, live) in WindowMatcher.Match(item.App.Windows, windows))
                {
                    if (Apply(live.Handle, saved, monitors))
                    {
                        usedWindows.Add(live.Handle);
                        placed++;
                    }
                }

                // Self-restoring apps (browsers, Office, VS Code) may open their windows late; nothing else to do.
                _ = selfRestoring;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                errors.Add($"{item.App.ProcessName}: {ex.Message}");
            }
        }

        return new RestoreResult(launched, placed, skipped, errors);
    }

    private void Launch(AppSnapshot app)
    {
        var (fileName, capturedArguments) = CommandLineSplitter.Split(app.CommandLine ?? string.Empty, app.ExePath);
        var arguments = LaunchArguments.Rewrite(
            capturedArguments,
            settings.DropArguments,
            app.Windows.Select(static w => w.Title),
            File.Exists,
            Directory.Exists);

        if (app.IsPackaged && app.AppUserModelId is not null)
        {
            // Store apps (e.g. ImageGlass) opened with a file: activate with the file argument so it reopens.
            // PackagedArguments drops activation noise (-ServerName:, -Embedding); Rewrite already applied the drop list.
            var packagedArgs = arguments.Length == 0
                ? null
                : PackagedArguments.Extract($"\"{fileName}\" {arguments}", fileName);
            if (packagedArgs is not null && PackagedActivation.TryActivate(app.AppUserModelId, packagedArgs))
            {
                return;
            }

            // shell:AppsFolder activation works for any registered AUMID without COM interop.
            Process.Start(new ProcessStartInfo("explorer.exe", $"shell:AppsFolder\\{app.AppUserModelId}") { UseShellExecute = false });
            return;
        }

        var startInfo = new ProcessStartInfo(fileName, arguments)
        {
            UseShellExecute = true,
            WorkingDirectory = app.WorkingDirectory is { } dir && Directory.Exists(dir) ? dir : string.Empty
        };
        Process.Start(startInfo)?.Dispose();
    }

    private static HashSet<uint> PidsFor(AppSnapshot app) =>
        Process.GetProcessesByName(app.ProcessName).Select(static p =>
        {
            using (p)
            {
                return (uint)p.Id;
            }
        }).ToHashSet();

    /// <summary>
    /// Waits for windows of processes with the app's name. Matching by name rather than the launched PID
    /// handles launcher/stub processes (Office, Electron, AppsFolder activation) that hand off to another process.
    /// When <paramref name="preexistingPids"/> is given, windows of new processes are preferred.
    /// </summary>
    private List<LiveWindow> WaitForWindows(AppSnapshot app, HashSet<uint>? preexistingPids, HashSet<nint> used, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow.AddSeconds(settings.WindowWaitSeconds);
        var wanted = app.Windows.Count;
        List<LiveWindow> windows = [];

        while (true)
        {
            var pids = PidsFor(app);
            windows = WindowCapture.GetWindowsForProcesses(pids.Contains).Where(w => !used.Contains(w.Handle)).ToList();
            if (preexistingPids is not null)
            {
                var fresh = windows.Where(w =>
                {
                    GetWindowThreadProcessId(w.Handle, out var pid);
                    return !preexistingPids.Contains(pid);
                }).ToList();
                if (fresh.Count > 0)
                {
                    windows = fresh;
                }
            }

            if (windows.Count >= wanted || preexistingPids is null || DateTime.UtcNow >= deadline)
            {
                return windows;
            }

            cancellationToken.WaitHandle.WaitOne(PollInterval);
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    private static bool Apply(nint hwnd, WindowSnapshot saved, IReadOnlyList<MonitorSnapshot> monitors)
    {
        if (!IsWindow(hwnd))
        {
            return false;
        }

        var b = PlacementPlanner.ResolveNormalBounds(saved, monitors);
        var placement = new WindowPlacement
        {
            Length = (uint)sizeof(WindowPlacement),
            ShowCmd = saved.ShowState switch
            {
                WindowShowState.Maximized => SwShowMaximized,
                WindowShowState.Minimized => SwShowMinNoActive,
                _ => SwShowNormal
            },
            NormalPosition = new Rect { Left = b.Left, Top = b.Top, Right = b.Right, Bottom = b.Bottom }
        };

        // SetWindowPlacement clamps rectangles onto a visible monitor if the saved monitor is gone.
        if (!SetWindowPlacement(hwnd, ref placement))
        {
            return false;
        }

        if (saved.FillsMonitor is not null && saved.ShowState == WindowShowState.Normal)
        {
            // WINDOWPLACEMENT uses workspace coordinates; force exact screen bounds. Move first, then size:
            // crossing to a monitor with a different DPI triggers WM_DPICHANGED, which rescales the window.
            const uint flags = SwpNoZOrder | SwpNoActivate | SwpNoOwnerZOrder;
            SetWindowPos(hwnd, 0, b.Left, b.Top, b.Width, b.Height, flags);
            SetWindowPos(hwnd, 0, b.Left, b.Top, b.Width, b.Height, flags);
        }

        return true;
    }
}

using System.Diagnostics;
using static DisplayLullaby.SessionRestore.SessionNative;

namespace DisplayLullaby.SessionRestore;

internal sealed record RestoreResult(int Launched, int Placed, int Skipped, IReadOnlyList<string> Errors, int Snapped = 0, int SnapFailed = 0);

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

        int launched = 0, placed = 0, skipped = 0, snapped = 0, snapFailed = 0;
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
                    var outcome = Apply(live.Handle, saved, monitors);
                    if (outcome.Placed)
                    {
                        usedWindows.Add(live.Handle);
                        placed++;
                    }

                    if (outcome.Snap == SnapOutcome.Snapped)
                    {
                        snapped++;
                    }
                    else if (outcome.Snap == SnapOutcome.Failed)
                    {
                        snapFailed++;
                        if (!string.IsNullOrEmpty(outcome.SnapDetail))
                        {
                            errors.Add($"{item.App.ProcessName}: {outcome.SnapDetail}");
                        }
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

        return new RestoreResult(launched, placed, skipped, errors, snapped, snapFailed);
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

    private static ApplyOutcome Apply(nint hwnd, WindowSnapshot saved, IReadOnlyList<MonitorSnapshot> monitors)
    {
        if (!IsWindow(hwnd))
        {
            return ApplyOutcome.Missed;
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
            return ApplyOutcome.Missed;
        }

        if (saved.FillsMonitor is not null && saved.ShowState == WindowShowState.Normal)
        {
            // WINDOWPLACEMENT uses workspace coordinates; force exact screen bounds. Move first, then size:
            // crossing to a monitor with a different DPI triggers WM_DPICHANGED, which rescales the window.
            const uint flags = SwpNoZOrder | SwpNoActivate | SwpNoOwnerZOrder;
            SetWindowPos(hwnd, 0, b.Left, b.Top, b.Width, b.Height, flags);
            SetWindowPos(hwnd, 0, b.Left, b.Top, b.Width, b.Height, flags);
        }

        if (saved.Arrangement is { } arrangement && saved.ShowState == WindowShowState.Normal)
        {
            return ApplySnap(hwnd, saved, arrangement, monitors);
        }

        return ApplyOutcome.PlacedOnly;
    }

    private static ApplyOutcome ApplySnap(
        nint hwnd,
        WindowSnapshot saved,
        WindowArrangement arrangement,
        IReadOnlyList<MonitorSnapshot> monitors)
    {
        var monitor = ResolveSnapMonitor(saved, monitors);
        if (monitor is null)
        {
            System.Diagnostics.Debug.WriteLine($"Snap skip: no monitor for '{saved.Title}'");
            return ApplyOutcome.PlacedOnly;
        }

        var target = SnapArrangement.TargetHalf(monitor.WorkArea, arrangement);
        if (MatchesHalf(hwnd, target))
        {
            System.Diagnostics.Debug.WriteLine($"Snap already matched for '{saved.Title}' ({arrangement})");
            return new ApplyOutcome(true, SnapOutcome.Snapped, null);
        }

        // Seed a floating size on the target monitor so Win+Arrow snaps instead of cycling monitors.
        SeedOnMonitor(hwnd, monitor.WorkArea, arrangement);

        if (!TryBringToForeground(hwnd))
        {
            var detail = $"snap skipped (could not focus '{saved.Title}')";
            System.Diagnostics.Debug.WriteLine(detail);
            return new ApplyOutcome(true, SnapOutcome.Failed, detail);
        }

        var electron = saved.ClassName.Equals("Chrome_WidgetWin_1", StringComparison.Ordinal);
        var attempts = electron ? 3 : 2;
        for (var i = 0; i < attempts; i++)
        {
            if (i > 0)
            {
                Thread.Sleep(electron ? 350 : 200);
                if (!TryBringToForeground(hwnd))
                {
                    break;
                }
            }

            SendWinArrow(arrangement);
            Thread.Sleep(electron ? 400 : 250);
            if (MatchesHalf(hwnd, target))
            {
                System.Diagnostics.Debug.WriteLine($"Snap OK '{saved.Title}' -> {arrangement} (attempt {i + 1})");
                return new ApplyOutcome(true, SnapOutcome.Snapped, null);
            }
        }

        var fail = $"snap failed for '{saved.Title}' ({arrangement})";
        System.Diagnostics.Debug.WriteLine(fail);
        return new ApplyOutcome(true, SnapOutcome.Failed, fail);
    }

    private static MonitorSnapshot? ResolveSnapMonitor(WindowSnapshot saved, IReadOnlyList<MonitorSnapshot> monitors)
    {
        if (monitors.Count == 0)
        {
            return null;
        }

        if (!string.IsNullOrEmpty(saved.MonitorDevice))
        {
            var byName = monitors.FirstOrDefault(m =>
                m.DeviceName.Equals(saved.MonitorDevice, StringComparison.OrdinalIgnoreCase));
            if (byName is not null)
            {
                return byName;
            }
        }

        var hint = saved.NormalBounds;
        var cx = (hint.Left + hint.Right) / 2;
        var cy = (hint.Top + hint.Bottom) / 2;
        var containing = monitors.FirstOrDefault(m =>
            cx >= m.Bounds.Left && cx < m.Bounds.Right && cy >= m.Bounds.Top && cy < m.Bounds.Bottom);
        return containing ?? monitors.FirstOrDefault(m => m.IsPrimary) ?? monitors[0];
    }

    private static bool MatchesHalf(nint hwnd, WindowRect target)
    {
        var visible = ReadVisibleBounds(hwnd);
        return visible is { } rect && MonitorFill.NearlyEquals(rect, target, MonitorFill.DefaultTolerancePx);
    }

    private static WindowRect? ReadVisibleBounds(nint hwnd)
    {
        if (DwmGetWindowAttributeRect(hwnd, DwmwaExtendedFrameBounds, out var frame, sizeof(Rect)) == 0
            && frame.Right > frame.Left
            && frame.Bottom > frame.Top)
        {
            return new WindowRect(frame.Left, frame.Top, frame.Right, frame.Bottom);
        }

        return GetWindowRect(hwnd, out var screen)
            ? new WindowRect(screen.Left, screen.Top, screen.Right, screen.Bottom)
            : null;
    }

    private static void SeedOnMonitor(nint hwnd, WindowRect workArea, WindowArrangement arrangement)
    {
        var half = SnapArrangement.TargetHalf(workArea, arrangement);
        // Floating seed centered in the target half (~60% of half width/height) so Win+Arrow engages snap.
        var width = Math.Max(320, half.Width * 3 / 5);
        var height = Math.Max(240, half.Height * 3 / 5);
        var left = half.Left + (half.Width - width) / 2;
        var top = half.Top + (half.Height - height) / 2;
        ShowWindow(hwnd, (int)SwShowNormal);
        const uint flags = SwpNoZOrder | SwpNoOwnerZOrder | SwpShowWindow;
        SetWindowPos(hwnd, 0, left, top, width, height, flags);
    }

    private static bool TryBringToForeground(nint hwnd)
    {
        if (!IsWindow(hwnd))
        {
            return false;
        }

        var target = GetAncestor(hwnd, GaRoot);
        if (target == 0)
        {
            target = hwnd;
        }

        ShowWindow(target, (int)SwShowNormal);
        if (GetForegroundWindow() == target)
        {
            return true;
        }

        // ALT key trick: unlock foreground permission for this process.
        SendAltPulse();

        var fore = GetForegroundWindow();
        var foreThread = fore == 0 ? 0u : GetWindowThreadProcessId(fore, out _);
        var targetThread = GetWindowThreadProcessId(target, out _);
        var current = GetCurrentThreadId();
        var attachedFore = false;
        var attachedTarget = false;
        try
        {
            if (foreThread != 0 && foreThread != current)
            {
                attachedFore = AttachThreadInput(current, foreThread, true);
            }

            if (targetThread != 0 && targetThread != current && targetThread != foreThread)
            {
                attachedTarget = AttachThreadInput(current, targetThread, true);
            }

            BringWindowToTop(target);
            return SetForegroundWindow(target) || GetForegroundWindow() == target;
        }
        finally
        {
            if (attachedTarget)
            {
                AttachThreadInput(current, targetThread, false);
            }

            if (attachedFore)
            {
                AttachThreadInput(current, foreThread, false);
            }
        }
    }

    private static void SendWinArrow(WindowArrangement arrangement)
    {
        var arrow = arrangement == WindowArrangement.LeftHalf ? VkLeft : VkRight;
        Span<Input> inputs = stackalloc Input[4];
        inputs[0] = Key(VkLwin, down: true);
        inputs[1] = Key(arrow, down: true);
        inputs[2] = Key(arrow, down: false);
        inputs[3] = Key(VkLwin, down: false);
        fixed (Input* ptr = inputs)
        {
            SendInput((uint)inputs.Length, ptr, sizeof(Input));
        }
    }

    private static void SendAltPulse()
    {
        Span<Input> inputs = stackalloc Input[2];
        inputs[0] = Key(VkMenu, down: true);
        inputs[1] = Key(VkMenu, down: false);
        fixed (Input* ptr = inputs)
        {
            SendInput((uint)inputs.Length, ptr, sizeof(Input));
        }
    }

    private static Input Key(ushort vk, bool down) => new()
    {
        Type = InputKeyboard,
        U = new InputUnion
        {
            Keyboard = new KeybdInput
            {
                Vk = vk,
                Flags = down ? 0u : KeyeventfKeyup
            }
        }
    };

    private enum SnapOutcome
    {
        None,
        Snapped,
        Failed
    }

    private readonly record struct ApplyOutcome(bool Placed, SnapOutcome Snap, string? SnapDetail)
    {
        public static ApplyOutcome Missed => new(false, SnapOutcome.None, null);
        public static ApplyOutcome PlacedOnly => new(true, SnapOutcome.None, null);
    }
}

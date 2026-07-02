using System.Runtime.InteropServices;

namespace DisplayLullaby;

internal sealed unsafe class TrayApplication
{
    private const uint TrayIconId = 1;
    private const int MenuBaseSleep = 1000;
    private const int MenuBaseWake = 2000;
    private const int MenuGlobalOff = 2500;
    private const int MenuTemporaryStandbyPrimary = 2600;
    private const int MenuTemporaryStandbySecondary = 2601;
    private const int MenuSettings = 2800;
    private const int MenuHelp = 2801;
    private const int MenuReload = 3000;
    private const int MenuExit = 3001;
    private const uint TemporaryStandbyTimerIntervalMs = 500;
    private const int TemporaryStandbyInputGraceMs = 1200;
    private static readonly nuint TemporaryStandbyTimerId = 1;

    private static readonly NativeMethods.WndProc WindowProc = WndProc;
    private static TrayApplication? _current;

    private readonly Dictionary<int, HotkeyBinding> _registeredHotkeys = new();
    private readonly Dictionary<string, bool> _targetSleepState = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _className = "DisplayLullabyTrayWindow";
    private HelpPopupWindow? _helpPopup;
    private SettingsWindow? _settingsWindow;
    private DateTime _lastHelpToggleUtc = DateTime.MinValue;
    private IntPtr _hwnd;
    private AppConfig _config = AppConfig.Parse(Array.Empty<string>());
    private TemporaryStandbyState? _temporaryStandby;
    private bool _temporaryStandbyTimerRunning;

    public static int Run()
    {
        if (!OperatingSystem.IsWindows())
        {
            return 1;
        }

        var app = new TrayApplication();
        _current = app;
        return app.RunMessageLoop();
    }

    private int RunMessageLoop()
    {
        _config = AppConfig.LoadOrCreate();
        CreateHiddenWindow();
        AddTrayIcon("DisplayLullaby");
        RegisterConfiguredHotkeys(showWarnings: true);

        while (NativeMethods.GetMessage(out var message, IntPtr.Zero, 0, 0))
        {
            NativeMethods.TranslateMessage(ref message);
            NativeMethods.DispatchMessage(ref message);
        }

        UnregisterConfiguredHotkeys();
        StopTemporaryStandbyTimer();
        _settingsWindow?.Close();
        _helpPopup?.Destroy();
        DeleteTrayIcon();
        _current = null;
        return 0;
    }

    private void CreateHiddenWindow()
    {
        var instance = NativeMethods.GetModuleHandle(null);
        var windowClass = new NativeMethods.WndClassEx
        {
            cbSize = (uint)Marshal.SizeOf<NativeMethods.WndClassEx>(),
            lpfnWndProc = WindowProc,
            hInstance = instance,
            hIcon = LoadApplicationIcon(instance),
            hIconSm = LoadApplicationIcon(instance),
            lpszClassName = _className
        };

        var classAtom = NativeMethods.RegisterClassEx(ref windowClass);
        if (classAtom == 0)
        {
            throw new InvalidOperationException($"Could not register tray window class: {NativeMethods.LastErrorMessage()}");
        }

        _hwnd = NativeMethods.CreateWindowEx(
            0,
            _className,
            "DisplayLullaby",
            0,
            0,
            0,
            0,
            0,
            IntPtr.Zero,
            IntPtr.Zero,
            instance,
            IntPtr.Zero);

        if (_hwnd == IntPtr.Zero)
        {
            throw new InvalidOperationException($"Could not create tray window: {NativeMethods.LastErrorMessage()}");
        }

        NativeMethods.ShowWindow(_hwnd, NativeMethods.SwHide);
    }

    private void AddTrayIcon(string tip)
    {
        var icon = LoadApplicationIcon(NativeMethods.GetModuleHandle(null));
        var data = CreateNotifyIconData(tip);
        data.uFlags = NativeMethods.NifMessage | NativeMethods.NifIcon | NativeMethods.NifTip;
        data.uCallbackMessage = NativeMethods.WmTray;
        data.hIcon = icon;

        NativeMethods.ShellNotifyIcon(NativeMethods.NimAdd, ref data);
        data.uVersion = NativeMethods.NotifyIconVersion4;
        NativeMethods.ShellNotifyIcon(NativeMethods.NimSetVersion, ref data);
    }

    private static IntPtr LoadApplicationIcon(IntPtr instance)
    {
        var icon = NativeMethods.LoadIcon(instance, new IntPtr(NativeMethods.IdIApplication));
        return icon != IntPtr.Zero
            ? icon
            : NativeMethods.LoadIcon(IntPtr.Zero, new IntPtr(NativeMethods.IdIApplication));
    }

    private void DeleteTrayIcon()
    {
        var data = CreateNotifyIconData("DisplayLullaby");
        NativeMethods.ShellNotifyIcon(NativeMethods.NimDelete, ref data);
    }

    private void ShowBalloon(string title, string text)
    {
        var data = CreateNotifyIconData("DisplayLullaby");
        data.uFlags = NativeMethods.NifInfo;
        NativeMethods.CopyFixedString(data.szInfoTitle, 64, title);
        NativeMethods.CopyFixedString(data.szInfo, 256, text);

        NativeMethods.ShellNotifyIcon(NativeMethods.NimModify, ref data);
    }

    private NativeMethods.NotifyIconData CreateNotifyIconData(string tip)
    {
        var data = new NativeMethods.NotifyIconData
        {
            cbSize = (uint)sizeof(NativeMethods.NotifyIconData),
            hWnd = _hwnd,
            uID = TrayIconId
        };

        NativeMethods.CopyFixedString(data.szTip, 128, tip);

        return data;
    }

    private void RegisterConfiguredHotkeys(bool showWarnings)
    {
        UnregisterConfiguredHotkeys();
        foreach (var hotkey in _config.Hotkeys)
        {
            if (NativeMethods.RegisterHotKey(_hwnd, hotkey.Id, hotkey.Modifiers, hotkey.VirtualKey))
            {
                _registeredHotkeys.Add(hotkey.Id, hotkey);
            }
            else if (showWarnings)
            {
                ShowBalloon("DisplayLullaby hotkey failed", $"{hotkey.DisplayText}: {NativeMethods.LastErrorMessage()}");
            }
        }

        if (showWarnings && _config.Warnings.Count > 0)
        {
            ShowBalloon("DisplayLullaby config", string.Join(Environment.NewLine, _config.Warnings.Take(3)));
        }
    }

    private void UnregisterConfiguredHotkeys()
    {
        foreach (var id in _registeredHotkeys.Keys)
        {
            NativeMethods.UnregisterHotKey(_hwnd, id);
        }

        _registeredHotkeys.Clear();
    }

    private void ReloadConfiguration(bool showBalloon = true)
    {
        _config = AppConfig.LoadOrCreate();
        RegisterConfiguredHotkeys(showWarnings: true);
        if (showBalloon)
        {
            ShowBalloon("DisplayLullaby", "Configuration reloaded.");
        }
    }

    private void Execute(TrayAction action, string target)
    {
        if (action == TrayAction.GlobalOff)
        {
            ClearTemporaryStandby();
            _helpPopup?.Hide();
            if (!MonitorController.TrySendGlobalPowerOff())
            {
                ShowBalloon("DisplayLullaby", $"Could not turn monitors off: {NativeMethods.LastErrorMessage()}");
            }

            return;
        }

        if (action == TrayAction.TemporaryStandby)
        {
            ExecuteTemporaryStandby(target);
            return;
        }

        var mode = action switch
        {
            TrayAction.Wake => MonitorPowerMode.On,
            TrayAction.Toggle => ChooseToggleMode(target),
            _ => _config.SleepMode
        };

        if (!MonitorController.TryApplyPowerMode(target, mode))
        {
            ShowBalloon("DisplayLullaby", $"Could not {action.ToString().ToLowerInvariant()} monitor '{target}'. Run DisplayLullaby list to inspect DDC/CI.");
            return;
        }

        _targetSleepState[target] = mode != MonitorPowerMode.On;
        if (mode == MonitorPowerMode.On && TemporaryStandbyMatches(target))
        {
            ClearTemporaryStandby();
        }
    }

    private void ExecuteTemporaryStandby(string target)
    {
        _helpPopup?.Hide();
        if (_temporaryStandby is { } standby && TemporaryStandbyMatches(target))
        {
            WakeTemporaryStandbyFromInput(standby);
            return;
        }

        if (_temporaryStandby is { } otherStandby)
        {
            WakeTemporaryStandbyFromInput(otherStandby);
        }

        if (MonitorController.ChooseToggleMode(target, MonitorPowerMode.Standby) == MonitorPowerMode.On)
        {
            ClearTemporaryStandby();
            MonitorController.TryApplyPowerMode(target, MonitorPowerMode.On);
            _targetSleepState[target] = false;
            return;
        }

        if (!MonitorController.TryApplyPowerMode(target, MonitorPowerMode.Standby))
        {
            ShowBalloon("DisplayLullaby", $"Could not temporarily standby monitor '{target}'. Run DisplayLullaby list to inspect DDC/CI.");
            return;
        }

        _targetSleepState[target] = true;
        _temporaryStandby = new TemporaryStandbyState(target, DateTime.UtcNow, TryGetLastInputTick(out var lastInputTick) ? lastInputTick : 0);
        StartTemporaryStandbyTimer();

        if (_config.TemporaryStandbyIdleMinutes > 0)
        {
            ShowBalloon(
                "DisplayLullaby",
                $"Temporary DDC standby sent. Handoff after {_config.TemporaryStandbyIdleMinutes} idle minutes.");
        }
        else
        {
            ShowBalloon("DisplayLullaby", "Temporary DDC standby sent. Automatic handoff is disabled.");
        }
    }

    private void ShowHelp()
    {
        var now = DateTime.UtcNow;
        if ((now - _lastHelpToggleUtc).TotalMilliseconds < 350)
        {
            return;
        }

        _lastHelpToggleUtc = now;
        _helpPopup ??= new HelpPopupWindow();

        if (_helpPopup.IsVisible)
        {
            _helpPopup.Hide();
            return;
        }

        if ((now - _helpPopup.LastHiddenUtc).TotalMilliseconds < 250)
        {
            return;
        }

        _helpPopup.Show(_hwnd, BuildHelpContent());
    }

    private void ShowSettings()
    {
        _helpPopup?.Hide();

        if (_settingsWindow is { IsOpen: true })
        {
            _settingsWindow.Show();
            return;
        }

        try
        {
            _settingsWindow = new SettingsWindow(
                _hwnd,
                _config.ToSettings(),
                () => ReloadConfiguration(showBalloon: false),
                Execute,
                UnregisterConfiguredHotkeys,
                () => RegisterConfiguredHotkeys(showWarnings: false),
                () => _settingsWindow = null);
            _settingsWindow.Show();
        }
        catch (Exception ex)
        {
            ShowBalloon("DisplayLullaby", $"Could not open settings: {ex.Message}");
        }
    }

    private HelpPopupContent BuildHelpContent()
    {
        var displays = new List<HelpDisplayRow>();

        try
        {
            using var session = MonitorController.OpenSession();
            foreach (var target in session.Targets)
            {
                var role = target.IsPrimary ? "primary" : "display";
                displays.Add(new HelpDisplayRow(TrimDeviceName(target.DeviceName), target.Description, role));
            }
        }
        catch
        {
            displays.Add(new HelpDisplayRow("-", "Could not read displays", ""));
        }

        var hotkeys = _config.Hotkeys.Select(FormatHotkeyForHelp).ToArray();
        return new HelpPopupContent(displays, hotkeys);
    }

    private static HelpHotkeyRow FormatHotkeyForHelp(HotkeyBinding hotkey)
    {
        var parts = hotkey.DisplayText.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var key = parts.Length > 0 ? parts[0] : hotkey.DisplayText;
        var target = parts.Length > 2 ? parts[2] : hotkey.Target;

        var description = hotkey.Action switch
        {
            TrayAction.Sleep => $"standby {target}",
            TrayAction.Wake => $"wake {target}",
            TrayAction.Toggle => $"standby / wake {target}",
            TrayAction.GlobalOff => "turn all monitors off",
            TrayAction.TemporaryStandby => $"standby {target} until input",
            _ => hotkey.DisplayText
        };

        return new HelpHotkeyRow(key, description);
    }

    private static string TrimDeviceName(string deviceName)
    {
        return deviceName.StartsWith(@"\\.\", StringComparison.Ordinal)
            ? deviceName[4..]
            : deviceName;
    }

    private MonitorPowerMode ChooseToggleMode(string target)
    {
        if (_targetSleepState.TryGetValue(target, out var appThinksTargetIsSleeping))
        {
            return appThinksTargetIsSleeping ? MonitorPowerMode.On : _config.SleepMode;
        }

        return MonitorController.ChooseToggleMode(target, _config.SleepMode);
    }

    private void ShowContextMenu()
    {
        _helpPopup?.Hide();
        var menu = NativeMethods.CreatePopupMenu();
        if (menu == IntPtr.Zero)
        {
            return;
        }

        try
        {
            NativeMethods.AppendMenu(menu, NativeMethods.MfString, MenuSettings, "Settings...");
            NativeMethods.AppendMenu(menu, NativeMethods.MfString, MenuHelp, "Help");
            NativeMethods.AppendMenu(menu, NativeMethods.MfSeparator, 0, null);
            NativeMethods.AppendMenu(menu, NativeMethods.MfString, MenuGlobalOff, "Turn all monitors off");
            NativeMethods.AppendMenu(menu, NativeMethods.MfSeparator, 0, null);
            NativeMethods.AppendMenu(menu, NativeMethods.MfString, MenuTemporaryStandbyPrimary, "Temporary standby / wake primary (F10)");
            NativeMethods.AppendMenu(menu, NativeMethods.MfString, MenuTemporaryStandbySecondary, "Temporary standby / wake secondary (F11)");
            NativeMethods.AppendMenu(menu, NativeMethods.MfSeparator, 0, null);
            NativeMethods.AppendMenu(menu, NativeMethods.MfString, MenuReload, "Reload hotkeys");
            NativeMethods.AppendMenu(menu, NativeMethods.MfString, MenuExit, "Exit");

            if (!NativeMethods.GetCursorPos(out var point))
            {
                point = new NativeMethods.Point();
            }

            NativeMethods.SetForegroundWindow(_hwnd);
            var command = NativeMethods.TrackPopupMenu(
                menu,
                NativeMethods.TpmRightButton | NativeMethods.TpmReturNCmd,
                point.X,
                point.Y,
                0,
                _hwnd,
                IntPtr.Zero);

            HandleMenuCommand(command);
        }
        finally
        {
            NativeMethods.DestroyMenu(menu);
        }
    }

    private void HandleMenuCommand(uint command)
    {
        if (command == 0)
        {
            return;
        }

        if (command == MenuExit)
        {
            NativeMethods.DestroyWindow(_hwnd);
            return;
        }

        if (command == MenuReload)
        {
            ReloadConfiguration();
            return;
        }

        if (command == MenuSettings)
        {
            ShowSettings();
            return;
        }

        if (command == MenuHelp)
        {
            ShowHelp();
            return;
        }

        if (command == MenuGlobalOff)
        {
            Execute(TrayAction.GlobalOff, "all");
            return;
        }

        if (command == MenuTemporaryStandbyPrimary)
        {
            Execute(TrayAction.TemporaryStandby, "primary");
            return;
        }

        if (command == MenuTemporaryStandbySecondary)
        {
            Execute(TrayAction.TemporaryStandby, "secondary");
            return;
        }

        if (command is >= MenuBaseSleep and < MenuBaseWake)
        {
            Execute(TrayAction.Sleep, (command - MenuBaseSleep).ToString());
            return;
        }

        if (command is >= MenuBaseWake and < MenuReload)
        {
            Execute(TrayAction.Wake, (command - MenuBaseWake).ToString());
        }
    }

    private void StartTemporaryStandbyTimer()
    {
        StopTemporaryStandbyTimer();
        if (NativeMethods.SetTimer(_hwnd, TemporaryStandbyTimerId, TemporaryStandbyTimerIntervalMs, IntPtr.Zero) == 0)
        {
            ShowBalloon("DisplayLullaby", $"Could not start temporary standby handoff timer: {NativeMethods.LastErrorMessage()}");
            return;
        }

        _temporaryStandbyTimerRunning = true;
    }

    private void StopTemporaryStandbyTimer()
    {
        if (!_temporaryStandbyTimerRunning)
        {
            return;
        }

        NativeMethods.KillTimer(_hwnd, TemporaryStandbyTimerId);
        _temporaryStandbyTimerRunning = false;
    }

    private void ClearTemporaryStandby()
    {
        _temporaryStandby = null;
        StopTemporaryStandbyTimer();
    }

    private void CheckTemporaryStandbyHandoff()
    {
        if (_temporaryStandby is not { } standby)
        {
            StopTemporaryStandbyTimer();
            return;
        }

        var elapsedSinceStart = DateTime.UtcNow - standby.StartedUtc;
        if (elapsedSinceStart.TotalMilliseconds < TemporaryStandbyInputGraceMs)
        {
            if (TryGetLastInputTick(out var graceLastInputTick))
            {
                _temporaryStandby = standby with { LastInputTick = graceLastInputTick };
            }

            return;
        }

        if (TryGetLastInputTick(out var lastInputTick) && lastInputTick != standby.LastInputTick)
        {
            WakeTemporaryStandbyFromInput(standby);
            return;
        }

        if (_config.TemporaryStandbyIdleMinutes <= 0 ||
            !TryGetIdleTime(out var idleTime) ||
            idleTime < TimeSpan.FromMinutes(_config.TemporaryStandbyIdleMinutes))
        {
            return;
        }

        CompleteTemporaryStandbyHandoff(standby);
    }

    private void WakeTemporaryStandbyFromInput(TemporaryStandbyState standby)
    {
        ClearTemporaryStandby();
        MonitorController.TryApplyPowerMode(standby.Target, MonitorPowerMode.On);
        _targetSleepState[standby.Target] = false;
    }

    private void CompleteTemporaryStandbyHandoff(TemporaryStandbyState standby)
    {
        ClearTemporaryStandby();
        MonitorController.TryApplyPowerMode(standby.Target, MonitorPowerMode.On);

        if (_config.TemporaryStandbyWakeDelaySeconds > 0)
        {
            Thread.Sleep(TimeSpan.FromSeconds(_config.TemporaryStandbyWakeDelaySeconds));
        }

        _helpPopup?.Hide();
        if (!MonitorController.TrySendGlobalPowerOff())
        {
            ShowBalloon("DisplayLullaby", $"Could not turn monitors off after temporary standby: {NativeMethods.LastErrorMessage()}");
        }
    }

    private bool TemporaryStandbyMatches(string target)
    {
        if (_temporaryStandby is not { } standby)
        {
            return false;
        }

        return target.Equals("all", StringComparison.OrdinalIgnoreCase) ||
               standby.Target.Equals(target, StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryGetIdleTime(out TimeSpan idleTime)
    {
        if (!TryGetLastInputTick(out var lastInputTick))
        {
            idleTime = TimeSpan.Zero;
            return false;
        }

        var milliseconds = unchecked((uint)Environment.TickCount - lastInputTick);
        idleTime = TimeSpan.FromMilliseconds(milliseconds);
        return true;
    }

    private static bool TryGetLastInputTick(out uint lastInputTick)
    {
        var info = new NativeMethods.LastInputInfo
        {
            cbSize = (uint)Marshal.SizeOf<NativeMethods.LastInputInfo>()
        };

        if (!NativeMethods.GetLastInputInfo(ref info))
        {
            lastInputTick = 0;
            return false;
        }

        lastInputTick = info.dwTime;
        return true;
    }

    private nint HandleWindowMessage(IntPtr hwnd, uint msg, nuint wParam, nint lParam)
    {
        switch (msg)
        {
            case NativeMethods.WmTray:
                var trayEvent = GetTrayEvent(lParam);
                if (trayEvent == NativeMethods.WmLButtonUp ||
                    trayEvent == NativeMethods.WmLButtonDblClk ||
                    trayEvent == NativeMethods.NinSelect ||
                    trayEvent == NativeMethods.NinKeySelect)
                {
                    ShowSettings();
                }
                else if (trayEvent == NativeMethods.WmRButtonUp ||
                         trayEvent == NativeMethods.WmContextMenu)
                {
                    ShowContextMenu();
                }

                return 0;

            case NativeMethods.WmTimer:
                if (wParam == TemporaryStandbyTimerId)
                {
                    CheckTemporaryStandbyHandoff();
                    return 0;
                }

                return 0;

            case NativeMethods.WmHotkey:
                if (_registeredHotkeys.TryGetValue((int)wParam, out var hotkey))
                {
                    Execute(hotkey.Action, hotkey.Target);
                }

                return 0;

            case NativeMethods.WmDisplayChange:
                RegisterConfiguredHotkeys(showWarnings: false);
                return 0;

            case NativeMethods.WmDestroy:
                StopTemporaryStandbyTimer();
                NativeMethods.PostQuitMessage(0);
                return 0;

            default:
                return NativeMethods.DefWindowProc(hwnd, msg, wParam, lParam);
        }
    }

    private static uint GetTrayEvent(nint lParam)
    {
        var value = unchecked((uint)(long)lParam);
        return value <= ushort.MaxValue ? value : value & 0xFFFF;
    }

    private static nint WndProc(IntPtr hwnd, uint msg, nuint wParam, nint lParam)
    {
        return _current?.HandleWindowMessage(hwnd, msg, wParam, lParam)
               ?? NativeMethods.DefWindowProc(hwnd, msg, wParam, lParam);
    }

    private sealed record TemporaryStandbyState(string Target, DateTime StartedUtc, uint LastInputTick);
}

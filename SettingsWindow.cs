using System.Runtime.InteropServices;
using System.Text;

namespace DisplayLullaby;

internal sealed class SettingsWindow
{
    private const string ClassName = "DisplayLullabySettingsWindow";
    private const int IdCaptureGlobal = 1001;
    private const int IdCapturePrimary = 1002;
    private const int IdCaptureSecondary = 1003;
    private const int IdTestPrimary = 1101;
    private const int IdTestSecondary = 1102;
    private const int IdTurnAllOff = 1103;
    private const int IdSave = 1201;
    private const int IdCancel = 1202;

    private static readonly NativeMethods.WndProc WindowProc = WndProc;
    private static SettingsWindow? _current;
    private static bool _classRegistered;

    private readonly IntPtr _owner;
    private readonly Action _onSaved;
    private readonly Action<TrayAction, string> _executeCommand;
    private readonly Action _onCaptureStarted;
    private readonly Action _onCaptureEnded;
    private readonly Action _onClosed;
    private readonly uint _dpi;

    private AppSettings _settings;
    private IntPtr _hwnd;
    private IntPtr _font;
    private IntPtr _titleFont;
    private IntPtr _sectionFont;
    private IntPtr _smallFont;
    private IntPtr _globalHotkeyButton;
    private IntPtr _primaryHotkeyButton;
    private IntPtr _secondaryHotkeyButton;
    private IntPtr _primaryTargetCombo;
    private IntPtr _secondaryTargetCombo;
    private IntPtr _powerModeCombo;
    private IntPtr _idleMinutesEdit;
    private IntPtr _wakeDelayEdit;
    private IntPtr _statusLabel;
    private CaptureTarget _captureTarget;

    public SettingsWindow(
        IntPtr owner,
        AppSettings settings,
        Action onSaved,
        Action<TrayAction, string> executeCommand,
        Action onCaptureStarted,
        Action onCaptureEnded,
        Action onClosed)
    {
        _owner = owner;
        _settings = settings;
        _onSaved = onSaved;
        _executeCommand = executeCommand;
        _onCaptureStarted = onCaptureStarted;
        _onCaptureEnded = onCaptureEnded;
        _onClosed = onClosed;
        _dpi = NativeMethods.GetDpiForSystem();
        if (_dpi == 0)
        {
            _dpi = 96;
        }
    }

    public bool IsOpen => _hwnd != IntPtr.Zero && NativeMethods.IsWindow(_hwnd);

    public void Close()
    {
        if (IsOpen)
        {
            NativeMethods.DestroyWindow(_hwnd);
        }
    }

    public void Show()
    {
        if (IsOpen)
        {
            NativeMethods.ShowWindow(_hwnd, NativeMethods.SwRestore);
            NativeMethods.SetForegroundWindow(_hwnd);
            return;
        }

        RegisterWindowClass();
        _current = this;
        CreateFonts();

        var (x, y, width, height) = CalculateWindowBounds();
        _hwnd = NativeMethods.CreateWindowEx(
            0,
            ClassName,
            "DisplayLullaby Settings",
            NativeMethods.WsOverlappedWindow,
            x,
            y,
            width,
            height,
            _owner,
            IntPtr.Zero,
            NativeMethods.GetModuleHandle(null),
            IntPtr.Zero);

        if (_hwnd == IntPtr.Zero)
        {
            _current = null;
            throw new InvalidOperationException($"Could not create settings window: {NativeMethods.LastErrorMessage()}");
        }

        CreateControls();
        NativeMethods.ShowWindow(_hwnd, NativeMethods.SwShow);
        NativeMethods.SetForegroundWindow(_hwnd);
    }

    private void RegisterWindowClass()
    {
        if (_classRegistered)
        {
            return;
        }

        var instance = NativeMethods.GetModuleHandle(null);
        var windowClass = new NativeMethods.WndClassEx
        {
            cbSize = (uint)Marshal.SizeOf<NativeMethods.WndClassEx>(),
            lpfnWndProc = WindowProc,
            hInstance = instance,
            hIcon = NativeMethods.LoadIcon(instance, new IntPtr(NativeMethods.IdIApplication)),
            hIconSm = NativeMethods.LoadIcon(instance, new IntPtr(NativeMethods.IdIApplication)),
            hCursor = NativeMethods.LoadCursor(IntPtr.Zero, new IntPtr(NativeMethods.IdcArrow)),
            hbrBackground = new IntPtr(NativeMethods.ColorWindow + 1),
            lpszClassName = ClassName
        };

        var classAtom = NativeMethods.RegisterClassEx(ref windowClass);
        if (classAtom == 0 && Marshal.GetLastWin32Error() != 1410)
        {
            throw new InvalidOperationException($"Could not register settings window class: {NativeMethods.LastErrorMessage()}");
        }

        _classRegistered = true;
    }

    private void CreateFonts()
    {
        _font = NativeMethods.CreateFont(
            -Scale(12),
            0,
            0,
            0,
            NativeMethods.FwNormal,
            0,
            0,
            0,
            NativeMethods.DefaultCharset,
            NativeMethods.OutDefaultPrecIs,
            NativeMethods.ClipDefaultPrecIs,
            NativeMethods.ClearTypeQuality,
            NativeMethods.DefaultPitch,
            "Segoe UI");

        _titleFont = NativeMethods.CreateFont(
            -Scale(22),
            0,
            0,
            0,
            NativeMethods.FwSemiBold,
            0,
            0,
            0,
            NativeMethods.DefaultCharset,
            NativeMethods.OutDefaultPrecIs,
            NativeMethods.ClipDefaultPrecIs,
            NativeMethods.ClearTypeQuality,
            NativeMethods.DefaultPitch,
            "Segoe UI");

        _sectionFont = NativeMethods.CreateFont(
            -Scale(12),
            0,
            0,
            0,
            NativeMethods.FwSemiBold,
            0,
            0,
            0,
            NativeMethods.DefaultCharset,
            NativeMethods.OutDefaultPrecIs,
            NativeMethods.ClipDefaultPrecIs,
            NativeMethods.ClearTypeQuality,
            NativeMethods.DefaultPitch,
            "Segoe UI");

        _smallFont = NativeMethods.CreateFont(
            -Scale(10),
            0,
            0,
            0,
            NativeMethods.FwNormal,
            0,
            0,
            0,
            NativeMethods.DefaultCharset,
            NativeMethods.OutDefaultPrecIs,
            NativeMethods.ClipDefaultPrecIs,
            NativeMethods.ClearTypeQuality,
            NativeMethods.DefaultPitch,
            "Segoe UI");
    }

    private (int X, int Y, int Width, int Height) CalculateWindowBounds()
    {
        var width = Scale(680);
        var height = Scale(585);
        var point = new NativeMethods.Point();
        NativeMethods.GetCursorPos(out point);

        var monitor = NativeMethods.MonitorFromPoint(point, NativeMethods.MonitorDefaultToNearest);
        var info = new NativeMethods.MonitorInfoEx
        {
            cbSize = (uint)Marshal.SizeOf<NativeMethods.MonitorInfoEx>()
        };

        if (monitor != IntPtr.Zero && NativeMethods.GetMonitorInfo(monitor, ref info))
        {
            var x = info.rcWork.Left + Math.Max(0, (info.rcWork.Width - width) / 2);
            var y = info.rcWork.Top + Math.Max(0, (info.rcWork.Height - height) / 2);
            return (x, y, width, height);
        }

        return (100, 100, width, height);
    }

    private void CreateControls()
    {
        CreateLabel("Command", 46, 122, 160, 20, _smallFont);
        CreateLabel("Hotkey", 268, 122, 90, 20, _smallFont);
        CreateLabel("Target", 402, 122, 120, 20, _smallFont);

        CreateLabel("All monitors off", 46, 150, 180, 26);
        _globalHotkeyButton = CreateButton(_settings.GlobalOffHotkey, IdCaptureGlobal, 268, 146, 98, 32);

        CreateLabel("Primary standby", 46, 192, 180, 26);
        _primaryHotkeyButton = CreateButton(_settings.PrimaryStandbyHotkey, IdCapturePrimary, 268, 188, 98, 32);
        _primaryTargetCombo = CreateTargetCombo(_settings.PrimaryStandbyTarget, 402, 188, 224, 160);

        CreateLabel("Secondary standby", 46, 234, 180, 26);
        _secondaryHotkeyButton = CreateButton(_settings.SecondaryStandbyHotkey, IdCaptureSecondary, 268, 230, 98, 32);
        _secondaryTargetCombo = CreateTargetCombo(_settings.SecondaryStandbyTarget, 402, 230, 224, 160);

        CreateLabel("DDC mode", 46, 318, 110, 26);
        _powerModeCombo = CreatePowerModeCombo(_settings.PowerMode, 160, 314, 150, 140);

        CreateLabel("Idle", 336, 318, 54, 26);
        _idleMinutesEdit = CreateEdit(_settings.TemporaryStandbyIdleMinutes.ToString(), 394, 314, 62, 30, numbersOnly: true);
        CreateLabel("min", 464, 318, 40, 22, _smallFont);

        CreateLabel("Wake", 508, 318, 56, 26);
        _wakeDelayEdit = CreateEdit(_settings.TemporaryStandbyWakeDelaySeconds.ToString(), 566, 314, 52, 30, numbersOnly: true);
        CreateLabel("sec", 624, 318, 36, 22, _smallFont);

        CreateLabel(BuildDisplaySummary(), 46, 398, 590, 42, _smallFont);

        _statusLabel = CreateLabel("Ready.", 24, 470, 360, 24, _smallFont);

        CreateButton("Test primary", IdTestPrimary, 24, 506, 112, 34);
        CreateButton("Test secondary", IdTestSecondary, 146, 506, 122, 34);
        CreateButton("All off", IdTurnAllOff, 278, 506, 78, 34);
        CreateButton("Save", IdSave, 496, 506, 72, 34, defaultButton: true);
        CreateButton("Cancel", IdCancel, 578, 506, 78, 34);
    }

    private void PaintWindow()
    {
        var hdc = NativeMethods.BeginPaint(_hwnd, out var paint);
        try
        {
            DrawSettingsSurface(hdc);
        }
        finally
        {
            NativeMethods.EndPaint(_hwnd, ref paint);
        }
    }

    private void DrawSettingsSurface(IntPtr hdc)
    {
        NativeMethods.GetClientRect(_hwnd, out var clientRect);
        FillRect(hdc, clientRect, ColorRef(246, 248, 252));

        var headerRect = ScaleRect(0, 0, 680, 86);
        FillRect(hdc, headerRect, ColorRef(238, 245, 253));

        DrawRoundRect(hdc, 24, 18, 50, 50, 14, ColorRef(37, 99, 235), ColorRef(30, 64, 175));
        DrawMonitorGlyph(hdc, 39, 33);

        DrawTextLine(hdc, "DisplayLullaby settings", 92, 19, 430, 30, _titleFont, ColorRef(15, 23, 42));
        DrawTextLine(hdc, "Hotkeys, standby handoff, and display targets", 94, 51, 430, 22, _font, ColorRef(71, 94, 131));

        DrawCard(hdc, 22, 100, 636, 178, "Hotkeys");
        DrawCard(hdc, 22, 294, 636, 72, "Standby behavior");
        DrawCard(hdc, 22, 382, 636, 76, "Detected displays");

        var footerLine = ScaleRect(22, 492, 636, 1);
        FillRect(hdc, footerLine, ColorRef(225, 232, 242));
    }

    private void DrawCard(IntPtr hdc, int x, int y, int width, int height, string title)
    {
        DrawRoundRect(hdc, x, y, width, height, 16, ColorRef(255, 255, 255), ColorRef(215, 226, 240));
        DrawTextLine(hdc, title, x + 20, y + 12, width - 40, 22, _sectionFont, ColorRef(32, 55, 83));
    }

    private void DrawMonitorGlyph(IntPtr hdc, int x, int y)
    {
        var pen = NativeMethods.CreatePen(NativeMethods.PsSolid, Scale(2), ColorRef(255, 255, 255));
        var brush = NativeMethods.CreateSolidBrush(ColorRef(219, 234, 254));
        var oldPen = NativeMethods.SelectObject(hdc, pen);
        var oldBrush = NativeMethods.SelectObject(hdc, brush);

        NativeMethods.RoundRect(hdc, Scale(x), Scale(y), Scale(x + 21), Scale(y + 16), Scale(4), Scale(4));
        var standRect = ScaleRect(x + 8, y + 18, 5, 6);
        FillRect(hdc, standRect, ColorRef(255, 255, 255));
        var footRect = ScaleRect(x + 4, y + 25, 14, 3);
        FillRect(hdc, footRect, ColorRef(255, 255, 255));

        NativeMethods.SelectObject(hdc, oldBrush);
        NativeMethods.SelectObject(hdc, oldPen);
        NativeMethods.DeleteObject(brush);
        NativeMethods.DeleteObject(pen);
    }

    private void DrawTextLine(IntPtr hdc, string text, int x, int y, int width, int height, IntPtr font, uint color)
    {
        var rect = ScaleRect(x, y, width, height);
        var oldFont = font == IntPtr.Zero ? IntPtr.Zero : NativeMethods.SelectObject(hdc, font);
        NativeMethods.SetBkMode(hdc, NativeMethods.Transparent);
        NativeMethods.SetTextColor(hdc, color);
        NativeMethods.DrawText(
            hdc,
            text,
            text.Length,
            ref rect,
            NativeMethods.DtLeft | NativeMethods.DtVCenter | NativeMethods.DtSingleLine | NativeMethods.DtNoPrefix | NativeMethods.DtEndEllipsis);

        if (oldFont != IntPtr.Zero)
        {
            NativeMethods.SelectObject(hdc, oldFont);
        }
    }

    private void DrawRoundRect(IntPtr hdc, int x, int y, int width, int height, int radius, uint fillColor, uint borderColor)
    {
        var pen = NativeMethods.CreatePen(NativeMethods.PsSolid, Scale(1), borderColor);
        var brush = NativeMethods.CreateSolidBrush(fillColor);
        var oldPen = NativeMethods.SelectObject(hdc, pen);
        var oldBrush = NativeMethods.SelectObject(hdc, brush);

        NativeMethods.RoundRect(
            hdc,
            Scale(x),
            Scale(y),
            Scale(x + width),
            Scale(y + height),
            Scale(radius),
            Scale(radius));

        NativeMethods.SelectObject(hdc, oldBrush);
        NativeMethods.SelectObject(hdc, oldPen);
        NativeMethods.DeleteObject(brush);
        NativeMethods.DeleteObject(pen);
    }

    private void DrawOwnerButton(nint lParam)
    {
        var item = Marshal.PtrToStructure<NativeMethods.DrawItemStruct>((IntPtr)lParam);
        var id = (int)item.CtlID;
        var text = ReadText(item.hwndItem);
        var isPressed = (item.itemState & NativeMethods.OdsSelected) != 0;
        var isPrimary = id == IdSave;
        var isHotkey = id is IdCaptureGlobal or IdCapturePrimary or IdCaptureSecondary;
        var isDanger = id == IdTurnAllOff;

        var fill = isPrimary ? ColorRef(37, 99, 235)
            : isDanger ? ColorRef(255, 251, 235)
            : isHotkey ? ColorRef(239, 246, 255)
            : ColorRef(255, 255, 255);
        var border = isPrimary ? ColorRef(29, 78, 216)
            : isDanger ? ColorRef(245, 158, 11)
            : isHotkey ? ColorRef(147, 197, 253)
            : ColorRef(203, 213, 225);
        var textColor = isPrimary ? ColorRef(255, 255, 255)
            : isDanger ? ColorRef(120, 53, 15)
            : isHotkey ? ColorRef(29, 78, 216)
            : ColorRef(15, 23, 42);

        if (isPressed)
        {
            fill = isPrimary ? ColorRef(29, 78, 216)
                : isDanger ? ColorRef(254, 243, 199)
                : isHotkey ? ColorRef(219, 234, 254)
                : ColorRef(241, 245, 249);
        }

        DrawRoundRectRaw(item.hDC, item.rcItem, Scale(10), fill, border);
        var textRect = item.rcItem;
        if (isPressed)
        {
            textRect.Left += Scale(1);
            textRect.Top += Scale(1);
        }

        DrawCenteredText(item.hDC, text, textRect, _font, textColor);
    }

    private void DrawRoundRectRaw(IntPtr hdc, NativeMethods.Rect rect, int radius, uint fillColor, uint borderColor)
    {
        var pen = NativeMethods.CreatePen(NativeMethods.PsSolid, Scale(1), borderColor);
        var brush = NativeMethods.CreateSolidBrush(fillColor);
        var oldPen = NativeMethods.SelectObject(hdc, pen);
        var oldBrush = NativeMethods.SelectObject(hdc, brush);

        NativeMethods.RoundRect(hdc, rect.Left, rect.Top, rect.Right, rect.Bottom, radius, radius);

        NativeMethods.SelectObject(hdc, oldBrush);
        NativeMethods.SelectObject(hdc, oldPen);
        NativeMethods.DeleteObject(brush);
        NativeMethods.DeleteObject(pen);
    }

    private void DrawCenteredText(IntPtr hdc, string text, NativeMethods.Rect rect, IntPtr font, uint color)
    {
        var oldFont = font == IntPtr.Zero ? IntPtr.Zero : NativeMethods.SelectObject(hdc, font);
        NativeMethods.SetBkMode(hdc, NativeMethods.Transparent);
        NativeMethods.SetTextColor(hdc, color);
        NativeMethods.DrawText(
            hdc,
            text,
            text.Length,
            ref rect,
            NativeMethods.DtCenter | NativeMethods.DtVCenter | NativeMethods.DtSingleLine | NativeMethods.DtNoPrefix | NativeMethods.DtEndEllipsis);

        if (oldFont != IntPtr.Zero)
        {
            NativeMethods.SelectObject(hdc, oldFont);
        }
    }

    private void FillRect(IntPtr hdc, NativeMethods.Rect rect, uint color)
    {
        var brush = NativeMethods.CreateSolidBrush(color);
        NativeMethods.FillRect(hdc, ref rect, brush);
        NativeMethods.DeleteObject(brush);
    }

    private NativeMethods.Rect ScaleRect(int x, int y, int width, int height) =>
        new()
        {
            Left = Scale(x),
            Top = Scale(y),
            Right = Scale(x + width),
            Bottom = Scale(y + height)
        };

    private static uint ColorRef(byte red, byte green, byte blue) =>
        (uint)(red | (green << 8) | (blue << 16));

    private IntPtr CreateLabel(string text, int x, int y, int width, int height, IntPtr font = default)
    {
        var handle = CreateControl(
            "STATIC",
            text,
            NativeMethods.WsChild | NativeMethods.WsVisible | NativeMethods.SsLeft,
            0,
            0,
            x,
            y,
            width,
            height);
        SetControlFont(handle, font == default ? _font : font);
        return handle;
    }

    private IntPtr CreateButton(string text, int id, int x, int y, int width, int height, bool defaultButton = false)
    {
        var style = NativeMethods.WsChild |
                    NativeMethods.WsVisible |
                    NativeMethods.WsTabStop |
                    NativeMethods.BsOwnerDraw |
                    (defaultButton ? NativeMethods.BsDefPushButton : NativeMethods.BsPushButton);
        var handle = CreateControl("BUTTON", text, style, 0, id, x, y, width, height);
        SetControlFont(handle, _font);
        return handle;
    }

    private IntPtr CreateEdit(string text, int x, int y, int width, int height, bool numbersOnly)
    {
        var style = NativeMethods.WsChild |
                    NativeMethods.WsVisible |
                    NativeMethods.WsTabStop |
                    NativeMethods.WsBorder |
                    NativeMethods.EsAutoHScroll |
                    (numbersOnly ? NativeMethods.EsNumber : 0);
        var handle = CreateControl("EDIT", text, style, NativeMethods.WsExClientEdge, 0, x, y, width, height);
        SetControlFont(handle, _font);
        return handle;
    }

    private IntPtr CreateTargetCombo(string selectedTarget, int x, int y, int width, int height)
    {
        var style = NativeMethods.WsChild |
                    NativeMethods.WsVisible |
                    NativeMethods.WsTabStop |
                    NativeMethods.CbsDropdown |
                    NativeMethods.CbsHasStrings |
                    NativeMethods.WsVScroll;
        var handle = CreateControl("COMBOBOX", string.Empty, style, 0, 0, x, y, width, height);
        SetControlFont(handle, _font);

        foreach (var option in BuildTargetOptions())
        {
            NativeMethods.SendMessageText(handle, NativeMethods.CbAddString, 0, option);
        }

        NativeMethods.SetWindowText(handle, selectedTarget);
        return handle;
    }

    private IntPtr CreatePowerModeCombo(MonitorPowerMode selectedMode, int x, int y, int width, int height)
    {
        var style = NativeMethods.WsChild |
                    NativeMethods.WsVisible |
                    NativeMethods.WsTabStop |
                    NativeMethods.CbsDropdownList |
                    NativeMethods.CbsHasStrings;
        var handle = CreateControl("COMBOBOX", string.Empty, style, 0, 0, x, y, width, height);
        SetControlFont(handle, _font);

        var modes = new[] { "Standby", "Suspend", "PowerOff", "SoftOff" };
        for (var i = 0; i < modes.Length; i++)
        {
            NativeMethods.SendMessageText(handle, NativeMethods.CbAddString, 0, modes[i]);
            if (modes[i].Equals(SerializePowerMode(selectedMode), StringComparison.OrdinalIgnoreCase))
            {
                NativeMethods.SendMessage(handle, NativeMethods.CbSetCurSel, (nuint)i, 0);
            }
        }

        return handle;
    }

    private IntPtr CreateControl(
        string className,
        string text,
        uint style,
        uint exStyle,
        int id,
        int x,
        int y,
        int width,
        int height)
    {
        var handle = NativeMethods.CreateWindowEx(
            exStyle,
            className,
            text,
            style,
            Scale(x),
            Scale(y),
            Scale(width),
            Scale(height),
            _hwnd,
            new IntPtr(id),
            NativeMethods.GetModuleHandle(null),
            IntPtr.Zero);

        if (handle == IntPtr.Zero)
        {
            throw new InvalidOperationException($"Could not create {className} control: {NativeMethods.LastErrorMessage()}");
        }

        return handle;
    }

    private void SetControlFont(IntPtr handle, IntPtr font)
    {
        if (font != IntPtr.Zero)
        {
            NativeMethods.SendMessage(handle, NativeMethods.WmSetFont, ToNuint(font), 1);
        }
    }

    private void StartCapture(CaptureTarget target)
    {
        EndCapture(resumeHotkeys: true);
        _captureTarget = target;
        _onCaptureStarted();

        var button = GetCaptureButton(target);
        NativeMethods.SetWindowText(button, "Press a key...");
        SetStatus(target switch
        {
            CaptureTarget.Global => "Press a key for all monitors off.",
            CaptureTarget.Primary => "Press a key for primary temporary standby.",
            CaptureTarget.Secondary => "Press a key for secondary temporary standby.",
            _ => "Press a key."
        });

        NativeMethods.SetFocus(_hwnd);
    }

    private void EndCapture(bool resumeHotkeys)
    {
        if (_captureTarget == CaptureTarget.None)
        {
            return;
        }

        NativeMethods.SetWindowText(_globalHotkeyButton, _settings.GlobalOffHotkey);
        NativeMethods.SetWindowText(_primaryHotkeyButton, _settings.PrimaryStandbyHotkey);
        NativeMethods.SetWindowText(_secondaryHotkeyButton, _settings.SecondaryStandbyHotkey);
        _captureTarget = CaptureTarget.None;

        if (resumeHotkeys)
        {
            _onCaptureEnded();
        }
    }

    private void CaptureKey(int virtualKey)
    {
        if (virtualKey == NativeMethods.VkEscape)
        {
            SetStatus("Hotkey capture canceled.");
            EndCapture(resumeHotkeys: true);
            return;
        }

        if (IsModifierKey(virtualKey))
        {
            return;
        }

        var keyText = FormatVirtualKey(virtualKey);
        if (keyText.Length == 0)
        {
            SetStatus("That key cannot be used as a hotkey.");
            return;
        }

        var chord = BuildChord(keyText);
        if (!AppConfig.TryValidateHotkeyText(chord, out var warning))
        {
            SetStatus(warning);
            return;
        }

        switch (_captureTarget)
        {
            case CaptureTarget.Global:
                _settings = _settings with { GlobalOffHotkey = chord };
                break;
            case CaptureTarget.Primary:
                _settings = _settings with { PrimaryStandbyHotkey = chord };
                break;
            case CaptureTarget.Secondary:
                _settings = _settings with { SecondaryStandbyHotkey = chord };
                break;
        }

        SetStatus($"Captured {chord}.");
        EndCapture(resumeHotkeys: true);
    }

    private bool TrySave()
    {
        EndCapture(resumeHotkeys: true);

        if (!TryReadSettings(out var settings, out var warning))
        {
            SetStatus(warning);
            return false;
        }

        if (!AppConfig.TryValidateSettings(settings, out warning))
        {
            SetStatus(warning);
            return false;
        }

        AppConfig.SaveSettings(settings);
        _settings = settings;
        _onSaved();
        SetStatus("Saved. Hotkeys applied.");
        return true;
    }

    private bool TryReadSettings(out AppSettings settings, out string warning)
    {
        settings = _settings;
        warning = string.Empty;

        if (!int.TryParse(ReadText(_idleMinutesEdit).Trim(), out var idleMinutes))
        {
            warning = "Idle handoff minutes must be a number.";
            return false;
        }

        if (!int.TryParse(ReadText(_wakeDelayEdit).Trim(), out var wakeDelaySeconds))
        {
            warning = "Wake delay seconds must be a number.";
            return false;
        }

        settings = _settings with
        {
            PrimaryStandbyTarget = ReadText(_primaryTargetCombo).Trim(),
            SecondaryStandbyTarget = ReadText(_secondaryTargetCombo).Trim(),
            PowerMode = ParsePowerMode(ReadText(_powerModeCombo)),
            TemporaryStandbyIdleMinutes = idleMinutes,
            TemporaryStandbyWakeDelaySeconds = wakeDelaySeconds
        };

        return true;
    }

    private void TestTemporaryStandby(bool primary)
    {
        EndCapture(resumeHotkeys: true);
        var target = ReadText(primary ? _primaryTargetCombo : _secondaryTargetCombo).Trim();
        if (target.Length == 0)
        {
            target = primary ? "primary" : "secondary";
        }

        _executeCommand(TrayAction.TemporaryStandby, target);
        SetStatus(primary ? "Primary standby command sent." : "Secondary standby command sent.");
    }

    private void TurnAllMonitorsOff()
    {
        EndCapture(resumeHotkeys: true);
        _executeCommand(TrayAction.GlobalOff, "all");
        SetStatus("Global monitor-off command sent.");
    }

    private string[] BuildTargetOptions()
    {
        var options = new List<string> { "primary", "secondary" };

        try
        {
            using var session = MonitorController.OpenSession();
            foreach (var target in session.Targets)
            {
                var deviceName = TrimDeviceName(target.DeviceName);
                if (!string.IsNullOrWhiteSpace(deviceName) &&
                    !options.Contains(deviceName, StringComparer.OrdinalIgnoreCase))
                {
                    options.Add(deviceName);
                }
            }
        }
        catch
        {
            // The target text remains editable even when display enumeration fails.
        }

        return options.ToArray();
    }

    private string BuildDisplaySummary()
    {
        try
        {
            using var session = MonitorController.OpenSession();
            if (session.Targets.Count == 0)
            {
                return "No DDC/CI displays found.";
            }

            return string.Join(
                Environment.NewLine,
                session.Targets.Select(static target =>
                    $"{TrimDeviceName(target.DeviceName)}  {(target.IsPrimary ? "primary" : "display")}  {target.Description}"));
        }
        catch
        {
            return "Could not read displays.";
        }
    }

    private IntPtr GetCaptureButton(CaptureTarget target) =>
        target switch
        {
            CaptureTarget.Global => _globalHotkeyButton,
            CaptureTarget.Primary => _primaryHotkeyButton,
            CaptureTarget.Secondary => _secondaryHotkeyButton,
            _ => IntPtr.Zero
        };

    private void SetStatus(string text)
    {
        if (_statusLabel != IntPtr.Zero)
        {
            NativeMethods.SetWindowText(_statusLabel, text);
            NativeMethods.InvalidateRect(_hwnd, IntPtr.Zero, false);
        }
    }

    private string ReadText(IntPtr handle)
    {
        var length = Math.Max(0, NativeMethods.GetWindowTextLength(handle));
        var builder = new StringBuilder(length + 1);
        NativeMethods.GetWindowText(handle, builder, builder.Capacity);
        return builder.ToString();
    }

    private static string BuildChord(string keyText)
    {
        var parts = new List<string>();
        if (IsKeyDown(NativeMethods.VkControl))
        {
            parts.Add("Ctrl");
        }

        if (IsKeyDown(NativeMethods.VkMenu))
        {
            parts.Add("Alt");
        }

        if (IsKeyDown(NativeMethods.VkShift))
        {
            parts.Add("Shift");
        }

        if (IsKeyDown(NativeMethods.VkLWin) || IsKeyDown(NativeMethods.VkRWin))
        {
            parts.Add("Win");
        }

        parts.Add(keyText);
        return string.Join("+", parts);
    }

    private static bool IsKeyDown(int virtualKey) =>
        (NativeMethods.GetKeyState(virtualKey) & unchecked((short)0x8000)) != 0;

    private static bool IsModifierKey(int virtualKey) =>
        virtualKey is NativeMethods.VkShift or NativeMethods.VkControl or NativeMethods.VkMenu or NativeMethods.VkLWin or NativeMethods.VkRWin;

    private static string FormatVirtualKey(int virtualKey)
    {
        if (virtualKey is >= 'A' and <= 'Z' or >= '0' and <= '9')
        {
            return ((char)virtualKey).ToString();
        }

        if (virtualKey is >= NativeMethods.VkF1 and <= NativeMethods.VkF24)
        {
            return $"F{virtualKey - NativeMethods.VkF1 + 1}";
        }

        if (virtualKey is >= NativeMethods.VkNumpad0 and <= NativeMethods.VkNumpad0 + 9)
        {
            return $"NumPad{virtualKey - NativeMethods.VkNumpad0}";
        }

        return virtualKey switch
        {
            NativeMethods.VkBackspace => "Backspace",
            NativeMethods.VkTab => "Tab",
            NativeMethods.VkReturn => "Enter",
            NativeMethods.VkSpace => "Space",
            NativeMethods.VkLeft => "Left",
            NativeMethods.VkUp => "Up",
            NativeMethods.VkRight => "Right",
            NativeMethods.VkDown => "Down",
            0x13 => "Pause",
            0x21 => "PageUp",
            0x22 => "PageDown",
            0x23 => "End",
            0x24 => "Home",
            0x2D => "Insert",
            0x2E => "Delete",
            0x91 => "ScrollLock",
            _ => string.Empty
        };
    }

    private static MonitorPowerMode ParsePowerMode(string text) =>
        text.Trim() switch
        {
            "Suspend" => MonitorPowerMode.Suspend,
            "PowerOff" => MonitorPowerMode.PowerOff,
            "SoftOff" => MonitorPowerMode.SoftOff,
            _ => MonitorPowerMode.Standby
        };

    private static string SerializePowerMode(MonitorPowerMode mode) =>
        mode switch
        {
            MonitorPowerMode.Suspend => "Suspend",
            MonitorPowerMode.PowerOff => "PowerOff",
            MonitorPowerMode.SoftOff => "SoftOff",
            _ => "Standby"
        };

    private static string TrimDeviceName(string deviceName) =>
        deviceName.StartsWith(@"\\.\", StringComparison.Ordinal)
            ? deviceName[4..]
            : deviceName;

    private int Scale(int value) => (int)Math.Round(value * _dpi / 96.0);

    private static nuint ToNuint(IntPtr value) => unchecked((nuint)value.ToInt64());

    private nint HandleWindowMessage(IntPtr hwnd, uint msg, nuint wParam, nint lParam)
    {
        switch (msg)
        {
            case NativeMethods.WmPaint:
                PaintWindow();
                return 0;

            case NativeMethods.WmDrawItem:
                DrawOwnerButton(lParam);
                return 1;

            case NativeMethods.WmEraseBkgnd:
                return 1;

            case NativeMethods.WmCtlColorStatic:
                var staticHdc = unchecked((IntPtr)(nint)wParam);
                NativeMethods.SetBkMode(staticHdc, NativeMethods.Transparent);
                NativeMethods.SetTextColor(
                    staticHdc,
                    lParam == _statusLabel ? ColorRef(64, 92, 133) : ColorRef(15, 23, 42));
                return NativeMethods.GetStockObject(NativeMethods.NullBrush);

            case NativeMethods.WmCommand:
                HandleCommand((int)(wParam & 0xFFFF));
                return 0;

            case NativeMethods.WmKeyDown:
            case NativeMethods.WmSysKeyDown:
                if (_captureTarget != CaptureTarget.None)
                {
                    CaptureKey((int)wParam);
                    return 0;
                }

                if ((int)wParam == NativeMethods.VkEscape)
                {
                    NativeMethods.DestroyWindow(hwnd);
                    return 0;
                }

                return NativeMethods.DefWindowProc(hwnd, msg, wParam, lParam);

            case NativeMethods.WmClose:
                NativeMethods.DestroyWindow(hwnd);
                return 0;

            case NativeMethods.WmDestroy:
                EndCapture(resumeHotkeys: true);
                if (_font != IntPtr.Zero)
                {
                    NativeMethods.DeleteObject(_font);
                    _font = IntPtr.Zero;
                }

                if (_titleFont != IntPtr.Zero)
                {
                    NativeMethods.DeleteObject(_titleFont);
                    _titleFont = IntPtr.Zero;
                }

                if (_sectionFont != IntPtr.Zero)
                {
                    NativeMethods.DeleteObject(_sectionFont);
                    _sectionFont = IntPtr.Zero;
                }

                if (_smallFont != IntPtr.Zero)
                {
                    NativeMethods.DeleteObject(_smallFont);
                    _smallFont = IntPtr.Zero;
                }

                _hwnd = IntPtr.Zero;
                _current = null;
                _onClosed();
                return 0;

            default:
                return NativeMethods.DefWindowProc(hwnd, msg, wParam, lParam);
        }
    }

    private void HandleCommand(int commandId)
    {
        switch (commandId)
        {
            case IdCaptureGlobal:
                StartCapture(CaptureTarget.Global);
                break;
            case IdCapturePrimary:
                StartCapture(CaptureTarget.Primary);
                break;
            case IdCaptureSecondary:
                StartCapture(CaptureTarget.Secondary);
                break;
            case IdTestPrimary:
                TestTemporaryStandby(primary: true);
                break;
            case IdTestSecondary:
                TestTemporaryStandby(primary: false);
                break;
            case IdTurnAllOff:
                TurnAllMonitorsOff();
                break;
            case IdSave:
                TrySave();
                break;
            case IdCancel:
                NativeMethods.DestroyWindow(_hwnd);
                break;
        }
    }

    private static nint WndProc(IntPtr hwnd, uint msg, nuint wParam, nint lParam) =>
        _current?.HandleWindowMessage(hwnd, msg, wParam, lParam)
        ?? NativeMethods.DefWindowProc(hwnd, msg, wParam, lParam);

    private enum CaptureTarget
    {
        None,
        Global,
        Primary,
        Secondary
    }
}

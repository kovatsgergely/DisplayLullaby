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
    private const int IdChoosePrimaryTarget = 1301;
    private const int IdChooseSecondaryTarget = 1302;
    private const int IdChoosePowerMode = 1303;
    private const int SelectorMenuBase = 40000;
    private const int ClientWidth = 420;
    private const int ClientHeight = 374;

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
    private IntPtr _idleMinutesEdit;
    private IntPtr _wakeDelayEdit;
    private readonly Dictionary<int, NativeMethods.Rect> _clickRegions = new();
    private string _statusText = "Ready.";
    private int _pressedCommandId;
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
            "DisplayLullaby settings",
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

        NativeMethods.SetWindowText(_hwnd, "DisplayLullaby settings");
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

        _titleFont = NativeMethods.CreateFont(
            -Scale(16),
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
            -Scale(10),
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
            -Scale(9),
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
        var (width, height) = CalculateOuterWindowSize(ClientWidth, ClientHeight);
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

    private (int Width, int Height) CalculateOuterWindowSize(int clientWidth, int clientHeight)
    {
        var rect = new NativeMethods.Rect
        {
            Left = 0,
            Top = 0,
            Right = Scale(clientWidth),
            Bottom = Scale(clientHeight)
        };

        if (NativeMethods.AdjustWindowRectEx(ref rect, NativeMethods.WsOverlappedWindow, false, 0))
        {
            return (rect.Width, rect.Height);
        }

        return (Scale(clientWidth), Scale(clientHeight) + Scale(42));
    }

    private void CreateControls()
    {
        _clickRegions.Clear();
        AddClickRegion(IdCaptureGlobal, 166, 109, 62, 22);
        AddClickRegion(IdCapturePrimary, 166, 134, 62, 22);
        AddClickRegion(IdCaptureSecondary, 166, 159, 62, 22);
        AddClickRegion(IdChoosePrimaryTarget, 250, 134, 140, 22);
        AddClickRegion(IdChooseSecondaryTarget, 250, 159, 140, 22);
        AddClickRegion(IdChoosePowerMode, 104, 222, 84, 22);

        _idleMinutesEdit = CreateEdit(_settings.TemporaryStandbyIdleMinutes.ToString(), 244, 225, 24, 16, numbersOnly: true);
        _wakeDelayEdit = CreateEdit(_settings.TemporaryStandbyWakeDelaySeconds.ToString(), 358, 225, 18, 16, numbersOnly: true);

        AddClickRegion(IdTestPrimary, 14, 340, 76, 24);
        AddClickRegion(IdTestSecondary, 98, 340, 88, 24);
        AddClickRegion(IdTurnAllOff, 194, 340, 56, 24);
        AddClickRegion(IdSave, 292, 340, 52, 24);
        AddClickRegion(IdCancel, 352, 340, 54, 24);
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

        var headerRect = ScaleRect(0, 0, 420, 48);
        FillRect(hdc, headerRect, ColorRef(238, 245, 253));

        DrawRoundRect(hdc, 14, 8, 32, 32, 8, ColorRef(37, 99, 235), ColorRef(30, 64, 175));
        DrawMonitorGlyph(hdc, 22, 17, 0.66);

        DrawTextLine(hdc, "DisplayLullaby settings", 56, 7, 292, 22, _titleFont, ColorRef(15, 23, 42));
        DrawTextLine(hdc, "Hotkeys, standby handoff, and display targets", 57, 28, 292, 16, _smallFont, ColorRef(71, 94, 131));

        DrawCard(hdc, 12, 58, 396, 124, "Hotkeys");
        DrawTextLine(hdc, "Command", 28, 86, 118, 16, _smallFont, ColorRef(15, 23, 42));
        DrawTextLine(hdc, "Hotkey", 166, 86, 62, 16, _smallFont, ColorRef(15, 23, 42));
        DrawTextLine(hdc, "Target", 250, 86, 100, 16, _smallFont, ColorRef(15, 23, 42));
        DrawTextLine(hdc, "All monitors off", 28, 111, 128, 18, _font, ColorRef(15, 23, 42));
        DrawTextLine(hdc, "Primary standby", 28, 136, 128, 18, _font, ColorRef(15, 23, 42));
        DrawTextLine(hdc, "Secondary standby", 28, 161, 128, 18, _font, ColorRef(15, 23, 42));
        DrawButton(hdc, IdCaptureGlobal, HotkeyText(CaptureTarget.Global), ButtonKind.Hotkey);
        DrawButton(hdc, IdCapturePrimary, HotkeyText(CaptureTarget.Primary), ButtonKind.Hotkey);
        DrawButton(hdc, IdCaptureSecondary, HotkeyText(CaptureTarget.Secondary), ButtonKind.Hotkey);
        DrawButton(hdc, IdChoosePrimaryTarget, _settings.PrimaryStandbyTarget, ButtonKind.Selector);
        DrawButton(hdc, IdChooseSecondaryTarget, _settings.SecondaryStandbyTarget, ButtonKind.Selector);

        DrawCard(hdc, 12, 190, 396, 60, "Standby behavior");
        DrawTextLine(hdc, "DDC mode", 28, 222, 66, 18, _font, ColorRef(15, 23, 42));
        DrawButton(hdc, IdChoosePowerMode, SerializePowerMode(_settings.PowerMode), ButtonKind.Selector);
        DrawTextLine(hdc, "Idle", 198, 222, 32, 18, _font, ColorRef(15, 23, 42));
        DrawInputFrame(hdc, 238, 222, 36, 22);
        DrawTextLine(hdc, "min", 280, 223, 24, 17, _smallFont, ColorRef(15, 23, 42));
        DrawTextLine(hdc, "Wake", 306, 222, 38, 18, _font, ColorRef(15, 23, 42));
        DrawInputFrame(hdc, 352, 222, 32, 22);
        DrawTextLine(hdc, "sec", 390, 223, 22, 17, _smallFont, ColorRef(15, 23, 42));

        DrawCard(hdc, 12, 258, 396, 60, "Detected displays");
        DrawDisplaySummary(hdc, 28, 288);

        DrawTextLine(hdc, _statusText, 14, 320, 278, 17, _smallFont, ColorRef(64, 92, 133));
        var footerLine = ScaleRect(12, 334, 396, 1);
        FillRect(hdc, footerLine, ColorRef(225, 232, 242));
        DrawButton(hdc, IdTestPrimary, "Test primary", ButtonKind.Secondary);
        DrawButton(hdc, IdTestSecondary, "Test secondary", ButtonKind.Secondary);
        DrawButton(hdc, IdTurnAllOff, "All off", ButtonKind.Danger);
        DrawButton(hdc, IdSave, "Save", ButtonKind.Primary);
        DrawButton(hdc, IdCancel, "Cancel", ButtonKind.Secondary);
    }

    private void AddClickRegion(int id, int x, int y, int width, int height)
    {
        _clickRegions[id] = ScaleRect(x, y, width, height);
    }

    private void DrawInputFrame(IntPtr hdc, int x, int y, int width, int height)
    {
        DrawRoundRect(hdc, x, y, width, height, 6, ColorRef(255, 255, 255), ColorRef(194, 205, 221));
    }

    private void DrawCard(IntPtr hdc, int x, int y, int width, int height, string title)
    {
        DrawRoundRect(hdc, x, y, width, height, 10, ColorRef(255, 255, 255), ColorRef(218, 228, 240));
        DrawTextLine(hdc, title, x + 18, y + 10, width - 36, 18, _sectionFont, ColorRef(32, 55, 83));
    }

    private void DrawDisplaySummary(IntPtr hdc, int x, int y)
    {
        var lines = BuildDisplaySummary().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < Math.Min(lines.Length, 2); i++)
        {
            DrawTextLine(hdc, lines[i], x, y + (i * 15), 360, 14, _smallFont, ColorRef(15, 23, 42));
        }
    }

    private string HotkeyText(CaptureTarget target)
    {
        if (_captureTarget == target)
        {
            return "Press key";
        }

        return target switch
        {
            CaptureTarget.Global => _settings.GlobalOffHotkey,
            CaptureTarget.Primary => _settings.PrimaryStandbyHotkey,
            CaptureTarget.Secondary => _settings.SecondaryStandbyHotkey,
            _ => string.Empty
        };
    }

    private void DrawButton(IntPtr hdc, int id, string text, ButtonKind kind)
    {
        if (!_clickRegions.TryGetValue(id, out var rect))
        {
            return;
        }

        var pressed = _pressedCommandId == id;
        var fill = kind switch
        {
            ButtonKind.Primary => pressed ? ColorRef(29, 78, 216) : ColorRef(37, 99, 235),
            ButtonKind.Danger => pressed ? ColorRef(254, 243, 199) : ColorRef(255, 251, 235),
            ButtonKind.Hotkey => pressed ? ColorRef(236, 244, 255) : ColorRef(248, 251, 255),
            _ => pressed ? ColorRef(248, 250, 252) : ColorRef(255, 255, 255)
        };
        var border = kind switch
        {
            ButtonKind.Primary => ColorRef(29, 78, 216),
            ButtonKind.Danger => ColorRef(245, 158, 11),
            ButtonKind.Hotkey => ColorRef(168, 198, 235),
            _ => ColorRef(176, 190, 210)
        };
        var textColor = kind switch
        {
            ButtonKind.Primary => ColorRef(255, 255, 255),
            ButtonKind.Danger => ColorRef(120, 53, 15),
            ButtonKind.Hotkey => ColorRef(29, 78, 216),
            _ => ColorRef(15, 23, 42)
        };

        DrawRoundRectRaw(hdc, rect, Scale(kind == ButtonKind.Primary ? 7 : 6), fill, border);
        var textRect = rect;
        if (pressed)
        {
            textRect.Left += Scale(1);
            textRect.Top += Scale(1);
        }

        if (kind == ButtonKind.Selector)
        {
            DrawTextLineRaw(hdc, text, textRect, _font, textColor);
            DrawSelectorChevron(hdc, rect);
            return;
        }

        DrawCenteredText(hdc, text, textRect, _font, textColor);
    }

    private void DrawMonitorGlyph(IntPtr hdc, int x, int y)
    {
        DrawMonitorGlyph(hdc, x, y, 1.0);
    }

    private void DrawMonitorGlyph(IntPtr hdc, int x, int y, double scale)
    {
        var pen = NativeMethods.CreatePen(NativeMethods.PsSolid, Scale(Math.Max(1, (int)Math.Round(2 * scale))), ColorRef(255, 255, 255));
        var brush = NativeMethods.CreateSolidBrush(ColorRef(219, 234, 254));
        var oldPen = NativeMethods.SelectObject(hdc, pen);
        var oldBrush = NativeMethods.SelectObject(hdc, brush);

        var screenWidth = (int)Math.Round(21 * scale);
        var screenHeight = (int)Math.Round(16 * scale);
        NativeMethods.RoundRect(hdc, Scale(x), Scale(y), Scale(x + screenWidth), Scale(y + screenHeight), Scale(4), Scale(4));
        var standRect = ScaleRect(x + (int)Math.Round(8 * scale), y + (int)Math.Round(18 * scale), Math.Max(3, (int)Math.Round(5 * scale)), Math.Max(4, (int)Math.Round(6 * scale)));
        FillRect(hdc, standRect, ColorRef(255, 255, 255));
        var footRect = ScaleRect(x + (int)Math.Round(4 * scale), y + (int)Math.Round(25 * scale), Math.Max(9, (int)Math.Round(14 * scale)), Math.Max(2, (int)Math.Round(3 * scale)));
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
        var isSelector = id is IdChoosePrimaryTarget or IdChooseSecondaryTarget or IdChoosePowerMode;
        var isDanger = id == IdTurnAllOff;

        var fill = isPrimary ? ColorRef(37, 99, 235)
            : isDanger ? ColorRef(255, 251, 235)
            : isHotkey ? ColorRef(248, 251, 255)
            : isSelector ? ColorRef(255, 255, 255)
            : ColorRef(255, 255, 255);
        var border = isPrimary ? ColorRef(29, 78, 216)
            : isDanger ? ColorRef(245, 158, 11)
            : isHotkey ? ColorRef(168, 198, 235)
            : isSelector ? ColorRef(176, 190, 210)
            : ColorRef(203, 213, 225);
        var textColor = isPrimary ? ColorRef(255, 255, 255)
            : isDanger ? ColorRef(120, 53, 15)
            : isHotkey ? ColorRef(29, 78, 216)
            : ColorRef(15, 23, 42);

        if (isPressed)
        {
            fill = isPrimary ? ColorRef(29, 78, 216)
                : isDanger ? ColorRef(254, 243, 199)
                : isHotkey ? ColorRef(236, 244, 255)
                : isSelector ? ColorRef(248, 250, 252)
                : ColorRef(241, 245, 249);
        }

        DrawRoundRectRaw(item.hDC, item.rcItem, Scale(isPrimary ? 7 : 6), fill, border);
        var textRect = item.rcItem;
        if (isPressed)
        {
            textRect.Left += Scale(1);
            textRect.Top += Scale(1);
        }

        if (isSelector)
        {
            DrawTextLineRaw(item.hDC, text, textRect, _font, textColor);
            DrawSelectorChevron(item.hDC, item.rcItem);
        }
        else
        {
            DrawCenteredText(item.hDC, text, textRect, _font, textColor);
        }
    }

    private void DrawSelectorChevron(IntPtr hdc, NativeMethods.Rect rect)
    {
        var pen = NativeMethods.CreatePen(NativeMethods.PsSolid, Scale(1), ColorRef(71, 85, 105));
        var oldPen = NativeMethods.SelectObject(hdc, pen);
        var centerX = rect.Right - Scale(15);
        var centerY = rect.Top + (rect.Height / 2) + Scale(1);
        var size = Scale(4);
        NativeMethods.MoveToEx(hdc, centerX - size, centerY - (size / 2), out _);
        NativeMethods.LineTo(hdc, centerX, centerY + (size / 2));
        NativeMethods.LineTo(hdc, centerX + size, centerY - (size / 2));
        NativeMethods.SelectObject(hdc, oldPen);
        NativeMethods.DeleteObject(pen);
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

    private void DrawTextLineRaw(IntPtr hdc, string text, NativeMethods.Rect rect, IntPtr font, uint color)
    {
        rect.Left += Scale(10);
        rect.Right -= Scale(26);
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
                    NativeMethods.EsAutoHScroll |
                    (numbersOnly ? NativeMethods.EsNumber : 0);
        var handle = CreateControl("EDIT", text, style, 0, 0, x, y, width, height);
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

        SetStatus(target switch
        {
            CaptureTarget.Global => "Press a key for all monitors off.",
            CaptureTarget.Primary => "Press a key for primary temporary standby.",
            CaptureTarget.Secondary => "Press a key for secondary temporary standby.",
            _ => "Press a key."
        });

        NativeMethods.InvalidateRect(_hwnd, IntPtr.Zero, false);
        NativeMethods.SetFocus(_hwnd);
    }

    private void EndCapture(bool resumeHotkeys)
    {
        if (_captureTarget == CaptureTarget.None)
        {
            return;
        }

        _captureTarget = CaptureTarget.None;
        NativeMethods.InvalidateRect(_hwnd, IntPtr.Zero, false);

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
            PrimaryStandbyTarget = _settings.PrimaryStandbyTarget,
            SecondaryStandbyTarget = _settings.SecondaryStandbyTarget,
            PowerMode = _settings.PowerMode,
            TemporaryStandbyIdleMinutes = idleMinutes,
            TemporaryStandbyWakeDelaySeconds = wakeDelaySeconds
        };

        return true;
    }

    private void TestTemporaryStandby(bool primary)
    {
        EndCapture(resumeHotkeys: true);
        var target = (primary ? _settings.PrimaryStandbyTarget : _settings.SecondaryStandbyTarget).Trim();
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

    private void ShowTargetMenu(bool primary)
    {
        EndCapture(resumeHotkeys: true);
        var current = (primary ? _settings.PrimaryStandbyTarget : _settings.SecondaryStandbyTarget).Trim();
        var selected = ShowSelectorMenu(BuildTargetOptions(current), current, SelectorMenuBase);
        if (selected.Length == 0)
        {
            return;
        }

        _settings = primary
            ? _settings with { PrimaryStandbyTarget = selected }
            : _settings with { SecondaryStandbyTarget = selected };
        SetStatus(primary ? "Primary target updated." : "Secondary target updated.");
    }

    private void ShowPowerModeMenu()
    {
        EndCapture(resumeHotkeys: true);
        var current = SerializePowerMode(_settings.PowerMode);
        var selected = ShowSelectorMenu(
            ["Standby", "Suspend", "PowerOff", "SoftOff"],
            current,
            SelectorMenuBase + 100);
        if (selected.Length == 0)
        {
            return;
        }

        _settings = _settings with { PowerMode = ParsePowerMode(selected) };
        SetStatus("DDC mode updated.");
    }

    private string ShowSelectorMenu(string[] options, string current, int idBase)
    {
        if (options.Length == 0)
        {
            return string.Empty;
        }

        var menu = NativeMethods.CreatePopupMenu();
        if (menu == IntPtr.Zero)
        {
            return string.Empty;
        }

        try
        {
            for (var i = 0; i < options.Length; i++)
            {
                var flags = NativeMethods.MfString;
                if (options[i].Equals(current, StringComparison.OrdinalIgnoreCase))
                {
                    flags |= NativeMethods.MfChecked;
                }

                NativeMethods.AppendMenu(menu, flags, (nuint)(idBase + i), options[i]);
            }

            if (!NativeMethods.GetCursorPos(out var point))
            {
                point = new NativeMethods.Point();
            }

            NativeMethods.SetForegroundWindow(_hwnd);
            var command = NativeMethods.TrackPopupMenu(
                menu,
                NativeMethods.TpmReturNCmd | NativeMethods.TpmRightButton,
                point.X,
                point.Y,
                0,
                _hwnd,
                IntPtr.Zero);
            var index = (int)command - idBase;
            return index >= 0 && index < options.Length ? options[index] : string.Empty;
        }
        finally
        {
            NativeMethods.DestroyMenu(menu);
        }
    }

    private string[] BuildTargetOptions(string selectedTarget)
    {
        var options = new List<string> { "primary", "secondary" };
        if (!string.IsNullOrWhiteSpace(selectedTarget) &&
            !options.Contains(selectedTarget, StringComparer.OrdinalIgnoreCase))
        {
            options.Add(selectedTarget);
        }

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

    private string[] BuildTargetOptions() => BuildTargetOptions(string.Empty);

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

    private void SetStatus(string text)
    {
        _statusText = text;
        NativeMethods.InvalidateRect(_hwnd, IntPtr.Zero, false);
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

    private int HitTest(nint lParam)
    {
        var x = unchecked((short)((long)lParam & 0xFFFF));
        var y = unchecked((short)(((long)lParam >> 16) & 0xFFFF));
        foreach (var (id, rect) in _clickRegions)
        {
            if (x >= rect.Left && x < rect.Right && y >= rect.Top && y < rect.Bottom)
            {
                return id;
            }
        }

        return 0;
    }

    private void HandleMouseDown(nint lParam)
    {
        var commandId = HitTest(lParam);
        if (commandId == 0)
        {
            return;
        }

        _pressedCommandId = commandId;
        NativeMethods.InvalidateRect(_hwnd, IntPtr.Zero, false);
    }

    private void HandleMouseUp(nint lParam)
    {
        var pressedCommandId = _pressedCommandId;
        _pressedCommandId = 0;
        if (pressedCommandId != 0)
        {
            NativeMethods.InvalidateRect(_hwnd, IntPtr.Zero, false);
        }

        if (pressedCommandId != 0 && pressedCommandId == HitTest(lParam))
        {
            HandleCommand(pressedCommandId);
        }
    }

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

            case NativeMethods.WmCtlColorEdit:
                var editHdc = unchecked((IntPtr)(nint)wParam);
                NativeMethods.SetTextColor(editHdc, ColorRef(15, 23, 42));
                NativeMethods.SetBkColor(editHdc, ColorRef(255, 255, 255));
                return NativeMethods.GetStockObject(NativeMethods.WhiteBrush);

            case NativeMethods.WmCtlColorStatic:
                var staticHdc = unchecked((IntPtr)(nint)wParam);
                NativeMethods.SetBkMode(staticHdc, NativeMethods.Transparent);
                NativeMethods.SetTextColor(staticHdc, ColorRef(15, 23, 42));
                return NativeMethods.GetStockObject(NativeMethods.NullBrush);

            case NativeMethods.WmLButtonDown:
                HandleMouseDown(lParam);
                return 0;

            case NativeMethods.WmLButtonUp:
                HandleMouseUp(lParam);
                return 0;

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
            case IdChoosePrimaryTarget:
                ShowTargetMenu(primary: true);
                break;
            case IdChooseSecondaryTarget:
                ShowTargetMenu(primary: false);
                break;
            case IdChoosePowerMode:
                ShowPowerModeMenu();
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

    private enum ButtonKind
    {
        Secondary,
        Primary,
        Danger,
        Hotkey,
        Selector
    }
}

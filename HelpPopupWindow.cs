using System.Runtime.InteropServices;

namespace DisplayLullaby;

internal sealed record HelpDisplayRow(string DeviceName, string Description, string Role);

internal sealed record HelpHotkeyRow(string Key, string Description);

internal sealed record HelpPopupContent(IReadOnlyList<HelpDisplayRow> Displays, IReadOnlyList<HelpHotkeyRow> Hotkeys);

internal sealed unsafe class HelpPopupWindow
{
    private const string ClassName = "DisplayLullabyHelpPopup";
    private const int DesignDpi = 96;
    private const int WidthDips = 390;
    private const int KeyPillWidthDips = 48;
    private const int KeyPillHeightDips = 22;
    private static readonly NativeMethods.WndProc PopupWndProc = WndProc;
    private static HelpPopupWindow? _current;

    private IntPtr _hwnd;
    private IntPtr _owner;
    private HelpPopupContent _content = new(Array.Empty<HelpDisplayRow>(), Array.Empty<HelpHotkeyRow>());
    private double _scale = 1.0;

    public HelpPopupWindow()
    {
        _current = this;
    }

    public bool IsVisible { get; private set; }

    public DateTime LastHiddenUtc { get; private set; } = DateTime.MinValue;

    public void Show(IntPtr owner, HelpPopupContent content)
    {
        _owner = owner;
        _content = content;
        EnsureWindow();
        UpdateDpiScale();

        var width = Scale(WidthDips);
        var height = Scale(CalculateHeightDips(content));
        var position = CalculatePosition(width, height);

        NativeMethods.SetWindowPos(
            _hwnd,
            NativeMethods.HwndTopMost,
            position.X,
            position.Y,
            width,
            height,
            NativeMethods.SwpShowWindow);

        NativeMethods.ShowWindow(_hwnd, NativeMethods.SwShow);
        NativeMethods.SetForegroundWindow(_hwnd);
        NativeMethods.SetFocus(_hwnd);
        NativeMethods.InvalidateRect(_hwnd, IntPtr.Zero, true);
        IsVisible = true;
    }

    public void Hide()
    {
        if (!IsVisible && _hwnd == IntPtr.Zero)
        {
            return;
        }

        if (_hwnd != IntPtr.Zero)
        {
            NativeMethods.ShowWindow(_hwnd, NativeMethods.SwHide);
        }

        IsVisible = false;
        LastHiddenUtc = DateTime.UtcNow;
    }

    public void Destroy()
    {
        if (_hwnd != IntPtr.Zero)
        {
            NativeMethods.DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
        }

        IsVisible = false;
        LastHiddenUtc = DateTime.UtcNow;
    }

    private void EnsureWindow()
    {
        if (_hwnd != IntPtr.Zero)
        {
            return;
        }

        var instance = NativeMethods.GetModuleHandle(null);
        var windowClass = new NativeMethods.WndClassEx
        {
            cbSize = (uint)Marshal.SizeOf<NativeMethods.WndClassEx>(),
            lpfnWndProc = PopupWndProc,
            hInstance = instance,
            lpszClassName = ClassName
        };

        NativeMethods.RegisterClassEx(ref windowClass);

        _hwnd = NativeMethods.CreateWindowEx(
            NativeMethods.WsExToolWindow | NativeMethods.WsExTopMost,
            ClassName,
            "DisplayLullaby",
            NativeMethods.WsPopup,
            0,
            0,
            Scale(WidthDips),
            Scale(CalculateHeightDips(_content)),
            _owner,
            IntPtr.Zero,
            instance,
            IntPtr.Zero);

        if (_hwnd == IntPtr.Zero)
        {
            throw new InvalidOperationException($"Could not create help popup: {NativeMethods.LastErrorMessage()}");
        }
    }

    private static NativeMethods.Point CalculatePosition(int width, int height)
    {
        if (!NativeMethods.GetCursorPos(out var point))
        {
            point = new NativeMethods.Point { X = 0, Y = 0 };
        }

        var work = GetWorkArea(point);
        var x = point.X - width + 24;
        var y = point.Y - height - 14;

        if (y < work.Top + 8)
        {
            y = point.Y + 14;
        }

        x = Math.Clamp(x, work.Left + 8, work.Right - width - 8);
        y = Math.Clamp(y, work.Top + 8, work.Bottom - height - 8);

        return new NativeMethods.Point { X = x, Y = y };
    }

    private static NativeMethods.Rect GetWorkArea(NativeMethods.Point point)
    {
        var monitor = NativeMethods.MonitorFromPoint(point, NativeMethods.MonitorDefaultToNearest);
        var info = new NativeMethods.MonitorInfoEx
        {
            cbSize = (uint)sizeof(NativeMethods.MonitorInfoEx)
        };

        return monitor != IntPtr.Zero && NativeMethods.GetMonitorInfo(monitor, ref info)
            ? info.rcWork
            : new NativeMethods.Rect { Left = 0, Top = 0, Right = 1920, Bottom = 1080 };
    }

    private static int CalculateHeightDips(HelpPopupContent content)
    {
        var displayRows = Math.Max(1, content.Displays.Count);
        var hotkeyRows = Math.Max(1, content.Hotkeys.Count);
        return 148 + (hotkeyRows * 38) + (displayRows * 30);
    }

    private void UpdateDpiScale()
    {
        var dpi = TryGetCursorMonitorDpi();
        if (dpi == 0)
        {
            dpi = TryGetWindowDpi();
        }

        if (dpi == 0)
        {
            dpi = TryGetSystemDpi();
        }

        if (dpi < 72 || dpi > 768)
        {
            dpi = DesignDpi;
        }

        _scale = dpi / (double)DesignDpi;
    }

    private static uint TryGetCursorMonitorDpi()
    {
        try
        {
            if (!NativeMethods.GetCursorPos(out var point))
            {
                return 0;
            }

            var monitor = NativeMethods.MonitorFromPoint(point, NativeMethods.MonitorDefaultToNearest);
            return monitor != IntPtr.Zero
                && NativeMethods.GetDpiForMonitor(monitor, NativeMethods.MdtEffectiveDpi, out var dpiX, out _) == 0
                    ? dpiX
                    : 0;
        }
        catch (DllNotFoundException)
        {
            return 0;
        }
        catch (EntryPointNotFoundException)
        {
            return 0;
        }
    }

    private uint TryGetWindowDpi()
    {
        try
        {
            return _hwnd == IntPtr.Zero ? 0 : NativeMethods.GetDpiForWindow(_hwnd);
        }
        catch (EntryPointNotFoundException)
        {
            return 0;
        }
    }

    private static uint TryGetSystemDpi()
    {
        try
        {
            return NativeMethods.GetDpiForSystem();
        }
        catch (EntryPointNotFoundException)
        {
            return DesignDpi;
        }
    }

    private int Scale(int dips) => Math.Max(1, (int)Math.Round(dips * _scale));

    private int ScaleSigned(int dips) => (int)Math.Round(dips * _scale);

    private nint HandleMessage(IntPtr hwnd, uint msg, nuint wParam, nint lParam)
    {
        switch (msg)
        {
            case NativeMethods.WmPaint:
                Paint(hwnd);
                return 0;

            case NativeMethods.WmKeyDown:
                if ((int)wParam == NativeMethods.VkEscape)
                {
                    Hide();
                    return 0;
                }

                break;

            case NativeMethods.WmLButtonDown:
            case NativeMethods.WmRButtonDown:
                Hide();
                return 0;

            case NativeMethods.WmActivate:
                if (((uint)wParam & 0xFFFF) == NativeMethods.WaInactive)
                {
                    Hide();
                    return 0;
                }

                break;

            case NativeMethods.WmKillFocus:
            case NativeMethods.WmClose:
                Hide();
                return 0;

            case NativeMethods.WmDestroy:
                if (hwnd == _hwnd)
                {
                    _hwnd = IntPtr.Zero;
                    IsVisible = false;
                }

                return 0;
        }

        return NativeMethods.DefWindowProc(hwnd, msg, wParam, lParam);
    }

    private void Paint(IntPtr hwnd)
    {
        var hdc = NativeMethods.BeginPaint(hwnd, out var paint);
        if (hdc == IntPtr.Zero)
        {
            return;
        }

        try
        {
            NativeMethods.GetClientRect(hwnd, out var client);
            DrawPanel(hdc, client);
        }
        finally
        {
            NativeMethods.EndPaint(hwnd, ref paint);
        }
    }

    private void DrawPanel(IntPtr hdc, NativeMethods.Rect client)
    {
        var canvasBrush = NativeMethods.CreateSolidBrush(ColorRef(248, 250, 252));
        var cardBrush = NativeMethods.CreateSolidBrush(ColorRef(255, 255, 255));
        var cardPen = NativeMethods.CreatePen(NativeMethods.PsSolid, Scale(1), ColorRef(226, 232, 240));
        var softBlueBrush = NativeMethods.CreateSolidBrush(ColorRef(239, 246, 255));
        var softBluePen = NativeMethods.CreatePen(NativeMethods.PsSolid, Scale(1), ColorRef(191, 219, 254));
        var rowBrush = NativeMethods.CreateSolidBrush(ColorRef(248, 250, 252));
        var rowPen = NativeMethods.CreatePen(NativeMethods.PsSolid, Scale(1), ColorRef(226, 232, 240));
        var blueBrush = NativeMethods.CreateSolidBrush(ColorRef(37, 99, 235));
        var whiteBrush = NativeMethods.CreateSolidBrush(ColorRef(255, 255, 255));

        var titleFont = CreateFont(20, NativeMethods.FwSemiBold);
        var subtitleFont = CreateFont(14, NativeMethods.FwNormal);
        var bodyFont = CreateFont(14, NativeMethods.FwNormal);
        var bodySemiboldFont = CreateFont(14, NativeMethods.FwSemiBold);
        var captionFont = CreateFont(11, NativeMethods.FwSemiBold);
        var footerFont = CreateFont(12, NativeMethods.FwNormal);
        var keyFont = CreateFont(14, NativeMethods.FwSemiBold);

        try
        {
            NativeMethods.FillRect(hdc, ref client, canvasBrush);
            NativeMethods.SetBkMode(hdc, NativeMethods.Transparent);

            SelectBrushAndPen(hdc, cardBrush, cardPen, h =>
                NativeMethods.RoundRect(
                    h,
                    Scale(6),
                    Scale(6),
                    client.Right - Scale(6),
                    client.Bottom - Scale(6),
                    Scale(20),
                    Scale(20)));

            DrawMonitorGlyph(hdc, 22, 21, blueBrush, whiteBrush);
            DrawText(hdc, "DisplayLullaby", 66, 18, 210, 24, titleFont, ColorRef(15, 23, 42));
            DrawText(hdc, "F9 off; F10/F11 standby until input", 66, 41, 292, 20, subtitleFont, ColorRef(100, 116, 139));

            var y = 77;
            if (_content.Hotkeys.Count == 0)
            {
                DrawActionRow(hdc, "-", "No hotkeys configured", 22, y, 346, softBlueBrush, softBluePen, keyFont, bodyFont);
                y += 42;
            }
            else
            {
                foreach (var hotkey in _content.Hotkeys)
                {
                    DrawActionRow(hdc, hotkey.Key, hotkey.Description, 22, y, 346, softBlueBrush, softBluePen, keyFont, bodyFont);
                    y += 42;
                }
            }

            y += 8;
            DrawText(hdc, "DISPLAYS", 22, y, 130, 18, captionFont, ColorRef(71, 85, 105));
            y += 22;

            if (_content.Displays.Count == 0)
            {
                DrawDisplayRow(hdc, "-", "No displays are visible right now", "", 22, y, 346, rowBrush, rowPen, bodySemiboldFont, bodyFont);
                y += 30;
            }
            else
            {
                foreach (var display in _content.Displays)
                {
                    DrawDisplayRow(hdc, display.DeviceName, display.Description, display.Role, 22, y, 346, rowBrush, rowPen, bodySemiboldFont, bodyFont);
                    y += 30;
                }
            }

            DrawText(
                hdc,
                "Left-click: hide. Right-click: menu. Esc: close.",
                22,
                CalculateHeightDips(_content) - 31,
                344,
                18,
                footerFont,
                ColorRef(100, 116, 139));
        }
        finally
        {
            DeleteIfNotZero(canvasBrush);
            DeleteIfNotZero(cardBrush);
            DeleteIfNotZero(cardPen);
            DeleteIfNotZero(softBlueBrush);
            DeleteIfNotZero(softBluePen);
            DeleteIfNotZero(rowBrush);
            DeleteIfNotZero(rowPen);
            DeleteIfNotZero(blueBrush);
            DeleteIfNotZero(whiteBrush);
            DeleteIfNotZero(titleFont);
            DeleteIfNotZero(subtitleFont);
            DeleteIfNotZero(bodyFont);
            DeleteIfNotZero(bodySemiboldFont);
            DeleteIfNotZero(captionFont);
            DeleteIfNotZero(footerFont);
            DeleteIfNotZero(keyFont);
        }
    }

    private void DrawMonitorGlyph(IntPtr hdc, int x, int y, IntPtr blueBrush, IntPtr whiteBrush)
    {
        SelectBrushAndPen(hdc, blueBrush, IntPtr.Zero, h =>
            NativeMethods.RoundRect(h, Scale(x), Scale(y), Scale(x + 32), Scale(y + 32), Scale(10), Scale(10)));

        SelectBrushAndPen(hdc, whiteBrush, IntPtr.Zero, h =>
            NativeMethods.RoundRect(h, Scale(x + 7), Scale(y + 8), Scale(x + 25), Scale(y + 22), Scale(4), Scale(4)));

        SelectBrushAndPen(hdc, blueBrush, IntPtr.Zero, h =>
            NativeMethods.RoundRect(h, Scale(x + 10), Scale(y + 11), Scale(x + 22), Scale(y + 18), Scale(2), Scale(2)));

        FillBox(hdc, x + 14, y + 22, x + 18, y + 26, whiteBrush);
        FillBox(hdc, x + 10, y + 26, x + 22, y + 28, whiteBrush);
    }

    private void DrawActionRow(
        IntPtr hdc,
        string key,
        string description,
        int x,
        int y,
        int width,
        IntPtr brush,
        IntPtr pen,
        IntPtr keyFont,
        IntPtr bodyFont)
    {
        SelectBrushAndPen(hdc, brush, pen, h =>
            NativeMethods.RoundRect(h, Scale(x), Scale(y), Scale(x + width), Scale(y + 34), Scale(10), Scale(10)));

        DrawKeyPill(hdc, key, x + 10, y + 6, keyFont);
        DrawText(hdc, description, x + 72, y + 7, width - 86, 20, bodyFont, ColorRef(30, 41, 59));
    }

    private void DrawKeyPill(IntPtr hdc, string key, int x, int y, IntPtr keyFont)
    {
        var brush = NativeMethods.CreateSolidBrush(ColorRef(255, 255, 255));
        var pen = NativeMethods.CreatePen(NativeMethods.PsSolid, Scale(1), ColorRef(191, 219, 254));

        try
        {
            SelectBrushAndPen(hdc, brush, pen, h =>
                NativeMethods.RoundRect(
                    h,
                    Scale(x),
                    Scale(y),
                    Scale(x + KeyPillWidthDips),
                    Scale(y + KeyPillHeightDips),
                    Scale(7),
                    Scale(7)));

            DrawText(
                hdc,
                key,
                x,
                y,
                KeyPillWidthDips,
                KeyPillHeightDips,
                keyFont,
                ColorRef(37, 99, 235),
                NativeMethods.DtCenter,
                useMetricCentering: true,
                visualOffsetYDips: 0);
        }
        finally
        {
            DeleteIfNotZero(brush);
            DeleteIfNotZero(pen);
        }
    }

    private void DrawDisplayRow(
        IntPtr hdc,
        string deviceName,
        string description,
        string role,
        int x,
        int y,
        int width,
        IntPtr brush,
        IntPtr pen,
        IntPtr deviceFont,
        IntPtr bodyFont)
    {
        SelectBrushAndPen(hdc, brush, pen, h =>
            NativeMethods.RoundRect(h, Scale(x), Scale(y), Scale(x + width), Scale(y + 26), Scale(8), Scale(8)));

        DrawText(hdc, deviceName, x + 10, y + 4, 72, 18, deviceFont, ColorRef(15, 23, 42));
        DrawText(hdc, description, x + 86, y + 4, width - 158, 18, bodyFont, ColorRef(30, 41, 59));

        if (!string.IsNullOrWhiteSpace(role))
        {
            DrawText(hdc, role, x + width - 64, y + 4, 54, 18, bodyFont, ColorRef(100, 116, 139), NativeMethods.DtCenter);
        }
    }

    private void FillBox(IntPtr hdc, int left, int top, int right, int bottom, IntPtr brush)
    {
        var rect = new NativeMethods.Rect
        {
            Left = Scale(left),
            Top = Scale(top),
            Right = Scale(right),
            Bottom = Scale(bottom)
        };
        NativeMethods.FillRect(hdc, ref rect, brush);
    }

    private static void SelectBrushAndPen(IntPtr hdc, IntPtr brush, IntPtr pen, Action<IntPtr> draw)
    {
        var oldBrush = brush == IntPtr.Zero ? IntPtr.Zero : NativeMethods.SelectObject(hdc, brush);
        var oldPen = pen == IntPtr.Zero ? IntPtr.Zero : NativeMethods.SelectObject(hdc, pen);

        try
        {
            draw(hdc);
        }
        finally
        {
            if (oldPen != IntPtr.Zero)
            {
                NativeMethods.SelectObject(hdc, oldPen);
            }

            if (oldBrush != IntPtr.Zero)
            {
                NativeMethods.SelectObject(hdc, oldBrush);
            }
        }
    }

    private void DrawText(
        IntPtr hdc,
        string text,
        int left,
        int top,
        int width,
        int height,
        IntPtr font,
        uint color,
        uint alignment = NativeMethods.DtLeft,
        bool useMetricCentering = false,
        int visualOffsetYDips = 0)
    {
        var visualOffsetY = ScaleSigned(visualOffsetYDips);
        var rect = new NativeMethods.Rect
        {
            Left = Scale(left),
            Top = Scale(top) + visualOffsetY,
            Right = Scale(left + width),
            Bottom = Scale(top + height) + visualOffsetY
        };

        var oldFont = NativeMethods.SelectObject(hdc, font);
        NativeMethods.SetTextColor(hdc, color);

        if (useMetricCentering && NativeMethods.GetTextMetrics(hdc, out var metrics))
        {
            var oldAlign = NativeMethods.SetTextAlign(
                hdc,
                NativeMethods.TaCenter | NativeMethods.TaBaseline | NativeMethods.TaNoUpdateCp);
            var textHeight = metrics.tmAscent + metrics.tmDescent;
            var baseline = rect.Top + ((rect.Height - textHeight) / 2) + metrics.tmAscent;
            NativeMethods.TextOut(hdc, rect.Left + (rect.Width / 2), baseline, text, text.Length);
            NativeMethods.SetTextAlign(hdc, oldAlign);
        }
        else
        {
            NativeMethods.DrawText(
                hdc,
                text,
                -1,
                ref rect,
                alignment | NativeMethods.DtSingleLine | NativeMethods.DtVCenter | NativeMethods.DtNoPrefix | NativeMethods.DtEndEllipsis);
        }

        NativeMethods.SelectObject(hdc, oldFont);
    }

    private IntPtr CreateFont(int height, int weight)
    {
        return NativeMethods.CreateFont(
            -Scale(height),
            0,
            0,
            0,
            weight,
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

    private static uint ColorRef(byte red, byte green, byte blue)
    {
        return (uint)(red | (green << 8) | (blue << 16));
    }

    private static void DeleteIfNotZero(IntPtr handle)
    {
        if (handle != IntPtr.Zero)
        {
            NativeMethods.DeleteObject(handle);
        }
    }

    private static nint WndProc(IntPtr hwnd, uint msg, nuint wParam, nint lParam)
    {
        return _current?.HandleMessage(hwnd, msg, wParam, lParam)
               ?? NativeMethods.DefWindowProc(hwnd, msg, wParam, lParam);
    }
}

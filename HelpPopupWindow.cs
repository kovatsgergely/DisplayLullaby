using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace DisplayLullaby;

internal sealed record HelpDisplayRow(string DeviceName, string Description, string Role);

internal sealed record HelpHotkeyRow(string Key, string Description);

internal sealed record HelpPopupContent(IReadOnlyList<HelpDisplayRow> Displays, IReadOnlyList<HelpHotkeyRow> Hotkeys);

internal sealed class HelpPopupWindow
{
    private const int DesignDpi = 96;
    private const int WidthDips = 500;
    private AvaloniaHelpPopupWindow? _window;
    private int _isVisible;

    public bool IsVisible => Volatile.Read(ref _isVisible) != 0;

    public DateTime LastHiddenUtc { get; private set; } = DateTime.MinValue;

    public void Show(IntPtr owner, HelpPopupContent content)
    {
        AvaloniaUiHost.Invoke(() =>
        {
            _window ??= new AvaloniaHelpPopupWindow(MarkHidden);
            _window.UpdateContent(content);
            _window.PrepareToShow();

            var heightDips = CalculateHeightDips(content);
            var scale = GetCursorScale();
            var widthPixels = Scale(WidthDips, scale);
            var heightPixels = Scale(heightDips, scale);
            var position = CalculatePosition(widthPixels, heightPixels);

            _window.Width = WidthDips;
            _window.Height = heightDips;
            _window.Position = new PixelPoint(position.X, position.Y);

            Volatile.Write(ref _isVisible, 1);
            if (!_window.IsVisible)
            {
                _window.Show();
            }

            _window.Activate();
        });
    }

    public void Hide()
    {
        if (!IsVisible && _window is null)
        {
            return;
        }

        AvaloniaUiHost.Invoke(() =>
        {
            if (_window is { } window && window.IsVisible)
            {
                window.HidePopup();
            }
            else
            {
                MarkHidden();
            }
        });
    }

    public void Destroy()
    {
        AvaloniaUiHost.Invoke(() =>
        {
            _window?.Close();
            _window = null;
            MarkHidden();
        });
    }

    private void MarkHidden()
    {
        Volatile.Write(ref _isVisible, 0);
        LastHiddenUtc = DateTime.UtcNow;
    }

    private static int CalculateHeightDips(HelpPopupContent content)
    {
        var displayRows = Math.Max(1, content.Displays.Count);
        var hotkeyRows = Math.Max(1, content.Hotkeys.Count);
        return 128 + (hotkeyRows * 40) + (displayRows * 31);
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

        var minX = work.Left + 8;
        var minY = work.Top + 8;
        var maxX = Math.Max(minX, work.Right - width - 8);
        var maxY = Math.Max(minY, work.Bottom - height - 8);

        return new NativeMethods.Point
        {
            X = Math.Clamp(x, minX, maxX),
            Y = Math.Clamp(y, minY, maxY)
        };
    }

    private static NativeMethods.Rect GetWorkArea(NativeMethods.Point point)
    {
        var monitor = NativeMethods.MonitorFromPoint(point, NativeMethods.MonitorDefaultToNearest);
        var info = new NativeMethods.MonitorInfoEx
        {
            cbSize = (uint)Marshal.SizeOf<NativeMethods.MonitorInfoEx>()
        };

        return monitor != IntPtr.Zero && NativeMethods.GetMonitorInfo(monitor, ref info)
            ? info.rcWork
            : new NativeMethods.Rect { Left = 0, Top = 0, Right = 1920, Bottom = 1080 };
    }

    private static double GetCursorScale()
    {
        var dpi = TryGetCursorMonitorDpi();
        if (dpi < 72 || dpi > 768)
        {
            dpi = DesignDpi;
        }

        return dpi / (double)DesignDpi;
    }

    private static uint TryGetCursorMonitorDpi()
    {
        try
        {
            if (!NativeMethods.GetCursorPos(out var point))
            {
                return DesignDpi;
            }

            var monitor = NativeMethods.MonitorFromPoint(point, NativeMethods.MonitorDefaultToNearest);
            return monitor != IntPtr.Zero
                && NativeMethods.GetDpiForMonitor(monitor, NativeMethods.MdtEffectiveDpi, out var dpiX, out _) == 0
                    ? dpiX
                    : DesignDpi;
        }
        catch (DllNotFoundException)
        {
            return DesignDpi;
        }
        catch (EntryPointNotFoundException)
        {
            return DesignDpi;
        }
    }

    private static int Scale(int dips, double scale) => Math.Max(1, (int)Math.Round(dips * scale));
}

internal sealed class AvaloniaHelpPopupWindow : Window
{
    private readonly Action _onHidden;
    private DateTime _shownUtc = DateTime.MinValue;
    private bool _hiding;

    public AvaloniaHelpPopupWindow(Action onHidden)
    {
        _onHidden = onHidden;

        Title = "DisplayLullaby";
        CanResize = false;
        ShowInTaskbar = false;
        SizeToContent = SizeToContent.Manual;
        WindowStartupLocation = WindowStartupLocation.Manual;
        WindowDecorations = Avalonia.Controls.WindowDecorations.None;
        Topmost = true;
        UseLayoutRounding = true;
        FontFamily = new FontFamily("Inter, Segoe UI");
        Background = Brush(248, 250, 252);

        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                HidePopup();
            }
        };
        PointerPressed += (_, _) =>
        {
            if (!IsOpeningGracePeriod())
            {
                HidePopup();
            }
        };
        Closed += (_, _) => _onHidden();
    }

    public void PrepareToShow()
    {
        _shownUtc = DateTime.UtcNow;
    }

    public void UpdateContent(HelpPopupContent content)
    {
        Content = BuildContent(content);
    }

    public void HidePopup()
    {
        if (_hiding)
        {
            return;
        }

        _hiding = true;
        try
        {
            Hide();
            _onHidden();
        }
        finally
        {
            _hiding = false;
        }
    }

    private static Control BuildContent(HelpPopupContent content)
    {
        var stack = new StackPanel
        {
            Spacing = 10
        };
        stack.Children.Add(BuildHeader());
        stack.Children.Add(BuildHotkeys(content.Hotkeys));
        stack.Children.Add(BuildDisplays(content.Displays));
        stack.Children.Add(new TextBlock
        {
            Text = "Left-click: hide. Right-click: menu. Esc: close.",
            FontSize = 12,
            Foreground = Brush(82, 105, 145),
            TextWrapping = TextWrapping.Wrap
        });

        return new Border
        {
            Margin = new Thickness(6),
            Padding = new Thickness(16),
            Background = Brushes.White,
            BorderBrush = Brush(203, 213, 225),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Child = stack
        };
    }

    private static Control BuildHeader()
    {
        var grid = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition(GridLength.Auto),
                new ColumnDefinition(GridLength.Star)
            },
            ColumnSpacing = 12
        };

        var icon = new Image
        {
            Source = LoadBitmapAsset("avares://DisplayLullaby/Assets/settings.png"),
            Width = 38,
            Height = 38,
            Stretch = Stretch.Uniform,
            VerticalAlignment = VerticalAlignment.Center
        };

        var text = new StackPanel
        {
            Spacing = 0,
            VerticalAlignment = VerticalAlignment.Center
        };
        text.Children.Add(new TextBlock
        {
            Text = "DisplayLullaby",
            FontSize = 20,
            FontWeight = FontWeight.SemiBold,
            Foreground = Brush(15, 23, 42)
        });
        text.Children.Add(new TextBlock
        {
            Text = "Monitor standby shortcuts",
            FontSize = 13,
            Foreground = Brush(82, 105, 145)
        });

        Grid.SetColumn(icon, 0);
        Grid.SetColumn(text, 1);
        grid.Children.Add(icon);
        grid.Children.Add(text);
        return grid;
    }

    private static Control BuildHotkeys(IReadOnlyList<HelpHotkeyRow> hotkeys)
    {
        var stack = new StackPanel
        {
            Spacing = 6
        };

        if (hotkeys.Count == 0)
        {
            stack.Children.Add(BuildActionRow("-", "No hotkeys configured"));
            return stack;
        }

        foreach (var hotkey in hotkeys)
        {
            stack.Children.Add(BuildActionRow(hotkey.Key, hotkey.Description));
        }

        return stack;
    }

    private static Control BuildActionRow(string key, string description)
    {
        var grid = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition(54, GridUnitType.Pixel),
                new ColumnDefinition(GridLength.Star)
            },
            ColumnSpacing = 10
        };

        var keyPill = new Border
        {
            Width = 48,
            Height = 22,
            Background = Brushes.White,
            BorderBrush = Brush(191, 219, 254),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = key,
                FontSize = 13,
                FontWeight = FontWeight.SemiBold,
                Foreground = Brush(37, 99, 235),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                TextAlignment = TextAlignment.Center
            }
        };

        var text = new TextBlock
        {
            Text = description,
            FontSize = 14,
            Foreground = Brush(30, 41, 59),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };

        Grid.SetColumn(keyPill, 0);
        Grid.SetColumn(text, 1);
        grid.Children.Add(keyPill);
        grid.Children.Add(text);

        return new Border
        {
            Height = 34,
            Background = Brush(239, 246, 255),
            BorderBrush = Brush(191, 219, 254),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(10, 5),
            Child = grid
        };
    }

    private static Control BuildDisplays(IReadOnlyList<HelpDisplayRow> displays)
    {
        var stack = new StackPanel
        {
            Spacing = 6
        };
        stack.Children.Add(new TextBlock
        {
            Text = "DISPLAYS",
            FontSize = 11,
            FontWeight = FontWeight.SemiBold,
            Foreground = Brush(71, 85, 105),
            Margin = new Thickness(0, 2, 0, 0)
        });

        if (displays.Count == 0)
        {
            stack.Children.Add(BuildDisplayRow("-", "No displays are visible right now", string.Empty));
            return stack;
        }

        foreach (var display in displays)
        {
            stack.Children.Add(BuildDisplayRow(display.DeviceName, display.Description, display.Role));
        }

        return stack;
    }

    private static Control BuildDisplayRow(string deviceName, string description, string role)
    {
        var grid = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition(76, GridUnitType.Pixel),
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(GridLength.Auto)
            },
            ColumnSpacing = 8
        };

        var device = new TextBlock
        {
            Text = deviceName,
            FontSize = 13,
            FontWeight = FontWeight.SemiBold,
            Foreground = Brush(15, 23, 42),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        var descriptionText = new TextBlock
        {
            Text = description,
            FontSize = 13,
            Foreground = Brush(30, 41, 59),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        var roleText = new TextBlock
        {
            Text = role,
            FontSize = 13,
            Foreground = Brush(82, 105, 145),
            VerticalAlignment = VerticalAlignment.Center,
            IsVisible = !string.IsNullOrWhiteSpace(role)
        };

        Grid.SetColumn(device, 0);
        Grid.SetColumn(descriptionText, 1);
        Grid.SetColumn(roleText, 2);
        grid.Children.Add(device);
        grid.Children.Add(descriptionText);
        grid.Children.Add(roleText);

        return new Border
        {
            Height = 26,
            Background = Brush(248, 250, 252),
            BorderBrush = Brush(226, 232, 240),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 3),
            Child = grid
        };
    }

    private static Bitmap LoadBitmapAsset(string assetUri)
    {
        using var stream = AssetLoader.Open(new Uri(assetUri));
        return new Bitmap(stream);
    }

    private static SolidColorBrush Brush(byte r, byte g, byte b) => new(Color.FromRgb(r, g, b));

    private bool IsOpeningGracePeriod() => (DateTime.UtcNow - _shownUtc).TotalMilliseconds < 300;
}

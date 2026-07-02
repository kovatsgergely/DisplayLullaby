using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace DisplayLullaby;

internal sealed class SettingsWindow
{
    private readonly AppSettings _settings;
    private readonly Action _onSaved;
    private readonly Action<TrayAction, string> _executeCommand;
    private readonly Action _onCaptureStarted;
    private readonly Action _onCaptureEnded;
    private readonly Action _onClosed;
    private AvaloniaSettingsWindow? _window;
    private int _isOpen;

    public SettingsWindow(
        IntPtr owner,
        AppSettings settings,
        Action onSaved,
        Action<TrayAction, string> executeCommand,
        Action onCaptureStarted,
        Action onCaptureEnded,
        Action onClosed)
    {
        _settings = settings;
        _onSaved = onSaved;
        _executeCommand = executeCommand;
        _onCaptureStarted = onCaptureStarted;
        _onCaptureEnded = onCaptureEnded;
        _onClosed = onClosed;
    }

    public bool IsOpen => Volatile.Read(ref _isOpen) != 0;

    public void Show()
    {
        AvaloniaUiHost.Invoke(() =>
        {
            if (_window is { } existing)
            {
                if (!existing.IsVisible)
                {
                    existing.Show();
                }

                existing.Activate();
                return;
            }

            _window = new AvaloniaSettingsWindow(
                _settings,
                _onSaved,
                _executeCommand,
                _onCaptureStarted,
                _onCaptureEnded,
                () =>
                {
                    Volatile.Write(ref _isOpen, 0);
                    _window = null;
                    _onClosed();
                });

            Volatile.Write(ref _isOpen, 1);
            _window.Show();
            _window.Activate();
        });
    }

    public void Close()
    {
        AvaloniaUiHost.Invoke(() => _window?.Close());
    }
}

internal sealed class AvaloniaSettingsWindow : Window
{
    private readonly Action _onSaved;
    private readonly Action<TrayAction, string> _executeCommand;
    private readonly Action _onCaptureStarted;
    private readonly Action _onCaptureEnded;
    private readonly Action _onClosed;
    private readonly TextBlock _statusText;
    private readonly Border _statusBox;
    private readonly Button _globalHotkeyButton;
    private readonly Button _primaryHotkeyButton;
    private readonly Button _secondaryHotkeyButton;
    private readonly ComboBox _primaryTargetCombo;
    private readonly ComboBox _secondaryTargetCombo;
    private readonly ComboBox _powerModeCombo;
    private readonly TextBox _idleMinutesTextBox;
    private readonly TextBox _wakeDelayTextBox;
    private AppSettings _settings;
    private CaptureTarget _captureTarget;
    private Button? _captureTooltipButton;
    private DateTime _captureStartedUtc;

    public AvaloniaSettingsWindow(
        AppSettings settings,
        Action onSaved,
        Action<TrayAction, string> executeCommand,
        Action onCaptureStarted,
        Action onCaptureEnded,
        Action onClosed)
    {
        _settings = settings;
        _onSaved = onSaved;
        _executeCommand = executeCommand;
        _onCaptureStarted = onCaptureStarted;
        _onCaptureEnded = onCaptureEnded;
        _onClosed = onClosed;

        Title = "DisplayLullaby settings";
        Width = 470;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        UseLayoutRounding = true;
        FontFamily = new FontFamily("Inter, Segoe UI");
        Background = Brush(246, 248, 252);

        _statusText = new TextBlock
        {
            Text = string.Empty,
            FontSize = 12,
            FontWeight = FontWeight.SemiBold,
            Foreground = Brush(30, 64, 120),
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center
        };

        _statusBox = new Border
        {
            IsVisible = false,
            MinHeight = 36,
            Background = Brush(238, 245, 253),
            BorderBrush = Brush(190, 210, 235),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(10, 7),
            Margin = new Thickness(0, 0, 0, 7),
            Child = _statusText
        };

        _globalHotkeyButton = HotkeyButton(settings.GlobalOffHotkey, () => StartCapture(CaptureTarget.Global));
        _primaryHotkeyButton = HotkeyButton(settings.PrimaryStandbyHotkey, () => StartCapture(CaptureTarget.Primary));
        _secondaryHotkeyButton = HotkeyButton(settings.SecondaryStandbyHotkey, () => StartCapture(CaptureTarget.Secondary));

        var targetOptions = BuildTargetOptions(settings.PrimaryStandbyTarget, settings.SecondaryStandbyTarget);
        _primaryTargetCombo = Combo(targetOptions, settings.PrimaryStandbyTarget);
        _secondaryTargetCombo = Combo(targetOptions, settings.SecondaryStandbyTarget);
        _powerModeCombo = Combo(["Standby", "Suspend", "PowerOff", "SoftOff"], SerializePowerMode(settings.PowerMode));

        _idleMinutesTextBox = NumberBox(settings.TemporaryStandbyIdleMinutes.ToString(), 52);
        _wakeDelayTextBox = NumberBox(settings.TemporaryStandbyWakeDelaySeconds.ToString(), 52);

        Content = BuildContent();
        KeyDown += HandleKeyDown;
        PointerPressed += HandlePointerPressed;
        Deactivated += HandleDeactivated;
        Closed += (_, _) =>
        {
            EndCapture(resumeHotkeys: true);
            _onClosed();
        };
    }

    private Control BuildContent()
    {
        var root = new DockPanel
        {
            LastChildFill = true
        };

        var header = BuildHeader();
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);

        var body = new StackPanel
        {
            Spacing = 8,
            Margin = new Thickness(10, 10, 10, 0)
        };
        body.Children.Add(BuildHotkeyCard());
        body.Children.Add(BuildBehaviorCard());
        body.Children.Add(BuildDisplaysCard());

        DockPanel.SetDock(body, Dock.Top);
        root.Children.Add(body);

        var footer = BuildFooter();
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);

        return root;
    }

    private Control BuildHeader()
    {
        var grid = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition(GridLength.Auto),
                new ColumnDefinition(GridLength.Star)
            },
            Margin = new Thickness(0)
        };

        var icon = new Border
        {
            Width = 44,
            Height = 44,
            CornerRadius = new CornerRadius(9),
            Background = Brush(37, 99, 235),
            BorderBrush = Brush(30, 64, 175),
            BorderThickness = new Thickness(1),
            Child = new TextBlock
            {
                Text = "Display",
                FontSize = 8,
                FontWeight = FontWeight.SemiBold,
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                TextAlignment = TextAlignment.Center
            }
        };

        var text = new StackPanel
        {
            Spacing = 1,
            Margin = new Thickness(12, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        text.Children.Add(new TextBlock
        {
            Text = "DisplayLullaby settings",
            FontSize = 21,
            FontWeight = FontWeight.SemiBold,
            Foreground = Brush(15, 23, 42)
        });
        text.Children.Add(new TextBlock
        {
            Text = "Hotkeys, standby handoff, and display targets",
            FontSize = 12,
            Foreground = Brush(55, 83, 128)
        });

        Grid.SetColumn(icon, 0);
        Grid.SetColumn(text, 1);
        grid.Children.Add(icon);
        grid.Children.Add(text);
        return new Border
        {
            Background = Brush(238, 245, 253),
            Padding = new Thickness(16, 12, 16, 12),
            Child = grid
        };
    }

    private Control BuildHotkeyCard()
    {
        var grid = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition(1.45, GridUnitType.Star),
                new ColumnDefinition(78, GridUnitType.Pixel),
                new ColumnDefinition(154, GridUnitType.Pixel)
            },
            RowDefinitions =
            {
                new RowDefinition(GridLength.Auto),
                new RowDefinition(GridLength.Auto),
                new RowDefinition(GridLength.Auto),
                new RowDefinition(GridLength.Auto)
            },
            ColumnSpacing = 10,
            RowSpacing = 6
        };

        AddCell(grid, HeaderText("Command"), 0, 0);
        AddCell(grid, HeaderText("Hotkey"), 0, 1);
        AddCell(grid, HeaderText("Target"), 0, 2);
        AddHotkeyRow(grid, 1, "All monitors off", _globalHotkeyButton, null);
        AddHotkeyRow(grid, 2, "Primary standby", _primaryHotkeyButton, _primaryTargetCombo);
        AddHotkeyRow(grid, 3, "Secondary standby", _secondaryHotkeyButton, _secondaryTargetCombo);

        return Card("Hotkeys", grid);
    }

    private Control BuildBehaviorCard()
    {
        var grid = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition(82, GridUnitType.Pixel),
                new ColumnDefinition(124, GridUnitType.Pixel),
                new ColumnDefinition(1, GridUnitType.Star),
                new ColumnDefinition(GridLength.Auto)
            },
            ColumnSpacing = 6,
            RowSpacing = 7,
            RowDefinitions =
            {
                new RowDefinition(GridLength.Auto),
                new RowDefinition(GridLength.Auto)
            }
        };

        AddCell(grid, Label("DDC mode"), 0, 0);
        AddCell(grid, _powerModeCombo, 0, 1);
        AddCell(grid, Label("Idle"), 1, 0);
        AddCell(grid, ValueWithUnit(_idleMinutesTextBox, "min"), 1, 1);
        AddCell(grid, WakeDelayGroup(), 1, 3);

        return Card("Standby behavior", grid);
    }

    private Control BuildDisplaysCard()
    {
        var stack = new StackPanel
        {
            Spacing = 4
        };

        foreach (var row in BuildDisplayRows())
        {
            stack.Children.Add(new TextBlock
            {
                Text = row,
                FontSize = 12,
                Foreground = Brush(15, 23, 42)
            });
        }

        return Card("Detected displays", stack);
    }

    private Control BuildFooter()
    {
        var grid = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition(GridLength.Auto),
                new ColumnDefinition(GridLength.Auto),
                new ColumnDefinition(GridLength.Auto),
                new ColumnDefinition(1, GridUnitType.Star),
                new ColumnDefinition(GridLength.Auto),
                new ColumnDefinition(GridLength.Auto)
            },
            ColumnSpacing = 5,
            RowDefinitions =
            {
                new RowDefinition(GridLength.Auto),
                new RowDefinition(GridLength.Auto)
            },
            Margin = new Thickness(10, 7, 10, 10)
        };

        AddCell(grid, _statusBox, 0, 0, columnSpan: 6);

        var testPrimary = SecondaryButton("Test primary", () => TestTemporaryStandby(primary: true), 100);
        var testSecondary = SecondaryButton("Test secondary", () => TestTemporaryStandby(primary: false), 118);
        var allOff = SecondaryButton("All off", TurnAllMonitorsOff, 64);
        allOff.Foreground = Brush(120, 53, 15);
        allOff.BorderBrush = Brush(245, 158, 11);
        allOff.Background = Brush(255, 251, 235);
        var save = PrimaryButton("Save", Save, 68);
        var cancel = SecondaryButton("Cancel", Close, 70);

        AddCell(grid, testPrimary, 1, 0);
        AddCell(grid, testSecondary, 1, 1);
        AddCell(grid, allOff, 1, 2);
        AddCell(grid, save, 1, 4);
        AddCell(grid, cancel, 1, 5);

        return grid;
    }

    private static Border Card(string title, Control content)
    {
        var stack = new StackPanel
        {
            Spacing = 8
        };
        stack.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 14,
            FontWeight = FontWeight.SemiBold,
            Foreground = Brush(32, 55, 83)
        });
        stack.Children.Add(content);

        return new Border
        {
            Background = Brushes.White,
            BorderBrush = Brush(218, 228, 240),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14, 12, 14, 12),
            Child = stack
        };
    }

    private static TextBlock HeaderText(string text) =>
        new()
        {
            Text = text,
            FontSize = 11,
            Foreground = Brush(15, 23, 42)
        };

    private static TextBlock Label(string text) =>
        new()
        {
            Text = text,
            FontSize = 13,
            Foreground = Brush(15, 23, 42),
            VerticalAlignment = VerticalAlignment.Center
        };

    private static TextBlock Unit(string text) =>
        new()
        {
            Text = text,
            FontSize = 11,
            Foreground = Brush(15, 23, 42),
            VerticalAlignment = VerticalAlignment.Center
        };

    private StackPanel WakeDelayGroup()
    {
        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center
        };
        panel.Children.Add(Label("Wake"));
        panel.Children.Add(_wakeDelayTextBox);
        panel.Children.Add(Unit("sec"));
        return panel;
    }

    private static StackPanel ValueWithUnit(Control value, string unit)
    {
        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center
        };
        panel.Children.Add(value);
        panel.Children.Add(Unit(unit));
        return panel;
    }

    private static Button HotkeyButton(string text, Action click)
    {
        var button = SecondaryButton(text, click, 78);
        button.Foreground = Brush(29, 78, 216);
        button.Background = Brush(248, 251, 255);
        ToolTip.SetPlacement(button, PlacementMode.Right);
        return button;
    }

    private static Button PrimaryButton(string text, Action click, double minWidth)
    {
        var button = Button(text, click, minWidth);
        button.Background = Brush(37, 99, 235);
        button.Foreground = Brushes.White;
        button.BorderBrush = Brush(29, 78, 216);
        return button;
    }

    private static Button SecondaryButton(string text, Action click, double minWidth) => Button(text, click, minWidth);

    private static Button Button(string text, Action click, double minWidth)
    {
        var button = new Button
        {
            Content = text,
            MinWidth = minWidth,
            MinHeight = 30,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Padding = new Thickness(10, 4),
            FontSize = 12
        };
        button.Click += (_, _) => click();
        return button;
    }

    private static ComboBox Combo(string[] items, string selected)
    {
        var combo = new ComboBox
        {
            ItemsSource = items,
            SelectedItem = items.FirstOrDefault(item => item.Equals(selected, StringComparison.OrdinalIgnoreCase)) ?? selected,
            MinHeight = 30,
            FontSize = 12,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        return combo;
    }

    private static TextBox NumberBox(string text, double width) =>
        new()
        {
            Text = text,
            Width = width,
            MinHeight = 30,
            FontSize = 12,
            HorizontalAlignment = HorizontalAlignment.Left,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Padding = new Thickness(6, 4)
        };

    private static void AddHotkeyRow(Grid grid, int row, string label, Button hotkey, ComboBox? target)
    {
        AddCell(grid, Label(label), row, 0);
        AddCell(grid, hotkey, row, 1);
        if (target is not null)
        {
            AddCell(grid, target, row, 2);
        }
    }

    private static void AddCell(Grid grid, Control control, int row, int column, int columnSpan = 1)
    {
        Grid.SetRow(control, row);
        Grid.SetColumn(control, column);
        if (columnSpan > 1)
        {
            Grid.SetColumnSpan(control, columnSpan);
        }

        grid.Children.Add(control);
    }

    private void StartCapture(CaptureTarget target)
    {
        _captureTarget = target;
        _captureStartedUtc = DateTime.UtcNow;
        _onCaptureStarted();
        UpdateHotkeyButtons();
        SetStatus(string.Empty);
        ShowCaptureTooltip(target, target switch
        {
            CaptureTarget.Global => "Press a key for all monitors off.",
            CaptureTarget.Primary => "Press a key for primary standby.",
            CaptureTarget.Secondary => "Press a key for secondary standby.",
            _ => "Press a key."
        });
        Focus();
    }

    private void EndCapture(bool resumeHotkeys)
    {
        if (_captureTarget == CaptureTarget.None)
        {
            return;
        }

        HideCaptureTooltip();
        _captureTarget = CaptureTarget.None;
        UpdateHotkeyButtons();
        if (resumeHotkeys)
        {
            _onCaptureEnded();
        }
    }

    private void ShowCaptureTooltip(CaptureTarget target, string message)
    {
        HideCaptureTooltip();
        var button = CaptureButton(target);
        if (button is null)
        {
            return;
        }

        ToolTip.SetTip(button, message);
        ToolTip.SetIsOpen(button, true);
        _captureTooltipButton = button;
    }

    private void HideCaptureTooltip()
    {
        if (_captureTooltipButton is null)
        {
            return;
        }

        ToolTip.SetIsOpen(_captureTooltipButton, false);
        ToolTip.SetTip(_captureTooltipButton, null);
        _captureTooltipButton = null;
    }

    private Button? CaptureButton(CaptureTarget target) =>
        target switch
        {
            CaptureTarget.Global => _globalHotkeyButton,
            CaptureTarget.Primary => _primaryHotkeyButton,
            CaptureTarget.Secondary => _secondaryHotkeyButton,
            _ => null
        };

    private void UpdateHotkeyButtons()
    {
        _globalHotkeyButton.Content = _captureTarget == CaptureTarget.Global ? "Press key" : _settings.GlobalOffHotkey;
        _primaryHotkeyButton.Content = _captureTarget == CaptureTarget.Primary ? "Press key" : _settings.PrimaryStandbyHotkey;
        _secondaryHotkeyButton.Content = _captureTarget == CaptureTarget.Secondary ? "Press key" : _settings.SecondaryStandbyHotkey;
    }

    private void HandleKeyDown(object? sender, KeyEventArgs e)
    {
        if (_captureTarget != CaptureTarget.None)
        {
            e.Handled = true;
            CaptureKey(e);
            return;
        }

        if (e.Key == Key.Escape)
        {
            Close();
        }
    }

    private void HandlePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_captureTarget == CaptureTarget.None)
        {
            return;
        }

        EndCapture(resumeHotkeys: true);
        e.Handled = true;
    }

    private void HandleDeactivated(object? sender, EventArgs e)
    {
        if (_captureTarget == CaptureTarget.None ||
            (DateTime.UtcNow - _captureStartedUtc).TotalMilliseconds < 300)
        {
            return;
        }

        EndCapture(resumeHotkeys: true);
    }

    private void CaptureKey(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            SetStatus("Hotkey capture canceled.");
            EndCapture(resumeHotkeys: true);
            return;
        }

        if (IsModifierKey(e.Key))
        {
            return;
        }

        var chord = FormatChord(e);
        if (chord.Length == 0)
        {
            SetStatus("That key cannot be used as a hotkey.");
            return;
        }

        if (!AppConfig.TryValidateHotkeyText(chord, out var warning))
        {
            SetStatus(warning);
            return;
        }

        _settings = _captureTarget switch
        {
            CaptureTarget.Global => _settings with { GlobalOffHotkey = chord },
            CaptureTarget.Primary => _settings with { PrimaryStandbyHotkey = chord },
            CaptureTarget.Secondary => _settings with { SecondaryStandbyHotkey = chord },
            _ => _settings
        };

        EndCapture(resumeHotkeys: true);
        SetStatus($"Hotkey set to {chord}.");
    }

    private void Save()
    {
        EndCapture(resumeHotkeys: true);
        if (!TryReadSettings(out var settings, out var warning))
        {
            SetStatus(warning);
            return;
        }

        if (!AppConfig.TryValidateSettings(settings, out warning))
        {
            SetStatus(warning);
            return;
        }

        AppConfig.SaveSettings(settings);
        _settings = settings;
        _onSaved();
        SetStatus("Saved. Hotkeys applied.");
    }

    private bool TryReadSettings(out AppSettings settings, out string warning)
    {
        settings = _settings;
        warning = string.Empty;

        if (!int.TryParse((_idleMinutesTextBox.Text ?? string.Empty).Trim(), out var idleMinutes))
        {
            warning = "Idle handoff minutes must be a number.";
            return false;
        }

        if (!int.TryParse((_wakeDelayTextBox.Text ?? string.Empty).Trim(), out var wakeDelaySeconds))
        {
            warning = "Wake delay seconds must be a number.";
            return false;
        }

        var primaryTarget = (_primaryTargetCombo.SelectedItem as string ?? _settings.PrimaryStandbyTarget).Trim();
        var secondaryTarget = (_secondaryTargetCombo.SelectedItem as string ?? _settings.SecondaryStandbyTarget).Trim();
        var powerMode = ParsePowerMode(_powerModeCombo.SelectedItem as string ?? SerializePowerMode(_settings.PowerMode));

        settings = _settings with
        {
            PrimaryStandbyTarget = primaryTarget,
            SecondaryStandbyTarget = secondaryTarget,
            PowerMode = powerMode,
            TemporaryStandbyIdleMinutes = idleMinutes,
            TemporaryStandbyWakeDelaySeconds = wakeDelaySeconds
        };

        return true;
    }

    private void TestTemporaryStandby(bool primary)
    {
        EndCapture(resumeHotkeys: true);
        var target = (primary
            ? _primaryTargetCombo.SelectedItem as string ?? _settings.PrimaryStandbyTarget
            : _secondaryTargetCombo.SelectedItem as string ?? _settings.SecondaryStandbyTarget).Trim();

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

    private static string[] BuildTargetOptions(params string[] currentValues)
    {
        var options = new List<string> { "primary", "secondary" };

        foreach (var value in currentValues)
        {
            AddOption(options, value);
        }

        try
        {
            using var session = MonitorController.OpenSession();
            foreach (var target in session.Targets)
            {
                AddOption(options, TrimDeviceName(target.DeviceName));
                AddOption(options, target.Id.ToString());
            }
        }
        catch
        {
            // The settings UI can still edit existing logical targets if DDC/CI enumeration fails.
        }

        return options.ToArray();
    }

    private static IEnumerable<string> BuildDisplayRows()
    {
        try
        {
            using var session = MonitorController.OpenSession();
            if (session.Targets.Count == 0)
            {
                return ["No DDC/CI displays detected."];
            }

            return session.Targets
                .Take(3)
                .Select(target => $"{TrimDeviceName(target.DeviceName)}  {(target.IsPrimary ? "primary" : "display")}  {target.Description}")
                .ToArray();
        }
        catch
        {
            return ["Could not read displays."];
        }
    }

    private static void AddOption(List<string> options, string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length > 0 && !options.Any(option => option.Equals(trimmed, StringComparison.OrdinalIgnoreCase)))
        {
            options.Add(trimmed);
        }
    }

    private static string TrimDeviceName(string deviceName) =>
        deviceName.StartsWith(@"\\.\", StringComparison.Ordinal)
            ? deviceName[4..]
            : deviceName;

    private static string SerializePowerMode(MonitorPowerMode mode) =>
        mode switch
        {
            MonitorPowerMode.Suspend => "Suspend",
            MonitorPowerMode.PowerOff => "PowerOff",
            MonitorPowerMode.SoftOff => "SoftOff",
            _ => "Standby"
        };

    private static MonitorPowerMode ParsePowerMode(string text) =>
        text switch
        {
            "Suspend" => MonitorPowerMode.Suspend,
            "PowerOff" => MonitorPowerMode.PowerOff,
            "SoftOff" => MonitorPowerMode.SoftOff,
            _ => MonitorPowerMode.Standby
        };

    private static string FormatChord(KeyEventArgs e)
    {
        var key = FormatKey(e.Key);
        if (key.Length == 0)
        {
            return string.Empty;
        }

        var parts = new List<string>();
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            parts.Add("Ctrl");
        }

        if (e.KeyModifiers.HasFlag(KeyModifiers.Alt))
        {
            parts.Add("Alt");
        }

        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            parts.Add("Shift");
        }

        if (e.KeyModifiers.HasFlag(KeyModifiers.Meta))
        {
            parts.Add("Win");
        }

        parts.Add(key);
        return string.Join("+", parts);
    }

    private static string FormatKey(Key key)
    {
        if (key is >= Key.A and <= Key.Z)
        {
            return key.ToString();
        }

        if (key is >= Key.D0 and <= Key.D9)
        {
            return ((int)(key - Key.D0)).ToString();
        }

        if (key is >= Key.F1 and <= Key.F24)
        {
            return key.ToString();
        }

        if (key is >= Key.NumPad0 and <= Key.NumPad9)
        {
            return "NumPad" + (int)(key - Key.NumPad0);
        }

        return key switch
        {
            Key.Space => "Space",
            Key.Tab => "Tab",
            Key.Return => "Enter",
            Key.Back => "Backspace",
            Key.Escape => "Esc",
            Key.Pause => "Pause",
            Key.Insert => "Insert",
            Key.Delete => "Delete",
            Key.Home => "Home",
            Key.End => "End",
            Key.PageUp => "PageUp",
            Key.PageDown => "PageDown",
            Key.Left => "Left",
            Key.Up => "Up",
            Key.Right => "Right",
            Key.Down => "Down",
            _ => string.Empty
        };
    }

    private static bool IsModifierKey(Key key) =>
        key is Key.LeftCtrl or Key.RightCtrl
            or Key.LeftShift or Key.RightShift
            or Key.LeftAlt or Key.RightAlt
            or Key.LWin or Key.RWin;

    private void SetStatus(string text)
    {
        _statusText.Text = text;
        _statusBox.IsVisible = text.Length > 0;
    }

    private static SolidColorBrush Brush(byte r, byte g, byte b) => new(Color.FromRgb(r, g, b));

    private enum CaptureTarget
    {
        None,
        Global,
        Primary,
        Secondary
    }
}

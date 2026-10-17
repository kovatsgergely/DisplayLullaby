using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using UI = DisplayLullaby.AvaloniaSettingsWindow;

namespace DisplayLullaby.SessionRestore;

/// <summary>Callbacks into the tray message loop; everything here is posted, never run on the UI thread.</summary>
internal sealed record SessionSettingsActions(
    Func<SessionRestoreSettings> LoadSettings,
    Func<SessionSnapshot?> LoadLatestSnapshot,
    Action SettingsSaved,
    Action SnapshotNow,
    Action RestoreNow,
    Action OpenSnapshotsFolder,
    Action Closed);

/// <summary>Single-instance host, same pattern as <see cref="SettingsWindow"/>.</summary>
internal sealed class SessionSettingsWindow(SessionSettingsActions actions)
{
    private AvaloniaSessionSettingsWindow? _window;
    private int _isOpen;

    public bool IsOpen => Volatile.Read(ref _isOpen) != 0;

    public void Show()
    {
        AvaloniaUiHost.Invoke(() =>
        {
            AvaloniaUiHost.ApplySystemTheme();
            if (_window is { } existing)
            {
                if (!existing.IsVisible)
                {
                    existing.Show();
                }

                existing.Activate();
                return;
            }

            _window = new AvaloniaSessionSettingsWindow(actions with
            {
                Closed = () =>
                {
                    Volatile.Write(ref _isOpen, 0);
                    _window = null;
                    actions.Closed();
                }
            }, AppIconLoader.Load);
            Volatile.Write(ref _isOpen, 1);
            _window.Show();
            _window.Activate();
        });
    }

    public void Close() => AvaloniaUiHost.Invoke(() => _window?.Close());
}

internal sealed class AvaloniaSessionSettingsWindow : Window
{
    private const string IntervalTip = "How often running apps and window positions are recorded (1-60 minutes).";
    private const string DelayTip = "Wait after sign-in before restoring, so the shell and tray can settle (0-600 seconds).";
    private const string WaitTip = "How long to wait for a launched app's window before moving on (1-120 seconds).";
    private const string KeepTip = "Number of snapshot files kept; older ones are deleted (1-50).";
    private const string AutoTip = "Restore only when Windows was restarted after the last snapshot.";

    private readonly SessionSettingsActions _actions;
    private readonly Func<AppRow, Bitmap?> _iconLoader;
    private readonly SessionSettingsForm _form;
    private readonly CheckBox _enabled;
    private readonly CheckBox _autoRestore;
    private readonly TextBox _interval;
    private readonly TextBox _delay;
    private readonly TextBox _wait;
    private readonly TextBox _keep;
    private readonly TextBox _advancedRules;
    private readonly TextBox _selfRestoring;
    private readonly TextBox _dropArguments;
    private readonly TextBlock _snapshotInfo;
    private readonly TextBlock _statusText;
    private readonly Border _statusBox;

    public AvaloniaSessionSettingsWindow(SessionSettingsActions actions, Func<AppRow, Bitmap?> iconLoader)
    {
        _actions = actions;
        _iconLoader = iconLoader;
        var latest = actions.LoadLatestSnapshot();
        _form = SessionSettingsForm.From(actions.LoadSettings(), latest);

        Title = "Session restore settings";
        Width = 560;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        UseLayoutRounding = true;
        FontFamily = new FontFamily("Inter, Segoe UI");
        Background = Palette.WindowBackground;

        _enabled = Check("Record snapshots of open apps", _form.Enabled);
        _autoRestore = UI.WithTooltip(Check("Restore automatically after reboot", _form.RestoreOnStartup), AutoTip);
        _interval = UI.NumberBox(_form.SnapshotIntervalMinutes, 52);
        _delay = UI.NumberBox(_form.StartupDelaySeconds, 52);
        _wait = UI.NumberBox(_form.WindowWaitSeconds, 52);
        _keep = UI.NumberBox(_form.KeepSnapshots, 52);
        _advancedRules = MultiLine(_form.AdvancedIgnoreRules, "e.g. C:\\Tools\\*  or  *.Photos_*", 54);
        _selfRestoring = MultiLine(_form.SelfRestoring, "Empty = built-in list:\nbrowsers, VS Code, Office,\nTerminal, Teams, Slack, ...", 74);
        _dropArguments = MultiLine(_form.DropArguments, "Empty = built-in list:\n--no-startup-window, /prefetch:*,\n--type=*, --background, ...", 74);
        _snapshotInfo = new TextBlock { FontSize = 12, Foreground = Palette.PrimaryText, TextWrapping = TextWrapping.Wrap };
        UpdateSnapshotInfo(latest);

        _statusText = new TextBlock
        {
            FontSize = 12,
            FontWeight = FontWeight.SemiBold,
            Foreground = Palette.StatusText,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center
        };
        _statusBox = new Border
        {
            IsVisible = false,
            MinHeight = 36,
            Background = Palette.StatusBackground,
            BorderBrush = Palette.StatusBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(10, 7),
            Margin = new Thickness(0, 0, 0, 7),
            Child = _statusText
        };

        Content = BuildContent();
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                Close();
            }
        };
        Closed += (_, _) => _actions.Closed();
    }

    private static DisplayLullabyPalette Palette => UI.Palette;

    private Control BuildContent()
    {
        var root = new DockPanel { LastChildFill = true };
        var header = BuildHeader();
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);

        var footer = BuildFooter();
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);

        var body = new StackPanel { Spacing = 8, Margin = new Thickness(10, 10, 10, 2) };
        body.Children.Add(BuildGeneralCard());
        body.Children.Add(BuildAppsCard());
        body.Children.Add(BuildAdvancedCard());
        body.Children.Add(BuildStatusCard());
        root.Children.Add(new ScrollViewer
        {
            Content = body,
            MaxHeight = 860,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled
        });
        return root;
    }

    private static Control BuildHeader()
    {
        var grid = new Grid { ColumnDefinitions = { new ColumnDefinition(GridLength.Auto), new ColumnDefinition(GridLength.Star) } };
        var icon = new Image
        {
            Width = 44,
            Height = 44,
            Source = UI.LoadBitmapAsset("avares://DisplayLullaby/Assets/settings.png"),
            Stretch = Stretch.Uniform,
            VerticalAlignment = VerticalAlignment.Center
        };
        var text = new StackPanel { Spacing = 1, Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = "Session restore", FontSize = 21, FontWeight = FontWeight.SemiBold, Foreground = Palette.PrimaryText });
        text.Children.Add(new TextBlock { Text = "Snapshots, apps to bring back, and relaunch rules", FontSize = 12, Foreground = Palette.SecondaryText });
        Grid.SetColumn(icon, 0);
        Grid.SetColumn(text, 1);
        grid.Children.Add(icon);
        grid.Children.Add(text);
        return new Border { Background = Palette.HeaderBackground, Padding = new Thickness(16, 12, 16, 12), Child = grid };
    }

    private Control BuildGeneralCard()
    {
        var grid = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition(120, GridUnitType.Pixel),
                new ColumnDefinition(1, GridUnitType.Star),
                new ColumnDefinition(120, GridUnitType.Pixel),
                new ColumnDefinition(1, GridUnitType.Star)
            },
            RowDefinitions =
            {
                new RowDefinition(GridLength.Auto),
                new RowDefinition(GridLength.Auto),
                new RowDefinition(GridLength.Auto),
                new RowDefinition(GridLength.Auto)
            },
            ColumnSpacing = 6,
            RowSpacing = 7
        };

        UI.AddCell(grid, _enabled, 0, 0, columnSpan: 4);
        UI.AddCell(grid, _autoRestore, 1, 0, columnSpan: 4);
        UI.AddCell(grid, UI.Label("Snapshot every", IntervalTip), 2, 0);
        UI.AddCell(grid, UI.ValueWithUnit(UI.WithTooltip(_interval, IntervalTip), "min"), 2, 1);
        UI.AddCell(grid, UI.Label("Startup delay", DelayTip), 2, 2);
        UI.AddCell(grid, UI.ValueWithUnit(UI.WithTooltip(_delay, DelayTip), "sec"), 2, 3);
        UI.AddCell(grid, UI.Label("Window wait", WaitTip), 3, 0);
        UI.AddCell(grid, UI.ValueWithUnit(UI.WithTooltip(_wait, WaitTip), "sec"), 3, 1);
        UI.AddCell(grid, UI.Label("Keep snapshots", KeepTip), 3, 2);
        UI.AddCell(grid, UI.ValueWithUnit(UI.WithTooltip(_keep, KeepTip), "files"), 3, 3);
        return UI.Card("General", grid);
    }

    private Control BuildAppsCard()
    {
        var list = new StackPanel { Spacing = 4, Margin = new Thickness(0, 0, 14, 0) };
        if (_form.Apps.Count == 0)
        {
            list.Children.Add(new TextBlock { Text = "No snapshot yet. Take one to see your apps here.", FontSize = 12, Foreground = Palette.SecondaryText });
        }

        foreach (var row in _form.Apps)
        {
            list.Children.Add(BuildAppRow(row));
        }

        var stack = new StackPanel { Spacing = 8 };
        stack.Children.Add(new ScrollViewer { Content = list, MaxHeight = 220 });
        stack.Children.Add(UI.HeaderText("Advanced ignore rules (wildcards, exe paths, AppUserModelIDs), one per line"));
        stack.Children.Add(_advancedRules);
        return UI.Card("Apps", stack);
    }

    private Control BuildAppRow(AppRow row)
    {
        var grid = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition(28, GridUnitType.Pixel),
                new ColumnDefinition(1, GridUnitType.Star),
                new ColumnDefinition(GridLength.Auto)
            },
            ColumnSpacing = 10
        };

        Control icon = _iconLoader(row) is { } bitmap
            ? new Image { Source = bitmap, Width = 24, Height = 24, Stretch = Stretch.Uniform }
            : new Border
            {
                Width = 24,
                Height = 24,
                CornerRadius = new CornerRadius(5),
                Background = Palette.AccentBackground,
                BorderBrush = Palette.AccentBorder,
                BorderThickness = new Thickness(1),
                Child = new TextBlock
                {
                    Text = row.ProcessName.Length > 0 ? row.ProcessName[..1].ToUpperInvariant() : "?",
                    FontSize = 12,
                    FontWeight = FontWeight.SemiBold,
                    Foreground = Palette.AccentText,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            };
        icon.VerticalAlignment = VerticalAlignment.Center;

        var text = new StackPanel { Spacing = 0, VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = row.ProcessName, FontSize = 13, FontWeight = FontWeight.SemiBold, Foreground = Palette.PrimaryText });
        var subtitle = row.WindowTitles.Count > 0
            ? string.Join("  ·  ", row.WindowTitles.Take(2)) + (row.WindowTitles.Count > 2 ? $"  (+{row.WindowTitles.Count - 2})" : "")
            : row.InSnapshot ? "No titled windows" : "Ignored, not in the latest snapshot";
        text.Children.Add(new TextBlock
        {
            Text = subtitle,
            FontSize = 11,
            Foreground = Palette.SecondaryText,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        if (row.WindowTitles.Count > 0)
        {
            ToolTip.SetTip(text, string.Join(Environment.NewLine, row.WindowTitles));
        }

        var check = new CheckBox
        {
            Content = new TextBlock { Text = "Restore", FontSize = 12, Foreground = Palette.PrimaryText, VerticalAlignment = VerticalAlignment.Center },
            IsChecked = row.Restore && !row.IgnoredByAdvancedRule,
            IsEnabled = !row.IgnoredByAdvancedRule,
            VerticalAlignment = VerticalAlignment.Center
        };
        if (row.IgnoredByAdvancedRule)
        {
            ToolTip.SetTip(check, "Ignored by an advanced rule below.");
        }

        check.IsCheckedChanged += (_, _) => row.Restore = check.IsChecked == true;

        Grid.SetColumn(icon, 0);
        Grid.SetColumn(text, 1);
        Grid.SetColumn(check, 2);
        grid.Children.Add(icon);
        grid.Children.Add(text);
        grid.Children.Add(check);
        return new Border
        {
            Padding = new Thickness(8, 5),
            CornerRadius = new CornerRadius(6),
            Background = Palette.HelpRowBackground,
            BorderBrush = Palette.HelpRowBorder,
            BorderThickness = new Thickness(1),
            Child = grid
        };
    }

    private Control BuildAdvancedCard()
    {
        var grid = new Grid
        {
            ColumnDefinitions = { new ColumnDefinition(1, GridUnitType.Star), new ColumnDefinition(1, GridUnitType.Star) },
            RowDefinitions = { new RowDefinition(GridLength.Auto), new RowDefinition(GridLength.Auto) },
            ColumnSpacing = 10,
            RowSpacing = 4
        };
        UI.AddCell(grid, UI.HeaderText("Self-restoring apps (launched once)"), 0, 0);
        UI.AddCell(grid, UI.HeaderText("Drop launch arguments (* and ? allowed)"), 0, 1);
        UI.AddCell(grid, _selfRestoring, 1, 0);
        UI.AddCell(grid, _dropArguments, 1, 1);
        return UI.Card("Advanced", grid);
    }

    private Control BuildStatusCard()
    {
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
        buttons.Children.Add(UI.SecondaryButton("Snapshot now", SnapshotNow, 110));
        buttons.Children.Add(UI.SecondaryButton("Restore now", RestoreNow, 100));
        buttons.Children.Add(UI.SecondaryButton("Open snapshots folder", _actions.OpenSnapshotsFolder, 150));

        var stack = new StackPanel { Spacing = 8 };
        stack.Children.Add(_snapshotInfo);
        stack.Children.Add(buttons);
        return UI.Card("Status", stack);
    }

    private Control BuildFooter()
    {
        var grid = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition(1, GridUnitType.Star),
                new ColumnDefinition(GridLength.Auto),
                new ColumnDefinition(GridLength.Auto)
            },
            RowDefinitions = { new RowDefinition(GridLength.Auto), new RowDefinition(GridLength.Auto) },
            ColumnSpacing = 5,
            Margin = new Thickness(10, 7, 10, 10)
        };
        UI.AddCell(grid, _statusBox, 0, 0, columnSpan: 3);
        UI.AddCell(grid, UI.PrimaryButton("Save", Save, 68), 1, 1);
        UI.AddCell(grid, UI.SecondaryButton("Cancel", Close, 70), 1, 2);
        return grid;
    }

    private void Save()
    {
        _form.Enabled = _enabled.IsChecked == true;
        _form.RestoreOnStartup = _autoRestore.IsChecked == true;
        _form.SnapshotIntervalMinutes = _interval.Text ?? "";
        _form.StartupDelaySeconds = _delay.Text ?? "";
        _form.WindowWaitSeconds = _wait.Text ?? "";
        _form.KeepSnapshots = _keep.Text ?? "";
        _form.AdvancedIgnoreRules = _advancedRules.Text ?? "";
        _form.SelfRestoring = _selfRestoring.Text ?? "";
        _form.DropArguments = _dropArguments.Text ?? "";

        if (!_form.TryBuild(out var settings, out var errors))
        {
            SetStatus(string.Join(Environment.NewLine, errors.Take(3)));
            return;
        }

        try
        {
            SessionRestoreController.WriteSettings(settings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SetStatus($"Could not save: {ex.Message}");
            return;
        }

        _actions.SettingsSaved();
        Close();
    }

    private void SnapshotNow()
    {
        _actions.SnapshotNow();
        SetStatus("Taking a snapshot...");
        DispatcherTimer.RunOnce(() =>
        {
            UpdateSnapshotInfo(_actions.LoadLatestSnapshot());
            SetStatus("Snapshot taken. App rows refresh the next time this window opens.");
        }, TimeSpan.FromSeconds(2));
    }

    private void RestoreNow()
    {
        _actions.RestoreNow();
        SetStatus("Restore started (uses the saved settings, not unsaved changes here).");
    }

    private void UpdateSnapshotInfo(SessionSnapshot? latest)
    {
        _snapshotInfo.Text = latest is null
            ? "No snapshot yet."
            : $"Last snapshot: {latest.CapturedAt.ToLocalTime():yyyy-MM-dd HH:mm} ({latest.Reason}), " +
              $"{latest.Apps.Count} app(s), {latest.Apps.Sum(static a => a.Windows.Count)} window(s).";
    }

    private void SetStatus(string text)
    {
        _statusText.Text = text;
        _statusBox.IsVisible = text.Length > 0;
    }

    private static CheckBox Check(string text, bool value) => new()
    {
        Content = new TextBlock { Text = text, FontSize = 13, Foreground = Palette.PrimaryText, VerticalAlignment = VerticalAlignment.Center },
        IsChecked = value,
        VerticalAlignment = VerticalAlignment.Center
    };

    private static TextBox MultiLine(string text, string watermark, double height) => new()
    {
        Text = text,
        PlaceholderText = watermark,
        AcceptsReturn = true,
        TextWrapping = TextWrapping.NoWrap,
        Height = height,
        FontSize = 12,
        Padding = new Thickness(6, 4)
    };
}

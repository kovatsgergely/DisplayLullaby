using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;
using Microsoft.Win32;

namespace DisplayLullaby;

internal static class DisplayLullabyTheme
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    public static ThemeVariant RequestedThemeVariant => IsDark ? ThemeVariant.Dark : ThemeVariant.Light;

    public static DisplayLullabyPalette Current => IsDark ? DisplayLullabyPalette.Dark : DisplayLullabyPalette.Light;

    private static bool IsDark
    {
        get
        {
            if (!OperatingSystem.IsWindows())
            {
                return false;
            }

            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
                return key?.GetValue("AppsUseLightTheme") is int appsUseLightTheme && appsUseLightTheme == 0;
            }
            catch
            {
                return false;
            }
        }
    }
}

internal sealed class DisplayLullabyPalette
{
    public static readonly DisplayLullabyPalette Light = new(
        WindowBackground: Brush(246, 248, 252),
        HeaderBackground: Brush(238, 245, 253),
        SurfaceBackground: Brushes.White,
        SurfaceBorder: Brush(218, 228, 240),
        PrimaryText: Brush(15, 23, 42),
        SecondaryText: Brush(55, 83, 128),
        MutedText: Brush(82, 105, 145),
        CardTitleText: Brush(32, 55, 83),
        StatusBackground: Brush(238, 245, 253),
        StatusBorder: Brush(190, 210, 235),
        StatusText: Brush(30, 64, 120),
        AccentText: Brush(29, 78, 216),
        AccentBackground: Brush(248, 251, 255),
        AccentBorder: Brush(29, 78, 216),
        PrimaryButtonBackground: Brush(37, 99, 235),
        PrimaryButtonBorder: Brush(29, 78, 216),
        PrimaryButtonText: Brushes.White,
        WarningText: Brush(120, 53, 15),
        WarningBackground: Brush(255, 251, 235),
        WarningBorder: Brush(245, 158, 11),
        HelpActionBackground: Brush(239, 246, 255),
        HelpActionBorder: Brush(191, 219, 254),
        HelpRowBackground: Brush(248, 250, 252),
        HelpRowBorder: Brush(226, 232, 240));

    public static readonly DisplayLullabyPalette Dark = new(
        WindowBackground: Brush(13, 15, 20),
        HeaderBackground: Brush(20, 24, 32),
        SurfaceBackground: Brush(19, 22, 29),
        SurfaceBorder: Brush(48, 55, 68),
        PrimaryText: Brush(241, 245, 249),
        SecondaryText: Brush(183, 196, 215),
        MutedText: Brush(148, 163, 184),
        CardTitleText: Brush(226, 232, 240),
        StatusBackground: Brush(16, 34, 52),
        StatusBorder: Brush(38, 83, 132),
        StatusText: Brush(147, 197, 253),
        AccentText: Brush(125, 177, 255),
        AccentBackground: Brush(17, 25, 39),
        AccentBorder: Brush(49, 104, 183),
        PrimaryButtonBackground: Brush(37, 99, 235),
        PrimaryButtonBorder: Brush(59, 130, 246),
        PrimaryButtonText: Brushes.White,
        WarningText: Brush(251, 191, 36),
        WarningBackground: Brush(42, 32, 17),
        WarningBorder: Brush(180, 119, 31),
        HelpActionBackground: Brush(18, 31, 49),
        HelpActionBorder: Brush(51, 96, 159),
        HelpRowBackground: Brush(22, 26, 34),
        HelpRowBorder: Brush(48, 55, 68));

    private DisplayLullabyPalette(
        IBrush WindowBackground,
        IBrush HeaderBackground,
        IBrush SurfaceBackground,
        IBrush SurfaceBorder,
        IBrush PrimaryText,
        IBrush SecondaryText,
        IBrush MutedText,
        IBrush CardTitleText,
        IBrush StatusBackground,
        IBrush StatusBorder,
        IBrush StatusText,
        IBrush AccentText,
        IBrush AccentBackground,
        IBrush AccentBorder,
        IBrush PrimaryButtonBackground,
        IBrush PrimaryButtonBorder,
        IBrush PrimaryButtonText,
        IBrush WarningText,
        IBrush WarningBackground,
        IBrush WarningBorder,
        IBrush HelpActionBackground,
        IBrush HelpActionBorder,
        IBrush HelpRowBackground,
        IBrush HelpRowBorder)
    {
        this.WindowBackground = WindowBackground;
        this.HeaderBackground = HeaderBackground;
        this.SurfaceBackground = SurfaceBackground;
        this.SurfaceBorder = SurfaceBorder;
        this.PrimaryText = PrimaryText;
        this.SecondaryText = SecondaryText;
        this.MutedText = MutedText;
        this.CardTitleText = CardTitleText;
        this.StatusBackground = StatusBackground;
        this.StatusBorder = StatusBorder;
        this.StatusText = StatusText;
        this.AccentText = AccentText;
        this.AccentBackground = AccentBackground;
        this.AccentBorder = AccentBorder;
        this.PrimaryButtonBackground = PrimaryButtonBackground;
        this.PrimaryButtonBorder = PrimaryButtonBorder;
        this.PrimaryButtonText = PrimaryButtonText;
        this.WarningText = WarningText;
        this.WarningBackground = WarningBackground;
        this.WarningBorder = WarningBorder;
        this.HelpActionBackground = HelpActionBackground;
        this.HelpActionBorder = HelpActionBorder;
        this.HelpRowBackground = HelpRowBackground;
        this.HelpRowBorder = HelpRowBorder;
    }

    public IBrush WindowBackground { get; }

    public IBrush HeaderBackground { get; }

    public IBrush SurfaceBackground { get; }

    public IBrush SurfaceBorder { get; }

    public IBrush PrimaryText { get; }

    public IBrush SecondaryText { get; }

    public IBrush MutedText { get; }

    public IBrush CardTitleText { get; }

    public IBrush StatusBackground { get; }

    public IBrush StatusBorder { get; }

    public IBrush StatusText { get; }

    public IBrush AccentText { get; }

    public IBrush AccentBackground { get; }

    public IBrush AccentBorder { get; }

    public IBrush PrimaryButtonBackground { get; }

    public IBrush PrimaryButtonBorder { get; }

    public IBrush PrimaryButtonText { get; }

    public IBrush WarningText { get; }

    public IBrush WarningBackground { get; }

    public IBrush WarningBorder { get; }

    public IBrush HelpActionBackground { get; }

    public IBrush HelpActionBorder { get; }

    public IBrush HelpRowBackground { get; }

    public IBrush HelpRowBorder { get; }

    private static SolidColorBrush Brush(byte r, byte g, byte b) => new(Color.FromRgb(r, g, b));
}

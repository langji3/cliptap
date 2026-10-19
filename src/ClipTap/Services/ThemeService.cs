using System.Security;
using System.Windows.Media;
using ClipTap.Core;
using Microsoft.Win32;
using System.Windows.Interop;

namespace ClipTap.Services;

internal sealed class ThemeService : IDisposable
{
    private AppearanceMode _mode;
    private AccentPalette _accent;
    public event Action? Changed;
    public ThemeService(AppearanceMode mode, AccentPalette accent)
    {
        SetMode(mode, accent);
        SystemEvents.UserPreferenceChanged += OnPreferenceChanged;
    }
    public void SetMode(AppearanceMode mode, AccentPalette accent)
    { _mode = mode; _accent = accent; ApplyTheme(mode, accent); Changed?.Invoke(); }

    private void OnPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.HasShutdownStarted) return;
        dispatcher.BeginInvoke(() => { if (_mode == AppearanceMode.System) { ApplyTheme(_mode, _accent); Changed?.Invoke(); } });
    }

    /// <summary>
    /// Rewrites the neutral surface palette plus the accent-derived resources. Window backgrounds stay
    /// neutral; chroma only reaches primary buttons, keyboard focus and the light selection tint.
    /// </summary>
    internal static void ApplyTheme(AppearanceMode mode, AccentPalette accent = AccentPalette.Blue)
    {
        var dark = mode == AppearanceMode.Dark || mode == AppearanceMode.System && SystemUsesDarkTheme();
        // One hue per accent; light/dark pick the member that stays readable as text and as a filled surface.
        var accentColor = Parse(accent switch
        {
            AccentPalette.Purple => dark ? "#C3A6F0" : "#6F42B0",
            AccentPalette.Green => dark ? "#7FD3AC" : "#1F6C52",
            _ => dark ? "#8FB6FF" : "#2C5FC4"
        });
        var surface = Parse(dark ? "#282A2E" : "#FFFFFF");
        var colors = new Dictionary<string, Color>
        {
            ["Ink"] = Parse(dark ? "#F2F3F5" : "#1F2124"),
            ["Muted"] = Parse(dark ? "#A4A8B0" : "#6B7078"),
            ["Accent"] = accentColor,
            // Filled accent surfaces pick whichever of near-black/near-white clears 4.5:1 against the accent.
            ["OnAccent"] = RelativeLuminance(accentColor) > 0.45 ? Parse("#15203A") : Colors.White,
            ["OnDanger"] = Colors.White,
            ["Page"] = Parse(dark ? "#202124" : "#FAFAFA"),
            ["PreviewSurface"] = Parse(dark ? "#202124" : "#FFFFFF"),
            ["Field"] = surface,
            ["FieldBackground"] = surface,
            ["ButtonSurface"] = dark ? Parse("#3A3E45") : Parse("#ECEDEF"),
            // Unchecked switches need their own track colour: the surface tone disappears on the page.
            ["SwitchTrack"] = dark ? Parse("#42464D") : Parse("#D2D5DA"),
            ["SwitchKnob"] = dark ? Parse("#B9BEC6") : Parse("#7C838C"),
            ["Chrome"] = dark ? Parse("#26282C") : Parse("#F1F2F4"),
            ["Hover"] = dark ? Parse("#303338") : Parse("#F0F1F3"),
            ["Selected"] = Mix(surface, accentColor, dark ? 0.16 : 0.10),
            ["Line"] = Parse(dark ? "#36383D" : "#E7E8EB"),
            ["CardLine"] = Parse(dark ? "#36383D" : "#E7E8EB"),
            ["Danger"] = Parse(dark ? "#FF9A93" : "#B3261E"),
            ["DangerSurface"] = dark ? Parse("#4A2A28") : Parse("#FDECEA"),
            ["EmptyIcon"] = Parse(dark ? "#5A5F68" : "#B4B9C1"),
            ["BrandColor"] = accentColor,
            ["LogoInk"] = Colors.White,
            ["Success"] = Parse(dark ? "#7FD3AC" : "#1F6C52")
        };
        var resources = System.Windows.Application.Current.Resources;
        foreach (var (key, value) in colors)
        {
            var brush = new SolidColorBrush(value);
            brush.Freeze();
            resources[key] = brush;
        }
        foreach (System.Windows.Window window in System.Windows.Application.Current.Windows) ApplyTitleBar(window);
    }

    private static Color Parse(string hex) => (Color)ColorConverter.ConvertFromString(hex);

    private static Color Mix(Color from, Color to, double amount) => Color.FromRgb(
        (byte)Math.Round(from.R + (to.R - from.R) * amount),
        (byte)Math.Round(from.G + (to.G - from.G) * amount),
        (byte)Math.Round(from.B + (to.B - from.B) * amount));

    /// <summary>WCAG relative luminance; decides whether a filled accent surface needs light or dark ink.</summary>
    private static double RelativeLuminance(Color color)
    {
        static double Channel(byte value)
        {
            var channel = value / 255.0;
            return channel <= 0.03928 ? channel / 12.92 : Math.Pow((channel + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Channel(color.R) + 0.7152 * Channel(color.G) + 0.0722 * Channel(color.B);
    }

    internal static void ApplyTitleBar(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == nint.Zero) return;
        var background = (SolidColorBrush)System.Windows.Application.Current.Resources["Page"];
        var dark = background.Color.R < 128 ? 1 : 0;
        NativeMethods.DwmSetWindowAttribute(handle, 20, ref dark, sizeof(int));
    }

    private static bool SystemUsesDarkTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException) { return false; }
    }
    public void Dispose() => SystemEvents.UserPreferenceChanged -= OnPreferenceChanged;
}

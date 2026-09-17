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

    internal static void ApplyTheme(AppearanceMode mode, AccentPalette accent = AccentPalette.Blue)
    {
        var dark = mode == AppearanceMode.Dark || mode == AppearanceMode.System && SystemUsesDarkTheme();
        var palette = accent switch
        {
            AccentPalette.Blue => new[] { "#326CC4", "#80B2FF", "#E9F0FF", "#263C58", "#BECEEF", "#496B9B" },
            AccentPalette.Purple => new[] { "#8057B6", "#C0A3ED", "#F1EBF9", "#3D3151", "#D5C3EB", "#796092" },
            _ => new[] { "#28785D", "#77CDA6", "#E7F1EA", "#2B4638", "#C4DCCD", "#4B755D" }
        };
        var colors = new Dictionary<string, string>
        {
            ["Ink"] = dark ? "#E9EDF5" : "#253044",
            ["Muted"] = dark ? "#A6AFC0" : "#69778D",
            ["Accent"] = palette[dark ? 1 : 0],
            ["OnAccent"] = dark ? "#182127" : "#FFFFFF",
            ["Page"] = dark ? "#1D2228" : "#FAFCFA",
            ["Chrome"] = dark ? "#242B37" : "#E5EBF3",
            ["Field"] = dark ? "#252D35" : "#FFFFFF",
            ["TabSurface"] = dark ? "#39434C" : "#FFFFFF",
            ["ButtonSurface"] = dark ? "#354052" : "#E7EDF5",
            ["Hover"] = palette[dark ? 3 : 2],
            ["Selected"] = palette[dark ? 3 : 2],
            ["SelectedBorder"] = palette[dark ? 5 : 4],
            ["Line"] = dark ? "#3D4854" : "#D9E3DD",
            ["EmptyIcon"] = dark ? "#688D77" : "#9EB9A8",
            ["Danger"] = dark ? "#F0A39D" : "#AB403B"
        };
        var resources = System.Windows.Application.Current.Resources;
        foreach (var (key, value) in colors)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(value));
            brush.Freeze(); resources[key] = brush;
        }
        // Keep chroma in highlights; neutral surfaces carry the text and visual hierarchy.
        var hue = (Color)ColorConverter.ConvertFromString(palette[0]);
        var glow = (Color)ColorConverter.ConvertFromString(palette[1]);
        Color Neutral(string hex) => (Color)ColorConverter.ConvertFromString(hex);
        Color Mix(Color a, Color b, double amount) => Color.FromRgb(
            (byte)(a.R + (b.R - a.R) * amount), (byte)(a.G + (b.G - a.G) * amount), (byte)(a.B + (b.B - a.B) * amount));
        void Gradient(string key, Color start, Color end)
        {
            var brush = new LinearGradientBrush(start, end, new Point(0, 0), new Point(1, 1));
            brush.Freeze(); resources[key] = brush;
        }
        Gradient("PanelGradient", Mix(Neutral(dark ? "#20242D" : "#F4F7FB"), glow, dark ? .09 : .12), Neutral(dark ? "#171B23" : "#EEF1F6"));
        Gradient("CardGradient", Neutral(dark ? "#2D333F" : "#FFFFFF"), Mix(Neutral(dark ? "#252A34" : "#FAFBFD"), glow, .035));
        Gradient("CardSelectedGradient", Mix(Neutral(dark ? "#303848" : "#FFFFFF"), glow, .13), Mix(Neutral(dark ? "#252C39" : "#F7F9FC"), glow, .07));
        Gradient("CardHoverGradient", Mix(Neutral(dark ? "#303848" : "#FFFFFF"), glow, .24), Mix(Neutral(dark ? "#252C39" : "#F7F9FC"), glow, .15));
        var brand = accent == AccentPalette.Blue ? Neutral("#6CA5E8") : hue;
        resources["BrandColor"] = new SolidColorBrush(brand);
        if (accent == AccentPalette.Blue)
            Gradient("BrandGradient", Mix(brand, Neutral("#FFFFFF"), .18), brand);
        else
            Gradient("BrandGradient", Mix(hue, glow, .52), Mix(hue, Neutral("#111D3D"), .24));
        Gradient("ActionGradient", Mix((Color)ColorConverter.ConvertFromString(palette[dark ? 1 : 0]), Neutral("#FFFFFF"), .12), (Color)ColorConverter.ConvertFromString(palette[dark ? 1 : 0]));
        Gradient("TabGradient", Neutral(dark ? "#465162" : "#FFFFFF"), Neutral(dark ? "#353E4D" : "#F5F8FC"));
        resources["CardLine"] = new SolidColorBrush(Neutral(dark ? "#414958" : "#E0E6EE"));
        resources["LogoInk"] = Brushes.White;
        foreach (Window window in System.Windows.Application.Current.Windows) ApplyTitleBar(window);
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

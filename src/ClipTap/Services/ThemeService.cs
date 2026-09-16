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

    internal static void ApplyTheme(AppearanceMode mode, AccentPalette accent = AccentPalette.Green)
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
            ["Ink"] = dark ? "#E6EEE9" : "#202F2A",
            ["Muted"] = dark ? "#A0B0A7" : "#718078",
            ["Accent"] = palette[dark ? 1 : 0],
            ["OnAccent"] = dark ? "#182127" : "#FFFFFF",
            ["Page"] = dark ? "#1D2228" : "#FAFCFA",
            ["Chrome"] = dark ? "#272F38" : "#ECF1ED",
            ["Field"] = dark ? "#252D35" : "#FFFFFF",
            ["TabSurface"] = dark ? "#39434C" : "#FFFFFF",
            ["ButtonSurface"] = dark ? "#34404B" : "#EEF3EF",
            ["Hover"] = dark ? "#2B3640" : "#F0F4F1",
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

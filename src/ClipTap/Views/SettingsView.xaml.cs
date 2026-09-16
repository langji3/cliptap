using System.Security;
using System.Windows.Controls;
using ClipTap.Core;
using ClipTap.Services;

namespace ClipTap.Views;

internal sealed record ThemeChoice(AppearanceMode Mode, string Label)
{
    public override string ToString() => Label;
}

public partial class SettingsView : UserControl
{
    private readonly App _app;
    private readonly AppearanceMode _originalTheme;
    private readonly AccentPalette _originalAccent;
    private AccentPalette _accent;
    private bool _ready, _saved;
    internal event Action? Finished;
    internal bool IsDropDownOpen => ThemeInput.IsDropDownOpen;

    public SettingsView(App app)
    {
        _app = app;
        _originalTheme = app.Library.State.Settings.Theme;
        _originalAccent = _accent = app.Library.State.Settings.Accent;
        InitializeComponent();
        ThemeInput.ItemsSource = new[] { new ThemeChoice(AppearanceMode.Light, "浅色"), new ThemeChoice(AppearanceMode.Dark, "深色"), new ThemeChoice(AppearanceMode.System, "跟随系统") };
        ThemeInput.SelectedValue = _originalTheme;
        (FindName(_accent + "Accent") as RadioButton)!.IsChecked = true;
        try { StartupInput.IsChecked = StartupService.IsEnabled(); }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        { StartupInput.IsEnabled = false; ErrorLabel.Text = "无法读取开机启动设置"; }
        _ready = true;
    }

    internal void Discard()
    {
        Confirmation.Dismiss();
        if (!_saved) _app.PreviewTheme(_originalTheme, _originalAccent);
    }
    internal bool DismissConfirmation()
    { if (!Confirmation.IsOpen) return false; Confirmation.Dismiss(); return true; }
    private void OnThemeChanged(object sender, SelectionChangedEventArgs e) => Preview();
    private void OnAccentChanged(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string tag }) _accent = Enum.Parse<AccentPalette>(tag);
        Preview();
    }
    private void Preview()
    { if (_ready && ThemeInput.SelectedValue is AppearanceMode mode) _app.PreviewTheme(mode, _accent); }
    private void OnSave(object sender, RoutedEventArgs e) => Save();

    internal void Save()
    {
        string? originalCommand = null;
        var startupChanged = false;
        try
        {
            if (StartupInput.IsEnabled)
            {
                originalCommand = StartupService.GetCommand();
                if ((originalCommand is not null) != (StartupInput.IsChecked == true))
                { StartupService.SetEnabled(StartupInput.IsChecked == true); startupChanged = true; }
            }
            var settings = new AppSettings
            {
                HistoryLimit = _app.Library.State.Settings.HistoryLimit,
                CapturePaused = _app.Library.State.Settings.CapturePaused,
                Theme = ThemeInput.SelectedValue is AppearanceMode mode ? mode : AppearanceMode.Light,
                Accent = _accent
            };
            if (_app.TrySaveSettings(settings)) { _saved = true; Finished?.Invoke(); }
            else ErrorLabel.Text = "保存失败，请重试";
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException or InvalidOperationException)
        { ErrorLabel.Text = ex.Message; }
        finally
        {
            if (!_saved && startupChanged)
            {
                try { StartupService.RestoreCommand(originalCommand); }
                catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
                { ErrorLabel.Text += " · 开机启动恢复失败"; }
            }
        }
    }

    private void OnClear(object sender, RoutedEventArgs e)
    {
        Confirmation.Ask("清空 Windows 剪贴板历史？\nWin+V 中的固定项会保留。", "清空", async () =>
        {
            ClearButton.IsEnabled = false;
            try { ErrorLabel.Text = await _app.ClearSystemHistoryAsync() ? "已清空" : "清空失败，请重试"; }
            finally { ClearButton.IsEnabled = true; }
        });
    }
    private void OnSystemSettings(object sender, RoutedEventArgs e)
    { if (!_app.OpenSystemClipboardSettings()) ErrorLabel.Text = "无法打开 Windows 设置"; }
}

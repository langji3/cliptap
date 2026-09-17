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
    private readonly UpdateService _updates = new();
    private Uri? _releaseUrl;
    private CancellationTokenSource? _updateCancellation;
    internal event Action? Finished;
    internal bool IsDropDownOpen => ThemeInput.IsDropDownOpen;

    public SettingsView(App app)
    {
        _app = app;
        _originalTheme = app.Library.State.Settings.Theme;
        _originalAccent = _accent = app.Library.State.Settings.Accent;
        InitializeComponent();
        UpdatePanel.Visibility = UpdateService.IsInstalled ? Visibility.Visible : Visibility.Collapsed;
        VersionLabel.Text = "ClipTap " + UpdateService.VersionText;
        Unloaded += (_, _) => _updateCancellation?.Cancel();
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

    private async void OnCheckUpdate(object sender, RoutedEventArgs e)
    {
        if (!UpdateService.IsInstalled || _updateCancellation is not null) return;
        using var cancellation = new CancellationTokenSource();
        _updateCancellation = cancellation;
        CheckUpdateButton.IsEnabled = false;
        OpenReleaseButton.Visibility = Visibility.Collapsed;
        _releaseUrl = null;
        UpdateStatusLabel.Text = "正在检查…";
        try
        {
            var result = await _updates.CheckAsync(cancellation.Token);
            if (cancellation.IsCancellationRequested) { UpdateStatusLabel.Text = "检查已取消，可重试"; return; }
            _releaseUrl = result.ReleaseUrl;
            UpdateStatusLabel.Text = result.Status switch
            {
                UpdateStatus.Current => "已是最新版本",
                UpdateStatus.Available => "发现新版本 " + result.Version,
                UpdateStatus.NoRelease => "暂未发布正式版本",
                _ => "暂时无法检查更新，请稍后重试"
            };
            OpenReleaseButton.Visibility = _releaseUrl is null ? Visibility.Collapsed : Visibility.Visible;
        }
        finally { _updateCancellation = null; CheckUpdateButton.IsEnabled = true; }
    }

    private void OnOpenRelease(object sender, RoutedEventArgs e)
    {
        if (_releaseUrl is null) return;
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(_releaseUrl.AbsoluteUri) { UseShellExecute = true }); }
        catch (System.ComponentModel.Win32Exception) { UpdateStatusLabel.Text = "无法打开浏览器，请稍后重试"; }
    }
}

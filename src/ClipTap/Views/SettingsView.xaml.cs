using System.Security;
using System.Windows.Controls;
using System.Windows.Input;
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
    private WakeHotkey _wakeHotkey;
    internal bool IsRecordingHotkey { get; private set; }
    private bool _ready, _saved;
    private readonly UpdateService _updates = new();
    private Uri? _releaseUrl;
    private UpdateResult? _availableUpdate;
    private CancellationTokenSource? _updateCancellation;
    internal event Action? Finished;
    internal bool IsDropDownOpen => ThemeInput.IsDropDownOpen;

    public SettingsView(App app)
    {
        _app = app;
        _originalTheme = app.Library.State.Settings.Theme;
        _originalAccent = _accent = app.Library.State.Settings.Accent;
        _wakeHotkey = app.Library.State.Settings.WakeHotkey;
        InitializeComponent();
        HotkeyButton.Content = _wakeHotkey.ToString();
        UpdatePanel.Visibility = UpdateService.IsInstalled ? Visibility.Visible : Visibility.Collapsed;
        VersionLabel.Text = "ClipTap " + UpdateService.VersionText;
        Unloaded += (_, _) => { _updateCancellation?.Cancel(); StopRecording(); };
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
        StopRecording();
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
        StopRecording();
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
                Accent = _accent,
                WakeHotkey = _wakeHotkey
            };
            if (_app.TrySaveSettings(settings, out var error)) { _saved = true; Finished?.Invoke(); }
            else ErrorLabel.Text = error;
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

    private void OnRecordHotkey(object sender, RoutedEventArgs e)
    {
        if (IsRecordingHotkey) { StopRecording(); return; }
        IsRecordingHotkey = true;
        HotkeyButton.Focus();
        HotkeyButton.Content = "请按快捷键…";
        HotkeyHint.Text = "按 Esc 取消录入；需包含 Ctrl 或 Alt，可搭配 Shift。";
        _app.RecordHotkey(shortcut => { _wakeHotkey = shortcut; StopRecording(); });
    }

    internal void CaptureHotkey(KeyEventArgs e)
    {
        if (!IsRecordingHotkey) return;
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Escape) { StopRecording(); return; }
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin) return;
        var modifiers = Keyboard.Modifiers;
        var shortcut = new WakeHotkey((uint)modifiers, (uint)KeyInterop.VirtualKeyFromKey(key));
        if (!shortcut.IsValid)
        { HotkeyHint.Text = "请使用 Ctrl/Alt 加字母、数字、空格或 F1–F11，可加 Shift。"; return; }
        _wakeHotkey = shortcut;
        StopRecording();
    }

    internal void StopRecording()
    {
        if (IsRecordingHotkey) _app.RecordHotkey(null);
        IsRecordingHotkey = false;
        HotkeyButton.Content = _wakeHotkey.ToString();
        HotkeyHint.Text = "点击录入，保存后生效。支持 Ctrl/Alt 加字母、数字、空格或 F1–F11，可加 Shift。";
    }
    private void OnHotkeyFocusLost(object sender, KeyboardFocusChangedEventArgs e) => StopRecording();
    private void OnResetHotkey(object sender, RoutedEventArgs e)
    { _wakeHotkey = WakeHotkey.Default; StopRecording(); }

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
        InstallUpdateButton.Visibility = Visibility.Collapsed;
        _availableUpdate = null;
        _releaseUrl = null;
        UpdateStatusLabel.Text = "正在检查…";
        try
        {
            var result = await _updates.CheckAsync(cancellation.Token);
            if (cancellation.IsCancellationRequested) { UpdateStatusLabel.Text = "检查已取消，可重试"; return; }
            _releaseUrl = result.ReleaseUrl;
            _availableUpdate = result.DownloadUrl is null ? null : result;
            UpdateStatusLabel.Text = result.Status switch
            {
                UpdateStatus.Current => "已是最新版本",
                UpdateStatus.Available => "发现新版本 " + result.Version,
                UpdateStatus.NoRelease => "暂未发布正式版本",
                _ => "暂时无法检查更新，请稍后重试"
            };
            OpenReleaseButton.Visibility = _releaseUrl is null ? Visibility.Collapsed : Visibility.Visible;
            InstallUpdateButton.Visibility = _availableUpdate is null ? Visibility.Collapsed : Visibility.Visible;
        }
        finally { _updateCancellation = null; CheckUpdateButton.IsEnabled = true; }
    }

    private async void OnInstallUpdate(object sender, RoutedEventArgs e)
    {
        if (_availableUpdate is not { } update || _updateCancellation is not null) return;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(15));
        _updateCancellation = cancellation;
        CheckUpdateButton.IsEnabled = InstallUpdateButton.IsEnabled = false;
        CancelUpdateButton.Visibility = Visibility.Visible;
        string? installer = null;
        try
        {
            UpdateStatusLabel.Text = "正在下载更新…";
            installer = await UpdateInstaller.DownloadAsync(update, new Progress<int>(percent =>
            {
                if (!cancellation.IsCancellationRequested) UpdateStatusLabel.Text = $"正在下载更新 {percent}%";
            }), cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (!_app.SaveNow()) { UpdateStatusLabel.Text = "保存失败，未开始安装，请重试"; return; }
            UpdateStatusLabel.Text = "校验通过，正在更新，完成后会自动回到托盘…";
            CancelUpdateButton.Visibility = Visibility.Collapsed;
            await UpdateInstaller.StartAsync(installer, update.Sha256!);
            cancellation.Token.ThrowIfCancellationRequested();
            if (!_app.QuitForUpdate(installer)) UpdateStatusLabel.Text = "保存失败，未开始安装，请重试";
        }
        catch (OperationCanceledException) { UpdateStatusLabel.Text = "更新已取消或下载超时，可重试"; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Net.Http.HttpRequestException or
            System.ComponentModel.Win32Exception or InvalidOperationException)
        { UpdateStatusLabel.Text = "更新未完成，可重试或前往下载页手动升级"; }
        finally
        {
            if (!_app.IsQuitting && installer is not null)
            {
                try { File.Delete(installer); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
            _updateCancellation = null;
            CheckUpdateButton.IsEnabled = InstallUpdateButton.IsEnabled = true;
            CancelUpdateButton.Visibility = Visibility.Collapsed;
        }
    }

    private void OnCancelUpdate(object sender, RoutedEventArgs e) => _updateCancellation?.Cancel();

    private void OnOpenRelease(object sender, RoutedEventArgs e)
    {
        if (_releaseUrl is null) return;
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(_releaseUrl.AbsoluteUri) { UseShellExecute = true }); }
        catch (System.ComponentModel.Win32Exception) { UpdateStatusLabel.Text = "无法打开浏览器，请稍后重试"; }
    }
}

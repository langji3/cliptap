using System.Security;
using ClipTap.Services;
using ClipTap.Core;
using System.Windows.Controls;

namespace ClipTap;

public partial class SettingsWindow : Window
{
    private readonly App _app;
    private readonly AppearanceMode _originalTheme;
    private readonly AccentPalette _originalAccent;
    private bool _ready, _saved;
    public SettingsWindow(App app)
    {
        _app = app;
        _originalTheme = app.Library.State.Settings.Theme;
        _originalAccent = app.Library.State.Settings.Accent;
        InitializeComponent();
        SourceInitialized += (_, _) => ThemeService.ApplyTitleBar(this);
        ThemeInput.ItemsSource = new[] { "跟随系统", "浅色", "深色" };
        ThemeInput.SelectedIndex = (int)_originalTheme;
        AccentInput.ItemsSource = new[] { "绿 · Green", "蓝 · Blue", "紫 · Purple" };
        AccentInput.SelectedIndex = (int)_originalAccent;
        LimitInput.ItemsSource = new[] { 20, 50, 100, 200, 500 };
        LimitInput.SelectedItem = app.Library.State.Settings.HistoryLimit;
        if (LimitInput.SelectedIndex < 0) LimitInput.SelectedItem = 100;
        PauseInput.IsChecked = app.Library.State.Settings.CapturePaused;
        try { StartupInput.IsChecked = StartupService.IsEnabled(); }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        { StartupInput.IsEnabled = false; ErrorLabel.Text = "无法读取开机启动设置。"; }
        _ready = true;
        Closed += (_, _) =>
        {
            if (!_saved)
            {
                _app.Library.State.Settings.Theme = _originalTheme;
                _app.Library.State.Settings.Accent = _originalAccent;
                _app.PreviewTheme(_originalTheme, _originalAccent);
            }
        };
    }
    private void OnThemeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_ready && ThemeInput.SelectedIndex >= 0 && AccentInput.SelectedIndex >= 0)
            _app.PreviewTheme((AppearanceMode)ThemeInput.SelectedIndex, (AccentPalette)AccentInput.SelectedIndex);
    }
    private void OnSave(object sender, RoutedEventArgs e)
    {
        string? originalCommand = null;
        var startupChanged = false;
        try
        {
            if (StartupInput.IsEnabled)
            {
                originalCommand = StartupService.GetCommand();
                if ((originalCommand is not null) != (StartupInput.IsChecked == true))
                {
                    StartupService.SetEnabled(StartupInput.IsChecked == true);
                    startupChanged = true;
                }
            }
            var settings = new AppSettings
            {
                HistoryLimit = (int)(LimitInput.SelectedItem ?? 100),
                CapturePaused = PauseInput.IsChecked == true,
                Theme = (AppearanceMode)Math.Max(0, ThemeInput.SelectedIndex),
                Accent = (AccentPalette)Math.Max(0, AccentInput.SelectedIndex)
            };
            if (_app.TrySaveSettings(settings)) { _saved = true; DialogResult = true; }
            else ErrorLabel.Text = "保存失败，设置和历史记录未更改。请检查磁盘空间或文件权限。";
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException or InvalidOperationException)
        { ErrorLabel.Text = ex.Message; }
        finally
        {
            if (!_saved && startupChanged)
            {
                try { StartupService.RestoreCommand(originalCommand); }
                catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
                { ErrorLabel.Text += " 开机启动恢复失败，请在 Windows 启动应用设置中检查 ClipTap。"; }
            }
        }
    }
    private void OnClear(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "清空全部剪贴板历史？快捷片段会保留。", "ClipTap", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _app.Library.State.History.Clear();
        _app.Changed();
        ErrorLabel.Text = _app.SaveNow() ? "历史记录已清空。" : "清空后的状态尚未保存，请重试。";
    }
}

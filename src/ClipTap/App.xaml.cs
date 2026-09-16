using System.ComponentModel;
using System.Drawing;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows.Threading;
using ClipTap.Core;
using ClipTap.Services;
using ClipTap.Branding;
using Forms = System.Windows.Forms;

namespace ClipTap;

public partial class App : System.Windows.Application
{
    private Mutex? _instance;
    private Forms.NotifyIcon? _tray;
    private DispatcherTimer? _saveTimer;
    private EncryptedStore? _store;
    private ClipboardService? _clipboard;
    private ThemeService? _theme;
    private DesktopEvents? _events;
    private bool _ownsMutex;
    private bool _dirty;
    private MainWindow? _panel;
    internal Library Library { get; set; } = null!;
    internal ClipboardService ClipboardService => _clipboard!;
    internal bool IsQuitting { get; private set; }
    internal bool HotkeyAvailable => _events?.HotkeyAvailable ?? true;
    private MainWindow Panel
    {
        get
        {
            if (_panel is null) { _panel = new MainWindow(this); MainWindow = _panel; }
            return _panel;
        }
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _instance = new Mutex(true, @"Local\ClipTap." + Environment.UserName, out _ownsMutex);
        if (!_ownsMutex)
        {
            MessageBox.Show("ClipTap 已在运行。按 Ctrl+Alt+V 唤起，或点击托盘图标。", "ClipTap");
            Shutdown(); return;
        }
        var dataDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClipTap");
        _store = new EncryptedStore(dataDirectory);
        try { Library = new Library(_store.Load()); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or CryptographicException or JsonException)
        {
            MessageBox.Show($"无法读取本地数据，原文件已保留，程序将退出以避免覆盖。\n\n{_store.FilePath}\n\n{ex.Message}", "ClipTap", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1); return;
        }
        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
        _saveTimer.Tick += (_, _) => SaveNow();
        _theme = new ThemeService(Library.State.Settings.Theme, Library.State.Settings.Accent);
        _theme.Changed += RefreshTrayIcon;
        // Persist normalization (including sensitive-history removal) even if the user makes no edits.
        if (File.Exists(_store.FilePath)) Changed();
        _clipboard = new ClipboardService();
        _clipboard.TextCaptured += text =>
        {
            if (Library.Capture(text, DateTimeOffset.Now)) { Changed(); _panel?.RefreshHistory(); }
        };
        CreateTray();
        try { _events = new DesktopEvents(() => Panel.ToggleFromHotkey(), _clipboard.OnChanged); }
        catch (Win32Exception ex)
        {
            MessageBox.Show("无法监听剪贴板：" + ex.Message, "ClipTap", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1); return;
        }
        if (!HotkeyAvailable) Notify("Ctrl+Alt+V 已被占用。请先通过托盘打开 ClipTap，或关闭占用此快捷键的应用。");
        if (!e.Args.Contains("--background")) Panel.OpenPanel(captureTarget: false);
    }

    private void CreateTray()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("打开 ClipTap    Ctrl+Alt+V", null, (_, _) => Panel.OpenPanel(captureTarget: false));
        menu.Items.Add("暂停记录", null, (_, _) =>
        {
            Library.State.Settings.CapturePaused = !Library.State.Settings.CapturePaused;
            Changed(); _panel?.RefreshRows();
        });
        menu.Items.Add("设置", null, (_, _) => { Panel.OpenPanel(captureTarget: false); Panel.ShowSettings(); });
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("退出 ClipTap", null, (_, _) => Quit());
        menu.Opening += (_, _) => ((Forms.ToolStripMenuItem)menu.Items[1]).Checked = Library.State.Settings.CapturePaused;
        _tray = new Forms.NotifyIcon { Text = "ClipTap · Ctrl+Alt+V", Icon = CreateIcon(), Visible = true, ContextMenuStrip = menu };
        _tray.MouseClick += (_, args) => { if (args.Button == Forms.MouseButtons.Left) Panel.OpenPanel(captureTarget: false); };
    }

    private static Icon CreateIcon()
    {
        var accent = ((System.Windows.Media.SolidColorBrush)Current.Resources["Accent"]).Color;
        using var bitmap = IconArtwork.Render(32, Color.FromArgb(accent.R, accent.G, accent.B));
        var handle = bitmap.GetHicon();
        try { using var original = Icon.FromHandle(handle); return (Icon)original.Clone(); }
        finally { DestroyIcon(handle); }
    }
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool DestroyIcon(nint icon);

    private void RefreshTrayIcon()
    {
        if (_tray is null) return;
        var previous = _tray.Icon;
        _tray.Icon = CreateIcon();
        previous?.Dispose();
    }

    internal void Notify(string message) => _tray?.ShowBalloonTip(3000, "ClipTap", message, Forms.ToolTipIcon.Info);
    internal void PreviewTheme(AppearanceMode mode, AccentPalette accent)
    {
        if (_theme is null) ThemeService.ApplyTheme(mode, accent);
        else _theme.SetMode(mode, accent);
    }

    internal void Changed()
    {
        _dirty = true;
        _saveTimer!.Stop();
        _saveTimer.Start();
    }

    internal bool SaveNow()
    {
        _saveTimer?.Stop();
        if (!_dirty || _store is null) return true;
        try { _store.Save(Library.State); _dirty = false; return true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
        {
            Notify("本地保存失败，当前更改仍在内存中。请检查磁盘空间和权限后重试。");
            return false;
        }
    }

    internal bool TrySaveSettings(AppSettings settings)
    {
        var candidate = Library.WithSettings(settings);
        try
        {
            _store!.Save(candidate.State);
            Library = candidate;
            _dirty = false;
            _saveTimer?.Stop();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
        { return false; }
    }

    internal void Quit()
    {
        if (!SaveNow() && MessageBox.Show("有更改未能保存。仍要退出吗？", "ClipTap", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        IsQuitting = true;
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        SaveNow();
        _saveTimer?.Stop();
        _events?.Dispose();
        _clipboard?.Dispose();
        _theme?.Dispose();
        if (_tray is not null) { _tray.Visible = false; _tray.Icon?.Dispose(); _tray.ContextMenuStrip?.Dispose(); _tray.Dispose(); }
        if (_ownsMutex) _instance?.ReleaseMutex();
        _instance?.Dispose();
        base.OnExit(e);
    }
}

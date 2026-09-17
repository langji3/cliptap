using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using ClipTap.Core;
using ClipTap.Services;
using ClipTap.Views;

namespace ClipTap;

internal sealed record EntryRow(Guid Id, string Title, string Detail, bool Sensitive, bool Pinned, bool IsImage = false, ImageSource? Thumbnail = null, bool ActionsOpen = false)
{
    public override string ToString() => Title;
}

public partial class MainWindow : Window
{
    private readonly App _app;
    private nint _handle;
    private int _tab;
    private bool _busy;
    private Guid? _menuId;
    private PasteTarget? _target;
    private SnippetView? _editor;
    private SettingsView? _settings;
    private bool IsList => PageHost.Content is null;

    public MainWindow(App app) { _app = app; InitializeComponent(); RefreshRows(); }

    internal void ToggleFromHotkey()
    {
        if (_busy) return;
        if (IsVisible) Hide(); else OpenPanel(captureTarget: true);
    }

    internal void OpenPanel(bool captureTarget)
    {
        if (_busy) return;
        var entering = !IsVisible;
        Confirmation.Dismiss();
        _menuId = null;
        _editor?.DismissConfirmation();
        _settings?.DismissConfirmation();
        _target = captureTarget ? PasteService.CaptureTarget() : null;
        if (_handle == nint.Zero)
        {
            _handle = new WindowInteropHelper(this).EnsureHandle();
            var corner = 2;
            NativeMethods.DwmSetWindowAttribute(_handle, 33, ref corner, sizeof(int));
        }
        if (IsList) { _tab = 0; RefreshRows(); }
        PlaceNearTarget();
        // Prepare before Show so there is no fully visible frame before the entrance starts.
        PanelSurface.BeginAnimation(OpacityProperty, null);
        PanelSurface.Opacity = entering && _app.AnimatePanel ? 0 : 1;
        Show(); Activate();
        if (entering && _app.AnimatePanel)
        {
            var duration = TimeSpan.FromMilliseconds(240);
            var easing = new QuarticEase { EasingMode = EasingMode.EaseOut };
            PanelSurface.Opacity = 1;
            PanelSurface.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)) { EasingFunction = easing });
            EntranceOffset.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(4, 0, duration) { EasingFunction = easing });
            EntranceScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.975, 1, duration) { EasingFunction = easing });
            EntranceScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.975, 1, duration) { EasingFunction = easing });
        }
        else
        {
            PanelSurface.BeginAnimation(OpacityProperty, null); PanelSurface.Opacity = 1;
            EntranceOffset.BeginAnimation(TranslateTransform.YProperty, null); EntranceOffset.Y = 0;
            EntranceScale.BeginAnimation(ScaleTransform.ScaleXProperty, null); EntranceScale.ScaleX = 1;
            EntranceScale.BeginAnimation(ScaleTransform.ScaleYProperty, null); EntranceScale.ScaleY = 1;
        }
        _ = LoadHistoryAsync();
        if (IsList) Entries.Focus(); else PageHost.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
    }

    private void PlaceNearTarget()
    {
        var anchor = _target?.Window ?? NativeMethods.GetForegroundWindow();
        var monitor = NativeMethods.MonitorFromWindow(anchor, 2);
        var info = new NativeMethods.MonitorInfo { Size = Marshal.SizeOf<NativeMethods.MonitorInfo>() };
        if (!NativeMethods.GetMonitorInfo(monitor, ref info)) return;
        var dpi = NativeMethods.GetDpiForWindow(anchor);
        var scale = dpi > 0 ? dpi / 96.0 : 1;
        Left = info.Work.Left / scale + Math.Max(0, ((info.Work.Right - info.Work.Left) / scale - Width) / 2);
        Top = info.Work.Top / scale + Math.Max(0, ((info.Work.Bottom - info.Work.Top) / scale - Height) / 3);
    }

    private async Task LoadHistoryAsync() { await _app.RefreshSystemHistoryAsync(); RefreshHistory(); }

    internal void RefreshHistory() { if (IsList && _tab == 0 && IsVisible) RefreshRows(preserveSelection: true); }
    internal void RefreshRows(bool preserveSelection = false)
    {
        var selected = preserveSelection ? (Entries.SelectedItem as EntryRow)?.Id : null;
        var rows = _tab == 0
            ? _app.History.Select(c => new EntryRow(c.Id, c.IsImage ? "图片" : Library.Preview(c.Text), c.CopiedAt.ToLocalTime().ToString("HH:mm"), false, false, c.IsImage, c.Thumbnail)).ToList()
            : _app.Library.OrderedSnippets().Select(s => new EntryRow(s.Id, s.Title, s.Trigger ?? "", s.IsSensitive, s.IsPinned)).ToList();
        Entries.ItemsSource = rows;
        if (_menuId.HasValue) { rows = rows.Select(r => r with { ActionsOpen = r.Id == _menuId }).ToList(); Entries.ItemsSource = rows; }
        Entries.SelectedItem = rows.FirstOrDefault(r => r.Id == selected) ?? rows.FirstOrDefault();
        if (Entries.SelectedItem is not null) Entries.ScrollIntoView(Entries.SelectedItem);
        if (_tab == 0) HistoryTab.SetResourceReference(BackgroundProperty, "TabGradient"); else HistoryTab.Background = Brushes.Transparent;
        if (_tab == 1) SnippetsTab.SetResourceReference(BackgroundProperty, "TabGradient"); else SnippetsTab.Background = Brushes.Transparent;
        HistoryTab.FontWeight = _tab == 0 ? FontWeights.SemiBold : FontWeights.Normal;
        SnippetsTab.FontWeight = _tab == 1 ? FontWeights.SemiBold : FontWeights.Normal;
        SnippetActions.Visibility = _tab == 1 ? Visibility.Visible : Visibility.Collapsed;
        EditButton.IsEnabled = rows.Count > 0;
        HistoryToolbar.Visibility = _tab == 0 ? Visibility.Visible : Visibility.Collapsed;
        ClearHistoryButton.IsEnabled = !_busy && _app.HistoryStatus == HistoryStatus.Ready && rows.Count > 0;
        StatusLabel.Text = _tab == 0 ? _app.HistoryStatus switch
        {
            HistoryStatus.Loading => "正在读取…",
            HistoryStatus.Disabled => "请开启 Windows 剪贴板历史",
            HistoryStatus.AccessDenied => "无法访问 Windows 历史",
            HistoryStatus.Unavailable => "Windows 历史暂不可用",
            _ => !_app.HotkeyAvailable ? "快捷键被占用" : "Alt+空格唤醒"
        } : "Alt+空格唤醒";
        SystemSettingsButton.Visibility = _tab == 0 && _app.HistoryStatus != HistoryStatus.Ready && _app.HistoryStatus != HistoryStatus.Loading
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnSystemSettings(object sender, RoutedEventArgs e) => _app.OpenSystemClipboardSettings();

    private void SwitchTab(int tab) { if (_busy) return; Confirmation.Dismiss(); _menuId = null; _tab = tab; RefreshRows(); Entries.Focus(); }
    private void OnHistoryTab(object sender, RoutedEventArgs e) => SwitchTab(0);
    private void OnSnippetsTab(object sender, RoutedEventArgs e) => SwitchTab(1);
    private async void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (_busy) return;
        if (e.Key == Key.Escape && _menuId.HasValue && !Confirmation.IsOpen)
        { _menuId = null; RefreshRows(true); Entries.Focus(); e.Handled = true; return; }
        if (Confirmation.IsOpen)
        {
            if (e.Key == Key.Escape) { Confirmation.Dismiss(); Entries.Focus(); e.Handled = true; }
            return;
        }
        if (!IsList)
        {
            if (_settings?.IsDropDownOpen == true) return;
            if (e.Key == Key.Escape)
            {
                if (!(_editor?.DismissConfirmation() == true || _settings?.DismissConfirmation() == true)) ClosePage();
                e.Handled = true;
            }
            else if (e.Key == Key.S && Keyboard.Modifiers == ModifierKeys.Control)
            { _editor?.Save(); _settings?.Save(); e.Handled = true; }
            return;
        }
        // Let focused header/footer buttons handle Enter and Space normally.
        if (e.Key == Key.Enter && Keyboard.FocusedElement is Button) return;
        switch (e.Key)
        {
            case Key.Escape: Hide(); break;
            case Key.Left: SwitchTab(0); break;
            case Key.Right: SwitchTab(1); break;
            case Key.Up: MoveSelection(-1); break;
            case Key.Down: MoveSelection(1); break;
            case Key.Enter: await PasteSelectedAsync(); break;
            case Key.F2 when _tab == 1: EditSnippet(); break;
            case Key.N when _tab == 1 && Keyboard.Modifiers == ModifierKeys.Control: EditSnippet(create: true); break;
            default: return;
        }
        e.Handled = true;
    }
    private void MoveSelection(int delta)
    {
        if (Entries.Items.Count == 0) return;
        Entries.SelectedIndex = Math.Clamp(Entries.SelectedIndex + delta, 0, Entries.Items.Count - 1);
        Entries.ScrollIntoView(Entries.SelectedItem);
    }
    private async void OnEntryClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source && FindButton(source)) return;
        if (ItemsControl.ContainerFromElement(Entries, e.OriginalSource as DependencyObject) is not ListBoxItem item) return;
        Entries.SelectedItem = item.DataContext; e.Handled = true;
        await PasteSelectedAsync();
    }

    private void OnMore(object sender, RoutedEventArgs e)
    {
        if (_busy || sender is not Button { DataContext: EntryRow row }) return;
        Entries.SelectedItem = row;
        if (_tab == 1) { EditSnippet(); return; }
        Confirmation.Dismiss();
        _menuId = _menuId == row.Id ? null : row.Id;
        RefreshRows(true);
    }
    private void OnDeleteHistory(object sender, RoutedEventArgs e)
    {
        if (_busy || sender is not Button { DataContext: EntryRow row }) return;
        Confirmation.Ask("从 Windows 历史删除此项？", "删除", async () =>
        {
            if (_busy) return;
            _busy = true;
            try
            {
                var deleted = await _app.DeleteSystemHistoryAsync(row.Id);
                _menuId = null; RefreshRows(true);
                if (!deleted) StatusLabel.Text = "删除失败，请重试";
            }
            finally { _busy = false; ClearHistoryButton.IsEnabled = _app.HistoryStatus == HistoryStatus.Ready && _app.History.Count > 0; }
        });
    }
    private void OnClearHistory(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        _menuId = null; RefreshRows(true);
        Confirmation.Ask("清空 Windows 剪贴板历史？\nWin+V 中的固定项会保留。", "清空", async () =>
        {
            if (_busy) return;
            _busy = true;
            try
            {
                var cleared = await _app.ClearSystemHistoryAsync();
                RefreshRows();
                if (!cleared) StatusLabel.Text = "清空失败，请重试";
            }
            finally { _busy = false; ClearHistoryButton.IsEnabled = _app.HistoryStatus == HistoryStatus.Ready && _app.History.Count > 0; }
        });
    }

    private Task PasteSelectedAsync()
    {
        if (_busy || Entries.SelectedItem is not EntryRow row) return Task.CompletedTask;
        if (row.Sensitive)
        {
            var target = _target;
            Confirmation.Ask($"粘贴“{row.Title}”？\n敏感内容会暂存剪贴板，30 秒后尝试清除。", "粘贴", () => PasteRowAsync(row, target));
            return Task.CompletedTask;
        }
        return PasteRowAsync(row, _target);
    }
    private async Task PasteRowAsync(EntryRow row, PasteTarget? target)
    {
        if (_busy) return;
        var value = _tab == 0 ? _app.History.FirstOrDefault(c => c.Id == row.Id)?.Text
            : _app.Library.State.Snippets.FirstOrDefault(s => s.Id == row.Id)?.Value;
        if (value is null) return;
        _busy = true;
        try
        {
            var copied = row.IsImage ? await _app.RestoreHistoryImageAsync(row.Id) : await _app.ClipboardService.WriteAsync(value, row.Sensitive);
            if (!copied) { StatusLabel.Text = row.IsImage ? "图片已失效或剪贴板忙" : "剪贴板忙，请重试"; return; }
            Hide();
            if (!await PasteService.PasteAsync(target)) _app.Notify("已复制 · Ctrl+V 粘贴");
        }
        finally { _busy = false; }
    }

    private void OnNew(object sender, RoutedEventArgs e) => EditSnippet(create: true);
    private void OnEdit(object sender, RoutedEventArgs e) => EditSnippet();
    private void EditSnippet(bool create = false)
    {
        if (!IsList || _busy) return;
        var selected = Entries.SelectedItem as EntryRow;
        var snippet = create ? null : _app.Library.State.Snippets.FirstOrDefault(s => s.Id == selected?.Id);
        if (!create && snippet is null) return;
        _editor = new SnippetView(_app, snippet);
        _editor.Finished += ClosePage;
        ShowPage(_editor, _editor.Heading);
    }
    private void OnSettings(object sender, RoutedEventArgs e) => ShowSettings();
    internal void ShowSettings()
    {
        if (!IsList || _busy) return;
        _settings = new SettingsView(_app);
        _settings.Finished += ClosePage;
        ShowPage(_settings, "设置");
    }
    private void ShowPage(UserControl page, string title)
    {
        Confirmation.Dismiss();
        ListPage.Visibility = Visibility.Collapsed; PageHost.Visibility = Visibility.Visible;
        PageHost.Content = page; PageTitle.Text = title;
        BrandIcon.Visibility = SettingsButton.Visibility = Visibility.Collapsed;
        BackButton.Visibility = Visibility.Visible;
        page.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
    }
    private void OnBack(object sender, RoutedEventArgs e) => ClosePage();
    private void ClosePage()
    {
        _editor?.Discard(); _settings?.Discard();
        _editor = null; _settings = null;
        PageHost.Content = null; PageHost.Visibility = Visibility.Collapsed; ListPage.Visibility = Visibility.Visible;
        PageTitle.Text = "ClipTap"; BrandIcon.Visibility = SettingsButton.Visibility = Visibility.Visible;
        BackButton.Visibility = Visibility.Collapsed;
        RefreshRows(preserveSelection: true); Entries.Focus();
    }
    internal void ConfirmExit(Action exit) => Confirmation.Ask("更改尚未保存，仍要退出？", "退出", () => { exit(); return Task.CompletedTask; });
    private void OnDeactivated(object? sender, EventArgs e) { if (!_busy) { Confirmation.Dismiss(); Hide(); } }
    private void OnClosing(object? sender, CancelEventArgs e) { if (!_app.IsQuitting) { e.Cancel = true; Hide(); } }
    private void OnHeaderDrag(object sender, MouseButtonEventArgs e)
    {
        if (_busy || e.ChangedButton != MouseButton.Left || e.LeftButton != MouseButtonState.Pressed) return;
        if (e.OriginalSource is DependencyObject source && FindButton(source)) return;
        e.Handled = true; DragMove();
    }
    private static bool FindButton(DependencyObject source)
    {
        for (var current = source; current is not null; current = current is Visual ? VisualTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current))
            if (current is Button) return true;
        return false;
    }
}

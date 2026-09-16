using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using ClipTap.Core;
using ClipTap.Services;

namespace ClipTap;

internal sealed record EntryRow(Guid Id, string Title, string Detail, string Glyph, bool Sensitive)
{
    public override string ToString() => Title;
}

public partial class MainWindow : Window
{
    private readonly App _app;
    private nint _handle;
    private int _tab;
    private bool _dialogOpen, _busy;
    private PasteTarget? _target;

    public MainWindow(App app) { _app = app; InitializeComponent(); RefreshRows(); }

    internal void ToggleFromHotkey()
    {
        if (_dialogOpen || _busy) return;
        if (IsVisible) Hide(); else OpenPanel(captureTarget: true);
    }

    internal void OpenPanel(bool captureTarget)
    {
        if (_dialogOpen || _busy) return;
        _target = captureTarget ? PasteService.CaptureTarget() : null;
        if (_handle == nint.Zero)
        {
            _handle = new WindowInteropHelper(this).EnsureHandle();
            var corner = 2;
            NativeMethods.DwmSetWindowAttribute(_handle, 33, ref corner, sizeof(int));
        }
        _tab = 0;
        RefreshRows();
        PlaceNearTarget();
        Show();
        Activate();
        Entries.Focus();
    }

    private void PlaceNearTarget()
    {
        var anchor = _target?.Window ?? NativeMethods.GetForegroundWindow();
        var monitor = NativeMethods.MonitorFromWindow(anchor, 2);
        var info = new NativeMethods.MonitorInfo { Size = Marshal.SizeOf<NativeMethods.MonitorInfo>() };
        if (!NativeMethods.GetMonitorInfo(monitor, ref info)) return;
        var dpi = NativeMethods.GetDpiForWindow(anchor);
        var scale = dpi > 0 ? dpi / 96.0 : 1;
        var width = (info.Work.Right - info.Work.Left) / scale;
        var height = (info.Work.Bottom - info.Work.Top) / scale;
        Left = info.Work.Left / scale + Math.Max(0, (width - Width) / 2);
        Top = info.Work.Top / scale + Math.Max(0, (height - Height) / 3);
    }

    internal void RefreshHistory() { if (_tab == 0 && IsVisible) RefreshRows(preserveSelection: true); }

    internal void RefreshRows(bool preserveSelection = false)
    {
        var selected = preserveSelection ? (Entries.SelectedItem as EntryRow)?.Id : null;
        var rows = _tab == 0
            ? _app.Library.State.History.Select(c => new EntryRow(c.Id, Library.Preview(c.Text), $"{c.CopiedAt:MM-dd HH:mm}  ·  {c.Text.Length:N0} 字符", "▤", false)).ToList()
            : _app.Library.OrderedSnippets().Select(s => new EntryRow(s.Id, s.Title,
                (s.IsPinned ? "置顶  ·  " : "") + (s.IsSensitive ? "敏感内容 · 已隐藏" : "快捷片段"), s.IsSensitive ? "◇" : "≡", s.IsSensitive)).ToList();
        Entries.ItemsSource = rows;
        Entries.SelectedItem = rows.FirstOrDefault(r => r.Id == selected) ?? rows.FirstOrDefault();
        if (Entries.SelectedItem is not null) Entries.ScrollIntoView(Entries.SelectedItem);
        if (_tab == 0) HistoryTab.SetResourceReference(BackgroundProperty, "TabSurface");
        else HistoryTab.Background = Brushes.Transparent;
        if (_tab == 1) SnippetsTab.SetResourceReference(BackgroundProperty, "TabSurface");
        else SnippetsTab.Background = Brushes.Transparent;
        HistoryTab.FontWeight = _tab == 0 ? FontWeights.SemiBold : FontWeights.Normal;
        SnippetsTab.FontWeight = _tab == 1 ? FontWeights.SemiBold : FontWeights.Normal;
        SectionLabel.Text = _tab == 0 ? "最近复制" : "你的常用内容";
        CountLabel.Text = $"{rows.Count} 条";
        EmptyState.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyTitle.Text = _tab == 0 ? "从一次复制开始" : "把常用内容放在手边";
        EmptyHint.Text = _tab == 0 ? "复制一段文字，它会出现在这里。\n随时按 Alt+空格 唤起。" : "添加标题和内容，下次选中即可粘贴。";
        SnippetActions.Visibility = _tab == 1 ? Visibility.Visible : Visibility.Collapsed;
        StatusLabel.Text = _tab == 1 ? "F2 编辑" : _app.Library.State.Settings.CapturePaused ? "记录已暂停" : "仅存于本机 · 加密保存";
        if (!_app.HotkeyAvailable) StatusLabel.Text = "快捷键被占用 · 请使用托盘";
    }

    private void SwitchTab(int tab) { _tab = tab; RefreshRows(); Entries.Focus(); }
    private void OnHistoryTab(object sender, RoutedEventArgs e) => SwitchTab(0);
    private void OnSnippetsTab(object sender, RoutedEventArgs e) => SwitchTab(1);
    private async void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (_dialogOpen || _busy) return;
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
        var item = ItemsControl.ContainerFromElement(Entries, e.OriginalSource as DependencyObject) as ListBoxItem;
        if (item is null) return;
        Entries.SelectedItem = item.DataContext;
        e.Handled = true;
        await PasteSelectedAsync();
    }

    private async Task PasteSelectedAsync()
    {
        if (_busy || Entries.SelectedItem is not EntryRow row) return;
        var value = _tab == 0 ? _app.Library.State.History.FirstOrDefault(c => c.Id == row.Id)?.Text
            : _app.Library.State.Snippets.FirstOrDefault(s => s.Id == row.Id)?.Value;
        if (value is null) return;
        _busy = true;
        try
        {
            if (row.Sensitive)
            {
                _dialogOpen = true;
                var result = MessageBox.Show(this, $"使用敏感片段“{row.Title}”？\n\n内容会暂存系统剪贴板，并在 30 秒后尝试清除。其他剪贴板工具仍可能读取它。", "敏感片段", MessageBoxButton.OKCancel, MessageBoxImage.Information);
                _dialogOpen = false;
                if (result != MessageBoxResult.OK) return;
            }
            if (!await _app.ClipboardService.WriteAsync(value, row.Sensitive))
            { _app.Notify("剪贴板正在被其他程序使用，请稍后重试。"); return; }
            var target = _target;
            Hide();
            if (!await PasteService.PasteAsync(target)) _app.Notify("已复制。请聚焦输入框后按 Ctrl+V 粘贴。");
        }
        finally { _busy = false; _dialogOpen = false; }
    }

    private void OnNew(object sender, RoutedEventArgs e) => EditSnippet(create: true);
    private void OnEdit(object sender, RoutedEventArgs e) => EditSnippet();
    private void EditSnippet(bool create = false)
    {
        var selected = Entries.SelectedItem as EntryRow;
        var snippet = create ? null : _app.Library.State.Snippets.FirstOrDefault(s => s.Id == selected?.Id);
        if (!create && snippet is null) return;
        _dialogOpen = true;
        try
        {
            var editor = new SnippetWindow(snippet) { Owner = this };
            if (editor.ShowDialog() == true)
            {
                if (editor.DeleteRequested && snippet is not null) _app.Library.State.Snippets.Remove(snippet);
                else if (editor.Result is { } result) _app.Library.SaveSnippet(result);
                _app.Changed();
                _app.SaveNow();
                RefreshRows();
            }
        }
        catch (ArgumentException ex) { MessageBox.Show(this, ex.Message, "ClipTap"); }
        finally { _dialogOpen = false; Activate(); Entries.Focus(); }
    }

    private void OnSettings(object sender, RoutedEventArgs e) => ShowSettings();
    internal void ShowSettings()
    {
        if (_dialogOpen) return;
        _dialogOpen = true;
        try { new SettingsWindow(_app) { Owner = this }.ShowDialog(); RefreshRows(); }
        finally { _dialogOpen = false; Activate(); Entries.Focus(); }
    }
    private void OnDeactivated(object? sender, EventArgs e) { if (!_dialogOpen && !_busy) Hide(); }
    private void OnClosing(object? sender, CancelEventArgs e) { if (!_app.IsQuitting) { e.Cancel = true; Hide(); } }
    private void OnHeaderDrag(object sender, MouseButtonEventArgs e)
    {
        if (_busy || _dialogOpen || e.ChangedButton != MouseButton.Left || e.LeftButton != MouseButtonState.Pressed) return;
        if (e.OriginalSource is DependencyObject source && FindButton(source)) return;
        e.Handled = true;
        DragMove();
    }
    private static bool FindButton(DependencyObject source)
    {
        for (var current = source; current is not null;
             current = current is Visual ? VisualTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current))
            if (current is Button) return true;
        return false;
    }
}

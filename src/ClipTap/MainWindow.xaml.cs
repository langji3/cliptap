using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Security.Cryptography;
using System.Text;
using ClipTap.Core;
using ClipTap.Services;
using ClipTap.Views;

namespace ClipTap;

/// <summary>
/// One row as presented to the list. The visual tree binds only <see cref="Title"/>, <see cref="Meta"/>,
/// <see cref="Detail"/>, <see cref="Hint"/>, <see cref="Thumbnail"/>, the two state flags and the row's
/// automation name, which is <see cref="Title"/>. <see cref="SearchBody"/> carries the text a query is
/// allowed to match and is deliberately null for sensitive snippets, whose value must never reach a
/// searchable field or a display field.
/// </summary>
internal sealed record EntryRow(
    Guid Id,
    string Title,
    string Detail,
    bool Sensitive,
    bool Pinned,
    bool IsImage = false,
    ImageSource? Thumbnail = null,
    bool ActionsOpen = false,
    string Meta = "",
    bool IsSnippet = false,
    bool IsLink = false,
    string? SearchBody = null,
    string? Hint = null,
    LaunchableApp? Application = null,
    int? CommandTab = null)
{
    public bool IsApplication => Application is not null;
    public bool IsCommand => CommandTab.HasValue;
    public override string ToString() => Title;
}

public partial class MainWindow : Window
{
    private const double SearchBarHeight = 74;
    private const double ExpandedHeight = 540;
    private readonly App _app;
    private double _panelHeight = SearchBarHeight;
    private nint _handle;
    private int _tab = -1;
    private readonly IApplicationCatalog _catalog;
    private IReadOnlyList<LaunchableApp> _applications = [];
    private bool _loadingApplications;
    private DateTime _applicationsLoadedAt;
    private string? _applicationError;
    private string _typeFilter = "all";
    private int _imageZoom;
    private Guid? _previewImageId;
    private Guid? _lastClickedEntryId;
    private bool _busy;
    private bool _menuOpen;
    private Guid? _menuId;
    private Guid? _historySelection;
    private PasteTarget? _target;
    private SnippetView? _editor;
    private SettingsView? _settings;
    private bool IsList => PageHost.Content is null;

    /// <summary>Full history rows rebuilt on every render; the displayed list is never the data source.</summary>
    private List<EntryRow> _historyRows = [];
    /// <summary>Remembered list context so returning from a page restores tab, query and selection.</summary>
    private (int Tab, string Query, Guid? Selection)? _listContext;
    // Track identity, not a recycled ListBoxItem container. Every press and release in the window
    // resets this state, including gestures that start on a button, backdrop or footer.
    private Guid? _pressedEntryId;
    /// <summary>A failure message that survives the next chrome refresh; cleared once it is shown.</summary>
    private string? _statusOverride;

    public MainWindow(App app) : this(app, new WindowsApplicationCatalog()) { }

    internal MainWindow(App app, IApplicationCatalog catalog)
    {
        _app = app;
        _catalog = catalog;
        InitializeComponent();
        Confirmation.FocusReleased += FocusSearch;
        RefreshRows();
    }

    internal void ToggleFromHotkey()
    {
        if (_busy) return;
        if (IsVisible) Hide(); else OpenPanel(captureTarget: true);
    }

    internal void OpenPanel(bool captureTarget)
    {
        if (_busy) return;
        _pressedEntryId = null;
        _lastClickedEntryId = null;
        var entering = !IsVisible;
        Confirmation.Dismiss();
        CloseMenu();
        _editor?.DismissConfirmation();
        _settings?.DismissConfirmation();
        _target = captureTarget ? PasteService.CaptureTarget() : null;
        if (_handle == nint.Zero)
            _handle = new WindowInteropHelper(this).EnsureHandle();
        // A fresh summon shows only the search bar; a page return keeps its context.
        if (IsList && entering)
        {
            _tab = -1;
            _typeFilter = "all";
            TypeFilter.SelectedIndex = 0;
            if (SearchBox.Text.Length > 0) SearchBox.Clear();
        }
        if (IsList) RefreshRows();
        PlaceNearTarget();
        // Prepare before Show so there is no fully visible frame before the entrance starts.
        PanelSurface.BeginAnimation(OpacityProperty, null);
        PanelSurface.Opacity = entering && _app.AnimatePanel ? 0 : 1;
        Show();
        PlaceNearTarget();
        NativeMethods.SetForegroundWindow(_handle);
        Activate();
        if (entering && _app.AnimatePanel)
        {
            var duration = TimeSpan.FromMilliseconds(140);
            var easing = new QuarticEase { EasingMode = EasingMode.EaseOut };
            PanelSurface.Opacity = 1;
            PanelSurface.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(120)) { EasingFunction = easing });
            EntranceOffset.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(4, 0, duration) { EasingFunction = easing });
            // No scale pop: keep the entrance to a short fade plus a small rise.
            EntranceScale.BeginAnimation(ScaleTransform.ScaleXProperty, null); EntranceScale.ScaleX = 1;
            EntranceScale.BeginAnimation(ScaleTransform.ScaleYProperty, null); EntranceScale.ScaleY = 1;
        }
        else
        {
            PanelSurface.BeginAnimation(OpacityProperty, null); PanelSurface.Opacity = 1;
            EntranceOffset.BeginAnimation(TranslateTransform.YProperty, null); EntranceOffset.Y = 0;
            EntranceScale.BeginAnimation(ScaleTransform.ScaleXProperty, null); EntranceScale.ScaleX = 1;
            EntranceScale.BeginAnimation(ScaleTransform.ScaleYProperty, null); EntranceScale.ScaleY = 1;
        }
        _ = LoadHistoryAsync();
        if (IsList && _tab == -1) _ = LoadApplicationsAsync();
        if (IsList) FocusSearch(); else PageHost.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
    }

    private void OnPanelContentSizeChanged(object sender, SizeChangedEventArgs e)
    {
        // Clip every page to the same inner curve as the single outer border, including during resizing.
        var radius = Math.Max(0, PanelSurface.CornerRadius.TopLeft - PanelSurface.BorderThickness.Left);
        var clip = new RectangleGeometry(new Rect(e.NewSize), radius, radius);
        clip.Freeze();
        PanelContent.Clip = clip;
    }

    private void PlaceNearTarget()
    {
        var anchor = _target?.Window ?? NativeMethods.GetForegroundWindow();
        var monitor = NativeMethods.MonitorFromWindow(anchor, 2);
        var info = new NativeMethods.MonitorInfo { Size = Marshal.SizeOf<NativeMethods.MonitorInfo>() };
        if (!NativeMethods.GetMonitorInfo(monitor, ref info)) return;
        // Screen origins are physical pixels, not WPF DIPs. First move onto the target
        // monitor so WPF processes its DPI change, then measure and position in pixels.
        const uint moveOnly = 0x0001 | 0x0004 | 0x0010; // NOSIZE | NOZORDER | NOACTIVATE
        if (NativeMethods.MonitorFromWindow(_handle, 2) != monitor)
            NativeMethods.SetWindowPos(_handle, 0, info.Work.Left, info.Work.Top, 0, 0, moveOnly);
        if (!NativeMethods.GetWindowRect(_handle, out var bounds)) return;
        // A small work area (or heavy display scaling) can leave less room than the panel's design size.
        // Clamp to the work area in DIP so the panel never extends past the desktop edge, and let the
        // inner list scroll instead.
        var scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        var fit = PanelFit(info.Work, scale, Width, ExpandedHeight);
        MaxWidth = fit.Width;
        MaxHeight = fit.Height;
        // Reserve the expanded panel's space so typing never pushes results off-screen or moves the bar.
        var position = PanelPosition(info.Work, bounds.Right - bounds.Left, (int)Math.Ceiling(fit.Height * scale));
        NativeMethods.SetWindowPos(_handle, 0, position.X, position.Y, 0, 0, moveOnly);
    }

    /// <summary>
    /// Largest the panel may be inside <paramref name="work"/> without exceeding the desktop edge. The
    /// work area is in physical pixels while the design size is in DIP, so the two are reconciled here.
    /// </summary>
    internal static (double Width, double Height) PanelFit(NativeMethods.Rect work, double scale, double designWidth, double designHeight)
    {
        if (scale <= 0) scale = 1;
        var availableWidth = (work.Right - work.Left) / scale;
        var availableHeight = (work.Bottom - work.Top) / scale;
        return (Math.Min(designWidth, availableWidth), Math.Min(designHeight, availableHeight));
    }

    internal static (int X, int Y) PanelPosition(NativeMethods.Rect work, int width, int height) =>
        (work.Left + Math.Max(0, (work.Right - work.Left - width) / 2),
         work.Top + Math.Max(0, (work.Bottom - work.Top - height) / 3));

    private async Task LoadHistoryAsync() { await _app.RefreshSystemHistoryAsync(); RefreshHistory(); }

    internal void RefreshHistory() { if (IsList && _tab == 0 && IsVisible) RefreshRows(preserveSelection: true); }

    /// <summary>
    /// Rebuilds rows from the live data sources and re-applies the active query. History comes from the
    /// system snapshot and snippets from the library, so the rendered list is never used as a data source.
    /// </summary>
    internal void RefreshRows(bool preserveSelection = false)
    {
        var query = ClipboardSearch.Normalize(SearchBox.Text);
        var selected = preserveSelection ? (Entries.SelectedItem as EntryRow)?.Id : null;
        if (_tab == 0) selected ??= _historySelection;

        List<EntryRow> rows;
        if (_tab == 0)
        {
            _historyRows = _app.History.Select(HistoryRow).ToList();
            rows = query.Length == 0
                ? _historyRows
                : _historyRows.Where(r => Match(r, query)).ToList();
        }
        else if (_tab == 1)
        {
            rows = _app.Library.OrderedSnippets()
                .Where(s => ClipboardSearch.Matches(ClipboardSearch.SnippetField(s.IsSensitive), s.Title,
                    s.Trigger, s.IsSensitive ? null : s.Value, query))
                .Select(SnippetRow)
                .ToList();
        }
        else
        {
            rows = query.Length == 0 ? [] : HomeRows().Where(r => Match(r, query))
                .OrderBy(r => r.IsCommand ? 0 : r.Pinned ? 1 : 2)
                .ThenBy(r => r.Title.StartsWith(query, StringComparison.CurrentCultureIgnoreCase) ? 0 : 1)
                .ToList();
        }
        if (_tab == 0) rows = rows.Where(r => _typeFilter switch
        {
            "image" => r.IsImage,
            "link" => r.IsLink,
            "text" => !r.IsImage && !r.IsLink,
            _ => true
        }).ToList();
        if (_menuId.HasValue) rows = rows.Select(r => r with { ActionsOpen = r.Id == _menuId }).ToList();

        Entries.ItemsSource = rows;
        Entries.SelectedItem = rows.FirstOrDefault(r => r.Id == selected) ?? rows.FirstOrDefault();
        // A query with no matches must clear the selection so nothing can be pasted by accident.
        if (rows.Count == 0) Entries.SelectedIndex = -1;
        if (Entries.SelectedItem is not null) Entries.ScrollIntoView(Entries.SelectedItem);

        ApplyChrome(rows.Count, query);
        UpdatePreview();
    }

    private IEnumerable<EntryRow> HomeRows()
    {
        yield return new(Guid.Parse("1db523ca-d47b-41cd-91c5-5476237d6000"), "Clipboard", "", false, false,
            Meta: "查看复制过的文字与图片", SearchBody: "剪贴板 clipboard history 历史", CommandTab: 0);
        yield return new(Guid.Parse("1db523ca-d47b-41cd-91c5-5476237d6001"), "Snip · 快捷片段", "", false, false,
            Meta: "常用文字与快捷片段", IsSnippet: true, SearchBody: "snip snippet 片段 快捷文字", CommandTab: 1);
        foreach (var app in _applications)
            yield return new(new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(app.Id))[..16]), app.Name, "", false,
                _app.Library.State.PinnedApplications.Contains(app.Id, StringComparer.OrdinalIgnoreCase),
                Thumbnail: app.Icon, Meta: "应用", SearchBody: app.Name + " " + Path.GetFileName(app.ExecutablePath), Application: app);
    }

    private async Task LoadApplicationsAsync(bool force = false)
    {
        if (_loadingApplications || !force && DateTime.UtcNow - _applicationsLoadedAt < TimeSpan.FromMinutes(2)) return;
        _loadingApplications = true;
        _applicationError = null;
        if (IsList && _tab == -1) RefreshRows(true);
        try
        {
            _applications = await _catalog.ReadAsync().WaitAsync(TimeSpan.FromSeconds(8));
            _applicationsLoadedAt = DateTime.UtcNow;
        }
        catch (Exception ex) when (ex is TimeoutException or COMException or InvalidOperationException or IOException or UnauthorizedAccessException)
        { _applicationError = "应用列表读取失败，请刷新"; }
        finally
        {
            _loadingApplications = false;
            if (IsList && _tab == -1) RefreshRows(true);
        }
    }

    private async void OnRefreshApplications(object sender, RoutedEventArgs e)
    { CloseMenu(); await LoadApplicationsAsync(force: true); }

    private void OnTypeFilterChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Entries is null || PageHost is null) return;
        _typeFilter = TypeFilter.SelectedValue as string ?? "all";
        CloseMenu();
        if (IsList) RefreshRows(true);
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e) => UpdatePreview();

    private void UpdatePreview()
    {
        if (PreviewText is null) return;
        PreviewText.Clear();
        PreviewImage.Source = null;
        PreviewAppIcon.Source = null;
        PreviewText.Visibility = PreviewImageFrame.Visibility = Visibility.Collapsed;
        ImageZoomActions.Visibility = Visibility.Collapsed;
        PreviewEmpty.Visibility = Visibility.Visible;
        PreviewInfo.Visibility = Visibility.Collapsed;
        PreviewTitle.Text = "预览";
        PreviewEmptyTitle.Text = "没有选中内容";
        PreviewEmptyDetail.Text = "选择左侧条目查看详情";
        PreviewKind.Text = PreviewMeta.Text = PreviewMetaLabel.Text = "";
        PasteButton.IsEnabled = Entries.SelectedItem is EntryRow;
        EditButton.IsEnabled = _tab == 1 && Entries.SelectedItem is EntryRow;
        if (Entries.SelectedItem is not EntryRow row) return;
        PreviewTitle.Text = row.IsApplication || row.IsCommand || row.IsSnippet ? row.Title : row.IsImage ? "图片预览" : "文本内容";
        PreviewInfo.Visibility = Visibility.Visible;
        PreviewMetaLabel.Text = "字符数";
        if (row.IsApplication || row.IsCommand)
        {
            PreviewEmptyTitle.Text = row.Title;
            PreviewEmptyDetail.Text = row.IsApplication ? "按 Enter 打开应用" : row.CommandTab == 0 ? "搜索、预览并粘贴复制过的内容" : "保存常用文字，通过触发词快速输入";
            PreviewAppIcon.Source = row.Application?.Icon;
            PreviewKind.Text = row.IsApplication ? "应用" : "内置功能";
            PreviewMetaLabel.Text = "操作";
            PreviewMeta.Text = "Enter 打开";
        }
        else if (row.Sensitive)
        {
            PreviewEmptyTitle.Text = "敏感片段";
            PreviewEmptyDetail.Text = "内容已隐藏 · 按 Enter 使用";
            PreviewKind.Text = "敏感片段";
            PreviewMetaLabel.Text = "保护";
            PreviewMeta.Text = "不参与正文搜索";
        }
        else if (row.IsImage)
        {
            PreviewKind.Text = "图片";
            PreviewMetaLabel.Text = "预览尺寸";
            if (row.Thumbnail is { } bitmap)
            {
                PreviewEmpty.Visibility = Visibility.Collapsed;
                PreviewImageFrame.Visibility = Visibility.Visible;
                PreviewImage.Source = bitmap;
                ImageZoomActions.Visibility = Visibility.Visible;
                if (_previewImageId != row.Id)
                {
                    _imageZoom = bitmap.Width / Math.Max(1, bitmap.Height) >= 4 ? 1 : 0;
                    _previewImageId = row.Id;
                    ImageViewport.ScrollToHome();
                    ImageViewport.ScrollToLeftEnd();
                }
                ApplyImageZoom();
                PreviewMeta.Text = bitmap is BitmapSource pixels ? $"{pixels.PixelWidth} × {pixels.PixelHeight}" : $"{bitmap.Width:0} × {bitmap.Height:0}";
            }
            else
            {
                PreviewEmptyTitle.Text = "图片预览暂不可用";
                PreviewEmptyDetail.Text = "仍可按 Enter 使用原始图片";
                PreviewMeta.Text = "不可用";
            }
        }
        else
        {
            PreviewEmpty.Visibility = Visibility.Collapsed;
            PreviewText.Visibility = Visibility.Visible;
            PreviewText.Text = row.SearchBody ?? "";
            PreviewText.ScrollToHome();
            PreviewKind.Text = row.IsSnippet ? "快捷片段" : row.IsLink ? "链接" : "文本";
            PreviewMeta.Text = PreviewText.Text.Length.ToString("N0");
        }
    }

    private void OnImageZoom(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string mode } || !int.TryParse(mode, out _imageZoom)) return;
        ApplyImageZoom();
        ImageViewport.ScrollToHome();
        ImageViewport.ScrollToLeftEnd();
    }

    private void OnImageViewportSizeChanged(object sender, SizeChangedEventArgs e) => ApplyImageZoom();

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        ApplyImageZoom();
    }

    private void ApplyImageZoom()
    {
        if (PreviewImage?.Source is not BitmapSource bitmap) return;
        var fit = _imageZoom == 0;
        ImageViewport.HorizontalScrollBarVisibility = ImageViewport.VerticalScrollBarVisibility =
            fit ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto;
        var dpi = VisualTreeHelper.GetDpi(this);
        var factor = _imageZoom == 2 ? 2d : 1d;
        PreviewImage.Width = fit ? Math.Max(1, ImageViewport.ActualWidth) : bitmap.PixelWidth * factor / dpi.DpiScaleX;
        PreviewImage.Height = fit ? Math.Max(1, ImageViewport.ActualHeight) : bitmap.PixelHeight * factor / dpi.DpiScaleY;
        SetTabState(ImageFitButton, fit);
        SetTabState(ImageActualButton, _imageZoom == 1);
        SetTabState(ImageDoubleButton, _imageZoom == 2);
    }

    private static bool Match(EntryRow row, string query) =>
        ClipboardSearch.Matches(row.Title, query) || ClipboardSearch.Matches(row.SearchBody, query);

    private static EntryRow HistoryRow(HistoryEntry item)
    {
        var isLink = !item.IsImage && LooksLikeLink(item.Text);
        return new(item.Id,
            item.IsImage ? ClipboardSearch.ImageLabel : Library.Preview(item.Text, 160),
            item.CopiedAt.ToLocalTime().ToString("HH:mm"),
            false, false, item.IsImage, item.Thumbnail,
            // The "图片" label is already the row title, so image rows carry no second line.
            Meta: item.IsImage ? "" : isLink ? "链接" : "文本",
            IsLink: isLink,
            // Images match on their type label; text matches on the complete stored value.
            SearchBody: item.IsImage ? ClipboardSearch.ImageLabel : item.Text,
            Hint: item.IsImage ? "图片" : Flatten(item.Text));
    }

    private static EntryRow SnippetRow(Snippet snippet) =>
        new(snippet.Id, snippet.Title, "", snippet.IsSensitive, snippet.IsPinned,
            Meta: snippet.Trigger is { Length: > 0 } trigger ? "触发词 " + trigger : snippet.IsSensitive ? "敏感" : "",
            IsSnippet: true,
            // Sensitive values are never handed to the searchable field.
            SearchBody: snippet.IsSensitive ? null : snippet.Value,
            Hint: string.IsNullOrEmpty(snippet.Trigger) ? snippet.Title : $"{snippet.Title}\n{snippet.Trigger}");

    /// <summary>Flattens newlines for the hover hint; the searchable field keeps the raw text.</summary>
    private static string Flatten(string text)
    {
        var flat = text.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ').Trim();
        return flat.Length <= 300 ? flat : flat[..300] + "…";
    }

    /// <summary>Local, offline link recognition; no title or favicon is ever fetched.</summary>
    private static bool LooksLikeLink(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length is 0 or > 2048 || trimmed.Contains(' ') || trimmed.Contains('\n')) return false;
        return trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("www.", StringComparison.OrdinalIgnoreCase);
    }

    private void ApplyChrome(int rowCount, string query)
    {
        ApplyPanelLayout(rowCount, query);
        SetTabState(HomeButton, _tab == -1);
        HomeButton.Visibility = HistoryTab.Visibility = SnippetsTab.Visibility = _tab == -1 ? Visibility.Collapsed : Visibility.Visible;
        PreviewPane.Visibility = _tab == -1 ? Visibility.Collapsed : Visibility.Visible;
        Grid.SetColumnSpan(ListPane, _tab == -1 ? 2 : 1);
        SetTabState(HistoryTab, _tab == 0);
        SetTabState(SnippetsTab, _tab == 1);
        SnippetActions.Visibility = _tab == 1 ? Visibility.Visible : Visibility.Collapsed;
        // Edit targets the selected snippet, so it is meaningful only on the snippets tab.
        EditButton.IsEnabled = _tab == 1 && rowCount > 0;
        CountLabel.Text = rowCount > 0 ? $"{rowCount} 项" : "";
        SearchHint.Text = _tab switch { 0 => "搜索剪贴板…", 1 => "搜索 Snip…", _ => "搜索应用、Clipboard、Snip…" };
        SectionLabel.Text = _tab switch { 0 => "Clipboard History", 1 => "快捷片段", _ => "搜索结果" };
        ScopeLabel.Text = _tab == 1 ? "Snip" : "应用与功能";
        TypeFilter.Visibility = _tab == 0 ? Visibility.Visible : Visibility.Collapsed;
        ScopeLabel.Visibility = _tab == 1 ? Visibility.Visible : Visibility.Collapsed;
        BrandIcon.Visibility = _tab == -1 ? Visibility.Visible : Visibility.Collapsed;
        BackButton.Visibility = _tab == -1 ? Visibility.Collapsed : Visibility.Visible;
        SearchHeader.Visibility = SettingsButton.Visibility = Visibility.Visible;
        PageTitle.Visibility = Visibility.Collapsed;
        PasteButton.Content = _tab == -1 ? "打开 ↵" : "粘贴 ↵";
        ClearSearchButton.Visibility = query.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        MenuClearButton.IsEnabled = !_busy && _app.HistoryStatus == HistoryStatus.Ready && _app.History.Count > 0;
        MenuClearButton.Visibility = MenuHint.Visibility = _tab == -1 ? Visibility.Collapsed : Visibility.Visible;
        MenuRefreshAppsButton.Visibility = _tab == -1 ? Visibility.Visible : Visibility.Collapsed;
        MenuRefreshAppsButton.IsEnabled = !_loadingApplications;
        ShortcutLabel.Text = _tab == 1 ? "" : "↑↓ 选择";

        var status = _app.HistoryStatus switch
        {
            HistoryStatus.Loading => "正在读取…",
            HistoryStatus.Disabled => "请开启 Windows 剪贴板历史",
            HistoryStatus.AccessDenied => "无法访问 Windows 历史",
            HistoryStatus.Unavailable => "Windows 历史暂不可用",
            _ => !_app.HotkeyAvailable ? "快捷键被占用" : _app.HotkeyLabel + " 唤起"
        };
        var blocked = _tab == 0 && _app.HistoryStatus is not (HistoryStatus.Ready or HistoryStatus.Loading);
        // Real availability problems outrank "no search results" and stay readable in the footer.
        // A one-shot override carries a failure message across the refresh that follows it.
        StatusLabel.Text = _statusOverride ?? (_tab == -1
            ? _applicationError ?? (_loadingApplications ? "正在读取应用…" : $"{_applications.Count} 个应用")
            : _tab == 0 ? status : _app.HotkeyLabel + " 唤起");
        StatusLabel.ToolTip = StatusLabel.Text;
        _statusOverride = null;
        SystemSettingsButton.Visibility = blocked ? Visibility.Visible : Visibility.Collapsed;

        ApplyEmptyState(rowCount, query, blocked, status);
    }

    private void ApplyPanelLayout(int rowCount, string query)
    {
        var showResults = _tab >= 0 || query.Length > 0;
        var revealing = showResults && ListPage.Visibility != Visibility.Visible;
        ListPage.Visibility = showResults ? Visibility.Visible : Visibility.Collapsed;
        var height = _tab >= 0 ? ExpandedHeight
            : !showResults ? (_menuOpen ? 230 : SearchBarHeight)
            : rowCount == 0 ? 280 : Math.Min(ExpandedHeight, SearchBarHeight + 84 + Math.Min(rowCount, 8) * 50);
        SetPanelHeight(height);
        if (!showResults || !_app.AnimatePanel)
        {
            ListPage.BeginAnimation(OpacityProperty, null);
            ResultsOffset.BeginAnimation(TranslateTransform.YProperty, null);
        }
        else if (revealing && IsVisible)
        {
            var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
            ListPage.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160))
                { EasingFunction = easing, FillBehavior = FillBehavior.Stop });
            ResultsOffset.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(5, 0, TimeSpan.FromMilliseconds(180))
                { EasingFunction = easing, FillBehavior = FillBehavior.Stop });
        }
    }

    private void SetPanelHeight(double height)
    {
        if (_panelHeight == height && IsVisible && _app.AnimatePanel) return;
        var previous = Height;
        _panelHeight = height;
        if (!IsVisible || !_app.AnimatePanel || Math.Abs(previous - height) < 1)
        {
            Height = height;
            BeginAnimation(HeightProperty, null);
            return;
        }
        // Setting the base Height first resizes the native window before the animation's first tick.
        // Keep its current value until the animation finishes, including when a new query interrupts it.
        var animation = new DoubleAnimation(previous, height, TimeSpan.FromMilliseconds(180))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        animation.Completed += (_, _) =>
        {
            if (_panelHeight != height) return;
            Height = height;
            BeginAnimation(HeightProperty, null);
            // Newly opened pages/menus may have had no visible body when focus was first requested.
            if (!IsVisible) return;
            if (_menuOpen && !MenuLayer.IsKeyboardFocusWithin)
                (_tab == -1 ? MenuRefreshAppsButton : MenuClearButton).Focus();
            else if (PageHost.Content is UIElement page && !page.IsKeyboardFocusWithin)
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, new Action(() =>
                {
                    if (IsVisible && ReferenceEquals(PageHost.Content, page) && !page.IsKeyboardFocusWithin)
                        page.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
                }));
        };
        BeginAnimation(HeightProperty, animation);
    }

    /// <summary>
    /// Tints the tabs through resource references rather than local values, so a live system theme change
    /// keeps updating them.
    /// </summary>
    private static void SetTabState(Button tab, bool active)
    {
        tab.SetResourceReference(ForegroundProperty, active ? "Ink" : "Muted");
        tab.FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal;
        if (active) tab.SetResourceReference(BackgroundProperty, "Selected");
        else tab.Background = Brushes.Transparent;
    }

    private void ApplyEmptyState(int rowCount, string query, bool blocked, string status)
    {
        if (rowCount > 0 || _tab == -1 && query.Length == 0) { EmptyState.Visibility = Visibility.Collapsed; return; }
        EmptyState.Visibility = Visibility.Visible;
        EmptyActionButton.Visibility = Visibility.Collapsed;
        if (blocked)
        {
            EmptyTitle.Text = status;
            EmptyDetail.Text = "开启后，复制的内容会显示在这里。";
            return;
        }
        // A query miss must be distinguishable from an empty system history.
        if (query.Length > 0)
        {
            if (_tab == -1 && (_loadingApplications || _applicationError is not null))
            {
                EmptyTitle.Text = _applicationError ?? "正在查找应用…";
                EmptyDetail.Text = _applicationError is null ? "应用列表即将就绪" : "可通过底部“操作”刷新应用列表";
                return;
            }
            EmptyTitle.Text = "没有找到匹配内容";
            EmptyDetail.Text = _tab == -1 ? "换个应用名称，或试试 Clipboard、Snip。" : "换个关键词，或清空搜索查看全部内容。";
            EmptyActionButton.Content = "清空搜索";
            EmptyActionButton.Visibility = Visibility.Visible;
            return;
        }
        if (_tab == 1)
        {
            EmptyTitle.Text = "还没有快捷片段";
            EmptyDetail.Text = "保存常用内容，下次直接粘贴";
            EmptyActionButton.Content = "新建片段";
            EmptyActionButton.Visibility = Visibility.Visible;
            return;
        }
        EmptyTitle.Text = "还没有剪贴板记录";
        EmptyDetail.Text = "复制一段文字或图片后，会显示在这里";
    }

    /// <summary>Shows a failure in the footer and keeps it across the refresh that follows the operation.</summary>
    private void FailStatus(string message)
    {
        _statusOverride = message;
        StatusLabel.Text = message;
    }

    private void FocusSearch()
    {
        if (!IsList) return;
        SearchBox.Focus();
        SearchBox.CaretIndex = SearchBox.Text.Length;
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        if (PageHost is null || Entries is null) return;
        if (!IsList) return;
        CloseMenu();
        _menuId = null;
        RefreshRows(preserveSelection: true);
    }

    private void OnClearSearch(object sender, RoutedEventArgs e) => ClearQuery();

    private void ClearQuery()
    {
        SearchBox.Clear();
        RefreshRows(preserveSelection: true);
        FocusSearch();
    }

    private void OnEmptyAction(object sender, RoutedEventArgs e)
    {
        if (ClipboardSearch.Normalize(SearchBox.Text).Length > 0) ClearQuery();
        else if (_tab == 1) EditSnippet(create: true);
    }

    private void OnSystemSettings(object sender, RoutedEventArgs e) => _app.OpenSystemClipboardSettings();

    private void SwitchTab(int tab)
    {
        if (_busy) return;
        RememberHistorySelection();
        Confirmation.Dismiss();
        CloseMenu();
        _menuId = null;
        _tab = tab;
        _typeFilter = "all";
        TypeFilter.SelectedIndex = 0;
        // Switching tabs drops the query so an empty list is never mistaken for another tab's results.
        if (SearchBox.Text.Length > 0) SearchBox.Clear();
        RefreshRows();
        FocusSearch();
        if (tab == -1) _ = LoadApplicationsAsync();
    }

    internal void OpenSection(int tab) => SwitchTab(tab);
    private void OnHome(object sender, RoutedEventArgs e) => SwitchTab(-1);

    private void RememberHistorySelection()
    { if (_tab == 0 && Entries.SelectedItem is EntryRow row) _historySelection = row.Id; }

    private void OnHistoryTab(object sender, RoutedEventArgs e) => SwitchTab(0);
    private void OnSnippetsTab(object sender, RoutedEventArgs e) => SwitchTab(1);

    private async void OnKeyDown(object sender, KeyEventArgs e)
    {
        // A key the IME consumed (including the Space/Enter that commits a candidate) must never start a
        // paste. WPF reports those as ImeProcessed, so filtering them here covers both arrival paths and
        // needs no cross-event state.
        if (e.Key == Key.ImeProcessed) return;
        if (_settings?.IsRecordingHotkey == true) { _settings.CaptureHotkey(e); return; }
        if (_busy) return;
        if (_menuOpen)
        {
            if (e.Key == Key.Escape) { CloseMenu(); e.Handled = true; }
            // Let the menu's focused control handle navigation and activation; never route to the list.
            return;
        }
        if (e.Key == Key.Escape && _menuId.HasValue && !Confirmation.IsOpen)
        { _menuId = null; RefreshRows(true); FocusSearch(); e.Handled = true; return; }
        if (Confirmation.IsOpen)
        {
            if (e.Key == Key.Escape) { Confirmation.Dismiss(); if (IsList) FocusSearch(); e.Handled = true; }
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
        if (TypeFilter.IsDropDownOpen) return;
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.K)
        { OnMoreMenu(this, new RoutedEventArgs()); e.Handled = true; return; }
        // Selecting/copying preview text must keep the textbox's native navigation.
        if (PreviewText.IsKeyboardFocusWithin && e.Key is not (Key.Escape or Key.Enter)) return;
        // Let focused buttons handle Enter and Space normally.
        if (e.Key == Key.Enter && Keyboard.FocusedElement is Button) return;
        var action = ListKeyRouting.Resolve(e.Key, Keyboard.Modifiers, SearchBox.IsKeyboardFocusWithin,
            ClipboardSearch.Normalize(SearchBox.Text), _tab == 1);
        if (action == ListKeyAction.None) return;
        e.Handled = true;
        await ApplyListActionAsync(action);
    }

    /// <summary>Runs one resolved list-page action; also the entry point used by the behaviour tests.</summary>
    internal async Task ApplyListActionAsync(ListKeyAction action)
    {
        switch (action)
        {
            case ListKeyAction.HistoryTab: SwitchTab(0); break;
            case ListKeyAction.SnippetsTab: SwitchTab(1); break;
            case ListKeyAction.FocusSearch: SearchBox.Focus(); SearchBox.SelectAll(); break;
            case ListKeyAction.ClearQuery: ClearQuery(); break;
            case ListKeyAction.HidePanel:
                if (_tab >= 0) SwitchTab(-1); else Hide();
                break;
            case ListKeyAction.SelectPrevious: MoveSelection(-1); break;
            case ListKeyAction.SelectNext: MoveSelection(1); break;
            case ListKeyAction.PasteSelected: await PasteSelectedAsync(); break;
            case ListKeyAction.EditSnippet: EditSnippet(); break;
            case ListKeyAction.NewSnippet: EditSnippet(create: true); break;
        }
    }

    private void MoveSelection(int delta)
    {
        if (Entries.Items.Count == 0) return;
        Entries.SelectedIndex = Math.Clamp(Entries.SelectedIndex + delta, 0, Entries.Items.Count - 1);
        Entries.ScrollIntoView(Entries.SelectedItem);
    }

    private void OnPanelMouseDown(object sender, MouseButtonEventArgs e)
    {
        _pressedEntryId = null;
        if (!_busy && IsList && !_menuOpen && !Confirmation.IsOpen &&
            e.OriginalSource is DependencyObject source && !FindButton(source) &&
            ItemsControl.ContainerFromElement(Entries, source) is ListBoxItem { DataContext: EntryRow row })
        {
            _pressedEntryId = row.Id;
        }
        else _lastClickedEntryId = null;
    }

    private void OnEntryClick(object sender, MouseButtonEventArgs e)
    {
        var pressedId = _pressedEntryId;
        _pressedEntryId = null;
        if (_busy || !IsList || _menuOpen || Confirmation.IsOpen) return;
        // Buttons, the scrollbar and blank space must never start a paste.
        if (e.OriginalSource is DependencyObject source && FindButton(source)) return;
        if (ItemsControl.ContainerFromElement(Entries, e.OriginalSource as DependencyObject) is not ListBoxItem { DataContext: EntryRow row }) return;
        // A release without an observed press is never a paste, including in synthetic UI tests.
        if (pressedId != row.Id) { _lastClickedEntryId = null; return; }
        Entries.SelectedItem = row; e.Handled = true;
        _lastClickedEntryId = row.Id;
        UpdatePreview();
    }

    private async void OnEntryDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_busy || _menuOpen || Confirmation.IsOpen || !IsList || e.ChangedButton != MouseButton.Left) return;
        if (e.OriginalSource is not DependencyObject source || FindButton(source)) return;
        if (ItemsControl.ContainerFromElement(Entries, source) is not ListBoxItem { DataContext: EntryRow row }) return;
        if (_pressedEntryId != row.Id || _lastClickedEntryId != row.Id) return;
        _pressedEntryId = _lastClickedEntryId = null;
        Entries.SelectedItem = row;
        e.Handled = true;
        await PasteSelectedAsync();
    }

    private async void OnUseSelected(object sender, RoutedEventArgs e) => await PasteSelectedAsync();

    private void OnPinApplication(object sender, RoutedEventArgs e)
    {
        if (_busy || sender is not Button { DataContext: EntryRow { Application: { } application } }) return;
        if (!_app.TryUpdateLibrary(library =>
        {
            var pins = library.State.PinnedApplications;
            if (pins.Contains(application.Id, StringComparer.OrdinalIgnoreCase))
                pins.RemoveAll(id => string.Equals(id, application.Id, StringComparison.OrdinalIgnoreCase));
            else pins.Add(application.Id);
        })) FailStatus("固定应用未能保存，请重试");
        RefreshRows(true);
        FocusSearch();
    }

    private void OnMore(object sender, RoutedEventArgs e)
    {
        if (_busy || _tab == -1 || sender is not Button { DataContext: EntryRow row }) return;
        Entries.SelectedItem = row;
        if (_tab == 1) { EditSnippet(); return; }
        Confirmation.Dismiss();
        _menuId = _menuId == row.Id ? null : row.Id;
        RefreshRows(true);
        FocusSearch();
    }

    private void OnMoreMenu(object sender, RoutedEventArgs e)
    {
        if (_busy || _menuOpen) return;
        Confirmation.Dismiss();
        _menuId = null;
        RefreshRows(true);
        MenuLayer.Visibility = Visibility.Visible;
        _menuOpen = true;
        ApplyPanelLayout(Entries.Items.Count, ClipboardSearch.Normalize(SearchBox.Text));
        if (!(_tab == -1 ? MenuRefreshAppsButton : MenuClearButton).Focus()) MenuLayer.Focus();
    }

    private void OnMenuBackdrop(object sender, MouseButtonEventArgs e)
    {
        CloseMenu();
        e.Handled = true;
    }
    private bool CloseMenu()
    {
        if (!_menuOpen) return false;
        MenuLayer.Visibility = Visibility.Collapsed;
        _menuOpen = false;
        if (IsList) ApplyPanelLayout(Entries.Items.Count, ClipboardSearch.Normalize(SearchBox.Text));
        // The menu button took keyboard focus on click; hand it back so typing keeps reaching the search box.
        FocusSearch();
        return true;
    }

    private void OnDeleteHistory(object sender, RoutedEventArgs e)
    {
        if (_busy || sender is not Button { DataContext: EntryRow row }) return;
        _menuId = null;
        RefreshRows(true);
        Confirmation.Ask(row.IsSnippet ? "删除这个片段？" : "从 Windows 历史删除此项？", "删除", async () =>
        {
            if (_busy) return;
            _busy = true;
            try
            {
                if (row.IsSnippet)
                {
                    if (!_app.TryUpdateLibrary(library => library.State.Snippets.RemoveAll(s => s.Id == row.Id)))
                        FailStatus("删除未能保存，请重试");
                }
                else
                {
                    var deleted = await _app.DeleteSystemHistoryAsync(row.Id);
                    if (!deleted) FailStatus("删除失败，请重试");
                }
                if (_historySelection == row.Id) _historySelection = null;
            }
            finally { _busy = false; }
            // Refresh after the message is set so the failure stays readable in the footer.
            RefreshRows(true);
        });
    }

    private void OnClearHistory(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        CloseMenu();
        _menuId = null;
        RefreshRows(true);
        // Clearing always targets the whole Windows history, even while a query narrows the visible list.
        Confirmation.Ask("清空 Windows 剪贴板历史？\nWin+V 中的固定项会保留。", "清空", async () =>
        {
            if (_busy) return;
            _busy = true;
            try
            {
                var cleared = await _app.ClearSystemHistoryAsync();
                if (!cleared) FailStatus("清空失败，请重试");
            }
            finally { _busy = false; }
            RefreshRows();
        });
    }

    private Task PasteSelectedAsync()
    {
        if (_busy || Entries.SelectedItem is not EntryRow row) return Task.CompletedTask;
        if (row.CommandTab is int section) { SwitchTab(section); return Task.CompletedTask; }
        if (row.Application is { } app)
        {
            if (_catalog.Launch(app)) Hide(); else FailStatus("应用启动失败，请刷新列表后重试");
            return Task.CompletedTask;
        }
        RememberHistorySelection();
        return PasteRowAsync(row, _target);
    }

    private async Task PasteRowAsync(EntryRow row, PasteTarget? target)
    {
        if (_busy) return;
        var value = _tab == 0 ? _app.History.FirstOrDefault(c => c.Id == row.Id)?.Text
            : _app.Library.State.Snippets.FirstOrDefault(s => s.Id == row.Id)?.Value;
        // Image history rows carry no text; Windows restores their original content by id.
        if (value is null && !row.IsImage) return;
        _busy = true;
        try
        {
            var copied = _tab == 0
                ? await _app.RestoreHistoryItemAsync(row.Id)
                : await _app.ClipboardService.WriteAsync(value!, row.Sensitive);
            if (!copied) { FailStatus(_tab == 0 ? "历史记录已失效或剪贴板忙" : "剪贴板忙，请重试"); return; }
            Hide();
            if (!await PasteService.PasteAsync(target)) _app.Notify("已复制 · Ctrl+V 粘贴");
        }
        finally { _busy = false; }
    }

    private void OnNew(object sender, RoutedEventArgs e) => EditSnippet(create: true);

    private void OnEdit(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: EntryRow row }) Entries.SelectedItem = row;
        EditSnippet();
    }

    private void EditSnippet(bool create = false)
    {
        if (!IsList || _busy) return;
        var selected = Entries.SelectedItem as EntryRow;
        var snippet = create ? null : _app.Library.State.Snippets.FirstOrDefault(s => s.Id == selected?.Id);
        if (!create && snippet is null) return;
        RememberHistorySelection();
        CaptureListContext();
        _editor = new SnippetView(_app, snippet);
        _editor.Finished += ClosePage;
        ShowPage(_editor, _editor.Heading);
    }

    private void OnSettings(object sender, RoutedEventArgs e) => ShowSettings();

    internal void ShowSettings()
    {
        if (!IsList || _busy) return;
        RememberHistorySelection();
        CaptureListContext();
        _settings = new SettingsView(_app);
        _settings.Finished += ClosePage;
        ShowPage(_settings, "设置");
    }

    private void CaptureListContext()
    {
        CloseMenu();
        _listContext = (_tab, ClipboardSearch.Normalize(SearchBox.Text), (Entries.SelectedItem as EntryRow)?.Id ?? _historySelection);
    }

    private void ShowPage(UserControl page, string title)
    {
        Confirmation.Dismiss();
        CloseMenu();
        ListPage.Visibility = Visibility.Collapsed;
        PageHost.Visibility = Visibility.Visible;
        PageHost.Content = page;
        SetPanelHeight(ExpandedHeight);
        PageTitle.Text = title;
        SearchHeader.Visibility = TypeFilter.Visibility = ScopeLabel.Visibility = Visibility.Collapsed;
        PageTitle.Visibility = Visibility.Visible;
        BrandIcon.Visibility = SettingsButton.Visibility = Visibility.Collapsed;
        BackButton.Visibility = Visibility.Visible;
        page.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
    }

    private void OnBack(object sender, RoutedEventArgs e)
    { if (IsList) SwitchTab(-1); else ClosePage(); }

    private void ClosePage()
    {
        _editor?.Discard(); _settings?.Discard();
        _editor = null; _settings = null;
        PageHost.Content = null;
        PageHost.Visibility = Visibility.Collapsed;
        ListPage.Visibility = Visibility.Visible;
        PageTitle.Text = "ClipTap";
        BrandIcon.Visibility = SettingsButton.Visibility = Visibility.Visible;
        BackButton.Visibility = Visibility.Collapsed;
        // Restore the exact list context the page was opened from.
        if (_listContext is { } context)
        {
            _listContext = null;
            _tab = context.Tab;
            if (SearchBox.Text != context.Query) SearchBox.Text = context.Query;
            if (context.Tab == 0 && context.Selection.HasValue) _historySelection = context.Selection;
        }
        RefreshRows(preserveSelection: true);
        FocusSearch();
    }

    internal void ConfirmExit(Action exit) => Confirmation.Ask("更改尚未保存，仍要退出？", "退出", () => { exit(); return Task.CompletedTask; });

    private void OnDeactivated(object? sender, EventArgs e)
    {
        _pressedEntryId = null;
        _lastClickedEntryId = null;
        RememberHistorySelection();
        _settings?.StopRecording();
        if (!_busy) { Confirmation.Dismiss(); CloseMenu(); Hide(); }
    }

    private void OnClosing(object? sender, CancelEventArgs e) { if (!_app.IsQuitting) { e.Cancel = true; Hide(); } }

    private void OnHeaderDrag(object sender, MouseButtonEventArgs e)
    {
        if (_busy || e.ChangedButton != MouseButton.Left || e.LeftButton != MouseButtonState.Pressed) return;
        if (SearchBox.IsMouseOver || TypeFilter.IsMouseOver) return;
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

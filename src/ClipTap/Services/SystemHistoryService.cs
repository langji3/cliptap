using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Windows.Storage.Streams;
using ClipTap.Core;
using Windows.ApplicationModel.DataTransfer;
using SystemClipboard = Windows.ApplicationModel.DataTransfer.Clipboard;

namespace ClipTap.Services;

internal enum HistoryStatus { Ready, Loading, Disabled, AccessDenied, Unavailable }
internal sealed record HistoryEntry(Guid Id, string Text, DateTimeOffset CopiedAt, bool IsImage = false, BitmapSource? Thumbnail = null)
{
    public static implicit operator HistoryEntry(ClipEntry entry) => new(entry.Id, entry.Text, entry.CopiedAt);
}
internal sealed record HistorySnapshot(HistoryStatus Status, IReadOnlyList<HistoryEntry> Items);

internal interface ISystemHistorySource : IDisposable
{
    event Action? Changed;
    Task<HistorySnapshot> ReadAsync();
    bool Clear();
    Task<bool> RestoreImageAsync(Guid id);
}

internal sealed class WindowsHistorySource : ISystemHistorySource
{
    private bool _historySubscribed, _enabledSubscribed;
    public event Action? Changed;
    public WindowsHistorySource()
    {
        // Event registration is optional: opening the panel still performs a fresh read.
        try { SystemClipboard.HistoryChanged += OnChanged; _historySubscribed = true; }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or InvalidOperationException) { }
        try { SystemClipboard.HistoryEnabledChanged += OnChanged; _enabledSubscribed = true; }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or InvalidOperationException) { }
    }
    private void OnChanged(object? sender, object args) => Changed?.Invoke();

    public async Task<HistorySnapshot> ReadAsync()
    {
        var result = await SystemClipboard.GetHistoryItemsAsync();
        if (result.Status != ClipboardHistoryItemsResultStatus.Success)
            return new(result.Status == ClipboardHistoryItemsResultStatus.ClipboardHistoryDisabled
                ? HistoryStatus.Disabled : HistoryStatus.AccessDenied, []);
        var entries = new List<HistoryEntry>();
        var previewBudget = System.Diagnostics.Stopwatch.StartNew();
        foreach (var item in result.Items)
        {
            try
            {
                var id = Identity(item.Id);
                if (item.Content.Contains(StandardDataFormats.Bitmap))
                {
                    BitmapSource? thumbnail = null;
                    if (previewBudget.Elapsed < TimeSpan.FromSeconds(2))
                    {
                        try { thumbnail = await ReadThumbnailAsync(item).WaitAsync(TimeSpan.FromMilliseconds(250)); }
                        catch (Exception ex) when (ex is COMException or IOException or InvalidOperationException or ArgumentException or NotSupportedException or TimeoutException or UnauthorizedAccessException) { }
                    }
                    entries.Add(new(id, "", item.Timestamp, true, thumbnail));
                    continue;
                }
                if (!item.Content.Contains(StandardDataFormats.Text)) continue;
                var text = await item.Content.GetTextAsync();
                if (string.IsNullOrEmpty(text)) continue;
                // Windows IDs are strings; derive a stable UI identity without retaining native data objects.
                entries.Add(new(id, text, item.Timestamp));
            }
            catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or InvalidOperationException)
            { /* An individual item can disappear while the snapshot is being read. */ }
        }
        return new(HistoryStatus.Ready, entries);
    }
    private static Guid Identity(string id) => new(SHA256.HashData(Encoding.UTF8.GetBytes(id)).AsSpan(0, 16));
    private static async Task<BitmapSource> ReadThumbnailAsync(ClipboardHistoryItem item)
    {
        var reference = await item.Content.GetBitmapAsync();
        using var stream = await reference.OpenReadAsync();
        return await DecodeThumbnailAsync(stream);
    }
    internal static async Task<BitmapSource> DecodeThumbnailAsync(IRandomAccessStream stream)
    {
        var decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(stream);
        var scale = Math.Min(1, Math.Min(160.0 / decoder.PixelWidth, 96.0 / decoder.PixelHeight));
        var width = Math.Max(1u, (uint)(decoder.PixelWidth * scale));
        var height = Math.Max(1u, (uint)(decoder.PixelHeight * scale));
        var transform = new Windows.Graphics.Imaging.BitmapTransform { ScaledWidth = width, ScaledHeight = height };
        var pixels = await decoder.GetPixelDataAsync(Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8,
            Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied, transform,
            Windows.Graphics.Imaging.ExifOrientationMode.IgnoreExifOrientation, Windows.Graphics.Imaging.ColorManagementMode.DoNotColorManage);
        var bitmap = BitmapSource.Create((int)width, (int)height, 96, 96, PixelFormats.Pbgra32, null, pixels.DetachPixelData(), (int)width * 4);
        bitmap.Freeze();
        return bitmap;
    }
    public async Task<bool> RestoreImageAsync(Guid id)
    {
        // Re-query before writing: a cached thumbnail must never resurrect an item deleted in Win+V.
        var result = await SystemClipboard.GetHistoryItemsAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        if (result.Status != ClipboardHistoryItemsResultStatus.Success) return false;
        var item = result.Items.FirstOrDefault(i => Identity(i.Id) == id);
        return item is not null && item.Content.Contains(StandardDataFormats.Bitmap)
            && SystemClipboard.SetHistoryItemAsContent(item) == SetHistoryItemAsContentStatus.Success;
    }
    public bool Clear() => SystemClipboard.ClearHistory();
    public void Dispose()
    {
        if (_historySubscribed) SystemClipboard.HistoryChanged -= OnChanged;
        if (_enabledSubscribed) SystemClipboard.HistoryEnabledChanged -= OnChanged;
    }
}

// Used on the UI dispatcher. Native event callbacks are dispatched by App before invalidating.
internal sealed class SystemHistoryService(ISystemHistorySource source) : IDisposable
{
    private long _revision;
    private bool _disposed;
    internal HistorySnapshot Snapshot { get; private set; } = new(HistoryStatus.Loading, []);
    internal event Action? Updated;
    internal void Invalidate()
    {
        ++_revision;
        Snapshot = new(HistoryStatus.Loading, []);
        Updated?.Invoke();
    }
    internal async Task RefreshAsync()
    {
        if (_disposed) return;
        var revision = ++_revision;
        HistorySnapshot snapshot;
        try { snapshot = await source.ReadAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or InvalidOperationException or TimeoutException or OperationCanceledException)
        { snapshot = new(HistoryStatus.Unavailable, []); }
        if (_disposed || revision != _revision) return;
        Snapshot = snapshot;
        Updated?.Invoke();
    }
    internal async Task<bool> ClearAsync()
    {
        bool cleared;
        try { cleared = source.Clear(); }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or InvalidOperationException)
        { cleared = false; }
        Invalidate();
        await RefreshAsync();
        return cleared;
    }
    internal async Task<bool> RestoreImageAsync(Guid id)
    {
        if (_disposed || !Snapshot.Items.Any(i => i.Id == id && i.IsImage)) return false;
        try { return await source.RestoreImageAsync(id); }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or InvalidOperationException or TimeoutException or OperationCanceledException)
        { return false; }
    }
    public void Dispose() { _disposed = true; ++_revision; source.Dispose(); }
}

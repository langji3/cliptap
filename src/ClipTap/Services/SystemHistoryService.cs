using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using ClipTap.Core;
using Windows.ApplicationModel.DataTransfer;
using SystemClipboard = Windows.ApplicationModel.DataTransfer.Clipboard;

namespace ClipTap.Services;

internal enum HistoryStatus { Ready, Loading, Disabled, AccessDenied, Unavailable }
internal sealed record HistorySnapshot(HistoryStatus Status, IReadOnlyList<ClipEntry> Items);

internal interface ISystemHistorySource : IDisposable
{
    event Action? Changed;
    Task<HistorySnapshot> ReadAsync();
    bool Clear();
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
        var entries = new List<ClipEntry>();
        foreach (var item in result.Items)
        {
            try
            {
                if (!item.Content.Contains(StandardDataFormats.Text)) continue;
                var text = await item.Content.GetTextAsync();
                if (string.IsNullOrEmpty(text)) continue;
                // Windows IDs are strings; derive a stable UI identity without retaining native data objects.
                var id = new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(item.Id)).AsSpan(0, 16));
                entries.Add(new(id, text, item.Timestamp));
            }
            catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or InvalidOperationException)
            { /* An individual item can disappear while the snapshot is being read. */ }
        }
        return new(HistoryStatus.Ready, entries);
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
    public void Dispose() { _disposed = true; ++_revision; source.Dispose(); }
}

using System.Runtime.InteropServices;
using System.Windows.Threading;
using ClipTap.Core;

namespace ClipTap.Services;

internal sealed class ClipboardService : IDisposable
{
    private const string SourceFormat = "ClipTap.Source";
    private const string ExcludeFormat = "ExcludeClipboardContentFromMonitorProcessing";
    private const string HistoryFormat = "CanIncludeInClipboardHistory";
    private readonly DispatcherTimer _readTimer;
    private readonly DispatcherTimer _clearTimer;
    private int _attempts;
    private uint? _secretSequence;
    public event Action<string>? TextCaptured;

    public ClipboardService()
    {
        _readTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(90) };
        _readTimer.Tick += (_, _) => Read();
        _clearTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _clearTimer.Tick += (_, _) => ClearOwnedSecret();
    }

    public void OnChanged()
    {
        _attempts = 0;
        _readTimer.Stop();
        _readTimer.Start();
    }

    private void Read()
    {
        _readTimer.Stop();
        try
        {
            var data = Clipboard.GetDataObject();
            if (data is null || data.GetDataPresent(SourceFormat) || data.GetDataPresent(ExcludeFormat)) return;
            if (data.GetDataPresent(HistoryFormat) && IsZero(data.GetData(HistoryFormat))) return;
            if (data.GetDataPresent(DataFormats.UnicodeText) && data.GetData(DataFormats.UnicodeText) is string text
                && text.Length <= Library.MaxTextLength)
                TextCaptured?.Invoke(text);
        }
        catch (COMException) { if (++_attempts < 5) _readTimer.Start(); }
    }

    private static bool IsZero(object? value) => value switch
    {
        MemoryStream stream => stream.ToArray() is var bytes && bytes.Length >= 4 && BitConverter.ToInt32(bytes) == 0,
        int number => number == 0,
        _ => false
    };

    public async Task<bool> WriteAsync(string text, bool sensitive)
    {
        for (var i = 0; i < 5; i++)
        {
            try
            {
                var data = new DataObject();
                data.SetText(text, TextDataFormat.UnicodeText);
                var writeToken = Guid.NewGuid().ToString("N");
                data.SetData(SourceFormat, writeToken);
                if (sensitive)
                {
                    data.SetData(ExcludeFormat, new MemoryStream(BitConverter.GetBytes(1)));
                    data.SetData(HistoryFormat, new MemoryStream(BitConverter.GetBytes(0)));
                    data.SetData("CanUploadToCloudClipboard", new MemoryStream(BitConverter.GetBytes(0)));
                }
                Clipboard.SetDataObject(data, copy: true);
                _clearTimer.Stop();
                _clearTimer.Interval = TimeSpan.FromSeconds(30);
                _secretSequence = null;
                if (sensitive)
                {
                    var sequence = NativeMethods.GetClipboardSequenceNumber();
                    if (sequence != 0 && Equals(Clipboard.GetData(SourceFormat), writeToken)
                        && sequence == NativeMethods.GetClipboardSequenceNumber())
                    { _secretSequence = sequence; _clearTimer.Start(); }
                }
                return true;
            }
            catch (COMException) { await Task.Delay(60); }
        }
        return false;
    }

    private void ClearOwnedSecret()
    {
        _clearTimer.Stop();
        if (_secretSequence is > 0)
        {
            // Hold the clipboard lock across comparison and clearing, so a later copy cannot be erased.
            if (!NativeMethods.OpenClipboard(nint.Zero))
            { _clearTimer.Interval = TimeSpan.FromSeconds(2); _clearTimer.Start(); return; }
            try
            {
                if (_secretSequence == NativeMethods.GetClipboardSequenceNumber() && !NativeMethods.EmptyClipboard())
                { _clearTimer.Interval = TimeSpan.FromSeconds(2); _clearTimer.Start(); return; }
            }
            finally { NativeMethods.CloseClipboard(); }
        }
        _secretSequence = null;
        _clearTimer.Interval = TimeSpan.FromSeconds(30);
    }

    public void Dispose() { _readTimer.Stop(); ClearOwnedSecret(); _clearTimer.Stop(); }
}

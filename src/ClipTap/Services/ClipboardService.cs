using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace ClipTap.Services;

internal sealed class ClipboardService : IDisposable
{
    private const string SourceFormat = "ClipTap.Source";
    private const string ExcludeFormat = "ExcludeClipboardContentFromMonitorProcessing";
    private const string HistoryFormat = "CanIncludeInClipboardHistory";
    private readonly DispatcherTimer _clearTimer;
    private uint? _secretSequence;

    public ClipboardService()
    {
        _clearTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _clearTimer.Tick += (_, _) => ClearOwnedSecret();
    }

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

    public void Dispose() { ClearOwnedSecret(); _clearTimer.Stop(); }
}

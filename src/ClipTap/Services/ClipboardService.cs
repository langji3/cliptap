using System.Runtime.InteropServices;

namespace ClipTap.Services;

internal interface IClipboardWriter
{
    Task<bool> WriteAsync(string text, bool sensitive);
}

internal sealed class ClipboardService : IClipboardWriter
{
    private const string SourceFormat = "ClipTap.Source";
    private const string ExcludeFormat = "ExcludeClipboardContentFromMonitorProcessing";
    private const string HistoryFormat = "CanIncludeInClipboardHistory";

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
                return true;
            }
            catch (COMException) { await Task.Delay(60); }
        }
        return false;
    }

}

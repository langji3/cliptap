using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace ClipTap.Services;

internal sealed class DesktopEvents : NativeWindow, IDisposable
{
    private readonly Action _hotkey;
    private readonly Action _clipboard;
    internal bool HotkeyAvailable { get; }

    internal DesktopEvents(Action hotkey, Action clipboard)
    {
        _hotkey = hotkey;
        _clipboard = clipboard;
        // A message-only window receives events without creating the WPF panel or a rendering surface.
        CreateHandle(new CreateParams { Caption = "ClipTap events", Parent = new nint(-3) });
        if (!NativeMethods.AddClipboardFormatListener(Handle))
        {
            var error = Marshal.GetLastWin32Error();
            DestroyHandle();
            throw new Win32Exception(error);
        }
        HotkeyAvailable = NativeMethods.RegisterHotKey(Handle, NativeMethods.HotkeyId, 0x4003, 0x56);
    }

    protected override void WndProc(ref Message message)
    {
        if (message.Msg == NativeMethods.WmClipboardUpdate) _clipboard();
        if (message.Msg == NativeMethods.WmHotkey && message.WParam == NativeMethods.HotkeyId)
        { _hotkey(); return; }
        base.WndProc(ref message);
    }

    public void Dispose()
    {
        NativeMethods.RemoveClipboardFormatListener(Handle);
        if (HotkeyAvailable) NativeMethods.UnregisterHotKey(Handle, NativeMethods.HotkeyId);
        DestroyHandle();
    }
}

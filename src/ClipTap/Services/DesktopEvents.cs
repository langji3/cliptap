using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace ClipTap.Services;

internal sealed class DesktopEvents : NativeWindow, IDisposable
{
    private readonly Action _hotkey;
    private readonly PriorityHotkey _priority;
    private readonly bool _registered;
    internal bool HotkeyAvailable => _registered || _priority.Available;

    internal DesktopEvents(Action hotkey)
    {
        _hotkey = hotkey;
        // A message-only window receives events without creating the WPF panel or a rendering surface.
        CreateHandle(new CreateParams { Caption = "ClipTap events", Parent = new nint(-3) });
        // MOD_ALT | MOD_NOREPEAT, VK_SPACE.
        _registered = NativeMethods.RegisterHotKey(Handle, NativeMethods.HotkeyId, 0x4001, 0x20);
        _priority = new PriorityHotkey(Handle);
    }

    protected override void WndProc(ref Message message)
    {
        if (message.Msg == PriorityHotkey.Message ||
            message.Msg == NativeMethods.WmHotkey && message.WParam == NativeMethods.HotkeyId)
        { _hotkey(); return; }
        base.WndProc(ref message);
    }

    public void Dispose()
    {
        _priority.Dispose();
        if (_registered) NativeMethods.UnregisterHotKey(Handle, NativeMethods.HotkeyId);
        DestroyHandle();
    }
}

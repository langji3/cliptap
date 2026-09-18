using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using ClipTap.Core;

namespace ClipTap.Services;

internal sealed class DesktopEvents : NativeWindow, IDisposable
{
    private readonly Action _hotkey;
    private PriorityHotkey? _priority;
    private bool _registered;
    private int _id = NativeMethods.HotkeyId;
    private WakeHotkey _current;
    private Action<WakeHotkey>? _record;
    internal bool HotkeyAvailable => _registered || _priority?.Available == true;

    internal DesktopEvents(Action hotkey, WakeHotkey? shortcut = null)
    {
        _hotkey = hotkey;
        _current = shortcut ?? WakeHotkey.Default;
        // A message-only window receives events without creating the WPF panel or a rendering surface.
        CreateHandle(new CreateParams { Caption = "ClipTap events", Parent = new nint(-3) });
        // MOD_ALT | MOD_NOREPEAT, VK_SPACE.
        _registered = NativeMethods.RegisterHotKey(Handle, _id, 0x4000 | _current.Modifiers, _current.Key);
        if (_current == WakeHotkey.Default) _priority = new PriorityHotkey(Handle);
    }

    internal void Record(Action<WakeHotkey>? callback)
    {
        _record = callback;
        if (_priority is not null) _priority.Suspended = callback is not null;
    }

    internal bool TryChange(WakeHotkey shortcut, Func<bool> save, out string error)
    {
        error = "";
        if (!shortcut.IsValid) { error = "请选择 Ctrl 或 Alt 加字母、数字、空格或 F1–F11"; return false; }
        if (shortcut == _current)
        {
            var saved = save();
            if (!saved) error = "保存失败，请重试";
            return saved;
        }
        var nextId = _id == NativeMethods.HotkeyId ? _id + 1 : NativeMethods.HotkeyId;
        if (!NativeMethods.RegisterHotKey(Handle, nextId, shortcut.Modifiers | 0x4000, shortcut.Key))
        { error = "该快捷键已被占用或由系统保留，请换一个"; return false; }
        var committed = false;
        try
        {
            if (!save()) { error = "保存失败，原快捷键仍有效"; return false; }
            if (_registered) NativeMethods.UnregisterHotKey(Handle, _id);
            _priority?.Dispose(); _priority = null;
            _id = nextId; _registered = true; _current = shortcut;
            if (_current == WakeHotkey.Default) _priority = new PriorityHotkey(Handle);
            committed = true;
            return true;
        }
        finally { if (!committed) NativeMethods.UnregisterHotKey(Handle, nextId); }
    }

    protected override void WndProc(ref Message message)
    {
        if (message.Msg == PriorityHotkey.Message && _priority is not null ||
            message.Msg == NativeMethods.WmHotkey && message.WParam == _id)
        { if (_record is not null) _record(_current); else _hotkey(); return; }
        base.WndProc(ref message);
    }

    public void Dispose()
    {
        _priority?.Dispose();
        if (_registered) NativeMethods.UnregisterHotKey(Handle, _id);
        DestroyHandle();
    }
}

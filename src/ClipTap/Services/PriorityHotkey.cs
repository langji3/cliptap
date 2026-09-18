using System.Runtime.InteropServices;
using Forms = System.Windows.Forms;

namespace ClipTap.Services;

internal readonly record struct HotkeyDecision(bool Suppress, bool MaskMenu, bool Invoke);

internal sealed class HotkeyGesture
{
    private bool _space, _pending;
    internal HotkeyDecision Feed(uint key, bool down, bool alt, bool otherModifier)
    {
        var mask = false;
        var suppress = key == 0x20 && _space;
        if (key == 0x20 && down && !_space && alt && !otherModifier)
        { _space = _pending = suppress = mask = true; }
        if (key == 0x20 && !down) _space = false;
        if (down && key is not (0x20 or 0x12 or 0xA4 or 0xA5)) _pending = false;
        var invoke = _pending && !_space && !alt;
        if (invoke) _pending = false;
        return new(suppress, mask, invoke);
    }
}

internal sealed class PriorityHotkey : IDisposable
{
    private readonly nint _window;
    private readonly Thread _thread;
    private readonly Hook _callback;
    private readonly ManualResetEventSlim _ready = new();
    private readonly HotkeyGesture _gesture = new();
    private volatile bool _stop;
    internal bool Available { get; private set; }
    internal const int Message = 0x8000 + 71;

    internal PriorityHotkey(nint window)
    {
        _window = window;
        _callback = OnKeyboard;
        _thread = new Thread(Run) { IsBackground = true, Name = "ClipTap hotkey" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        if (!_ready.Wait(TimeSpan.FromSeconds(3))) _stop = true;
    }

    private void Run()
    {
        var hook = SetWindowsHookEx(13, _callback, GetModuleHandle(null), 0);
        Available = hook != 0;
        _ready.Set();
        try
        {
            if (!Available || _stop) return;
            using var timer = new Forms.Timer { Interval = 100 };
            timer.Tick += (_, _) => { if (_stop) Forms.Application.ExitThread(); };
            timer.Start();
            Forms.Application.Run();
        }
        finally { if (hook != 0) UnhookWindowsHookEx(hook); Available = false; }
    }

    private nint OnKeyboard(int code, nuint message, nint data)
    {
        if (code < 0 || _stop) return CallNextHookEx(0, code, message, data);
        var key = Marshal.PtrToStructure<KeyboardEvent>(data);
        if ((key.Flags & 0x10) != 0) return CallNextHookEx(0, code, message, data);
        var down = message is 0x100 or 0x104;
        bool Held(int value) => (NativeMethods.GetAsyncKeyState(value) & 0x8000) != 0;
        // Async key state still reflects the previous event inside a low-level hook.
        var alt = key.Key switch
        {
            0xA4 => down || Held(0xA5),
            0xA5 => down || Held(0xA4),
            0x12 => down,
            _ => (key.Flags & 0x20) != 0
        };
        var result = _gesture.Feed(key.Key, down, alt, Held(0x11) || Held(0x10) || Held(0x5B) || Held(0x5C));
        if (result.MaskMenu)
        {
            // Mark Alt as used without changing Ctrl/Shift state or sending a character.
            var mask = new[] { NativeMethods.Key(0xE8), NativeMethods.Key(0xE8, true) };
            NativeMethods.SendInput(2, mask, Marshal.SizeOf<NativeMethods.Input>());
        }
        if (result.Invoke) PostMessage(_window, Message, 0, 0);
        return result.Suppress ? 1 : CallNextHookEx(0, code, message, data);
    }

    public void Dispose() { _stop = true; _thread.Join(1500); }

    private delegate nint Hook(int code, nuint message, nint data);
    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardEvent { public uint Key, Scan, Flags, Time; public nuint Extra; }
    [DllImport("user32.dll")] private static extern nint SetWindowsHookEx(int id, Hook callback, nint module, uint thread);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll")] private static extern nint CallNextHookEx(nint hook, int code, nuint message, nint data);
    [DllImport("user32.dll")] private static extern bool PostMessage(nint window, int message, nint wParam, nint lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandle(string? name);
}

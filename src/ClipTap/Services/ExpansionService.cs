using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using ClipTap.Core;
using Forms = System.Windows.Forms;

namespace ClipTap.Services;

// Dedicated message pump: disk/UI work must never delay the low-level hooks.
internal sealed class ExpansionService : IDisposable
{
    private readonly Thread _thread;
    private readonly Action _failed;
    private readonly ExpansionMatcher _matcher = new();
    private readonly Hook _keyboardCallback, _mouseCallback;
    private readonly WinEvent _focusCallback;
    private readonly ManualResetEventSlim _ready = new();
    private readonly nuint _testInputTag;
    private Snippet[] _configuration = [];
    private Snippet[]? _applied;
    private volatile bool _stopping, _paused;
    private nint _keyboard, _mouse, _foregroundEvent, _focusEvent;
    private PasteTarget? _target;
    private nint _layout;
    private int _resetVersion, _appliedResetVersion;
    private readonly HashSet<uint> _pressed = [];
    private bool _caps;
    internal const nuint OutputTag = 0x43544558;
    internal bool Available { get; private set; }
    internal bool Paused { get => _paused; set { _paused = value; Interlocked.Increment(ref _resetVersion); } }

    internal ExpansionService(Action failed, nuint testInputTag = 0)
    {
        _failed = failed; _testInputTag = testInputTag;
        _keyboardCallback = OnKeyboard; _mouseCallback = OnMouse;
        _focusCallback = (_, _, _, _, _, _, _) => Reset();
        _thread = new Thread(Run) { IsBackground = true, Name = "ClipTap expansion" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        if (!_ready.Wait(TimeSpan.FromSeconds(3))) { _stopping = true; return; }
    }

    internal void Configure(IEnumerable<Snippet> snippets) => Volatile.Write(ref _configuration, snippets.ToArray());

    private void Run()
    {
        try
        {
            _caps = (GetKeyState(0x14) & 1) != 0;
            _keyboard = SetWindowsHookEx(13, _keyboardCallback, GetModuleHandle(null), 0);
            _mouse = SetWindowsHookEx(14, _mouseCallback, GetModuleHandle(null), 0);
            _foregroundEvent = SetWinEventHook(3, 3, 0, _focusCallback, 0, 0, 0);
            _focusEvent = SetWinEventHook(0x8005, 0x8005, 0, _focusCallback, 0, 0, 0);
            Available = _keyboard != 0 && _mouse != 0 && _foregroundEvent != 0 && _focusEvent != 0;
            _ready.Set();
            if (!Available || _stopping) return;
            using var timer = new Forms.Timer { Interval = 100 };
            timer.Tick += (_, _) => { if (_stopping) Forms.Application.ExitThread(); if (_paused) Reset(); };
            timer.Start();
            Forms.Application.Run();
        }
        finally
        {
            if (_keyboard != 0) UnhookWindowsHookEx(_keyboard);
            if (_mouse != 0) UnhookWindowsHookEx(_mouse);
            if (_foregroundEvent != 0) UnhookWinEvent(_foregroundEvent);
            if (_focusEvent != 0) UnhookWinEvent(_focusEvent);
            Available = false;
            _ready.Set();
        }
    }

    private void Reset() { _matcher.Reset(); _target = null; _layout = 0; }

    private void Fail()
    {
        _paused = true;
        try { _failed(); }
        catch (InvalidOperationException) { } // Dispatcher may already be shutting down.
    }

    private nint OnMouse(int code, nuint message, nint data)
    {
        if (code >= 0 && message != 0x200) Reset(); // clicks, wheels, button release
        return CallNextHookEx(0, code, message, data);
    }

    private nint OnKeyboard(int code, nuint message, nint data)
    {
        if (code < 0) return CallNextHookEx(0, code, message, data);
        try
        {
            var key = Marshal.PtrToStructure<KeyboardEvent>(data);
            if ((key.Flags & 0x10) != 0)
            {
                if (key.Extra == OutputTag) return CallNextHookEx(0, code, message, data);
                if (_testInputTag == 0 || key.Extra != _testInputTag)
                { Reset(); return CallNextHookEx(0, code, message, data); }
            }
            var down = message is 0x100 or 0x104;
            if (!down) { _pressed.Remove(key.Key); return CallNextHookEx(0, code, message, data); }
            var repeated = !_pressed.Add(key.Key);
            if (key.Key == 0x14 && !repeated) _caps = !_caps;
            if (key.Key is 0x10 or 0xA0 or 0xA1) return CallNextHookEx(0, code, message, data);

            var resetVersion = Volatile.Read(ref _resetVersion);
            if (resetVersion != _appliedResetVersion) { Reset(); _appliedResetVersion = resetVersion; }

            var configuration = Volatile.Read(ref _configuration);
            if (!ReferenceEquals(configuration, _applied))
            { _matcher.Configure(configuration); _applied = configuration; Reset(); }
            if (_paused || _stopping || (repeated && key.Key != 8) || configuration.Length == 0 ||
                (key.Key < 0x20 && key.Key != 8) || key.Key == 0x7F || key.Key is >= 0x21 and <= 0x2F ||
                key.Key is >= 0x70 and <= 0x87 ||
                new[] { 0x11, 0x12, 0x5B, 0x5C, 0x01, 0x02, 0x04, 0x05, 0x06 }.Any(Held) ||
                key.Key is 0x11 or 0x12 or 0x5B or 0x5C or >= 0xA2 and <= 0xA5)
            { Reset(); return CallNextHookEx(0, code, message, data); }

            var target = PasteService.CaptureTarget();
            if (target is null)
            { Reset(); return CallNextHookEx(0, code, message, data); }
            var thread = NativeMethods.GetWindowThreadProcessId(target.Window, out _);
            var layout = GetKeyboardLayout(thread);
            if (!IsDirectInput(target.Focus, layout))
            { Reset(); return CallNextHookEx(0, code, message, data); }
            if (target != _target || layout != _layout) { Reset(); _target = target; _layout = layout; }
            var shift = Held(0x10) || _pressed.Contains(0xA0) || _pressed.Contains(0xA1);
            if (key.Key == 8)
            {
                if (shift) Reset(); else _matcher.Backspace(Environment.TickCount64);
                return CallNextHookEx(0, code, message, data); // The target still performs the deletion.
            }
            var state = new byte[256];
            state[0x10] = shift ? (byte)0x80 : (byte)0;
            state[0x14] = _caps ? (byte)1 : (byte)0;
            state[key.Key] |= 0x80;
            var text = new StringBuilder(8);
            // Flag 4 avoids mutating the foreground layout's dead-key state.
            if (ToUnicodeEx(key.Key, key.Scan, state, text, text.Capacity, 4, layout) != 1)
            { Reset(); return CallNextHookEx(0, code, message, data); }
            var match = _matcher.Feed(text[0], Environment.TickCount64);
            if (match is null) return CallNextHookEx(0, code, message, data);
            // Never delete under Shift (selection semantics), or after target changed.
            if (shift || target != PasteService.CaptureTarget()) { Reset(); return CallNextHookEx(0, code, message, data); }
            var inputs = BuildInputs(match);
            Reset();
            var sent = NativeMethods.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeMethods.Input>());
            if (sent == inputs.Length) return 1; // final physical character was not yet delivered
            ReleasePartialInput(inputs, sent);
            Fail(); // Never retry/roll back into an unknown caret position.
            return sent > 0 ? 1 : CallNextHookEx(0, code, message, data);
        }
        catch (Exception ex) when (ex is Win32Exception or ArgumentException or InvalidOperationException)
        { Reset(); Fail(); }
        return CallNextHookEx(0, code, message, data);
    }

    internal static NativeMethods.Input[] BuildInputs(Snippet snippet)
    {
        if (!ExpansionMatcher.IsValidTrigger(snippet.Trigger) || !ExpansionMatcher.CanExpand(snippet.Value))
            throw new ArgumentException("Invalid expansion");
        var inputs = new List<NativeMethods.Input>();
        for (var i = 1; i < snippet.Trigger!.Length; i++)
        { inputs.Add(NativeMethods.Key(8)); inputs.Add(NativeMethods.Key(8, true)); }
        foreach (var character in snippet.Value)
        {
            inputs.Add(new() { Type = 1, Data = new() { Keyboard = new() { Scan = character, Flags = 4 } } });
            inputs.Add(new() { Type = 1, Data = new() { Keyboard = new() { Scan = character, Flags = 6 } } });
        }
        var result = inputs.ToArray();
        for (var i = 0; i < result.Length; i++) result[i].Data.Keyboard.Extra = OutputTag;
        return result;
    }

    private static bool Held(int key) => (NativeMethods.GetAsyncKeyState(key) & 0x8000) != 0;
    internal static NativeMethods.Input? PartialRelease(NativeMethods.Input[] inputs, uint sent)
    {
        if (sent == 0 || sent >= inputs.Length || (inputs[sent - 1].Data.Keyboard.Flags & 2) != 0) return null;
        var release = inputs[sent - 1]; release.Data.Keyboard.Flags |= 2;
        return release;
    }

    private static void ReleasePartialInput(NativeMethods.Input[] inputs, uint sent)
    {
        if (PartialRelease(inputs, sent) is { } release)
            NativeMethods.SendInput(1, [release], Marshal.SizeOf<NativeMethods.Input>());
    }

    private static bool IsDirectInput(nint window, nint layout)
    {
        // IMM contexts are process-local. Query the target thread's IME window instead.
        var ime = ImmGetDefaultIMEWnd(window);
        var language = (long)layout & 0x3FF;
        if (ime == 0) return language is not (0x04 or 0x11 or 0x12); // unknown CJK status: fail closed
        if (SendMessageTimeout(ime, 0x283, 5, 0, 2, 15, out var open) == 0) return false;
        if (open == 0) return true;
        // Chinese IMEs can remain open in English mode (including Chromium input fields).
        // Open status alone does not tell us whether keys will enter composition.
        return SendMessageTimeout(ime, 0x283, 1, 0, 2, 15, out var mode) != 0 && IsDirectConversionMode(mode);
    }

    internal static bool IsDirectConversionMode(nuint mode) =>
        // Only alphanumeric/half-width input; allow Roman, soft keyboard and no-conversion flags.
        // Reject native, full-width, character-code, symbol and unknown conversion modes.
        (mode & ~(nuint)(0x10 | 0x80 | 0x100)) == 0;

    public void Dispose() { _stopping = true; if (_thread.Join(1500)) _ready.Dispose(); }

    private delegate nint Hook(int code, nuint message, nint data);
    private delegate void WinEvent(nint hook, uint evt, nint window, int obj, int child, uint thread, uint time);
    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardEvent { public uint Key, Scan, Flags, Time; public nuint Extra; }
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint SetWindowsHookEx(int id, Hook callback, nint module, uint thread);
    [DllImport("user32.dll")] private static extern nint CallNextHookEx(nint hook, int code, nuint message, nint data);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll")] private static extern nint SetWinEventHook(uint min, uint max, nint module, WinEvent callback, uint process, uint thread, uint flags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(nint hook);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandle(string? name);
    [DllImport("user32.dll")] private static extern nint GetKeyboardLayout(uint thread);
    [DllImport("user32.dll")] private static extern short GetKeyState(int key);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int ToUnicodeEx(uint key, uint scan, byte[] state, StringBuilder text, int count, uint flags, nint layout);
    [DllImport("imm32.dll")] private static extern nint ImmGetDefaultIMEWnd(nint window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint SendMessageTimeout(nint window, uint message, nuint wParam, nint lParam, uint flags, uint timeout, out nuint result);
}

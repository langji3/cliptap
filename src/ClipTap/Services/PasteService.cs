using System.Runtime.InteropServices;
using System.Text;

namespace ClipTap.Services;

internal sealed record PasteTarget(nint Window, nint Focus, uint ProcessId);

internal static class PasteService
{
    public static PasteTarget? CaptureTarget()
    {
        var window = NativeMethods.GetForegroundWindow();
        if (window == nint.Zero) return null;
        var thread = NativeMethods.GetWindowThreadProcessId(window, out var process);
        if (process == Environment.ProcessId) return null;
        var name = new StringBuilder(128);
        NativeMethods.GetClassName(window, name, name.Capacity);
        if (name.ToString() is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") return null;
        var info = new NativeMethods.GuiThreadInfo { Size = Marshal.SizeOf<NativeMethods.GuiThreadInfo>() };
        if (!NativeMethods.GetGUIThreadInfo(thread, ref info) || info.Focus == nint.Zero) return null;
        return new PasteTarget(window, info.Focus, process);
    }

    public static async Task<bool> PasteAsync(PasteTarget? target)
    {
        if (target is null || !NativeMethods.IsWindow(target.Window) || !NativeMethods.IsWindow(target.Focus)) return false;
        NativeMethods.GetWindowThreadProcessId(target.Window, out var process);
        if (process != target.ProcessId || !NativeMethods.SetForegroundWindow(target.Window)) return false;
        await Task.Delay(100);
        // Never synthesize key-up events for keys physically held by the user.
        for (var i = 0; i < 30; i++)
        {
            if (NativeMethods.GetForegroundWindow() != target.Window) return false;
            if (!KeysHeld()) break;
            await Task.Delay(25);
        }
        if (KeysHeld() || NativeMethods.GetForegroundWindow() != target.Window) return false;
        var thread = NativeMethods.GetWindowThreadProcessId(target.Window, out process);
        var info = new NativeMethods.GuiThreadInfo { Size = Marshal.SizeOf<NativeMethods.GuiThreadInfo>() };
        if (process != target.ProcessId || !NativeMethods.GetGUIThreadInfo(thread, ref info) || info.Focus != target.Focus) return false;
        var keys = new[] { NativeMethods.Key(0x11), NativeMethods.Key(0x56), NativeMethods.Key(0x56, true), NativeMethods.Key(0x11, true) };
        var sent = NativeMethods.SendInput((uint)keys.Length, keys, Marshal.SizeOf<NativeMethods.Input>());
        if (sent > 0 && sent < keys.Length)
        {
            var release = new[] { NativeMethods.Key(0x56, true), NativeMethods.Key(0x11, true) };
            NativeMethods.SendInput(2, release, Marshal.SizeOf<NativeMethods.Input>());
        }
        return sent == keys.Length;
    }

    private static bool KeysHeld() => new[] { 0x10, 0x11, 0x12, 0x5B, 0x5C, 0x0D, 0x01 }
        .Any(key => (NativeMethods.GetAsyncKeyState(key) & 0x8000) != 0);
}

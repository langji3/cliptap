using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using ClipTap.Core;
using ClipTap.Services;

internal static class ExpansionIntegration
{
    private const nuint TestTag = 0x43545453;
    internal static int RunFixture(string name)
    {
        var app = new Application();
        var input = new TextBox { Text = "seed:", Margin = new Thickness(20), FontSize = 18 };
        var second = new TextBox { Margin = new Thickness(20, 0, 20, 10) };
        var form = new StackPanel(); form.Children.Add(input); form.Children.Add(second);
        System.Windows.Input.InputMethod.SetIsInputMethodEnabled(input, false);
        var window = new Window { Title = "ClipTap expansion test", Width = 480, Height = 180, Content = form, Topmost = true };
        window.Loaded += async (_, _) =>
        {
            try
            {
                using var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
                await pipe.ConnectAsync(5000);
                using var reader = new StreamReader(pipe); using var writer = new StreamWriter(pipe) { AutoFlush = true };
                window.Activate(); input.Focus(); input.CaretIndex = input.Text.Length;
                await Task.Delay(250);
                await writer.WriteLineAsync(new WindowInteropHelper(window).Handle.ToString());
                while (await reader.ReadLineAsync() is { } command && command != "exit")
                {
                    if (command == "reset") { input.Text = "seed:"; input.CaretIndex = input.Text.Length; }
                    if (command == "focus-away") second.Focus();
                    if (command == "focus-back") { input.Focus(); input.CaretIndex = input.Text.Length; }
                    if (command == "ime-english")
                    {
                        System.Windows.Input.InputMethod.SetIsInputMethodEnabled(input, true);
                        System.Windows.Input.InputMethod.SetPreferredImeState(input, System.Windows.Input.InputMethodState.On);
                        System.Windows.Input.InputMethod.SetPreferredImeConversionMode(input, System.Windows.Input.ImeConversionModeValues.Alphanumeric);
                        second.Focus(); input.Focus(); input.CaretIndex = input.Text.Length;
                        var ime = ImmGetDefaultIMEWnd(new WindowInteropHelper(window).Handle);
                        SendMessage(ime, 0x283, 2, 0); // Half-width alphanumeric, including ASCII punctuation.
                        var open = SendMessage(ime, 0x283, 5, 0);
                        var mode = SendMessage(ime, 0x283, 1, 0);
                        await writer.WriteLineAsync($"Fixture IME: open={open}, conversion={mode}");
                        continue;
                    }
                    await writer.WriteLineAsync(JsonSerializer.Serialize(input.Text));
                }
            }
            finally { window.Close(); }
        };
        return app.Run(window);
    }

    internal static async Task VerifyAsync()
    {
        var name = "ClipTap.Expansion.Tests." + Guid.NewGuid().ToString("N");
        using var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
        if (string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        start.ArgumentList.Add("--expansion-fixture"); start.ArgumentList.Add(name);
        using var process = Process.Start(start)!;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            await pipe.WaitForConnectionAsync(timeout.Token);
            using var reader = new StreamReader(pipe); using var writer = new StreamWriter(pipe) { AutoFlush = true };
            var handle = new nint(long.Parse((await reader.ReadLineAsync(timeout.Token))!));
            NativeMethods.SetForegroundWindow(handle); await Task.Delay(250);
            if (PasteService.CaptureTarget()?.Window != handle) throw new InvalidOperationException("Fixture not focused; no input sent");
            var failures = 0;
            using var service = new ExpansionService(() => Interlocked.Increment(ref failures), TestTag);
            if (!service.Available) throw new InvalidOperationException("Hooks unavailable");
            var snippet = new Snippet(Guid.NewGuid(), "Synthetic test", "虚构value🔑", true, false, DateTimeOffset.Now, "!ab");
            service.Configure([snippet]);
            var clipboardSequence = NativeMethods.GetClipboardSequenceNumber();
            async Task Key(ushort key, bool up = false)
            {
                if (PasteService.CaptureTarget()?.Window != handle) throw new InvalidOperationException("Fixture lost focus; stopped");
                var input = NativeMethods.Key(key, up); input.Data.Keyboard.Extra = TestTag;
                if (NativeMethods.SendInput(1, [input], Marshal.SizeOf<NativeMethods.Input>()) != 1) throw new InvalidOperationException("Test input blocked");
                await Task.Delay(35);
            }
            async Task Letter(ushort key) { await Key(key); await Key(key, true); }
            async Task Prefix() { await Key(0xA0); await Letter(0x31); await Key(0xA0, true); await Letter(0x41); }
            async Task Expect(string text)
            {
                await Task.Delay(150); await writer.WriteLineAsync("read");
                var actual = JsonSerializer.Deserialize<string>((await reader.ReadLineAsync(timeout.Token))!);
                if (actual != text) throw new InvalidOperationException($"Expansion fixture result did not match expected synthetic text (length {actual?.Length}; unchanged trigger: {actual == "seed:!253pass"})");
            }
            async Task Reset() { await writer.WriteLineAsync("reset"); await reader.ReadLineAsync(timeout.Token); }
            await Prefix(); await Letter(0x42); await Expect("seed:" + snippet.Value);
            await Reset(); service.Paused = true;
            await Prefix(); await Letter(0x42); await Expect("seed:!ab");
            await Reset(); service.Paused = false;
            await Prefix(); await Letter(0x25); await Letter(0x42); await Expect("seed:!ba");
            await Reset(); await Prefix(); await Letter(8); await Letter(0x42); await Expect("seed:!b");
            await Reset(); await Prefix();
            await writer.WriteLineAsync("focus-away"); await reader.ReadLineAsync(timeout.Token); await Task.Delay(100);
            await writer.WriteLineAsync("focus-back"); await reader.ReadLineAsync(timeout.Token); await Task.Delay(100);
            await Letter(0x42); await Expect("seed:!ab");
            await Reset(); await Prefix(); service.Paused = true; service.Paused = false;
            await Letter(0x42); await Expect("seed:!ab");
            await Reset(); service.Configure([snippet with { Trigger = "!a_b" }]);
            await Prefix(); await Key(0xA0); await Letter(0xBD); await Key(0xA0, true);
            await Letter(0x42); await Expect("seed:" + snippet.Value);
            await Reset(); service.Configure([snippet with { Value = "updated" }]);
            await Prefix(); await Letter(0x42); await Expect("seed:updated");
            await Reset(); service.Configure([]);
            await Prefix(); await Letter(0x42); await Expect("seed:!ab");
            await Reset(); service.Configure([snippet with { Trigger = "!253pass" }]);
            await writer.WriteLineAsync("ime-english");
            var imeStatus = await reader.ReadLineAsync(timeout.Token);
            if (imeStatus != "Fixture IME: open=1, conversion=0") throw new InvalidOperationException("Fixture could not enter open, half-width English IME mode: " + imeStatus);
            Console.WriteLine(imeStatus); await Task.Delay(150);
            await Key(0xA0); await Letter(0x31); await Key(0xA0, true);
            foreach (var key in new ushort[] { 0x32, 0x35, 0x33, 0x50, 0x41, 0x53, 0x53 }) await Letter(key);
            await Expect("seed:" + snippet.Value);
            if (failures != 0 || clipboardSequence != NativeMethods.GetClipboardSequenceNumber())
                throw new InvalidOperationException("Expansion reported failure or clipboard changed");
            await writer.WriteLineAsync("exit"); await process.WaitForExitAsync(timeout.Token);
        }
        finally { if (!process.HasExited) process.Kill(); }
    }

    [DllImport("imm32.dll")] private static extern nint ImmGetDefaultIMEWnd(nint window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint SendMessage(nint window, uint message, nuint command, nint value);
}

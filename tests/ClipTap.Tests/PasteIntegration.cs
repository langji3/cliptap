using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using ClipTap.Services;

internal static class PasteIntegration
{
    internal static int RunFixture(string pipeName)
    {
        var application = new Application();
        var input = new TextBox { Text = "seed:", Margin = new Thickness(20), FontSize = 18 };
        var window = new Window { Title = "ClipTap paste test · temporary input", Width = 460, Height = 150, Content = input };
        window.Loaded += async (_, _) =>
        {
            try
            {
                using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                await pipe.ConnectAsync(5000);
                using var reader = new StreamReader(pipe, leaveOpen: true);
                using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
                window.Activate(); input.Focus(); input.CaretIndex = input.Text.Length;
                await Task.Delay(200);
                await writer.WriteLineAsync(new WindowInteropHelper(window).Handle.ToString());
                await reader.ReadLineAsync();
                await writer.WriteLineAsync(JsonSerializer.Serialize(input.Text));
            }
            finally { window.Close(); }
        };
        return application.Run(window);
    }

    internal static void Verify()
    {
        var oldContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        var frame = new DispatcherFrame();
        var task = VerifyAsync();
        task.GetAwaiter().OnCompleted(() => frame.Continue = false);
        Dispatcher.PushFrame(frame);
        SynchronizationContext.SetSynchronizationContext(oldContext);
        task.GetAwaiter().GetResult();
    }

    private static async Task VerifyAsync()
    {
        var pipeName = "ClipTap.Tests." + Guid.NewGuid().ToString("N");
        using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
        if (string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        start.ArgumentList.Add("--paste-fixture"); start.ArgumentList.Add(pipeName);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Cannot launch test input");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        var backup = SnapshotClipboard();
        uint? writtenSequence = null;
        using var clipboard = new ClipboardService();
        try
        {
            await pipe.WaitForConnectionAsync(timeout.Token);
            using var reader = new StreamReader(pipe, leaveOpen: true);
            using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
            var fixtureHandle = new nint(long.Parse(await reader.ReadLineAsync(timeout.Token) ?? "0"));
            NativeMethods.SetForegroundWindow(fixtureHandle);
            await Task.Delay(150);
            var target = PasteService.CaptureTarget();
            if (target?.Window != fixtureHandle)
            {
                var foreground = NativeMethods.GetForegroundWindow();
                var thread = NativeMethods.GetWindowThreadProcessId(foreground, out var pid);
                var gui = new NativeMethods.GuiThreadInfo { Size = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.GuiThreadInfo>() };
                var found = NativeMethods.GetGUIThreadInfo(thread, ref gui);
                throw new InvalidOperationException($"Test input did not gain focus; no input sent. Fixture={fixtureHandle}, foreground={foreground}, PID={pid}, GUI={found}, focus={gui.Focus}");
            }
            const string payload = "ClipTap 验证 ✓";
            if (!await clipboard.WriteAsync(payload, sensitive: false)) throw new InvalidOperationException("Clipboard busy");
            writtenSequence = NativeMethods.GetClipboardSequenceNumber();
            if (Clipboard.GetText() != payload) throw new InvalidOperationException("Clipboard write did not match");
            if (!await PasteService.PasteAsync(target)) throw new InvalidOperationException("Paste was not sent to test input");
            await Task.Delay(200);
            await writer.WriteLineAsync("read");
            var actual = JsonSerializer.Deserialize<string>(await reader.ReadLineAsync(timeout.Token) ?? "null");
            if (actual != "seed:" + payload) throw new InvalidOperationException("Unexpected pasted text: " + actual);
            await process.WaitForExitAsync(timeout.Token);
            if (await PasteService.PasteAsync(target)) throw new InvalidOperationException("Destroyed window was accepted");
            if (await PasteService.PasteAsync(null)) throw new InvalidOperationException("Null target was accepted");
        }
        finally
        {
            if (!process.HasExited) process.Kill();
            if (writtenSequence == NativeMethods.GetClipboardSequenceNumber())
            {
                if (backup is null) Clipboard.Clear();
                else Clipboard.SetDataObject(backup, copy: true);
            }
        }
    }

    private static DataObject? SnapshotClipboard()
    {
        var source = Clipboard.GetDataObject();
        if (source is null) return null;
        var copy = new DataObject();
        foreach (var format in source.GetFormats(autoConvert: false))
        {
            var value = source.GetData(format, autoConvert: false);
            if (value is MemoryStream stream) value = new MemoryStream(stream.ToArray());
            if (value is not null) copy.SetData(format, value, autoConvert: false);
        }
        return copy;
    }
}

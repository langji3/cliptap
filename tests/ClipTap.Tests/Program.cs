using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ClipTap;
using ClipTap.Core;
using ClipTap.Services;

internal static class Program
{
    private static int _passed, _failed;
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-16T12:00:00+08:00");

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--paste-fixture") return PasteIntegration.RunFixture(args[1]);
        Test("History: exact deduplication moves a copy to the front", () =>
        {
            var library = NewLibrary();
            library.Capture(" first ", Now); library.Capture("second", Now.AddSeconds(1)); library.Capture(" first ", Now.AddSeconds(2));
            Equal(2, library.State.History.Count); Equal(" first ", library.State.History[0].Text);
            library.Capture("first", Now.AddSeconds(3)); Equal(3, library.State.History.Count);
        });
        Test("History: blank, oversized and paused captures are ignored", () =>
        {
            var library = NewLibrary();
            Check(!library.Capture(null, Now)); Check(!library.Capture(" \r\n", Now));
            Check(!library.Capture(new string('x', Library.MaxTextLength + 1), Now));
            Check(library.Capture(new string('x', Library.MaxTextLength), Now));
            library.State.Settings.CapturePaused = true; Check(!library.Capture("ignored", Now));
            Equal(1, library.State.History.Count);
        });
        Test("History: bounded retention preserves newest entries", () =>
        {
            var library = NewLibrary(); library.SetHistoryLimit(20);
            for (var i = 0; i < 30; i++) library.Capture($"item-{i}", Now.AddSeconds(i));
            Equal(20, library.State.History.Count); Equal("item-29", library.State.History[0].Text);
            Equal("item-10", library.State.History[^1].Text);
            library.SetHistoryLimit(-1); Equal(20, library.State.Settings.HistoryLimit);
            library.SetHistoryLimit(9999); Equal(500, library.State.Settings.HistoryLimit);
        });
        Test("Sensitive snippets purge matching history and block recapture", () =>
        {
            var library = NewLibrary(); library.Capture("secret-value", Now);
            library.SaveSnippet(Snippet("Production password", "secret-value", sensitive: true));
            Equal(0, library.State.History.Count); Check(!library.Capture("secret-value", Now));
            Check(library.Capture("ordinary", Now));
            Equal("Production password", library.OrderedSnippets().Single().Title);
        });
        Test("Settings: preparing a smaller limit does not trim live history before save", () =>
        {
            var library = NewLibrary();
            for (var i = 0; i < 40; i++) library.Capture($"item-{i}", Now.AddSeconds(i));
            var candidate = library.WithSettings(new AppSettings { HistoryLimit = 20, CapturePaused = true, Theme = AppearanceMode.Dark, Accent = AccentPalette.Purple });
            Equal(20, candidate.State.History.Count); Equal(40, library.State.History.Count);
            Equal(100, library.State.Settings.HistoryLimit); Check(!library.State.Settings.CapturePaused);
            Equal(AppearanceMode.System, library.State.Settings.Theme);
            Equal(AccentPalette.Purple, candidate.State.Settings.Accent); Equal(AccentPalette.Green, library.State.Settings.Accent);
        });
        Test("Snippets: updates preserve identity and pinned items sort first", () =>
        {
            var library = NewLibrary(); var first = Snippet("A", "a", pinned: true);
            library.SaveSnippet(first); library.SaveSnippet(Snippet("B", "b") with { UpdatedAt = Now.AddDays(1) });
            library.SaveSnippet(first with { Title = " A edited " });
            Equal(2, library.State.Snippets.Count); Equal("A edited", library.OrderedSnippets().First().Title);
            Throws<ArgumentException>(() => library.SaveSnippet(Snippet(" ", "x")));
            Throws<ArgumentException>(() => library.SaveSnippet(Snippet("valid", "")));
        });
        Test("Loaded state is normalized and unknown versions fail closed", () =>
        {
            var state = new AppState { History = [new(Guid.NewGuid(), "same", Now), new(Guid.NewGuid(), "same", Now.AddDays(1))] };
            Equal(1, new Library(state).State.History.Count);
            Throws<InvalidDataException>(() => new Library(new AppState { Version = 999 }));
        });
        Test("Preview handles multiline text without modifying stored value", () =>
        {
            Equal("hello  world", Library.Preview("hello\r\nworld"));
            Equal("abcd…", Library.Preview("abcdefgh", 4));
        });
        Test("DPAPI: encrypted round trip, atomic replacement and tamper rejection", () =>
        {
            WithTempDirectory(directory =>
            {
                var store = new EncryptedStore(directory); var state = new AppState();
                state.Snippets.Add(Snippet("private title", "a-secret-not-for-disk-秘密", sensitive: true));
                store.Save(state);
                var raw = File.ReadAllBytes(store.FilePath);
                Check(!Encoding.UTF8.GetString(raw).Contains("private title"));
                Check(!Encoding.UTF8.GetString(raw).Contains("a-secret-not-for-disk"));
                Equal(state.Snippets[0].Value, store.Load().Snippets[0].Value);
                state.History.Add(new(Guid.NewGuid(), "changed", Now)); store.Save(state);
                Equal(1, store.Load().History.Count); Equal(1, Directory.GetFiles(directory).Length);
                raw = File.ReadAllBytes(store.FilePath); raw[^1] ^= 0xFF; File.WriteAllBytes(store.FilePath, raw);
                Throws<CryptographicException>(() => store.Load());
                Check(File.ReadAllBytes(store.FilePath).SequenceEqual(raw));
            });
        });
        Test("Persistence: missing files start empty; invalid headers are preserved", () =>
        {
            WithTempDirectory(directory =>
            {
                var store = new EncryptedStore(directory); Equal(0, store.Load().History.Count);
                File.WriteAllText(store.FilePath, "unrecognized-file");
                Throws<InvalidDataException>(() => store.Load()); Equal("unrecognized-file", File.ReadAllText(store.FilePath));
            });
        });
        Test("Win32: INPUT structure includes full native union", () =>
        { Equal(IntPtr.Size == 8 ? 40 : 28, Marshal.SizeOf<NativeMethods.Input>()); });
        Test("Win32: clipboard listener registration and cleanup", () =>
        {
            using var source = new HwndSource(new HwndSourceParameters("ClipTap integration test") { ParentWindow = new nint(-3) });
            Check(NativeMethods.AddClipboardFormatListener(source.Handle));
            Check(NativeMethods.RemoveClipboardFormatListener(source.Handle));
        });
        Test("Background: native message window routes events without a WPF panel", () =>
        {
            var hotkeys = 0; var copies = 0;
            using var events = new DesktopEvents(() => hotkeys++, () => copies++);
            SendMessage(events.Handle, NativeMethods.WmHotkey, NativeMethods.HotkeyId, nint.Zero);
            SendMessage(events.Handle, NativeMethods.WmClipboardUpdate, nint.Zero, nint.Zero);
            Equal(1, hotkeys); Equal(1, copies);
        });

        var app = new App { Library = NewLibrary() };
        app.InitializeComponent();
        ThemeService.ApplyTheme(AppearanceMode.Light);
        var panel = new MainWindow(app);
        Test("UI: empty state and clipboard default", () =>
        {
            Equal("从一次复制开始", ((TextBlock)panel.FindName("EmptyTitle")).Text);
            Equal(Visibility.Collapsed, ((StackPanel)panel.FindName("SnippetActions")).Visibility);
        });
        var artifactDirectory = Path.GetFullPath(args.FirstOrDefault(a => !a.StartsWith("--")) ?? "artifacts/test-results");
        Directory.CreateDirectory(artifactDirectory);
        Test("UI: arrows switch tabs, clamp selection and preserve secret titles", () =>
        {
            app.Library.Capture("欢迎使用 ClipTap", Now);
            app.Library.Capture("https://github.com/langji3/cliptap", Now.AddSeconds(1));
            app.Library.Capture("让常用内容，随取随贴。", Now.AddSeconds(2));
            app.Library.SaveSnippet(Snippet("常用邮箱", "hello@example.com", pinned: true));
            app.Library.SaveSnippet(Snippet("测试数据库密码", "never-visible-in-row", sensitive: true));
            panel.Show(); panel.RefreshRows();
            var entries = (ListBox)panel.FindName("Entries");
            Equal(0, entries.SelectedIndex);
            SendKey(panel, Key.Down); Equal(1, entries.SelectedIndex);
            SendKey(panel, Key.Up); SendKey(panel, Key.Up); Equal(0, entries.SelectedIndex);
            Render(panel, Path.Combine(artifactDirectory, "clipboard.png"));
            SendKey(panel, Key.Right); Equal(2, entries.Items.Count);
            var rows = entries.Items.Cast<EntryRow>().ToArray();
            Equal("常用邮箱", rows[0].Title); Check(rows.All(r => !r.Title.Contains("never-visible") && !r.Detail.Contains("never-visible")));
            Render(panel, Path.Combine(artifactDirectory, "snippets.png"));
            SendKey(panel, Key.Left); Equal(3, entries.Items.Count);
            SendKey(panel, Key.Escape); Check(!panel.IsVisible);
            panel.OpenPanel(captureTarget: false); Equal(3, entries.Items.Count); Equal(0, entries.SelectedIndex);
            panel.Hide();
        });
        Test("UI: sensitive editor keeps content masked and preserves reveal toggles", () =>
        {
            var editor = new SnippetWindow(Snippet("Password", "multiline\nsecret", sensitive: true));
            var plain = (TextBox)editor.FindName("ValueInput"); var secret = (PasswordBox)editor.FindName("SecretInput");
            Equal(Visibility.Collapsed, plain.Visibility); Equal("", plain.Text); Equal("multiline\nsecret", secret.Password);
            ((CheckBox)editor.FindName("RevealInput")).IsChecked = true; Equal("multiline\nsecret", plain.Text); Equal("", secret.Password);
            ((CheckBox)editor.FindName("RevealInput")).IsChecked = false; Equal("", plain.Text); Equal("multiline\nsecret", secret.Password);
            editor.Close();
        });
        Test("Theme: dark/light updates existing controls and settings preview cancels cleanly", () =>
        {
            panel.Show();
            ThemeService.ApplyTheme(AppearanceMode.Dark);
            panel.UpdateLayout();
            Check(((SolidColorBrush)panel.Background).Color.R < 128);
            SendKey(panel, Key.Right);
            panel.UpdateLayout();
            var themedList = (ListBox)panel.FindName("Entries");
            var themedRow = (ListBoxItem)themedList.ItemContainerGenerator.ContainerFromIndex(0);
            Equal(((SolidColorBrush)app.Resources["Ink"]).Color, ((SolidColorBrush)themedRow.Foreground).Color);
            Render(panel, Path.Combine(artifactDirectory, "snippets-dark.png"));
            SendKey(panel, Key.Left);
            Render(panel, Path.Combine(artifactDirectory, "clipboard-dark.png"));
            var editor = new SnippetWindow(Snippet("测试数据库密码", "fixture-secret", sensitive: true));
            editor.Show(); Render(editor, Path.Combine(artifactDirectory, "editor-dark.png")); editor.Close();
            app.Library.State.Settings.Theme = AppearanceMode.Dark;
            var settings = new SettingsWindow(app);
            settings.Show(); Render(settings, Path.Combine(artifactDirectory, "settings-dark.png"));
            ((ComboBox)settings.FindName("AccentInput")).SelectedIndex = (int)AccentPalette.Purple;
            Equal((Color)ColorConverter.ConvertFromString("#C0A3ED"), ((SolidColorBrush)app.Resources["Accent"]).Color);
            ((ComboBox)settings.FindName("ThemeInput")).SelectedIndex = (int)AppearanceMode.Light;
            Check(((SolidColorBrush)panel.Background).Color.R > 128);
            settings.Close();
            Check(((SolidColorBrush)panel.Background).Color.R < 128);
            Equal(AppearanceMode.Dark, app.Library.State.Settings.Theme);
            Equal(AccentPalette.Green, app.Library.State.Settings.Accent);
            Equal((Color)ColorConverter.ConvertFromString("#77CDA6"), ((SolidColorBrush)app.Resources["Accent"]).Color);
            ThemeService.ApplyTheme(AppearanceMode.Light);
            Check(((SolidColorBrush)panel.Background).Color.R > 128);
            panel.Hide();
        });
        Test("Theme: preference survives encrypted storage; invalid values use system", () =>
        {
            WithTempDirectory(directory =>
            {
                var store = new EncryptedStore(directory); var state = new AppState();
                state.Settings.Theme = AppearanceMode.Dark; state.Settings.Accent = AccentPalette.Purple; store.Save(state);
                Equal(AppearanceMode.Dark, store.Load().Settings.Theme);
                Equal(AccentPalette.Purple, store.Load().Settings.Accent);
                state.Settings.Theme = (AppearanceMode)999; Equal(AppearanceMode.System, new Library(state).State.Settings.Theme);
            });
        });
        Test("Accents: all six color/mode combinations render with readable text", () =>
        {
            panel.Show();
            foreach (var mode in new[] { AppearanceMode.Light, AppearanceMode.Dark })
            foreach (var accent in Enum.GetValues<AccentPalette>())
            {
                ThemeService.ApplyTheme(mode, accent); panel.UpdateLayout();
                var ink = ((SolidColorBrush)app.Resources["Ink"]).Color;
                var background = ((SolidColorBrush)panel.Background).Color;
                Check(mode == AppearanceMode.Dark ? ink.R > background.R + 120 : background.R > ink.R + 120);
                var list = (ListBox)panel.FindName("Entries");
                Equal(ink, ((SolidColorBrush)((ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(0)).Foreground).Color);
                Render(panel, Path.Combine(artifactDirectory, $"clipboard-{mode.ToString().ToLowerInvariant()}-{accent.ToString().ToLowerInvariant()}.png"));
            }
            panel.Hide();
        });
        panel.Hide();
        if (args.Contains("--integration"))
            Test("Cross-process: restore a real input focus and paste; ignore own clipboard writes", PasteIntegration.Verify);
        Console.WriteLine($"\n{_passed} passed, {_failed} failed. Screenshots: {artifactDirectory}");
        return _failed == 0 ? 0 : 1;
    }

    private static void Render(Window window, string path)
    {
        window.UpdateLayout();
        var content = (FrameworkElement)window.Content;
        var bounds = new Rect(0, 0, content.ActualWidth + content.Margin.Left + content.Margin.Right,
            content.ActualHeight + content.Margin.Top + content.Margin.Bottom);
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.DrawRectangle(window.Background, null, bounds);
            context.DrawRectangle(new VisualBrush(content), null,
                new Rect(content.Margin.Left, content.Margin.Top, content.ActualWidth, content.ActualHeight));
        }
        var bitmap = new RenderTargetBitmap((int)bounds.Width, (int)bounds.Height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path); encoder.Save(stream);
    }
    private static void SendKey(Window window, Key key)
    {
        var source = PresentationSource.FromVisual(window) ?? throw new InvalidOperationException("Window has no presentation source");
        window.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
    }
    private static Library NewLibrary() => new(new AppState());
    [DllImport("user32.dll")]
    private static extern nint SendMessage(nint window, int message, nint wParam, nint lParam);
    private static Snippet Snippet(string title, string value, bool sensitive = false, bool pinned = false) => new(Guid.NewGuid(), title, value, sensitive, pinned, Now);
    private static void Test(string name, Action action)
    {
        try { action(); _passed++; Console.WriteLine("PASS " + name); }
        catch (Exception ex) { _failed++; Console.WriteLine("FAIL " + name + "\n" + ex); }
    }
    private static void Equal<T>(T expected, T actual) { if (!Equals(expected, actual)) throw new InvalidOperationException($"Expected {expected}; got {actual}"); }
    private static void Check(bool condition) { if (!condition) throw new InvalidOperationException("Assertion failed"); }
    private static void Throws<T>(Action action) where T : Exception
    { try { action(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    private static void WithTempDirectory(Action<string> action)
    {
        var root = Path.Combine(Path.GetTempPath(), "ClipTap.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try { action(root); } finally { Directory.Delete(root, recursive: true); }
    }
}

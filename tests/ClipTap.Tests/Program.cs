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
using ClipTap.Views;

internal static class Program
{
    private static int _passed, _failed;
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-16T12:00:00+08:00");

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--paste-fixture") return PasteIntegration.RunFixture(args[1]);
        if (args.Length == 2 && args[0] == "--expansion-fixture") return ExpansionIntegration.RunFixture(args[1]);
        Test("Updates: installed version checks, portable isolation, release validation and failures", () => Await(UpdateTests.VerifyAsync()));
        Test("Expansion: trigger validation, conflicts and removal", () =>
        {
            var library = NewLibrary();
            var snippet = Snippet("Test", "虚构密码 ✓", sensitive: true) with { Trigger = " !hzpass " };
            library.SaveSnippet(snippet);
            Equal("!hzpass", library.State.Snippets.Single().Trigger);
            foreach (var invalid in new[] { "!a", "abc", "!ABC", "!中a", "!a b", "!ab_", "!" + new string('a', 32) })
                Throws<ArgumentException>(() => library.SaveSnippet(Snippet("Invalid", "v") with { Trigger = invalid }));
            foreach (var conflict in new[] { "!hzpass", "!hz", "!hzpass2" })
                Throws<ArgumentException>(() => library.SaveSnippet(Snippet("Conflict", "v") with { Trigger = conflict }));
            Throws<ArgumentException>(() => library.SaveSnippet(snippet with { Value = "a\nb" }));
            Throws<ArgumentException>(() => library.SaveSnippet(snippet with { Value = "a\tb" }));
            Throws<ArgumentException>(() => library.SaveSnippet(snippet with { Value = new string('a', 2001) }));
            library.SaveSnippet(snippet with { Trigger = "", Value = "a\nb" });
            Equal("", library.State.Snippets.Single().Trigger);
        });
        Test("Expansion: precise matching, reset, timeout, reconfigure and ambiguity", () =>
        {
            var matcher = new ExpansionMatcher();
            var snippet = Snippet("Test", "value") with { Trigger = "!hzpass" };
            matcher.Configure([snippet]); long time = 0;
            Snippet? Feed(string value) { Snippet? result = null; foreach (var c in value) result = matcher.Feed(c, time += 30); return result; }
            Check(Feed("normal text hzpass") is null); Equal(snippet, Feed("!hzpass"));
            Check(Feed("!hz") is null); matcher.Reset(); Check(Feed("pass") is null);
            Feed("!hz"); time += 6000; Check(Feed("pass") is null);
            Check(Feed("!hzXpass") is null); Equal(snippet, Feed("!!hzpass"));
            Feed("!hz"); matcher.Configure([snippet]); Check(Feed("pass") is null);
            matcher.Configure([snippet, snippet with { Id = Guid.NewGuid(), Trigger = "!hz" }]);
            Check(Feed("!hzpass") is null);
            matcher.Configure([]); Check(Feed("!hzpass") is null);
        });
        Test("Expansion: backspace corrects valid prefixes and multiple mistyped characters", () =>
        {
            var matcher = new ExpansionMatcher();
            var snippet = Snippet("Correction", "synthetic-secret", sensitive: true) with { Trigger = "!253pass" };
            matcher.Configure([snippet]); long time = 0;
            Snippet? Feed(string value) { Snippet? result = null; foreach (var c in value) result = matcher.Feed(c, time += 30); return result; }
            void Back(int count) { for (var i = 0; i < count; i++) matcher.Backspace(time += 30); }
            Feed("!253pa"); Back(2); Equal(snippet, Feed("pass"));
            Feed("!259xy"); Back(3); Equal(snippet, Feed("3pass"));
            Feed("!253pX"); Check(Feed("ass") is null); Back(4); Equal(snippet, Feed("ass"));
            Feed("!253"); Back(5); Check(Feed("253pass") is null); Equal(snippet, Feed("!253pass"));
            // Successful expansion has no retained prefix to resurrect with Backspace.
            Back(1); Check(Feed("s") is null);
        });
        Test("Expansion: correction cannot survive reset, timeout, non-ASCII or excessive input", () =>
        {
            var matcher = new ExpansionMatcher();
            var snippet = Snippet("Correction", "value") with { Trigger = "!ab" };
            matcher.Configure([snippet]); long time = 0;
            Snippet? Feed(string value) { Snippet? result = null; foreach (var c in value) result = matcher.Feed(c, time += 30); return result; }
            void Back(int count) { for (var i = 0; i < count; i++) matcher.Backspace(time += 30); }
            Feed("!ax"); matcher.Reset(); Back(1); Check(Feed("b") is null);
            Feed("!ax"); time += 6000; Back(1); Check(Feed("b") is null);
            Feed("!ax"); time = 0; Back(1); Check(Feed("b") is null);
            Feed("!ax"); matcher.Configure([snippet]); Back(1); Check(Feed("b") is null);
            foreach (var boundary in new[] { " ", "\n", "中", "🔑" })
            { Feed("!a" + boundary); Back(boundary.Length); Check(Feed("b") is null); }
            Feed("!a" + new string('x', 64)); Back(64); Check(Feed("b") is null);
            Equal(snippet, Feed("!ab"));
        });
        Test("Expansion: sensitive numeric trigger and English IME conversion modes", () =>
        {
            var snippet = Snippet("Synthetic", "synthetic-secret", sensitive: true) with { Trigger = "!253pass" };
            var matcher = new ExpansionMatcher(); matcher.Configure([snippet]);
            Snippet? result = null; long time = 0;
            foreach (var character in snippet.Trigger) result = matcher.Feed(character, time += 30);
            Equal(snippet, result);
            foreach (nuint mode in new nuint[] { 0, 0x10, 0x80, 0x100, 0x190 })
                Check(ExpansionService.IsDirectConversionMode(mode));
            foreach (nuint mode in new nuint[] { 1, 9, 8, 0x20, 0x200, 0x400, 0x800, 0x1000, 0x11 })
                Check(!ExpansionService.IsDirectConversionMode(mode));
        });
        Test("Expansion: old JSON and encrypted trigger round trip", () =>
        {
            var old = System.Text.Json.JsonSerializer.Deserialize<Snippet>("{\"Id\":\"00000000-0000-0000-0000-000000000001\",\"Title\":\"old\",\"Value\":\"data\"}")!;
            Check(old.Trigger is null);
            WithTempDirectory(directory =>
            {
                var store = new EncryptedStore(directory); var state = new AppState();
                state.Snippets.Add(Snippet("Test", "fake-secret") with { Trigger = "!hzpass" });
                store.Save(state); Equal("!hzpass", store.Load().Snippets[0].Trigger);
                Check(!Encoding.UTF8.GetString(File.ReadAllBytes(store.FilePath)).Contains("!hzpass"));
            });
        });
        Test("Expansion: native batch deletes only delivered prefix and emits Unicode without Ctrl+V", () =>
        {
            var inputs = ExpansionService.BuildInputs(Snippet("Test", "中文🔑") with { Trigger = "!ab" });
            Equal(12, inputs.Length);
            Equal((ushort)8, inputs[0].Data.Keyboard.Key); Equal(2u, inputs[1].Data.Keyboard.Flags);
            Equal((ushort)'中', inputs[4].Data.Keyboard.Scan); Equal(4u, inputs[4].Data.Keyboard.Flags);
            Check(inputs.All(i => i.Data.Keyboard.Extra == ExpansionService.OutputTag));
            Check(inputs.Skip(4).All(i => i.Data.Keyboard.Key == 0));
            Check(ExpansionService.PartialRelease(inputs, 0) is null);
            Equal(2u, ExpansionService.PartialRelease(inputs, 1)!.Value.Data.Keyboard.Flags);
            Check(ExpansionService.PartialRelease(inputs, 2) is null);
            Equal(6u, ExpansionService.PartialRelease(inputs, 5)!.Value.Data.Keyboard.Flags);
            Check(ExpansionService.PartialRelease(inputs, (uint)inputs.Length) is null);
        });
        Test("Sensitive snippets purge matching legacy history", () =>
        {
            var library = NewLibrary(); library.State.History.Add(new(Guid.NewGuid(), "secret-value", Now));
            library.SaveSnippet(Snippet("Production password", "secret-value", sensitive: true));
            Equal(0, library.State.History.Count);
            Equal("Production password", library.OrderedSnippets().Single().Title);
        });
        Test("Settings: candidate state preserves legacy data without managing retention", () =>
        {
            var library = NewLibrary();
            for (var i = 0; i < 40; i++) library.State.History.Add(new(Guid.NewGuid(), $"item-{i}", Now.AddSeconds(i)));
            var candidate = library.WithSettings(new AppSettings { HistoryLimit = 20, CapturePaused = true, Theme = AppearanceMode.Dark, Accent = AccentPalette.Purple });
            Equal(40, candidate.State.History.Count); Equal(40, library.State.History.Count);
            Equal(100, library.State.Settings.HistoryLimit); Check(!library.State.Settings.CapturePaused);
            Equal(AppearanceMode.Light, library.State.Settings.Theme);
            Equal(AccentPalette.Purple, candidate.State.Settings.Accent); Equal(AccentPalette.Blue, library.State.Settings.Accent);
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
            Equal(2, new Library(state).State.History.Count);
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
            var hotkeys = 0;
            using var events = new DesktopEvents(() => hotkeys++);
            Console.WriteLine($"  Alt+Space registration available: {events.HotkeyAvailable}");
            SendMessage(events.Handle, NativeMethods.WmHotkey, NativeMethods.HotkeyId, nint.Zero);
            Equal(1, hotkeys);
        });

        var uiDirectory = Path.Combine(Path.GetTempPath(), "ClipTap.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(uiDirectory);
        var historySource = new FakeHistorySource();
        var app = new App(new EncryptedStore(uiDirectory), historySource) { Library = NewLibrary() };
        app.InitializeComponent();
        Await(app.RefreshSystemHistoryAsync());
        ThemeService.ApplyTheme(AppearanceMode.Light);
        var panel = new MainWindow(app);
        Test("UI: trigger create, edit, validation and disable persist immediately", () =>
        {
            var editor = new SnippetView(app, null);
            ((TextBox)editor.FindName("TitleInput")).Text = "Expansion test";
            ((TextBox)editor.FindName("ValueInput")).Text = "fake-value";
            ((TextBox)editor.FindName("TriggerInput")).Text = "!dbpass";
            editor.Save(); var saved = app.Library.State.Snippets.Single(); Equal("!dbpass", saved.Trigger);
            editor = new SnippetView(app, saved);
            Equal("!dbpass", ((TextBox)editor.FindName("TriggerInput")).Text);
            ((TextBox)editor.FindName("TriggerInput")).Text = "bad"; editor.Save();
            Check(((TextBlock)editor.FindName("ErrorLabel")).Text.Length > 0);
            Equal("!dbpass", app.Library.State.Snippets.Single().Trigger);
            ((TextBox)editor.FindName("TriggerInput")).Clear(); editor.Save();
            Equal("", app.Library.State.Snippets.Single().Trigger);
            Check(app.TryUpdateLibrary(library => library.State.Snippets.Clear()));
        });
        Test("UI: empty state and clipboard default", () =>
        {
            Equal("Alt+空格唤醒", ((TextBlock)panel.FindName("StatusLabel")).Text);
            Equal(0, ((ListBox)panel.FindName("Entries")).Items.Count);
            Equal(Visibility.Collapsed, ((StackPanel)panel.FindName("SnippetActions")).Visibility);
        });
        var artifactDirectory = Path.GetFullPath(args.FirstOrDefault(a => !a.StartsWith("--")) ?? "artifacts/test-results");
        Directory.CreateDirectory(artifactDirectory);
        Test("UI: arrows switch tabs, clamp selection and preserve secret titles", () =>
        {
            app.Library.State.History.AddRange([
                new(Guid.NewGuid(), "让常用内容，随取随贴。", Now.AddSeconds(2)),
                new(Guid.NewGuid(), "https://github.com/langji3/cliptap", Now.AddSeconds(1)),
                new(Guid.NewGuid(), "欢迎使用 ClipTap", Now)]);
            historySource.Snapshot = new(HistoryStatus.Ready, app.Library.State.History.Select(c => (HistoryEntry)c).ToArray());
            Await(app.RefreshSystemHistoryAsync());
            app.Library.SaveSnippet(Snippet("常用邮箱", "hello@example.com", pinned: true));
            app.Library.SaveSnippet(Snippet("测试数据库密码", "never-visible-in-row", sensitive: true));
            panel.Show(); panel.RefreshRows();
            panel.UpdateLayout();
            var header = (Grid)panel.FindName("HeaderDragArea");
            Equal<object>(header, header.InputHitTest(new Point(260, 32)));
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
            var editor = new SnippetView(app, Snippet("Password", "multiline\nsecret", sensitive: true));
            var plain = (TextBox)editor.FindName("ValueInput"); var secret = (PasswordBox)editor.FindName("SecretInput");
            Equal(Visibility.Collapsed, plain.Visibility); Equal("", plain.Text); Equal("multiline\nsecret", secret.Password);
            ((CheckBox)editor.FindName("RevealInput")).IsChecked = true; Equal("multiline\nsecret", plain.Text); Equal("", secret.Password);
            ((CheckBox)editor.FindName("RevealInput")).IsChecked = false; Equal("", plain.Text); Equal("multiline\nsecret", secret.Password);
            editor.Discard(); Equal("", secret.Password); Equal("", plain.Text);
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
            SendKey(panel, Key.Right);
            ((ListBox)panel.FindName("Entries")).SelectedIndex = 1;
            Click(panel, "EditButton");
            Render(panel, Path.Combine(artifactDirectory, "editor-dark.png"));
            Click(panel, "BackButton"); SendKey(panel, Key.Left);
            app.Library.State.Settings.Theme = AppearanceMode.Dark;
            panel.ShowSettings();
            var settings = (SettingsView)((ContentControl)panel.FindName("PageHost")).Content;
            Render(panel, Path.Combine(artifactDirectory, "settings-dark.png"));
            ((RadioButton)settings.FindName("PurpleAccent")).IsChecked = true;
            Equal((Color)ColorConverter.ConvertFromString("#C0A3ED"), ((SolidColorBrush)app.Resources["Accent"]).Color);
            ((ComboBox)settings.FindName("ThemeInput")).SelectedValue = AppearanceMode.Light;
            Check(((SolidColorBrush)panel.Background).Color.R > 128);
            Render(panel, Path.Combine(artifactDirectory, "settings-light.png"));
            Click(panel, "BackButton");
            Check(((SolidColorBrush)panel.Background).Color.R < 128);
            Equal(AppearanceMode.Dark, app.Library.State.Settings.Theme);
            Equal(AccentPalette.Blue, app.Library.State.Settings.Accent);
            Equal((Color)ColorConverter.ConvertFromString("#80B2FF"), ((SolidColorBrush)app.Resources["Accent"]).Color);
            ThemeService.ApplyTheme(AppearanceMode.Light);
            Check(((SolidColorBrush)panel.Background).Color.R > 128);
            panel.Hide();
        });
        Test("Theme: preference survives encrypted storage; defaults and invalid values use light", () =>
        {
            WithTempDirectory(directory =>
            {
                var store = new EncryptedStore(directory); var state = new AppState();
                Equal(AppearanceMode.Light, state.Settings.Theme);
                state.Settings.Theme = AppearanceMode.Dark; state.Settings.Accent = AccentPalette.Purple; store.Save(state);
                Equal(AppearanceMode.Dark, store.Load().Settings.Theme);
                Equal(AccentPalette.Purple, store.Load().Settings.Accent);
                state.Settings.Theme = AppearanceMode.System; store.Save(state);
                Equal(AppearanceMode.System, new Library(store.Load()).State.Settings.Theme);
                state.Settings.Theme = (AppearanceMode)999; Equal(AppearanceMode.Light, new Library(state).State.Settings.Theme);
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
        Test("Navigation: editor stays in one window, preserves drafts, and saves inline", () =>
        {
            panel.OpenPanel(false); SendKey(panel, Key.Right);
            var windows = app.Windows.Count;
            Click(panel, "NewButton"); panel.UpdateLayout();
            var host = (ContentControl)panel.FindName("PageHost");
            var editor = (SnippetView)host.Content;
            Equal(windows, app.Windows.Count);
            ((TextBox)editor.FindName("TitleInput")).Text = "Inline test";
            var value = (TextBox)editor.FindName("ValueInput"); value.Text = "line one\nline two"; value.Focus();
            var key = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(panel), 0, Key.Enter) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            panel.RaiseEvent(key); Check(!key.Handled); Equal<object>(editor, host.Content);
            SendKey(panel, Key.Left); Equal<object>(editor, host.Content);
            panel.Hide(); panel.OpenPanel(false); Equal<object>(editor, host.Content); Equal("line one\nline two", value.Text);
            Render(panel, Path.Combine(artifactDirectory, "editor-light.png"));
            Click(editor, "SaveButton"); Check(host.Content is null);
            Check(app.Library.State.Snippets.Any(s => s.Title == "Inline test")); Equal(windows, app.Windows.Count);
            var entries = (ListBox)panel.FindName("Entries");
            entries.SelectedItem = entries.Items.Cast<EntryRow>().Single(r => r.Title == "Inline test");
            Click(panel, "EditButton"); editor = (SnippetView)host.Content!;
            Click(editor, "DeleteButton");
            var confirm = (InlineConfirmation)editor.FindName("Confirmation"); Check(confirm.IsOpen);
            Render(panel, Path.Combine(artifactDirectory, "delete-inline.png"));
            SendKey(panel, Key.Escape); Check(!confirm.IsOpen); Equal<object>(editor, host.Content!);
            Click(editor, "DeleteButton"); panel.Hide(); panel.OpenPanel(false);
            Check(!confirm.IsOpen); Equal<object>(editor, host.Content!);
            Click(editor, "DeleteButton"); Click(confirm, "ConfirmButton"); Check(host.Content is null);
            Check(!app.Library.State.Snippets.Any(s => s.Title == "Inline test"));
            panel.ShowSettings(); var settings = (SettingsView)host.Content!;
            Click(settings, "ClearButton"); var clear = (InlineConfirmation)settings.FindName("Confirmation");
            Click(clear, "CancelButton"); Equal(3, app.History.Count); Equal(0, historySource.ClearCalls);
            Click(settings, "ClearButton"); Click(clear, "ConfirmButton"); Equal(0, app.History.Count); Equal(1, historySource.ClearCalls);
            Equal(3, app.Library.State.History.Count); // Legacy data is not the system-history source.
            SendKey(panel, Key.Escape); Check(host.Content is null); Equal(windows, app.Windows.Count);
        });
        Test("Navigation: failed save retains editor draft and committed data", () =>
        {
            File.Delete(Path.Combine(uiDirectory, "library.dat")); Directory.Delete(uiDirectory);
            File.WriteAllText(uiDirectory, "block storage for this test");
            try
            {
                Click(panel, "NewButton");
                var host = (ContentControl)panel.FindName("PageHost"); var editor = (SnippetView)host.Content;
                ((TextBox)editor.FindName("TitleInput")).Text = "Unsaved";
                ((TextBox)editor.FindName("ValueInput")).Text = "Keep this draft";
                Click(editor, "SaveButton"); Equal<object>(editor, host.Content);
                Check(!app.Library.State.Snippets.Any(s => s.Title == "Unsaved"));
                Equal("Keep this draft", ((TextBox)editor.FindName("ValueInput")).Text);
                Check(((TextBlock)editor.FindName("ErrorLabel")).Text.Length > 0);
                SendKey(panel, Key.Escape); Check(host.Content is null);
            }
            finally { File.Delete(uiDirectory); Directory.CreateDirectory(uiDirectory); }
        });
        panel.Hide();
        Test("System history: replacement, disabled/access errors and sensitive filtering", () =>
        {
            panel.OpenPanel(false);
            var old = new ClipEntry(Guid.NewGuid(), "before deletion", Now);
            historySource.Snapshot = new(HistoryStatus.Ready, [old]);
            Await(app.RefreshSystemHistoryAsync()); panel.RefreshRows(); Equal(1, app.History.Count);
            historySource.Snapshot = new(HistoryStatus.Ready, []);
            Await(app.RefreshSystemHistoryAsync()); panel.RefreshRows(); Equal(0, app.History.Count);
            Equal(0, ((ListBox)panel.FindName("Entries")).Items.Count);
            foreach (var state in new[] { HistoryStatus.Disabled, HistoryStatus.AccessDenied, HistoryStatus.Unavailable })
            {
                historySource.Snapshot = new(state, []);
                Await(app.RefreshSystemHistoryAsync()); panel.RefreshRows();
                Equal(Visibility.Visible, ((Button)panel.FindName("SystemSettingsButton")).Visibility);
                Equal(0, app.History.Count);
            }
            var secret = app.Library.State.Snippets.Single(s => s.IsSensitive).Value;
            historySource.Snapshot = new(HistoryStatus.Ready, [new(Guid.NewGuid(), secret, Now), old]);
            Await(app.RefreshSystemHistoryAsync()); panel.RefreshRows(); Equal(1, app.History.Count); Equal(old.Id, app.History[0].Id);
            Check(app.Library.State.History.All(c => c.Id != old.Id));
            panel.Hide();
            historySource.Snapshot = new(HistoryStatus.Ready, []);
            historySource.RaiseChanged();
            Equal(HistoryStatus.Loading, app.HistoryStatus); Equal(0, app.History.Count);
            panel.OpenPanel(false); Equal(HistoryStatus.Ready, app.HistoryStatus); Equal(0, app.History.Count);
            panel.Hide();
        });
        Test("System history: stale reads cannot restore deleted items; failures stay visible", () =>
        {
            var pending = new TaskCompletionSource<HistorySnapshot>();
            using var source = new FakeHistorySource { Read = () => pending.Task };
            using var history = new SystemHistoryService(source);
            var oldRead = history.RefreshAsync();
            source.Read = () => Task.FromResult(new HistorySnapshot(HistoryStatus.Ready, []));
            Await(history.RefreshAsync());
            pending.SetResult(new(HistoryStatus.Ready, [new(Guid.NewGuid(), "stale", Now)]));
            Await(oldRead); Equal(0, history.Snapshot.Items.Count);
            source.Read = () => throw new UnauthorizedAccessException();
            Await(history.RefreshAsync()); Equal(HistoryStatus.Unavailable, history.Snapshot.Status);
            source.ClearResult = false;
            var clear = history.ClearAsync(); Await(clear); Check(!clear.Result);
        });
        BitmapSource? imagePreview = null;
        Test("Images: native decoder preserves original dimensions, pixels and alpha", () =>
        {
            foreach (var (width, height) in new[] { (1200, 600), (4, 400), (1, 1) })
            {
                var pixels = new byte[width * height * 4];
                for (var i = 0; i < pixels.Length; i += 4)
                { pixels[i] = (byte)((i / 4) % 256); pixels[i + 1] = 120; pixels[i + 2] = 40; pixels[i + 3] = width == 1 ? (byte)128 : (byte)255; }
                var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var memory = new MemoryStream(); encoder.Save(memory); memory.Position = 0;
                using var stream = memory.AsRandomAccessStream();
                var decode = WindowsHistorySource.DecodeThumbnailAsync(stream); Await(decode);
                var thumbnail = decode.Result;
                Check(thumbnail.IsFrozen); Equal(width, thumbnail.PixelWidth); Equal(height, thumbnail.PixelHeight);
                if (width > 1) { var decoded = new byte[pixels.Length]; thumbnail.CopyPixels(decoded, width * 4, 0); Check(pixels.SequenceEqual(decoded)); }
                if (width == 1) { var pixel = new byte[4]; thumbnail.CopyPixels(pixel, 4, 0); Equal((byte)128, pixel[3]); Check(pixel[0] <= 91); }
                if (width == 1200) imagePreview = thumbnail;
            }
        });
        Test("Images: mixed rows, missing preview, restore original identity and expired-item failure", () =>
        {
            Check(imagePreview is not null);
            var imageId = Guid.NewGuid();
            historySource.Snapshot = new(HistoryStatus.Ready, [
                new(imageId, "", Now, true, imagePreview),
                new(Guid.NewGuid(), "文字与图片共用 Windows 历史", Now),
                new(Guid.NewGuid(), "", Now, true)]);
            panel.OpenPanel(false); panel.RefreshRows();
            var list = (ListBox)panel.FindName("Entries"); var rows = list.Items.Cast<EntryRow>().ToArray();
            Equal(3, rows.Length); Check(rows[0].IsImage && rows[0].Thumbnail is not null);
            Check(!rows[1].IsImage); Check(rows[2].IsImage && rows[2].Thumbnail is null);
            ThemeService.ApplyTheme(AppearanceMode.Light, AccentPalette.Blue);
            Render(panel, Path.Combine(artifactDirectory, "images-light.png"));
            ThemeService.ApplyTheme(AppearanceMode.Dark, AccentPalette.Purple);
            Render(panel, Path.Combine(artifactDirectory, "images-dark.png"));
            historySource.RestoreResult = false;
            var imageCard = (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(0);
            imageCard.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = Mouse.PreviewMouseUpEvent });
            Equal<Guid?>(imageId, historySource.RestoredImage); Check(panel.IsVisible);
            list.Focus(); SendKey(panel, Key.Enter);
            Equal<Guid?>(imageId, historySource.RestoredImage); Check(panel.IsVisible);
            Equal("图片已失效或剪贴板忙", ((TextBlock)panel.FindName("StatusLabel")).Text);
            historySource.RestoreResult = true; SendKey(panel, Key.Enter); Check(!panel.IsVisible);
            using var snapshot = new SystemHistoryService(historySource);
            Await(snapshot.RefreshAsync());
            historySource.Snapshot = new(HistoryStatus.Ready, []);
            var restore = snapshot.RestoreImageAsync(imageId); Await(restore); Check(!restore.Result);
            Check(app.Library.State.History.All(i => i.Id != imageId));
        });
        Test("Cards: more never pastes, Escape closes actions, delete/clear require confirmation", () =>
        {
            var id = Guid.NewGuid();
            historySource.Snapshot = new(HistoryStatus.Ready, [new(id, "卡片操作验证", Now)]);
            panel.OpenPanel(false); panel.UpdateLayout();
            var list = (ListBox)panel.FindName("Entries");
            Button RowButton(string label)
            {
                panel.UpdateLayout();
                return Descendants<Button>((DependencyObject)list.ItemContainerGenerator.ContainerFromIndex(0)).Single(b =>
                    Equals(b.Content, label) || System.Windows.Automation.AutomationProperties.GetName(b) == label);
            }
            var more = RowButton("更多操作");
            more.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = Mouse.PreviewMouseUpEvent });
            Check(panel.IsVisible);
            more.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Check(((EntryRow)list.Items[0]).ActionsOpen);
            SendKey(panel, Key.Escape); Check(panel.IsVisible); Check(!((EntryRow)list.Items[0]).ActionsOpen);
            RowButton("更多操作").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            RowButton("删除").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var confirmation = (InlineConfirmation)panel.FindName("Confirmation");
            Check(confirmation.IsOpen); Check(historySource.DeletedId is null);
            Click(confirmation, "CancelButton"); Equal(1, app.History.Count);
            RowButton("删除").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            historySource.DeleteResult = false; Click(confirmation, "ConfirmButton");
            Equal(1, app.History.Count); Equal("删除失败，请重试", ((TextBlock)panel.FindName("StatusLabel")).Text);
            historySource.DeleteResult = true;
            RowButton("更多操作").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            RowButton("删除").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Click(confirmation, "ConfirmButton");
            Equal<Guid?>(id, historySource.DeletedId); Equal(0, app.History.Count);
            Check(!((Button)panel.FindName("ClearHistoryButton")).IsEnabled);
            historySource.Snapshot = new(HistoryStatus.Ready, [new(Guid.NewGuid(), "clear test", Now)]);
            Await(app.RefreshSystemHistoryAsync()); panel.RefreshRows();
            Click(panel, "ClearHistoryButton"); Check(confirmation.IsOpen); Click(confirmation, "CancelButton"); Equal(1, app.History.Count);
            Click(panel, "ClearHistoryButton"); Click(confirmation, "ConfirmButton"); Equal(0, app.History.Count);
            SendKey(panel, Key.Right); Equal(Visibility.Collapsed, ((Grid)panel.FindName("HistoryToolbar")).Visibility);
            panel.Hide();
        });
        if (args.Contains("--system-history"))
            Test("Windows native history: read-only API probe (contents not logged)", () =>
            {
                using var native = new WindowsHistorySource();
                var read = native.ReadAsync(); Await(read);
                Console.WriteLine($"  Windows history status: {read.Result.Status}; text: {read.Result.Items.Count(i => !i.IsImage)}; images: {read.Result.Items.Count(i => i.IsImage)}");
                Check(Enum.IsDefined(read.Result.Status));
            });
        Directory.Delete(uiDirectory, recursive: true);
        if (args.Contains("--integration"))
            Test("Cross-process: restore a real input focus and paste", PasteIntegration.Verify);
        if (args.Contains("--expansion-integration"))
            Test("Cross-process: keyboard expansion preserves surrounding text and clipboard", () => Await(ExpansionIntegration.VerifyAsync()));
        if (args.Contains("--expansion-correction-integration"))
            Test("Cross-process: backspace correction, repeated deletion and navigation reset", () => Await(ExpansionIntegration.VerifyAsync(correctionsOnly: true)));
        Console.WriteLine($"\n{_passed} passed, {_failed} failed. Screenshots: {artifactDirectory}");
        return _failed == 0 ? 0 : 1;
    }

    private static void Click(FrameworkElement parent, string name) => ((Button)parent.FindName(name)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) yield return match;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }
    private static void Await(Task task)
    {
        if (!task.IsCompleted)
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            var frame = new DispatcherFrame();
            var timeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
            timeout.Tick += (_, _) => frame.Continue = false;
            timeout.Start();
            task.GetAwaiter().OnCompleted(() => dispatcher.BeginInvoke(new Action(() => frame.Continue = false)));
            Dispatcher.PushFrame(frame);
            timeout.Stop();
            if (!task.IsCompleted) throw new TimeoutException("Async UI test timed out");
        }
        task.GetAwaiter().GetResult();
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

internal sealed class FakeHistorySource : ISystemHistorySource
{
    public event Action? Changed;
    internal HistorySnapshot Snapshot { get; set; } = new(HistoryStatus.Ready, []);
    internal Func<Task<HistorySnapshot>>? Read { get; set; }
    internal int ClearCalls { get; private set; }
    internal bool ClearResult { get; set; } = true;
    internal Guid? RestoredImage { get; private set; }
    internal bool RestoreResult { get; set; } = true;
    internal Guid? DeletedId { get; private set; }
    internal bool DeleteResult { get; set; } = true;
    public Task<bool> DeleteAsync(Guid id)
    {
        DeletedId = id;
        if (DeleteResult) Snapshot = Snapshot with { Items = Snapshot.Items.Where(i => i.Id != id).ToArray() };
        return Task.FromResult(DeleteResult);
    }
    public Task<bool> RestoreImageAsync(Guid id) { RestoredImage = id; return Task.FromResult(RestoreResult && Snapshot.Items.Any(i => i.Id == id && i.IsImage)); }
    public Task<HistorySnapshot> ReadAsync() => Read?.Invoke() ?? Task.FromResult(Snapshot);
    public bool Clear()
    {
        ++ClearCalls;
        if (ClearResult) Snapshot = new(HistoryStatus.Ready, []);
        return ClearResult;
    }
    internal void RaiseChanged() => Changed?.Invoke();
    public void Dispose() { }
}

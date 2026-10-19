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
        if (args.Contains("--application-catalog"))
        {
            Test("Windows application catalog: read-only COM discovery, unique identities and frozen icons", () =>
            {
                var read = new WindowsApplicationCatalog().ReadAsync(); Await(read);
                var applications = read.Result;
                Check(applications.Count > 0);
                Equal(applications.Count, applications.Select(application => application.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
                Check(applications.All(application => application.Icon is null || application.Icon.IsFrozen));
                Console.WriteLine($"  Windows application count: {applications.Count}; icons: {applications.Count(application => application.Icon is not null)}");
            });
            return _failed == 0 ? 0 : 1;
        }
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
            Equal(AppearanceMode.System, library.State.Settings.Theme);
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
            SendMessage(events.Handle, PriorityHotkey.Message, nint.Zero, nint.Zero);
            Equal(2, hotkeys);
        });
        Test("Hotkey settings: defaults, validation, cloning and encrypted persistence", () =>
        {
            var shortcut = new WakeHotkey(6, 0x4B);
            Check(shortcut.IsValid); Equal("Ctrl+Shift+K", shortcut.ToString());
            Equal(WakeHotkey.Default, new AppSettings().WakeHotkey);
            foreach (var invalid in new[] { new WakeHotkey(0, 0x4B), new WakeHotkey(4, 0x41), new WakeHotkey(8, 0x20), new WakeHotkey(2, 0x7B) }) Check(!invalid.IsValid);
            var library = NewLibrary().WithSettings(new AppSettings { WakeHotkey = shortcut });
            Equal(shortcut, library.WithSettings(library.State.Settings).State.Settings.WakeHotkey);
            var invalidState = new AppState { Settings = new AppSettings { WakeHotkey = null! } };
            Equal(WakeHotkey.Default, new Library(invalidState).State.Settings.WakeHotkey);
            var directory = Path.Combine(Path.GetTempPath(), "ClipTap.Tests", Guid.NewGuid().ToString("N"));
            try
            {
                var store = new EncryptedStore(directory); store.Save(library.State);
                Equal(shortcut, store.Load().Settings.WakeHotkey);
            }
            finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
        });
        Test("Hotkey settings: registration conflicts and failed saves preserve previous binding", () =>
        {
            var free = new List<(DesktopEvents Events, WakeHotkey Key)>();
            try
            {
                for (uint key = 0x70; key <= 0x7A && free.Count < 3; key++)
                {
                    var shortcut = new WakeHotkey(7, key);
                    var listener = new DesktopEvents(() => { }, shortcut);
                    if (listener.HotkeyAvailable) free.Add((listener, shortcut)); else listener.Dispose();
                }
                Check(free.Count == 3);
                var saveCalled = false;
                Check(!free[0].Events.TryChange(free[1].Key, () => { saveCalled = true; return true; }, out var conflict));
                Check(!saveCalled && conflict.Length > 0);
                var candidate = free[2].Key; free[2].Events.Dispose(); free.RemoveAt(2);
                Check(!free[0].Events.TryChange(candidate, () => false, out var failure));
                Check(failure.Length > 0);
                using (var releasedCandidate = new DesktopEvents(() => { }, candidate)) Check(releasedCandidate.HotkeyAvailable);
                Check(free[0].Events.TryChange(candidate, () => true, out _));
                using var oldBinding = new DesktopEvents(() => { }, free[0].Key);
                Check(oldBinding.HotkeyAvailable);
                using var newBinding = new DesktopEvents(() => { }, candidate);
                Check(!newBinding.HotkeyAvailable);
            }
            finally { foreach (var entry in free) entry.Events.Dispose(); }
        });
        Test("Placement: physical monitor origins, scaled sizes and negative coordinates", () =>
        {
            Equal((2510, 160), MainWindow.PanelPosition(new NativeMethods.Rect { Left = 1920, Top = 0, Right = 3840, Bottom = 1600 }, 740, 1120));
            Equal((-1180, -220), MainWindow.PanelPosition(new NativeMethods.Rect { Left = -1920, Top = -480, Right = 0, Bottom = 1080 }, 440, 780));
            Equal((0, 0), MainWindow.PanelPosition(new NativeMethods.Rect { Right = 320, Bottom = 400 }, 380, 560));
            // The panel keeps its design size when the work area is roomy...
            Equal((520d, 600d), MainWindow.PanelFit(new NativeMethods.Rect { Right = 1920, Bottom = 1040 }, 1, 520, 600));
            // ...and is clamped to the work area when display scaling leaves too little room. The work
            // area is physical pixels, so 1560x1040 at 200% is only 780x520 DIP.
            Equal((520d, 520d), MainWindow.PanelFit(new NativeMethods.Rect { Right = 1560, Bottom = 1040 }, 2, 520, 600));
            Equal((400d, 600d), MainWindow.PanelFit(new NativeMethods.Rect { Right = 800, Bottom = 1200 }, 2, 520, 600));
            // A degenerate scale must not divide by zero.
            Equal((520d, 600d), MainWindow.PanelFit(new NativeMethods.Rect { Right = 1920, Bottom = 1040 }, 0, 520, 600));
        });
        Test("Hotkey: capture chord, suppress repeat, release ordering and preserve other shortcuts", () =>
        {
            foreach (var altFirst in new[] { false, true })
            {
                var gesture = new HotkeyGesture();
                Check(!gesture.Feed(0xA4, true, true, false).Suppress);
                Equal(new HotkeyDecision(true, true, false), gesture.Feed(0x20, true, true, false));
                Equal(new HotkeyDecision(true, false, false), gesture.Feed(0x20, true, true, false));
                if (altFirst)
                {
                    Equal(new HotkeyDecision(false, false, false), gesture.Feed(0xA4, false, false, false));
                    Equal(new HotkeyDecision(true, false, true), gesture.Feed(0x20, false, false, false));
                }
                else
                {
                    Equal(new HotkeyDecision(true, false, false), gesture.Feed(0x20, false, true, false));
                    Equal(new HotkeyDecision(false, false, true), gesture.Feed(0xA4, false, false, false));
                }
                Equal(default(HotkeyDecision), gesture.Feed(0x20, false, false, false));
            }
            var normal = new HotkeyGesture();
            Equal(default(HotkeyDecision), normal.Feed(0x20, true, false, false));
            Equal(default(HotkeyDecision), normal.Feed(0x20, true, true, true));
            Equal(default(HotkeyDecision), normal.Feed(0x09, true, true, false));
            Equal(default(HotkeyDecision), normal.Feed(0xA4, false, false, false));
            normal.Feed(0x20, true, true, false);
            normal.Feed(0x09, true, true, false);
            Check(!normal.Feed(0x20, false, true, false).Invoke);
            Check(!normal.Feed(0xA4, false, false, false).Invoke);
        });

        var uiDirectory = Path.Combine(Path.GetTempPath(), "ClipTap.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(uiDirectory);
        var historySource = new FakeHistorySource();
        var clipboardWriter = new FakeClipboardWriter();
        var app = new App(new EncryptedStore(uiDirectory), historySource, clipboardWriter) { Library = NewLibrary() };
        app.InitializeComponent();
        Await(app.RefreshSystemHistoryAsync());
        ThemeService.ApplyTheme(AppearanceMode.Light);
        var catalog = new FakeApplicationCatalog();
        var panel = new MainWindow(app, catalog);
        var panelSurface = (Border)panel.FindName("PanelSurface");
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
            panel.OpenSection(0);
            Equal("Alt+空格 唤起", ((TextBlock)panel.FindName("StatusLabel")).Text);
            Equal(0, ((ListBox)panel.FindName("Entries")).Items.Count);
            Equal(Visibility.Collapsed, ((StackPanel)panel.FindName("SnippetActions")).Visibility);
        });
        var artifactDirectory = Path.GetFullPath(args.FirstOrDefault(a => !a.StartsWith("--")) ?? "artifacts/test-results");
        Directory.CreateDirectory(artifactDirectory);
        Test("Home: compact search, prioritized apps, feature commands and safe activation", () =>
        {
            panel.OpenPanel(captureTarget: false); panel.UpdateLayout();
            var list = (ListBox)panel.FindName("Entries");
            var search = (TextBox)panel.FindName("SearchBox");
            try
            {
                Equal(0, list.Items.Count);
                Check(catalog.ReadCalls > 0 && search.IsKeyboardFocusWithin);
                Check(!((Border)panel.FindName("PreviewPane")).IsVisible);
                Check(!((Grid)panel.FindName("ListPage")).IsVisible);
                Check(panel.ActualHeight < 100 && list.SelectedItem is null);
                var emptyLaunches = catalog.LaunchCalls;
                SendKey(panel, Key.Enter);
                Equal(emptyLaunches, catalog.LaunchCalls); Check(panel.IsVisible);
                Render(panel, Path.Combine(artifactDirectory, "home-light.png"));
                ThemeService.ApplyTheme(AppearanceMode.Dark);
                Render(panel, Path.Combine(artifactDirectory, "home-dark.png"));
                ThemeService.ApplyTheme(AppearanceMode.Light);
                search.Text = "示例编辑器";
                Equal(1, list.Items.Count);
                Equal("示例编辑器", ((EntryRow)list.Items[0]).Title);
                panel.UpdateLayout();
                Check(panel.ActualHeight > 100 && panel.ActualHeight < 540);
                Check(((Grid)panel.FindName("ListPage")).IsVisible);
                Render(panel, Path.Combine(artifactDirectory, "home-search-light.png"));
                ThemeService.ApplyTheme(AppearanceMode.Dark);
                Render(panel, Path.Combine(artifactDirectory, "home-search-dark.png"));
                ThemeService.ApplyTheme(AppearanceMode.Light);
                var launchesBeforePin = catalog.LaunchCalls;
                RowPart<Button>(list, 0, "PinApplication").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Equal(launchesBeforePin, catalog.LaunchCalls);
                Check(app.Library.State.PinnedApplications.Contains("fixture.editor"));
                var restoredPins = new Library(new EncryptedStore(uiDirectory).Load());
                Check(restoredPins.State.PinnedApplications.Contains("fixture.editor"));
                Check(restoredPins.WithSettings(new AppSettings { Theme = AppearanceMode.Dark }).State.PinnedApplications.Contains("fixture.editor"));
                search.Clear(); Equal(0, list.Items.Count);
                panel.UpdateLayout(); Check(panel.ActualHeight < 100);
                search.Text = "demo";
                Equal(2, list.Items.Count);
                Equal("fixture.editor", ((EntryRow)list.Items[0]).Application?.Id);
                var pinnedIndex = list.Items.Cast<EntryRow>().ToList().FindIndex(row => row.Application?.Id == "fixture.editor");
                panel.UpdateLayout();
                RowPart<Button>(list, pinnedIndex, "PinApplication").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Equal(2, list.Items.Count);
                Equal(0, app.Library.State.PinnedApplications.Count);
                search.Text = "DEMO-EDITOR.EXE";
                Equal(1, list.Items.Count);
                Equal("示例编辑器", ((EntryRow)list.Items[0]).Title);
                search.Text = "no-such-installed-app";
                Equal(0, list.Items.Count);
                var launches = catalog.LaunchCalls;
                SendKey(panel, Key.Enter);
                Equal(launches, catalog.LaunchCalls); Check(panel.IsVisible);
                Render(panel, Path.Combine(artifactDirectory, "home-no-results.png"));
                search.Text = "   ";
                Equal(0, list.Items.Count);
                panel.UpdateLayout(); Check(panel.ActualHeight < 100);
                panel.ShowSettings(); panel.UpdateLayout();
                Check(panel.ActualHeight == 540);
                Click(panel, "BackButton"); panel.UpdateLayout();
                Check(panel.ActualHeight < 100 && search.IsKeyboardFocusWithin);
                // The shortcut menu remains usable even while the results/footer are collapsed.
                Click(panel, "MoreButton"); panel.UpdateLayout();
                Check(((Button)panel.FindName("MenuRefreshAppsButton")).IsVisible);
                Check(panel.ActualHeight > 100);
                SendKey(panel, Key.Escape); panel.UpdateLayout();
                Check(panel.ActualHeight < 100 && search.IsKeyboardFocusWithin);
                search.Text = "clipboard";
                list.SelectedItem = list.Items.Cast<EntryRow>().Single(row => row.Title == "Clipboard");
                SendKey(panel, Key.Enter);
                Check(panel.IsVisible && ((Border)panel.FindName("PreviewPane")).IsVisible);
                Equal(0, list.Items.Count);
                search.Text = "nothing in this feature";
                SendKey(panel, Key.Escape); Equal("", search.Text); Check(panel.IsVisible);
                SendKey(panel, Key.Escape);
                Equal(0, list.Items.Count);
                search.Text = "片段";
                list.SelectedItem = list.Items.Cast<EntryRow>().Single(row => row.Title.Contains("片段"));
                SendKey(panel, Key.Enter);
                Equal(Visibility.Visible, ((StackPanel)panel.FindName("SnippetActions")).Visibility);
                SendKey(panel, Key.Escape);
                Equal(0, list.Items.Count);
                search.Text = "demo-editor";
                SendKey(panel, Key.Enter);
                Equal("fixture.editor", catalog.LastLaunchedId);
                Equal(launches + 1, catalog.LaunchCalls); Check(!panel.IsVisible);
                panel.OpenPanel(captureTarget: false);
                catalog.LaunchResult = false;
                search.Text = "demo-calc";
                SendKey(panel, Key.Enter);
                Equal("fixture.calculator", catalog.LastLaunchedId);
                Equal(launches + 2, catalog.LaunchCalls); Check(panel.IsVisible);
                SendKey(panel, Key.Escape); Equal("", search.Text); Check(panel.IsVisible);
                SendKey(panel, Key.Escape); Check(!panel.IsVisible);
            }
            finally
            {
                catalog.LaunchResult = true;
                ThemeService.ApplyTheme(AppearanceMode.Light);
                panel.OpenSection(0);
                panel.Hide();
            }
        });
        Test("UI: the tab row switches tabs, the search box keeps the arrows, and secret titles stay hidden", () =>
        {
            app.Library.State.History.AddRange([
                new(Guid.NewGuid(), "让常用内容，随取随贴。", Now.AddSeconds(2)),
                new(Guid.NewGuid(), "https://github.com/langji3/cliptap", Now.AddSeconds(1)),
                new(Guid.NewGuid(), "欢迎使用 ClipTap", Now)]);
            historySource.Snapshot = new(HistoryStatus.Ready, app.Library.State.History.Select(c => (HistoryEntry)c).ToArray());
            Await(app.RefreshSystemHistoryAsync());
            app.Library.SaveSnippet(Snippet("常用邮箱", "hello@example.com", pinned: true));
            app.Library.SaveSnippet(Snippet("测试数据库密码", "never-visible-in-row", sensitive: true));
            OpenHistory(panel);
            panel.RefreshRows();
            panel.UpdateLayout();
            // The blank corner remains draggable; the central header now contains the search input.
            var header = (Grid)panel.FindName("HeaderDragArea");
            panel.UpdateLayout();
            Equal<object>(header, header.InputHitTest(new Point(2, 2)));
            var entries = (ListBox)panel.FindName("Entries");
            Equal(0, entries.SelectedIndex);
            var search = (TextBox)panel.FindName("SearchBox");
            // The search box holds focus on the list page, so arrows move the selection and never the tab.
            Check(search.IsKeyboardFocusWithin);
            foreach (var key in new[] { Key.Up, Key.Down, Key.Left, Key.Right, Key.Escape })
                Check(ListKeyRouting.Resolve(key, ModifierKeys.None, search.IsKeyboardFocusWithin, "", false)
                    is not (ListKeyAction.HistoryTab or ListKeyAction.SnippetsTab));
            // Ctrl+Tab cycles the tabs; the opposite direction is Ctrl+Shift+Tab.
            Equal(ListKeyAction.HistoryTab, ListKeyRouting.Resolve(Key.Tab, ModifierKeys.Control | ModifierKeys.Shift, false, "", true));
            SendKey(panel, Key.Down); Equal(1, entries.SelectedIndex);
            SendKey(panel, Key.Up); SendKey(panel, Key.Up); Equal(0, entries.SelectedIndex);
            SendKey(panel, Key.Right); Equal(3, entries.Items.Count); Equal(0, entries.SelectedIndex);
            Render(panel, Path.Combine(artifactDirectory, "clipboard.png"));
            SwitchTab(panel, 1); Equal(2, entries.Items.Count);
            // The snippets tool row carries the high-frequency create/edit entries, not a nested menu.
            Equal(Visibility.Visible, ((StackPanel)panel.FindName("SnippetActions")).Visibility);
            Check(((Button)panel.FindName("EditButton")).IsEnabled);
            var rows = entries.Items.Cast<EntryRow>().ToArray();
            Equal("常用邮箱", rows[0].Title); Check(rows.All(r => !r.Title.Contains("never-visible") && !r.Detail.Contains("never-visible")));
            Render(panel, Path.Combine(artifactDirectory, "snippets.png"));
            SwitchTab(panel, 0); Equal(3, entries.Items.Count);
            Equal(Visibility.Collapsed, ((StackPanel)panel.FindName("SnippetActions")).Visibility);
            SendKey(panel, Key.Escape); Check(panel.IsVisible);
            SendKey(panel, Key.Escape); Check(!panel.IsVisible);
            panel.OpenPanel(captureTarget: false);
            Equal(0, entries.Items.Count);
            panel.OpenSection(0); Equal(3, entries.Items.Count); Equal(0, entries.SelectedIndex);
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
            Check(((SolidColorBrush)panelSurface.Background).Color.R < 128);
            SwitchTab(panel, 1);
            panel.UpdateLayout();
            var themedList = (ListBox)panel.FindName("Entries");
            var themedRow = (ListBoxItem)themedList.ItemContainerGenerator.ContainerFromIndex(0);
            Equal(((SolidColorBrush)app.Resources["Ink"]).Color, ((SolidColorBrush)themedRow.Foreground).Color);
            Render(panel, Path.Combine(artifactDirectory, "snippets-dark.png"));
            SwitchTab(panel, 0);
            Render(panel, Path.Combine(artifactDirectory, "clipboard-dark.png"));
            SwitchTab(panel, 1);
            ((ListBox)panel.FindName("Entries")).SelectedIndex = 1;
            Click(panel, "EditButton");
            Render(panel, Path.Combine(artifactDirectory, "editor-dark.png"));
            Click(panel, "BackButton"); SwitchTab(panel, 0);
            app.Library.State.Settings.Theme = AppearanceMode.Dark;
            panel.ShowSettings();
            var settings = (SettingsView)((ContentControl)panel.FindName("PageHost")).Content;
            Render(panel, Path.Combine(artifactDirectory, "settings-dark.png"));
            ((RadioButton)settings.FindName("PurpleAccent")).IsChecked = true;
            Equal((Color)ColorConverter.ConvertFromString("#C3A6F0"), ((SolidColorBrush)app.Resources["Accent"]).Color);
            ((ComboBox)settings.FindName("ThemeInput")).SelectedValue = AppearanceMode.Light;
            Check(((SolidColorBrush)panelSurface.Background).Color.R > 128);
            Render(panel, Path.Combine(artifactDirectory, "settings-light.png"));
            Click(panel, "BackButton");
            Check(((SolidColorBrush)panelSurface.Background).Color.R < 128);
            Equal(AppearanceMode.Dark, app.Library.State.Settings.Theme);
            Equal(AccentPalette.Blue, app.Library.State.Settings.Accent);
            Equal((Color)ColorConverter.ConvertFromString("#8FB6FF"), ((SolidColorBrush)app.Resources["Accent"]).Color);
            ThemeService.ApplyTheme(AppearanceMode.Light);
            Check(((SolidColorBrush)panelSurface.Background).Color.R > 128);
            panel.Hide();
        });
        Test("Hotkey UI: escape cancels recording, discard preserves setting and reset requires save", () =>
        {
            var custom = new WakeHotkey(6, 0x4B);
            app.Library.State.Settings.WakeHotkey = custom;
            OpenHistory(panel); panel.ShowSettings();
            var host = (ContentControl)panel.FindName("PageHost");
            var settings = (SettingsView)host.Content;
            ((CheckBox)settings.FindName("StartupInput")).IsEnabled = false;
            Equal("Ctrl+Shift+K", ((Button)settings.FindName("HotkeyButton")).Content);
            Click(settings, "HotkeyButton"); Check(settings.IsRecordingHotkey);
            SendKey(panel, Key.Escape); Check(!settings.IsRecordingHotkey);
            Equal<object>(settings, host.Content);
            Click(settings, "ResetHotkeyButton"); Equal(custom, app.Library.State.Settings.WakeHotkey);
            Click(panel, "BackButton"); Equal(custom, app.Library.State.Settings.WakeHotkey);
            panel.ShowSettings(); settings = (SettingsView)host.Content;
            ((CheckBox)settings.FindName("StartupInput")).IsEnabled = false;
            Click(settings, "ResetHotkeyButton"); Click(settings, "SaveButton");
            Equal(WakeHotkey.Default, app.Library.State.Settings.WakeHotkey);
            panel.Hide();
        });
        Test("Theme: preference survives encrypted storage; defaults and invalid values follow the system", () =>
        {
            WithTempDirectory(directory =>
            {
                var store = new EncryptedStore(directory); var state = new AppState();
                Equal(AppearanceMode.System, state.Settings.Theme);
                state.Settings.Theme = AppearanceMode.Dark; state.Settings.Accent = AccentPalette.Purple; store.Save(state);
                Equal(AppearanceMode.Dark, store.Load().Settings.Theme);
                Equal(AccentPalette.Purple, store.Load().Settings.Accent);
                state.Settings.Theme = AppearanceMode.System; store.Save(state);
                Equal(AppearanceMode.System, new Library(store.Load()).State.Settings.Theme);
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
                var background = ((SolidColorBrush)panelSurface.Background).Color;
                // Real WCAG contrast, not a single-channel gap; body text must clear 4.5:1.
                Check(Contrast(ink, background) >= 4.5);
                Check(Contrast(((SolidColorBrush)app.Resources["Muted"]).Color, background) >= 4.5);
                Check(Contrast(ink, ((SolidColorBrush)app.Resources["Selected"]).Color) >= 4.5);
                // Filled primary buttons must stay readable in every accent.
                Check(Contrast(((SolidColorBrush)app.Resources["OnAccent"]).Color, ((SolidColorBrush)app.Resources["Accent"]).Color) >= 4.5);
                var list = (ListBox)panel.FindName("Entries");
                Equal(ink, ((SolidColorBrush)((ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(0)).Foreground).Color);
                Render(panel, Path.Combine(artifactDirectory, $"clipboard-{mode.ToString().ToLowerInvariant()}-{accent.ToString().ToLowerInvariant()}.png"));
            }
            panel.Hide();
        });
        Test("Palette: light and dark neutrals keep body, muted and accent text readable", () =>
        {
            // The acceptance floor is measured against the surface each colour actually sits on.
            foreach (var mode in new[] { AppearanceMode.Light, AppearanceMode.Dark })
            {
                ThemeService.ApplyTheme(mode, AccentPalette.Blue);
                var ink = ((SolidColorBrush)app.Resources["Ink"]).Color;
                var muted = ((SolidColorBrush)app.Resources["Muted"]).Color;
                var page = ((SolidColorBrush)app.Resources["Page"]).Color;
                var field = ((SolidColorBrush)app.Resources["FieldBackground"]).Color;
                var accent = ((SolidColorBrush)app.Resources["Accent"]).Color;
                var danger = ((SolidColorBrush)app.Resources["Danger"]).Color;
                Check(Contrast(ink, page) >= 4.5);
                Check(Contrast(ink, field) >= 4.5);
                Check(Contrast(muted, page) >= 4.5);
                Check(Contrast(muted, field) >= 4.5);
                Check(Contrast(accent, field) >= 4.5);
                Check(Contrast(danger, page) >= 4.5);
            }
            ThemeService.ApplyTheme(AppearanceMode.Light);
        });
        Test("Fields: filled single-line text fits its viewport and password padding is applied once", () =>
        {
            OpenHistory(panel);
            var search = (TextBox)panel.FindName("SearchBox");
            search.Text = "可见查询 visible";
            panel.UpdateLayout();
            CheckSingleLineViewport(search);
            SwitchTab(panel, 1);
            var list = (ListBox)panel.FindName("Entries");
            list.SelectedItem = list.Items.Cast<EntryRow>().First(row => row.Sensitive);
            Click(panel, "EditButton");
            try
            {
                var editor = (SnippetView)((ContentControl)panel.FindName("PageHost")).Content;
                var title = (TextBox)editor.FindName("TitleInput");
                var trigger = (TextBox)editor.FindName("TriggerInput");
                var secret = (PasswordBox)editor.FindName("SecretInput");
                title.Text = "已填写标题 Visible title";
                trigger.Text = "!visible";
                foreach (var mode in new[] { AppearanceMode.Light, AppearanceMode.Dark })
                {
                    ThemeService.ApplyTheme(mode);
                    panel.UpdateLayout();
                    CheckSingleLineViewport(title);
                    CheckSingleLineViewport(trigger);
                    Check(secret.IsVisible && secret.Password.Length > 0);
                    var host = (ScrollViewer)secret.Template.FindName("PART_ContentHost", secret);
                    var viewport = Descendants<ScrollContentPresenter>(host).First();
                    var origin = viewport.TransformToAncestor(secret).Transform(new Point());
                    Check(Math.Abs(origin.X - secret.BorderThickness.Left - secret.Padding.Left) <= 2);
                    Check(Math.Abs(origin.Y - secret.BorderThickness.Top - secret.Padding.Top) <= 2);
                    Check(host.ViewportHeight >= secret.FontSize && host.ScrollableHeight <= 1);
                    Render(panel, Path.Combine(artifactDirectory, $"fields-{mode.ToString().ToLowerInvariant()}.png"));
                }
            }
            finally
            {
                Click(panel, "BackButton");
                ThemeService.ApplyTheme(AppearanceMode.Light);
                panel.Hide();
            }
        });
        panel.Hide();
        Test("Navigation: editor stays in one window, preserves drafts, and saves inline", () =>
        {
            OpenHistory(panel); SwitchTab(panel, 1);
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
            panel.Hide(); OpenHistory(panel); Equal<object>(editor, host.Content); Equal("line one\nline two", value.Text);
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
            Click(editor, "DeleteButton"); panel.Hide(); OpenHistory(panel);
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
                var committedLibrary = app.Library;
                Check(!app.TrySaveSettings(new AppSettings { Theme = AppearanceMode.System }, out var saveError));
                Check(saveError.Contains("0x") && saveError != "保存失败，请重试");
                Check(ReferenceEquals(committedLibrary, app.Library));
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
            OpenHistory(panel);
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
            OpenHistory(panel); Equal(HistoryStatus.Ready, app.HistoryStatus); Equal(0, app.History.Count);
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
            var displayedImage = CreateImageFixture(1200, 700);
            var imageId = Guid.NewGuid();
            // Show explicitly: this test renders the panel, so it must not inherit visibility from earlier tests.
            panel.Show();
            historySource.Snapshot = new(HistoryStatus.Ready, [
                new(imageId, "", Now, true, displayedImage),
                new(Guid.NewGuid(), "文字与图片共用 Windows 历史", Now),
                new(Guid.NewGuid(), "", Now, true)]);
            OpenHistory(panel); panel.RefreshRows();
            var list = (ListBox)panel.FindName("Entries"); var rows = list.Items.Cast<EntryRow>().ToArray();
            Equal(3, rows.Length); Check(rows[0].IsImage && rows[0].Thumbnail is not null);
            Check(!rows[1].IsImage); Check(rows[2].IsImage && rows[2].Thumbnail is null);
            list.SelectedIndex = 0;
            panel.UpdateLayout();
            CheckImagePreview(panel, displayedImage, minimumWidth: 160, minimumHeight: 80);
            ThemeService.ApplyTheme(AppearanceMode.Light, AccentPalette.Blue);
            Render(panel, Path.Combine(artifactDirectory, "images-light.png"));
            ThemeService.ApplyTheme(AppearanceMode.Dark, AccentPalette.Purple);
            Render(panel, Path.Combine(artifactDirectory, "images-dark.png"));
            // Rendering can outlive a foreground transition; establish a visible input panel for gestures.
            OpenHistory(panel); panel.UpdateLayout();
            historySource.RestoreResult = false;
            try
            {
                var imageCard = (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(0);
                var restores = historySource.RestoreCalls;
                MouseGesture(imageCard, imageCard);
                Equal(restores, historySource.RestoreCalls); Check(panel.IsVisible);
                CheckImagePreview(panel, displayedImage, minimumWidth: 160, minimumHeight: 80);
                DoubleClickRow(imageCard);
                Equal(restores + 1, historySource.RestoreCalls);
                Equal<Guid?>(imageId, historySource.RestoredImage); Check(panel.IsVisible);
                list.Focus(); SendKey(panel, Key.Enter);
                Equal<Guid?>(imageId, historySource.RestoredImage); Check(panel.IsVisible);
                Equal("历史记录已失效或剪贴板忙", ((TextBlock)panel.FindName("StatusLabel")).Text);
                historySource.RestoreResult = true; SendKey(panel, Key.Enter); Check(!panel.IsVisible);
            }
            finally { historySource.RestoreResult = true; }
            using var snapshot = new SystemHistoryService(historySource);
            Await(snapshot.RefreshAsync());
            historySource.Snapshot = new(HistoryStatus.Ready, []);
            var restore = snapshot.RestoreItemAsync(imageId); Await(restore); Check(!restore.Result);
            Check(app.Library.State.History.All(i => i.Id != imageId));
        });
        Test("Images: native decoding bounds large and extremely narrow previews to 1600 pixels", () =>
        {
            foreach (var (width, height, expectedWidth, expectedHeight) in new[] { (3840, 2160, 1600, 900), (1, 8192, 1, 1600) })
            {
                var original = CreateImageFixture(width, height);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(original));
                using var memory = new MemoryStream(); encoder.Save(memory); memory.Position = 0;
                using var stream = memory.AsRandomAccessStream();
                var decode = WindowsHistorySource.DecodeThumbnailAsync(stream); Await(decode);
                var preview = decode.Result;
                Check(preview.IsFrozen);
                Equal(expectedWidth, preview.PixelWidth); Equal(expectedHeight, preview.PixelHeight);
                Check(preview.PixelWidth > 0 && preview.PixelHeight > 0);
                var pixels = new byte[preview.PixelWidth * preview.PixelHeight * 4];
                preview.CopyPixels(pixels, preview.PixelWidth * 4, 0);
                Check(pixels.Where((_, index) => index % 4 == 3).All(alpha => alpha == 255));
            }
        });
        Test("Images: wide strips open at pixel scale, zoom scrolls, fit restores and placeholders never overlap", () =>
        {
            var strip = CreateImageFixture(1498, 168);
            historySource.Snapshot = new(HistoryStatus.Ready, [new(Guid.NewGuid(), "", Now, true, strip), new(Guid.NewGuid(), "", Now.AddSeconds(-1), true)]);
            Await(app.RefreshSystemHistoryAsync()); OpenHistory(panel); panel.UpdateLayout();
            var list = (ListBox)panel.FindName("Entries");
            var preview = (Image)panel.FindName("PreviewImage");
            var viewport = (ScrollViewer)panel.FindName("ImageViewport");
            var dpi = VisualTreeHelper.GetDpi(panel);
            Check(Math.Abs(preview.Width * dpi.DpiScaleX - 1498) < 1);
            Check(viewport.ScrollableWidth > 0);
            Equal(Visibility.Collapsed, RowPart<System.Windows.Shapes.Path>(list, 0, "ImageFallbackGlyph").Visibility);
            Click(panel, "ImageDoubleButton"); panel.UpdateLayout();
            Check(Math.Abs(preview.Width * dpi.DpiScaleX - 2996) < 1);
            viewport.ScrollToRightEnd(); panel.UpdateLayout(); Check(viewport.HorizontalOffset > 0);
            Render(panel, Path.Combine(artifactDirectory, "images-wide-zoom.png"));
            Click(panel, "ImageFitButton"); panel.UpdateLayout();
            CheckImagePreview(panel, strip, minimumWidth: 160, minimumHeight: 1);
            Equal(ScrollBarVisibility.Disabled, viewport.HorizontalScrollBarVisibility);
            list.SelectedIndex = 1; panel.UpdateLayout();
            Equal(Visibility.Visible, RowPart<System.Windows.Shapes.Path>(list, 1, "ImageFallbackGlyph").Visibility);
            Equal(Visibility.Collapsed, ((StackPanel)panel.FindName("ImageZoomActions")).Visibility);
            Check(preview.Source is null);
        });
        Test("Images: portrait and narrow originals fit the detail pane; text and missing images never reuse it", () =>
        {
            var portrait = CreateImageFixture(360, 900);
            var narrow = CreateImageFixture(40, 900);
            const string text = "这是文本，不应该保留上一张图片。";
            historySource.Snapshot = new(HistoryStatus.Ready, [
                new(Guid.NewGuid(), "", Now, true, portrait),
                new(Guid.NewGuid(), "", Now.AddSeconds(-1), true, narrow),
                new(Guid.NewGuid(), text, Now.AddSeconds(-2)),
                new(Guid.NewGuid(), "", Now.AddSeconds(-3), true)]);
            Await(app.RefreshSystemHistoryAsync()); OpenHistory(panel);
            var list = (ListBox)panel.FindName("Entries");
            var preview = (Image)panel.FindName("PreviewImage");
            ThemeService.ApplyTheme(AppearanceMode.Light);
            list.SelectedIndex = 0; panel.UpdateLayout();
            CheckImagePreview(panel, portrait, minimumWidth: 32, minimumHeight: 160);
            Render(panel, Path.Combine(artifactDirectory, "images-portrait.png"));
            list.SelectedIndex = 1; panel.UpdateLayout();
            CheckImagePreview(panel, narrow, minimumWidth: 1, minimumHeight: 160);
            Render(panel, Path.Combine(artifactDirectory, "images-narrow.png"));
            list.SelectedIndex = 2; panel.UpdateLayout();
            Check(!((Border)panel.FindName("PreviewImageFrame")).IsVisible);
            Check(preview.Source is null);
            Equal(text, ((TextBox)panel.FindName("PreviewText")).Text);
            list.SelectedIndex = 3; panel.UpdateLayout();
            Check(preview.Source is null && !preview.IsVisible);
            Check(((StackPanel)panel.FindName("PreviewEmpty")).IsVisible);
            Check(RowPart<System.Windows.Shapes.Path>(list, 3, "ImageFallbackGlyph").IsVisible);
            Equal("图片", RowPart<TextBlock>(list, 3, "RowTitle").Text);
            Render(panel, Path.Combine(artifactDirectory, "images-missing.png"));
            panel.Hide();
        });
        Test("Preview: complete selectable text, ordinary snippets, concealed secrets and empty selection", () =>
        {
            var fullText = "完整正文\n" + new string('甲', 300) + "\nFULL-TEXT-END";
            historySource.Snapshot = new(HistoryStatus.Ready, [new(Guid.NewGuid(), fullText, Now)]);
            Await(app.RefreshSystemHistoryAsync()); OpenHistory(panel); panel.UpdateLayout();
            var list = (ListBox)panel.FindName("Entries");
            var text = (TextBox)panel.FindName("PreviewText");
            Check(text.IsVisible && text.IsReadOnly);
            Equal(fullText, text.Text);
            text.SelectAll(); Equal(fullText, text.SelectedText);
            Check(!((Border)panel.FindName("PreviewImageFrame")).IsVisible);
            Render(panel, Path.Combine(artifactDirectory, "preview-text.png"));
            SwitchTab(panel, 1);
            var ordinary = app.Library.State.Snippets.First(snippet => !snippet.IsSensitive);
            list.SelectedItem = list.Items.Cast<EntryRow>().Single(row => row.Id == ordinary.Id);
            panel.UpdateLayout(); Equal(ordinary.Value, text.Text);
            var sensitive = app.Library.State.Snippets.First(snippet => snippet.IsSensitive);
            list.SelectedItem = list.Items.Cast<EntryRow>().Single(row => row.Id == sensitive.Id);
            panel.UpdateLayout();
            Check(!text.Text.Contains(sensitive.Value));
            Check(!((TextBlock)panel.FindName("PreviewTitle")).Text.Contains(sensitive.Value));
            Check(!((TextBlock)panel.FindName("PreviewMeta")).Text.Contains(sensitive.Value));
            Check(((Image)panel.FindName("PreviewImage")).Source is null);
            Render(panel, Path.Combine(artifactDirectory, "preview-sensitive.png"));
            ((TextBox)panel.FindName("SearchBox")).Text = "no-such-preview-result";
            panel.UpdateLayout();
            Check(list.SelectedItem is null);
            Equal("", text.Text);
            Check(((Image)panel.FindName("PreviewImage")).Source is null);
            Check(((StackPanel)panel.FindName("PreviewEmpty")).IsVisible);
            Check(!((Button)panel.FindName("PasteButton")).IsEnabled);
            panel.Hide();
        });
        Test("Paste: history restores original identity and selection; secrets need no confirmation", () =>
        {
            var first = new HistoryEntry(Guid.NewGuid(), "first", Now);
            var second = new HistoryEntry(Guid.NewGuid(), "second", Now.AddSeconds(-1));
            historySource.Snapshot = new(HistoryStatus.Ready, [first, second]);
            OpenHistory(panel); panel.UpdateLayout();
            var list = (ListBox)panel.FindName("Entries");
            list.SelectedIndex = 1; list.Focus();
            var writes = clipboardWriter.Writes;
            Click(panel, "PasteButton");
            Equal<Guid?>(second.Id, historySource.RestoredImage);
            Equal(writes, clipboardWriter.Writes); Check(!panel.IsVisible);
            historySource.Snapshot = new(HistoryStatus.Ready, [second, first]); // OS last-used ordering
            OpenHistory(panel);
            Equal(first.Id, ((EntryRow)list.Items[0]).Id);
            Equal(second.Id, ((EntryRow)list.SelectedItem).Id);
            var newest = new HistoryEntry(Guid.NewGuid(), "new copy", Now.AddSeconds(1));
            historySource.Snapshot = new(HistoryStatus.Ready, [second, newest, first]);
            Await(app.RefreshSystemHistoryAsync());
            panel.RefreshHistory();
            Equal(newest.Id, ((EntryRow)list.Items[0]).Id);
            Equal(second.Id, ((EntryRow)list.SelectedItem).Id);
            SwitchTab(panel, 1);
            list.SelectedItem = list.Items.Cast<EntryRow>().First(r => r.Sensitive);
            list.Focus(); SendKey(panel, Key.Enter);
            Check(!((InlineConfirmation)panel.FindName("Confirmation")).IsOpen);
            Equal(writes + 1, clipboardWriter.Writes); Check(clipboardWriter.Sensitive);
            Check(!panel.IsVisible);
        });
        Test("Cards: more never pastes, Escape closes actions, delete/clear require confirmation", () =>
        {
            var id = Guid.NewGuid();
            historySource.Snapshot = new(HistoryStatus.Ready, [new(id, "卡片操作验证", Now)]);
            OpenHistory(panel); panel.UpdateLayout();
            var list = (ListBox)panel.FindName("Entries");
            Button RowButton(string label)
            {
                panel.UpdateLayout();
                return Descendants<Button>((DependencyObject)list.ItemContainerGenerator.ContainerFromIndex(0)).Single(b =>
                    Equals(b.Content, label) || System.Windows.Automation.AutomationProperties.GetName(b) == label);
            }
            var more = RowButton("更多操作");
            MouseGesture(more, more);
            Check(panel.IsVisible);
            more.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Check(((EntryRow)list.Items[0]).ActionsOpen);
            panel.UpdateLayout();
            Equal(Visibility.Visible, RowPart<Button>(list, 0, "DeleteAction").Visibility);
            Equal(Visibility.Collapsed, RowPart<TextBlock>(list, 0, "RowTime").Visibility);
            Equal(Visibility.Collapsed, RowPart<StackPanel>(list, 0, "RowHandles").Visibility);
            Render(panel, Path.Combine(artifactDirectory, "history-actions.png"));
            SendKey(panel, Key.Escape); Check(panel.IsVisible); Check(!((EntryRow)list.Items[0]).ActionsOpen);
            panel.UpdateLayout();
            Equal(Visibility.Collapsed, RowPart<Button>(list, 0, "DeleteAction").Visibility);
            Equal(Visibility.Visible, RowPart<TextBlock>(list, 0, "RowTime").Visibility);
            Equal(Visibility.Visible, RowPart<StackPanel>(list, 0, "RowHandles").Visibility);
            RowButton("更多操作").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            RowButton("删除").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var confirmation = (InlineConfirmation)panel.FindName("Confirmation");
            Check(confirmation.IsOpen); Check(historySource.DeletedId is null);
            Click(confirmation, "CancelButton"); Equal(1, app.History.Count);
            RowButton("更多操作").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            RowButton("删除").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            historySource.DeleteResult = false; Click(confirmation, "ConfirmButton");
            Equal(1, app.History.Count); Equal("删除失败，请重试", ((TextBlock)panel.FindName("StatusLabel")).Text);
            historySource.DeleteResult = true;
            RowButton("更多操作").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            RowButton("删除").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Click(confirmation, "ConfirmButton");
            Equal<Guid?>(id, historySource.DeletedId); Equal(0, app.History.Count);
            Check(!((Button)panel.FindName("MenuClearButton")).IsEnabled);
            historySource.Snapshot = new(HistoryStatus.Ready, [new(Guid.NewGuid(), "clear test", Now)]);
            Await(app.RefreshSystemHistoryAsync()); panel.RefreshRows();
            Check(((Button)panel.FindName("MenuClearButton")).IsEnabled);
            // Clear always targets the whole Windows history, even while a query narrows the visible list.
            ((TextBox)panel.FindName("SearchBox")).Text = "no such entry anywhere";
            panel.RefreshRows();
            Equal(0, list.Items.Count);
            Click(panel, "MoreButton");
            Click(panel, "MenuClearButton");
            Check(confirmation.IsOpen); Click(confirmation, "CancelButton"); Equal(1, app.History.Count);
            Click(panel, "MoreButton"); Click(panel, "MenuClearButton");
            Click(confirmation, "ConfirmButton"); Equal(0, app.History.Count);
            // A tab switch drops the query and hands focus back to the search box.
            SwitchTab(panel, 1);
            Equal("", ((TextBox)panel.FindName("SearchBox")).Text);
            Check(((TextBox)panel.FindName("SearchBox")).IsKeyboardFocusWithin);
            Equal(Visibility.Visible, ((StackPanel)panel.FindName("SnippetActions")).Visibility);
            panel.Hide();
        });
        // ---- Search behaviour -------------------------------------------------
        Test("Search: matches complete history text, including beyond the preview truncation", () =>
        {
            // 300 head characters plus the marker, so the needle sits past the 160-char row preview.
            var longText = new string('甲', 300) + "NEEDLE-AT-THE-END";
            historySource.Snapshot = new(HistoryStatus.Ready,
                [new(Guid.NewGuid(), longText, Now), new(Guid.NewGuid(), "短文本", Now.AddSeconds(-1))]);
            Await(app.RefreshSystemHistoryAsync());
            OpenHistory(panel);
            var list = (ListBox)panel.FindName("Entries");
            var search = (TextBox)panel.FindName("SearchBox");
            Equal(2, list.Items.Count);
            Check(!((EntryRow)list.Items[0]).Title.Contains("NEEDLE"));
            search.Text = "needle-at-the-end";
            Equal(1, list.Items.Count);
            Equal("文本", ((EntryRow)list.Items[0]).Meta);
            panel.UpdateLayout();
            var meta = RowPart<TextBlock>(list, 0, "RowMeta");
            Check(meta.IsVisible && meta.ActualHeight > 0);
            Equal("文本", meta.Text);
            // Clearing the query restores the untouched snapshot order.
            search.Text = "";
            Equal(2, list.Items.Count);
            Equal(longText, app.History[0].Text);
            panel.Hide();
        });
        Test("Search: Chinese and ASCII match case-insensitively within the current tab", () =>
        {
            historySource.Snapshot = new(HistoryStatus.Ready,
                [new(Guid.NewGuid(), "HTTPS://Example.COM/Docs", Now), new(Guid.NewGuid(), "今天会议的结论", Now.AddSeconds(-1))]);
            Await(app.RefreshSystemHistoryAsync());
            OpenHistory(panel);
            var list = (ListBox)panel.FindName("Entries");
            var search = (TextBox)panel.FindName("SearchBox");
            search.Text = "example.com/docs";
            Equal(1, list.Items.Count);
            search.Text = "会议";
            Equal(1, list.Items.Count);
            Equal("今天会议的结论", ((EntryRow)list.Items[0]).Title);
            // Surrounding whitespace is trimmed; a whitespace-only query is not a query.
            search.Text = "  会议  ";
            Equal(1, list.Items.Count);
            search.Text = "   ";
            Equal(2, list.Items.Count);
            panel.Hide();
        });
        Test("Search: snippet bodies match, sensitive bodies never do", () =>
        {
            app.TryUpdateLibrary(library =>
            {
                library.State.Snippets.Clear();
                library.SaveSnippet(Snippet("普通片段", "ordinary-body-marker"));
                library.SaveSnippet(Snippet("敏感片段", "secret-body-marker", sensitive: true));
                library.State.Snippets[1] = library.State.Snippets[1] with { Trigger = "!sec" };
            });
            OpenHistory(panel); SwitchTab(panel, 1);
            var list = (ListBox)panel.FindName("Entries");
            var search = (TextBox)panel.FindName("SearchBox");
            Equal(2, list.Items.Count);
            search.Text = "ordinary-body-marker";
            Equal(1, list.Items.Count);
            Equal("普通片段", ((EntryRow)list.Items[0]).Title);
            panel.UpdateLayout();
            Equal(Visibility.Collapsed, RowPart<TextBlock>(list, 0, "RowMeta").Visibility);
            // The sensitive value is invisible to search, so its absence cannot be used to probe it.
            search.Text = "secret-body-marker";
            Equal(0, list.Items.Count);
            // Title and trigger still match normally, and the row still leaks no body text.
            search.Text = "敏感片段";
            Equal(1, list.Items.Count);
            var sensitiveRow = (EntryRow)list.Items[0];
            Check(!sensitiveRow.Title.Contains("secret-body-marker")
                && !sensitiveRow.Detail.Contains("secret-body-marker")
                && !sensitiveRow.Meta.Contains("secret-body-marker")
                && !(sensitiveRow.Hint ?? "").Contains("secret-body-marker"));
            search.Text = "!sec";
            Equal(1, list.Items.Count);
            panel.UpdateLayout();
            var triggerMeta = RowPart<TextBlock>(list, 0, "RowMeta");
            Check(triggerMeta.IsVisible && triggerMeta.ActualHeight > 0);
            Check(triggerMeta.Text.Contains("!sec"));
            var sensitiveContainer = (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(0);
            var triggerHint = Descendants<Grid>(sensitiveContainer).Select(grid => grid.ToolTip).OfType<string>().Single();
            Check(triggerHint.Contains("!sec") && !triggerHint.Contains("secret-body-marker"));
            Render(panel, Path.Combine(artifactDirectory, "snippet-trigger.png"));
            search.Text = "";
            panel.Hide();
        });
        Test("Search: the snippet no-results action clears the query without opening an editor", () =>
        {
            OpenHistory(panel); SwitchTab(panel, 1);
            var search = (TextBox)panel.FindName("SearchBox");
            var list = (ListBox)panel.FindName("Entries");
            var count = list.Items.Count;
            Check(count > 0);
            search.Text = "no-snippet-can-match-this-query";
            Equal(0, list.Items.Count);
            var action = (Button)panel.FindName("EmptyActionButton");
            Check(action.IsVisible);
            Equal<object>("清空搜索", action.Content);
            Click(panel, "EmptyActionButton");
            Check(((ContentControl)panel.FindName("PageHost")).Content is null);
            Equal("", search.Text);
            Equal(count, list.Items.Count);
            Check(search.IsKeyboardFocusWithin);
            panel.Hide();
        });
        Test("Search: no results clear the selection and Enter performs no action", () =>
        {
            var id = Guid.NewGuid();
            historySource.Snapshot = new(HistoryStatus.Ready, [new(id, "唯一记录", Now)]);
            Await(app.RefreshSystemHistoryAsync());
            OpenHistory(panel);
            var list = (ListBox)panel.FindName("Entries");
            var search = (TextBox)panel.FindName("SearchBox");
            search.Text = "完全不存在的内容";
            Equal(0, list.Items.Count);
            Equal(-1, list.SelectedIndex);
            Check(list.SelectedItem is null);
            var writes = clipboardWriter.Writes;
            list.Focus();
            SendKey(panel, Key.Enter);
            // Enter with no results must not copy, restore or hide anything.
            Equal(writes, clipboardWriter.Writes);
            Check(panel.IsVisible);
            // Escape unwinds the query before the panel.
            SendKey(panel, Key.Escape);
            Check(panel.IsVisible);
            Equal("", search.Text);
            Equal(1, list.Items.Count);
            SendKey(panel, Key.Escape);
            Check(panel.IsVisible);
            SendKey(panel, Key.Escape);
            Check(!panel.IsVisible);
            panel.Hide();
        });
        Test("Search: arrow selection still works, and a removed item is not remembered", () =>
        {
            var first = new HistoryEntry(Guid.NewGuid(), "第一条", Now);
            var second = new HistoryEntry(Guid.NewGuid(), "第二条", Now.AddSeconds(-1));
            historySource.Snapshot = new(HistoryStatus.Ready, [first, second]);
            Await(app.RefreshSystemHistoryAsync());
            OpenHistory(panel);
            var list = (ListBox)panel.FindName("Entries");
            var search = (TextBox)panel.FindName("SearchBox");
            search.Text = "条";
            Equal(2, list.Items.Count);
            Equal(0, list.SelectedIndex);
            SendKey(panel, Key.Down); Equal(1, list.SelectedIndex);
            SendKey(panel, Key.Up); Equal(0, list.SelectedIndex);
            // No results, then a changed result set, must not keep a stale selection alive.
            search.Text = "第一条";
            Equal(1, list.Items.Count);
            Equal(first.Id, ((EntryRow)list.SelectedItem!).Id);
            historySource.Snapshot = new(HistoryStatus.Ready, [second]);
            Await(app.RefreshSystemHistoryAsync());
            panel.RefreshHistory();
            Equal(0, list.Items.Count);
            Check(list.SelectedItem is null);
            search.Text = "";
            Equal(1, list.Items.Count);
            Equal(second.Id, ((EntryRow)list.SelectedItem!).Id);
            panel.Hide();
        });
        Test("Search: tabs, pages and re-summoning keep or reset the query as specified", () =>
        {
            historySource.Snapshot = new(HistoryStatus.Ready, [new(Guid.NewGuid(), "上下文记录", Now)]);
            Await(app.RefreshSystemHistoryAsync());
            app.TryUpdateLibrary(library =>
            {
                library.State.Snippets.Clear();
                library.SaveSnippet(Snippet("上下文片段", "ctx-body"));
            });
            OpenHistory(panel);
            var list = (ListBox)panel.FindName("Entries");
            var search = (TextBox)panel.FindName("SearchBox");
            search.Text = "上下文记录";
            Equal(1, list.Items.Count);
            // Switching tabs drops the query rather than showing an empty list for the wrong reason.
            SwitchTab(panel, 1);
            Equal("", search.Text);
            Equal(1, list.Items.Count);
            Check(search.IsKeyboardFocusWithin);
            // Settings keeps the query and restores it on return.
            search.Text = "上下文片段";
            Equal(1, list.Items.Count);
            panel.ShowSettings();
            Check(panel.FindName("PageHost") is ContentControl { Content: SettingsView });
            Click(panel, "BackButton");
            Equal("上下文片段", search.Text);
            Equal(1, list.Items.Count);
            // The editor page keeps the list context too.
            Click(panel, "EditButton");
            var host = (ContentControl)panel.FindName("PageHost");
            Check(host.Content is SnippetView);
            Click(panel, "BackButton");
            Equal("上下文片段", search.Text);
            // Re-summoning resets to Home; explicitly entering history then shows its remembered selection.
            panel.Hide(); panel.OpenPanel(captureTarget: false);
            Equal("", search.Text);
            Equal(0, list.Items.Count);
            panel.OpenSection(0);
            Equal(1, list.Items.Count);
            Check(search.IsKeyboardFocusWithin);
            panel.Hide();
        });
        Test("Keys: either Ctrl+Tab direction cycles from each actual tab", () =>
        {
            OpenHistory(panel);
            var search = (TextBox)panel.FindName("SearchBox");
            var actions = (StackPanel)panel.FindName("SnippetActions");
            foreach (var modifiers in new[] { ModifierKeys.Control, ModifierKeys.Control | ModifierKeys.Shift })
            {
                SwitchTab(panel, 0);
                foreach (var onSnippets in new[] { false, true })
                {
                    Equal(onSnippets, actions.Visibility == Visibility.Visible);
                    search.Text = "query before switching";
                    var resolved = ListKeyRouting.Resolve(Key.Tab, modifiers, search.IsKeyboardFocusWithin,
                        ClipboardSearch.Normalize(search.Text), actions.Visibility == Visibility.Visible);
                    Equal(onSnippets ? ListKeyAction.HistoryTab : ListKeyAction.SnippetsTab, resolved);
                    Await(panel.ApplyListActionAsync(resolved));
                    Equal(!onSnippets, actions.Visibility == Visibility.Visible);
                    Equal("", search.Text);
                    Check(search.IsKeyboardFocusWithin);
                }
            }
            panel.Hide();
        });
        Test("Menu: opening focuses its action, Tab and Enter remain available, Escape restores search", () =>
        {
            historySource.Snapshot = new(HistoryStatus.Ready, [new(Guid.NewGuid(), "菜单键盘验证", Now)]);
            Await(app.RefreshSystemHistoryAsync());
            OpenHistory(panel);
            Click(panel, "MoreButton");
            panel.UpdateLayout();
            var layer = (Grid)panel.FindName("MenuLayer");
            var clear = (Button)panel.FindName("MenuClearButton");
            var restores = historySource.RestoreCalls;
            Check(clear.IsKeyboardFocusWithin);
            foreach (var key in new[] { Key.Tab, Key.Enter })
            {
                // Raise the preview from the focused menu control: the window must leave it to WPF's
                // focus navigation / Button handling, rather than swallowing it or pasting the list.
                var input = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(panel), 0, key)
                    { RoutedEvent = Keyboard.PreviewKeyDownEvent };
                clear.RaiseEvent(input);
                Check(!input.Handled);
                Equal(Visibility.Visible, layer.Visibility);
                Equal(restores, historySource.RestoreCalls);
                if (key == Key.Tab)
                {
                    clear.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
                    Check(layer.IsKeyboardFocusWithin);
                }
            }
            var clears = historySource.ClearCalls;
            clear.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(panel), 0, Key.Enter)
                { RoutedEvent = Keyboard.KeyDownEvent });
            var confirmation = (InlineConfirmation)panel.FindName("Confirmation");
            Check(confirmation.IsOpen);
            Equal(clears, historySource.ClearCalls);
            Click(confirmation, "CancelButton");
            Click(panel, "MoreButton");
            SendKey(panel, Key.Escape);
            Equal(Visibility.Collapsed, layer.Visibility);
            Check(((TextBox)panel.FindName("SearchBox")).IsKeyboardFocusWithin);
            Check(panel.IsVisible);
            panel.Hide();
            // With no Windows history the command is disabled, even if snippets still offer a paste target.
            historySource.Snapshot = new(HistoryStatus.Ready, []);
            Await(app.RefreshSystemHistoryAsync());
            OpenHistory(panel); SwitchTab(panel, 1);
            Check(((ListBox)panel.FindName("Entries")).Items.Count > 0);
            var writes = clipboardWriter.Writes;
            Click(panel, "MoreButton");
            Check(!clear.IsEnabled && layer.IsKeyboardFocusWithin);
            foreach (var key in new[] { Key.Tab, Key.Enter }) SendKey(panel, key);
            Equal(Visibility.Visible, layer.Visibility);
            Equal(writes, clipboardWriter.Writes);
            Equal(restores, historySource.RestoreCalls);
            Check(!confirmation.IsOpen && panel.IsVisible);
            SendKey(panel, Key.Escape);
            Check(((TextBox)panel.FindName("SearchBox")).IsKeyboardFocusWithin);
            panel.Hide();
        });
        Test("Search: clearing the menu and the search box never pastes", () =>
        {
            var id = Guid.NewGuid();
            historySource.Snapshot = new(HistoryStatus.Ready, [new(id, "不应被粘贴", Now)]);
            Await(app.RefreshSystemHistoryAsync());
            OpenHistory(panel);
            // Pin the theme so the recorded search screenshots are the light variant.
            ThemeService.ApplyTheme(AppearanceMode.Light);
            panel.UpdateLayout();
            var list = (ListBox)panel.FindName("Entries");
            var search = (TextBox)panel.FindName("SearchBox");
            var writesBefore = clipboardWriter.Writes;
            search.Text = "不应";
            Equal(1, list.Items.Count);
            Render(panel, Path.Combine(artifactDirectory, "search-results.png"));
            search.Text = "没有任何记录会匹配这一段";
            Equal(0, list.Items.Count);
            Render(panel, Path.Combine(artifactDirectory, "search-empty.png"));
            search.Text = "不应";
            Equal(1, list.Items.Count);
            // The search clear button, the row handles and the toolbar menu must not start a paste.
            Click(panel, "ClearSearchButton");
            Check(panel.IsVisible); Equal("", search.Text);
            Click(panel, "MoreButton");
            Check(panel.IsVisible);
            Equal(writesBefore, clipboardWriter.Writes);
            panel.Hide();
        });
        Test("Search: dismissing the more menu must not paste the row it uncovers", () =>
        {
            var id = Guid.NewGuid();
            historySource.Snapshot = new(HistoryStatus.Ready, [new(id, "菜单下方记录", Now)]);
            Await(app.RefreshSystemHistoryAsync());
            OpenHistory(panel);
            var list = (ListBox)panel.FindName("Entries");
            var writes = clipboardWriter.Writes;
            var restores = historySource.RestoreCalls;
            Click(panel, "MoreButton");
            Equal(Visibility.Visible, ((Grid)panel.FindName("MenuLayer")).Visibility);
            // A press on the backdrop closes the menu; the matching release lands on the row underneath.
            // A real gesture raises both the preview and the promoted event, so both are raised here.
            var backdrop = (Border)panel.FindName("MenuBackdrop");
            backdrop.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = Mouse.PreviewMouseDownEvent });
            backdrop.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = Mouse.MouseDownEvent });
            Equal(Visibility.Collapsed, ((Grid)panel.FindName("MenuLayer")).Visibility);
            var row = (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(0);
            row.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = Mouse.PreviewMouseUpEvent });
            // The gesture began outside the row, so it must not paste, copy or close the panel.
            Equal(writes, clipboardWriter.Writes);
            Equal(restores, historySource.RestoreCalls);
            Check(panel.IsVisible);
            // Focus returns to the search box so typing keeps working after the menu closes.
            Check(((TextBox)panel.FindName("SearchBox")).IsKeyboardFocusWithin);
            // A single click only previews; a complete double click activates that same item.
            MouseGesture(row, row);
            Equal(restores, historySource.RestoreCalls);
            Check(panel.IsVisible);
            DoubleClickRow(row);
            Equal<Guid?>(id, historySource.RestoredImage);
            Equal(restores + 1, historySource.RestoreCalls);
            Check(!panel.IsVisible);
            panel.Hide();
        });
        Test("Mouse: release-only, footer-to-row and cross-row gestures never restore history", () =>
        {
            var first = new HistoryEntry(Guid.NewGuid(), "鼠标第一行", Now);
            var second = new HistoryEntry(Guid.NewGuid(), "鼠标第二行", Now.AddSeconds(-1));
            historySource.Snapshot = new(HistoryStatus.Ready, [first, second]);
            Await(app.RefreshSystemHistoryAsync());
            OpenHistory(panel); panel.UpdateLayout();
            var list = (ListBox)panel.FindName("Entries");
            var firstRow = (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(0);
            var secondRow = (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(1);
            var restores = historySource.RestoreCalls;
            var writes = clipboardWriter.Writes;
            firstRow.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
                { RoutedEvent = Mouse.PreviewMouseUpEvent });
            RaiseDoubleClick(firstRow);
            Equal(restores, historySource.RestoreCalls);
            MouseGesture((TextBlock)panel.FindName("StatusLabel"), firstRow);
            RaiseDoubleClick(firstRow);
            Equal(restores, historySource.RestoreCalls);
            MouseGesture(firstRow, secondRow);
            RaiseDoubleClick(secondRow);
            Equal(restores, historySource.RestoreCalls);
            Equal(writes, clipboardWriter.Writes);
            Check(panel.IsVisible);
            // A refresh can reuse the visual container; identity must still come from the pressed item.
            firstRow.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
                { RoutedEvent = Mouse.PreviewMouseDownEvent });
            historySource.Snapshot = new(HistoryStatus.Ready, [second]);
            Await(app.RefreshSystemHistoryAsync()); panel.RefreshHistory(); panel.UpdateLayout();
            secondRow = (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(0);
            RaiseDoubleClick(secondRow);
            secondRow.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
                { RoutedEvent = Mouse.PreviewMouseUpEvent });
            Equal(restores, historySource.RestoreCalls);
            Check(panel.IsVisible);
            MouseGesture(secondRow, secondRow);
            Equal(restores, historySource.RestoreCalls);
            Check(panel.IsVisible);
            DoubleClickRow(secondRow);
            Equal(restores + 1, historySource.RestoreCalls);
            Equal<Guid?>(second.Id, historySource.RestoredImage);
            Check(!panel.IsVisible);
        });
        Test("Keys: an IME-consumed key never pastes and does not swallow the next key", () =>
        {
            var id = Guid.NewGuid();
            historySource.Snapshot = new(HistoryStatus.Ready, [new(id, "输入法记录", Now)]);
            Await(app.RefreshSystemHistoryAsync());
            OpenHistory(panel);
            var list = (ListBox)panel.FindName("Entries");
            var writes = clipboardWriter.Writes;
            // WPF reports the Space/Enter that commits a candidate as ImeProcessed: it must be ignored.
            var imeKey = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(panel), 0, Key.ImeProcessed) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            panel.RaiseEvent(imeKey);
            Check(!imeKey.Handled);
            Equal(writes, clipboardWriter.Writes);
            Check(panel.IsVisible);
            // Keep the search focus: this is a synthetic-key regression, not a real IME integration test.
            Check(((TextBox)panel.FindName("SearchBox")).IsKeyboardFocusWithin);
            SendKey(panel, Key.Enter);
            Equal<Guid?>(id, historySource.RestoredImage);
            Check(!panel.IsVisible);
            panel.Hide();
        });
        Test("Search: clearing history keeps its whole-history scope while filtered", () =>
        {
            var kept = Guid.NewGuid();
            historySource.Snapshot = new(HistoryStatus.Ready,
                [new(kept, "保留项", Now), new(Guid.NewGuid(), "另一个", Now.AddSeconds(-1))]);
            Await(app.RefreshSystemHistoryAsync());
            OpenHistory(panel);
            var clearsBefore = historySource.ClearCalls;
            var search = (TextBox)panel.FindName("SearchBox");
            var list = (ListBox)panel.FindName("Entries");
            search.Text = "保留项";
            Equal(1, list.Items.Count);
            Click(panel, "MoreButton");
            Click(panel, "MenuClearButton");
            var confirmation = (InlineConfirmation)panel.FindName("Confirmation");
            Check(confirmation.IsOpen);
            // The wording must describe the whole Windows history, not the visible filtered subset.
            var message = (TextBlock)confirmation.FindName("MessageLabel");
            Check(message.Text.Contains("Windows") && message.Text.Contains("固定项"));
            Click(confirmation, "ConfirmButton");
            Equal(0, app.History.Count);
            Equal(clearsBefore + 1, historySource.ClearCalls);
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
    private static void MouseGesture(UIElement pressed, UIElement released)
    {
        pressed.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = Mouse.PreviewMouseDownEvent });
        released.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = Mouse.PreviewMouseUpEvent });
    }
    private static void DoubleClickRow(ListBoxItem row)
    {
        MouseGesture(row, row);
        row.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = Mouse.PreviewMouseDownEvent });
        RaiseDoubleClick(row);
    }
    private static void RaiseDoubleClick(ListBoxItem row)
    {
        // MouseDoubleClick is direct, not bubbling. Raise it on the subscribed list with the hit row as
        // its source, as WPF does when promoting a native double click along the mouse-event route.
        var list = (ListBox)ItemsControl.ItemsControlFromItemContainer(row);
        list.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
            { RoutedEvent = Control.MouseDoubleClickEvent, Source = row });
    }
    private static void CheckImagePreview(MainWindow panel, BitmapSource source, double minimumWidth, double minimumHeight)
    {
        panel.UpdateLayout();
        var image = (Image)panel.FindName("PreviewImage");
        var frame = (Border)panel.FindName("PreviewImageFrame");
        Check(image.IsVisible && frame.IsVisible);
        Check(ReferenceEquals(source, image.Source));
        Equal(Stretch.Uniform, image.Stretch);
        Check(image.ActualWidth >= minimumWidth && image.ActualHeight >= minimumHeight);
        var drawn = VisualTreeHelper.GetContentBounds(image);
        Check(!drawn.IsEmpty && drawn.Width > 0 && drawn.Height > 0);
        // WPF rounds layout to device pixels. A subpixel height change is a large ratio error for a
        // wide strip, so check the fitted dimensions within one physical pixel at the current DPI.
        // Width/Height are the available viewport; ActualWidth/ActualHeight are already rounded by
        // Image.ArrangeOverride and must not be used to compute a second, distorted fit.
        var scale = Math.Min(image.Width / source.Width, image.Height / source.Height);
        var dpi = VisualTreeHelper.GetDpi(image);
        if (Math.Abs(drawn.Width - source.Width * scale) * dpi.DpiScaleX > 1.01 ||
            Math.Abs(drawn.Height - source.Height * scale) * dpi.DpiScaleY > 1.01)
            throw new InvalidOperationException($"Preview fit: drawn={drawn}, actual={image.ActualWidth}x{image.ActualHeight}, expected={source.Width * scale}x{source.Height * scale}, dpi={dpi.DpiScaleX}x{dpi.DpiScaleY}");
        var bounds = image.TransformToAncestor(frame).TransformBounds(drawn);
        Check(bounds.Left >= -1 && bounds.Top >= -1 && bounds.Right <= frame.ActualWidth + 1 && bounds.Bottom <= frame.ActualHeight + 1);
    }
    private static BitmapSource CreateImageFixture(int width, int height)
    {
        // Entirely synthetic artwork: an application window with a sidebar, coloured cards and a chart.
        // The recognisable shapes make tiny / clipped previews apparent in the saved UI screenshots.
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            SolidColorBrush ColorBrush(byte red, byte green, byte blue) => new(Color.FromRgb(red, green, blue));
            void Rect(Brush brush, double x, double y, double w, double h) =>
                drawing.DrawRectangle(brush, null, new Rect(x * width, y * height, w * width, h * height));
            Rect(ColorBrush(241, 245, 249), 0, 0, 1, 1);
            Rect(ColorBrush(31, 41, 55), 0, 0, 1, .12);
            foreach (var (position, brush) in new[] { (.045, Brushes.Salmon), (.085, Brushes.Gold), (.125, Brushes.MediumSeaGreen) })
                drawing.DrawEllipse(brush, null, new Point(width * position, height * .06), Math.Min(width, height) * .014, Math.Min(width, height) * .014);
            Rect(ColorBrush(222, 231, 242), 0, .12, .2, .88);
            for (var row = 0; row < 5; row++) Rect(ColorBrush(129, 151, 176), .04, .22 + row * .11, .11, .025);
            Rect(Brushes.White, .24, .17, .71, .74);
            Rect(ColorBrush(37, 99, 235), .28, .23, .18, .21);
            Rect(ColorBrush(20, 184, 166), .50, .23, .18, .21);
            Rect(ColorBrush(249, 115, 22), .72, .23, .18, .21);
            Rect(ColorBrush(100, 116, 139), .29, .50, .36, .025);
            for (var bar = 0; bar < 6; bar++)
            {
                var barHeight = .10 + bar * .028;
                Rect(ColorBrush(99, 102, 241), .30 + bar * .10, .83 - barHeight, .06, barHeight);
            }
        }
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual); bitmap.Freeze();
        return bitmap;
    }
    private static T RowPart<T>(ListBox list, int index, string name) where T : FrameworkElement
    {
        list.UpdateLayout();
        var row = (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(index);
        return Descendants<T>(row).Single(part => part.Name == name);
    }
    private static void CheckSingleLineViewport(TextBox input)
    {
        Check(input.IsVisible && input.Text.Length > 0);
        var host = (ScrollViewer)input.Template.FindName("PART_ContentHost", input);
        var first = input.GetRectFromCharacterIndex(0);
        var last = input.GetRectFromCharacterIndex(input.Text.Length - 1, trailingEdge: true);
        Check(!first.IsEmpty && !last.IsEmpty && first.Height >= input.FontSize);
        Check(host.ViewportHeight + 1 >= first.Height);
        Check(host.ScrollableHeight <= 1);
        Check(first.Top >= input.BorderThickness.Top - 1 && first.Bottom <= input.ActualHeight - input.BorderThickness.Bottom + 1);
        Check(last.Top >= input.BorderThickness.Top - 1 && last.Bottom <= input.ActualHeight - input.BorderThickness.Bottom + 1);
    }
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
    private static void OpenHistory(MainWindow panel)
    {
        panel.OpenPanel(captureTarget: false);
        if (((ContentControl)panel.FindName("PageHost")).Content is null) panel.OpenSection(0);
    }
    /// <summary>Arrange a feature page; keyboard cycling is tested separately.</summary>
    private static void SwitchTab(Window window, int tab)
    {
        ((MainWindow)window).OpenSection(tab);
        Equal(tab == 1, ((StackPanel)window.FindName("SnippetActions")).Visibility == Visibility.Visible);
    }
    /// <summary>WCAG 2.1 relative luminance; readability is judged on this, not on a single RGB channel.</summary>
    private static double Luminance(Color color)
    {
        static double Channel(double value) => value <= 0.03928 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
        return 0.2126 * Channel(color.R / 255.0) + 0.7152 * Channel(color.G / 255.0) + 0.0722 * Channel(color.B / 255.0);
    }
    /// <summary>WCAG contrast ratio, always &gt;= 1; the acceptance floor for body text is 4.5.</summary>
    private static double Contrast(Color first, Color second)
    {
        var a = Luminance(first); var b = Luminance(second);
        return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
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

internal sealed class FakeClipboardWriter : IClipboardWriter
{
    internal int Writes;
    internal bool Sensitive;
    public Task<bool> WriteAsync(string text, bool sensitive)
    { Writes++; Sensitive = sensitive; return Task.FromResult(true); }
}

internal sealed class FakeApplicationCatalog : IApplicationCatalog
{
    internal IReadOnlyList<LaunchableApp> Applications { get; } = [
        new("fixture.editor", "示例编辑器", @"C:\ClipTap.TestFixtures\demo-editor.exe"),
        new("fixture.calculator", "Fixture Calculator", @"C:\ClipTap.TestFixtures\demo-calc.exe")];
    internal int ReadCalls { get; private set; }
    internal int LaunchCalls { get; private set; }
    internal string? LastLaunchedId { get; private set; }
    internal bool LaunchResult { get; set; } = true;
    public Task<IReadOnlyList<LaunchableApp>> ReadAsync() { ReadCalls++; return Task.FromResult(Applications); }
    public bool Launch(LaunchableApp application)
    {
        LaunchCalls++;
        LastLaunchedId = application.Id;
        return LaunchResult;
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
    internal int RestoreCalls { get; private set; }
    internal bool RestoreResult { get; set; } = true;
    internal Guid? DeletedId { get; private set; }
    internal bool DeleteResult { get; set; } = true;
    public Task<bool> DeleteAsync(Guid id)
    {
        DeletedId = id;
        if (DeleteResult) Snapshot = Snapshot with { Items = Snapshot.Items.Where(i => i.Id != id).ToArray() };
        return Task.FromResult(DeleteResult);
    }
    public Task<bool> RestoreItemAsync(Guid id) { RestoreCalls++; RestoredImage = id; return Task.FromResult(RestoreResult && Snapshot.Items.Any(i => i.Id == id)); }
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

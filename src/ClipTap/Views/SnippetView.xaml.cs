using System.Windows.Controls;
using ClipTap.Core;

namespace ClipTap.Views;

public partial class SnippetView : UserControl
{
    private readonly App _app;
    private readonly Snippet? _original;
    private bool _ready, _masked;
    internal string Heading => _original is null ? "新建片段" : "编辑片段";
    internal event Action? Finished;

    public SnippetView(App app, Snippet? snippet)
    {
        _app = app; _original = snippet;
        InitializeComponent();
        TitleInput.Text = snippet?.Title ?? "";
        TriggerInput.Text = snippet?.Trigger ?? "";
        ValueInput.Text = snippet?.Value ?? "";
        SensitiveInput.IsChecked = snippet?.IsSensitive ?? false;
        PinnedInput.IsChecked = snippet?.IsPinned ?? false;
        DeleteButton.Visibility = snippet is null ? Visibility.Collapsed : Visibility.Visible;
        _ready = true;
        UpdateMode();
        Loaded += (_, _) => TitleInput.Focus();
    }

    private void OnModeChanged(object sender, RoutedEventArgs e) { if (_ready) { RevealInput.IsChecked = false; UpdateMode(); } }
    private void OnRevealChanged(object sender, RoutedEventArgs e) { if (_ready) UpdateMode(); }
    private void UpdateMode()
    {
        var mask = SensitiveInput.IsChecked == true && RevealInput.IsChecked != true;
        if (mask != _masked)
        {
            if (mask) { SecretInput.Password = ValueInput.Text; ValueInput.Clear(); }
            else { ValueInput.Text = SecretInput.Password; SecretInput.Clear(); }
            _masked = mask;
        }
        ValueInput.Visibility = mask ? Visibility.Collapsed : Visibility.Visible;
        SecretInput.Visibility = mask ? Visibility.Visible : Visibility.Collapsed;
        RevealInput.Visibility = SensitiveInput.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    internal void Discard() { Confirmation.Dismiss(); SecretInput.Clear(); ValueInput.Clear(); }
    internal bool DismissConfirmation()
    { if (!Confirmation.IsOpen) return false; Confirmation.Dismiss(); return true; }
    private void OnSave(object sender, RoutedEventArgs e) => Save();
    internal void Save()
    {
        var value = _masked ? SecretInput.Password : ValueInput.Text;
        if (string.IsNullOrWhiteSpace(TitleInput.Text)) { ErrorLabel.Text = "请输入标题"; TitleInput.Focus(); return; }
        if (string.IsNullOrEmpty(value)) { ErrorLabel.Text = "请输入内容"; return; }
        var result = new Snippet(_original?.Id ?? Guid.NewGuid(), TitleInput.Text.Trim(), value,
            SensitiveInput.IsChecked == true, PinnedInput.IsChecked == true, DateTimeOffset.Now, TriggerInput.Text);
        try
        {
            if (_app.TryUpdateLibrary(library => library.SaveSnippet(result))) Finished?.Invoke();
            else ErrorLabel.Text = "保存失败，请重试";
        }
        catch (ArgumentException ex) { ErrorLabel.Text = ex.Message; }
    }
    private void OnDelete(object sender, RoutedEventArgs e)
    {
        if (_original is null) return;
        Confirmation.Ask("删除这个片段？", "删除", () =>
        {
            if (_app.TryUpdateLibrary(library => library.State.Snippets.RemoveAll(s => s.Id == _original.Id))) Finished?.Invoke();
            else ErrorLabel.Text = "删除未能保存，请重试";
            return Task.CompletedTask;
        });
    }
}

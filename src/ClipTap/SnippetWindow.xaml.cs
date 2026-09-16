using ClipTap.Core;
using ClipTap.Services;

namespace ClipTap;

public partial class SnippetWindow : Window
{
    private readonly Snippet? _original;
    private bool _ready;
    private bool _masked;
    public Snippet? Result { get; private set; }
    public bool DeleteRequested { get; private set; }

    public SnippetWindow(Snippet? snippet)
    {
        _original = snippet;
        InitializeComponent();
        SourceInitialized += (_, _) => ThemeService.ApplyTitleBar(this);
        Heading.Text = snippet is null ? "新建快捷片段" : "编辑快捷片段";
        Title = Heading.Text + " · ClipTap";
        TitleInput.Text = snippet?.Title ?? "";
        ValueInput.Text = snippet?.Value ?? "";
        SensitiveInput.IsChecked = snippet?.IsSensitive ?? false;
        PinnedInput.IsChecked = snippet?.IsPinned ?? false;
        DeleteButton.Visibility = snippet is null ? Visibility.Collapsed : Visibility.Visible;
        _ready = true;
        UpdateMode();
        Loaded += (_, _) => TitleInput.Focus();
        Closed += (_, _) => { SecretInput.Clear(); ValueInput.Clear(); };
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

    private void OnSave(object sender, RoutedEventArgs e)
    {
        var value = _masked ? SecretInput.Password : ValueInput.Text;
        if (string.IsNullOrWhiteSpace(TitleInput.Text)) { ErrorLabel.Text = "请填写片段标题。"; TitleInput.Focus(); return; }
        if (string.IsNullOrEmpty(value)) { ErrorLabel.Text = "请填写片段内容。"; return; }
        if (value.Length > Library.MaxTextLength) { ErrorLabel.Text = "内容最多 20,000 个字符。"; return; }
        Result = new Snippet(_original?.Id ?? Guid.NewGuid(), TitleInput.Text.Trim(), value,
            SensitiveInput.IsChecked == true, PinnedInput.IsChecked == true, DateTimeOffset.Now);
        DialogResult = true;
    }

    private void OnDelete(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "删除这个片段？此操作无法撤销。", "ClipTap", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        DeleteRequested = true;
        DialogResult = true;
    }
}

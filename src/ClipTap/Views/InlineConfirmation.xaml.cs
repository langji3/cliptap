using System.Windows.Controls;

namespace ClipTap.Views;

public partial class InlineConfirmation : UserControl
{
    private Func<Task>? _action;
    public bool IsOpen => Visibility == Visibility.Visible;
    public InlineConfirmation() => InitializeComponent();

    internal void Ask(string message, string confirm, Func<Task> action)
    {
        MessageLabel.Text = message;
        ConfirmButton.Content = confirm;
        _action = action;
        Visibility = Visibility.Visible;
        CancelButton.Focus();
    }

    internal void Dismiss() { _action = null; Visibility = Visibility.Collapsed; }
    private void OnCancel(object sender, RoutedEventArgs e) => Dismiss();
    private async void OnConfirm(object sender, RoutedEventArgs e)
    {
        var action = _action;
        Dismiss();
        if (action is not null) await action();
    }
}

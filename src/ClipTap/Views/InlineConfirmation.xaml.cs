using System.Windows.Controls;
using System.Windows.Input;

namespace ClipTap.Views;

public partial class InlineConfirmation : UserControl
{
    private Func<Task>? _action;
    public bool IsOpen => Visibility == Visibility.Visible;
    /// <summary>Raised when closing removed keyboard focus, so the host can restore it.</summary>
    internal event Action? FocusReleased;
    public InlineConfirmation() => InitializeComponent();

    internal void Ask(string message, string confirm, Func<Task> action)
    {
        MessageLabel.Text = message;
        ConfirmButton.Content = confirm;
        _action = action;
        Visibility = Visibility.Visible;
        CancelButton.Focus();
    }

    internal void Dismiss()
    {
        if (Visibility != Visibility.Visible) return;
        var hadFocus = IsKeyboardFocusWithin;
        _action = null;
        Visibility = Visibility.Collapsed;
        // The cancel button just left the tree, so keyboard input would go nowhere without this.
        if (hadFocus) FocusReleased?.Invoke();
    }
    private void OnCancel(object sender, RoutedEventArgs e) => Dismiss();
    private async void OnConfirm(object sender, RoutedEventArgs e)
    {
        var action = _action;
        Dismiss();
        if (action is not null) await action();
    }
}

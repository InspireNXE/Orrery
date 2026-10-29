using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Orrery.App;

/// <summary>Small message and confirmation windows in the app's dark style.</summary>
static class Dialogs
{
    public static readonly IBrush PanelBrush = new SolidColorBrush(Color.Parse("#14161B"));
    public static readonly IBrush MutedBrush = new SolidColorBrush(Color.Parse("#9AA3B2"));
    public static readonly IBrush DangerBrush = new SolidColorBrush(Color.Parse("#FF9AA2"));
    public static readonly IBrush WarningBrush = new SolidColorBrush(Color.Parse("#F3B672"));
    public static readonly FontFamily MonoFont = new("Cascadia Mono, Consolas, Menlo, monospace");

    public static Window MessageWindow(string title, string body)
    {
        var close = new Button { Content = "Close", IsDefault = true, IsCancel = true, HorizontalAlignment = HorizontalAlignment.Right };
        var window = Shell(title, close);
        close.Click += (_, _) => window.Close();
        ((StackPanel)window.Content!).Children.Insert(1, new SelectableTextBlock { Text = body, TextWrapping = TextWrapping.Wrap });
        return window;
    }

    public static Task ShowMessageAsync(Window owner, string title, string body) =>
        MessageWindow(title, body).ShowDialog(owner);

    /// <summary>Asks a yes/no question. Cancel is the default, so Enter never confirms something destructive.</summary>
    public static Task<bool> ConfirmAsync(Window owner, string title, string body, string confirmLabel)
    {
        var cancel = new Button { Content = "Cancel", IsDefault = true, IsCancel = true };
        var confirm = new Button { Content = confirmLabel, Foreground = DangerBrush };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { confirm, cancel } };

        var window = Shell(title, buttons);
        ((StackPanel)window.Content!).Children.Insert(1, new TextBlock { Text = body, TextWrapping = TextWrapping.Wrap });
        cancel.Click += (_, _) => window.Close(false);
        confirm.Click += (_, _) => window.Close(true);
        return window.ShowDialog<bool>(owner);
    }

    static Window Shell(string title, Control buttons) => new()
    {
        Title = "Orrery",
        Width = 460,
        SizeToContent = SizeToContent.Height,
        CanResize = false,
        WindowStartupLocation = WindowStartupLocation.CenterOwner,
        Background = PanelBrush,
        Content = new StackPanel
        {
            Margin = new Thickness(24),
            Spacing = 12,
            Children =
            {
                new TextBlock { Text = title, FontSize = 16, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap },
                buttons,
            },
        },
    };

    public static TextBlock Label(string text) => new() { Text = text, FontSize = 12, Foreground = MutedBrush };

    public static Control Field(string label, Control input) =>
        new StackPanel { Spacing = 4, Children = { Label(label), input } };
}

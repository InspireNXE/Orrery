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

    /// <summary>Asks for a password. Null if cancelled.</summary>
    public static Task<string?> PromptPasswordAsync(Window owner, string title, string body, string? error = null)
    {
        var box = new TextBox { PasswordChar = '•' };
        var ok = new Button { Content = "OK", IsDefault = true };
        ok.Classes.Add("accent");
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { cancel, ok } };

        var window = Shell(title, buttons);
        var panel = (StackPanel)window.Content!;
        panel.Children.Insert(1, new TextBlock { Text = body, TextWrapping = TextWrapping.Wrap });
        panel.Children.Insert(2, box);
        if (error is not null)
        {
            panel.Children.Insert(3, new TextBlock { Text = error, Foreground = DangerBrush, TextWrapping = TextWrapping.Wrap });
        }

        ok.Click += (_, _) => window.Close(box.Text ?? string.Empty);
        cancel.Click += (_, _) => window.Close(null);
        window.Opened += (_, _) => box.Focus();
        return window.ShowDialog<string?>(owner);
    }

    /// <summary>
    /// Asks for a line of text, such as a new name, starting with <paramref name="initial"/> selected. Null if
    /// cancelled; OK is disabled while the text is blank.
    /// </summary>
    public static Task<string?> PromptTextAsync(Window owner, string title, string label, string initial, string confirmLabel)
    {
        var box = new TextBox { Text = initial };
        var ok = new Button { Content = confirmLabel, IsDefault = true, IsEnabled = !string.IsNullOrWhiteSpace(initial) };
        ok.Classes.Add("accent");
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { cancel, ok } };

        var window = Shell(title, buttons);
        ((StackPanel)window.Content!).Children.Insert(1, Field(label, box));
        box.TextChanged += (_, _) => ok.IsEnabled = !string.IsNullOrWhiteSpace(box.Text);
        ok.Click += (_, _) => window.Close(box.Text!.Trim());
        cancel.Click += (_, _) => window.Close(null);
        window.Opened += (_, _) =>
        {
            box.Focus();
            box.SelectAll();
        };
        return window.ShowDialog<string?>(owner);
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

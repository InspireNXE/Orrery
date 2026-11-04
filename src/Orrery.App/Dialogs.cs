using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Orrery.App.Controls;

namespace Orrery.App;

/// <summary>Small message and confirmation windows in the app's dark style.</summary>
static class Dialogs
{
    public static readonly IBrush PanelBrush = new SolidColorBrush(Color.Parse("#14161B"));
    public static readonly IBrush MutedBrush = new SolidColorBrush(Color.Parse("#9AA3B2"));
    /// <summary>Errors and destructive actions; one brush app-wide, see <see cref="Palette"/>.</summary>
    public static IBrush DangerBrush => Palette.Danger;
    public static IBrush WarningBrush => Palette.Warning;
    public static readonly FontFamily MonoFont = new("Cascadia Mono, Consolas, Menlo, monospace");

    public static Window MessageWindow(string title, string body)
    {
        var close = new Button { Content = "Close", IsDefault = true, IsCancel = true, HorizontalAlignment = HorizontalAlignment.Right };
        var window = Shell(title, close);
        AutomationProperties.SetHelpText(window, body);
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
        AutomationProperties.SetHelpText(window, body);
        ((StackPanel)window.Content!).Children.Insert(1, new TextBlock { Text = body, TextWrapping = TextWrapping.Wrap });
        cancel.Click += (_, _) => window.Close(false);
        confirm.Click += (_, _) => window.Close(true);
        return window.ShowDialog<bool>(owner);
    }

    /// <summary>
    /// Asks a choice between two ordinary answers, neither destructive. <paramref name="noLabel"/> is the default, and
    /// closing the window answers it too.
    /// </summary>
    public static async Task<bool> AskAsync(Window owner, string title, string body, string yesLabel, string noLabel)
    {
        var no = new Button { Content = noLabel, IsDefault = true, IsCancel = true };
        var yes = new Button { Content = yesLabel };
        yes.Classes.Add("accent");
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { no, yes } };

        var window = Shell(title, buttons);
        AutomationProperties.SetHelpText(window, body);
        ((StackPanel)window.Content!).Children.Insert(1, new TextBlock { Text = body, TextWrapping = TextWrapping.Wrap });
        no.Click += (_, _) => window.Close(false);
        yes.Click += (_, _) => window.Close(true);
        return await window.ShowDialog<bool?>(owner) == true;
    }

    /// <summary>Asks for a password. Null if canceled.</summary>
    public static Task<string?> PromptPasswordAsync(Window owner, string title, string body, string? error = null) =>
        PromptPasswordAsync(owner, title, body, error, check: null);

    /// <summary>
    /// Asks for a password, and keeps the window open until <paramref name="check"/> takes it: check returns why it
    /// was turned down, shown in the window with the box cleared for another try, or null to close. Null if canceled.
    /// </summary>
    public static Task<string?> PromptPasswordAsync(Window owner, string title, string body, Func<string, Task<string?>> check) =>
        PromptPasswordAsync(owner, title, body, error: null, check);

    static Task<string?> PromptPasswordAsync(Window owner, string title, string body, string? error, Func<string, Task<string?>>? check)
    {
        var box = new PasswordBox();
        AutomationProperties.SetName(box, "Password");
        AutomationProperties.SetHelpText(box, error is null ? body : $"{error} {body}");
        var ok = new Button { Content = "OK", IsDefault = true };
        ok.Classes.Add("accent");
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { cancel, ok } };

        var window = Shell(title, buttons);
        var panel = (StackPanel)window.Content!;
        var notice = new Notice(NoticeKind.Error, error) { IsVisible = error is not null };
        panel.Children.Insert(1, new TextBlock { Text = body, TextWrapping = TextWrapping.Wrap });
        panel.Children.Insert(2, box);
        panel.Children.Insert(3, notice);

        ok.Click += async (_, _) =>
        {
            var password = box.Text ?? string.Empty;
            if (check is null)
            {
                window.Close(password);
                return;
            }

            ok.IsEnabled = false;
            try
            {
                if (await check(password) is { } rejected)
                {
                    notice.Text = rejected;
                    notice.IsVisible = true;
                    AutomationProperties.SetHelpText(box, $"{rejected} {body}");
                    box.Text = string.Empty;
                    box.Focus();
                    return;
                }

                window.Close(password);
            }
            finally
            {
                ok.IsEnabled = true;
            }
        };
        cancel.Click += (_, _) => window.Close(null);
        window.Opened += (_, _) => box.Focus();
        return window.ShowDialog<string?>(owner);
    }

    /// <summary>
    /// Asks for a line of text, such as a new name, starting with <paramref name="initial"/> selected. Null if
    /// canceled; OK is disabled while the text is blank.
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

    // The window is named after the question, which is what a screen reader says when it opens.
    static Window Shell(string title, Control buttons) => new()
    {
        Title = title,
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
                Heading(new TextBlock { Text = title, FontSize = 16, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap }),
                buttons,
            },
        },
    };

    /// <summary>Marks a dialog's title as a heading, for screen readers that list them.</summary>
    public static TextBlock Heading(TextBlock title)
    {
        AutomationProperties.SetHeadingLevel(title, 1);
        return title;
    }

    /// <summary>
    /// A trust prompt's heading. For a changed host key or certificate it's in the danger color with the error icon
    /// beside it, so the change is plain without color.
    /// </summary>
    /// <summary>
    /// When <paramref name="window"/> opens, focuses <paramref name="button"/> and has the screen reader read
    /// <paramref name="spoken"/>, so a prompt that appears over a session is heard before anything else.
    /// </summary>
    public static void FocusAndSayOnOpen(Window window, Button button, string spoken)
    {
        window.Opened += (_, _) =>
        {
            button.Focus();
            ScreenReader.Announce(button, spoken, "Orrery.Prompt", ScreenReader.CurrentThenMostRecent);
        };
    }

    public static Control TrustHeading(string text, bool changed)
    {
        if (!changed)
        {
            return Heading(new TextBlock { Text = text, FontSize = 18, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap });
        }

        var heading = new Notice(NoticeKind.Error, text) { IconSize = 22, Prefix = "Warning" };
        heading.Message.FontSize = 18;
        heading.Message.FontWeight = FontWeight.SemiBold;
        AutomationProperties.SetHeadingLevel(heading, 1);
        return heading;
    }

    public static TextBlock Label(string text) => new() { Text = text, FontSize = 12, Foreground = MutedBrush };

    /// <summary>
    /// A label over an input. The input is named after the label, since a screen reader doesn't connect the two by
    /// position; text shown as the value (not an input) keeps its own text.
    /// </summary>
    public static Control Field(string label, Control input)
    {
        if (input is not TextBlock and not Panel && string.IsNullOrEmpty(AutomationProperties.GetName(input)))
        {
            AutomationProperties.SetName(input, label);
        }

        return new StackPanel { Spacing = 4, Children = { Label(label), input } };
    }

    static readonly IBrush ChoiceBrush = new SolidColorBrush(Color.Parse("#181B21"));
    static readonly IBrush ChosenBrush = new SolidColorBrush(Color.Parse("#1A2130"));

    /// <summary>
    /// A radio button as a tile: its title over a muted line, the whole tile clickable, and the chosen one edged in the
    /// accent. Arrow keys move between a group's tiles (<see cref="Input.RadioKeys"/>).
    /// </summary>
    public static Control Choice(RadioButton radio, string title, string? line = null, bool mono = false)
    {
        var text = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center, Children = { new TextBlock { Text = title, TextWrapping = TextWrapping.Wrap } } };
        if (line is not null)
        {
            text.Children.Add(new TextBlock { Text = line, FontSize = 12, Foreground = MutedBrush, TextWrapping = TextWrapping.Wrap, FontFamily = mono ? MonoFont : FontFamily.Default });
        }

        radio.Content = text;
        radio.HorizontalAlignment = HorizontalAlignment.Stretch;
        radio.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        radio.Padding = new Thickness(10, 0, 0, 0);
        // The margin is the tile's padding, kept inside the button so a click anywhere on the tile chooses it.
        radio.Margin = new Thickness(12, 8);
        // The content is a panel, which a screen reader can't read as the button's name.
        AutomationProperties.SetName(radio, title);
        if (line is not null)
        {
            AutomationProperties.SetHelpText(radio, line);
        }

        var tile = new Border { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Child = radio };
        void Show()
        {
            bool chosen = radio.IsChecked == true;
            tile.Background = chosen ? ChosenBrush : ChoiceBrush;
            tile.BorderBrush = chosen ? new SolidColorBrush(Palette.Accent.Color, 0.55) : Pane.LineBrush;
        }

        radio.IsCheckedChanged += (_, _) => Show();
        Show();
        return tile;
    }
}

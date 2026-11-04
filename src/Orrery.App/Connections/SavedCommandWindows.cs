using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Orrery.Core.Connections;

namespace Orrery.App.Connections;

/// <summary>
/// Asks before a saved command is sent: the command as it will be typed, and the session it goes to in large type.
/// Cancel has the focus and is the default, so a quick Enter sends nothing. There's no "don't ask again": the
/// command's or the folder's setting says whether it asks.
/// </summary>
sealed class ConfirmCommandWindow : Window
{
    public ConfirmCommandWindow(string commandName, string text, string target, string? where, bool pressEnter)
    {
        Title = "Send this command?";
        Width = 520;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Dialogs.PanelBrush;

        var cancel = new Button { Content = "Cancel", IsDefault = true, IsCancel = true };
        var send = new Button { Content = pressEnter ? "Send and run" : "Send", Foreground = Dialogs.DangerBrush };
        AutomationProperties.SetName(send, pressEnter ? "Send and run" : "Send without pressing Enter");
        cancel.Click += (_, _) => Close(false);
        send.Click += (_, _) => Close(true);

        var targetText = new TextBlock { Text = target, FontSize = 18, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
        var command = new SelectableTextBlock
        {
            Text = text,
            FontFamily = Dialogs.MonoFont,
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
        };
        AutomationProperties.SetName(command, "Command");
        var spoken = $"{commandName} goes to {target}{(where is null ? "" : $", {where}")}. The command: {text}. "
            + (pressEnter ? "It runs as soon as it's sent." : "It's typed at the prompt without pressing Enter.");
        AutomationProperties.SetHelpText(this, spoken);

        Content = new StackPanel
        {
            Margin = new Thickness(24),
            Spacing = 12,
            Children =
            {
                Dialogs.Heading(new TextBlock { Text = $"Send \"{commandName}\" to", FontSize = 14, Foreground = Dialogs.MutedBrush, TextWrapping = TextWrapping.Wrap }),
                targetText,
                new TextBlock { Text = where, Foreground = Dialogs.MutedBrush, FontSize = 12, IsVisible = where is not null, TextWrapping = TextWrapping.Wrap },
                new Border
                {
                    Padding = new Thickness(12, 10),
                    CornerRadius = new CornerRadius(6),
                    BorderThickness = new Thickness(1),
                    BorderBrush = new SolidColorBrush(Color.Parse("#2A2F38")),
                    Background = new SolidColorBrush(Color.Parse("#0B0D10")),
                    Child = new ScrollViewer { MaxHeight = 220, Content = command },
                },
                new TextBlock
                {
                    Text = pressEnter ? "It runs as soon as it's sent." : "It's typed at the prompt; nothing runs until you press Enter.",
                    FontSize = 12,
                    Foreground = Dialogs.MutedBrush,
                },
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { send, cancel } },
            },
        };
        Opened += (_, _) => cancel.Focus();
    }
}

/// <summary>Somewhere a new command can be saved: a connection, a folder, or the defaults (the tree's top).</summary>
sealed record CommandPlace(string Label, ConnectionNode Node);

/// <summary>Adds or changes a saved command: its name, what's typed, whether Enter is pressed, and whether it asks first.</summary>
sealed class SavedCommandWindow : Window
{
    readonly IReadOnlyList<CommandPlace> _places = [];
    readonly ComboBox _place = new() { HorizontalAlignment = HorizontalAlignment.Stretch };

    readonly TextBox _name = new() { PlaceholderText = "Tail the nginx error log" };
    readonly TextBox _text = new()
    {
        PlaceholderText = "sudo tail -f /var/log/nginx/error.log",
        AcceptsReturn = true,
        TextWrapping = TextWrapping.Wrap,
        MinHeight = 90,
        MaxHeight = 220,
        MaxLength = SavedCommands.MaxLength,
        FontFamily = Dialogs.MonoFont,
    };
    readonly CheckBox _pressEnter = new() { Content = "Press Enter after it, so it runs", IsChecked = true };
    readonly CheckBox _confirm = new() { Content = "Ask before sending it" };
    readonly TextBlock _hint = new() { FontSize = 12, Foreground = Dialogs.WarningBrush, TextWrapping = TextWrapping.Wrap, IsVisible = false };
    readonly TextBlock _error = new() { FontSize = 12, Foreground = Dialogs.DangerBrush, TextWrapping = TextWrapping.Wrap, IsVisible = false };
    bool _confirmTouched, _settingConfirm;

    /// <summary>A new command from a terminal, saved on whichever of <paramref name="places"/> is chosen (the first to begin with).</summary>
    public SavedCommandWindow(SavedCommand? command, IReadOnlyList<CommandPlace> places)
        : this(command, places.Count > 0 ? places[0].Label : string.Empty)
    {
        _places = places;
        if (places.Count < 2)
        {
            return;
        }

        foreach (var place in places)
        {
            _place.Items.Add(new ComboBoxItem { Content = place.Label });
        }

        _place.SelectedIndex = 0;
        var panel = (StackPanel)Content!;
        int at = panel.Children.IndexOf(_ownerLine);
        panel.Children[at] = Dialogs.Field("Save on", _place);
    }

    /// <summary>Where the command is to be saved, when the window was given places.</summary>
    public CommandPlace? ChosenPlace => _places.Count == 0 ? null : _places[Math.Max(0, _place.SelectedIndex)];

    internal ComboBox PlaceBox => _place;

    readonly TextBlock _ownerLine = new() { Foreground = Dialogs.MutedBrush, FontSize = 12 };

    public SavedCommandWindow(SavedCommand? command, string ownerName)
    {
        bool adding = command is null;
        Title = adding ? "New command" : "Edit command";
        Width = 520;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Dialogs.PanelBrush;

        if (command is not null)
        {
            _name.Text = command.Name;
            _text.Text = command.Text;
            _pressEnter.IsChecked = command.PressEnter;
            _confirm.IsChecked = command.Confirm;
            _confirmTouched = true;
        }

        // A command that looks like it deletes or stops something asks first, unless the box was set by hand.
        _confirm.IsCheckedChanged += (_, _) => _confirmTouched |= !_settingConfirm;
        _text.TextChanged += (_, _) => NoteDestructive();

        // Enter in the name saves; in the command it's a new line, and Ctrl+Enter saves (DialogKeys).
        var save = new Button { Content = adding ? "Add" : "Save", IsDefault = true };
        save.Classes.Add("accent");
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        save.Click += (_, _) => Save();
        cancel.Click += (_, _) => Close(null);

        Content = new StackPanel
        {
            Margin = new Thickness(24),
            Spacing = 12,
            Children =
            {
                Dialogs.Heading(new TextBlock { Text = Title, FontSize = 16, FontWeight = FontWeight.SemiBold }),
                _ownerLine,
                Dialogs.Field("Name", _name),
                Dialogs.Field("Command", _text),
                new TextBlock
                {
                    Text = $"{{host}}, {{username}} and {{name}} are filled in from the connection. Several lines are pasted as one. {(OperatingSystem.IsMacOS() ? "Cmd" : "Ctrl")}+Enter {(adding ? "adds" : "saves")} it.",
                    FontSize = 12,
                    Foreground = Dialogs.MutedBrush,
                    TextWrapping = TextWrapping.Wrap,
                },
                _pressEnter,
                _confirm,
                _hint,
                _error,
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { cancel, save } },
            },
        };
        _ownerLine.Text = $"On {ownerName}";
        Opened += (_, _) => (adding ? _name : _text).Focus();
    }

    /// <summary>Starts the text from a terminal selection, for Save selection as command.</summary>
    public string? Seed
    {
        set
        {
            _text.Text = value;
            _name.Text = value is null ? null : FirstWords(value);
        }
    }

    internal TextBox NameBox => _name;
    internal TextBox TextBox => _text;
    internal CheckBox ConfirmBox => _confirm;
    internal CheckBox PressEnterBox => _pressEnter;

    void NoteDestructive()
    {
        bool risky = SavedCommands.LooksDestructive(_text.Text ?? string.Empty);
        _hint.Text = "This looks like it deletes, stops or overwrites something, so it asks first.";
        _hint.IsVisible = risky && !_confirmTouched;
        if (risky && !_confirmTouched)
        {
            _settingConfirm = true;
            _confirm.IsChecked = true;
            _settingConfirm = false;
        }
    }

    internal void Save()
    {
        var text = (_text.Text ?? string.Empty).TrimEnd();
        if (string.IsNullOrWhiteSpace(text))
        {
            ShowError("Type the command to send.");
            _text.Focus();
            return;
        }

        if (text.Any(c => char.IsControl(c) && c is not ('\n' or '\r' or '\t')))
        {
            ShowError("The command has control characters, which would act as keys. Take them out.");
            _text.Focus();
            return;
        }

        var name = string.IsNullOrWhiteSpace(_name.Text) ? FirstWords(text) : _name.Text.Trim();
        Close(new SavedCommand { Name = name, Text = text, PressEnter = _pressEnter.IsChecked == true, Confirm = _confirm.IsChecked == true });
    }

    void ShowError(string message)
    {
        _error.Text = message;
        _error.IsVisible = true;
    }

    static string FirstWords(string text)
    {
        var line = text.Split('\n', 2)[0].Trim();
        return line.Length <= 40 ? line : line[..39] + "…";
    }
}

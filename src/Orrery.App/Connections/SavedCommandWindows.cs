using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Orrery.App.Credentials;
using Orrery.Core.Connections;
using Orrery.Core.Credentials;

namespace Orrery.App.Connections;

/// <summary>
/// Asks before a saved command is sent: the command as it will be typed, and the session it goes to in large type.
/// Cancel has the focus and is the default, so a quick Enter sends nothing. There's no "don't ask again": the
/// command's or the folder's setting says whether it asks.
/// </summary>
sealed class ConfirmCommandWindow : Window
{
    /// <param name="password">Which password it types if a prompt shows, said under the command; null when none.</param>
    public ConfirmCommandWindow(string commandName, string text, string target, string? where, bool pressEnter, string? password = null)
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
            + (pressEnter ? "It runs as soon as it's sent." : "It's typed at the prompt without pressing Enter.")
            + (password is null ? "" : $" {password}");
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
                new TextBlock { Text = password, FontSize = 12, Foreground = Dialogs.WarningBrush, TextWrapping = TextWrapping.Wrap, IsVisible = password is not null },
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

    // Saved connections only: a local shell or a quick connect never sends anything by itself.
    readonly CheckBox _whenConnected = new() { Content = "Send it by itself each time a saved connection connects" };

    // Only with Enter pressed: the prompt comes from running it.
    readonly ComboBox _password = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    readonly CredentialPicker? _passwordCredential;
    readonly TextBlock _passwordNote = new()
    {
        Text = "Typed once, and only if a password prompt shows within a few seconds of sending it. Nothing is typed at a shell prompt.",
        FontSize = 12,
        Foreground = Dialogs.MutedBrush,
        TextWrapping = TextWrapping.Wrap,
        IsVisible = false,
    };
    readonly TextBlock _passwordReach = new() { FontSize = 12, Foreground = Dialogs.WarningBrush, TextWrapping = TextWrapping.Wrap, IsVisible = false };
    readonly ConnectionNode? _owner;
    readonly Button _becomeRoot = new() { Content = "Become root (sudo -i)", Classes = { "link" } };
    readonly TextBlock _hint = new() { FontSize = 12, Foreground = Dialogs.WarningBrush, TextWrapping = TextWrapping.Wrap, IsVisible = false };
    readonly TextBlock _error = new() { FontSize = 12, Foreground = Dialogs.DangerBrush, TextWrapping = TextWrapping.Wrap, IsVisible = false };
    bool _confirmTouched, _settingConfirm;

    /// <summary>A new command from a terminal, saved on whichever of <paramref name="places"/> is chosen (the first to begin with).</summary>
    public SavedCommandWindow(SavedCommand? command, IReadOnlyList<CommandPlace> places, ICredentialStore? credentials = null)
        : this(command, places.Count > 0 ? places[0].Label : string.Empty, credentials)
    {
        _places = places;
        ShowPassword();
        if (places.Count < 2)
        {
            return;
        }

        foreach (var place in places)
        {
            _place.Items.Add(new ComboBoxItem { Content = place.Label });
        }

        _place.SelectedIndex = 0;
        _place.SelectionChanged += (_, _) => ShowPassword();
        var panel = (StackPanel)Content!;
        int at = panel.Children.IndexOf(_ownerLine);
        panel.Children[at] = Dialogs.Field("Save on", _place);
    }

    /// <summary>Where the command is to be saved, when the window was given places.</summary>
    public CommandPlace? ChosenPlace => _places.Count == 0 ? null : _places[Math.Max(0, _place.SelectedIndex)];

    /// <summary>Where it's saved, so the editor can say which connections a password it types reaches.</summary>
    ConnectionNode? SavedOn => ChosenPlace?.Node ?? _owner;

    internal ComboBox PlaceBox => _place;

    readonly TextBlock _ownerLine = new() { Foreground = Dialogs.MutedBrush, FontSize = 12 };

    /// <summary>
    /// With <paramref name="credentials"/>, a password prompt can be answered from a chosen credential as well as the
    /// connection's login. With <paramref name="owner"/>, where it's saved, it says which connections that reaches.
    /// </summary>
    public SavedCommandWindow(SavedCommand? command, string ownerName, ICredentialStore? credentials = null, ConnectionNode? owner = null)
    {
        _owner = owner;
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
            _whenConnected.IsChecked = command.WhenConnected;
            _confirmTouched = true;
        }

        _password.Items.Add(new ComboBoxItem { Content = "Leave it to me", Tag = CommandPassword.None });
        _password.Items.Add(new ComboBoxItem { Content = "Type the connection's login password", Tag = CommandPassword.Login });
        if (credentials is not null)
        {
            _password.Items.Add(new ComboBoxItem { Content = "Type a saved credential's password", Tag = CommandPassword.Credential });
            _passwordCredential = new CredentialPicker(credentials, "Choose a credential") { IsVisible = false };
            AutomationProperties.SetName(_passwordCredential, "Credential whose password is typed");
        }

        var choice = command?.Password ?? CommandPassword.None;
        PasswordChoice = _passwordCredential is null && choice == CommandPassword.Credential ? CommandPassword.None : choice;
        _password.SelectionChanged += (_, _) => ShowPassword();
        _pressEnter.IsCheckedChanged += (_, _) => ShowPassword();
        ShowPassword();
        _becomeRoot.Click += (_, _) => BecomeRoot();
        AutomationProperties.SetHelpText(_becomeRoot, "Sets the command to sudo -i, answering its password prompt with the connection's login password");

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
                _becomeRoot,
                _pressEnter,
                _confirm,
                _whenConnected,
                Dialogs.Field("If it asks for a password", _password),
                _passwordCredential ?? new Control { IsVisible = false },
                _passwordNote,
                _passwordReach,
                _hint,
                _error,
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { cancel, save } },
            },
        };
        _ownerLine.Text = $"On {ownerName}";
        Opened += async (_, _) =>
        {
            (adding ? _name : _text).Focus();
            if (_passwordCredential is not null)
            {
                await _passwordCredential.RefreshAsync(command?.PasswordCredentialId);
            }
        };
    }

    /// <summary>What a password prompt is answered with, as chosen.</summary>
    internal CommandPassword PasswordChoice
    {
        get => (_password.SelectedItem as ComboBoxItem)?.Tag is CommandPassword choice ? choice : CommandPassword.None;
        set => _password.SelectedItem = _password.Items.OfType<ComboBoxItem>().FirstOrDefault(i => i.Tag is CommandPassword c && c == value) ?? _password.Items[0];
    }

    void ShowPassword()
    {
        bool enter = _pressEnter.IsChecked == true;
        _password.IsEnabled = enter;
        var choice = PasswordChoice;
        _passwordNote.IsVisible = enter && choice != CommandPassword.None;
        if (_passwordCredential is not null)
        {
            _passwordCredential.IsVisible = enter && choice == CommandPassword.Credential;
        }

        _passwordReach.Text = ReachWarning(enter ? choice : CommandPassword.None);
        _passwordReach.IsVisible = _passwordReach.Text is not null;
    }

    /// <summary>
    /// A chosen credential's password, saved on a folder, goes to every connection in it that asks; a server that's
    /// been broken into could ask just to collect it. From the defaults it isn't typed at all. Each connection's own
    /// login password is its own, so that isn't warned about.
    /// </summary>
    string? ReachWarning(CommandPassword choice) =>
        choice != CommandPassword.Credential || SavedOn is not { } owner || SavedCommands.PasswordReach(owner) is not { } reach ? null
        : owner.Parent is null ? "A chosen credential's password isn't typed from the defaults for all connections, which reach every server. Save it on a folder or a connection."
        : $"This password goes to {reach} that asks for one. A server that's been broken into could ask just to collect it, so save it on the connections that need it.";

    /// <summary>The ready-made command for root: <c>sudo -i</c>, answering sudo's prompt with the login's password.</summary>
    internal void BecomeRoot()
    {
        _text.Text = "sudo -i";
        if (string.IsNullOrWhiteSpace(_name.Text))
        {
            _name.Text = "Become root";
        }

        _pressEnter.IsChecked = true;
        PasswordChoice = CommandPassword.Login;
        _text.Focus();
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
    internal CheckBox WhenConnectedBox => _whenConnected;
    internal ComboBox PasswordBox => _password;
    internal CredentialPicker? PasswordCredentialPicker => _passwordCredential;

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

        bool pressEnter = _pressEnter.IsChecked == true;
        var password = pressEnter ? PasswordChoice : CommandPassword.None;
        Guid? credential = password == CommandPassword.Credential ? _passwordCredential?.SelectedId : null;
        if (password == CommandPassword.Credential && SavedOn is ConnectionFolder { Parent: null })
        {
            ShowError("Save it on a folder or a connection to type a chosen credential's password, or choose the login password.");
            _password.Focus();
            return;
        }

        if (password == CommandPassword.Credential && credential is null)
        {
            ShowError("Choose the credential whose password it types.");
            _passwordCredential?.Focus();
            return;
        }

        var name = string.IsNullOrWhiteSpace(_name.Text) ? FirstWords(text) : _name.Text.Trim();
        Close(new SavedCommand
        {
            Name = name,
            Text = text,
            PressEnter = pressEnter,
            Confirm = _confirm.IsChecked == true,
            WhenConnected = _whenConnected.IsChecked == true,
            Password = password,
            PasswordCredentialId = credential,
        });
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

using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Orrery.App.Controls;
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
                new Notice(NoticeKind.Warning, password) { IsVisible = password is not null },
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { send, cancel } },
            },
        };
        Opened += (_, _) => cancel.Focus();
    }
}


/// <summary>
/// The fields of a saved command: its name, what's typed, whether Enter is pressed, whether it asks first, and which
/// password it types. In the editor window, and in the right-hand pane beside the Commands page.
/// </summary>
sealed class SavedCommandEditor : StackPanel
{
    CommandPlacePicker? _picker;
    IReadOnlyList<ConnectionNode>? _fixedPlaces;

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

    // What typing a password means, shown whenever it would: the risk always, then which connections it reaches, and
    // whether it crosses the line unencrypted. Notices, so each has an icon and is read out as a warning.
    readonly Notice _passwordRisk = new(NoticeKind.Warning, PasswordRisk);
    readonly Notice _passwordReach = new(NoticeKind.Warning) { IsVisible = false };
    readonly Notice _passwordPlain = new(NoticeKind.Warning, PlainText) { IsVisible = false };
    readonly Notice _passwordSafer = new(NoticeKind.Info, Safer);
    readonly StackPanel _passwordNotes = new() { Spacing = 8, IsVisible = false };

    internal const string PasswordRisk = "Orrery types this password once, and only if a password prompt shows within 10 seconds of sending "
        + "the command, never at a shell prompt. Any program on the server can show a prompt like that, so use this only on servers you trust.";
    internal const string PlainText = "It reaches a Telnet or serial connection. Those don't encrypt anything, so on them this password crosses the "
        + "line as plain text, as the login does.";
    internal const string Safer = "Safer where you can: a NOPASSWD sudoers rule for the commands you need, or a key to log in as root. "
        + "Neither keeps a sudo password in Orrery.";
    internal const string NotFromDefaults = "A chosen credential's password isn't typed from the defaults for all connections, which reach every "
        + "server. Save it on a folder or a connection, or choose the login password.";

    readonly IReadOnlyList<ConnectionNode> _owners;
    readonly Guid? _credentialId;
    readonly Guid? _linkId;
    readonly Button _becomeRoot = new() { Content = "Become root (sudo -i)", Classes = { "link" } };
    readonly TextBlock _hint = new() { FontSize = 12, Foreground = Dialogs.WarningBrush, TextWrapping = TextWrapping.Wrap, IsVisible = false };
    readonly TextBlock _error = new() { FontSize = 12, Foreground = Dialogs.DangerBrush, TextWrapping = TextWrapping.Wrap, IsVisible = false };
    readonly TextBlock _ownerLine = new() { Foreground = Dialogs.MutedBrush, FontSize = 12, TextWrapping = TextWrapping.Wrap };
    bool _confirmTouched, _settingConfirm;

    /// <summary>
    /// With <paramref name="credentials"/>, a password prompt can be answered from a chosen credential as well as the
    /// connection's login. With <paramref name="owners"/>, where it's saved, it says which connections that reaches.
    /// </summary>
    /// <param name="saveKeys">What saves it, for the line under the command, such as "Ctrl+Enter adds it".</param>
    public SavedCommandEditor(SavedCommand? command, string ownerName, ICredentialStore? credentials = null, IReadOnlyList<ConnectionNode>? owners = null, string? saveKeys = null)
    {
        _owners = owners ?? [];
        _credentialId = command?.PasswordCredentialId;
        _linkId = command?.LinkId;
        Spacing = 12;
        _passwordNotes.Children.AddRange([_passwordRisk, _passwordReach, _passwordPlain, _passwordSafer]);

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

        Children.AddRange(
        [
            _ownerLine,
            Dialogs.Field("Name", _name),
            Dialogs.Field("Command", _text),
            new TextBlock
            {
                Text = "{host}, {username} and {name} are filled in from the connection. Several lines are pasted as one."
                    + (saveKeys is null ? string.Empty : $" {saveKeys}."),
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
            _passwordNotes,
            _hint,
            _error,
        ]);
        _ownerLine.Text = $"On {ownerName}";
    }

    /// <summary>
    /// Offers the tree's places to save it on, any number of them, with <paramref name="chosen"/> ticked, in place of
    /// the line naming one. A tree with no connections has only the defaults, so it keeps the line.
    /// </summary>
    public void UsePlaces(ConnectionTree tree, IEnumerable<ConnectionNode> chosen)
    {
        if (tree.Root.Children.Count == 0)
        {
            _fixedPlaces = [tree.Root];
            ShowPassword();
            return;
        }

        _picker = new CommandPlacePicker(tree, chosen);
        AutomationProperties.SetName(_picker, "Save on");
        _picker.Changed += (_, _) => ShowPassword();
        Children[Children.IndexOf(_ownerLine)] = Dialogs.Field("Save on", _picker);
        ShowPassword();
    }

    /// <summary>Lists the credentials, with the command's chosen, once the editor is on screen.</summary>
    public Task LoadAsync() => _passwordCredential?.RefreshAsync(_credentialId) ?? Task.CompletedTask;

    /// <summary>Where the command is to be saved, when the editor was given places; empty otherwise.</summary>
    public IReadOnlyList<ConnectionNode> ChosenPlaces => _picker?.Chosen ?? _fixedPlaces ?? [];

    /// <summary>Where it's saved, so the editor can say which connections a password it types reaches.</summary>
    IReadOnlyList<ConnectionNode> SavedOn => _picker is not null || _fixedPlaces is not null ? ChosenPlaces : _owners;

    internal CommandPlacePicker? PlacePicker => _picker;
    internal TextBox NameBox => _name;
    internal TextBox TextBox => _text;
    internal CheckBox ConfirmBox => _confirm;
    internal CheckBox PressEnterBox => _pressEnter;
    internal CheckBox WhenConnectedBox => _whenConnected;
    internal ComboBox PasswordBox => _password;
    internal CredentialPicker? PasswordCredentialPicker => _passwordCredential;

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
        var choice = enter ? PasswordChoice : CommandPassword.None;
        if (_passwordCredential is not null)
        {
            _passwordCredential.IsVisible = choice == CommandPassword.Credential;
        }

        _passwordNotes.IsVisible = choice != CommandPassword.None;
        var (kind, reach) = ReachNotice(choice);
        _passwordReach.Kind = kind;
        _passwordReach.Text = reach;
        _passwordReach.IsVisible = reach is not null;
        _passwordPlain.IsVisible = choice != CommandPassword.None && SavedOn.Any(SavedCommands.ReachesUnencrypted);
    }

    /// <summary>
    /// Which connections the password goes to. A chosen credential's, saved on a folder, goes to every connection in it
    /// that asks, and one server that's been broken into could ask just to collect it; from the defaults it isn't typed
    /// at all. A login password is each connection's own.
    /// </summary>
    (NoticeKind Kind, string? Text) ReachNotice(CommandPassword choice)
    {
        var places = SavedOn;
        var folders = places.OfType<ConnectionFolder>().ToList();
        if (choice == CommandPassword.None || places.Count == 0)
        {
            return (NoticeKind.Info, null);
        }

        if (choice == CommandPassword.Login)
        {
            if (folders.Count == 0)
            {
                return (NoticeKind.Info, null);
            }

            string reach = folders.Count == 1 ? SavedCommands.PasswordReach(folders[0])!
                : folders.Any(f => f.Parent is null) ? "every saved connection"
                : $"every connection in {Names(folders)}";
            return (NoticeKind.Info, $"Saved here, it reaches {reach}. Each types its own login password, and only on saved connections.");
        }

        if (folders.Any(f => f.Parent is null))
        {
            return (NoticeKind.Error, NotFromDefaults);
        }

        if (folders.Count > 0)
        {
            return (NoticeKind.Warning, $"Saved on {Names(folders)}, so every connection in {(folders.Count == 1 ? "it" : "them")} gets this password when it asks. "
                + "One server there that's been broken into could ask just to collect it. Save it on the connections that need it.");
        }

        return places.Count > 1
            ? (NoticeKind.Info, $"Each of the {places.Count} connections it's saved on gets this password when it asks.")
            : (NoticeKind.Info, null);
    }

    static string Names(IReadOnlyList<ConnectionFolder> nodes) =>
        nodes.Count == 1 ? nodes[0].Name : $"{string.Join(", ", nodes.Take(nodes.Count - 1).Select(n => n.Name))} and {nodes[^1].Name}";

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

    /// <summary>The command as filled in, or null with what's wrong shown under the fields.</summary>
    public SavedCommand? Build()
    {
        var text = (_text.Text ?? string.Empty).TrimEnd();
        if (string.IsNullOrWhiteSpace(text))
        {
            return Fail("Type the command to send.", _text);
        }

        if (text.Any(c => char.IsControl(c) && c is not ('\n' or '\r' or '\t')))
        {
            return Fail("The command has control characters, which would act as keys. Take them out.", _text);
        }

        bool pressEnter = _pressEnter.IsChecked == true;
        var password = pressEnter ? PasswordChoice : CommandPassword.None;
        Guid? credential = password == CommandPassword.Credential ? _passwordCredential?.SelectedId : null;
        if (_picker is not null && _picker.Chosen.Count == 0)
        {
            return Fail("Tick at least one place to save it on.", _picker);
        }

        if (password == CommandPassword.Credential && SavedOn.Any(n => n.Parent is null))
        {
            return Fail("It can't be saved like this: see the red message above.", _password);
        }

        if (password == CommandPassword.Credential && credential is null)
        {
            return Fail("Choose the credential whose password it types.", _passwordCredential);
        }

        _error.IsVisible = false;
        return new SavedCommand
        {
            Name = string.IsNullOrWhiteSpace(_name.Text) ? FirstWords(text) : _name.Text.Trim(),
            Text = text,
            PressEnter = pressEnter,
            Confirm = _confirm.IsChecked == true,
            WhenConnected = _whenConnected.IsChecked == true,
            Password = password,
            PasswordCredentialId = credential,
            LinkId = _linkId,
        };
    }

    SavedCommand? Fail(string message, Control? focus)
    {
        _error.Text = message;
        _error.IsVisible = true;
        focus?.Focus();
        return null;
    }

    static string FirstWords(string text)
    {
        var line = text.Split('\n', 2)[0].Trim();
        return line.Length <= 40 ? line : line[..39] + "…";
    }
}

/// <summary>Adds or changes a saved command in a window of its own, from a terminal or the properties pane.</summary>
sealed class SavedCommandWindow : Window
{
    internal const string PasswordRisk = SavedCommandEditor.PasswordRisk;
    internal const string PlainText = SavedCommandEditor.PlainText;
    internal const string Safer = SavedCommandEditor.Safer;
    internal const string NotFromDefaults = SavedCommandEditor.NotFromDefaults;

    /// <summary>A new command from a terminal, saved on whichever of the tree's places are ticked (<paramref name="chosen"/> to begin with).</summary>
    public SavedCommandWindow(SavedCommand? command, ConnectionTree tree, IReadOnlyList<ConnectionNode> chosen, ICredentialStore? credentials = null)
        : this(command, "the defaults for all connections", credentials) => Editor.UsePlaces(tree, chosen);

    /// <inheritdoc cref="SavedCommandEditor(SavedCommand?, string, ICredentialStore?, IReadOnlyList{ConnectionNode}?, string?)"/>
    public SavedCommandWindow(SavedCommand? command, string ownerName, ICredentialStore? credentials = null, IReadOnlyList<ConnectionNode>? owners = null)
    {
        bool adding = command is null;
        Title = adding ? "New command" : "Edit command";
        Width = 520;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Dialogs.PanelBrush;
        Editor = new SavedCommandEditor(command, ownerName, credentials, owners,
            $"{(OperatingSystem.IsMacOS() ? "Cmd" : "Ctrl")}+Enter {(adding ? "adds" : "saves")} it");

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
                Editor,
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { cancel, save } },
            },
        };
        Opened += async (_, _) =>
        {
            (adding ? Editor.NameBox : Editor.TextBox).Focus();
            await Editor.LoadAsync();
        };
    }

    public SavedCommandEditor Editor { get; }

    public IReadOnlyList<ConnectionNode> ChosenPlaces => Editor.ChosenPlaces;

    public string? Seed { set => Editor.Seed = value; }

    internal CommandPlacePicker? PlacePicker => Editor.PlacePicker;
    internal TextBox NameBox => Editor.NameBox;
    internal TextBox TextBox => Editor.TextBox;
    internal CheckBox ConfirmBox => Editor.ConfirmBox;
    internal CheckBox PressEnterBox => Editor.PressEnterBox;
    internal CheckBox WhenConnectedBox => Editor.WhenConnectedBox;
    internal ComboBox PasswordBox => Editor.PasswordBox;
    internal CredentialPicker? PasswordCredentialPicker => Editor.PasswordCredentialPicker;

    internal CommandPassword PasswordChoice
    {
        get => Editor.PasswordChoice;
        set => Editor.PasswordChoice = value;
    }

    internal void BecomeRoot() => Editor.BecomeRoot();

    internal void Save()
    {
        if (Editor.Build() is { } command)
        {
            Close(command);
        }
    }
}

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Orrery.App.Controls;
using Orrery.Core.Credentials;
using Orrery.Protocols.Ssh;

namespace Orrery.App.Credentials;

/// <summary>
/// Saved credentials, shown like connections: <see cref="List"/> goes in the side panel and
/// <see cref="Editor"/> in the right-hand pane. Secrets are passwords, or SSH private keys with an
/// optional passphrase. A locked store shows an Unlock button; leaving a secret blank when editing
/// keeps the saved one.
/// </summary>
sealed class CredentialsView
{
    const string KeyIcon = "M4 15a4 4 0 1 0 8 0a4 4 0 1 0-8 0 M11 12l9-9 M17 6l3 3 M15 8l2 2";
    static readonly IBrush TileBrush = new SolidColorBrush(Color.Parse("#2E2445")).ToImmutable();
    static readonly IBrush TileIconBrush = new SolidColorBrush(Color.Parse("#C9B0FF")).ToImmutable();

    readonly ICredentialStore _store;
    readonly Func<Task<bool>> _unlock;
    readonly ListBox _list = new() { Background = Brushes.Transparent, Padding = new Thickness(12, 0), Classes = { "side" } };
    readonly TextBlock _listEmpty = Pane.EmptyText("No saved credentials yet. Add one with the + button.", side: true);
    readonly Control _locked;
    readonly ContentControl _editorHost = new();
    readonly Control _form, _empty = Pane.EmptyText("Select a credential to edit it.");

    readonly TextBlock _title = new() { FontSize = 15, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
    readonly TextBlock _subtitle = new() { FontSize = 12, Foreground = Dialogs.MutedBrush };
    readonly ComboBox _kind = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    readonly TextBox _name = new(), _username = new(), _domain = new(), _notes = new() { AcceptsReturn = true, Height = 72, TextWrapping = TextWrapping.Wrap };
    readonly TextBox _password = new() { PasswordChar = '•' };
    readonly TextBox _key = new() { AcceptsReturn = true, Height = 120, FontFamily = Dialogs.MonoFont, FontSize = 11, TextWrapping = TextWrapping.NoWrap };
    readonly TextBox _passphrase = new() { PasswordChar = '•' };
    readonly TextBlock _secretHint = new() { FontSize = 11, Foreground = Dialogs.MutedBrush, TextWrapping = TextWrapping.Wrap, IsVisible = false };
    readonly TextBlock _error = new() { Foreground = Dialogs.DangerBrush, TextWrapping = TextWrapping.Wrap, IsVisible = false };
    readonly Control _passwordField, _keyFields, _domainField;
    readonly Button _save, _delete;
    Guid? _editing;
    bool _refreshing;

    /// <param name="unlock">Asks for the master password; true once the store is open.</param>
    public CredentialsView(ICredentialStore store, Func<Task<bool>> unlock)
    {
        _store = store;
        _unlock = unlock;

        _kind.Items.Add(new ComboBoxItem { Content = "Password", Tag = CredentialKind.Password });
        _kind.Items.Add(new ComboBoxItem { Content = "SSH private key", Tag = CredentialKind.SshKey });
        _kind.SelectedIndex = 0;
        _kind.SelectionChanged += (_, _) => ShowKindFields();

        _list.ItemTemplate = new FuncDataTemplate<CredentialSummary>((c, _) => new DockPanel
        {
            Margin = new Thickness(0, 4),
            Children =
            {
                Pane.Docked(new StrokeIcon { Data = Geometry.Parse(KeyIcon), Width = 15, Height = 15, Margin = new Thickness(0, 0, 10, 0), Foreground = TileIconBrush, VerticalAlignment = VerticalAlignment.Center }, Dock.Left),
                new StackPanel
                {
                    Children =
                    {
                        new TextBlock { Text = c?.Name, TextTrimming = TextTrimming.CharacterEllipsis },
                        new TextBlock { Text = c is null ? null : Describe(c), FontSize = 11, Foreground = Dialogs.MutedBrush, TextTrimming = TextTrimming.CharacterEllipsis },
                    },
                },
            },
        });
        _list.SelectionChanged += async (_, _) =>
        {
            if (!_refreshing && _list.SelectedItem is CredentialSummary summary)
            {
                await EditAsync(summary.Id);
            }
        };

        var add = new Button { Content = StrokeIcon.Create("M12 5v14 M5 12h14", 16), Width = 28, Height = 28 };
        add.Classes.Add("icon");
        ToolTip.SetTip(add, "New credential");
        Avalonia.Automation.AutomationProperties.SetName(add, "New credential");
        add.Click += async (_, _) =>
        {
            if (_store.IsLocked && !await _unlock())
            {
                return;
            }

            await RefreshAsync(select: null);
            StartNew();
        };

        var unlockButton = new Button { Content = "Unlock", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(14, 0) };
        unlockButton.Classes.Add("accent");
        unlockButton.Click += async (_, _) => await ShowAsync();
        _locked = new StackPanel
        {
            Children =
            {
                Pane.EmptyText("The vault is locked. Unlock it to see your saved credentials.", side: true),
                unlockButton,
            },
        };

        List = new DockPanel
        {
            Children =
            {
                Pane.Docked(new DockPanel
                {
                    Margin = new Thickness(14, 10, 10, 6),
                    Children =
                    {
                        Pane.Docked(add, Dock.Right),
                        new TextBlock { Text = "CREDENTIALS", Classes = { "section" }, VerticalAlignment = VerticalAlignment.Center },
                    },
                }, Dock.Top),
                new Panel { Children = { _list, _listEmpty, _locked } },
            },
        };

        var loadKey = new Button { Content = "Load from file…", MinHeight = 0, Padding = new Thickness(8, 2), FontSize = 11 };
        loadKey.Click += async (_, _) => await LoadKeyFileAsync();
        _save = new Button { Content = "Save", HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center };
        _save.Classes.Add("accent");
        _save.Click += async (_, _) => await SaveAsync();
        _delete = new Button { Content = "Delete", HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center };
        _delete.Classes.Add("danger");
        _delete.Click += async (_, _) => await DeleteAsync();

        _passwordField = Dialogs.Field("Password", _password);
        _domainField = Dialogs.Field("Domain (optional)", _domain);
        _keyFields = new StackPanel
        {
            Spacing = 10,
            Children =
            {
                new StackPanel
                {
                    Spacing = 4,
                    Children =
                    {
                        new DockPanel { Children = { Pane.Docked(loadKey, Dock.Right), Dialogs.Label("Private key (OpenSSH, PEM or .ppk)") } },
                        _key,
                    },
                },
                Dialogs.Field("Passphrase (if the key has one)", _passphrase),
            },
        };

        var tile = Pane.Tile(new StrokeIcon { Data = Geometry.Parse(KeyIcon), Foreground = TileIconBrush });
        tile.Background = TileBrush;
        _form = Pane.WithFooter(
            new StackPanel
            {
                Spacing = 20,
                Margin = new Thickness(16),
                Children =
                {
                    Pane.Header(tile, _title, _subtitle),
                    Pane.Section("Credential", Dialogs.Field("Name", _name), Dialogs.Field("Type", _kind), Dialogs.Field("Username", _username), _domainField),
                    Pane.Section("Secret", _passwordField, _keyFields, _secretHint),
                    Pane.Section("Notes", _notes),
                },
            },
            new StackPanel { Spacing = 8, Children = { _error, Pane.Pair(_save, _delete) } });

        Editor = _editorHost;
        _editorHost.Content = _empty;
    }

    /// <summary>The list, for the side panel.</summary>
    public Control List { get; }

    /// <summary>The selected credential's editor, for the right-hand pane.</summary>
    public Control Editor { get; }

    /// <summary>Reloads the list, unlocking the store first if needed. Cancelling leaves the locked notice up.</summary>
    public async Task ShowAsync()
    {
        if (_store.IsLocked && !await _unlock())
        {
            ShowLocked();
            return;
        }

        await RefreshAsync(select: _editing);
    }

    /// <summary>Forgets what was on screen, so nothing from a locked store stays visible.</summary>
    public void ShowLocked()
    {
        _editing = null;
        _list.ItemsSource = null;
        _list.IsVisible = _listEmpty.IsVisible = false;
        _locked.IsVisible = true;
        _editorHost.Content = _empty;
    }

    static string Describe(CredentialSummary c) => c.Kind == CredentialKind.Password
        ? c.DisplayUser
        : string.IsNullOrEmpty(c.DisplayUser) ? "SSH key" : $"{c.DisplayUser} · SSH key";

    CredentialKind SelectedKind => (CredentialKind)((ComboBoxItem)_kind.SelectedItem!).Tag!;

    void ShowKindFields()
    {
        bool password = SelectedKind == CredentialKind.Password;
        _passwordField.IsVisible = _domainField.IsVisible = password;
        _keyFields.IsVisible = !password;
        _subtitle.Text = password ? "Password" : "SSH private key";
    }

    async Task RefreshAsync(Guid? select)
    {
        var items = await _store.ListAsync();
        _refreshing = true;
        try
        {
            _list.ItemsSource = items;
            _list.SelectedItem = items.FirstOrDefault(c => c.Id == select);
        }
        finally
        {
            _refreshing = false;
        }

        _locked.IsVisible = false;
        _list.IsVisible = items.Count > 0;
        _listEmpty.IsVisible = items.Count == 0;
        if (select is { } id && items.Any(c => c.Id == id))
        {
            await EditAsync(id);
        }
        else if (_editing is not null)
        {
            _editing = null;
            _editorHost.Content = _empty;
        }
    }

    void StartNew()
    {
        _editing = null;
        _list.SelectedItem = null;
        _name.Text = _username.Text = _domain.Text = _password.Text = _key.Text = _passphrase.Text = _notes.Text = string.Empty;
        _kind.SelectedIndex = 0;
        _title.Text = "New credential";
        _secretHint.IsVisible = false;
        ShowDelete(false);
        _error.IsVisible = false;
        ShowKindFields();
        _editorHost.Content = _form;
        Avalonia.Threading.Dispatcher.UIThread.Post(() => _name.Focus(), Avalonia.Threading.DispatcherPriority.Background);
    }

    // Save takes the whole footer until there is something to delete.
    void ShowDelete(bool show)
    {
        _delete.IsVisible = show;
        Grid.SetColumnSpan(_save, show ? 1 : 3);
    }

    async Task EditAsync(Guid id)
    {
        if (await _store.GetAsync(id) is not { } credential)
        {
            return;
        }

        _editing = id;
        _title.Text = credential.Name;
        _name.Text = credential.Name;
        _kind.SelectedIndex = credential.Kind == CredentialKind.Password ? 0 : 1;
        _username.Text = credential.Username;
        _domain.Text = credential.Domain;
        _notes.Text = credential.Notes;
        _password.Text = _key.Text = _passphrase.Text = string.Empty;
        _secretHint.Text = credential.Kind == CredentialKind.Password
            ? "Leave the password blank to keep the saved one."
            : "Leave the key and passphrase blank to keep the saved ones.";
        _secretHint.IsVisible = true;
        ShowDelete(true);
        _error.IsVisible = false;
        ShowKindFields();
        _editorHost.Content = _form;
    }

    async Task LoadKeyFileAsync()
    {
        if (TopLevel.GetTopLevel(Editor)?.StorageProvider is not { } storage)
        {
            return;
        }

        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Choose a private key", AllowMultiple = false });
        if (files.Count == 0)
        {
            return;
        }

        await using var stream = await files[0].OpenReadAsync();
        if (stream.Length > 64 * 1024)
        {
            ShowError("That file is too large to be a private key.");
            return;
        }

        using var reader = new StreamReader(stream);
        _key.Text = await reader.ReadToEndAsync();
    }

    async Task SaveAsync()
    {
        var name = _name.Text?.Trim();
        if (string.IsNullOrEmpty(name))
        {
            ShowError("Give the credential a name.");
            return;
        }

        if (_store.IsLocked && !await _unlock())
        {
            return;
        }

        var existing = _editing is { } id ? await _store.GetAsync(id) : null;
        var kind = SelectedKind;
        string secret;
        string? passphrase = null;

        if (kind == CredentialKind.Password)
        {
            if (!string.IsNullOrEmpty(_password.Text)) secret = _password.Text;
            else if (existing?.Kind == CredentialKind.Password) secret = existing.Secret;
            else { ShowError("Enter a password."); return; }
        }
        else
        {
            bool keepKey = string.IsNullOrWhiteSpace(_key.Text) && existing?.Kind is CredentialKind.SshKey or CredentialKind.SshKeyWithPassphrase;
            if (!keepKey && string.IsNullOrWhiteSpace(_key.Text))
            {
                ShowError("Paste a private key or load one from a file.");
                return;
            }

            secret = keepKey ? existing!.Secret : _key.Text!.Trim() + "\n";
            passphrase = !string.IsNullOrEmpty(_passphrase.Text) ? _passphrase.Text : keepKey ? existing!.Passphrase : null;
            if (SshSession.ValidatePrivateKey(secret, passphrase) is { } problem)
            {
                ShowError(problem);
                return;
            }

            kind = string.IsNullOrEmpty(passphrase) ? CredentialKind.SshKey : CredentialKind.SshKeyWithPassphrase;
        }

        var credential = new Credential(_editing ?? Guid.NewGuid(), name, kind,
            Blank(_username.Text), kind == CredentialKind.Password ? Blank(_domain.Text) : null, secret, passphrase, Blank(_notes.Text));

        try
        {
            await _store.SaveAsync(credential);
        }
        catch (Exception e) when (e is InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
        {
            ShowError(e.Message);
            return;
        }

        _editing = credential.Id;
        await RefreshAsync(select: credential.Id);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    async Task DeleteAsync()
    {
        if (_editing is not { } id || TopLevel.GetTopLevel(Editor) is not Window owner
            || !await Dialogs.ConfirmAsync(owner, "Delete credential?", $"\"{_name.Text}\" will be deleted. Connections that use it will ask for a login instead.", "Delete"))
        {
            return;
        }

        await _store.DeleteAsync(id);
        _editing = null;
        await RefreshAsync(select: null);
        _editorHost.Content = _empty;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Raised after a credential is saved or deleted, so pickers elsewhere can reload.</summary>
    public event EventHandler? Changed;

    void ShowError(string message)
    {
        _error.Text = message;
        _error.IsVisible = true;
    }

    static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Orrery.Core.Credentials;
using Orrery.Protocols.Ssh;

namespace Orrery.App.Credentials;

/// <summary>
/// Lists saved credentials and edits them: passwords, or SSH private keys with an optional passphrase.
/// The store must already be unlocked. Leaving a secret blank when editing keeps the saved one.
/// </summary>
sealed class CredentialsWindow : Window
{
    readonly ICredentialStore _store;
    readonly ListBox _list = new();
    readonly ComboBox _kind = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    readonly TextBox _name = new(), _username = new(), _domain = new(), _notes = new() { AcceptsReturn = true, Height = 64 };
    readonly TextBox _password = new() { PasswordChar = '•' };
    readonly TextBox _key = new() { AcceptsReturn = true, Height = 110, FontFamily = Dialogs.MonoFont, FontSize = 11, TextWrapping = TextWrapping.NoWrap };
    readonly TextBox _passphrase = new() { PasswordChar = '•' };
    readonly TextBlock _secretHint = new() { FontSize = 11, Foreground = Dialogs.MutedBrush };
    readonly TextBlock _error = new() { Foreground = Dialogs.DangerBrush, TextWrapping = TextWrapping.Wrap, IsVisible = false };
    readonly Control _passwordField, _keyFields, _domainField;
    readonly Button _delete;
    Guid? _editing;

    public CredentialsWindow(ICredentialStore store)
    {
        _store = store;

        Title = "Saved credentials";
        Width = 780;
        Height = 560;
        MinWidth = 640;
        MinHeight = 440;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Dialogs.PanelBrush;

        _kind.Items.Add(new ComboBoxItem { Content = "Password", Tag = CredentialKind.Password });
        _kind.Items.Add(new ComboBoxItem { Content = "SSH private key", Tag = CredentialKind.SshKey });
        _kind.SelectedIndex = 0;
        _kind.SelectionChanged += (_, _) => ShowKindFields();

        _list.ItemTemplate = new FuncDataTemplate<CredentialSummary>((c, _) => new StackPanel
        {
            Margin = new Thickness(2),
            Children =
            {
                new TextBlock { Text = c?.Name, FontWeight = FontWeight.SemiBold },
                new TextBlock
                {
                    Text = c is null ? null : c.Kind == CredentialKind.Password ? c.DisplayUser : $"{c.DisplayUser} · SSH key",
                    FontSize = 12,
                    Foreground = Dialogs.MutedBrush,
                },
            },
        });
        _list.SelectionChanged += async (_, _) =>
        {
            if (_list.SelectedItem is CredentialSummary summary)
            {
                await EditAsync(summary.Id);
            }
        };

        var add = new Button { Content = "New credential", HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 8, 0, 0) };
        add.Click += (_, _) => StartNew();

        var loadKey = new Button { Content = "Load from file…", FontSize = 12 };
        loadKey.Click += async (_, _) => await LoadKeyFileAsync();

        var save = new Button { Content = "Save", Padding = new Thickness(16, 6) };
        save.Classes.Add("accent");
        save.Click += async (_, _) => await SaveAsync();
        _delete = new Button { Content = "Delete", Padding = new Thickness(16, 6) };
        _delete.Click += async (_, _) => await DeleteAsync();

        _passwordField = Dialogs.Field("Password", _password);
        _domainField = Dialogs.Field("Domain (optional)", _domain);
        var keyHeader = new DockPanel();
        DockPanel.SetDock(loadKey, Dock.Right);
        keyHeader.Children.Add(loadKey);
        keyHeader.Children.Add(Dialogs.Label("Private key (OpenSSH, PEM or PuTTY .ppk)"));
        _keyFields = new StackPanel
        {
            Spacing = 10,
            Children =
            {
                new StackPanel { Spacing = 4, Children = { keyHeader, _key } },
                Dialogs.Field("Passphrase (if the key has one)", _passphrase),
            },
        };

        var left = new DockPanel { Width = 240, Margin = new Thickness(0, 0, 16, 0) };
        DockPanel.SetDock(add, Dock.Bottom);
        left.Children.Add(add);
        left.Children.Add(new Border { BorderBrush = new SolidColorBrush(Color.Parse("#2A2F38")), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Child = _list });

        var editor = new StackPanel
        {
            Spacing = 10,
            Children =
            {
                Dialogs.Field("Name", _name),
                Dialogs.Field("Type", _kind),
                Dialogs.Field("Username", _username),
                _domainField,
                _passwordField,
                _keyFields,
                _secretHint,
                Dialogs.Field("Notes", _notes),
                _error,
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { _delete, save } },
            },
        };

        var root = new DockPanel { Margin = new Thickness(20) };
        DockPanel.SetDock(left, Dock.Left);
        root.Children.Add(left);
        root.Children.Add(new ScrollViewer { Content = editor });
        Content = root;

        Opened += async (_, _) => await RefreshAsync(select: null);
        StartNew();
    }

    CredentialKind SelectedKind => (CredentialKind)((ComboBoxItem)_kind.SelectedItem!).Tag!;

    void ShowKindFields()
    {
        bool password = SelectedKind == CredentialKind.Password;
        _passwordField.IsVisible = _domainField.IsVisible = password;
        _keyFields.IsVisible = !password;
    }

    async Task RefreshAsync(Guid? select)
    {
        var items = await _store.ListAsync();
        _list.ItemsSource = items;
        _list.SelectedItem = items.FirstOrDefault(c => c.Id == select);
    }

    void StartNew()
    {
        _editing = null;
        _list.SelectedItem = null;
        _name.Text = _username.Text = _domain.Text = _password.Text = _key.Text = _passphrase.Text = _notes.Text = string.Empty;
        _kind.SelectedIndex = 0;
        _secretHint.Text = string.Empty;
        _delete.IsEnabled = false;
        _error.IsVisible = false;
        ShowKindFields();
        _name.Focus();
    }

    async Task EditAsync(Guid id)
    {
        if (await _store.GetAsync(id) is not { } credential)
        {
            return;
        }

        _editing = id;
        _name.Text = credential.Name;
        _kind.SelectedIndex = credential.Kind == CredentialKind.Password ? 0 : 1;
        _username.Text = credential.Username;
        _domain.Text = credential.Domain;
        _notes.Text = credential.Notes;
        _password.Text = _key.Text = _passphrase.Text = string.Empty;
        _secretHint.Text = credential.Kind == CredentialKind.Password
            ? "Leave the password blank to keep the saved one."
            : "Leave the key and passphrase blank to keep the saved ones.";
        _delete.IsEnabled = true;
        _error.IsVisible = false;
        ShowKindFields();
    }

    async Task LoadKeyFileAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Choose a private key", AllowMultiple = false });
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

        await RefreshAsync(select: credential.Id);
    }

    async Task DeleteAsync()
    {
        if (_editing is { } id)
        {
            await _store.DeleteAsync(id);
            await RefreshAsync(select: null);
            StartNew();
        }
    }

    void ShowError(string message)
    {
        _error.Text = message;
        _error.IsVisible = true;
    }

    static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}

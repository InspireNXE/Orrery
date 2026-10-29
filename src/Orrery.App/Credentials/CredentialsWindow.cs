using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Orrery.Core.Credentials;

namespace Orrery.App.Credentials;

/// <summary>
/// Lists saved credentials and edits them. The store must already be unlocked.
/// Leaving the password blank when editing keeps the saved one.
/// </summary>
sealed class CredentialsWindow : Window
{
    static readonly IBrush MutedBrush = new SolidColorBrush(Color.Parse("#9AA3B2"));
    static readonly IBrush DangerBrush = new SolidColorBrush(Color.Parse("#FF9AA2"));

    readonly ICredentialStore _store;
    readonly ListBox _list = new();
    readonly TextBox _name = new(), _username = new(), _domain = new(), _notes = new() { AcceptsReturn = true, Height = 64 };
    readonly TextBox _password = new() { PasswordChar = '•' };
    readonly TextBlock _passwordHint = new() { FontSize = 11, Foreground = MutedBrush };
    readonly TextBlock _error = new() { Foreground = DangerBrush, TextWrapping = TextWrapping.Wrap, IsVisible = false };
    readonly Button _delete;
    Guid? _editing;

    public CredentialsWindow(ICredentialStore store)
    {
        _store = store;

        Title = "Saved credentials";
        Width = 760;
        Height = 480;
        MinWidth = 620;
        MinHeight = 400;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.Parse("#14161B"));

        _list.ItemTemplate = new FuncDataTemplate<CredentialSummary>((c, _) => new StackPanel
        {
            Margin = new Thickness(2),
            Children =
            {
                new TextBlock { Text = c?.Name, FontWeight = FontWeight.SemiBold },
                new TextBlock { Text = c?.DisplayUser, FontSize = 12, Foreground = MutedBrush },
            },
        });
        _list.SelectionChanged += async (_, _) =>
        {
            if (_list.SelectedItem is CredentialSummary summary)
            {
                await EditAsync(summary.Id);
            }
        };

        var add = new Button { Content = "New credential", HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center };
        add.Click += (_, _) => StartNew();

        var save = new Button { Content = "Save", Padding = new Thickness(16, 6) };
        save.Classes.Add("accent");
        save.Click += async (_, _) => await SaveAsync();
        _delete = new Button { Content = "Delete", Padding = new Thickness(16, 6) };
        _delete.Click += async (_, _) => await DeleteAsync();

        var left = new DockPanel { Width = 240, Margin = new Thickness(0, 0, 16, 0) };
        DockPanel.SetDock(add, Dock.Bottom);
        add.Margin = new Thickness(0, 8, 0, 0);
        left.Children.Add(add);
        left.Children.Add(new Border { BorderBrush = new SolidColorBrush(Color.Parse("#2A2F38")), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Child = _list });

        var editor = new StackPanel
        {
            Spacing = 10,
            Children =
            {
                Field("Name", _name),
                Field("Username", _username),
                Field("Domain (optional)", _domain),
                new StackPanel { Spacing = 4, Children = { Label("Password"), _password, _passwordHint } },
                Field("Notes", _notes),
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

    static TextBlock Label(string text) => new() { Text = text, FontSize = 12, Foreground = MutedBrush };

    static Control Field(string label, Control input) => new StackPanel { Spacing = 4, Children = { Label(label), input } };

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
        _name.Text = _username.Text = _domain.Text = _password.Text = _notes.Text = string.Empty;
        _passwordHint.Text = string.Empty;
        _delete.IsEnabled = false;
        _error.IsVisible = false;
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
        _username.Text = credential.Username;
        _domain.Text = credential.Domain;
        _notes.Text = credential.Notes;
        _password.Text = string.Empty;
        _passwordHint.Text = "Leave blank to keep the saved password.";
        _delete.IsEnabled = true;
        _error.IsVisible = false;
    }

    async Task SaveAsync()
    {
        var name = _name.Text?.Trim();
        if (string.IsNullOrEmpty(name))
        {
            ShowError("Give the credential a name.");
            return;
        }

        string secret;
        if (!string.IsNullOrEmpty(_password.Text))
        {
            secret = _password.Text;
        }
        else if (_editing is { } id && await _store.GetAsync(id) is { } existing)
        {
            secret = existing.Secret;
        }
        else
        {
            ShowError("Enter a password.");
            return;
        }

        var credential = new Credential(_editing ?? Guid.NewGuid(), name, CredentialKind.Password,
            Blank(_username.Text), Blank(_domain.Text), secret, Notes: Blank(_notes.Text));

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

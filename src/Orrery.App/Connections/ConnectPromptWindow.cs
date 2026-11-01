using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Orrery.App.Controls;
using Orrery.App.Credentials;
using Orrery.Core.Credentials;

namespace Orrery.App.Connections;

/// <summary>What to log in with. <see cref="CredentialId"/> is set when a saved credential was used or created.</summary>
sealed record ConnectPromptResult(string? Username, string? Domain, string Password, Guid? CredentialId, bool RememberForConnection);

/// <summary>
/// Asks how to log in when a connection has no saved credential: pick one, or type a username and
/// password and optionally save them.
/// </summary>
sealed class ConnectPromptWindow : Window
{
    readonly ICredentialStore _store;
    readonly string _host;
    readonly CredentialPicker _picker;
    readonly TextBox _username = new(), _domain = new(), _password = new PasswordBox();
    readonly CheckBox _save = new() { Content = "Save as a credential" };
    readonly CheckBox _remember = new() { Content = "Use it for this connection from now on", IsChecked = true };
    readonly Notice _error = new(NoticeKind.Error) { IsVisible = false };
    readonly bool _offerRemember;

    public ConnectPromptWindow(ICredentialStore store, string title, string host, string? username, string? domain, bool offerRemember)
    {
        _store = store;
        _host = host;
        _offerRemember = offerRemember;
        _picker = new CredentialPicker(store, "None: type below");
        _username.Text = username;
        _domain.Text = domain;

        Title = "Connect";
        Width = 420;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Dialogs.PanelBrush;

        var connect = new Button { Content = "Connect", IsDefault = true, Padding = new Thickness(16, 6) };
        connect.Classes.Add("accent");
        connect.Click += async (_, _) => await SubmitAsync();
        var cancel = new Button { Content = "Cancel", IsCancel = true, Padding = new Thickness(16, 6) };
        cancel.Click += (_, _) => Close(null);

        _picker.ChoiceChanged += (_, _) => ApplyChoice();
        _save.IsCheckedChanged += (_, _) => _remember.IsVisible = _offerRemember && (_save.IsChecked == true || _picker.SelectedId is not null);
        _remember.IsVisible = false;

        Content = new StackPanel
        {
            Spacing = 12,
            Margin = new Thickness(24),
            Children =
            {
                new TextBlock { Text = title, FontSize = 16, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis },
                new TextBlock { Text = host, FontFamily = Dialogs.MonoFont, FontSize = 12, Foreground = Dialogs.MutedBrush },
                Dialogs.Field("Saved credential", _picker),
                Dialogs.Field("Username", _username),
                Dialogs.Field("Domain (optional)", _domain),
                Dialogs.Field("Password", _password),
                _save,
                _remember,
                _error,
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { cancel, connect } },
            },
        };

        Opened += async (_, _) =>
        {
            await _picker.RefreshAsync(select: null);
            (string.IsNullOrEmpty(_username.Text) ? _username : _password).Focus();
        };

        // If the vault locks itself while this is open, stop showing what came out of it. The choice stays;
        // Connect asks for the master password again.
        if (store is VaultFileStore vault)
        {
            EventHandler onAutoLocked = (_, _) => Dispatcher.UIThread.Post(async () =>
            {
                try
                {
                    var chosen = _picker.SelectedId;
                    if (chosen is not null)
                    {
                        _username.Text = _domain.Text = null;
                    }

                    await _picker.RefreshAsync(chosen);
                }
                catch (Exception)
                {
                    // The lock has happened; a failure redrawing the list mustn't take the app down.
                }
            });
            vault.AutoLocked += onAutoLocked;
            Closed += (_, _) => vault.AutoLocked -= onAutoLocked;
        }
    }

    void ApplyChoice()
    {
        bool typing = _picker.SelectedId is null;
        _username.IsEnabled = _domain.IsEnabled = _password.IsEnabled = _save.IsVisible = typing;
        _password.Watermark = typing ? null : "Saved";
        if (_picker.SelectedSummary is { } summary)
        {
            _username.Text = summary.Username;
            _domain.Text = summary.Domain;
            _password.Text = string.Empty;
        }

        _remember.IsVisible = _offerRemember && (!typing || _save.IsChecked == true);
    }

    async Task SubmitAsync()
    {
        try
        {
            if (_picker.SelectedId is { } id)
            {
                if (!await VaultUnlockWindow.EnsureUnlockedAsync(_store, this))
                {
                    return;
                }

                if (await _store.GetAsync(id) is not { } credential)
                {
                    ShowError("That saved credential couldn't be read.");
                    return;
                }

                Close(new ConnectPromptResult(credential.Username, credential.Domain, credential.Secret, id, _remember.IsChecked == true));
                return;
            }

            string? username = Blank(_username.Text), domain = Blank(_domain.Text);
            var password = _password.Text ?? string.Empty;
            Guid? saved = null;
            if (_save.IsChecked == true)
            {
                if (!await VaultUnlockWindow.EnsureUnlockedAsync(_store, this))
                {
                    return;
                }

                var credential = new Credential(Guid.NewGuid(), username is null ? _host : $"{username}@{_host}",
                    CredentialKind.Password, username, domain, password);
                await _store.SaveAsync(credential);
                saved = credential.Id;
            }

            Close(new ConnectPromptResult(username, domain, password, saved, saved is not null && _remember.IsChecked == true));
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            ShowError(e.Message);
        }
    }

    void ShowError(string message)
    {
        _error.Text = message;
        _error.IsVisible = true;
    }

    static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}

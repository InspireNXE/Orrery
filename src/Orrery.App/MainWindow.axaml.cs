using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Orrery.App.Credentials;
using Orrery.App.Security;
using Orrery.Core.Credentials;
using Orrery.App.Sessions;
using Orrery.Core.Security;
using Orrery.Core.Sessions;
using Orrery.Core.Storage;
using Orrery.Protocols.Rdp;

namespace Orrery.App;

public partial class MainWindow : Window
{
    const int DefaultRdpPort = 3389;
    const double TabHeaderHeight = 48;

    readonly TabControl _sessions;
    readonly TextBlock _emptyState;
    readonly TextBox _host, _user, _domain, _password;
    readonly TextBlock _error;
    readonly ServerCertificateValidator _certificateValidator;

    // For the XAML previewer only.
    public MainWindow() : this(DataLocationResolver.ForPortable(Path.GetTempPath(), PlatformFolders.ForCurrentOs(), PortableDataChoice.WithApp))
    {
    }

    public MainWindow(DataLocation location)
    {
        AvaloniaXamlLoader.Load(this);

        var storageText = this.FindControl<TextBlock>("StorageText")!;
        storageText.Text = $"{(location.Mode == StorageMode.Portable ? "Portable" : "Installed")} · {location.DataDir}";
        ToolTip.SetTip(storageText, location.DataDir);

        _certificateValidator = new ServerCertificateValidator(
            new JsonCertificateTrustStore(location.FileIn("trusted-certificates.json")),
            prompt => Dispatcher.UIThread.InvokeAsync(
                () => new CertificatePromptWindow(prompt).ShowDialog<CertificateTrustDecision>(this)));

        _sessions = this.FindControl<TabControl>("Sessions")!;
        _emptyState = this.FindControl<TextBlock>("EmptyState")!;
        _host = this.FindControl<TextBox>("HostBox")!;
        _user = this.FindControl<TextBox>("UserBox")!;
        _domain = this.FindControl<TextBox>("DomainBox")!;
        _password = this.FindControl<TextBox>("PasswordBox")!;
        _error = this.FindControl<TextBlock>("ErrorText")!;
        _credentialBox = this.FindControl<ComboBox>("CredentialBox")!;
        _saveCredential = this.FindControl<CheckBox>("SaveCredentialBox")!;
        _lockVault = this.FindControl<Button>("LockVaultButton")!;

        _credentials = CredentialStores.For(location);
        if (_credentials is VaultFileStore vault)
        {
            vault.AutoLocked += (_, _) => Dispatcher.UIThread.Post(() => _ = RefreshCredentialChoicesAsync());
        }

        _credentialBox.SelectionChanged += async (_, _) => await OnCredentialChoiceChangedAsync();
        _ = RefreshCredentialChoicesAsync();

        Closing += (_, _) =>
        {
            foreach (var view in OpenViews())
            {
                _ = view.Session.DisposeAsync().AsTask();
            }

            (_credentials as IDisposable)?.Dispose();
        };
    }

    async void OnConnectClick(object? sender, RoutedEventArgs e)
    {
        if (!TryParseHost(_host.Text, out var host, out var port))
        {
            ShowError("Enter a host name or IP address, optionally followed by :port.");
            return;
        }

        ShowError(null);
        string? username = NullIfEmpty(_user.Text), domain = NullIfEmpty(_domain.Text);
        string password;

        try
        {
            if (SelectedCredential is { } summary)
            {
                if (!await VaultUnlockWindow.EnsureUnlockedAsync(_credentials, this))
                {
                    return;
                }

                if (await _credentials.GetAsync(summary.Id) is not { } credential)
                {
                    ShowError($"The saved credential \"{summary.Name}\" couldn't be read.");
                    return;
                }

                (username, domain, password) = (credential.Username, credential.Domain, credential.Secret);
            }
            else
            {
                password = _password.Text ?? string.Empty;
                if (_saveCredential.IsChecked == true && !await SaveTypedCredentialAsync(host, username, domain, password))
                {
                    return;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            ShowError(ex.Message);
            return;
        }

        var target = new ConnectionTarget(host, port, username, domain);
        var (width, height) = DesktopSizeForView();

        var session = RdpSession.Start(target, password, width, height, _certificateValidator);
        _password.Text = string.Empty;
        AddTab(new RemoteFrameView(session));
    }

    // ---- Saved credentials ----

    // The item that asks to unlock the vault; selecting it shows the unlock dialog.
    const string UnlockChoice = "Unlock vault to choose…";
    const string NoCredentialChoice = "None: type credentials below";

    readonly ICredentialStore _credentials;
    readonly ComboBox _credentialBox;
    readonly CheckBox _saveCredential;
    readonly Button _lockVault;
    bool _refreshingChoices;

    CredentialSummary? SelectedCredential => (_credentialBox.SelectedItem as ComboBoxItem)?.Tag as CredentialSummary;

    async Task RefreshCredentialChoicesAsync(Guid? select = null)
    {
        _refreshingChoices = true;
        try
        {
            var items = new List<ComboBoxItem> { new() { Content = NoCredentialChoice } };
            if (_credentials.IsLocked)
            {
                items.Add(new ComboBoxItem { Content = _credentials.Exists ? UnlockChoice : "Create a vault to save credentials…", Tag = UnlockChoice });
            }
            else
            {
                items.AddRange((await _credentials.ListAsync()).Select(c => new ComboBoxItem
                {
                    Content = c.Username is null ? c.Name : $"{c.Name}  ({c.DisplayUser})",
                    Tag = c,
                }));
            }

            _credentialBox.ItemsSource = items;
            _credentialBox.SelectedItem = items.FirstOrDefault(i => i.Tag is CredentialSummary c && c.Id == select) ?? items[0];
            _lockVault.IsVisible = _credentials is VaultFileStore && !_credentials.IsLocked;
        }
        finally
        {
            _refreshingChoices = false;
        }

        ApplyCredentialChoice();
    }

    async Task OnCredentialChoiceChangedAsync()
    {
        if (_refreshingChoices)
        {
            return;
        }

        if ((_credentialBox.SelectedItem as ComboBoxItem)?.Tag is UnlockChoice)
        {
            bool unlocked = await VaultUnlockWindow.EnsureUnlockedAsync(_credentials, this);
            await RefreshCredentialChoicesAsync();
            if (unlocked)
            {
                _credentialBox.IsDropDownOpen = true;
            }

            return;
        }

        ApplyCredentialChoice();
    }

    /// <summary>A saved credential fills in and locks the user fields; "None" frees them again.</summary>
    void ApplyCredentialChoice()
    {
        var summary = SelectedCredential;
        bool typing = summary is null;

        _user.IsEnabled = _domain.IsEnabled = _password.IsEnabled = typing;
        _saveCredential.IsVisible = typing;
        _password.Watermark = typing ? null : "Saved";
        if (summary is not null)
        {
            _user.Text = summary.Username;
            _domain.Text = summary.Domain;
            _password.Text = string.Empty;
            _saveCredential.IsChecked = false;
        }
    }

    async Task<bool> SaveTypedCredentialAsync(string host, string? username, string? domain, string password)
    {
        if (!await VaultUnlockWindow.EnsureUnlockedAsync(_credentials, this))
        {
            return false;
        }

        var name = username is null ? host : $"{username}@{host}";
        var credential = new Credential(Guid.NewGuid(), name, CredentialKind.Password, username, domain, password);
        await _credentials.SaveAsync(credential);
        await RefreshCredentialChoicesAsync(select: credential.Id);
        return true;
    }

    async void OnManageCredentialsClick(object? sender, RoutedEventArgs e)
    {
        if (!await VaultUnlockWindow.EnsureUnlockedAsync(_credentials, this))
        {
            return;
        }

        var selected = SelectedCredential?.Id;
        await new CredentialsWindow(_credentials).ShowDialog(this);
        await RefreshCredentialChoicesAsync(selected);
    }

    async void OnLockVaultClick(object? sender, RoutedEventArgs e)
    {
        _credentials.Lock();
        await RefreshCredentialChoicesAsync();
    }

    void AddTab(RemoteFrameView view)
    {
        var title = new TextBlock { Text = view.Session.Target.Host, VerticalAlignment = VerticalAlignment.Center };
        var close = new Button
        {
            Content = "✕",
            FontSize = 11,
            Padding = new Thickness(6, 2),
            Background = null,
            VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTip.SetTip(close, "Disconnect and close");

        var tab = new TabItem
        {
            Header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { title, close } },
            Content = view,
        };

        close.Click += async (_, _) =>
        {
            _sessions.Items.Remove(tab);
            UpdateEmptyState();
            await view.Session.DisposeAsync();
        };

        _sessions.Items.Add(tab);
        _sessions.SelectedItem = tab;
        UpdateEmptyState();
    }

    /// <summary>
    /// The starting desktop size, in device pixels, filling the session area. The view corrects it with
    /// a resize once the tab is laid out.
    /// </summary>
    (int Width, int Height) DesktopSizeForView()
    {
        var bounds = _sessions.Bounds;
        double scale = RenderScaling;
        int width = Math.Max(640, (int)(bounds.Width * scale));
        int height = Math.Max(480, (int)((bounds.Height - TabHeaderHeight) * scale));

        // Many RDP servers need the width to be a multiple of 4.
        return (width - width % 4, height);
    }

    IEnumerable<RemoteFrameView> OpenViews() =>
        _sessions.Items.OfType<TabItem>().Select(t => t.Content).OfType<RemoteFrameView>();

    void UpdateEmptyState() => _emptyState.IsVisible = _sessions.Items.Count == 0;

    void ShowError(string? message)
    {
        _error.Text = message;
        _error.IsVisible = message is not null;
    }

    static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    /// <summary>Accepts "host", "host:port", "[ipv6]" and "[ipv6]:port".</summary>
    static bool TryParseHost(string? input, out string host, out int port)
    {
        host = string.Empty;
        port = DefaultRdpPort;
        var text = input?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        string? portText = null;
        if (text.StartsWith('['))
        {
            int end = text.IndexOf(']');
            if (end < 0) return false;
            host = text[1..end];
            if (end + 1 < text.Length)
            {
                if (text[end + 1] != ':') return false;
                portText = text[(end + 2)..];
            }
        }
        else if (text.Count(c => c == ':') == 1)
        {
            int colon = text.IndexOf(':');
            host = text[..colon];
            portText = text[(colon + 1)..];
        }
        else
        {
            host = text; // A bare IPv6 address has several colons and no port.
        }

        if (portText is not null && (!int.TryParse(portText, out port) || port is < 1 or > 65535))
        {
            return false;
        }

        return host.Length > 0;
    }
}

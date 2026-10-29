using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Orrery.App.Security;
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

        Closing += (_, _) =>
        {
            foreach (var view in OpenViews())
            {
                _ = view.Session.DisposeAsync().AsTask();
            }
        };
    }

    void OnConnectClick(object? sender, RoutedEventArgs e)
    {
        if (!TryParseHost(_host.Text, out var host, out var port))
        {
            ShowError("Enter a host name or IP address, optionally followed by :port.");
            return;
        }

        ShowError(null);
        var target = new ConnectionTarget(host, port, NullIfEmpty(_user.Text), NullIfEmpty(_domain.Text));
        var (width, height) = DesktopSizeForView();

        var session = RdpSession.Start(target, _password.Text ?? string.Empty, width, height, _certificateValidator);
        _password.Text = string.Empty;
        AddTab(new RemoteFrameView(session));
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

using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;
using Orrery.Core.Sessions;

namespace Orrery.App.Sessions;

/// <summary>
/// A web page in a tab: a web connection or a machine's management page, drawn by the system's own browser engine
/// (WebView2 on Windows, WebKit on macOS, WebKitGTK on Linux). Its sign-ins and cookies are kept in
/// <c>web</c> in Orrery's data folder, apart from the default browser's. Back, forward, reload and "open in the
/// browser" are in the session toolbar, as other sessions' actions are.
/// </summary>
sealed class WebPageView : Panel
{
    readonly NativeWebView _web = new() { Background = new SolidColorBrush(Color.Parse("#0B0D10")) };
    bool _covered;
    static bool? _engineAvailable;

    /// <summary>
    /// Whether this computer has the engine to draw pages with. Windows 11 and macOS always do; Linux needs WebKitGTK
    /// (libwebkit2gtk-4.1), which the packages only recommend.
    /// </summary>
    public static bool EngineAvailable => _engineAvailable ??= !OperatingSystem.IsLinux()
        || (System.Runtime.InteropServices.NativeLibrary.TryLoad("libwebkit2gtk-4.1.so.0", out var library) && Free(library));

    static bool Free(IntPtr library)
    {
        System.Runtime.InteropServices.NativeLibrary.Free(library);
        return true;
    }

    /// <param name="dataFolder">Where the engine keeps cookies, sign-ins and its cache.</param>
    public WebPageView(Uri url, string dataFolder)
    {
        Address = url;
        Background = new SolidColorBrush(Color.Parse("#0B0D10"));
        _web.EnvironmentRequested += (_, e) =>
        {
            switch (e)
            {
                case WindowsWebView2EnvironmentRequestedEventArgs windows:
                    // By default WebView2 keeps its data beside the program, which for a portable copy means
                    // writing into the app's own folder.
                    windows.UserDataFolder = dataFolder;
                    break;
                case GtkWebViewEnvironmentRequestedEventArgs gtk:
                    gtk.BaseDataDirectory = Path.Combine(dataFolder, "data");
                    gtk.BaseCacheDirectory = Path.Combine(dataFolder, "cache");
                    // Orrery's window is an X11 one under XWayland, and a page can only be put inside it from X11.
                    gtk.ForceX11GdkBackend = true;
                    break;
            }
        };
        _web.NavigationStarted += (_, e) =>
        {
            if (e.Request is { } request)
            {
                Address = request;
            }

            Loading = true;
            Changed?.Invoke(this, EventArgs.Empty);
        };
        _web.NavigationCompleted += (_, e) =>
        {
            if (e.Request is { } request)
            {
                Address = request;
            }

            Loading = false;
            Changed?.Invoke(this, EventArgs.Empty);
        };
        // A link that asks for a new window opens here instead: a tab of its own would have no connection behind it.
        _web.NewWindowRequested += (_, e) =>
        {
            e.Handled = true;
            if (e.Request is { } request)
            {
                _web.Navigate(request);
            }
        };
        _web.Source = url;
        Children.Add(_web);
    }

    /// <summary>The page showing now, which links and redirects change.</summary>
    public Uri Address { get; private set; }

    public bool Loading { get; private set; } = true;

    public bool CanGoBack => _web.CanGoBack;

    public bool CanGoForward => _web.CanGoForward;

    /// <summary>The page moved, finished loading, or can now go back or forward.</summary>
    public event EventHandler? Changed;

    /// <summary>
    /// Whether something Orrery draws lies over the page (Settings, a pane opened over the session, quick connect's
    /// dimming). The engine's view is a window of its own, drawn above everything in Orrery's, so it steps aside.
    /// </summary>
    public bool Covered
    {
        get => _covered;
        set
        {
            _covered = value;
            _web.IsVisible = !value;
        }
    }

    public void GoBack() => _web.GoBack();

    public void GoForward() => _web.GoForward();

    public void Reload()
    {
        Loading = true;
        _web.Refresh();
        Changed?.Invoke(this, EventArgs.Empty);
    }
}

/// <summary>
/// The session behind a web page's tab. There's no connection to keep: it's open until its tab closes, and the
/// page itself does any connecting.
/// </summary>
sealed class WebSession(ConnectionTarget target) : IConnectionSession
{
    SessionStateChangedEventArgs? _last = new(SessionState.Connected, null);

    public ConnectionTarget Target { get; } = target;

    public SessionState State { get; private set; } = SessionState.Connected;

    public event EventHandler<SessionStateChangedEventArgs>? StateChanged;

    public SessionStateChangedEventArgs? LastChange => _last;

    public Task DisconnectAsync()
    {
        if (State != SessionState.Disconnected)
        {
            State = SessionState.Disconnected;
            _last = new SessionStateChangedEventArgs(SessionState.Disconnected, null);
            StateChanged?.Invoke(this, _last);
        }

        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => new(DisconnectAsync());
}

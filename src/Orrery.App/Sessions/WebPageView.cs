using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
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
    /// <summary>The made-up address a link meant for a new tab is sent to, so the page can be told apart from it.</summary>
    internal const string NewTabScheme = "orrery-tab";

    /// <summary>
    /// Drawn in Orrery's picture, WebView2 doesn't pass on its requests for new windows and opens them as windows of its
    /// own. That suits a script's pop-up, such as a sign-in that reports back to the page that opened it, but not a
    /// link: a link meant for a new tab (target=_blank, or a Ctrl- or middle-click) is sent to <see cref="NewTabScheme"/>
    /// instead, which is turned back into a tab here.
    /// </summary>
    const string NewTabLinks = """
        if (!window.__orreryTabs) {
          window.__orreryTabs = true;
          const open = (e, always) => {
            if (e.defaultPrevented) return;
            const a = e.target && e.target.closest && e.target.closest('a[href]');
            if (!a || !/^https?:/.test(a.href)) return;
            if (!always && !/^_(blank|new)$/i.test(a.target || '')) return;
            e.preventDefault();
            location.href = 'orrery-tab:' + encodeURIComponent(a.href);
          };
          window.addEventListener('click', e => { if (e.button === 0) open(e, e.ctrlKey || e.metaKey); });
          window.addEventListener('auxclick', e => { if (e.button === 1) open(e, true); });
        }
        """;

    readonly NativeWebView _web = new() { Background = new SolidColorBrush(Color.Parse("#0B0D10")) };
    bool _covered;
    IDisposable? _keys;
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

    /// <summary>
    /// Whether the page is drawn as part of Orrery's window, as on Windows, rather than as a window of its own laid
    /// over it. A page drawn over Orrery has to be hidden whenever Orrery draws something on top of it.
    /// </summary>
    public static bool DrawnInOrrery => OperatingSystem.IsWindows() && Environment.GetEnvironmentVariable("ORRERY_WEB_OFFSCREEN") != "0";

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
                    // Drawn into Orrery's own picture rather than as a window over it, so menus, Settings and panes
                    // opened over the session draw on top of the page instead of the page stepping aside for them.
                    windows.ExperimentalOffscreen = DrawnInOrrery;
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
            if (e.Request is { Scheme: NewTabScheme } tab)
            {
                e.Cancel = true;
                if (Uri.TryCreate(Uri.UnescapeDataString(tab.OriginalString[(NewTabScheme.Length + 1)..]), UriKind.Absolute, out var link)
                    && (link.Scheme == Uri.UriSchemeHttp || link.Scheme == Uri.UriSchemeHttps))
                {
                    NewWindowRequested?.Invoke(this, link);
                }

                return;
            }

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
            Failed = !e.IsSuccess;
            Changed?.Invoke(this, EventArgs.Empty);
            if (DrawnInOrrery && e.IsSuccess)
            {
                // A page that's gone by the time it runs is no loss.
                _web.InvokeScript(NewTabLinks).ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
            }
        };
        // A link or script that asks for a new window gets a tab of its own, as in a browser, rather than a bare
        // window outside Orrery.
        _web.NewWindowRequested += (_, e) =>
        {
            e.Handled = true;
            if (e.Request is { } request)
            {
                NewWindowRequested?.Invoke(this, request);
            }
        };
        if (OperatingSystem.IsWindows())
        {
            _web.AdapterCreated += (_, e) =>
            {
                if (e.TryGetPlatformHandle() is IWindowsWebView2PlatformHandle handle)
                {
                    _keys?.Dispose();
                    _keys = WebView2Keys.Watch(handle.CoreWebView2Controller, Offer);
                }
            };
            _web.AdapterDestroyed += (_, _) =>
            {
                _keys?.Dispose();
                _keys = null;
            };
        }

        _web.Source = url;
        Children.Add(_web);
    }

    /// <summary>The page showing now, which links and redirects change.</summary>
    public Uri Address { get; private set; }

    public bool Loading { get; private set; } = true;

    /// <summary>
    /// The last page didn't load: the site couldn't be reached, or its certificate isn't trusted. The engine shows
    /// why in the page, with a way past an untrusted certificate.
    /// </summary>
    public bool Failed { get; private set; }

    public bool CanGoBack => _web.CanGoBack;

    public bool CanGoForward => _web.CanGoForward;

    /// <summary>The page moved, finished loading, or can now go back or forward.</summary>
    public event EventHandler? Changed;

    /// <summary>The page asked to open this address in a new window.</summary>
    public event EventHandler<Uri>? NewWindowRequested;

    /// <summary>
    /// Whether something Orrery draws lies over the page (Settings, a pane opened over the session, quick connect's
    /// dimming). Where the engine's view is a window of its own, drawn above everything in Orrery's, it steps aside.
    /// </summary>
    public bool Covered
    {
        get => _covered;
        set
        {
            _covered = value;
            _web.IsVisible = !value || DrawnInOrrery;
        }
    }

    /// <summary>
    /// A shortcut key pressed in the page, offered to Orrery as if pressed on this view: the window's shortcuts see it
    /// on the way down and up. True when one of them used it, so the page doesn't get it too.
    /// </summary>
    bool Offer(Key key, KeyModifiers modifiers)
    {
        var e = new KeyEventArgs { RoutedEvent = KeyDownEvent, Key = key, KeyModifiers = modifiers, Source = this };
        RaiseEvent(e);
        return e.Handled;
    }

    public void GoBack() => _web.GoBack();

    public void GoForward() => _web.GoForward();

    /// <summary>The engine's own print preview, over the page.</summary>
    public void Print() => _web.ShowPrintUI();

    /// <summary>The page laid out for printing, as a PDF.</summary>
    public Task<Stream> PdfAsync() => _web.PrintToPdfStreamAsync();

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

using System.Runtime.Versioning;
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

    /// <summary>Whether a page engine has started in this run, and so may hold its data folder open until Orrery exits.</summary>
    public static bool EngineStarted { get; private set; }

    /// <param name="dataFolder">Where the engine keeps cookies, sign-ins and its cache.</param>
    public WebPageView(Uri url, string dataFolder)
    {
        EngineStarted = true;
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
                if (Uri.TryCreate(Uri.UnescapeDataString(tab.OriginalString[(NewTabScheme.Length + 1)..]), UriKind.Absolute, out var link))
                {
                    OpenNewTab(link);
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

            // Where there's no start-up script (Linux and macOS), the page is corrected once it has loaded.
            if (ActiveCorrection is not null)
            {
                RunColorScript();
            }
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
                OpenNewTab(request);
            }
        };
        if (OperatingSystem.IsWindows())
        {
            WatchWebView2();
        }

        _web.Source = url;
        Children.Add(_web);
    }

    /// <summary>Takes WebView2's keys and frames once its controller exists, and lets them go with it.</summary>
    [SupportedOSPlatform("windows")]
    void WatchWebView2()
    {
        _web.AdapterCreated += (_, e) =>
        {
            if (e.TryGetPlatformHandle() is IWindowsWebView2PlatformHandle handle)
            {
                _keys?.Dispose();
                _keys = WebView2Keys.Watch(handle.CoreWebView2Controller, Offer);
                Frames.Connect(handle.CoreWebView2);
            }
        };
        _web.AdapterDestroyed += (_, _) =>
        {
            _keys?.Dispose();
            _keys = null;
            Frames.Disconnect();
        };
    }

    /// <summary>
    /// Passes on a new tab the page asked for. Only web addresses: a tab opened this way is a navigation the engine
    /// starts itself, which would otherwise reach file: and the like that a page can't open. And only a few at a
    /// time, since a script can ask without a click, over and over.
    /// </summary>
    void OpenNewTab(Uri address)
    {
        if (address.Scheme != Uri.UriSchemeHttp && address.Scheme != Uri.UriSchemeHttps)
        {
            return;
        }

        var now = DateTime.UtcNow;
        while (_newTabs.Count > 0 && now - _newTabs.Peek() > NewTabWindow)
        {
            _newTabs.Dequeue();
        }

        if (_newTabs.Count >= MaxNewTabs)
        {
            return;
        }

        _newTabs.Enqueue(now);
        NewWindowRequested?.Invoke(this, address);
    }

    const int MaxNewTabs = 5;
    static readonly TimeSpan NewTabWindow = TimeSpan.FromSeconds(10);
    readonly Queue<DateTime> _newTabs = new();

    /// <summary>The page's picture, for recording it (Windows).</summary>
    public WebFrames Frames { get; } = new();

    ColorCorrection? _correction;
    bool _allowsCorrection = true;

    /// <summary>The color correction for the viewer's color vision, from Settings; null for none.</summary>
    internal ColorCorrection? Correction
    {
        get => _correction;
        set
        {
            _correction = value;
            ApplyColors();
        }
    }

    /// <summary>The connection's "Color correction": off shows the page in its own colors whatever Settings says.</summary>
    public bool AllowsColorCorrection
    {
        get => _allowsCorrection;
        set
        {
            _allowsCorrection = value;
            ApplyColors();
        }
    }

    /// <summary>The correction the page is drawn with: none where the connection turns it off.</summary>
    internal ColorCorrection? ActiveCorrection => _allowsCorrection ? _correction : null;

    /// <summary>
    /// Puts the correction on the page: on Windows as a start-up script, so each new page is corrected before it's
    /// first drawn, and on the page showing now.
    /// </summary>
    void ApplyColors()
    {
        Frames.ShownWith = ActiveCorrection;
        Frames.SetStartScript(ActiveCorrection is null ? null : ColorScript(ActiveCorrection));
        RunColorScript();
    }

    void RunColorScript()
    {
        try
        {
            // A page that's gone, or not there yet, by the time it runs is no loss: the next one gets the start-up script.
            _web.InvokeScript(ColorScript(ActiveCorrection)).ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
        }
        catch (InvalidOperationException)
        {
        }
    }

    /// <summary>
    /// Script that corrects the top page's colors, or takes the correction off when <paramref name="correction"/> is
    /// null. It puts an SVG feColorMatrix filter on the root element, which works in linear-light RGB as remote
    /// desktops are corrected. Built from DOM calls rather than markup or a style sheet, so a page's content security
    /// policy doesn't stop it, and put back if the page replaces the root's children. The root is the one element
    /// whose filter doesn't change how fixed elements are placed.
    /// </summary>
    internal static string ColorScript(ColorCorrection? correction) => $$"""
        (() => {
          if (window.top !== window) return;
          window.__orreryColors = "{{correction?.FilterValues ?? ""}}";
          const apply = () => {
            const root = document.documentElement;
            if (!root) return;
            const values = window.__orreryColors;
            let svg = document.getElementById("__orrery-colors");
            if (!values) {
              if (svg) svg.remove();
              if (root.style.getPropertyValue("filter").includes("__orrery-colors")) root.style.removeProperty("filter");
              return;
            }
            if (!svg) {
              const ns = "http://www.w3.org/2000/svg";
              svg = document.createElementNS(ns, "svg");
              svg.id = "__orrery-colors";
              svg.setAttribute("aria-hidden", "true");
              svg.setAttribute("width", "0");
              svg.setAttribute("height", "0");
              svg.style.position = "absolute";
              svg.style.pointerEvents = "none";
              const filter = document.createElementNS(ns, "filter");
              filter.id = "__orrery-colors-filter";
              filter.setAttribute("color-interpolation-filters", "linearRGB");
              const matrix = document.createElementNS(ns, "feColorMatrix");
              matrix.setAttribute("type", "matrix");
              filter.appendChild(matrix);
              svg.appendChild(filter);
              root.appendChild(svg);
            }
            svg.querySelector("feColorMatrix").setAttribute("values", values);
            root.style.setProperty("filter", "url(#__orrery-colors-filter)", "important");
          };
          apply();
          if (!window.__orreryColorsWatched) {
            window.__orreryColorsWatched = true;
            let watched = null;
            const watch = () => {
              const root = document.documentElement;
              if (root && root !== watched) {
                watched = root;
                new MutationObserver(() => {
                  if (window.__orreryColors && !document.getElementById("__orrery-colors")) apply();
                }).observe(root, { childList: true });
              }
            };
            new MutationObserver(() => { watch(); apply(); }).observe(document, { childList: true });
            watch();
          }
        })();
        """;

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
/// page itself does any connecting. It offers the page's picture as a screen, so a recording takes it as it takes a
/// remote desktop's; there's no input to send, since the page takes its own.
/// </summary>
sealed class WebSession(ConnectionTarget target, WebFrames? frames = null) : IFrameBufferSession
{
    readonly WebFrames _frames = frames ?? new WebFrames();

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

    public int Width => _frames.Width;

    public int Height => _frames.Height;

    public event EventHandler<FrameUpdatedEventArgs>? FrameUpdated
    {
        add => _frames.FrameUpdated += value;
        remove => _frames.FrameUpdated -= value;
    }

    public void CopyRegion(FrameRect region, IntPtr destination, int destinationStride) => _frames.CopyRegion(region, destination, destinationStride);

    public void SendKey(ushort scancode, bool pressed)
    {
    }

    public void ReleaseAllKeys()
    {
    }

    public void SendPointerMove(int x, int y)
    {
    }

    public void SendPointerButton(PointerButton button, bool pressed)
    {
    }

    public void SendWheel(int units, bool horizontal)
    {
    }

    public void RequestResize(int width, int height)
    {
    }
}

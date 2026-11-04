using System.Diagnostics;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.Logging;
using Orrery.App.Logging;
using Orrery.App.Setup;
using Orrery.App.Updates;
using Orrery.Core.Credentials;
using Orrery.Core.Diagnostics;
using Orrery.Core.Settings;
using Orrery.Core.Storage;
using Orrery.Core.Updates;

namespace Orrery.App;

public partial class App : Application
{
    const string RestartedFlag = UpdateCommand.RestartedFlag;
    const string MovedFromFlag = "--moved-from";
    static readonly TimeSpan RestartLockWait = TimeSpan.FromSeconds(15);

    DataFolderLock? _dataLock;
    string[] _args = [];
    bool _errorShown;

    /// <summary>Orrery is closing to start again (<see cref="Restart"/>), not quitting: an update put off until quitting waits.</summary>
    internal static bool Restarting { get; private set; }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        Palette.AddTo(Resources);
        Input.DialogKeys.Install();
        Input.RadioKeys.Install();
        Input.OverlappingRightClick.Install();
    }

    public override void OnFrameworkInitializationCompleted()
    {
        // A backstop only: each session should still contain its own failures. Without it an exception
        // escaping an async void handler on the UI thread ends the process, and every open session with it.
        // AppDomain.UnhandledException is left alone, since those can't be recovered.
        Dispatcher.UIThread.UnhandledException += OnUiThreadUnhandledException;
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.For("App").LogError(e.Exception, "Unobserved task exception");
            e.SetObserved();
        };

        // In every window, a click outside the text field being typed in leaves it, also on parts of the
        // window that can't take focus themselves; a click on something that can then focuses that.
        InputElement.PointerPressedEvent.AddClassHandler<Window>(LeaveTextFieldOnClickOutside, RoutingStrategies.Tunnel, handledEventsToo: true);

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Exit += (_, _) =>
            {
                AppLog.Stop();
                _dataLock?.Dispose();
            };
            _args = desktop.Args ?? [];

            var appDirectory = DataLocationResolver.AppDirectory(AppContext.BaseDirectory);
            var folders = PlatformFolders.ForCurrentOs();

            var storage = DataLocationResolver.Resolve(appDirectory, folders);
            desktop.MainWindow = FirstLaunch.Decide(storage) == SetupKind.None && storage is StartupStorage.Ready ready
                ? OpenMainWindow(ready.Location)
                : SetupWindow(desktop, storage, appDirectory, folders);
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// First-launch setup, which opens the main window when it's done. Closing it early while a portable copy has no data
    /// folder yet ends the app, as it always has; once the folder is known, closing takes the defaults and carries on, so
    /// setup isn't asked again.
    /// </summary>
    Window SetupWindow(IClassicDesktopStyleApplicationLifetime desktop, StartupStorage storage, string appDirectory, PlatformFolders folders)
    {
        var known = (storage as StartupStorage.Ready)?.Location;
        var wizard = new SetupWizard(
            known is null ? new PortableSetup(appDirectory, folders) : null,
            known is not null && CredentialStores.CanUseOsKeychain(known) ? CredentialStores.OsKeychain!.Name : null,
            ImportSources.OnThisComputer());

        bool started = false;
        void Start(DataLocation location, SetupChoices choices, IReadOnlyList<string>? imports, string? vaultPassword = null)
        {
            // Swap the main window before closing setup, or closing it would end the app.
            started = true;
            desktop.MainWindow = OpenMainWindow(location, choices, imports, vaultPassword);
            desktop.MainWindow.Show();
        }

        wizard.Completed += result =>
        {
            Start(result.Location ?? known!, result.Choices, result.Choices.Imports, result.VaultPassword);
            wizard.Close();
        };

        if (known is not null)
        {
            // The defaults, but nothing imported unasked: the main window's usual offer of mRemoteNG's connections still comes.
            wizard.Closing += (_, e) =>
            {
                if (!started && e.CloseReason == WindowCloseReason.WindowClosing)
                {
                    Start(known, new SetupChoices(), imports: null);
                }
            };
        }

        return wizard;
    }

    internal static void LeaveTextFieldOnClickOutside(Window window, PointerPressedEventArgs e)
    {
        if (window.FocusManager?.GetFocusedElement() is not TextBox field)
        {
            return;
        }

        // A field inside another control (a number box's spin buttons, say) counts that whole control as inside.
        var inside = field.TemplatedParent as Visual ?? field;
        if (e.Source is Visual source && (source == inside || inside.IsVisualAncestorOf(source)))
        {
            return;
        }

        window.FocusManager?.Focus(null);
    }

    void OnUiThreadUnhandledException(object? sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.For("App").LogError(e.Exception, "Unhandled exception on the UI thread");
        e.Handled = true;

        // One message at a time, so a fault that repeats doesn't stack up windows.
        if (_errorShown)
        {
            return;
        }

        _errorShown = true;
        var window = Dialogs.MessageWindow("Something went wrong",
            "Orrery hit an unexpected error and carried on. Open sessions may need reconnecting.");
        window.Closed += (_, _) => _errorShown = false;
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime { MainWindow: { IsVisible: true } owner })
        {
            window.ShowDialog(owner);
        }
        else
        {
            window.Show();
        }
    }

    /// <param name="setup">What first-launch setup chose, saved to the settings before the window reads them.</param>
    /// <param name="importOnStart">Files to import once the window opens; see <see cref="MainWindow"/>.</param>
    /// <param name="newVaultPassword">The master password setup chose for a new vault, made as the window opens.</param>
    Window OpenMainWindow(DataLocation location, SetupChoices? setup = null, IReadOnlyList<string>? importOnStart = null, string? newVaultPassword = null)
    {
        try
        {
            _dataLock = DataFolderLock.TryAcquire(location.DataDir);

            // After a restart the previous process may still be closing and holding the folder.
            var waitUntil = DateTime.UtcNow + RestartLockWait;
            while (_dataLock is null && _args.Contains(RestartedFlag) && DateTime.UtcNow < waitUntil)
            {
                Thread.Sleep(200);
                _dataLock = DataFolderLock.TryAcquire(location.DataDir);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.For("App").LogError(e, "Can't open data folder {DataDir}", location.DataDir);
            return MessageWindow("Orrery can't open its data folder", $"{location.DataDir}\n\n{e.Message}");
        }

        if (_dataLock is null)
        {
            return MessageWindow("Orrery is already running",
                $"Another copy of Orrery is using this data folder:\n{location.DataDir}");
        }

        AppLog.Start(location);
        Orrery.Protocols.Rdp.IronRdpLog.StartIfAsked(location.LogDir);
        if (setup is not null)
        {
            SaveSetup(location, setup);
        }

        int moved = Array.IndexOf(_args, MovedFromFlag);

        // "Orrery file.rdp", as Windows runs it for a .rdp file opened with Orrery: connected to once the window opens.
        var rdpFile = _args.FirstOrDefault(a => Orrery.Core.Import.RdpFileImporter.IsRdpFile(a) && File.Exists(a));
        return new MainWindow(location, movedFrom: moved >= 0 && moved + 1 < _args.Length ? _args[moved + 1] : null, openOnStart: rdpFile,
            importOnStart: importOnStart, updates: StartUpdates(location), newVaultPassword: newVaultPassword);
    }

    /// <summary>
    /// The update engine for this copy, once what an earlier update left behind is cleared away (the executable set
    /// aside, old downloads). Problems are logged, never shown; without an engine Orrery runs on, just without updates.
    /// </summary>
    static IUpdateService? StartUpdates(DataLocation location)
    {
        EngineUpdateService updates;
        try
        {
            updates = new EngineUpdateService(new Updater(location));
        }
        catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException or UriFormatException)
        {
            Log.For("Updates").LogError(e, "Couldn't set up update checks");
            return null;
        }

        try
        {
            updates.RemoveLeftovers();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.For("Updates").LogWarning(e, "Couldn't clear up after the last update");
        }

        return updates;
    }

    /// <summary>Saves setup's choices, once the data folder is this copy's. If they can't be saved, setup comes again next time.</summary>
    internal static void SaveSetup(DataLocation location, SetupChoices setup)
    {
        try
        {
            var store = AppSettingsStore.In(location);
            store.Save(setup.ApplyTo(store.Load()));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.For("App").LogError(e, "Couldn't save the setup choices in {DataDir}", location.DataDir);
        }
    }

    /// <summary>
    /// Starts a new Orrery and shuts this one down. The new process waits for the data folder lock, which
    /// this one releases on exit.
    /// </summary>
    public static void Restart(params string[] arguments)
    {
        if (Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop || Environment.ProcessPath is not { } exe)
        {
            return;
        }

        Restarting = true;

        var start = new ProcessStartInfo(RestartExecutable(exe, Environment.GetEnvironmentVariable("APPIMAGE")));
        if (Path.GetFileNameWithoutExtension(exe).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
            && Assembly.GetEntryAssembly()?.Location is { Length: > 0 } dll)
        {
            start.ArgumentList.Add(dll);
        }

        start.ArgumentList.Add(RestartedFlag);
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        try
        {
            Process.Start(start);
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Log.For("App").LogError(e, "Couldn't restart Orrery");
        }

        desktop.Shutdown();
    }

    /// <summary>
    /// What <see cref="Restart"/> starts: the AppImage file when running from one (<paramref name="appImage"/>,
    /// from <c>$APPIMAGE</c>), since its mount goes away when this process exits; otherwise this executable.
    /// </summary>
    internal static string RestartExecutable(string processPath, string? appImage) =>
        appImage is { Length: > 0 } && Path.IsPathRooted(appImage) ? appImage : processPath;

    static Window MessageWindow(string title, string body)
    {
        var close = new Button { Content = "Close", IsDefault = true, IsCancel = true, HorizontalAlignment = HorizontalAlignment.Right };
        var window = new Window
        {
            Title = "Orrery",
            Width = 480,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Background = new SolidColorBrush(Color.Parse("#14161B")),
            Content = new StackPanel
            {
                Margin = new Thickness(24),
                Spacing = 12,
                Children =
                {
                    new TextBlock { Text = title, FontSize = 16, FontWeight = FontWeight.SemiBold },
                    new SelectableTextBlock { Text = body, TextWrapping = TextWrapping.Wrap },
                    close,
                },
            },
        };
        close.Click += (_, _) => window.Close();
        return window;
    }
}

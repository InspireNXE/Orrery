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
using Orrery.App.Storage;
using Orrery.Core.Storage;

namespace Orrery.App;

public partial class App : Application
{
    const string RestartedFlag = "--restarted";
    const string MovedFromFlag = "--moved-from";
    static readonly TimeSpan RestartLockWait = TimeSpan.FromSeconds(15);

    DataFolderLock? _dataLock;
    string[] _args = [];
    bool _errorShown;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        // A backstop only: each session should still contain its own failures. Without it an exception
        // escaping an async void handler on the UI thread ends the process, and every open session with it.
        // AppDomain.UnhandledException is left alone, since those can't be recovered.
        Dispatcher.UIThread.UnhandledException += OnUiThreadUnhandledException;
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Trace.TraceError($"Unobserved task exception: {e.Exception}");
            e.SetObserved();
        };

        // In every window, a click outside the text field being typed in leaves it, also on parts of the
        // window that can't take focus themselves; a click on something that can then focuses that.
        InputElement.PointerPressedEvent.AddClassHandler<Window>(LeaveTextFieldOnClickOutside, RoutingStrategies.Tunnel, handledEventsToo: true);

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Exit += (_, _) => _dataLock?.Dispose();
            _args = desktop.Args ?? [];

            var appDirectory = DataLocationResolver.AppDirectory(AppContext.BaseDirectory);
            var folders = PlatformFolders.ForCurrentOs();

            switch (DataLocationResolver.Resolve(appDirectory, folders))
            {
                case StartupStorage.Ready ready:
                    desktop.MainWindow = OpenMainWindow(ready.Location);
                    break;

                case StartupStorage.NeedsPortableChoice:
                    var setup = new PortableSetupWindow(appDirectory, folders);
                    setup.Completed += location =>
                    {
                        // Swap the main window before closing setup, or closing it would end the app.
                        desktop.MainWindow = OpenMainWindow(location);
                        desktop.MainWindow.Show();
                        setup.Close();
                    };
                    desktop.MainWindow = setup;
                    break;
            }
        }

        base.OnFrameworkInitializationCompleted();
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

        window.FocusManager.ClearFocus();
    }

    void OnUiThreadUnhandledException(object? sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Trace.TraceError($"Unhandled exception on the UI thread: {e.Exception}");
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

    Window OpenMainWindow(DataLocation location)
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
            Trace.TraceError($"Can't open data folder {location.DataDir}: {e}");
            return MessageWindow("Orrery can't open its data folder", $"{location.DataDir}\n\n{e.Message}");
        }

        if (_dataLock is null)
        {
            return MessageWindow("Orrery is already running",
                $"Another copy of Orrery is using this data folder:\n{location.DataDir}");
        }

        int moved = Array.IndexOf(_args, MovedFromFlag);
        return new MainWindow(location, movedFrom: moved >= 0 && moved + 1 < _args.Length ? _args[moved + 1] : null);
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

        var start = new ProcessStartInfo(exe);
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
            Trace.TraceError($"Couldn't restart Orrery: {e}");
        }

        desktop.Shutdown();
    }

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

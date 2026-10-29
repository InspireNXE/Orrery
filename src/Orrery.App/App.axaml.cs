using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Orrery.App.Storage;
using Orrery.Core.Storage;

namespace Orrery.App;

public partial class App : Application
{
    DataFolderLock? _dataLock;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Exit += (_, _) => _dataLock?.Dispose();

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

    Window OpenMainWindow(DataLocation location)
    {
        try
        {
            _dataLock = DataFolderLock.TryAcquire(location.DataDir);
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

        return new MainWindow(location);
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

using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Orrery.App;
using Orrery.App.Connections;
using Orrery.App.Sessions;
using Orrery.App.Settings;
using Orrery.Core.Connections;
using Orrery.Core.Credentials;
using Orrery.Core.Sessions;
using Orrery.Core.Settings;
using Orrery.Core.Storage;

namespace Orrery.Screenshots;

static class Shots
{
    const int Width = 1280, Height = 800;
    static readonly IBrush Page = new SolidColorBrush(Color.Parse("#0E1014"));
    static readonly IBrush Muted = new SolidColorBrush(Color.Parse("#9AA3B2"));
    static readonly IBrush Line = new SolidColorBrush(Color.Parse("#2A2F38"));

    /// <param name="icon">The app icon (src/Orrery.App/Assets/orrery.png), for the social preview.</param>
    public static async Task TakeAllAsync(string output, string icon)
    {
        var temp = Path.Combine(Path.GetTempPath(), "orrery-screenshots-" + Guid.NewGuid().ToString("N"));
        var location = new DataLocation(StorageMode.Portable, Path.Combine(temp, "data"), Path.Combine(temp, "cache"), Path.Combine(temp, "logs"));
        Directory.CreateDirectory(location.DataDir);
        MainWindow? window = null;
        try
        {
            var demo = DemoData.Write(location);
            window = new MainWindow(location) { Width = Width, Height = Height };
            window.Show();
            await SettleAsync();
            Stage(window);
            await SettleAsync();

            // Three tabs: connected, still connecting, and a login the server turned down.
            var tabs = Private<List<SessionTab>>(window, "_tabs");
            var web = new DemoTerminal(demo.Web01.Host, SessionState.Connected);
            AddTab(window, demo.Web01, web);
            AddTab(window, demo.BuildAgent, new DemoTerminal(demo.BuildAgent.Host, SessionState.Connecting));
            var db = new DemoTerminal(demo.Db02.Host, SessionState.Connecting);
            AddTab(window, demo.Db02, db);
            db.Fail(new SessionFailureException(FailureKind.LoginFailed, "Permission denied (publickey,password)."));
            SelectTab(window, tabs[0]);
            Select(window, demo.Web01.Id);
            await SettleAsync();
            web.Output(DemoShell.Text);
            await SettleAsync(10);

            using var hero = Capture(window);
            Save(hero, output, "main-window.png");

            // The same rows in Clearer colors. A cropped bitmap disposes its source, so these aren't disposed here.
            Palette.Use(InterfaceColorScheme.Clearer);
            await SettleAsync();
            var clearer = Capture(window);
            Palette.Use(InterfaceColorScheme.Standard);
            using (var compared = await RenderAsync(Comparison(new CroppedBitmap(hero, TreeArea), new CroppedBitmap(clearer, TreeArea)),
                2 * TreeArea.Width + 72, TreeArea.Height + 84))
            {
                Save(compared, output, "interface-colors.png");
            }

            await QuickConnectAsync(window, output);

            SelectTab(window, tabs[2]);
            Select(window, demo.Db02.Id);
            await SettleAsync();
            using (var lost = Capture(window))
            {
                Save(lost, output, "connection-lost.png");
            }

            await RecordingAsync(window, web, tabs[0], demo.Web01.Id, output);

            await SettingsAsync(window, location, output);

            using var preview = await RenderAsync(SocialPreview(hero, new Bitmap(icon)), 1280, 640);
            Save(preview, output, "social-preview.png");
        }
        finally
        {
            window?.Close();
            await SettleAsync();
            try
            {
                Directory.Delete(temp, recursive: true);
            }
            catch (IOException)
            {
                Console.Error.WriteLine($"Couldn't delete {temp}");
            }
        }
    }

    /// <summary>The window as it would look on someone else's computer: no real path, and a host check that asks no one.</summary>
    static void Stage(MainWindow window)
    {
        window.FindControl<TextBlock>("StorageText")!.Text = @"D:\Tools\Orrery\data";
        window.FindControl<Control>("DataAccessText")!.IsVisible = false;

        var field = typeof(MainWindow).GetField("_hostStatus", BindingFlags.Instance | BindingFlags.NonPublic)!;
        ((HostStatusController)field.GetValue(window)!).Dispose();
        var treeView = Private<TreeView>(window, "_treeView");
        var hostStatus = new HostStatusController(() => treeView.ItemsSource as IEnumerable<NodeViewModel> ?? [], () => true,
            _ => new DemoProbe(DemoData.DownHosts));
        field.SetValue(window, hostStatus);
        hostStatus.Enabled = true;
    }

    /// <summary>Quick connect drawn over the dimmed window where it opens, with a query typed in.</summary>
    static async Task QuickConnectAsync(MainWindow window, string output)
    {
        var dimmer = Private<Border>(window, "_dimmer");
        dimmer.IsVisible = true;
        var open = Private<List<SessionTab>>(window, "_tabs").Select(t => new OpenSession(t.Origin.Title, t.Address, t.Origin.Protocol, t.Origin.Label)).ToList();
        var quick = new QuickConnectWindow(Private<ConnectionTree>(window, "_tree"), open);
        _ = quick.ShowOver(window);
        await SettleAsync();
        var query = quick.GetVisualDescendants().OfType<TextBox>().First();
        query.Text = "web";
        query.CaretIndex = query.Text.Length;
        await SettleAsync();

        using var under = Capture(window);
        using var over = Capture(quick);
        var canvas = new Canvas();
        canvas.Children.Add(new Image { Source = under, Width = Width, Height = Height });
        var box = new Image { Source = over, Width = over.PixelSize.Width, Height = over.PixelSize.Height };
        Canvas.SetLeft(box, (Width - over.PixelSize.Width) / 2);
        Canvas.SetTop(box, 96); // QuickConnectWindow.TopOffset
        canvas.Children.Add(box);
        using var composed = await RenderAsync(canvas, Width, Height);
        Save(composed, output, "quick-connect.png");

        quick.Close();
        dimmer.IsVisible = false;
        await SettleAsync();
    }

    /// <summary>
    /// web-01 recording, with its red dot and REC pill, then the Recordings section playing that recording back. The
    /// vault is the temp folder's own, made here, so the recordings key is a real one and nothing asks for a password.
    /// </summary>
    static async Task RecordingAsync(MainWindow window, DemoTerminal web, SessionTab tab, Guid id, string output)
    {
        var credentials = Private<ICredentialStore>(window, "_credentials");
        await credentials.UnlockAsync("screenshots");
        SelectTab(window, tab);
        Select(window, id);
        await (Task)Method(window, "StartRecordingAsync").Invoke(window, [tab])!;
        await SettleAsync();
        // Real time apart, so the recording has a timeline to show.
        foreach (var step in DemoShell.Recorded)
        {
            web.Output(step);
            await SettleAsync(3);
            await Task.Delay(1200);
        }

        await SettleAsync(10);
        using (var recording = Capture(window))
        {
            Save(recording, output, "recording.png");
        }

        tab.Recorder!.Stop();
        await SettleAsync(10);

        Invoke(window, "ShowRecordings");
        await SettleAsync(20);
        var panel = Private<Orrery.App.Recording.RecordingsPanel>(window, "_recordingsPanel");
        var item = Private<System.Collections.IList>(panel, "_items")[0]!;
        var key = await Orrery.Core.Recording.RecordingKeys.PrivateKeyAsync(credentials)
            ?? throw new InvalidOperationException("No recordings key was made.");
        panel.Stage.Play(Private<Orrery.App.Recording.RecordingService>(window, "_recordings"), (Orrery.App.Recording.RecordingItem)item, key);
        await SettleAsync(20);
        var player = panel.Stage.Player!;
        player.Pause();
        var duration = Private<TimeSpan>(player, "_duration");
        Method(player, "Seek").Invoke(player, [duration * 0.8]);
        await SettleAsync(10);
        using (var playback = Capture(window))
        {
            Save(playback, output, "recording-playback.png");
        }

        panel.Stage.Close();
        var connections = Private<Dictionary<string, (Button RailButton, Control View)>>(window, "_sides")["connections"].RailButton;
        Invoke(window, "OnRailClick", connections, new Avalonia.Interactivity.RoutedEventArgs());
        await SettleAsync();
    }

    /// <summary>Settings › Appearance with a color vision chosen, as choosing one leaves it.</summary>
    static async Task SettingsAsync(MainWindow window, DataLocation location, string output)
    {
        var store = AppSettingsStore.In(location);
        store.Save(store.Load() with
        {
            ColorVision = ColorVision.Deuteranopia,
            ColorCorrectionStrength = 80,
            InterfaceColors = InterfaceColorScheme.ColorBlind,
            TerminalColors = TerminalColorScheme.ColorBlind,
        });
        Palette.Use(InterfaceColorScheme.ColorBlind);

        // Opened by hand rather than through the window, so nothing asks the system about Windows Hello or Touch ID.
        var host = Private<ContentControl>(window, "_settingsHost");
        host.Content = new SettingsView(location, store, Private<ICredentialStore>(window, "_credentials"),
            () => Task.CompletedTask, () => Task.CompletedTask, () => Task.CompletedTask);
        host.IsVisible = true;
        foreach (var (button, _) in Private<Dictionary<string, (Button RailButton, Control View)>>(window, "_sides").Values)
        {
            button.Classes.Set("active", false);
        }

        Private<Button>(window, "_settingsRailButton").Classes.Set("active", true);
        await SettleAsync();
        using (var settings = Capture(window))
        {
            Save(settings, output, "settings-appearance.png");
        }

        host.IsVisible = false;
        host.Content = null;
        Palette.Use(InterfaceColorScheme.Standard);
    }

    /// <summary>The connection rows, from the first folder to the last connection.</summary>
    static readonly PixelRect TreeArea = new(52, 128, 280, 392);

    static Control Comparison(CroppedBitmap standard, CroppedBitmap clearer)
    {
        Control Column(string label, CroppedBitmap image) => new StackPanel
        {
            Spacing = 10,
            Children =
            {
                new TextBlock { Text = label, FontSize = 13, FontWeight = FontWeight.SemiBold, Foreground = Muted },
                new Border
                {
                    BorderBrush = Line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), ClipToBounds = true,
                    Child = new Image { Source = image, Width = TreeArea.Width, Height = TreeArea.Height },
                },
            },
        };

        return new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 24,
            Margin = new Thickness(24, 20),
            Children = { Column("Standard", standard), Column("Clearer", clearer) },
        };
    }

    /// <summary>1280×640 for GitHub's social preview: the name on the left, the main window running off the right.</summary>
    static Control SocialPreview(Bitmap hero, Bitmap icon)
    {
        var shot = new Border
        {
            BorderBrush = Line,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            ClipToBounds = true,
            Child = new Image { Source = hero, Width = Width * 0.72, Height = Height * 0.72, Stretch = Stretch.Fill },
        };
        RenderOptions.SetBitmapInterpolationMode(shot.Child!, BitmapInterpolationMode.HighQuality);
        Canvas.SetLeft(shot, 500);
        Canvas.SetTop(shot, 96);

        var text = new StackPanel
        {
            Width = 400,
            Spacing = 14,
            Children =
            {
                new Image { Source = icon, Width = 96, Height = 96, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 10) },
                new TextBlock { Text = "Orrery", FontSize = 60, FontWeight = FontWeight.SemiBold, Foreground = Brushes.White },
                new TextBlock
                {
                    Text = "A remote connection manager for Windows, Linux and macOS",
                    FontSize = 24, LineHeight = 32, TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.Parse("#C3C9D3")),
                },
                new TextBlock { Text = "RDP · SSH · VNC · Telnet", FontSize = 18, Foreground = Muted, Margin = new Thickness(0, 6, 0, 0) },
            },
        };
        RenderOptions.SetBitmapInterpolationMode(text.Children[0], BitmapInterpolationMode.HighQuality);
        Canvas.SetLeft(text, 72);
        Canvas.SetTop(text, 150);

        return new Canvas { Children = { shot, text } };
    }

    /// <summary>Opens <paramref name="session"/> in a tab as connecting does, in a terminal the window makes itself.</summary>
    static void AddTab(MainWindow window, ConnectionEntry entry, DemoTerminal session)
    {
        var view = typeof(MainWindow).GetMethod("NewTerminalView", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [session])!;
        Invoke(window, "AddTab", view, session, new SessionOrigin(entry.Name, Protocol.Ssh, entry.Id, () => Task.CompletedTask));
    }

    static void SelectTab(MainWindow window, SessionTab tab) => Invoke(window, "SelectTab", tab, true, true);

    static MethodInfo Method(object owner, string name) =>
        owner.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!;

    static void Select(MainWindow window, Guid id) =>
        typeof(MainWindow).GetMethod("Select", BindingFlags.Instance | BindingFlags.NonPublic, [typeof(Guid)])!.Invoke(window, [id]);

    static void Invoke(object owner, string method, params object[] arguments) => Method(owner, method).Invoke(owner, arguments);

    static T Private<T>(object owner, string field) =>
        (T)owner.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!;

    /// <summary>Lets layout, posted work and the fake host checks finish, and draws a frame.</summary>
    static async Task SettleAsync(int rounds = 5)
    {
        for (int i = 0; i < rounds; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            await Task.Delay(30);
        }
    }

    static WriteableBitmap Capture(TopLevel window) => window.CaptureRenderedFrame() ?? throw new InvalidOperationException("No frame was drawn.");

    /// <summary>Draws <paramref name="content"/> in a window of its own, for pictures put together from others.</summary>
    static async Task<WriteableBitmap> RenderAsync(Control content, int width, int height)
    {
        var window = new Window { Width = width, Height = height, Background = Page, Content = content };
        window.Show();
        await SettleAsync();
        try
        {
            return Capture(window);
        }
        finally
        {
            window.Close();
        }
    }

    static void Save(Bitmap image, string output, string name)
    {
        var path = Path.Combine(output, name);
        image.Save(path, PngBitmapEncoderOptions.Default);
        Console.WriteLine($"{path}: {image.PixelSize.Width}x{image.PixelSize.Height}, {new FileInfo(path).Length / 1024} KB");
    }
}

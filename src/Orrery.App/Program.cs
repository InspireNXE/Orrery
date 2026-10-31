using Avalonia;

namespace Orrery.App;

static class Program
{
    [STAThread]
    public static void Main(string[] args) =>
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            // Matches StartupWMClass in installer/linux/orrery.desktop, so docks group the window with its launcher.
            .With(new X11PlatformOptions { WmClass = "Orrery" })
            .WithInterFont()
            .LogToTrace();
}

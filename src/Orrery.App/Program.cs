using Avalonia;

namespace Orrery.App;

static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // Started through UAC for an administrator shell (Orrery.Protocols.Local.ElevatedHost): no window, no data folder.
        if (OperatingSystem.IsWindows() && args.Length > 0 && args[0] == Orrery.Protocols.Local.ElevatedHost.Flag)
        {
            return Orrery.Protocols.Local.ElevatedHost.Run(args[1..]);
        }

        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            // Matches StartupWMClass in installer/linux/org.inspirenxe.Orrery.desktop, so docks group the window with its launcher.
            .With(new X11PlatformOptions { WmClass = "Orrery" })
            .WithInterFont()
            .LogToTrace();
}

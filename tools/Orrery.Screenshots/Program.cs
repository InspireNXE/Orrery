using Avalonia;
using Avalonia.Headless;
using Avalonia.Threading;

namespace Orrery.Screenshots;

/// <summary>
/// Renders the README's screenshots into docs/images: the real windows, drawn by Skia on Avalonia's headless
/// platform, filled with made-up connections and fake sessions. Nothing connects anywhere, and the data folder is
/// a new temporary one, deleted afterwards.
/// </summary>
static class Program
{
    static int Main(string[] args)
    {
        var root = RepoRoot();

        // --password-prompts <folder>: the windows a command that types a password shows, for review, not the README's.
        bool passwords = args is ["--password-prompts", _];
        var output = passwords ? Path.GetFullPath(args[1]) : args.Length > 0 ? Path.GetFullPath(args[0]) : Path.Combine(root, "docs", "images");
        var icon = Path.Combine(root, "src", "Orrery.App", "Assets", "orrery.png");
        Directory.CreateDirectory(output);

        AppBuilder.Configure<Orrery.App.App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .WithInterFont()
            .SetupWithoutStarting();

        Exception? failure = null;
        using var done = new CancellationTokenSource();
        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                await (passwords ? PasswordShots.TakeAllAsync(output) : Shots.TakeAllAsync(output, icon));
            }
            catch (Exception e)
            {
                failure = e;
            }
            finally
            {
                done.Cancel();
            }
        });
        Dispatcher.UIThread.MainLoop(done.Token);

        if (failure is not null)
        {
            Console.Error.WriteLine(failure);
            return 1;
        }

        return 0;
    }

    /// <summary>The folder holding Orrery.sln, found upwards from the build output.</summary>
    static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Orrery.sln")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("Couldn't find Orrery.sln above the build output.");
    }
}

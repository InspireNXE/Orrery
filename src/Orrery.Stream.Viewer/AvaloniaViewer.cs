using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Orrery.Protocols.Stream.Windows;

namespace Orrery.Stream.Viewer;

/// <summary>
/// <c>--avalonia</c>: the same stream presented into a native child window inside an Avalonia window, through
/// <see cref="NativeControlHost"/> (option 1 in docs/design-stream.md §5), to measure what Avalonia costs against the
/// bare window.
/// </summary>
[SupportedOSPlatform("windows")]
static class AvaloniaViewer
{
    public static void Run(StreamSession session, int? seconds)
    {
        AppBuilder.Configure(() => new ViewerApp(session, seconds))
            .UsePlatformDetect()
            .StartWithClassicDesktopLifetime([]);
    }

    sealed class ViewerApp(StreamSession session, int? seconds) : Application
    {
        public override void Initialize() => Styles.Add(new FluentTheme());

        public override void OnFrameworkInitializationCompleted()
        {
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                var window = new Window
                {
                    Title = "Orrery Stream Viewer (Avalonia)",
                    Width = 1280,
                    Height = 720,
                };
                window.Content = new StreamHost(session, title => Dispatcher.UIThread.Post(() => window.Title = title + " (Avalonia)"));
                desktop.MainWindow = window;
                if (seconds is { } s)
                {
                    DispatcherTimer.RunOnce(window.Close, TimeSpan.FromSeconds(s));
                }
            }

            base.OnFrameworkInitializationCompleted();
        }
    }

    /// <summary>Hosts the stream's own child window, and starts the stream into it once it exists.</summary>
    sealed class StreamHost(StreamSession session, Action<string> setTitle) : NativeControlHost
    {
        StreamWindow? _child;

        protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
        {
            _child = StreamWindow.CreateChild(parent.Handle);
            session.Start(_child.Handle, setTitle);
            return new PlatformHandle(_child.Handle, "HWND");
        }

        protected override void DestroyNativeControlCore(IPlatformHandle control)
        {
            // The worker presents into the child, so it stops before the child goes.
            session.StopReceiving();
            _child?.Destroy();
            _child = null;
        }
    }
}

using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Orrery.App.Controls;
using Orrery.App.Sessions;
using Orrery.Core.Recording;

namespace Orrery.App.Recording;

/// <summary>
/// The session toolbar's recording control (docs/design-session-recording.md §5.2). While the tab records, a red
/// "REC 1:23" pill whose panel shows how long and how big the recording is, changes its frame rate and sound for this
/// recording, and stops it; otherwise a round button that starts one.
/// </summary>
static class RecordingControl
{
    internal static readonly int[] FrameRates = [1, 2, 5, 10];

    /// <summary>The control for <paramref name="tab"/>, or null when there's nothing it can record.</summary>
    public static Control? For(SessionTab tab, Action start, Action stop, Action showRecordings)
    {
        if (!tab.IsRecording)
        {
            var record = new Button { Content = StrokeIcon.Create(Icons.Record, 15) };
            record.Classes.Add("icon");
            record.Classes.Add("small");
            ToolTip.SetTip(record, "Start recording this session");
            AutomationProperties.SetName(record, "Start recording");
            record.Click += (_, _) => start();
            return record;
        }

        var recorder = tab.Recorder!;
        var time = new TextBlock { FontFamily = Dialogs.MonoFont, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        var pill = new Button
        {
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                Children =
                {
                    new Ellipse { Width = 8, Height = 8, Fill = SessionTab.RecordingBrush, VerticalAlignment = VerticalAlignment.Center },
                    new TextBlock { Text = "REC", FontSize = 11, FontWeight = FontWeight.SemiBold, LetterSpacing = 0.6, VerticalAlignment = VerticalAlignment.Center },
                    time,
                },
            },
        };
        pill.Classes.Add("rec");
        AutomationProperties.SetName(pill, "Recording");
        ToolTip.SetTip(pill, "Recording this session. Click to change or stop it.");

        var size = new TextBlock { FontSize = 12, Foreground = Dialogs.MutedBrush };
        void Tick()
        {
            time.Text = Format(recorder.Elapsed);
            size.Text = $"{Format(recorder.Elapsed)} so far · {Describe(recorder.BytesWritten)}";
        }

        Tick();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) => Tick();
        pill.AttachedToVisualTree += (_, _) => timer.Start();
        pill.DetachedFromVisualTree += (_, _) => timer.Stop();

        var panel = new StackPanel { Spacing = 12, Width = 280 };
        panel.Children.Add(new StackPanel
        {
            Spacing = 2,
            Children =
            {
                new TextBlock { Text = "Recording", FontSize = 14, FontWeight = FontWeight.SemiBold },
                size,
            },
        });

        if (recorder is ScreenRecorder screen)
        {
            var rates = new Grid { ColumnDefinitions = new ColumnDefinitions(string.Join(",", FrameRates.Select(_ => "*"))), ColumnSpacing = 3 };
            var buttons = new List<Button>();
            foreach (var (rate, i) in FrameRates.Select((r, i) => (r, i)))
            {
                var button = new Button { Content = $"{rate}", Classes = { "segment" } };
                AutomationProperties.SetName(button, $"{rate} frames a second");
                Grid.SetColumn(button, i);
                button.Click += (_, _) =>
                {
                    screen.FramesPerSecond = rate;
                    foreach (var other in buttons)
                    {
                        other.Classes.Set("selected", other == button);
                    }
                };
                button.Classes.Set("selected", screen.FramesPerSecond == rate);
                buttons.Add(button);
                rates.Children.Add(button);
            }

            panel.Children.Add(Labeled("Frames a second", new Border
            {
                Background = new SolidColorBrush(Color.Parse("#0E1116")),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(3),
                Child = rates,
            }));

            if (screen.CanRecordSound)
            {
                var sound = new ToggleSwitch { IsChecked = screen.RecordsSound, OnContent = null, OffContent = null, MinWidth = 0, Margin = new Thickness(0, 0, -11, 0) };
                AutomationProperties.SetName(sound, "Record sound");
                sound.IsCheckedChanged += (_, _) => screen.RecordsSound = sound.IsChecked == true;
                panel.Children.Add(new DockPanel
                {
                    Children =
                    {
                        Pane.Docked(sound, Dock.Right),
                        new TextBlock { Text = "Record sound", FontSize = 13, VerticalAlignment = VerticalAlignment.Center },
                    },
                });
            }

            panel.Children.Add(new TextBlock
            {
                Text = "Changes here are for this recording. Settings > Recordings has the defaults.",
                FontSize = 11,
                Foreground = Dialogs.MutedBrush,
                TextWrapping = TextWrapping.Wrap,
            });
        }

        var flyout = new Flyout { Placement = PlacementMode.BottomEdgeAlignedRight, Content = panel };
        var show = new Button { Content = "Show recordings", HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center };
        show.Click += (_, _) =>
        {
            flyout.Hide();
            showRecordings();
        };
        var stopButton = new Button { Content = "Stop recording", Classes = { "danger" }, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center };
        stopButton.Click += (_, _) =>
        {
            flyout.Hide();
            stop();
        };
        panel.Children.Add(Pane.Pair(show, stopButton));

        flyout.Opened += (_, _) => Tick();
        pill.Flyout = flyout;
        return pill;
    }

    static Control Labeled(string label, Control control) => new StackPanel
    {
        Spacing = 6,
        Children = { new TextBlock { Text = label, FontSize = 12, Foreground = Dialogs.MutedBrush }, control },
    };

    static string Format(TimeSpan time) => time.TotalHours >= 1 ? time.ToString(@"h\:mm\:ss") : time.ToString(@"m\:ss");

    static string Describe(long bytes) => bytes >= 1L << 30 ? $"{bytes / (1024.0 * 1024 * 1024):0.0} GB"
        : bytes >= 1 << 20 ? $"{bytes / (1024.0 * 1024):0.0} MB"
        : $"{Math.Max(1, bytes / 1024)} KB";
}

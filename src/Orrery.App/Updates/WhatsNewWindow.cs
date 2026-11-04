using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Orrery.App.Controls;
using Orrery.Core.Updates;

namespace Orrery.App.Updates;

/// <summary>
/// The release notes of this version and the ones around it, newest first, from the same release list update checks
/// read. This copy's version opens with its notes showing (the newest, for a build that isn't a release); the others
/// open on a click. Asks GitHub only when opened.
/// </summary>
sealed class WhatsNewWindow : Window
{
    readonly Func<CancellationToken, Task<IReadOnlyList<Release>>> _load;
    readonly ReleaseVersion _current;
    readonly CancellationTokenSource _closing = new();

    public WhatsNewWindow(Func<CancellationToken, Task<IReadOnlyList<Release>>> load, ReleaseVersion current, bool testFeed)
    {
        _load = load;
        _current = current;
        Title = "What's new in Orrery";
        Width = 640;
        Height = 620;
        MinWidth = 420;
        MinHeight = 320;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Dialogs.PanelBrush;

        Status = new TextBlock { Text = "Getting the release notes from GitHub…", Foreground = Dialogs.MutedBrush, TextWrapping = TextWrapping.Wrap };
        Releases = new StackPanel { Spacing = 8 };
        var all = new Button { Content = "All releases on GitHub", Classes = { "link" }, Padding = new Thickness(0, 3) };
        ToolTip.SetTip(all, ReleaseFeed.ReleasesPage.AbsoluteUri);
        all.Click += (_, _) => UpdateDetailsWindow.OpenPage(ReleaseFeed.ReleasesPage);
        var close = new Button { Content = "Close", IsCancel = true, IsDefault = true };
        close.Click += (_, _) => Close();

        var summary = $"You have {current}" + (testFeed ? " · test feed" : string.Empty);
        Content = new DockPanel
        {
            Margin = new Thickness(24),
            Children =
            {
                Pane.Docked(new StackPanel
                {
                    Spacing = 6,
                    Margin = new Thickness(0, 0, 0, 12),
                    Children =
                    {
                        Dialogs.Heading(new TextBlock { Text = Title, FontSize = 16, FontWeight = FontWeight.SemiBold }),
                        new TextBlock { Text = summary, Foreground = Dialogs.MutedBrush },
                    },
                }, Dock.Top),
                Pane.Docked(new DockPanel
                {
                    Margin = new Thickness(0, 12, 0, 0),
                    Children = { Pane.Docked(close, Dock.Right), all },
                }, Dock.Bottom),
                new ScrollViewer
                {
                    HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
                    Content = new StackPanel { Spacing = 8, Children = { Status, Releases } },
                },
            },
        };

        Opened += async (_, _) => await LoadAsync();
        Closed += (_, _) => _closing.Cancel();
    }

    /// <summary>"Getting the release notes…", then why they couldn't be got; hidden once they're in.</summary>
    internal TextBlock Status { get; }

    /// <summary>One <see cref="Entry"/> per release, newest first.</summary>
    internal StackPanel Releases { get; }

    internal async Task LoadAsync()
    {
        IReadOnlyList<Release> releases;
        try
        {
            releases = await _load(_closing.Token);
        }
        catch (UpdateException e)
        {
            Status.Text = $"Couldn't get the release notes: {e.Message}";
            ScreenReader.Announce(Status, Status.Text, "Orrery.WhatsNew", ScreenReader.MostRecent);
            return;
        }
        catch (OperationCanceledException)
        {
            return;
        }

        var sorted = releases.OrderByDescending(r => r.Version).ToList();
        if (sorted.Count == 0)
        {
            Status.Text = "No releases came back.";
            return;
        }

        var open = sorted.FirstOrDefault(r => r.Version == _current) ?? sorted[0];
        Releases.Children.Clear();
        foreach (var release in sorted)
        {
            Releases.Children.Add(new Entry(release, isCurrent: release.Version == _current, expanded: release == open));
        }

        Status.IsVisible = false;
    }

    /// <summary>A release's heading, a button that shows or hides its notes.</summary>
    internal sealed class Entry : StackPanel
    {
        public Entry(Release release, bool isCurrent, bool expanded)
        {
            Release = release;
            Spacing = 6;
            var facts = new List<string> { release.Version.ToString() };
            if (release.PublishedAt is { } published)
            {
                facts.Add(published.LocalDateTime.ToString("d MMM yyyy", System.Globalization.CultureInfo.CurrentCulture));
            }

            if (release.IsPreview)
            {
                facts.Add("preview");
            }

            if (isCurrent)
            {
                facts.Add("this copy");
            }

            Heading = string.Join(" · ", facts);
            Toggle = new Button
            {
                Content = Heading,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                FontWeight = isCurrent ? FontWeight.SemiBold : FontWeight.Normal,
            };
            Notes = new MarkdownView(release.Notes, page => UpdateDetailsWindow.OpenPage(page), release.Page, empty: "No notes came with this release.");
            AutomationProperties.SetName(Notes, $"Release notes for {release.Version}");
            NotesBox = new Border
            {
                Background = new SolidColorBrush(Color.Parse("#0E1014")),
                BorderBrush = Pane.LineBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(14, 10),
                Child = Notes,
                IsVisible = expanded,
            };
            Toggle.Click += (_, _) => Expanded = !Expanded;
            Expanded = expanded;
            Children.Add(Toggle);
            Children.Add(NotesBox);
        }

        public Release Release { get; }

        /// <summary>"0.1.2 · 3 Nov 2026 · preview · this copy".</summary>
        public string Heading { get; }

        public Button Toggle { get; }

        public MarkdownView Notes { get; }

        Border NotesBox { get; }

        public bool Expanded
        {
            get => NotesBox.IsVisible;
            set
            {
                NotesBox.IsVisible = value;
                AutomationProperties.SetHelpText(Toggle, value ? "Notes showing. Press to hide them." : "Press to show the notes.");
            }
        }
    }
}

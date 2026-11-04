using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Orrery.App.Controls;
using Orrery.Core.Updates;

namespace Orrery.App.Updates;

/// <summary>What was chosen in an update's details.</summary>
enum UpdateChoice
{
    Update,
    Later,
    Skip,
}

/// <summary>Words and numbers the update screens share.</summary>
static class UpdateText
{
    const double Megabyte = 1024 * 1024;

    /// <summary>A size in megabytes with one decimal, as in "45.2 MB".</summary>
    public static string Megabytes(long bytes) => (bytes / Megabyte).ToString("0.0", CultureInfo.CurrentCulture) + " MB";

    /// <summary>
    /// Why this copy can only point at the release page rather than install it: the way it was installed, or a
    /// release without this copy's file.
    /// </summary>
    public static string WhyNotInstalled(CopyKind kind) => kind switch
    {
        CopyKind.LinuxPackage => "Install it with your package manager.",
        CopyKind.MacOS => "Download it from GitHub.",
        CopyKind.Development => "This copy runs from a build, so it doesn't update itself. Download it from GitHub.",
        _ => "This release has no checked file for this copy of Orrery. Download it from GitHub.",
    };
}

/// <summary>
/// A release's details: the version against this one, its notes, and the choice of updating, putting it off or
/// skipping it. A release this copy can't install itself offers its download page instead, and says why.
/// </summary>
sealed class UpdateDetailsWindow : Window
{
    public UpdateDetailsWindow(UpdateOffer offer, ReleaseVersion current, CopyKind kind, bool testFeed)
    {
        var release = offer.Release;
        Title = $"Orrery {release.Version}";
        Width = 560;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Dialogs.PanelBrush;

        var facts = new List<string> { $"You have {current}" };
        if (offer.Asset is { } asset)
        {
            facts.Add(UpdateText.Megabytes(asset.Size));
        }

        if (release.IsPreview)
        {
            facts.Add("preview");
        }

        if (testFeed)
        {
            facts.Add("test feed");
        }

        Summary = new TextBlock { Text = string.Join(" · ", facts), Foreground = Dialogs.MutedBrush, TextWrapping = TextWrapping.Wrap };
        Notes = new MarkdownView(release.Notes, page => OpenPage(page), release.Page, empty: "No notes came with this release.");
        AutomationProperties.SetName(Notes, "Release notes");
        var notesBox = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#0E1014")),
            BorderBrush = Pane.LineBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Child = new ScrollViewer
            {
                MaxHeight = 320,
                HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
                Content = new Border { Padding = new Thickness(14, 10), Child = Notes },
            },
        };

        var fullNotes = new Button { Content = "Full notes", Classes = { "link" }, IsVisible = release.Page is not null, Padding = new Thickness(0, 3) };
        ToolTip.SetTip(fullNotes, release.Page?.AbsoluteUri);
        fullNotes.Click += (_, _) => OpenPage(release.Page);

        Primary = new Button { Content = offer.CanInstall ? "Update" : "Open download page", IsDefault = true };
        Primary.Classes.Add("accent");
        Primary.IsEnabled = offer.CanInstall || release.Page is not null;
        var later = new Button { Content = "Later", IsCancel = true };
        var skip = new Button { Content = "Skip version" };
        ToolTip.SetTip(skip, $"Don't offer {release.Version} again. A later version is still offered.");
        Primary.Click += (_, _) =>
        {
            if (offer.CanInstall)
            {
                Close(UpdateChoice.Update);
                return;
            }

            // The bar stays, so the release can still be skipped once it's installed by other means.
            OpenPage(release.Page);
            Close(null);
        };
        later.Click += (_, _) => Close(UpdateChoice.Later);
        skip.Click += (_, _) => Close(UpdateChoice.Skip);

        Why = new TextBlock { Text = UpdateText.WhyNotInstalled(kind), TextWrapping = TextWrapping.Wrap, IsVisible = !offer.CanInstall };
        var panel = new StackPanel
        {
            Margin = new Thickness(24),
            Spacing = 12,
            Children =
            {
                Dialogs.Heading(new TextBlock { Text = Title, FontSize = 16, FontWeight = FontWeight.SemiBold }),
                Summary,
                notesBox,
                fullNotes,
                Why,
                new DockPanel
                {
                    Children =
                    {
                        Pane.Docked(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { later, Primary } }, Dock.Right),
                        skip,
                    },
                },
            },
        };
        Content = panel;
        AutomationProperties.SetHelpText(this, Summary.Text);
        Opened += (_, _) => Primary.Focus();
    }

    /// <summary>"You have 0.1.0 · 45.2 MB", with "preview" for a preview.</summary>
    internal TextBlock Summary { get; }

    internal MarkdownView Notes { get; }

    /// <summary>Why the release can't be installed from here; hidden when it can.</summary>
    internal TextBlock Why { get; }

    /// <summary>Update, or Open download page when it can't be installed from here.</summary>
    internal Button Primary { get; }

    static void OpenPage(Uri? page)
    {
        if (page is not null && MainWindow.OpenInBrowser(page) is { } problem)
        {
            Microsoft.Extensions.Logging.LoggerExtensions.LogWarning(Orrery.Core.Diagnostics.Log.For("Updates"), "Couldn't open {Page}: {Problem}", page, problem);
        }
    }
}

/// <summary>
/// Downloads an update and checks it, showing how far it's got. Cancel stops it; a failure is shown with Close, and
/// nothing is replaced either way.
/// </summary>
sealed class UpdateProgressWindow : Window
{
    readonly CancellationTokenSource _cancel = new();
    readonly long _total;
    readonly TextBlock _heading;
    readonly Button _close = new() { Content = "Cancel", IsCancel = true, HorizontalAlignment = HorizontalAlignment.Right };
    bool _running;

    public UpdateProgressWindow(UpdateOffer offer)
    {
        _total = Math.Max(offer.Asset?.Size ?? 0, 1);
        Title = "Updating Orrery";
        Width = 420;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Dialogs.PanelBrush;

        _heading = Dialogs.Heading(new TextBlock { Text = $"Downloading Orrery {offer.Release.Version}", FontSize = 16, FontWeight = FontWeight.SemiBold });
        Bar = new ProgressBar { Minimum = 0, Maximum = _total, Value = 0 };
        AutomationProperties.SetName(Bar, "Download");
        Amount = new TextBlock { Text = $"0.0 MB of {UpdateText.Megabytes(_total)}", FontSize = 12, Foreground = Dialogs.MutedBrush };
        Downloaded = new Step("Downloaded");
        Checked = new Step("Checksum matches");
        Error = new Notice(NoticeKind.Error) { IsVisible = false };

        _close.Click += (_, _) =>
        {
            if (_running)
            {
                _cancel.Cancel();
                _heading.Text = "Canceling…";
                _close.IsEnabled = false;
            }
            else
            {
                Close();
            }
        };

        Content = new StackPanel
        {
            Margin = new Thickness(24),
            Spacing = 12,
            Children =
            {
                _heading,
                Bar,
                Amount,
                new StackPanel { Spacing = 6, Children = { Downloaded, Checked } },
                Error,
                _close,
            },
        };

        Closing += (_, e) =>
        {
            // Closing the window cancels; it goes once the download has stopped.
            if (_running)
            {
                e.Cancel = true;
                _cancel.Cancel();
            }
        };
    }

    internal ProgressBar Bar { get; }

    /// <summary>"12.3 MB of 45.2 MB".</summary>
    internal TextBlock Amount { get; }

    internal Step Downloaded { get; }

    internal Step Checked { get; }

    internal Notice Error { get; }

    /// <summary>Cancel while it runs, Close after a failure.</summary>
    internal Button CloseButton => _close;

    /// <summary>
    /// Shows the window over <paramref name="owner"/> and runs <paramref name="download"/>. The checked file, or null
    /// when it was canceled or failed (the failure stays on screen until closed).
    /// </summary>
    public async Task<DownloadedUpdate?> RunAsync(Window owner, Func<IProgress<long>, CancellationToken, Task<DownloadedUpdate>> download, CancellationToken closing)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_cancel.Token, closing);
        _running = true;
        var shown = ShowDialog(owner);
        DownloadedUpdate? result = null;
        try
        {
            result = await download(new UiProgress(Report), linked.Token);
            Report(_total);
            Checked.Done = true;
            _running = false;
            Close();
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            _running = false;
            Close();
        }
        catch (UpdateException e)
        {
            _running = false;
            Fail(e.Message);
        }

        await shown;
        return result;
    }

    void Report(long received)
    {
        // Progress posted from the download can arrive after it has finished or failed.
        if (!_running)
        {
            return;
        }

        Bar.Value = Math.Min(received, _total);
        Amount.Text = $"{UpdateText.Megabytes(Math.Min(received, _total))} of {UpdateText.Megabytes(_total)}";
        if (received >= _total && !Downloaded.Done)
        {
            Downloaded.Done = true;
            _heading.Text = "Checking the download";
        }
    }

    void Fail(string message)
    {
        _heading.Text = Downloaded.Done ? "The download didn't pass its check" : "The update didn't download";
        Error.Text = $"{message} Nothing was replaced.";
        Error.IsVisible = true;
        _close.Content = "Close";
        _close.IsEnabled = true;
        AutomationProperties.SetHelpText(this, Error.Text);
        ScreenReader.Announce(_close, Error.Text, "Orrery.Update", ScreenReader.MostRecent);
        _close.Focus();
    }

    /// <summary>One step of the download: a check mark lights when it's done.</summary>
    internal sealed class Step : StackPanel
    {
        static readonly IBrush DoneBrush = new SolidColorBrush(Color.Parse("#5FD38D"));
        readonly StrokeIcon _mark = new() { Width = 14, Height = 14, StrokeThickness = 2, Data = StreamGeometry.Parse(Icons.Check), VerticalAlignment = VerticalAlignment.Center };
        readonly TextBlock _label;
        bool _done;

        public Step(string label)
        {
            Orientation = Orientation.Horizontal;
            Spacing = 8;
            _label = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center };
            Children.Add(_mark);
            Children.Add(_label);
            Done = false;
        }

        public bool Done
        {
            get => _done;
            set
            {
                _done = value;
                _mark.Foreground = value ? DoneBrush : Dialogs.MutedBrush;
                _mark.Opacity = value ? 1 : 0.35;
                if (value)
                {
                    _label.ClearValue(TextBlock.ForegroundProperty);
                }
                else
                {
                    _label.Foreground = Dialogs.MutedBrush;
                }

                AutomationProperties.SetName(this, value ? $"{_label.Text}: done" : $"{_label.Text}: not yet");
            }
        }
    }

    /// <summary>Progress reported from any thread, shown on the UI thread.</summary>
    sealed class UiProgress(Action<long> report) : IProgress<long>
    {
        public void Report(long value) => Dispatcher.UIThread.Post(() => report(value));
    }
}

using System.Net;
using System.Net.Sockets;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Orrery.App.Sessions;
using Orrery.App.Settings;
using Orrery.App.Updates;
using Orrery.Core.Connections;
using Orrery.Core.Credentials;
using Orrery.Core.Sessions;
using Orrery.Core.Settings;
using Orrery.Core.Storage;
using Orrery.Core.Updates;
using Xunit;

namespace Orrery.App.Tests;

/// <summary>The update bar, details, download, Settings section and schedule, driven by a stand-in engine: no network.</summary>
public class UpdateUiTests : IDisposable
{
    static readonly DateTimeOffset Now = new(2026, 11, 3, 12, 0, 0, TimeSpan.Zero);

    readonly string _dir = Path.Combine(Path.GetTempPath(), "orrery-tests-" + Guid.NewGuid().ToString("N"));
    readonly DataLocation _location;
    readonly AppSettingsStore _settings;

    public UpdateUiTests()
    {
        _location = new DataLocation(StorageMode.Portable, Path.Combine(_dir, "data"), Path.Combine(_dir, "cache"), Path.Combine(_dir, "logs"));
        Directory.CreateDirectory(_location.DataDir);
        _settings = AppSettingsStore.In(_location);
    }

    /// <summary>Stands in for the engine: offers what it's told, and counts what it's asked.</summary>
    sealed class FakeUpdates : IUpdateService
    {
        public UpdateOffer? Offer { get; set; }
        public UpdateException? Failure { get; set; }
        public int Checks { get; private set; }
        public string? LastSkipped { get; private set; } = "unset";
        public TaskCompletionSource<DownloadedUpdate>? Download { get; set; }
        public bool Disposed { get; private set; }

        public ReleaseVersion CurrentVersion { get; } = ReleaseVersion.Parse("0.1.0");
        public CopyKind Kind { get; set; } = CopyKind.WindowsPortable;
        public bool UsesOtherFeed { get; set; }

        public Task<UpdateOffer?> CheckAsync(bool previews, string? skippedVersion, CancellationToken cancellationToken)
        {
            Checks++;
            LastSkipped = skippedVersion;
            return Failure is { } failure ? Task.FromException<UpdateOffer?>(failure) : Task.FromResult(Offer);
        }

        public IReadOnlyList<Release> Releases { get; set; } = [];

        public Task<IReadOnlyList<Release>> GetReleasesAsync(CancellationToken cancellationToken) =>
            Failure is { } failure ? Task.FromException<IReadOnlyList<Release>>(failure) : Task.FromResult(Releases);

        public Task<DownloadedUpdate> DownloadAsync(UpdateOffer offer, IProgress<long>? progress, CancellationToken cancellationToken) =>
            Download?.Task ?? Task.FromResult(Downloaded(offer));

        public UpdateCommand Apply(DownloadedUpdate update) => new("Orrery.exe", [UpdateCommand.RestartedFlag], RunsSetup: false);

        public void RemoveLeftovers()
        {
        }

        public void Dispose() => Disposed = true;
    }

    sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    static UpdateOffer OfferOf(string version, bool installable = true, string notes = "Fixes.", bool prerelease = false)
    {
        var asset = new ReleaseAsset($"Orrery-{version}-win-x64-portable.zip", 45L * 1024 * 1024, new Uri("https://example.test/orrery.zip"), null);
        var release = new Release(ReleaseVersion.Parse(version), $"v{version}", $"Orrery {version}", notes,
            new Uri($"https://github.com/InspireNXE/Orrery/releases/tag/v{version}"), prerelease, Now, [asset]);
        return new UpdateOffer(release, installable ? asset : null);
    }

    static DownloadedUpdate Downloaded(UpdateOffer offer) => new(offer.Release, offer.Asset!, Path.Combine(Path.GetTempPath(), offer.Asset!.Name), new string('a', 64));

    UpdateController Controller(FakeUpdates fake, TimeProvider? time = null) => new(fake, _settings, time ?? new FixedTime(Now));

    static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    // ---- The bar ----

    [AvaloniaFact]
    public async Task The_bar_shows_for_an_offer_and_not_when_up_to_date()
    {
        var fake = new FakeUpdates();
        using var updates = Controller(fake);
        var bar = new UpdateBar(updates);

        await updates.CheckIfDueAsync();
        Assert.False(bar.IsVisible);

        _settings.Save(_settings.Load() with { LastUpdateCheck = null });
        fake.Offer = OfferOf("0.2.0");
        await updates.CheckIfDueAsync();
        Assert.True(bar.IsVisible);
        Assert.Equal("Orrery 0.2.0 is out", bar.Text.Text);
        Assert.Equal("See what's new", bar.Details.Content);
        Assert.Equal("Later", bar.LaterButton.Content);
        Assert.Equal("Skip this version", ToolTip.GetTip(bar.SkipButton));
    }

    [AvaloniaFact]
    public async Task The_bar_sits_above_the_session_tabs_in_the_main_window()
    {
        VaultAutoLockTests.AddMainWindowResources();
        var fake = new FakeUpdates { Offer = OfferOf("0.2.0") };
        var window = new MainWindow(_location, updates: fake);
        try
        {
            window.Show();
            var host = window.FindControl<ContentControl>("UpdateBarHost")!;
            var bar = Assert.IsType<UpdateBar>(host.Content);
            Assert.False(bar.IsVisible);

            await VaultAutoLockTests.Private<UpdateController>(window, "_updates").CheckIfDueAsync();
            Dispatcher.UIThread.RunJobs();

            Assert.True(bar.IsEffectivelyVisible);
            var column = (DockPanel)host.Parent!;
            Assert.True(column.Children.IndexOf(host) < column.Children.IndexOf(window.FindControl<Control>("TabStripBar")!));
        }
        finally
        {
            window.Close();
        }

        Assert.True(fake.Disposed);
    }

    [AvaloniaFact]
    public async Task Later_hides_the_bar_until_a_newer_release_comes()
    {
        var fake = new FakeUpdates { Offer = OfferOf("0.2.0") };
        using var updates = Controller(fake);
        var bar = new UpdateBar(updates);
        await updates.CheckNowAsync();

        Click(bar.LaterButton);

        Assert.False(bar.IsVisible);
        Assert.Null(_settings.Load().SkippedUpdateVersion);

        fake.Offer = OfferOf("0.2.1");
        await updates.CheckNowAsync();
        Assert.True(bar.IsVisible);
        Assert.Equal("Orrery 0.2.1 is out", bar.Text.Text);
    }

    [AvaloniaFact]
    public async Task The_cross_skips_the_version_and_a_check_by_hand_still_offers_it()
    {
        var fake = new FakeUpdates { Offer = OfferOf("0.2.0") };
        using var updates = Controller(fake);
        var bar = new UpdateBar(updates);
        await updates.CheckNowAsync();

        Click(bar.SkipButton);

        Assert.False(bar.IsVisible);
        Assert.Equal("0.2.0", _settings.Load().SkippedUpdateVersion);

        await updates.CheckIfDueAsync();
        Assert.Equal("0.2.0", fake.LastSkipped);

        await updates.CheckNowAsync();
        Assert.Null(fake.LastSkipped);
        Assert.True(bar.IsVisible);
    }

    [AvaloniaFact]
    public async Task A_test_feed_says_so()
    {
        var fake = new FakeUpdates { Offer = OfferOf("0.2.0"), UsesOtherFeed = true };
        using var updates = Controller(fake);
        var bar = new UpdateBar(updates);

        await updates.CheckNowAsync();

        Assert.Equal("Orrery 0.2.0 is out (test feed)", bar.Text.Text);
        Assert.Contains("test feed", new UpdateDetailsWindow(fake.Offer, fake.CurrentVersion, fake.Kind, testFeed: true).Summary.Text);
    }

    [AvaloniaFact]
    public async Task With_Download_chosen_an_automatic_check_downloads_quietly_and_the_bar_says_ready()
    {
        _settings.Save(new AppSettings { WhenUpdateFound = UpdateAction.Download });
        var download = new TaskCompletionSource<DownloadedUpdate>();
        var fake = new FakeUpdates { Offer = OfferOf("0.2.0"), Download = download };
        using var updates = Controller(fake);
        var bar = new UpdateBar(updates);

        await updates.CheckIfDueAsync();
        Assert.Equal("Orrery 0.2.0 is out", bar.Text.Text);

        download.SetResult(Downloaded(fake.Offer));
        for (int i = 0; i < 20 && updates.Ready is null; i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(10);
        }

        Assert.NotNull(updates.Ready);
        Assert.Equal("Orrery 0.2.0 is ready", bar.Text.Text);
    }

    [AvaloniaFact]
    public void The_palette_offers_Check_for_updates_only_with_an_update_engine()
    {
        VaultAutoLockTests.AddMainWindowResources();
        static List<string> Titles(MainWindow window) =>
            ((List<Orrery.App.Connections.PaletteCommand>)typeof(MainWindow).GetMethod("PaletteCommands", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(window, [])!).Select(c => c.Title).ToList();

        var with = new MainWindow(_location, updates: new FakeUpdates());
        var check = ((List<Orrery.App.Connections.PaletteCommand>)typeof(MainWindow).GetMethod("PaletteCommands", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(with, [])!).Single(c => c.Title == "Check for updates");
        Assert.True(check.Score("update") > 0);
        with.Close();

        var without = new MainWindow(_location);
        Assert.DoesNotContain("Check for updates", Titles(without));
        without.Close();
    }

    // ---- Details ----

    [AvaloniaFact]
    public void Details_show_the_notes_and_Update_when_this_copy_can_install_it()
    {
        var offer = OfferOf("0.2.0", notes: "## Fixes\r\n- **Faster** start-up, see [#12](https://github.com/x/y/pull/12)\r\n* `Ctrl+K` finds commands");
        var window = new UpdateDetailsWindow(offer, ReleaseVersion.Parse("0.1.0"), CopyKind.WindowsPortable, testFeed: false);
        window.Show();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("Orrery 0.2.0", window.Title);
        Assert.Equal("You have 0.1.0 · 45.0 MB", window.Summary.Text);
        Assert.Equal("Fixes\n• Faster start-up, see #12\n• Ctrl+K finds commands", window.Notes.Text);
        Assert.Equal("Update", window.Primary.Content);
        Assert.False(window.Why.IsVisible);
        var buttons = window.GetVisualDescendants().OfType<Button>().Select(b => b.Content as string).ToList();
        Assert.Contains("Skip version", buttons);
        Assert.Contains("Later", buttons);
        Assert.Contains("Full notes", buttons);
        window.Close();
    }

    [AvaloniaTheory]
    [InlineData(CopyKind.LinuxPackage, "Install it with your package manager.")]
    [InlineData(CopyKind.MacOS, "Download it from GitHub.")]
    public void Details_offer_the_download_page_and_say_why_when_this_copy_cannot_install_it(CopyKind kind, string why)
    {
        var offer = OfferOf("0.3.0-preview.1", installable: false, notes: "");
        var window = new UpdateDetailsWindow(offer, ReleaseVersion.Parse("0.1.0"), kind, testFeed: false);
        window.Show();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("Open download page", window.Primary.Content);
        Assert.True(window.Why.IsVisible);
        Assert.Equal(why, window.Why.Text);
        Assert.Equal("You have 0.1.0 · preview", window.Summary.Text);
        Assert.Equal("No notes came with this release.", window.Notes.Text);
        window.Close();
    }

    // ---- Download ----

    [AvaloniaFact]
    public async Task The_download_window_counts_megabytes_ticks_its_steps_and_returns_the_checked_file()
    {
        var offer = OfferOf("0.2.0");
        var owner = new Window();
        owner.Show();
        var window = new UpdateProgressWindow(offer);
        var finish = new TaskCompletionSource<DownloadedUpdate>();
        IProgress<long>? reporter = null;

        var running = window.RunAsync(owner, (progress, _) =>
        {
            reporter = progress;
            return finish.Task;
        }, CancellationToken.None);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("0.0 MB of 45.0 MB", window.Amount.Text);
        Assert.False(window.Downloaded.Done);

        reporter!.Report(45L * 1024 * 1024);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("45.0 MB of 45.0 MB", window.Amount.Text);
        Assert.True(window.Downloaded.Done);
        Assert.False(window.Checked.Done);

        finish.SetResult(Downloaded(offer));
        var result = await running;

        Assert.NotNull(result);
        Assert.True(window.Checked.Done);
        Assert.False(window.IsVisible);
        owner.Close();
    }

    [AvaloniaFact]
    public async Task A_failed_download_shows_why_with_Close_and_returns_nothing()
    {
        var owner = new Window();
        owner.Show();
        var window = new UpdateProgressWindow(OfferOf("0.2.0"));

        var running = window.RunAsync(owner, (_, _) => Task.FromException<DownloadedUpdate>(new UpdateException("Orrery-0.2.0.zip doesn't match its checksum, so it was deleted.")),
            CancellationToken.None);
        Dispatcher.UIThread.RunJobs();

        Assert.True(window.Error.IsVisible);
        Assert.StartsWith("Orrery-0.2.0.zip doesn't match its checksum", window.Error.Text);
        Assert.Equal("Close", window.CloseButton.Content);
        Assert.False(window.Checked.Done);

        Click(window.CloseButton);
        Assert.Null(await running);
        owner.Close();
    }

    [AvaloniaFact]
    public async Task Cancel_stops_the_download()
    {
        var owner = new Window();
        owner.Show();
        var window = new UpdateProgressWindow(OfferOf("0.2.0"));

        var running = window.RunAsync(owner, async (_, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            throw new InvalidOperationException("unreachable");
        }, CancellationToken.None);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("Cancel", window.CloseButton.Content);

        Click(window.CloseButton);
        for (int i = 0; i < 50 && !running.IsCompleted; i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(10);
        }

        Assert.Null(await running);
        owner.Close();
    }

    // ---- Schedule ----

    [AvaloniaFact]
    public async Task An_automatic_check_runs_only_when_due_and_notes_when_it_ran()
    {
        var time = new FixedTime(Now);
        var fake = new FakeUpdates();
        using var updates = Controller(fake, time);

        _settings.Save(new AppSettings { LastUpdateCheck = Now - TimeSpan.FromMinutes(20) });
        await updates.CheckIfDueAsync();
        Assert.Equal(0, fake.Checks);
        Assert.Equal(TimeSpan.FromMinutes(40), updates.NextLook);

        time.Now = Now + TimeSpan.FromMinutes(40);
        await updates.CheckIfDueAsync();
        Assert.Equal(1, fake.Checks);
        Assert.Equal(time.Now, _settings.Load().LastUpdateCheck);
        Assert.Equal(TimeSpan.FromHours(1), updates.NextLook);

        _settings.Save(_settings.Load() with { LastUpdateCheck = Now - TimeSpan.FromHours(3.5), UpdateCheckMinutes = 240 });
        time.Now = Now;
        await updates.CheckIfDueAsync();
        Assert.Equal(1, fake.Checks);
        Assert.Equal(TimeSpan.FromMinutes(30), updates.NextLook);
    }

    [AvaloniaFact]
    public async Task A_failed_automatic_check_is_not_noted_so_it_is_tried_again_later()
    {
        var fake = new FakeUpdates { Failure = new UpdateException("No network.") };
        using var updates = Controller(fake);
        var bar = new UpdateBar(updates);

        await updates.CheckIfDueAsync();

        Assert.Equal(1, fake.Checks);
        Assert.Null(_settings.Load().LastUpdateCheck);
        Assert.Equal(UpdateController.RetryDelay, updates.NextLook);
        Assert.False(bar.IsVisible);
    }

    [AvaloniaFact]
    public async Task With_checks_off_nothing_is_checked_or_scheduled()
    {
        _settings.Save(new AppSettings { UpdateChecks = false });
        var fake = new FakeUpdates { Offer = OfferOf("0.2.0") };
        using var updates = Controller(fake);

        updates.Start();
        await updates.CheckIfDueAsync();

        Assert.Equal(0, fake.Checks);
        Assert.Null(updates.NextLook);
    }

    [AvaloniaFact]
    public async Task Orrery_checks_as_it_starts_however_recent_the_last_check_was_then_on_the_schedule()
    {
        _settings.Save(new AppSettings { LastUpdateCheck = Now - TimeSpan.FromMinutes(5), UpdateCheckMinutes = 30 });
        var fake = new FakeUpdates();
        using var updates = Controller(fake);

        updates.Start();
        Assert.Equal(TimeSpan.Zero, updates.NextLook);

        await updates.CheckIfDueAsync();
        Assert.Equal(1, fake.Checks);
        Assert.Equal(TimeSpan.FromMinutes(30), updates.NextLook);

        await updates.CheckIfDueAsync();
        Assert.Equal(1, fake.Checks); // Only the first was the start's.
    }

    [AvaloniaFact]
    public async Task A_failed_check_is_tried_again_no_later_than_the_schedule_says()
    {
        _settings.Save(new AppSettings { UpdateCheckMinutes = 30 });
        var fake = new FakeUpdates { Failure = new UpdateException("No network.") };
        using var updates = Controller(fake);

        await updates.CheckIfDueAsync();

        Assert.Equal(TimeSpan.FromMinutes(30), updates.NextLook);
    }

    // ---- What's new ----

    static Release ReleaseOf(string version, string notes, bool prerelease = false) =>
        OfferOf(version, notes: notes, prerelease: prerelease).Release;

    [AvaloniaFact]
    public async Task Whats_new_lists_releases_newest_first_with_this_copy_s_notes_open()
    {
        var fake = new FakeUpdates { Releases = [ReleaseOf("0.0.9", "Older."), ReleaseOf("0.2.0-preview.1", "Next.", prerelease: true), ReleaseOf("0.1.0", "**Ours.**")] };
        var window = new WhatsNewWindow(fake.GetReleasesAsync, fake.CurrentVersion, testFeed: false);

        await window.LoadAsync();

        var entries = window.Releases.Children.OfType<WhatsNewWindow.Entry>().ToList();
        Assert.Equal(["0.2.0-preview.1", "0.1.0", "0.0.9"], entries.Select(e => e.Release.Version.ToString()));
        Assert.Contains("preview", entries[0].Heading);
        Assert.EndsWith("this copy", entries[1].Heading);
        Assert.Equal([false, true, false], entries.Select(e => e.Expanded));
        Assert.False(window.Status.IsVisible);

        Click(entries[0].Toggle);
        Assert.True(entries[0].Expanded);
    }

    [AvaloniaFact]
    public async Task Whats_new_says_why_when_the_notes_can_t_be_got()
    {
        var fake = new FakeUpdates { Failure = new UpdateException("GitHub didn't answer.") };
        var window = new WhatsNewWindow(fake.GetReleasesAsync, fake.CurrentVersion, testFeed: false);

        await window.LoadAsync();

        Assert.Equal("Couldn't get the release notes: GitHub didn't answer.", window.Status.Text);
        Assert.Empty(window.Releases.Children);
    }

    // ---- Settings ----

    [AvaloniaFact]
    public async Task The_Updates_section_reads_and_writes_the_settings_and_checks_by_hand()
    {
        _settings.Save(new AppSettings { UpdatePreviews = true, WhenUpdateFound = UpdateAction.Download });
        var fake = new FakeUpdates();
        using var updates = Controller(fake);
        using var vault = new VaultFileStore(_location.FileIn("vault.frv"), autoLockAfter: CredentialStores.DefaultAutoLock);
        var view = new SettingsView(_location, _settings, vault, () => Task.CompletedTask, () => Task.CompletedTask, () => Task.CompletedTask, updates: updates);
        var window = new Window { Content = view };
        window.Show();
        view.ShowPage("About");
        Dispatcher.UIThread.RunJobs();

        var section = window.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Classes.Contains("section") && t.Text == "UPDATES");
        Assert.True(section.IsEffectivelyVisible);
        var texts = window.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text).ToList();
        Assert.Contains("Orrery 0.1.0", texts);
        Assert.Contains("Checks GitHub as Orrery starts, then on that schedule while it's open. Sends only your version.", texts);
        Assert.Contains(window.GetVisualDescendants().OfType<Button>(), b => Equals(b.Content, "What's new"));
        var every = window.GetVisualDescendants().OfType<ComboBox>().Single(c => Avalonia.Automation.AutomationProperties.GetName(c) == "Then every");
        Assert.Equal("1 hour", (every.SelectedItem as ComboBoxItem)?.Content);
        every.SelectedItem = every.Items.OfType<ComboBoxItem>().Single(i => Equals(i.Content, "4 hours"));
        Assert.Equal(240, _settings.Load().UpdateCheckMinutes);

        CheckBox Box(string content) => window.GetVisualDescendants().OfType<CheckBox>().Single(c => Equals(c.Content, content));
        RadioButton Radio(string content) => window.GetVisualDescendants().OfType<RadioButton>().Single(r => r.GroupName == "WhenUpdateFound" && Equals(r.Content, content));

        Assert.True(Box("Check automatically").IsChecked);
        Assert.True(Box("Previews").IsChecked);
        Assert.True(Radio("Download it").IsChecked);

        Box("Check automatically").IsChecked = false;
        Assert.False(every.IsEnabled);
        Box("Previews").IsChecked = false;
        Radio("Ask me").IsChecked = true;
        var saved = _settings.Load();
        Assert.False(saved.UpdateChecks);
        Assert.False(saved.UpdatePreviews);
        Assert.Equal(UpdateAction.Ask, saved.WhenUpdateFound);

        // What the update checks write meanwhile isn't undone by the next change here.
        _settings.Save(_settings.Load() with { SkippedUpdateVersion = "0.2.0", LastUpdateCheck = Now });
        Box("Check automatically").IsChecked = true;
        Assert.Equal("0.2.0", _settings.Load().SkippedUpdateVersion);
        Assert.Equal(Now, _settings.Load().LastUpdateCheck);

        var check = window.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "Check now"));
        Click(check);
        await Until(() => view.UpdateCheckResult!.Text != "Checking…");
        Assert.Equal("You have the latest version (0.1.0).", view.UpdateCheckResult!.Text);
        Assert.Null(fake.LastSkipped);

        fake.Failure = new UpdateException("Couldn't reach GitHub: no network.");
        Click(check);
        await Until(() => view.UpdateCheckResult!.Text != "Checking…");
        Assert.Equal("Couldn't reach GitHub: no network.", view.UpdateCheckResult!.Text);
        window.Close();
    }

    // ---- Restart ----

    [Fact]
    public void Setup_starts_through_the_shell_without_asking_for_elevation_and_a_portable_copy_starts_directly()
    {
        var setup = UpdateRunner.StartInfo(new UpdateCommand(@"C:\cache\updates\0.2.0\Orrery-Setup-0.2.0.exe", UpdateCommand.SetupArguments, RunsSetup: true));
        Assert.True(setup.UseShellExecute);
        Assert.Equal(string.Empty, setup.Verb);
        Assert.Equal("/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS /RELAUNCH=1", setup.Arguments);

        var portable = UpdateRunner.StartInfo(new UpdateCommand(@"D:\Tools\Orrery\Orrery.exe", [UpdateCommand.RestartedFlag], RunsSetup: false));
        Assert.False(portable.UseShellExecute);
        Assert.Equal(@"D:\Tools\Orrery\Orrery.exe", portable.FileName);
        Assert.Equal(["--restarted"], portable.ArgumentList);
    }

    [AvaloniaFact]
    public void Before_a_restart_for_an_update_the_open_tabs_are_marked_to_reopen()
    {
        VaultAutoLockTests.AddMainWindowResources();
        var window = new MainWindow(_location, updates: new FakeUpdates());
        try
        {
            window.Show();
            typeof(MainWindow).GetMethod("AddTab", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window,
                [new Border(), new IdleSession("typed.example"), new SessionOrigin("typed.example", Protocol.Ssh, null, () => Task.CompletedTask)]);
            var host = (IUpdateHost)window;
            Assert.Equal(1, host.OpenSessions);
            Assert.True(host.ReopensSessions);

            host.PrepareToRestart(true);
            Assert.True(new WorkspaceStore(ComputerFiles.PathFor(_location, WorkspaceStore.FileName)).Load().ReopenAtStart);

            host.PrepareToRestart(false);
            Assert.Null(new WorkspaceStore(ComputerFiles.PathFor(_location, WorkspaceStore.FileName)).Load().ReopenAtStart);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task After_an_update_the_tabs_reopen_without_asking_once()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        _ = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    (await listener.AcceptTcpClientAsync()).Dispose();
                }
            }
            catch (Exception e) when (e is ObjectDisposedException or SocketException or InvalidOperationException)
            {
            }
        });

        var tree = ConnectionTree.Empty();
        tree.Add(new ConnectionEntry { Name = "box", Host = "box.example" }, tree.Root);
        ConnectionStore.In(_location).Save(tree);
        var file = ComputerFiles.PathFor(_location, WorkspaceStore.FileName);
        new WorkspaceStore(file).Save(new Workspace { Tabs = [new WorkspaceTab(null, "127.0.0.1", port, Protocol.Telnet)], ReopenAtStart = true });
        VaultAutoLockTests.AddMainWindowResources();
        var window = new MainWindow(_location);
        try
        {
            window.Show();
            var tabs = VaultAutoLockTests.Private<List<SessionTab>>(window, "_tabs");
            await Until(() => tabs.Count == 1);

            Assert.False(window.FindControl<Border>("RestoreBar")!.IsVisible);
            Assert.Null(new WorkspaceStore(file).Load().ReopenAtStart);
        }
        finally
        {
            window.Close();
            listener.Stop();
        }
    }

    sealed class IdleSession(string host) : IConnectionSession
    {
        public ConnectionTarget Target { get; } = new(host, 22, "someone", null);
        public SessionState State => SessionState.Connected;
        public event EventHandler<SessionStateChangedEventArgs>? StateChanged { add { } remove { } }
        public Task DisconnectAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    static async Task Until(Func<bool> condition)
    {
        for (int i = 0; i < 200 && !condition(); i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(25);
        }

        Assert.True(condition(), "timed out waiting");
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        }
    }
}

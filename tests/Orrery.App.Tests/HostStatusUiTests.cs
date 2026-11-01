using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.Logging;
using Orrery.App.Connections;
using Orrery.App.Logging;
using Orrery.App.Settings;
using Orrery.Core.Connections;
using Orrery.Core.Credentials;
using Orrery.Core.Diagnostics;
using Orrery.Core.Network;
using Orrery.Core.Settings;
using Orrery.Core.Storage;
using Xunit;

namespace Orrery.App.Tests;

[Collection(nameof(LoggingTests))]
public class HostStatusUiTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "orrery-tests-" + Guid.NewGuid().ToString("N"));

    sealed class RecordingProbe : IHostProbe
    {
        public List<string> Calls { get; } = [];

        public Task<bool> IsUpAsync(string host, int port, CancellationToken cancellationToken)
        {
            lock (Calls) Calls.Add($"{host}:{port}");
            return Task.FromResult(host.StartsWith("up"));
        }
    }

    sealed class Fixture
    {
        public ConnectionTree Tree = ConnectionTree.Empty();
        public ConnectionEntry Up = new() { Name = "up", Host = "up.example" };
        public ConnectionEntry Down = new() { Name = "down", Host = "down.example" };
        public ConnectionEntry Hidden = new() { Name = "hidden", Host = "up-hidden.example" };
        public ConnectionEntry Jumped = new() { Name = "jumped", Host = "up-jumped.internal" };
        public ConnectionFolder Closed = new() { Name = "closed", Expanded = false };
        public List<NodeViewModel> Rows = [];

        public Fixture()
        {
            var bastion = new ConnectionEntry { Name = "bastion", Host = "up-bastion.example", Settings = { Protocol = Protocol.Ssh } };
            Jumped.Settings.JumpHostId = bastion.Id;
            Tree.Add(Up, Tree.Root);
            Tree.Add(Down, Tree.Root);
            Tree.Add(bastion, Tree.Root);
            Tree.Add(Jumped, Tree.Root);
            Tree.Add(Closed, Tree.Root);
            Tree.Add(Hidden, Closed);
            Rows = NodeViewModel.Build(Tree.Root, null, () => { });
        }

        public NodeViewModel Row(ConnectionNode node)
        {
            var stack = new Stack<NodeViewModel>(Rows);
            while (stack.TryPop(out var row))
            {
                if (row.Node == node) return row;
                foreach (var child in row.Children) stack.Push(child);
            }

            throw new InvalidOperationException();
        }
    }

    static async Task WaitFor(Func<bool> condition)
    {
        for (int i = 0; i < 200 && !condition(); i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(25);
        }

        Dispatcher.UIThread.RunJobs();
        Assert.True(condition());
    }

    [AvaloniaFact]
    public async Task Visible_connections_get_up_and_down_dots_and_collapsed_ones_are_not_probed()
    {
        var f = new Fixture();
        var probe = new RecordingProbe();
        using var controller = new HostStatusController(() => f.Rows, () => true, probe) { Enabled = true };

        await WaitFor(() => f.Row(f.Down).Reachability == HostState.Down);

        Assert.Equal(HostState.Up, f.Row(f.Up).Reachability);
        Assert.StartsWith("Host up", f.Row(f.Up).ReachabilityTip);
        Assert.True(f.Row(f.Up).ReachabilityUp);
        Assert.True(f.Row(f.Down).ReachabilityDown);
        Assert.StartsWith("Host down", f.Row(f.Down).ReachabilityTip);
        Assert.Contains("Checked", f.Row(f.Down).ReachabilityTip);

        // Not asked about: a collapsed folder's connection and one behind a jump host.
        Assert.DoesNotContain("up-hidden.example:3389", probe.Calls);
        Assert.DoesNotContain("up-jumped.internal:3389", probe.Calls);
        Assert.Equal(HostState.Unknown, f.Row(f.Hidden).Reachability);
        Assert.True(f.Row(f.Hidden).ReachabilityUnknown);

        var jumped = f.Row(f.Jumped);
        Assert.Equal(HostState.Unknown, jumped.Reachability);
        Assert.StartsWith("Host not checked: behind a jump host", jumped.ReachabilityTip);

        // Folders have no dot.
        Assert.False(f.Row(f.Closed).ReachabilityShown);
    }

    [AvaloniaFact]
    public async Task Expanding_a_folder_gets_its_connections_checked()
    {
        var f = new Fixture();
        var probe = new RecordingProbe();
        using var controller = new HostStatusController(() => f.Rows, () => true, probe) { Enabled = true };
        await WaitFor(() => f.Row(f.Up).Reachability == HostState.Up);

        f.Row(f.Closed).IsExpanded = true;
        controller.Refresh();

        await WaitFor(() => f.Row(f.Hidden).Reachability == HostState.Up);
    }

    [AvaloniaFact]
    public async Task Nothing_is_probed_while_the_connections_list_is_not_showing()
    {
        var f = new Fixture();
        var probe = new RecordingProbe();
        using var controller = new HostStatusController(() => f.Rows, () => false, probe) { Enabled = true };
        await Task.Delay(100);
        Dispatcher.UIThread.RunJobs();

        Assert.Empty(probe.Calls);
        Assert.Equal(HostState.Unknown, f.Row(f.Up).Reachability);
    }

    [AvaloniaFact]
    public async Task Turned_off_nothing_is_sent_and_no_dots_show_and_turning_it_on_again_checks_afresh()
    {
        var f = new Fixture();
        var probe = new RecordingProbe();
        using var controller = new HostStatusController(() => f.Rows, () => true, probe);
        Assert.All(f.Rows, r => Assert.False(r.ReachabilityShown));
        controller.Refresh();
        Assert.Empty(probe.Calls);
        Assert.All(f.Rows, r => Assert.False(r.ReachabilityShown));

        controller.Enabled = true;
        await WaitFor(() => f.Row(f.Up).Reachability == HostState.Up);
        int calls = probe.Calls.Count;

        controller.Enabled = false;
        controller.Refresh();
        Assert.All(f.Rows, r => Assert.False(r.ReachabilityShown));
        Assert.Equal(calls, probe.Calls.Count);

        controller.Enabled = true;
        await WaitFor(() => f.Row(f.Up).Reachability == HostState.Up);
        Assert.True(probe.Calls.Count > calls);
    }

    [AvaloniaFact]
    public void The_settings_checkbox_is_on_by_default_and_turning_it_off_is_saved_and_announced()
    {
        var location = new DataLocation(StorageMode.Portable, _root, Path.Combine(_root, "cache"), Path.Combine(_root, "logs"));
        var settings = AppSettingsStore.In(location);
        using var vault = new VaultFileStore(location.FileIn("vault.frv"), autoLockAfter: CredentialStores.DefaultAutoLock);
        var view = new SettingsView(location, settings, vault, () => Task.CompletedTask, () => Task.CompletedTask, () => Task.CompletedTask);
        bool? changed = null;
        view.HostStatusChecksChanged += (_, on) => changed = on;
        var window = new Window { Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var check = window.GetVisualDescendants().OfType<CheckBox>().Single(c => Equals(c.Content, "Show whether hosts are up"));
        Assert.True(check.IsChecked);
        Assert.True(settings.Load().ChecksHostStatus);

        check.IsChecked = false;

        Assert.False(changed);
        Assert.False(settings.Load().ChecksHostStatus);
        window.Close();
    }

    [Fact]
    public async Task Probing_writes_nothing_to_the_log_unless_detailed_logging_is_on()
    {
        var location = new DataLocation(StorageMode.Portable, _root, Path.Combine(_root, "cache"), Path.Combine(_root, "logs"));
        Directory.CreateDirectory(_root);
        var monitor = new HostStatusMonitor(new RecordingProbe());
        var targets = Enumerable.Range(0, 10).Select(i => new ProbeTarget(Guid.NewGuid(), $"up{i}.quiet.example", 22)).ToList();

        AppLog.Start(location);
        try
        {
            await monitor.CheckAsync(targets, TimeSpan.Zero, default);
            AppLog.Detailed = true;
            await monitor.CheckAsync([new ProbeTarget(Guid.NewGuid(), "loud.example", 22)], TimeSpan.Zero, default);
        }
        finally
        {
            AppLog.Detailed = false;
            AppLog.Stop();
        }

        var log = string.Join("\n", Directory.GetFiles(location.LogDir).Select(File.ReadAllText));
        Assert.DoesNotContain("quiet.example", log);
        Assert.Contains("loud.example", log);
    }

    [AvaloniaFact]
    public void A_row_says_its_session_state_and_its_host_state_apart_in_words_and_palette_colours()
    {
        var f = new Fixture();
        var row = f.Row(f.Down);
        row.Status = Orrery.Core.Sessions.SessionState.Failed;
        row.SetReachability(HostState.Down, "Host down: nothing answered on port 3389. Checked 10:00:00.");

        Assert.Equal("Session: Failed", row.SessionTip);
        Assert.Equal("Failed, host down", row.SpokenStatus);

        row.Status = null;
        Assert.Equal("No session open", row.SessionTip);
        Assert.Equal("Host down", row.SpokenStatus);
        row.SetReachability(null, null);
        Assert.Equal(string.Empty, row.SpokenStatus);

        // The host's mark uses the shared palette, so it follows the colour-blind setting too.
        Assert.Same(Palette.StateUp, Orrery.App.Controls.HostGlyph.BrushFor(HostState.Up));
        Assert.Same(Palette.StateDown, Orrery.App.Controls.HostGlyph.BrushFor(HostState.Down));
        Assert.Same(Palette.StateEnded, Orrery.App.Controls.HostGlyph.BrushFor(HostState.Unknown));
        var glyph = new Orrery.App.Controls.HostGlyph { State = HostState.Down };
        Assert.Equal("Host down", Avalonia.Automation.AutomationProperties.GetName(glyph));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }
}

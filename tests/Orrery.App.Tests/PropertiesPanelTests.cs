using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Orrery.App.Connections;
using Orrery.Core.Connections;
using Orrery.Core.Credentials;
using Xunit;

namespace Orrery.App.Tests;

public class PropertiesPanelTests
{
    [AvaloniaFact]
    public async Task Selecting_several_nodes_in_turn_does_not_crash()
    {
        var tree = ConnectionTree.Empty();
        var folder = new ConnectionFolder { Name = "Servers" };
        var first = new ConnectionEntry { Name = "one", Host = "one.example" };
        var second = new ConnectionEntry { Name = "two", Host = "two.example" };
        tree.Add(folder, tree.Root);
        tree.Add(first, folder);
        tree.Add(second, folder);

        var panel = new PropertiesPanel(tree, new EmptyCredentialStore());
        var window = new Window { Content = panel, Width = 400, Height = 600 };
        window.Show();

        foreach (var node in new ConnectionNode?[] { first, second, folder, null, first })
        {
            await panel.ShowAsync(node);
            Dispatcher.UIThread.RunJobs();
        }

        Assert.Same(first, panel.Node);
    }

    [AvaloniaFact]
    public async Task A_slow_earlier_selection_does_not_overwrite_a_newer_one()
    {
        var tree = ConnectionTree.Empty();
        var first = new ConnectionEntry { Name = "one", Host = "one.example", Settings = new InheritableSettings { Username = "alice" } };
        var second = new ConnectionEntry { Name = "two", Host = "two.example", Settings = new InheritableSettings { Username = "bob" } };
        tree.Add(first, tree.Root);
        tree.Add(second, tree.Root);

        var store = new EmptyCredentialStore { Gate = new TaskCompletionSource() };
        var panel = new PropertiesPanel(tree, store);
        var window = new Window { Content = panel, Width = 400, Height = 600 };
        window.Show();

        var slow = panel.ShowAsync(first);
        store.Gate = null;
        await panel.ShowAsync(second);
        var gate = store.FirstGate!;
        gate.SetResult();
        await slow;
        Dispatcher.UIThread.RunJobs();

        var texts = window.GetVisualDescendants().OfType<TextBox>().Select(t => t.Text).ToList();
        Assert.Same(second, panel.Node);
        Assert.Contains("bob", texts);
        Assert.DoesNotContain("alice", texts);
    }

    [AvaloniaFact]
    public async Task Turning_off_the_clipboard_for_a_connection_is_saved()
    {
        var tree = ConnectionTree.Empty();
        var folder = new ConnectionFolder { Name = "Servers", Settings = new InheritableSettings { ShareClipboard = false } };
        var entry = new ConnectionEntry { Name = "box", Host = "box.example" };
        tree.Add(folder, tree.Root);
        tree.Add(entry, folder);

        var panel = new PropertiesPanel(tree, new EmptyCredentialStore());
        var window = new Window { Content = panel, Width = 400, Height = 900 };
        window.Show();
        await panel.ShowAsync(entry);
        Dispatcher.UIThread.RunJobs();

        var clipboard = window.GetVisualDescendants().OfType<CheckBox>().Single(c => Equals(c.Content, "Share the clipboard (RDP)"));
        Assert.False(clipboard.IsChecked); // Inherited from the folder.
        Assert.False(clipboard.IsEnabled);

        var inherit = window.GetVisualDescendants().OfType<CheckBox>()
            .Where(c => Equals(c.Content, "Inherit")).Last();
        inherit.IsChecked = false;
        clipboard.IsChecked = true;
        Assert.True(panel.TrySave());

        Assert.True(entry.Settings.ShareClipboard);
        Assert.True(SettingsResolver.Resolve(entry).ShareClipboard.Value);
    }

    sealed class EmptyCredentialStore : ICredentialStore
    {
        TaskCompletionSource? _gate;

        /// <summary>When set, the next <see cref="ListAsync"/> waits for it.</summary>
        public TaskCompletionSource? Gate
        {
            get => _gate;
            set { _gate = value; FirstGate ??= value; }
        }

        public TaskCompletionSource? FirstGate { get; private set; }

        public bool IsLocked => false;
        public bool Exists => true;
        public Task UnlockAsync(string masterPassword) => Task.CompletedTask;
        public void Lock() { }

        public async Task<IReadOnlyList<CredentialSummary>> ListAsync()
        {
            if (_gate is { } gate)
            {
                await gate.Task;
            }

            return [];
        }

        public Task<Credential?> GetAsync(Guid id) => Task.FromResult<Credential?>(null);
        public Task SaveAsync(Credential credential) => Task.CompletedTask;
        public Task DeleteAsync(Guid id) => Task.CompletedTask;
    }
}

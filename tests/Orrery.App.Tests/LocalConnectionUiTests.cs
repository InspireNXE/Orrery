using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Orrery.App.Connections;
using Orrery.Core.Connections;
using Orrery.Core.Credentials;
using Xunit;

namespace Orrery.App.Tests;

/// <summary>A shell on this computer saved in the tree, in the properties pane.</summary>
public class LocalConnectionUiTests
{
    sealed class NoCredentials : ICredentialStore
    {
        public bool IsLocked => false;
        public bool Exists => true;
        public Task UnlockAsync(string masterPassword) => Task.CompletedTask;
        public void Lock() { }
        public Task<IReadOnlyList<CredentialSummary>> ListAsync() => Task.FromResult<IReadOnlyList<CredentialSummary>>([]);
        public Task<Credential?> GetAsync(Guid id) => Task.FromResult<Credential?>(null);
        public Task SaveAsync(Credential credential) => Task.CompletedTask;
        public Task SaveAllAsync(IReadOnlyList<Credential> credentials) => Task.CompletedTask;
        public Task DeleteAsync(Guid id) => Task.CompletedTask;
    }

    [AvaloniaFact]
    public async Task A_local_shell_shows_its_shell_and_starting_folder_but_no_login_or_network_settings()
    {
        var (panel, window, entry) = await ShowAsync();

        Assert.True(Text(window, "Shell")?.IsEffectivelyVisible);
        Assert.True(Text(window, "LOCAL TERMINAL")?.IsEffectivelyVisible);
        Assert.NotEqual(true, Text(window, "CREDENTIALS")?.IsEffectivelyVisible);
        Assert.NotEqual(true, Text(window, "WAKE ON LAN")?.IsEffectivelyVisible);
        Assert.NotEqual(true, Text(window, "Port")?.IsEffectivelyVisible);
        Assert.True(Named<Button>(window, "Choose a shell on this computer").IsEffectivelyVisible);
        GC.KeepAlive(panel);
        GC.KeepAlive(entry);
    }

    [AvaloniaFact]
    public async Task Its_starting_folder_and_administrator_switch_are_saved_with_it()
    {
        var (panel, window, entry) = await ShowAsync();

        Named<TextBox>(window, "Starting folder").Text = "~/repo";
        if (OperatingSystem.IsWindows())
        {
            Named<ToggleSwitch>(window, "Run as administrator").IsChecked = true;
        }

        Dispatcher.UIThread.RunJobs();
        Assert.True(panel.TrySave());

        Assert.Equal("~/repo", entry.StartFolder);
        Assert.Equal(OperatingSystem.IsWindows(), entry.RunAsAdministrator);
    }

    static async Task<(PropertiesPanel, Window, ConnectionEntry)> ShowAsync()
    {
        var tree = ConnectionTree.Empty();
        var entry = new ConnectionEntry { Name = "Repo shell", Host = "cmd", Settings = { Protocol = Protocol.Local } };
        tree.Add(entry, tree.Root);
        var panel = new PropertiesPanel(tree, new NoCredentials());
        var window = new Window { Content = panel, Width = 400, Height = 900 };
        window.Show();
        await panel.ShowAsync(entry);
        Dispatcher.UIThread.RunJobs();
        return (panel, window, entry);
    }

    static TextBlock? Text(Window window, string text) =>
        window.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault(t => t.Text == text);

    static T Named<T>(Window window, string name) where T : Control =>
        window.GetVisualDescendants().OfType<T>().First(c => AutomationProperties.GetName(c) == name);
}

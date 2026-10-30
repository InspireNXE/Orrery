using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Orrery.App.Connections;
using Orrery.App.Credentials;
using Orrery.Core.Connections;
using Orrery.Core.Credentials;
using Orrery.Core.Storage;
using Xunit;

namespace Orrery.App.Tests;

public class VaultAutoLockTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "orrery-tests-" + Guid.NewGuid().ToString("N"));

    [AvaloniaFact]
    public async Task Auto_locking_the_vault_clears_the_credential_picker_in_the_inspector()
    {
        var location = new DataLocation(StorageMode.Portable, Path.Combine(_dir, "data"), Path.Combine(_dir, "cache"), Path.Combine(_dir, "logs"));
        Directory.CreateDirectory(location.DataDir);
        AddMainWindowResources();
        var window = new MainWindow(location);
        try
        {
            window.Show();
            var vault = Assert.IsType<VaultFileStore>(Private<ICredentialStore>(window, "_credentials"));
            var tree = Private<ConnectionTree>(window, "_tree");
            var treeView = Private<TreeView>(window, "_treeView");

            await vault.UnlockAsync("correct horse battery staple");
            await vault.SaveAsync(new Credential(Guid.NewGuid(), "payroll-admin", CredentialKind.Password, "admin", "corp", "Sup3r-Secret!"));
            var entry = new ConnectionEntry { Name = "box", Host = "box.example" };
            tree.Add(entry, tree.Root);
            Dispatcher.UIThread.RunJobs();
            treeView.SelectedItem = ((IEnumerable<NodeViewModel>)treeView.ItemsSource!).Single(r => r.Node == entry);
            Dispatcher.UIThread.RunJobs();

            var picker = window.GetVisualDescendants().OfType<CredentialPicker>().Single();
            Assert.Contains(PickerText(picker), t => t.Contains("payroll-admin"));

            // The idle timer only ticks every 30 seconds, so run its check by hand once the vault is idle.
            vault.AutoLockAfter = TimeSpan.FromMilliseconds(1);
            await Task.Delay(50);
            typeof(VaultFileStore).GetMethod("AutoLockIfIdle", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(vault, null);
            Assert.True(vault.IsLocked);
            for (int i = 0; i < 20; i++)
            {
                Dispatcher.UIThread.RunJobs();
                await Task.Delay(10);
            }

            Assert.DoesNotContain(PickerText(picker), t => t.Contains("payroll-admin"));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Auto_locking_the_vault_clears_an_open_connect_prompt()
    {
        using var vault = new VaultFileStore(Path.Combine(_dir, "vault.frv"), new VaultFileStore.KdfSettings(MemoryKiB: 8 * 1024, Iterations: 1));
        await vault.UnlockAsync("correct horse battery staple");
        var credential = new Credential(Guid.NewGuid(), "payroll-admin", CredentialKind.Password, "admin", "corp", "Sup3r-Secret!");
        await vault.SaveAsync(credential);

        var prompt = new ConnectPromptWindow(vault, "box", "box.example", null, null, offerRemember: false);
        try
        {
            prompt.Show();
            var picker = prompt.GetVisualDescendants().OfType<CredentialPicker>().Single();
            for (int i = 0; i < 20; i++)
            {
                Dispatcher.UIThread.RunJobs();
                await Task.Delay(10);
            }

            picker.SelectedItem = picker.Items.OfType<ComboBoxItem>().Single(i => i.Tag is Guid id && id == credential.Id);
            Dispatcher.UIThread.RunJobs();
            Assert.Contains(prompt.GetVisualDescendants().OfType<TextBox>(), t => t.Text == "admin");

            vault.AutoLockAfter = TimeSpan.FromMilliseconds(1);
            await Task.Delay(50);
            typeof(VaultFileStore).GetMethod("AutoLockIfIdle", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(vault, null);
            for (int i = 0; i < 20; i++)
            {
                Dispatcher.UIThread.RunJobs();
                await Task.Delay(10);
            }

            Assert.DoesNotContain(PickerText(picker), t => t.Contains("payroll-admin"));
            Assert.DoesNotContain(prompt.GetVisualDescendants().OfType<TextBox>(), t => t.Text == "admin" || t.Text == "corp");
            Assert.Equal(credential.Id, picker.SelectedId);
        }
        finally
        {
            prompt.Close();
        }
    }

    /// <summary>The test app has Fluent's theme but not Orrery's palette, which the main window's XAML refers to by key.</summary>
    static void AddMainWindowResources()
    {
        var resources = Application.Current!.Resources;
        foreach (var (key, color) in new[]
        {
            ("OrreryAccent", "#5B9DFF"), ("OrreryBackground", "#0E1014"), ("OrreryBorder", "#23272F"), ("OrreryControlBorder", "#2A2F38"),
            ("OrreryFolder", "#E0B45A"), ("OrreryMuted", "#9AA3B2"), ("OrreryPanel", "#14161B"), ("OrrerySoftText", "#C3C9D3"), ("OrreryText", "#E6E8EC"),
        })
        {
            resources[key] = new SolidColorBrush(Color.Parse(color));
        }

        resources["OrreryMono"] = new FontFamily("Cascadia Mono, Consolas, Menlo, monospace");
    }

    static IEnumerable<string> PickerText(CredentialPicker picker) =>
        picker.Items.OfType<ComboBoxItem>().Select(i => i.Content?.ToString() ?? string.Empty).ToList();

    static T Private<T>(object owner, string field) =>
        (T)owner.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!;

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        }
    }
}

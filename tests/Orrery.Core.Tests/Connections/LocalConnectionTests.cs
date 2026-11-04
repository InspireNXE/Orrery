using System.Text;
using Orrery.Core.Connections;
using Orrery.Core.Export;
using Orrery.Core.Import;
using Orrery.Core.Network;
using Xunit;

namespace Orrery.Core.Tests.Connections;

/// <summary>A shell on this computer saved in the tree as a connection.</summary>
public class LocalConnectionTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "orrery-tests-" + Guid.NewGuid().ToString("N"));

    ConnectionStore Store() => new(Path.Combine(_dir, "connections.json"), Path.Combine(_dir, "backups"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    static (ConnectionTree Tree, ConnectionEntry Shell) Sample()
    {
        var tree = ConnectionTree.Empty();
        var shell = new ConnectionEntry
        {
            Name = "Admin PowerShell",
            Host = "pwsh",
            StartFolder = "~/repo",
            RunAsAdministrator = true,
            Settings = { Protocol = Protocol.Local },
        };
        tree.Add(shell, tree.Root);
        return (tree, shell);
    }

    [Fact]
    public void Its_shell_folder_and_administrator_switch_are_saved()
    {
        var (tree, shell) = Sample();
        Store().Save(tree);

        var loaded = Assert.IsType<ConnectionEntry>(Store().Load().Find(shell.Id));
        Assert.Equal(Protocol.Local, loaded.Settings.Protocol);
        Assert.Equal("pwsh", loaded.Host);
        Assert.Equal("~/repo", loaded.StartFolder);
        Assert.True(loaded.RunAsAdministrator);
    }

    [Fact]
    public void A_tree_with_one_is_written_with_a_schema_older_builds_refuse()
    {
        var (tree, shell) = Sample();
        Assert.Equal(6, ConnectionStore.SchemaFor(tree.Root));

        shell.Settings.Protocol = Protocol.Ssh;
        Assert.True(ConnectionStore.SchemaFor(tree.Root) < 6);
    }

    [Fact]
    public void It_is_not_checked_on_the_network()
    {
        var (_, shell) = Sample();
        Assert.Null(ProbeTarget.For(shell, out var why));
        Assert.Contains("shell on this computer", why);
    }

    [Fact]
    public void A_duplicate_keeps_how_it_starts()
    {
        var (tree, shell) = Sample();
        var copy = (ConnectionEntry)tree.Duplicate(shell);
        Assert.Equal("~/repo", copy.StartFolder);
        Assert.True(copy.RunAsAdministrator);
    }

    [Fact]
    public void An_imported_one_keeps_its_folder_but_not_running_as_administrator()
    {
        var export = ConnectionExporter.Export(Sample().Tree.Root, ExportFormat.Orrery, null, DateTimeOffset.Now);
        var imported = OrreryImporter.Import(new MemoryStream(Encoding.UTF8.GetBytes(export.Content)), "x.json");

        var shell = imported.Root.Descendants().OfType<ConnectionEntry>().Single();
        Assert.Equal(Protocol.Local, shell.Settings.Protocol);
        Assert.Equal("~/repo", shell.StartFolder);
        Assert.False(shell.RunAsAdministrator);
        Assert.Contains(imported.Report.Warnings, w => w.Contains("administrator"));
    }

    [Fact]
    public void It_is_left_out_of_an_mremoteng_export()
    {
        var export = ConnectionExporter.Export(Sample().Tree.Root, ExportFormat.MRemoteNg, null, DateTimeOffset.Now);
        Assert.DoesNotContain("Admin PowerShell", export.Content);
    }
}

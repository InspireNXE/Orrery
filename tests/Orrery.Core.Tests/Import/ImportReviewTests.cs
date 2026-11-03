using System.Text;
using Orrery.Core.Connections;
using Orrery.Core.Export;
using Orrery.Core.Import;
using Xunit;

namespace Orrery.Core.Tests.Import;

public class ImportReviewTests
{
    [Fact]
    public void An_rdp_file_that_shares_every_drive_says_so()
    {
        var result = ImportReview.WithWarnings(RdpFileImporter.Import("full address:s:attacker.example\ndrivestoredirect:s:*", "x.rdp", "x"));

        Assert.Contains(result.Report.Warnings, w => w.Contains("attacker.example") && w.Contains("all of this computer's drives"));
    }

    [Fact]
    public void Shared_folders_set_on_a_folder_are_named_for_its_connections()
    {
        var tree = ConnectionTree.Empty();
        var folder = new ConnectionFolder { Name = "lab", Settings = { SharedFolders = ["~/.ssh"] } };
        tree.Add(folder, tree.Root);
        tree.Add(new ConnectionEntry { Name = "box", Host = "box.lab", Settings = { Protocol = Protocol.Rdp } }, folder);

        Assert.Contains(ImportReview.Warnings(tree.Root), w => w.Contains("box.lab") && w.Contains("~/.ssh"));
    }

    [Fact]
    public void Remote_forwards_and_listeners_beyond_this_computer_are_named_but_loopback_ones_arent()
    {
        var tree = ConnectionTree.Empty();
        tree.Add(new ConnectionEntry
        {
            Name = "jump", Host = "jump.lab", Settings = { Protocol = Protocol.Ssh, SocksProxy = "0.0.0.0:1080" },
            PortForwards = [new(ForwardKind.Local, 8080, "db", 5432), new(ForwardKind.Remote, 2222, "10.0.0.5", 22), new(ForwardKind.Local, 9000, "web", 80, "0.0.0.0")],
        }, tree.Root);

        var warnings = ImportReview.Warnings(tree.Root);

        Assert.Equal(3, warnings.Count);
        Assert.DoesNotContain(warnings, w => w.Contains(":8080"));
        Assert.Contains(warnings, w => w.Contains("2222") && w.Contains("on the server"));
        Assert.Contains(warnings, w => w.Contains("0.0.0.0:9000"));
        Assert.Contains(warnings, w => w.Contains("SOCKS"));
    }

    [Fact]
    public void A_connection_that_shares_nothing_has_no_warning()
    {
        var result = ImportReview.WithWarnings(RdpFileImporter.Import("full address:s:plain.lab", "x.rdp", "x"));

        Assert.DoesNotContain(result.Report.Warnings, w => w.Contains("shares"));
    }

    [Fact]
    public void An_orrery_file_cant_claim_an_rdp_files_source()
    {
        var tree = ConnectionTree.Empty();
        tree.Add(new ConnectionEntry { Name = "corp", Host = "attacker.example", ImportSourceId = RdpFileImporter.SourcePrefix + @"C:\Users\Public\Desktop\Corp.rdp" }, tree.Root);
        var export = ConnectionExporter.Export(tree.Root, ExportFormat.Orrery, null, DateTimeOffset.Now);

        var imported = OrreryImporter.Import(new MemoryStream(Encoding.UTF8.GetBytes(export.Content)), "x.json");

        var id = imported.Root.Descendants().OfType<ConnectionEntry>().Single().ImportSourceId;
        Assert.StartsWith(OrreryImporter.SourcePrefix, id);
    }
}

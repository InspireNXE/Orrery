using System.Text;
using Orrery.Core.Connections;
using Orrery.Core.Export;
using Orrery.Core.Import;
using Orrery.Core.Network;
using Xunit;

namespace Orrery.Core.Tests.Network;

public class ManagementUrlTests
{
    [Theory]
    [InlineData("https://10.20.0.1:8443", "https://10.20.0.1:8443")]
    [InlineData("  http://ilo.lab/index.html  ", "http://ilo.lab/index.html")]
    [InlineData("10.20.0.1:8443", "https://10.20.0.1:8443")]
    [InlineData("firewall.lan/admin", "https://firewall.lan/admin")]
    [InlineData("HTTPS://Router.lan", "HTTPS://Router.lan")]
    public void A_web_address_is_read_with_https_added_when_it_has_no_scheme(string text, string expected)
    {
        Assert.True(ManagementUrl.TryNormalize(text, out var url));
        Assert.Equal(expected, url);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("javascript:alert(1)")]
    [InlineData("file:///C:/Windows/System32/calc.exe")]
    [InlineData("ftp://files.lan")]
    [InlineData("ms-settings://display")]
    [InlineData("https://")]
    [InlineData("https://has space.lan")]
    public void Anything_but_an_http_or_https_address_is_refused(string? text)
    {
        Assert.False(ManagementUrl.TryNormalize(text, out _));
    }

    static ConnectionTree Sample()
    {
        var tree = ConnectionTree.Empty();
        tree.Add(new ConnectionEntry { Name = "fw", Host = "10.20.0.1", ManagementUrl = "https://10.20.0.1:8443" }, tree.Root);
        return tree;
    }

    [Fact]
    public void It_survives_an_orrery_export_and_import_and_a_duplicate()
    {
        var tree = Sample();
        var export = ConnectionExporter.Export(tree.Root, ExportFormat.Orrery, null, DateTimeOffset.Now);
        var imported = OrreryImporter.Import(new MemoryStream(Encoding.UTF8.GetBytes(export.Content)), "x.json");
        Assert.Equal("https://10.20.0.1:8443", imported.Root.Descendants().OfType<ConnectionEntry>().Single().ManagementUrl);

        var copy = (ConnectionEntry)tree.Duplicate(tree.AllConnections().Single());
        Assert.Equal("https://10.20.0.1:8443", copy.ManagementUrl);
    }

    [Fact]
    public void An_imported_address_that_isnt_http_or_https_is_left_out_with_a_warning()
    {
        var export = ConnectionExporter.Export(Sample().Root, ExportFormat.Orrery, null, DateTimeOffset.Now);
        var edited = export.Content.Replace("https://10.20.0.1:8443", "file:///C:/Windows/System32/calc.exe");
        var imported = OrreryImporter.Import(new MemoryStream(Encoding.UTF8.GetBytes(edited)), "x.json");

        Assert.Null(imported.Root.Descendants().OfType<ConnectionEntry>().Single().ManagementUrl);
        Assert.Contains(imported.Report.Warnings, w => w.Contains("management URL"));
    }

    [Fact]
    public void An_mremoteng_export_says_management_urls_are_lost()
    {
        var export = ConnectionExporter.Export(Sample().Root, ExportFormat.MRemoteNg, null, DateTimeOffset.Now);
        Assert.Contains(export.Warnings, w => w.Contains("management URLs"));
    }
}

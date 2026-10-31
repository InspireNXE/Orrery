using Orrery.Core.Connections;
using Xunit;

namespace Orrery.Core.Tests.Connections;

public class ConnectionSearchTests
{
    static ConnectionEntry Web01()
    {
        var tree = ConnectionTree.Empty();
        var production = new ConnectionFolder { Name = "Production" };
        var web01 = new ConnectionEntry { Name = "web-01", Host = "10.0.4.21", Notes = "Behind the payments load balancer", Settings = { Username = "deploy" } };
        tree.Add(production, tree.Root);
        tree.Add(web01, production);
        return web01;
    }

    [Theory]
    [InlineData("web", 100)]
    [InlineData("10.0", 90)]
    [InlineData("-01", 80)]
    [InlineData("4.21", 70)]
    [InlineData("prod", 50)]
    [InlineData("deploy", 50)]
    [InlineData("payments", 50)]
    [InlineData("prod web", 60)]
    [InlineData("WEB PROD", 60)]
    [InlineData("wb01", 30)]
    public void Matches_rank_name_then_host_then_other_fields_then_letters_in_order(string query, int expected)
    {
        Assert.Equal(expected, ConnectionSearch.Score(Web01(), query));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("staging")]
    [InlineData("prod staging")]
    [InlineData("10w")]
    [InlineData("z")]
    public void Unrelated_text_doesnt_match(string query)
    {
        Assert.Equal(0, ConnectionSearch.Score(Web01(), query));
    }
}

using Orrery.Core.Connections;
using Xunit;

namespace Orrery.Core.Tests.Connections;

public class WebAddressTests
{
    [Theory]
    [InlineData("router.lan", "router.lan", "")]
    [InlineData("router.lan/admin", "router.lan", "/admin")]
    [InlineData(" router.lan:8443/admin?tab=1#top ", "router.lan:8443", "/admin?tab=1#top")]
    [InlineData("router.lan?x=1", "router.lan", "?x=1")]
    public void Splits_the_host_from_the_path(string host, string hostOnly, string path)
    {
        Assert.Equal(hostOnly, WebAddress.HostOf(host));
        Assert.Equal(path, WebAddress.PathOf(host));
    }

    [Theory]
    [InlineData(Protocol.Http, "router.lan", 80, "http://router.lan/")]
    [InlineData(Protocol.Https, "router.lan", 443, "https://router.lan/")]
    [InlineData(Protocol.Https, "router.lan", 8443, "https://router.lan:8443/")]
    [InlineData(Protocol.Http, "router.lan/admin?tab=1", 80, "http://router.lan/admin?tab=1")]
    [InlineData(Protocol.Https, "router.lan:9443/admin", 443, "https://router.lan:9443/admin")]
    [InlineData(Protocol.Https, "[fe80::1]", 443, "https://[fe80::1]/")]
    [InlineData(Protocol.Http, "10.0.0.1", 8080, "http://10.0.0.1:8080/")]
    public void Builds_the_page_to_open(Protocol protocol, string host, int port, string url)
    {
        Assert.Equal(url, WebAddress.Url(protocol, host, port)?.AbsoluteUri);
    }

    [Theory]
    [InlineData(Protocol.Rdp, "router.lan")]
    [InlineData(Protocol.Https, "")]
    [InlineData(Protocol.Https, "router lan")]
    [InlineData(Protocol.Https, "user@router.lan")]
    [InlineData(Protocol.Https, "good.example@evil.example/path")]
    [InlineData(Protocol.Https, "router.lan\n/admin")]
    public void Opens_nothing_it_cannot_trust(Protocol protocol, string host)
    {
        Assert.Null(WebAddress.Url(protocol, host, 443));
    }

    [Theory]
    [InlineData("https://router.lan", Protocol.Https, "router.lan", 443)]
    [InlineData("HTTP://router.lan/", Protocol.Http, "router.lan", 80)]
    [InlineData("https://router.lan:8443/admin", Protocol.Https, "router.lan/admin", 8443)]
    [InlineData("http://[fe80::1]:8080/x", Protocol.Http, "[fe80::1]/x", 8080)]
    public void Reads_a_typed_address(string text, Protocol protocol, string host, int port)
    {
        Assert.True(WebAddress.TryParseUrl(text, out var parsedProtocol, out var parsedHost, out var parsedPort));
        Assert.Equal((protocol, host, port), (parsedProtocol, parsedHost, parsedPort));
    }

    [Theory]
    [InlineData("router.lan")]
    [InlineData("ftp://router.lan")]
    [InlineData("https://")]
    [InlineData("https://user@router.lan")]
    public void Refuses_what_is_not_a_web_address(string text)
    {
        Assert.False(WebAddress.TryParseUrl(text, out _, out _, out _));
    }
}

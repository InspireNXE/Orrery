using System.Text;
using Orrery.Core.Sessions;
using Xunit;
using static Orrery.Protocols.Rdp.RdpRedirection;

namespace Orrery.Protocols.Rdp.Tests;

public class RdpRedirectionTests
{
    static readonly ConnectionTarget Broker = new("broker.corp", 3390, "me", "CORP");

    static RdpRedirection Redirection(uint flags, string? fqdn = null, string? address = null, string? netbios = null,
        string? username = null, string? domain = null, string token = "") =>
        new(flags, fqdn, address, netbios, username, domain, Encoding.ASCII.GetBytes(token), 7);

    [Fact]
    public void Prefers_the_full_name_then_the_address_then_the_netbios_name()
    {
        const uint all = TargetFqdnFlag | TargetNetAddressFlag | TargetNetbiosNameFlag;
        Assert.Equal("sh1.corp", Redirection(all, "sh1.corp", "10.0.0.5", "SH1").NextHost);
        Assert.Equal("10.0.0.5", Redirection(all, " ", "10.0.0.5", "SH1").NextHost);
        Assert.Equal("SH1", Redirection(TargetNetbiosNameFlag, "sh1.corp", null, "SH1").NextHost);
    }

    [Fact]
    public void A_name_its_flag_does_not_vouch_for_is_ignored()
    {
        Assert.Null(Redirection(0, "sh1.corp", "10.0.0.5", "SH1").NextHost);
    }

    [Fact]
    public void Keeps_the_port_and_login_unless_the_redirection_names_them()
    {
        var next = Redirection(TargetFqdnFlag, "sh1.corp").Next(Broker);
        Assert.Equal(new ConnectionTarget("sh1.corp", 3390, "me", "CORP"), next);

        next = Redirection(TargetFqdnFlag | UsernameFlag | DomainFlag, "sh1.corp", username: "me2", domain: "").Next(Broker);
        Assert.Equal(new ConnectionTarget("sh1.corp", 3390, "me2", ""), next);
    }

    [Fact]
    public void Follows_a_redirection_to_another_host()
    {
        Assert.Equal("sh1.corp", RdpSession.NextHop(Redirection(TargetFqdnFlag, "sh1.corp"), Broker, hop: 0).Host);
    }

    [Fact]
    public void Gives_up_after_too_many_redirections()
    {
        var redirection = Redirection(TargetFqdnFlag, "broker.corp");
        for (int hop = 0; hop < MaxHops - 1; hop++)
        {
            RdpSession.NextHop(redirection, Broker, hop);
        }

        var error = Assert.Throws<NotSupportedException>(() => RdpSession.NextHop(redirection, Broker, MaxHops - 1));
        Assert.Contains($"{MaxHops} times", error.Message);
    }

    [Fact]
    public void A_broker_that_wants_its_routing_token_back_is_called_again()
    {
        var redirection = Redirection(NoRedirectFlag | LoadBalanceInfoFlag | TargetFqdnFlag, "sh1.corp", token: "Cookie: msts=3640205228.15629.0000\r\n");
        Assert.True(redirection.StayOnServer);
        Assert.Null(redirection.NextHost);
        Assert.Equal("Cookie: msts=3640205228.15629.0000", redirection.RoutingTokenText);
        Assert.Equal(Broker, RdpSession.NextHop(redirection, Broker, 0));
        Assert.Equal(redirection.LoadBalanceInfo, redirection.RoutingToken);
    }

    [Fact]
    public void Sends_back_only_what_the_flags_vouch_for()
    {
        byte[] cookie = [1, 2, 3];
        var withFlags = new RdpRedirection(TargetFqdnFlag | LoadBalanceInfoFlag | PasswordFlag, "sh1.corp", null, null, null, null,
            Encoding.ASCII.GetBytes("tsv://MS Terminal Services Plugin.1.Pool"), 7, cookie);
        Assert.Equal(cookie, withFlags.Cookie);
        Assert.NotEmpty(withFlags.RoutingToken);

        var withoutFlags = withFlags with { Flags = TargetFqdnFlag };
        Assert.Empty(withoutFlags.Cookie);
        Assert.Empty(withoutFlags.RoutingToken);
        Assert.Empty((withFlags with { PasswordCookie = null }).Cookie);
    }

    [Fact]
    public void The_password_cookie_never_reaches_the_message()
    {
        var redirection = new RdpRedirection(TargetFqdnFlag | PasswordFlag, "sh1.corp", null, null, null, null, [], 7, Encoding.ASCII.GetBytes("s3cret"));
        Assert.DoesNotContain("s3cret", new ServerRedirectedException(redirection).Message);
    }

    [Fact]
    public void A_redirection_to_nowhere_is_refused()
    {
        Assert.Contains("without saying where", Assert.Throws<NotSupportedException>(() => RdpSession.NextHop(Redirection(0), Broker, 0)).Message);
        Assert.Equal("none", Redirection(0).RoutingTokenText);
    }
}

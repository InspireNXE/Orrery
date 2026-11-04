using Orrery.Core.Diagnostics;
using Orrery.Core.Storage;
using Xunit;

namespace Orrery.Core.Tests.Diagnostics;

public class LogMaskTests : IDisposable
{
    static readonly string DataDir = Path.Combine(Path.GetTempPath(), "orrery-mask-data");
    static readonly string Home = Path.Combine(Path.GetTempPath(), "orrery-mask-home");

    readonly string _root = Path.Combine(Path.GetTempPath(), "orrery-tests-" + Guid.NewGuid().ToString("N"));

    static LogMask NewMask(byte fill = 7) => new(Enumerable.Repeat(fill, 32).ToArray(), DataDir, Home);

    [Fact]
    public void A_value_gets_the_same_tag_every_time_and_another_key_gives_another()
    {
        var mask = NewMask();
        var tag = mask.Mask(LogMaskKind.Host, "server01.corp.example");

        Assert.Matches("^host-[0-9a-f]{6}$", tag);
        Assert.Equal(tag, mask.Mask(LogMaskKind.Host, "SERVER01.corp.example"));
        Assert.Equal(tag, NewMask().Mask(LogMaskKind.Host, "server01.corp.example"));
        Assert.NotEqual(tag, NewMask(fill: 8).Mask(LogMaskKind.Host, "server01.corp.example"));
        Assert.NotEqual(tag, mask.Mask(LogMaskKind.Host, "server02.corp.example"));
        Assert.Matches("^user-[0-9a-f]{6}$", mask.Mask(LogMaskKind.User, "admin"));
        Assert.Matches("^name-[0-9a-f]{6}$", mask.Mask(LogMaskKind.Name, "Payroll database"));
        Assert.Matches("^token-[0-9a-f]{6}$", mask.Mask(LogMaskKind.Token, "Cookie: msts=12345"));
        Assert.Equal(mask.Mask(LogMaskKind.Mac, "AA:BB:CC:DD:EE:FF"), mask.Mask(LogMaskKind.Mac, "aa-bb-cc-dd-ee-ff"));
    }

    [Theory]
    [InlineData("localhost")]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    [InlineData("0.0.0.0")]
    [InlineData("255.255.255.255")]
    public void Addresses_that_say_nothing_about_a_network_stay(string host) =>
        Assert.Equal(host, NewMask().Mask(LogMaskKind.Host, host));

    [Fact]
    public void An_ip_address_is_tagged_as_one_and_a_port_stays()
    {
        var mask = NewMask();
        var ip = mask.Mask(LogMaskKind.Host, "10.1.2.3");

        Assert.Matches("^ip-[0-9a-f]{6}$", ip);
        Assert.Equal(ip + ":3389", mask.Mask(LogMaskKind.Host, "10.1.2.3:3389"));
        Assert.Equal(mask.Mask(LogMaskKind.Host, "server01") + ":22", mask.Mask(LogMaskKind.Host, "server01:22"));
        Assert.Equal("[::1]:8080", mask.Mask(LogMaskKind.Host, "[::1]:8080"));
        Assert.Matches("^ip-[0-9a-f]{6}:443$", mask.Mask(LogMaskKind.Host, "[fe80::1]:443"));
    }

    [Fact]
    public void Files_in_the_data_folder_show_where_they_are_and_others_only_their_extension()
    {
        var mask = NewMask();

        Assert.Equal("<data>" + Path.DirectorySeparatorChar + "connections.json", mask.Mask(LogMaskKind.Path, Path.Combine(DataDir, "connections.json")));
        Assert.Matches(@"^path-[0-9a-f]{6}\.orrec$", mask.Mask(LogMaskKind.Path, Path.Combine(Home, "Recordings", "Payroll 2026-11-04.orrec")));
        Assert.Matches("^path-[0-9a-f]{6}$", mask.Mask(LogMaskKind.Path, "/home/bob/notes"));
    }

    [Fact]
    public void A_web_address_keeps_its_scheme_and_port_and_hides_its_host_as_a_host_is()
    {
        var mask = NewMask();
        var host = mask.Mask(LogMaskKind.Host, "nas.home.example");

        Assert.Equal($"https://{host}:5001/...", mask.Mask(LogMaskKind.Web, "https://nas.home.example:5001/admin/?token=abc"));
        Assert.Equal($"https://{host}", mask.Mask(LogMaskKind.Web, "https://nas.home.example"));
        Assert.Equal($"https://{mask.Mask(LogMaskKind.User, "bob")}@{host}/...", mask.Mask(LogMaskKind.Web, "https://bob:secret@nas.home.example/x"));
        Assert.Equal("https://github.com/InspireNXE/Orrery/releases", mask.Mask(LogMaskKind.Web, "https://github.com/InspireNXE/Orrery/releases"));
    }

    [Fact]
    public void Free_text_loses_names_seen_before_and_any_address()
    {
        var mask = NewMask();
        var host = mask.Mask(LogMaskKind.Host, "db01.corp.example");
        var name = mask.Mask(LogMaskKind.Name, "Payroll");
        var user = mask.Mask(LogMaskKind.User, "deploy");

        var text = mask.Redact("No such host db01.corp.example; \"Payroll\" as deploy failed. Payroll runs at 10:00:00 from 192.168.1.20:22, "
            + "fe80::1ff:fe23:4567:890a and https://intranet.example/login, mail ops@corp.example, card 00-1A-2B-3C-4D-5E");

        Assert.Contains($"No such host {host};", text);
        Assert.Contains($"\"{name}\" as {user} failed", text);
        Assert.Contains("Payroll runs at 10:00:00", text); // A name, unquoted, may be an everyday word.
        Assert.Matches(@"from ip-[0-9a-f]{6}:22,", text);
        Assert.Matches(@" ip-[0-9a-f]{6} and https://host-[0-9a-f]{6}/\.\.\.,", text);
        Assert.Matches(@"mail user-[0-9a-f]{6}@host-[0-9a-f]{6}, card mac-[0-9a-f]{6}$", text);
        Assert.Equal(text, mask.Redact(text)); // Tags are never tagged again.
    }

    [Theory]
    [InlineData("Microsoft Windows 10.0.26200")]
    [InlineData("SSH-2.0-OpenSSH_9.6p1 Ubuntu-3ubuntu13.5")]
    [InlineData("version 1.2.3.4.5")]
    [InlineData("took 12:34:56")]
    [InlineData("listening on 127.0.0.1:8080")]
    public void Versions_times_and_loopback_addresses_stay(string text) =>
        Assert.Equal(text, NewMask().Redact(text));

    [Fact]
    public void The_data_and_home_folders_are_shortened_in_free_text()
    {
        var text = NewMask().Redact($"Access to the path '{Path.Combine(DataDir, "settings.json")}' is denied; see {Path.Combine(Home, "Downloads")}");

        Assert.Equal($"Access to the path '<data>{Path.DirectorySeparatorChar}settings.json' is denied; see ~{Path.DirectorySeparatorChar}Downloads", text);
    }

    [Fact]
    public void Lookup_turns_a_tag_back_and_shows_a_value_s_tags_without_learning_it()
    {
        var mask = NewMask();
        var tag = mask.Mask(LogMaskKind.Host, "web01");
        var file = mask.Mask(LogMaskKind.Path, "/srv/backup.tar");

        Assert.Equal(new LogMaskMatch(LogMaskKind.Host, "web01", tag), Assert.Single(mask.Lookup(tag)));
        Assert.Equal("/srv/backup.tar", Assert.Single(mask.Lookup(file)).Value);
        Assert.Empty(mask.Lookup("host-000000"));

        var tags = mask.Lookup("printer");
        Assert.Contains(tags, m => m.Kind == LogMaskKind.Host && m.Tag == NewMask().Mask(LogMaskKind.Host, "printer"));
        Assert.Contains(tags, m => m.Kind == LogMaskKind.User);
        Assert.Equal("the printer jammed", mask.Redact("the printer jammed"));
    }

    [Fact]
    public void The_key_is_kept_in_the_data_folder_and_gives_the_same_tags_next_time()
    {
        var location = new DataLocation(StorageMode.Portable, _root, Path.Combine(_root, "cache"), Path.Combine(_root, "logs"));
        Directory.CreateDirectory(_root);

        var first = LogMask.For(location).Mask(LogMaskKind.Host, "server01");

        Assert.True(File.Exists(location.FileIn(LogMask.KeyFileName)));
        Assert.Equal(32, File.ReadAllBytes(location.FileIn(LogMask.KeyFileName)).Length);
        Assert.Equal(first, LogMask.For(location).Mask(LogMaskKind.Host, "server01"));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(location.FileIn(LogMask.KeyFileName)));
        }
    }

    [Fact]
    public void This_account_s_name_is_hidden_from_the_start()
    {
        var location = new DataLocation(StorageMode.Portable, _root, Path.Combine(_root, "cache"), Path.Combine(_root, "logs"));
        Directory.CreateDirectory(_root);
        var mask = LogMask.For(location);

        Assert.DoesNotContain(Environment.UserName, mask.Redact($"Couldn't open D:\\shares\\{Environment.UserName}\\notes"), StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }
}

using System.Text;
using Orrery.Core.Connections;
using Orrery.Core.Import;
using Orrery.Core.Sessions;
using Xunit;

namespace Orrery.Core.Tests.Import;

public class RdpFileImporterTests
{
    static ConnectionEntry Entry(string text, out ImportResult result)
    {
        result = RdpFileImporter.Import(text, "test.rdp", "test");
        return Assert.Single(result.Root.Children.OfType<ConnectionEntry>());
    }

    static ConnectionEntry Entry(string text) => Entry(text, out _);

    [Fact]
    public void Reads_typed_lines_whatever_their_case_and_skips_the_rest()
    {
        var settings = RdpFileImporter.Parse("""
            Full Address:s:server.example
            screen mode id:i:2
            not a setting
            :s:no key
            colon only:s
            odd type:x:1
            hash:b:0A0B
            full address:s:later.example
            """);

        Assert.Equal(new RdpSetting('s', "later.example"), settings["full address"]);
        Assert.Equal(new RdpSetting('i', "2"), settings["SCREEN MODE ID"]);
        Assert.Equal(new RdpSetting('b', "0A0B"), settings["hash"]);
        Assert.False(settings.ContainsKey("odd type"));
        Assert.False(settings.ContainsKey("colon only"));
        Assert.Equal(3, settings.Count);
    }

    [Fact]
    public void A_value_can_hold_colons()
    {
        Assert.Equal("Cookie: msts=1:2", RdpFileImporter.Parse("loadbalanceinfo:s:Cookie: msts=1:2")["loadbalanceinfo"].Value);
    }

    [Theory]
    [InlineData("full address:s:server.example", "server.example", null)]
    [InlineData("full address:s:server.example:3390", "server.example", 3390)]
    [InlineData("full address:s:server.example:3389", "server.example", null)]
    [InlineData("full address:s:server.example\nserver port:i:3391", "server.example", 3391)]
    [InlineData("full address:s:[fe80::1]:3392", "fe80::1", 3392)]
    [InlineData("alternate full address:s:alt.example", "alt.example", null)]
    public void Takes_the_host_and_a_port_other_than_rdps_own(string text, string host, int? port)
    {
        var entry = Entry(text);
        Assert.Equal(host, entry.Host);
        Assert.Equal(port, entry.Settings.Port);
        Assert.Equal(Protocol.Rdp, entry.Settings.Protocol);
    }

    [Fact]
    public void A_file_without_a_computer_says_so()
    {
        Entry("username:s:admin", out var result);
        Assert.Contains(result.Report.Warnings, w => w.Contains("which computer"));
    }

    [Theory]
    [InlineData("username:s:CORP\\admin", "admin", "CORP")]
    [InlineData("username:s:admin@corp.example", "admin@corp.example", null)]
    [InlineData("username:s:admin\ndomain:s:LAB", "admin", "LAB")]
    public void Splits_a_domain_from_the_user_name(string text, string username, string? domain)
    {
        var entry = Entry("full address:s:h\n" + text);
        Assert.Equal(username, entry.Settings.Username);
        Assert.Equal(domain, entry.Settings.Domain);
    }

    [Fact]
    public void A_saved_password_is_not_imported_and_the_report_says_why()
    {
        var entry = Entry("full address:s:h\npassword 51:b:01000000D08C9DDF0115D1118C7A00C04FC297EB", out var result);
        Assert.Null(entry.Settings.CredentialId);
        Assert.Contains(result.Report.Warnings, w => w.Contains("saved password wasn't imported"));
    }

    [Fact]
    public void A_fixed_size_is_kept_only_for_a_window_that_keeps_it()
    {
        const string size = "full address:s:h\ndesktopwidth:i:1280\ndesktopheight:i:800\n";
        Assert.Equal("1280x800", Entry(size + "screen mode id:i:1\ndynamic resolution:i:0").Settings.DesktopSize);
        Assert.Null(Entry(size + "screen mode id:i:2\ndynamic resolution:i:0").Settings.DesktopSize);
        Assert.Null(Entry(size + "screen mode id:i:1\ndynamic resolution:i:1").Settings.DesktopSize);
    }

    [Fact]
    public void Clipboard_quality_audio_monitors_and_the_admin_session_carry_over()
    {
        var entry = Entry("""
            full address:s:h
            redirectclipboard:i:0
            disable wallpaper:i:1
            disable themes:i:1
            allow font smoothing:i:0
            audiomode:i:2
            use multimon:i:1
            administrative session:i:1
            """);

        Assert.False(entry.Settings.ShareClipboard);
        Assert.Equal(DisplayQuality.Fastest, entry.Settings.DisplayQuality);
        Assert.Equal(RemoteAudio.DoNotPlay, entry.Settings.RemoteAudio);
        Assert.True(entry.Settings.UseAllMonitors);
        Assert.True(entry.Settings.AdminSession);
    }

    [Fact]
    public void Selected_monitors_become_the_chosen_displays_by_mstscs_numbers()
    {
        ChosenDisplay[] displays = [new(0, 360, 1920, 1080), new(1920, 0, 2560, 1440), new(4480, 0, 1080, 1920)];
        var result = RdpFileImporter.Import("full address:s:h\nuse multimon:i:1\nselectedmonitors:s:0,2", "test.rdp", "test", displays: displays);
        var entry = Assert.Single(result.Root.Children.OfType<ConnectionEntry>());

        Assert.Equal([displays[0], displays[2]], entry.Settings.ChosenDisplays);
        Assert.Null(entry.Settings.UseAllMonitors);
        Assert.Equal(FullScreenOn.ChosenDisplays, SettingsResolver.Resolve(entry).FullScreen.Value.On);
    }

    [Fact]
    public void Selected_monitors_this_computer_lacks_fall_back_to_every_display()
    {
        var result = RdpFileImporter.Import("full address:s:h\nuse multimon:i:1\nselectedmonitors:s:0,5", "test.rdp", "test", displays: [new(0, 0, 1920, 1080)]);
        var entry = Assert.Single(result.Root.Children.OfType<ConnectionEntry>());

        Assert.True(entry.Settings.UseAllMonitors);
        Assert.Null(entry.Settings.ChosenDisplays);
        Assert.Contains(result.Report.Warnings, w => w.Contains("doesn't have"));
    }

    [Fact]
    public void Settings_that_match_orrerys_defaults_are_left_to_inherit()
    {
        var entry = Entry("full address:s:h\nredirectclipboard:i:1\naudiomode:i:0\nuse multimon:i:0\nadministrative session:i:0");
        Assert.Null(entry.Settings.ShareClipboard);
        Assert.Null(entry.Settings.RemoteAudio);
        Assert.Null(entry.Settings.UseAllMonitors);
        Assert.Null(entry.Settings.AdminSession);
    }

    [Fact]
    public void Sound_left_on_the_remote_computer_is_not_played_and_reported()
    {
        var entry = Entry("full address:s:h\naudiomode:i:1", out var result);
        Assert.Equal(RemoteAudio.DoNotPlay, entry.Settings.RemoteAudio);
        Assert.Contains(result.Report.NotYetSupported, s => s.StartsWith("Sound left playing"));
    }

    [Theory]
    [InlineData("drivestoredirect:s:*", true, new string[0])]
    [InlineData("redirectdrives:i:1", true, new string[0])]
    [InlineData("drivestoredirect:s:C:;d", null, new[] { "C:\\", "D:\\" })]
    [InlineData("drivestoredirect:s:C:\\;DynamicDrives", null, new[] { "C:\\" })]
    public void Shared_drives_become_every_drive_or_chosen_folders(string text, bool? all, string[] folders)
    {
        var entry = Entry("full address:s:h\n" + text);
        Assert.Equal(all, entry.Settings.ShareAllDrives);
        Assert.Equal(folders, entry.Settings.SharedFolders ?? []);
    }

    [Fact]
    public void Drives_plugged_in_later_are_reported()
    {
        Entry("full address:s:h\ndrivestoredirect:s:DynamicDrives", out var result);
        Assert.Contains("Sharing drives plugged in later", result.Report.NotYetSupported);
    }

    [Fact]
    public void What_orrery_cannot_do_is_reported_not_imported()
    {
        Entry("""
            full address:s:h
            gatewayhostname:s:gw.example
            gatewayusagemethod:i:1
            remoteapplicationmode:i:1
            loadbalanceinfo:s:tsv://MS Terminal Services Plugin.1.Farm
            redirectprinters:i:1
            redirectsmartcards:i:1
            enablecredsspsupport:i:0
            """, out var result);

        Assert.Contains("RD Gateway", result.Report.NotYetSupported);
        Assert.Contains(result.Report.NotYetSupported, s => s.StartsWith("RemoteApp"));
        Assert.Contains("Load balance info", result.Report.NotYetSupported);
        Assert.Contains("Printer redirection", result.Report.NotYetSupported);
        Assert.Contains("Smart card redirection", result.Report.NotYetSupported);
        Assert.Contains(result.Report.Warnings, w => w.Contains("Network Level Authentication"));
    }

    [Fact]
    public void A_gateway_that_is_never_used_is_not_reported()
    {
        Entry("full address:s:h\ngatewayhostname:s:gw.example\ngatewayusagemethod:i:4", out var result);
        Assert.DoesNotContain("RD Gateway", result.Report.NotYetSupported);
    }

    [Fact]
    public void Reads_the_utf16_file_mstsc_saves_and_marks_where_it_came_from()
    {
        var dir = Path.Combine(Path.GetTempPath(), "orrery-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "Finance server.rdp");
            File.WriteAllText(path, "screen mode id:i:2\r\nfull address:s:fin.example\r\nusername:s:CORP\\me\r\n", Encoding.Unicode);

            var result = RdpFileImporter.ImportFile(path);
            var entry = Assert.Single(result.Root.Children.OfType<ConnectionEntry>());
            Assert.Equal("Finance server", entry.Name);
            Assert.Equal("fin.example", entry.Host);
            Assert.Equal("me", entry.Settings.Username);
            Assert.Equal(RdpFileImporter.SourcePrefix + path, entry.ImportSourceId);
            Assert.Equal(1, result.Report.Connections);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void A_file_far_too_large_to_be_an_rdp_file_is_refused()
    {
        var path = Path.Combine(Path.GetTempPath(), "orrery-tests-" + Guid.NewGuid().ToString("N") + ".rdp");
        try
        {
            File.WriteAllBytes(path, new byte[2 * 1024 * 1024]);
            Assert.Throws<ImportException>(() => RdpFileImporter.ImportFile(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("C:\\connections\\server.rdp", true)]
    [InlineData("server.RDP", true)]
    [InlineData("confCons.xml", false)]
    [InlineData("server.rdp.txt", false)]
    public void Knows_a_remote_desktop_file_by_its_name(string path, bool rdp)
    {
        Assert.Equal(rdp, RdpFileImporter.IsRdpFile(path));
    }
}

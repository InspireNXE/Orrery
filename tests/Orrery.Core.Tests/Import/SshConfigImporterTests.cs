using System.Diagnostics;
using System.Text;
using Orrery.Core.Connections;
using Orrery.Core.Import;
using Xunit;

namespace Orrery.Core.Tests.Import;

public sealed class SshConfigImporterTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "orrery-sshconfig-" + Guid.NewGuid().ToString("N"));

    public SshConfigImporterTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    SshConfigEnvironment Env => new(_dir, "local", _dir);

    string Write(string relative, string content)
    {
        var path = Path.Combine(_dir, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    ImportResult Import(string config) => SshConfigImporter.ImportFile(Write("config", config), Env);

    static ConnectionEntry Entry(ImportResult result, string name) =>
        result.Root.Descendants().OfType<ConnectionEntry>().Single(e => e.Name == name);

    static IEnumerable<string> Names(ImportResult result) => result.Root.Descendants().OfType<ConnectionEntry>().Select(e => e.Name);

    static List<string> JumpNames(ImportResult result, string name)
    {
        var chain = SettingsResolver.JumpChain(Entry(result, name), new ConnectionTree(result.Root), out var error);
        Assert.Null(error);
        return chain!.Select(e => e.Name).ToList();
    }

    // ---- Hosts ----

    [Fact]
    public void Each_concrete_host_becomes_an_ssh_connection_in_a_folder_named_after_the_file()
    {
        var result = Import("""
            # my servers
            Host web
                HostName web.example.com
                User deploy
                Port 2222

            Host db
                HostName 10.0.0.5
            """);

        Assert.Equal("config", result.Root.Name);
        Assert.Equal(["web", "db"], Names(result));
        Assert.Equal(2, result.Report.Connections);
        Assert.Equal(2, result.Report.Protocols["SSH"]);
        Assert.Equal("SSH config", result.Report.From);

        var web = Entry(result, "web");
        Assert.Equal("web.example.com", web.Host);
        Assert.Equal(Protocol.Ssh, web.Settings.Protocol);
        Assert.Equal("deploy", web.Settings.Username);
        Assert.Equal(2222, web.Settings.Port);

        var resolved = SettingsResolver.Resolve(Entry(result, "db"));
        Assert.Equal(Protocol.Ssh, resolved.Protocol.Value);
        Assert.Equal(22, resolved.Port.Value);
        Assert.Null(resolved.Username.Value);
        Assert.Empty(result.Credentials);
    }

    [Fact]
    public void A_host_without_HostName_connects_to_its_own_name()
    {
        var result = Import("Host build.example.com\n  User ci");

        Assert.Equal("build.example.com", Entry(result, "build.example.com").Host);
    }

    [Fact]
    public void One_Host_line_can_name_several_hosts_and_a_name_twice_makes_one_connection()
    {
        var result = Import("""
            Host a b
                User shared
            Host B
                Port 2200
            """);

        Assert.Equal(["a", "b"], Names(result));
        // Both blocks apply to b, as OpenSSH reads them.
        Assert.Equal("shared", Entry(result, "b").Settings.Username);
        Assert.Equal(2200, Entry(result, "b").Settings.Port);
    }

    [Fact]
    public void Keywords_are_case_insensitive_and_may_use_equals_signs()
    {
        var result = Import("host=web\n  HOSTNAME = web.example.com\n  user=\"me\"\r\n  port   2022 # the usual one\n");

        var web = Entry(result, "web");
        Assert.Equal("web.example.com", web.Host);
        Assert.Equal("me", web.Settings.Username);
        Assert.Equal(2022, web.Settings.Port);
    }

    // ---- Defaults ----

    [Fact]
    public void Host_star_gives_defaults_and_the_first_value_wins()
    {
        var result = Import("""
            Host web
                User deploy
            Host *
                User fallback
                Port 2200
                HostName ignored.example.com
            Host db
                HostName db.example.com
            """);

        var web = Entry(result, "web");
        Assert.Equal("deploy", web.Settings.Username);
        Assert.Equal(2200, web.Settings.Port);
        // "Host *" comes before db's own HostName and so wins, as it would in ssh.
        Assert.Equal("ignored.example.com", Entry(result, "db").Host);
        Assert.Equal("fallback", Entry(result, "db").Settings.Username);
    }

    [Fact]
    public void Settings_before_the_first_Host_apply_to_everything()
    {
        var result = Import("User everyone\nHost a\nHost b\n  User bee\n");

        Assert.Equal("everyone", Entry(result, "a").Settings.Username);
        Assert.Equal("everyone", Entry(result, "b").Settings.Username);
    }

    [Fact]
    public void Wildcard_blocks_apply_to_the_hosts_they_match_and_make_no_connection()
    {
        var result = Import("""
            Host *.corp
                User admin
                Port 2022
            Host ??-lab
                User tester
            Host web.corp ny-lab other
            """);

        Assert.Equal(["web.corp", "ny-lab", "other"], Names(result));
        Assert.Equal("admin", Entry(result, "web.corp").Settings.Username);
        Assert.Equal(2022, Entry(result, "web.corp").Settings.Port);
        Assert.Equal("tester", Entry(result, "ny-lab").Settings.Username);
        Assert.Null(Entry(result, "other").Settings.Username);
        Assert.Contains(result.Report.Skipped, s => s.Contains("*.corp") && s.Contains("??-lab"));
    }

    [Fact]
    public void Negated_patterns_exclude_a_host_from_a_block()
    {
        var result = Import("""
            Host * !secret
                User common
            Host secret
            Host open
            """);

        Assert.Null(Entry(result, "secret").Settings.Username);
        Assert.Equal("common", Entry(result, "open").Settings.Username);
    }

    [Fact]
    public void A_name_that_its_own_line_excludes_is_not_a_connection()
    {
        var result = Import("Host real fake !fake\n");

        Assert.Equal(["real"], Names(result));
    }

    // ---- Tokens and paths ----

    [Fact]
    public void HostName_expands_percent_h_and_leaves_other_tokens_noted()
    {
        var result = Import("""
            Host web
                HostName %h.internal
            Host odd
                HostName odd-%L.example.com
            """);

        Assert.Equal("web.internal", Entry(result, "web").Host);
        Assert.Equal("odd-%L.example.com", Entry(result, "odd").Host);
        Assert.Contains(result.Report.Warnings, w => w.Contains("%L") && w.Contains("HostName"));
    }

    [Fact]
    public void IdentityFile_is_kept_as_a_path_in_the_notes_and_never_read()
    {
        var keyFile = Write("id_test", "-----BEGIN OPENSSH PRIVATE KEY-----\nKEYBODY-9f3a\n-----END OPENSSH PRIVATE KEY-----\n");
        var result = Import($"""
            Host web
                HostName web.example.com
                User deploy
                IdentityFile ~/id_test
                IdentityFile {keyFile.Replace('\\', '/')}
                IdentityFile %d/keys/%r@%h
                IdentityFile none
            """);

        var web = Entry(result, "web");
        Assert.NotNull(web.Notes);
        // ~ and %d become the home folder, %r and %h the login and host; "none" is dropped.
        Assert.Equal(
            [
                "Key file: " + Path.Combine(_dir, "id_test"),
                "Key file: " + keyFile.Replace('\\', '/'),
                "Key file: " + _dir + "/keys/deploy@web.example.com",
            ],
            web.Notes.Split('\n'));
        Assert.DoesNotContain("KEYBODY", web.Notes);
        Assert.Empty(result.Credentials);
        Assert.Equal(1, result.Report.NotYetSupported.Count(l => l.StartsWith("Key files")));
        Assert.DoesNotContain("KEYBODY", result.Report.ToMarkdown(DateTimeOffset.Now));
    }

    [Fact]
    public void Unsupported_identity_tokens_and_variables_are_left_as_written_and_noted()
    {
        var result = Import("Host web\n  IdentityFile ~/.ssh/%C\n  IdentityFile ${KEYDIR}/id\n");

        Assert.Contains("%C", Entry(result, "web").Notes);
        Assert.Contains("${KEYDIR}/id", Entry(result, "web").Notes);
        Assert.Contains(result.Report.Warnings, w => w.Contains("%C"));
        Assert.Contains(result.Report.Warnings, w => w.Contains("environment variable"));
    }

    [Fact]
    public void Port_forwards_are_counted_as_not_imported()
    {
        var result = Import("Host web\n  LocalForward 8080 localhost:80\n  DynamicForward 1080\nHost db\n");

        Assert.Contains(result.Report.NotYetSupported, l => l.StartsWith("Port forwards") && l.Contains("1 connection"));
    }

    // ---- ProxyJump ----

    [Fact]
    public void A_ProxyJump_to_another_host_uses_that_connection()
    {
        var result = Import("""
            Host bastion
                HostName bastion.example.com
                User jump
            Host app
                HostName app.internal
                ProxyJump bastion
            """);

        Assert.Equal(Entry(result, "bastion").Id, Entry(result, "app").Settings.JumpHostId);
        Assert.Null(Entry(result, "bastion").Settings.JumpHostId);
        Assert.Equal(["bastion"], JumpNames(result, "app"));
        Assert.Equal(2, result.Report.Connections);
    }

    [Fact]
    public void A_ProxyJump_chain_becomes_the_matching_chain_of_jump_hosts()
    {
        var result = Import("""
            Host a
                HostName a.example.com
            Host b
                HostName b.example.com
            Host c
                HostName c.example.com
            Host target
                HostName t.example.com
                ProxyJump a,b,c
            """);

        var chain = SettingsResolver.JumpChain(Entry(result, "target"), new ConnectionTree(result.Root), out var error);
        Assert.Null(error);
        // Outermost first; a is reused, b and c are reached by a route of their own so they're new connections.
        Assert.Equal(["a.example.com", "b.example.com", "c.example.com"], chain!.Select(e => e.Host));
        Assert.Equal(["a", "b (jump host)", "c (jump host)"], chain!.Select(e => e.Name));
        Assert.Equal(Entry(result, "a").Id, chain[0].Id);
        Assert.Null(Entry(result, "b").Settings.JumpHostId);
        Assert.Equal(6, result.Report.Connections);
        Assert.Contains(result.Report.Warnings, w => w.Contains("2 jump host connections") && w.Contains("b (jump host)"));
    }

    [Fact]
    public void Jump_hosts_that_are_not_listed_become_connections_with_their_user_and_port()
    {
        var result = Import("""
            Host a
                ProxyJump deploy@gate.example.com:2222
            Host b
                ProxyJump ssh://deploy@gate.example.com:2222/
            """);

        var gate = Entry(result, "gate.example.com (jump host)");
        Assert.Equal("gate.example.com", gate.Host);
        Assert.Equal("deploy", gate.Settings.Username);
        Assert.Equal(2222, gate.Settings.Port);
        Assert.Equal(Protocol.Ssh, gate.Settings.Protocol);
        Assert.Equal(gate.Id, Entry(result, "a").Settings.JumpHostId);
        Assert.Equal(gate.Id, Entry(result, "b").Settings.JumpHostId);
        Assert.Equal(3, result.Report.Connections);
    }

    [Fact]
    public void A_jump_host_takes_its_settings_from_the_config_and_the_hop_text_overrides_them()
    {
        var result = Import("""
            Host *.example.com
                User fromstar
                Port 2200
            Host app
                ProxyJump gate.example.com,admin@gate2.example.com:22
            """);

        var gate = Entry(result, "gate.example.com (jump host)");
        Assert.Equal("fromstar", gate.Settings.Username);
        Assert.Equal(2200, gate.Settings.Port);
        var gate2 = Entry(result, "gate2.example.com (jump host)");
        Assert.Equal("admin", gate2.Settings.Username);
        Assert.Null(gate2.Settings.Port);
        Assert.Equal(["gate.example.com (jump host)", "gate2.example.com (jump host)"], JumpNames(result, "app"));
    }

    [Fact]
    public void A_first_hop_is_reached_by_its_own_ProxyJump_too()
    {
        var result = Import("""
            Host z
            Host a
                ProxyJump z
            Host t
                ProxyJump a
            """);

        Assert.Equal(["z", "a"], JumpNames(result, "t"));
        Assert.Equal(3, result.Report.Connections);
    }

    [Fact]
    public void ProxyJump_none_and_a_default_from_Host_star()
    {
        var result = Import("""
            Host bastion
                ProxyJump none
            Host direct
                ProxyJump none
            Host *
                ProxyJump bastion
            Host web
            """);

        Assert.Null(Entry(result, "bastion").Settings.JumpHostId);
        Assert.Null(Entry(result, "direct").Settings.JumpHostId);
        Assert.Equal(Entry(result, "bastion").Id, Entry(result, "web").Settings.JumpHostId);
    }

    [Fact]
    public void A_ProxyJump_that_loops_is_left_out_with_a_warning()
    {
        var result = Import("""
            Host bastion
            Host web
            Host *
                ProxyJump bastion
            """);

        Assert.Null(Entry(result, "web").Settings.JumpHostId);
        Assert.Null(Entry(result, "bastion").Settings.JumpHostId);
        Assert.Contains(result.Report.Warnings, w => w.Contains("loop") && w.Contains("without a jump host"));
    }

    [Fact]
    public void A_host_that_jumps_through_itself_or_around_in_a_circle_is_left_without_a_jump_host()
    {
        var result = Import("Host a\n  ProxyJump b\nHost b\n  ProxyJump a\nHost c\n  ProxyJump c\n");

        Assert.All(new[] { "a", "b", "c" }, name => Assert.Null(Entry(result, name).Settings.JumpHostId));
        Assert.Equal(3, result.Report.Warnings.Count(w => w.Contains("loop")));
    }

    [Fact]
    public void More_jump_hosts_than_Orrery_follows_is_refused_for_that_host()
    {
        var result = Import("Host far\n  ProxyJump h1,h2,h3,h4,h5,h6\nHost near\n  ProxyJump h1,h2,h3,h4,h5\n");

        Assert.Null(Entry(result, "far").Settings.JumpHostId);
        Assert.Contains(result.Report.Warnings, w => w.Contains("far") && w.Contains("more than 5"));
        Assert.Equal(5, JumpNames(result, "near").Count);
    }

    [Fact]
    public void An_unreadable_ProxyJump_is_left_out_with_a_warning_and_the_host_is_kept()
    {
        var result = Import("Host a\n  ProxyJump host:notaport\nHost b\n  ProxyJump @host\nHost c\n  ProxyJump bad;host\n");

        Assert.Equal(["a", "b", "c"], Names(result));
        Assert.All(new[] { "a", "b", "c" }, name => Assert.Null(Entry(result, name).Settings.JumpHostId));
        Assert.Equal(3, result.Report.Warnings.Count(w => w.Contains("without a jump host")));
    }

    // ---- Include ----

    [Fact]
    public void Include_follows_globs_in_name_order_relative_to_the_config_folder()
    {
        Write("conf.d/10-b.conf", "Host shared\n  User from-b\nHost only-b\n");
        Write("conf.d/05-a.conf", "Host shared\n  User from-a\nHost only-a\n");
        Write("conf.d/.hidden.conf", "Host hidden\n");
        Write("conf.d/notes.txt", "Host text\n");
        var result = Import("Include conf.d/*.conf\nHost last\n");

        Assert.Equal(["shared", "only-a", "only-b", "last"], Names(result));
        Assert.Equal("from-a", Entry(result, "shared").Settings.Username);
    }

    [Fact]
    public void Include_accepts_tilde_absolute_paths_and_several_patterns_and_nests()
    {
        var absolute = Write("elsewhere/abs.conf", "Host from-abs\n");
        Write("home.conf", "Host from-home\nInclude nested.conf\n");
        Write("nested.conf", "Host from-nested\n");
        var result = Import($"Include ~/home.conf \"{absolute}\"\n");

        Assert.Equal(["from-home", "from-nested", "from-abs"], Names(result));
    }

    [Fact]
    public void Include_inside_a_Host_block_starts_in_that_block()
    {
        Write("extra.conf", "User from-extra\nHost second\n");
        var result = Import("Host first\n  Include extra.conf\nHost third\n");

        Assert.Equal("from-extra", Entry(result, "first").Settings.Username);
        Assert.Null(Entry(result, "second").Settings.Username);
        Assert.Null(Entry(result, "third").Settings.Username);
    }

    [Fact]
    public void Include_cycles_are_cut_with_a_warning()
    {
        Write("a.conf", "Host from-a\nInclude b.conf\n");
        Write("b.conf", "Host from-b\nInclude a.conf\n");
        var result = Import("Include a.conf\nHost main\n");

        Assert.Equal(["from-a", "from-b", "main"], Names(result));
        Assert.Contains(result.Report.Warnings, w => w.Contains("loops back"));
    }

    [Fact]
    public void A_file_that_includes_itself_is_read_once()
    {
        var result = Import("Host one\nInclude config\n");

        Assert.Equal(["one"], Names(result));
        Assert.Contains(result.Report.Warnings, w => w.Contains("loops back"));
    }

    [Fact]
    public void Include_nested_too_deep_stops_at_the_limit()
    {
        for (int i = 0; i < SshConfig.MaxIncludeDepth + 4; i++)
        {
            Write($"f{i}.conf", $"Host h{i}\nInclude f{i + 1}.conf\n");
        }

        var result = Import("Include f0.conf\n");

        Assert.Equal(SshConfig.MaxIncludeDepth, Names(result).Count());
        Assert.Contains(result.Report.Warnings, w => w.Contains("deep"));
    }

    [Fact]
    public void Include_that_matches_nothing_or_uses_variables_is_noted()
    {
        var result = Import("Include nothing/*.conf\nInclude ${HOME}/x\nInclude ~someone/x\nInclude %L/x\nHost a\n");

        Assert.Equal(["a"], Names(result));
        Assert.Contains(result.Report.Warnings, w => w.Contains("matched no files"));
        Assert.Contains(result.Report.Warnings, w => w.Contains("environment variable"));
        Assert.Equal(2, result.Report.Warnings.Count(w => w.Contains("path form Orrery doesn't expand")));
    }

    [Fact]
    public void Too_many_included_files_are_refused()
    {
        for (int i = 0; i < SshConfig.MaxFiles + 1; i++)
        {
            Write($"many/{i:D4}.conf", "# nothing\n");
        }

        var e = Assert.Throws<ImportException>(() => Import("Include many/*.conf\n"));
        Assert.Contains("includes more than", e.Message);
    }

    [Fact]
    public void Relative_includes_start_from_the_ssh_folder_for_a_config_inside_it_and_its_own_folder_otherwise()
    {
        var ssh = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh");
        Assert.Equal(ssh, SshConfigEnvironment.For(Path.Combine(ssh, "config")).IncludeBase);
        Assert.Equal(ssh, SshConfigEnvironment.For(Path.Combine(ssh, "sub", "config")).IncludeBase);
        Assert.Equal(_dir, SshConfigEnvironment.For(Path.Combine(_dir, "config")).IncludeBase);
    }

    // ---- Match and commands ----

    [Fact]
    public void Match_blocks_are_skipped_counted_and_do_not_leak_into_hosts()
    {
        var result = Import("""
            Host a
                Port 2201
            Match host a
                User from-match
                Port 9999
            Match all
                ProxyJump nowhere
            Host b
            """);

        Assert.Equal(["a", "b"], Names(result));
        Assert.Null(Entry(result, "a").Settings.Username);
        Assert.Equal(2201, Entry(result, "a").Settings.Port);
        Assert.Null(Entry(result, "a").Settings.JumpHostId);
        Assert.Contains(result.Report.Skipped, s => s.StartsWith("2 Match blocks"));
    }

    [Fact]
    public void Commands_are_reported_never_run_and_the_host_is_still_imported()
    {
        var result = Import("""
            Host web db
                ProxyCommand ssh -W %h:%p gate-token-ZX81
                LocalCommand notify-send secret-token-Q7
                KnownHostsCommand /usr/bin/fetch-keys %H
            Host direct
                ProxyCommand none
            Host *
                ProxyCommand later-ignored
            """);

        Assert.Equal(["web", "db", "direct"], Names(result));
        Assert.Contains(result.Report.Skipped, s => s.StartsWith("ProxyCommand (config, line 2) is never run") && s.Contains("web, db"));
        Assert.Contains(result.Report.Skipped, s => s.StartsWith("LocalCommand"));
        Assert.Contains(result.Report.Skipped, s => s.StartsWith("KnownHostsCommand"));
        // "ProxyCommand none" wins over the later default, so nothing is reported for it.
        Assert.DoesNotContain(result.Report.Skipped, s => s.Contains("direct"));

        var everything = string.Join("\n", result.Report.Skipped.Concat(result.Report.Warnings).Concat(result.Report.NotYetSupported))
            + result.Report.ToMarkdown(DateTimeOffset.Now);
        Assert.DoesNotContain("gate-token-ZX81", everything);
        Assert.DoesNotContain("secret-token-Q7", everything);
        Assert.DoesNotContain("fetch-keys", everything);
    }

    // ---- Re-importing ----

    [Fact]
    public void Importing_again_skips_what_is_there_and_points_new_hosts_at_the_existing_jump_hosts()
    {
        var first = Import("Host bastion\n  HostName b.example.com\nHost app\n  ProxyJump bastion\n");
        var tree = ConnectionTree.Empty();
        tree.AddFrom(first.Root, tree.Root, includeSourceFolder: true);

        var second = Import("Host bastion\n  HostName b.example.com\nHost app\n  ProxyJump bastion\nHost db\n  ProxyJump bastion\n");
        Assert.Equal(2, second.CountAlreadyIn(tree));
        var fresh = second.WithoutAlreadyIn(tree);

        Assert.Equal(["db"], Names(fresh));
        var existingBastion = tree.AllConnections().Single(e => e.Name == "bastion");
        Assert.Equal(existingBastion.Id, Entry(fresh, "db").Settings.JumpHostId);

        tree.AddFrom(fresh.Root, tree.Root, includeSourceFolder: false);
        var chain = SettingsResolver.JumpChain(tree.AllConnections().Single(e => e.Name == "db"), tree, out var error);
        Assert.Null(error);
        Assert.Equal(["bastion"], chain!.Select(e => e.Name));
    }

    // ---- Nasty input ----

    [Fact]
    public void A_file_over_the_size_cap_is_refused()
    {
        var path = Write("config", string.Empty);
        using (var stream = File.OpenWrite(path))
        {
            stream.SetLength(SshConfig.MaxFileBytes + 1);
        }

        var e = Assert.Throws<ImportException>(() => SshConfigImporter.ImportFile(path, Env));
        Assert.Contains("larger than", e.Message);
    }

    [Fact]
    public void A_binary_file_is_refused_and_a_binary_include_is_skipped()
    {
        var path = Path.Combine(_dir, "config");
        File.WriteAllBytes(path, [0x7F, 0x45, 0x4C, 0x46, 0, 0, 0, 1, 2, 3]);
        Assert.Throws<ImportException>(() => SshConfigImporter.ImportFile(path, Env));

        File.WriteAllBytes(Path.Combine(_dir, "bin.conf"), [1, 0, 2, 0, 3]);
        var result = Import("Include bin.conf\nHost ok\n");
        Assert.Equal(["ok"], Names(result));
        Assert.Contains(result.Report.Warnings, w => w.Contains("doesn't look like a text file"));
    }

    [Fact]
    public void Utf16_with_a_byte_order_mark_and_a_utf8_bom_are_read()
    {
        File.WriteAllText(Path.Combine(_dir, "utf16"), "Host wide\n  User w\n", Encoding.Unicode);
        Assert.Equal(["wide"], Names(SshConfigImporter.ImportFile(Path.Combine(_dir, "utf16"), Env)));

        File.WriteAllText(Path.Combine(_dir, "utf8"), "Host bom\n  User b\n", new UTF8Encoding(true));
        var result = SshConfigImporter.ImportFile(Path.Combine(_dir, "utf8"), Env);
        Assert.Equal("b", Entry(result, "bom").Settings.Username);
    }

    [Fact]
    public void Broken_lines_are_skipped_without_quoting_them()
    {
        var result = Import(string.Join('\n',
            "Host good", "    User \"unclosed-quote-secret", "    Port 2222", "HostName", "Host", "!!!", "Host   =",
            "Host long", "    User " + new string('x', SshConfig.MaxLineLength + 10), string.Empty));

        Assert.Equal(["good", "long"], Names(result));
        Assert.Equal(2222, Entry(result, "good").Settings.Port);
        Assert.Null(Entry(result, "good").Settings.Username);
        Assert.Null(Entry(result, "long").Settings.Username);
        Assert.Contains(result.Report.Warnings, w => w.Contains("unclosed quote"));
        Assert.Contains(result.Report.Warnings, w => w.Contains("longer than Orrery reads"));
        Assert.DoesNotContain(result.Report.Warnings, w => w.Contains("unclosed-quote-secret"));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("65536")]
    [InlineData("-1")]
    [InlineData("22abc")]
    [InlineData("99999999999999999999")]
    public void An_invalid_port_is_noted_and_the_default_used(string port)
    {
        var result = Import($"Host web\n  Port {port}\n");

        Assert.Null(Entry(result, "web").Settings.Port);
        Assert.Contains(result.Report.Warnings, w => w.Contains("1 to 65535"));
    }

    [Theory]
    [InlineData("-oProxyCommand=evil")]
    [InlineData("bad;host")]
    [InlineData("a/b")]
    [InlineData("user@host")]
    [InlineData("$(whoami)")]
    public void A_HostName_that_cannot_be_a_host_skips_the_connection(string hostName)
    {
        var result = Import($"Host web\n  HostName {hostName}\nHost fine\n");

        Assert.Equal(["fine"], Names(result));
        Assert.Contains(result.Report.Skipped, s => s.StartsWith("web:"));
    }

    [Fact]
    public void A_host_name_with_a_space_in_it_is_skipped()
    {
        var result = Import("Host \"my host\"\nHost ok\n");

        Assert.Equal(["ok"], Names(result));
        Assert.Contains(result.Report.Skipped, s => s.StartsWith("my host:"));
    }

    [Fact]
    public void More_hosts_than_the_cap_are_refused()
    {
        var text = string.Concat(Enumerable.Range(0, SshConfig.MaxHosts + 1).Select(i => $"Host h{i}\n"));

        var e = Assert.Throws<ImportException>(() => Import(text));
        Assert.Contains("more than", e.Message);
    }

    [Fact]
    public void More_blocks_than_the_cap_are_refused()
    {
        var text = string.Concat(Enumerable.Range(0, SshConfig.MaxBlocks + 2).Select(i => $"Host *.p{i}\n"));

        Assert.Throws<ImportException>(() => Import(text));
    }

    [Fact]
    public void Very_long_runs_of_wildcards_match_quickly()
    {
        var timer = Stopwatch.StartNew();
        Assert.False(SshConfig.GlobMatch(string.Concat(Enumerable.Repeat("*a", 100)) + "b", new string('a', 255)));
        Assert.True(SshConfig.GlobMatch("*a*a*a*", "xaxaxax"));
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(2), $"took {timer.Elapsed}");

        var result = Import("Host " + string.Join(' ', Enumerable.Repeat("*a*a*a*a*a*a*a*b", 500)) + "\nHost " + new string('a', 200) + "\n");
        Assert.Single(Names(result));
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(5), $"took {timer.Elapsed}");
    }

    [Fact]
    public void Control_and_direction_override_characters_in_a_host_name_skip_the_connection()
    {
        var result = Import("Host\tbell\u0007\nHost \u202Eevil\nHost fine\n  HostName fine.example.com\n");

        Assert.Equal(["fine"], Names(result));
        Assert.Equal(2, result.Report.Skipped.Count(s => s.Contains("isn't a usable host name")));
    }

    // ---- Syntax ----

    [Fact]
    public void Lines_split_like_OpenSSH_does()
    {
        Assert.True(SshConfig.TrySplit("Host=foo bar", out var k, out var a));
        Assert.Equal("host", k);
        Assert.Equal(["foo", "bar"], a);

        Assert.True(SshConfig.TrySplit("IdentityFile \"C:\\Users\\me\\my key\" # comment", out k, out a));
        Assert.Equal(["C:\\Users\\me\\my key"], a);

        Assert.True(SshConfig.TrySplit("User \"a \\\"quoted\\\" name\"", out _, out a));
        Assert.Equal(["a \"quoted\" name"], a);

        Assert.True(SshConfig.TrySplit("Host a#b #c", out _, out a));
        Assert.Equal(["a#b"], a);

        Assert.False(SshConfig.TrySplit("User \"open", out _, out _));
    }

    [Fact]
    public void LooksLikeConfig_spots_ssh_config_names()
    {
        Assert.True(SshConfigImporter.LooksLikeConfig("/home/me/.ssh/config"));
        Assert.True(SshConfigImporter.LooksLikeConfig("ssh_config"));
        Assert.True(SshConfigImporter.LooksLikeConfig("work.conf"));
        Assert.False(SshConfigImporter.LooksLikeConfig("connections.json"));
        Assert.False(SshConfigImporter.LooksLikeConfig("notes.txt"));
    }

    // ---- A realistic file ----

    [Fact]
    public void A_realistic_config_with_includes_imports_as_expected()
    {
        var fixture = Path.Combine(AppContext.BaseDirectory, "Import", "Fixtures", "ssh-config");
        var env = new SshConfigEnvironment(fixture, "local", fixture);

        var result = SshConfigImporter.ImportFile(Path.Combine(fixture, "config"), env);

        // The Include comes first in the file, so lab comes first.
        Assert.Equal(["lab", "bastion", "web1", "web2", "db", "oldbox"], Names(result));
        Assert.Equal(["bastion"], JumpNames(result, "web1"));
        Assert.Equal(["bastion", "web1"], JumpNames(result, "db"));
        Assert.Equal("web1.internal.example.com", Entry(result, "web1").Host);
        Assert.Equal("ops", Entry(result, "web2").Settings.Username);
        Assert.Equal("defaultuser", Entry(result, "web1").Settings.Username);
        Assert.Equal(2222, Entry(result, "bastion").Settings.Port);
        Assert.Equal("lab.example.org", Entry(result, "lab").Host);
        Assert.Contains(result.Report.Skipped, s => s.StartsWith("1 Match block"));
        Assert.Contains(result.Report.Skipped, s => s.StartsWith("ProxyCommand") && s.Contains("oldbox"));
        Assert.Contains(result.Report.Skipped, s => s.Contains("*.example.org"));
        Assert.Contains(result.Report.NotYetSupported, l => l.StartsWith("Key files"));
        Assert.Empty(result.Credentials);
    }
}

using Orrery.Core.Connections;
using Orrery.Core.Sessions;
using Xunit;

namespace Orrery.Core.Tests.Connections;

/// <summary>Connections with guests: the tree's rules and the file. See docs/design-host-groups.md.</summary>
public class HostGroupTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "orrery-tests-" + Guid.NewGuid().ToString("N"));

    // Lab / hv-01 (guests vm-a, vm-b), then other-01 beside it
    static (ConnectionTree Tree, ConnectionFolder Lab, ConnectionEntry Host, ConnectionEntry VmA, ConnectionEntry VmB, ConnectionEntry Other) Sample()
    {
        var tree = ConnectionTree.Empty();
        var lab = new ConnectionFolder { Name = "Lab" };
        var host = new ConnectionEntry { Name = "hv-01", Host = "hv-01.lab" };
        var vmA = new ConnectionEntry { Name = "vm-a", Host = "vm-a.lab" };
        var vmB = new ConnectionEntry { Name = "vm-b", Host = "vm-b.lab" };
        var other = new ConnectionEntry { Name = "other-01", Host = "other-01.lab" };
        tree.Add(lab, tree.Root);
        tree.Add(host, lab);
        tree.Add(other, lab);
        tree.AddGuest(vmA, host);
        tree.AddGuest(vmB, host);
        return (tree, lab, host, vmA, vmB, other);
    }

    ConnectionStore Store() => new(Path.Combine(_dir, "connections.json"), Path.Combine(_dir, "backups"));

    [Fact]
    public void A_guest_sits_in_its_hosts_folder_and_is_found_after_its_host()
    {
        var (tree, lab, host, vmA, vmB, other) = Sample();

        Assert.True(host.IsHost);
        Assert.Equal([vmA, vmB], host.Guests);
        Assert.Same(lab, vmA.Parent);
        Assert.Same(host, vmA.GuestOf);
        Assert.Equal([host, other], lab.Children);
        Assert.Equal([lab, host, vmA, vmB, other], tree.Root.Descendants());
        Assert.Same(vmB, tree.Find(vmB.Id));
        Assert.Equal("Lab / hv-01", vmA.Path);
        Assert.Equal("Lab", host.Path);
    }

    [Fact]
    public void One_level_only()
    {
        var (tree, lab, host, vmA, _, other) = Sample();

        Assert.Throws<InvalidOperationException>(() => tree.AddGuest(new ConnectionEntry(), vmA));
        Assert.Throws<InvalidOperationException>(() => tree.MoveToHost(host, other));
        Assert.Throws<InvalidOperationException>(() => tree.MoveToHost(other, other));
        Assert.Throws<InvalidOperationException>(() => tree.AddGuest(other, host));
        Assert.Throws<InvalidOperationException>(() => tree.AddGuest(new ConnectionEntry(), new ConnectionEntry()));
        Assert.Equal(2, host.Guests.Count);
        Assert.False(other.IsHost);
    }

    [Fact]
    public void Moving_a_host_takes_its_guests_along()
    {
        var (tree, _, host, vmA, vmB, _) = Sample();
        var archive = new ConnectionFolder { Name = "Archive" };
        tree.Add(archive, tree.Root);

        tree.Move(host, archive);

        Assert.Same(archive, vmA.Parent);
        Assert.Same(archive, vmB.Parent);
        Assert.Equal("Archive / hv-01", vmB.Path);
    }

    [Fact]
    public void A_guest_moved_out_goes_right_after_its_host_and_the_last_one_out_leaves_a_plain_connection()
    {
        var (tree, lab, host, vmA, vmB, other) = Sample();

        tree.MoveOutOfHost(vmA);
        Assert.Null(vmA.GuestOf);
        Assert.Equal([host, vmA, other], lab.Children);
        Assert.Equal([vmB], host.Guests);

        tree.Move(vmB, tree.Root);
        Assert.Null(vmB.GuestOf);
        Assert.Same(tree.Root, vmB.Parent);
        Assert.False(host.IsHost);
        Assert.Empty(host.Guests);
    }

    [Fact]
    public void A_connection_can_become_a_guest_and_guests_can_be_reordered_or_change_host()
    {
        var (tree, lab, host, vmA, vmB, other) = Sample();

        tree.MoveToHost(other, host, 0);
        Assert.Equal([other, vmA, vmB], host.Guests);
        Assert.Equal([host], lab.Children);
        Assert.Same(lab, other.Parent);

        tree.MoveToHost(other, host, 3);
        Assert.Equal([vmA, vmB, other], host.Guests);

        var host2 = new ConnectionEntry { Name = "hv-02" };
        tree.Add(host2, tree.Root);
        tree.MoveToHost(vmA, host2);
        Assert.Same(host2, vmA.GuestOf);
        Assert.Same(tree.Root, vmA.Parent);
        Assert.Equal([vmB, other], host.Guests);
    }

    [Fact]
    public void Removing_a_host_removes_its_guests_and_removing_a_guest_leaves_the_host()
    {
        var (tree, _, host, vmA, vmB, _) = Sample();

        tree.Remove(vmA);
        Assert.Null(tree.Find(vmA.Id));
        Assert.Equal([vmB], host.Guests);

        tree.Remove(host);
        Assert.Null(tree.Find(host.Id));
        Assert.Null(tree.Find(vmB.Id));
    }

    [Fact]
    public void Duplicating_a_host_copies_its_guests_and_a_guests_copy_stays_with_the_host()
    {
        var (tree, lab, host, vmA, vmB, _) = Sample();

        var copy = Assert.IsType<ConnectionEntry>(tree.Duplicate(host));
        Assert.Equal("hv-01 (copy)", copy.Name);
        Assert.Same(lab, copy.Parent);
        Assert.Equal(["vm-a", "vm-b"], copy.Guests.Select(g => g.Name));
        Assert.All(copy.Guests, g => Assert.Same(copy, g.GuestOf));
        Assert.All(copy.Guests, g => Assert.Same(lab, g.Parent));
        Assert.DoesNotContain(copy.Guests, g => g.Id == vmA.Id || g.Id == vmB.Id);

        var guestCopy = Assert.IsType<ConnectionEntry>(tree.Duplicate(vmA));
        Assert.Same(host, guestCopy.GuestOf);
        Assert.Equal([vmA, guestCopy, vmB], host.Guests);
    }

    [Fact]
    public void Duplicating_a_folder_puts_its_hosts_guests_in_the_copy_so_they_take_its_settings()
    {
        var (tree, lab, _, _, _, _) = Sample();
        lab.Settings.Protocol = Protocol.Ssh;

        var copy = Assert.IsType<ConnectionFolder>(tree.Duplicate(lab));

        var hostCopy = copy.Children.OfType<ConnectionEntry>().Single(e => e.IsHost);
        Assert.All(hostCopy.Guests, g => Assert.Same(copy, g.Parent));
        Assert.All(hostCopy.Guests, g => Assert.Equal(Protocol.Ssh, SettingsResolver.Resolve(g).Protocol.Value));
        Assert.Equal("Lab (copy) / hv-01", hostCopy.Guests[0].Path);
    }

    [Fact]
    public void Bringing_in_only_a_folders_contents_gives_a_top_level_hosts_guests_what_they_took_from_it()
    {
        var source = ConnectionTree.Empty();
        source.Root.Settings.Protocol = Protocol.Ssh;
        source.Root.Settings.Username = "ops";
        source.Root.Settings.ShareClipboard = false;
        var host = new ConnectionEntry { Name = "hv-01", Host = "hv-01.lab" };
        var vm = new ConnectionEntry { Name = "vm", Host = "vm.lab", Settings = { Username = "me" } };
        source.Add(host, source.Root);
        source.AddGuest(vm, host);
        var tree = ConnectionTree.Empty();
        tree.Root.Settings.Protocol = Protocol.Vnc;

        tree.AddFrom(source.Root, tree.Root, includeSourceFolder: false);

        var resolved = SettingsResolver.Resolve(vm);
        Assert.Equal(Protocol.Ssh, resolved.Protocol.Value);
        Assert.Equal("me", resolved.Username.Value);
        Assert.False(resolved.ShareClipboard.Value);
        Assert.Same(host, resolved.ShareClipboard.Source);
        Assert.Null(vm.Settings.ShareClipboard);
    }

    [Fact]
    public void Bringing_in_a_folder_with_hosts_keeps_the_guests_with_them()
    {
        var (source, _, _, _, _, _) = Sample();
        var detached = source.Root;
        var tree = ConnectionTree.Empty();

        tree.AddFrom(detached, tree.Root, includeSourceFolder: false);

        var lab = Assert.IsType<ConnectionFolder>(tree.Root.Children.Single());
        var host = lab.Children.OfType<ConnectionEntry>().Single(e => e.IsHost);
        Assert.All(host.Guests, g => Assert.Same(lab, g.Parent));
    }

    [Fact]
    public void A_tree_without_hosts_is_saved_as_schema_1_and_one_with_hosts_as_schema_2()
    {
        var (tree, _, host, vmA, vmB, _) = Sample();
        var store = Store();
        var path = Path.Combine(_dir, "connections.json");
        host.GuestsCollapsed = true;

        store.Save(tree);
        var text = File.ReadAllText(path);
        Assert.Contains("\"schemaVersion\": 2", text);
        Assert.Contains("\"guests\"", text);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(text, "guestsCollapsed"));

        var loaded = store.Load();
        var loadedHost = Assert.IsType<ConnectionEntry>(loaded.Find(host.Id));
        Assert.True(loadedHost.GuestsCollapsed);
        Assert.Equal([vmA.Id, vmB.Id], loadedHost.Guests.Select(g => g.Id));
        Assert.All(loadedHost.Guests, g => Assert.Same(loadedHost, g.GuestOf));
        Assert.All(loadedHost.Guests, g => Assert.Same(loadedHost.Parent, g.Parent));

        tree.Move(vmA, tree.Root);
        tree.Move(vmB, tree.Root);
        store.Save(tree);
        text = File.ReadAllText(path);
        Assert.Contains("\"schemaVersion\": 1", text);
        Assert.DoesNotContain("guests\"", text);
    }

    [Theory]
    [InlineData("""{"type":"connection","name":"vm","guests":[{"name":"nested"}]}""")]
    [InlineData("null")]
    public void A_file_with_a_guest_that_has_guests_or_an_empty_guest_is_refused(string guest)
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "connections.json"),
            $$$"""{"schemaVersion":2,"root":{"type":"folder","name":"Connections","children":[{"type":"connection","name":"hv","guests":[{{{guest}}}]}]}}""");

        Assert.Throws<InvalidDataException>(() => Store().Load());
    }

    [Fact]
    public void A_file_from_a_newer_schema_is_still_refused()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "connections.json"), """{"schemaVersion":5,"root":{"type":"folder","name":"Connections"}}""");

        Assert.Throws<InvalidDataException>(() => Store().Load());
    }

    [Fact]
    public void Every_setting_is_either_passed_to_guests_or_kept_by_them()
    {
        var settings = typeof(InheritableSettings).GetProperties().Where(p => p.CanWrite).Select(p => p.Name).ToHashSet();

        Assert.Empty(InheritableSettings.PassedToGuests.Intersect(InheritableSettings.KeptByGuests));
        Assert.True(settings.SetEquals(InheritableSettings.PassedToGuests.Union(InheritableSettings.KeptByGuests)),
            "Unclassified: " + string.Join(", ", settings.Except(InheritableSettings.PassedToGuests).Except(InheritableSettings.KeptByGuests)));
    }

    [Fact]
    public void A_guest_takes_jump_host_clipboard_and_reconnect_from_its_host_before_its_folder()
    {
        var (tree, lab, host, vmA, _, other) = Sample();
        var bastion = new ConnectionEntry { Name = "bastion", Host = "bastion.lab", Settings = { Protocol = Protocol.Ssh } };
        tree.Add(bastion, tree.Root);
        lab.Settings.ShareClipboard = true;
        lab.Settings.AutoReconnect = true;
        host.Settings.JumpHostId = bastion.Id;
        host.Settings.ShareClipboard = false;
        host.Settings.AutoReconnect = false;

        var resolved = SettingsResolver.Resolve(vmA);
        Assert.Equal(bastion.Id, resolved.JumpHostId.Value);
        Assert.Same(host, resolved.JumpHostId.Source);
        Assert.False(resolved.ShareClipboard.Value);
        Assert.Same(host, resolved.ShareClipboard.Source);
        Assert.False(resolved.AutoReconnect.Value);

        // Not a guest, so the host's values don't reach it.
        Assert.True(SettingsResolver.Resolve(other).ShareClipboard.Value);
        Assert.Null(SettingsResolver.Resolve(other).JumpHostId.Value);

        // The guest's own value wins; the folder's is used when the host sets nothing.
        vmA.Settings.ShareClipboard = true;
        Assert.True(SettingsResolver.Resolve(vmA).ShareClipboard.Value);
        host.Settings.AutoReconnect = null;
        Assert.Same(lab, SettingsResolver.Resolve(vmA).AutoReconnect.Source);

        Assert.Equal([bastion], SettingsResolver.JumpChain(vmA, tree, out var error));
        Assert.Null(error);
    }

    [Fact]
    public void A_guest_keeps_its_own_login_protocol_and_display_settings_and_skips_the_host_for_them()
    {
        var (_, lab, host, vmA, _, _) = Sample();
        lab.Settings.Domain = "LAB";
        host.Settings.Protocol = Protocol.Ssh;
        host.Settings.Port = 2222;
        host.Settings.CredentialId = Guid.NewGuid();
        host.Settings.Username = "root";
        host.Settings.Domain = "HOST";
        host.Settings.UseSshAgent = false;
        host.Settings.AllowLegacySsh = true;
        host.Settings.DesktopSize = "1280x720";
        host.Settings.EnlargeRemoteDesktop = false;
        host.Settings.DisplayQuality = DisplayQuality.Fastest;

        var resolved = SettingsResolver.Resolve(vmA);
        Assert.Equal(Protocol.Rdp, resolved.Protocol.Value);
        Assert.Equal(3389, resolved.Port.Value);
        Assert.Null(resolved.CredentialId.Value);
        Assert.Null(resolved.Username.Value);
        Assert.Equal("LAB", resolved.Domain.Value);
        Assert.Same(lab, resolved.Domain.Source);
        Assert.True(resolved.UseSshAgent.Value);
        Assert.False(resolved.AllowLegacySsh.Value);
        Assert.Null(resolved.DesktopSize.Value);
        Assert.Null(resolved.EnlargeRemoteDesktop.Value);
        Assert.Equal(DisplayQuality.Balanced, resolved.DisplayQuality.Value);
    }

    [Fact]
    public void What_a_guest_would_inherit_includes_its_host()
    {
        var (_, lab, host, vmA, _, _) = Sample();
        lab.Settings.ShareClipboard = true;
        host.Settings.ShareClipboard = false;
        vmA.Settings.ShareClipboard = true;

        var inherited = SettingsResolver.ResolveInherited(vmA);
        Assert.False(inherited.ShareClipboard.Value);
        Assert.Same(host, inherited.ShareClipboard.Source);
        Assert.Same(lab, SettingsResolver.ResolveInherited(host).ShareClipboard.Source);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }
}

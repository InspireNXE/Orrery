using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Orrery.Core.Connections;
using Orrery.Core.Storage;
using Xunit;

namespace Orrery.Core.Tests.Storage;

/// <summary>Runs on Windows only: shows as skipped on Linux and macOS, where packet 04's Unix modes apply instead.</summary>
sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = "Windows ACLs: Windows only";
        }
    }
}

/// <summary>
/// Every test works under its own folder in the temp directory, which is first opened to <c>BUILTIN\Users</c> the way
/// <c>C:\</c> or a second drive is, so a folder that merely inherits its parent's ACL shows up as open.
/// </summary>
[SupportedOSPlatform("windows")]
public class FolderAccessTests : IDisposable
{
    static readonly SecurityIdentifier Users = new(WellKnownSidType.BuiltinUsersSid, null);
    static readonly SecurityIdentifier AuthenticatedUsers = new(WellKnownSidType.AuthenticatedUserSid, null);
    static readonly SecurityIdentifier[] Allowed =
    [
        WindowsIdentity.GetCurrent().User!,
        new(WellKnownSidType.LocalSystemSid, null),
        new(WellKnownSidType.BuiltinAdministratorsSid, null),
    ];

    readonly string _root = Path.Combine(Path.GetTempPath(), "orrery-tests-" + Guid.NewGuid().ToString("N"));

    public FolderAccessTests()
    {
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(_root);
            Grant(_root, Users, FileSystemRights.Modify, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None);
        }
    }

    string P(params string[] parts) => Path.Combine([_root, .. parts]);

    [WindowsFact]
    public void A_new_folder_is_protected_and_only_the_user_SYSTEM_and_Administrators_have_access()
    {
        var dir = P("data");

        FolderAccess.CreateProtected(dir);

        var security = new DirectoryInfo(dir).GetAccessControl();
        Assert.True(security.AreAccessRulesProtected, "inheritance from the parent is off");
        var sids = Rules(security).Select(r => (SecurityIdentifier)r.IdentityReference).Distinct().ToList();
        Assert.All(sids, sid => Assert.Contains(sid, Allowed));
        Assert.Contains(WindowsIdentity.GetCurrent().User!, sids);
        Assert.False(FolderAccess.IsOpenToOthers(dir));
    }

    [WindowsFact]
    public void A_file_saved_in_a_new_folder_gives_no_access_to_other_users()
    {
        var file = P("data", "connections.json");
        FolderAccess.CreateProtected(P("data"));

        AtomicFile.WriteAllText(file, "{}");

        AssertClosed(new FileInfo(file).GetAccessControl());
    }

    [WindowsFact]
    public void Missing_parents_are_created_protected_too()
    {
        FolderAccess.CreateProtected(P("outer", "inner"));

        AssertClosed(new DirectoryInfo(P("outer")).GetAccessControl());
        AssertClosed(new DirectoryInfo(P("outer", "inner")).GetAccessControl());
    }

    [WindowsFact]
    public void An_existing_folder_keeps_its_acl()
    {
        var dir = P("picked");
        Directory.CreateDirectory(dir);
        var before = new DirectoryInfo(dir).GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.All);

        FolderAccess.CreateProtected(dir);
        using (DataFolderLock.TryAcquire(dir))
        {
        }

        Assert.Equal(before, new DirectoryInfo(dir).GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.All));
        Assert.True(FolderAccess.IsOpenToOthers(dir));
    }

    [WindowsFact]
    public void A_folder_other_users_can_modify_is_open_and_a_protected_one_is_not()
    {
        var open = P("open");
        var closed = P("closed");
        FolderAccess.CreateProtected(open);
        FolderAccess.CreateProtected(closed);
        Grant(open, Users, FileSystemRights.Modify, InheritanceFlags.None, PropagationFlags.None);

        Assert.True(FolderAccess.IsOpenToOthers(open));
        Assert.False(FolderAccess.IsOpenToOthers(closed));
    }

    [WindowsFact]
    public void Read_access_and_inherit_only_access_that_never_reaches_files_do_not_count()
    {
        var dir = P("data");
        FolderAccess.CreateProtected(dir);
        Grant(dir, Users, FileSystemRights.ReadAndExecute, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None);
        Grant(dir, AuthenticatedUsers, FileSystemRights.Modify, InheritanceFlags.ContainerInherit, PropagationFlags.InheritOnly);

        Assert.False(FolderAccess.IsOpenToOthers(dir));

        Grant(dir, AuthenticatedUsers, FileSystemRights.Modify, InheritanceFlags.ObjectInherit, PropagationFlags.InheritOnly);
        Assert.True(FolderAccess.IsOpenToOthers(dir), "an inherit-only entry that files written later pick up counts");
    }

    [WindowsFact]
    public void Folders_Orrery_creates_for_its_data_are_protected()
    {
        AtomicFile.WriteAllText(P("saved", "settings.json"), "{}");
        Assert.True(DataLocationResolver.IsWritable(P("probed")));
        DataFolderLock.TryAcquire(P("locked"))!.Dispose();

        foreach (var dir in new[] { "saved", "probed", "locked" })
        {
            Assert.False(FolderAccess.IsOpenToOthers(P(dir)), dir);
        }

        AssertClosed(new FileInfo(P("saved", "settings.json")).GetAccessControl());
    }

    [WindowsFact]
    public void Backups_get_a_protected_folder_even_in_a_data_folder_the_user_picked()
    {
        var picked = P("picked");
        Directory.CreateDirectory(picked);
        var store = new ConnectionStore(Path.Combine(picked, "connections.json"), Path.Combine(picked, "backups"), backupInterval: TimeSpan.Zero);

        var tree = ConnectionTree.Empty();
        store.Save(tree);
        tree.Add(new ConnectionEntry { Name = "web", Host = "web.example" }, tree.Root);
        store.Save(tree);

        var backup = Assert.Single(Directory.GetFiles(Path.Combine(picked, "backups")));
        Assert.False(FolderAccess.IsOpenToOthers(Path.Combine(picked, "backups")));
        AssertClosed(new FileInfo(backup).GetAccessControl());
    }

    [WindowsFact]
    public void Move_data_to_a_new_folder_leaves_the_copy_closed_to_other_users()
    {
        var source = P("source");
        Directory.CreateDirectory(Path.Combine(source, "backups"));
        File.WriteAllText(Path.Combine(source, "connections.json"), "{}");
        File.WriteAllText(Path.Combine(source, "backups", "connections-1.json"), "{}");
        var target = P("moved");

        Assert.Null(DataMover.CheckTarget(source, target));
        Assert.Null(DataMover.WarnAboutTarget(target));
        DataMover.Copy(source, target);

        Assert.False(FolderAccess.IsOpenToOthers(target));
        Assert.False(FolderAccess.IsOpenToOthers(Path.Combine(target, "backups")));
        AssertClosed(new FileInfo(Path.Combine(target, "connections.json")).GetAccessControl());
        AssertClosed(new FileInfo(Path.Combine(target, "backups", "connections-1.json")).GetAccessControl());
    }

    [WindowsFact]
    public void Move_data_warns_about_an_existing_folder_other_users_can_change()
    {
        var source = P("source");
        Directory.CreateDirectory(source);
        var target = P("existing");
        Directory.CreateDirectory(target);

        Assert.Null(DataMover.CheckTarget(source, target));
        Assert.Equal(FolderAccess.OpenToOthersWarning, DataMover.WarnAboutTarget(target));

        var closed = P("closed");
        FolderAccess.CreateProtected(closed);
        Assert.Null(DataMover.WarnAboutTarget(closed));
    }

    static IEnumerable<FileSystemAccessRule> Rules(FileSystemSecurity security) =>
        security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>();

    static void AssertClosed(FileSystemSecurity security)
    {
        var sids = Rules(security).Select(r => r.IdentityReference).ToList();
        Assert.DoesNotContain(Users, sids);
        Assert.DoesNotContain(AuthenticatedUsers, sids);
    }

    static void Grant(string dir, SecurityIdentifier sid, FileSystemRights rights, InheritanceFlags inheritance, PropagationFlags propagation)
    {
        var info = new DirectoryInfo(dir);
        var security = info.GetAccessControl();
        security.AddAccessRule(new FileSystemAccessRule(sid, rights, inheritance, propagation, AccessControlType.Allow));
        info.SetAccessControl(security);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}

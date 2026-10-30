using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Orrery.Core.Storage;

/// <summary>
/// Who else can change a data folder on Windows. A folder under <c>C:\</c>, on a second drive or on exFAT can be
/// changed by every user of the machine, and the trust stores live in it, so another user could plant a host key
/// or rewrite a saved host. Folders Orrery creates get a protected ACL (the current user, SYSTEM and Administrators,
/// inheritance off); a folder that already exists keeps its own and is only checked, so the user can be warned.
/// Linux and macOS use <see cref="PrivateStorage"/>'s owner-only modes instead.
/// </summary>
public static class FolderAccess
{
    public const string OpenToOthersWarning =
        "Other people who use this computer can change files in this folder. Orrery's saved hosts and trusted keys would live here.";

    /// <summary>Rights that let someone change, add, remove or re-permission what's in a folder (generic ones included).</summary>
    [SupportedOSPlatform("windows")]
    const int WriteRights = (int)(FileSystemRights.WriteData | FileSystemRights.AppendData | FileSystemRights.Delete
        | FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership)
        | GenericAll | GenericWrite;

    const int GenericAll = 0x10000000;
    const int GenericWrite = 0x40000000;

    /// <summary>
    /// Creates <paramref name="path"/> and any missing parents. On Windows each folder created gets a protected ACL
    /// granting full control to the current user, SYSTEM and Administrators only, inherited by everything written
    /// in it later; elsewhere it's 0700 (<see cref="PrivateStorage.CreateDirectory"/>). An existing folder is left
    /// exactly as it is.
    /// </summary>
    public static void CreateProtected(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            CreateOnWindows(Path.GetFullPath(path));
        }
        else
        {
            PrivateStorage.CreateDirectory(path);
        }
    }

    /// <summary>
    /// True when someone other than the current user, SYSTEM or Administrators can change files in
    /// <paramref name="path"/>, or the drive keeps no permissions at all (FAT, exFAT). A folder that doesn't exist yet
    /// isn't open, since Orrery creates it protected. Always false on Linux and macOS.
    /// </summary>
    public static bool IsOpenToOthers(string path) => OperatingSystem.IsWindows() && IsOpenOnWindows(Path.GetFullPath(path));

    [SupportedOSPlatform("windows")]
    static void CreateOnWindows(string path)
    {
        if (Directory.Exists(path))
        {
            return;
        }

        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var sid in Trusted())
        {
            security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        }

        // Created with the ACL in place, missing parents included, so no folder exists open even for a moment.
        security.CreateDirectory(path);
    }

    [SupportedOSPlatform("windows")]
    static bool IsOpenOnWindows(string path)
    {
        if (HasNoPermissions(path))
        {
            return true;
        }

        if (!Directory.Exists(path))
        {
            return false;
        }

        DirectorySecurity security;
        try
        {
            security = new DirectoryInfo(path).GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException or PlatformNotSupportedException)
        {
            return true;
        }

        var trusted = Trusted();

        // The owner can always rewrite the ACL, whatever it says.
        if (security.GetOwner(typeof(SecurityIdentifier)) is SecurityIdentifier owner && !trusted.Contains(owner))
        {
            return true;
        }

        foreach (FileSystemAccessRule rule in security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier)))
        {
            bool reachesFiles = !rule.PropagationFlags.HasFlag(PropagationFlags.InheritOnly) || rule.InheritanceFlags.HasFlag(InheritanceFlags.ObjectInherit);
            if (rule.AccessControlType == AccessControlType.Allow
                && reachesFiles
                && ((int)rule.FileSystemRights & WriteRights) != 0
                && rule.IdentityReference is SecurityIdentifier sid
                && !trusted.Contains(sid)
                && !sid.IsWellKnown(WellKnownSidType.CreatorOwnerSid)
                && !sid.IsWellKnown(WellKnownSidType.WinCreatorOwnerRightsSid))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>FAT and exFAT drives store no ACL, so anyone using the machine can change anything on them.</summary>
    static bool HasNoPermissions(string path)
    {
        try
        {
            var format = new DriveInfo(Path.GetPathRoot(path)!).DriveFormat;
            return format.StartsWith("FAT", StringComparison.OrdinalIgnoreCase) || format.Equals("exFAT", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException)
        {
            // A network share or a drive that isn't ready: the folder's own ACL decides.
            return false;
        }
    }

    [SupportedOSPlatform("windows")]
    static SecurityIdentifier[] Trusted()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return
        [
            identity.User!,
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
        ];
    }
}

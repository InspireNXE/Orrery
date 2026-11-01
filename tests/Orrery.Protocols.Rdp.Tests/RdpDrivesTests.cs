using Xunit;

namespace Orrery.Protocols.Rdp.Tests;

/// <summary>The paths below are Windows ones, which aren't fully qualified elsewhere: shows as skipped there.</summary>
sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = "Windows paths: Windows only";
        }
    }
}

public class RdpDrivesTests
{
    const string Home = @"C:\Users\me";

    static IReadOnlyList<SharedDrive> Plan(bool shareAll, string[] folders, bool multiple, List<string> problems, params string[] roots) =>
        RdpDrives.Plan(shareAll, folders, Home, roots, multiple, path => !path.Contains("missing", StringComparison.OrdinalIgnoreCase), problems);

    [Fact]
    public void Shares_nothing_unless_asked()
    {
        var problems = new List<string>();
        Assert.Empty(Plan(shareAll: false, [], multiple: true, problems, @"C:\"));
        Assert.Empty(problems);
    }

    [WindowsFact]
    public void Every_drive_is_each_drive_root_where_several_can_be_shared()
    {
        var problems = new List<string>();
        var drives = Plan(shareAll: true, [@"C:\Work"], multiple: true, problems, @"C:\", @"D:\");
        Assert.Equal([new SharedDrive("C", @"C:\"), new SharedDrive("D", @"D:\")], drives);
        Assert.Empty(problems);
    }

    [WindowsFact]
    public void Every_drive_is_the_home_folder_where_only_one_can_be()
    {
        var problems = new List<string>();
        Assert.Equal([new SharedDrive("me", Home)], Plan(shareAll: true, [], multiple: false, problems));
        Assert.Empty(problems);
    }

    [WindowsFact]
    public void Shares_chosen_folders_by_their_own_names_and_expands_the_home_folder()
    {
        var problems = new List<string>();
        var drives = Plan(shareAll: false, [@"C:\Work\", "~", @"~\Shared", "~/Music"], multiple: true, problems);
        Assert.Equal(
            [new SharedDrive("Work", @"C:\Work\"), new SharedDrive("me", Home), new SharedDrive("Shared", Path.Combine(Home, "Shared")),
             new SharedDrive("Music", Path.Combine(Home, "Music"))],
            drives);
        Assert.Empty(problems);
    }

    [WindowsFact]
    public void Two_folders_of_one_name_get_different_drive_names()
    {
        var drives = Plan(shareAll: false, [@"C:\a\Shared", @"D:\b\shared", @"E:\Shared"], multiple: true, []);
        Assert.Equal(["Shared", "shared 2", "Shared 3"], drives.Select(d => d.Name));
    }

    [WindowsFact]
    public void The_same_folder_twice_is_shared_once()
    {
        Assert.Single(Plan(shareAll: false, [@"C:\Work", @"c:\work"], multiple: true, []));
    }

    [WindowsFact]
    public void Network_relative_and_missing_folders_are_reported_not_shared()
    {
        var problems = new List<string>();
        var drives = Plan(shareAll: false, [@"\\files\team", "Work", @"C:\missing", @"C:\Work"], multiple: true, problems);
        Assert.Equal([new SharedDrive("Work", @"C:\Work")], drives);
        Assert.Equal(3, problems.Count);
        Assert.Contains("on the network", problems[0]);
        Assert.Contains("isn't a folder", problems[1]);
        Assert.Contains("isn't a folder", problems[2]);
    }

    [WindowsFact]
    public void Where_only_one_can_be_shared_the_rest_are_reported()
    {
        var problems = new List<string>();
        var drives = Plan(shareAll: false, [@"C:\Work", @"C:\Play"], multiple: false, problems);
        Assert.Equal([new SharedDrive("Work", @"C:\Work")], drives);
        Assert.Contains(@"C:\Play isn't", Assert.Single(problems));
    }

    [Theory]
    [InlineData(@"c:\", "C")]
    [InlineData(@"D:", "D")]
    [InlineData("/home/me/Shared/", "Shared")]
    [InlineData("/", "Root")]
    public void Names_a_drive_root_by_its_letter_and_a_folder_by_its_name(string path, string expected)
    {
        Assert.Equal(expected, RdpDrives.NameFor(path));
    }
}

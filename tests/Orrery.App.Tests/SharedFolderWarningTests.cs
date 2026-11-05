using Orrery.Core.Storage;
using Xunit;

namespace Orrery.App.Tests;

public class SharedFolderWarningTests
{
    static readonly OtherComputer Laptop = new("a1b2c3", "LAPTOP-7", DateTimeOffset.UtcNow);

    [Fact]
    public void Nothing_is_said_for_a_folder_only_this_computer_uses() =>
        Assert.Null(MainWindow.SharedFolderWarning([], []));

    [Fact]
    public void Names_the_other_computer_and_what_to_do()
    {
        var text = MainWindow.SharedFolderWarning([Laptop], [])!;
        Assert.Contains("Orrery is also open on LAPTOP-7", text);
        Assert.Contains("Close Orrery on one of them", text);
        Assert.DoesNotContain("copies:", text);
    }

    [Fact]
    public void Lists_the_conflict_copies_to_look_at()
    {
        var text = MainWindow.SharedFolderWarning([], ["connections (conflict).json", "vault_Conflict.frv"])!;
        Assert.Contains("\n  connections (conflict).json\n  vault_Conflict.frv", text);
        Assert.DoesNotContain("also open", text);
    }
}

using Orrery.Core.Storage;
using Xunit;

namespace Orrery.App.Tests;

public class SharedFolderWarningTests
{
    static readonly OtherComputer Laptop = new("a1b2c3", "LAPTOP-7", DateTimeOffset.UtcNow);
    static readonly OtherComputer Desktop = new("d4e5f6", "DESKTOP-2", DateTimeOffset.UtcNow);

    [Fact]
    public void Nothing_is_said_for_a_folder_only_this_computer_uses()
    {
        Assert.Null(MainWindow.OtherComputersNotice([]));
        Assert.Null(MainWindow.ConflictCopiesWarning([]));
    }

    [Fact]
    public void Names_the_other_computers()
    {
        Assert.Equal("Orrery is also open on LAPTOP-7. Changes on either show on both.", MainWindow.OtherComputersNotice([Laptop]));
        Assert.StartsWith("Orrery is also open on LAPTOP-7, DESKTOP-2.", MainWindow.OtherComputersNotice([Laptop, Desktop]));
    }

    [Fact]
    public void Lists_the_conflict_copies_to_look_at() =>
        Assert.EndsWith("\n  connections (conflict).json\n  vault_Conflict.frv",
            MainWindow.ConflictCopiesWarning(["connections (conflict).json", "vault_Conflict.frv"]));
}

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
        Assert.Null(MainWindow.ConflictCopiesWarning([], @"D:\Sync\Orrery"));
    }

    [Fact]
    public void Names_the_other_computers()
    {
        Assert.Equal("Orrery is also open on LAPTOP-7. Changes on either show on both.", MainWindow.OtherComputersNotice([Laptop]));
        Assert.StartsWith("Orrery is also open on LAPTOP-7, DESKTOP-2.", MainWindow.OtherComputersNotice([Laptop, Desktop]));
    }

    [Fact]
    public void Lists_the_conflict_copies_to_look_at_and_where_they_are() =>
        Assert.EndsWith("\n\nIn D:\\Sync\\Orrery:\n  connections (conflict).json\n  vault_Conflict.frv",
            MainWindow.ConflictCopiesWarning(["connections (conflict).json", "vault_Conflict.frv"], @"D:\Sync\Orrery"));

    // A vault copy is encrypted: deleting it after a look at the files could lose credentials only it has.
    [Fact]
    public void A_copy_of_the_vault_is_to_be_kept_until_checked()
    {
        Assert.Contains("keep it until you've checked", MainWindow.ConflictCopiesWarning(["vault_Conflict.frv"], @"D:\Sync\Orrery"));
        Assert.DoesNotContain("vault", MainWindow.ConflictCopiesWarning(["settings (conflict).json"], @"D:\Sync\Orrery")!.Split("\n\n")[0]);
    }
}

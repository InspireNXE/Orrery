using Xunit;

namespace Orrery.Protocols.Local.Tests;

public class LocalShellsTests
{
    [Fact]
    public void Offers_the_shells_on_this_computer_with_the_default_first()
    {
        var shells = LocalShells.Available();

        Assert.NotEmpty(shells);
        Assert.All(shells, s => Assert.True(Path.IsPathFullyQualified(s.Program), s.Program));
        Assert.Equal(shells.Count, shells.Select(s => s.Id).Distinct().Count());
        if (OperatingSystem.IsWindows())
        {
            Assert.Contains(shells, s => s.Id == LocalShells.Cmd && File.Exists(s.Program));
            Assert.Contains(shells, s => s.Id == LocalShells.WindowsPowerShell);
            bool hasPwsh = shells.Any(s => s.Id == LocalShells.PowerShell7);
            Assert.Equal(hasPwsh ? LocalShells.PowerShell7 : LocalShells.WindowsPowerShell, shells[0].Id);
            Assert.All(shells.Where(s => s.Id.StartsWith("wsl:", StringComparison.Ordinal)), s => Assert.Contains("--cd", s.Arguments));
        }
        else
        {
            Assert.Equal(LocalShells.LoginShell, Assert.Single(shells).Id);
        }
    }

    [Fact]
    public void An_unknown_or_missing_choice_falls_back_to_the_default()
    {
        var first = LocalShells.Available()[0];

        Assert.Equal(first.Id, LocalShells.Find(null).Id);
        Assert.Equal(first.Id, LocalShells.Find("wsl:no-such-distribution").Id);
        if (OperatingSystem.IsWindows())
        {
            Assert.Equal(LocalShells.Cmd, LocalShells.Find(LocalShells.Cmd).Id);
        }
    }

    [Theory]
    [InlineData(new string[0], "\"C:\\a b\\sh.exe\"")]
    [InlineData(new[] { "-d", "Ubuntu 24.04", "--cd", "~" }, "\"C:\\a b\\sh.exe\" -d \"Ubuntu 24.04\" --cd ~")]
    [InlineData(new[] { "", "say \"hi\"", "end\\" }, "\"C:\\a b\\sh.exe\" \"\" \"say \\\"hi\\\"\" end\\")]
    [InlineData(new[] { "dir with\\" }, "\"C:\\a b\\sh.exe\" \"dir with\\\\\"")]
    public void Builds_a_command_line_that_splits_back_into_the_same_arguments(string[] arguments, string expected)
    {
        Assert.Equal(expected, WindowsCommandLine.For(@"C:\a b\sh.exe", arguments));
    }
}

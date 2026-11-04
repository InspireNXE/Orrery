using Orrery.Core.Terminal;
using Xunit;

namespace Orrery.Core.Tests.Terminal;

public class TerminalPromptTests
{
    [Theory]
    [InlineData("[sudo] password for riley:")]
    [InlineData("[sudo] password for riley: ")]
    [InlineData("Password:")]
    [InlineData("riley@web-01's password:")]
    [InlineData("Enter passphrase for key '/home/riley/.ssh/id_ed25519':")]
    public void Sudo_su_and_ssh_prompts_are_password_prompts(string line)
    {
        Assert.Equal(TerminalPromptKind.Password, TerminalPrompt.Classify(line));
    }

    [Theory]
    [InlineData("New password:")]
    [InlineData("Retype new password:")]
    [InlineData("Re-enter password:")]
    [InlineData("Repeat password:")]
    public void Passwds_prompts_arent_answered_as_that_would_change_the_password(string line)
    {
        Assert.Equal(TerminalPromptKind.None, TerminalPrompt.Classify(line));
    }

    [Theory]
    [InlineData("riley@web-01:~$", TerminalPromptKind.Shell)]
    [InlineData("root@web-01:~# ", TerminalPromptKind.Shell)]
    [InlineData("web-01%", TerminalPromptKind.Shell)]
    [InlineData("PS C:\\Users\\riley>", TerminalPromptKind.Shell)]
    [InlineData("~/src ❯", TerminalPromptKind.Shell)]
    [InlineData("", TerminalPromptKind.None)]
    [InlineData("Downloading", TerminalPromptKind.None)]
    [InlineData("The password is stored in the vault", TerminalPromptKind.None)]
    public void Shell_prompts_end_in_a_prompt_character(string line, TerminalPromptKind kind)
    {
        Assert.Equal(kind, TerminalPrompt.Classify(line));
    }

    [Fact]
    public void A_long_line_is_output_not_a_prompt()
    {
        Assert.Equal(TerminalPromptKind.None, TerminalPrompt.Classify(new string('x', 300) + " password:"));
        Assert.Equal(TerminalPromptKind.None, TerminalPrompt.Classify(null));
    }
}

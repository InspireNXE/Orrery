using System.Text.RegularExpressions;

namespace Orrery.Core.Terminal;

/// <summary>What the line the cursor is on looks like it's waiting for.</summary>
public enum TerminalPromptKind
{
    /// <summary>Neither: output still coming, a program running, or a prompt Orrery doesn't know.</summary>
    None,

    /// <summary>A password, as sudo, su or ssh ask: <c>[sudo] password for riley:</c>, <c>Password:</c>.</summary>
    Password,

    /// <summary>A shell's prompt: ending in <c>$</c>, <c>#</c>, <c>%</c>, <c>&gt;</c> or <c>❯</c>.</summary>
    Shell,
}

/// <summary>
/// Reads the line the cursor is on, so a saved command can wait for the shell before it's typed, and answer a password
/// prompt only when one is showing. A guess from the text, not something the server says: any program can print
/// "Password:", which is why only a command the user saved here answers one.
/// </summary>
public static partial class TerminalPrompt
{
    /// <summary>Longer than this and it's output, not a prompt.</summary>
    const int MaxPromptLength = 200;

    public static TerminalPromptKind Classify(string? line)
    {
        var text = line?.TrimEnd() ?? string.Empty;
        if (text.Length == 0 || text.Length > MaxPromptLength)
        {
            return TerminalPromptKind.None;
        }

        if (PasswordPrompt().IsMatch(text))
        {
            return TerminalPromptKind.Password;
        }

        return text[^1] is '$' or '#' or '%' or '>' or '❯' ? TerminalPromptKind.Shell : TerminalPromptKind.None;
    }

    /// <summary>
    /// The user a password prompt names, as sudo's does (<c>[sudo] password for riley:</c>) and ssh's
    /// (<c>riley@web-01's password:</c>), or null when it names none (<c>Password:</c>, su's).
    /// </summary>
    public static string? PromptUser(string? line)
    {
        var text = line?.TrimEnd() ?? string.Empty;
        if (Classify(text) != TerminalPromptKind.Password)
        {
            return null;
        }

        var match = NamedUser().Match(text);
        return match.Success ? match.Groups["user"].Value : null;
    }

    [GeneratedRegex(@"\bpassword for (?<user>[^\s:]+)\s*:$|^(?<user>[^\s@]+)@[^\s']+'s password:$", RegexOptions.IgnoreCase)]
    private static partial Regex NamedUser();

    // "password" (or "passphrase") then anything but another colon, ending in a colon: sudo's, su's and ssh's own.
    // Not "New password:" or "Retype password:", which passwd asks, and an answer there would change it.
    [GeneratedRegex(@"^(?!.*\b(new|retype|re-enter|repeat)\b).*\b(password|passphrase)\b[^:]*:$", RegexOptions.IgnoreCase)]
    private static partial Regex PasswordPrompt();
}

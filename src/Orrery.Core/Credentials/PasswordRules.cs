using System.Text;
using System.Text.RegularExpressions;

namespace Orrery.Core.Credentials;

/// <summary>How hard a password would be to guess, as the strength meter shows it.</summary>
public enum PasswordStrength
{
    TooWeak,
    Fair,
    Good,
    Strong,
}

/// <param name="Problem">Why the password isn't accepted, said to the user; null when it is.</param>
public sealed record PasswordCheck(PasswordStrength Strength, string? Problem)
{
    public bool Accepted => Problem is null;
}

/// <summary>
/// What a new vault master password or export password must be: at least 12 characters, and not mostly a common
/// password, a word close to the user (Orrery, their name), a keyboard run, a sequence or a repeat. Character classes
/// aren't required, so a few unrelated words pass. Strength is a rough estimate of the guessing work left once those
/// patterns are taken out, in the spirit of zxcvbn; it only grades passwords that are already accepted.
/// Passwords set before these rules (an existing vault, an export to import) are never checked.
/// </summary>
public static class PasswordRules
{
    public const int MinimumLength = 12;

    /// <summary>The rules, said where a new password is chosen.</summary>
    public const string Requirements = "At least 12 characters. Common passwords, words like Orrery or your name, keyboard runs and "
        + "repeats don't count. A few unrelated words make a strong password that's easy to remember.";

    // What's left once patterns are taken out must be at least this long.
    const int MinimumRemainder = 6;

    // The bases of the most common passwords, at least 4 letters; leetspeak is undone before matching.
    static readonly string[] CommonWords =
    [
        "password", "passwort", "welcome", "admin", "administrator", "letmein", "login", "iloveyou", "love", "monkey", "dragon",
        "master", "shadow", "sunshine", "princess", "football", "baseball", "soccer", "hockey", "superman", "batman", "starwars",
        "trustno", "whatever", "freedom", "secret", "changeme", "default", "guest", "user", "test", "hello", "hunter", "ninja",
        "mustang", "michael", "jordan", "jennifer", "jessica", "ashley", "charlie", "thomas", "robert", "daniel", "matthew",
        "andrew", "joshua", "maggie", "pepper", "ginger", "buster", "tigger", "cookie", "summer", "winter", "spring", "autumn",
        "january", "february", "march", "april", "june", "july", "august", "september", "october", "november", "december",
        "monday", "tuesday", "wednesday", "thursday", "friday", "saturday", "sunday", "computer", "internet", "windows", "linux",
        "apple", "google", "microsoft", "samsung", "orange", "banana", "chocolate", "flower", "purple", "silver", "golden",
        "diamond", "killer", "cheese", "liverpool", "chelsea", "arsenal", "yankees", "cowboys", "eagles", "lakers", "harley",
        "ranger", "corvette", "ferrari", "porsche", "mercedes", "family", "forever", "angel", "babygirl", "lovely", "beautiful",
        "sweet", "happy", "lucky", "money", "access", "server", "network", "system", "office", "company", "backup", "router",
        "firewall", "remote", "desktop", "connect", "manager", "vault", "keepass", "qwerty", "azerty", "abcd", "orrery",
    ];

    static readonly string[] KeyboardRows = ["qwertyuiop", "asdfghjkl", "zxcvbnm", "azertyuiop", "qwertzuiop", "1234567890"];

    static readonly Regex Year = new("(19|20)[0-9]{2}", RegexOptions.CultureInvariant);

    /// <param name="personal">Words close to the user that shouldn't carry the password, such as their account name.</param>
    public static PasswordCheck Check(string password, IEnumerable<string>? personal = null)
    {
        int length = password.EnumerateRunes().Count();
        if (length == 0)
        {
            return new(PasswordStrength.TooWeak, "Enter a password.");
        }

        if (length < MinimumLength)
        {
            return new(PasswordStrength.TooWeak, $"Use at least {MinimumLength} characters. This has {length}.");
        }

        if (password.EnumerateRunes().Distinct().Count() < 5)
        {
            return new(PasswordStrength.TooWeak, "Use more than a few different characters.");
        }

        var own = (personal ?? []).SelectMany(w => new[] { Normalize(w, leet: false), Normalize(w, leet: true) })
            .Where(w => w.Length >= 3).Distinct().ToList();
        int rest = Remainder(password, own);
        if (rest < MinimumRemainder && own.Count > 0 && Remainder(password, []) >= MinimumRemainder)
        {
            return new(PasswordStrength.TooWeak, "It's mostly your name or another word close to you, which is guessed early. Try a few unrelated words.");
        }

        if (rest < MinimumRemainder)
        {
            return new(PasswordStrength.TooWeak, "It's mostly a common password, a keyboard run, a sequence or a repeat, which are guessed first. Try a few unrelated words.");
        }

        // Each character left counts in full; one inside a pattern counts for a quarter.
        double perCharacter = Math.Log2(Pool(password));
        double bits = (rest + 0.25 * (length - rest)) * perCharacter;
        var strength = bits < 60 ? PasswordStrength.Fair : bits < 90 ? PasswordStrength.Good : PasswordStrength.Strong;
        return new(strength, null);
    }

    /// <summary>The meter's word for <paramref name="strength"/>.</summary>
    public static string Describe(PasswordStrength strength) => strength switch
    {
        PasswordStrength.TooWeak => "Too weak",
        PasswordStrength.Fair => "Fair",
        PasswordStrength.Good => "Good",
        _ => "Strong",
    };

    /// <summary>
    /// How many characters are left once patterns are taken out. Read twice, as written and with leetspeak undone
    /// ("P@ssw0rd" is "password", but "123456" must stay digits to be seen as a run), keeping the fewer.
    /// </summary>
    static int Remainder(string password, IReadOnlyList<string> personal)
    {
        // Years first, before leetspeak turns their digits into letters.
        string text = Year.Replace(password, " ");
        return Math.Min(Remove(Normalize(text, leet: false), personal).Length, Remove(Normalize(text, leet: true), personal).Length);
    }

    /// <summary>Lowercase letters and digits only, with leetspeak undone if asked, so "P@ssw0rd!" can read as "password".</summary>
    static string Normalize(string text, bool leet)
    {
        var plain = new StringBuilder(text.Length);
        foreach (char c in text.ToLowerInvariant())
        {
            char letter = !leet ? c : c switch
            {
                '@' or '4' => 'a',
                '$' or '5' => 's',
                '0' => 'o',
                '1' or '!' or '|' => 'i',
                '3' => 'e',
                '7' or '+' => 't',
                '8' => 'b',
                '9' => 'g',
                _ => c,
            };
            if (char.IsLetterOrDigit(letter))
            {
                plain.Append(letter);
            }
        }

        return plain.ToString();
    }

    /// <summary>What's left of <paramref name="plain"/> once common and personal words, runs and repeats are taken out.</summary>
    static string Remove(string plain, IReadOnlyList<string> personal)
    {
        string rest = plain;
        foreach (var word in personal.Concat(CommonWords).OrderByDescending(w => w.Length))
        {
            rest = rest.Replace(word, string.Empty, StringComparison.Ordinal);
        }

        return RemoveRuns(rest);
    }

    /// <summary>Takes out stretches of 3 or more characters that repeat one character, count up or down, or follow a keyboard row.</summary>
    static string RemoveRuns(string text)
    {
        var kept = new StringBuilder(text.Length);
        int i = 0;
        while (i < text.Length)
        {
            int end = i + 1;
            if (end < text.Length)
            {
                int step = Step(text[i], text[end]);
                if (step != int.MinValue)
                {
                    while (end + 1 < text.Length && Step(text[end], text[end + 1]) == step)
                    {
                        end++;
                    }

                    end++;
                }
            }

            if (end - i >= 3)
            {
                i = end;
                continue;
            }

            kept.Append(text[i]);
            i++;
        }

        return kept.ToString();
    }

    /// <summary>How <paramref name="b"/> follows <paramref name="a"/>: the same (0), next or previous (±1, or ±2 along a keyboard row); MinValue if not.</summary>
    static int Step(char a, char b)
    {
        if (a == b)
        {
            return 0;
        }

        if (Math.Abs(b - a) == 1 && char.IsLetterOrDigit(a) && char.IsLetterOrDigit(b))
        {
            return b - a;
        }

        foreach (var row in KeyboardRows)
        {
            int at = row.IndexOf(a), next = row.IndexOf(b);
            if (at >= 0 && next >= 0 && Math.Abs(next - at) == 1)
            {
                return 2 * (next - at);
            }
        }

        return int.MinValue;
    }

    /// <summary>How many characters each one could have been, from the kinds the password uses.</summary>
    static int Pool(string password)
    {
        bool lower = false, upper = false, digit = false, symbol = false, other = false;
        foreach (char c in password)
        {
            if (c is >= 'a' and <= 'z') lower = true;
            else if (c is >= 'A' and <= 'Z') upper = true;
            else if (c is >= '0' and <= '9') digit = true;
            else if (c < 128) symbol = true;
            else other = true;
        }

        return (lower ? 26 : 0) + (upper ? 26 : 0) + (digit ? 10 : 0) + (symbol ? 33 : 0) + (other ? 100 : 0);
    }
}

using Orrery.Core.Credentials;
using Xunit;

namespace Orrery.Core.Tests.Credentials;

public class PasswordRulesTests
{
    [Theory]
    [InlineData("", "Enter a password.")]
    [InlineData("Tr0ub4dor&3", "at least 12")]
    [InlineData("aaaaaaaaaaaaaaaa", "different characters")]
    [InlineData("abababababababab", "different characters")]
    public void Short_and_samey_passwords_are_refused(string password, string problem)
    {
        var check = PasswordRules.Check(password);
        Assert.False(check.Accepted);
        Assert.Equal(PasswordStrength.TooWeak, check.Strength);
        Assert.Contains(problem, check.Problem);
    }

    [Theory]
    [InlineData("Password1234")]
    [InlineData("P@ssw0rd2024!")]
    [InlineData("1234567890qwerty")]
    [InlineData("qwertyuiopasdf")]
    [InlineData("Summer2024Summer")]
    [InlineData("iloveyou123456")]
    [InlineData("zyxwvutsrqponm")]
    [InlineData("welcome-admin-2025")]
    [InlineData("OrreryOrrery99")]
    [InlineData("!@#$%^&*()!@#")]
    public void Common_passwords_runs_and_years_are_refused(string password)
    {
        var check = PasswordRules.Check(password);
        Assert.False(check.Accepted);
        Assert.Contains("common password", check.Problem);
    }

    [Fact]
    public void A_password_built_on_the_users_name_is_refused_and_says_so()
    {
        var check = PasswordRules.Check("Steven.Downer!", ["steven.downer", "steven", "downer"]);
        Assert.False(check.Accepted);
        Assert.Contains("your name", check.Problem);
        Assert.True(PasswordRules.Check("Steven.Downer!").Accepted);
    }

    [Theory]
    [InlineData("correct horse battery staple", PasswordStrength.Strong)]
    [InlineData("a brand new password", PasswordStrength.Good)]
    [InlineData("kfjrmtzqwpvd", PasswordStrength.Fair)]
    [InlineData("Gx7#pL2!vQ9z", PasswordStrength.Good)]
    [InlineData("granite lantern oboe quietly", PasswordStrength.Strong)]
    public void Unrelated_words_and_random_characters_are_accepted_and_graded(string password, PasswordStrength strength)
    {
        var check = PasswordRules.Check(password);
        Assert.True(check.Accepted, check.Problem);
        Assert.Equal(strength, check.Strength);
    }

    [Theory]
    [InlineData("#!$%&*()-_=+[]{}")]
    [InlineData("🦊🌲🚲🎻🧭🪐🍋🐙🎈🧩🌋🦉")]
    [InlineData("k#9]~q;<x/*?v")]
    public void A_long_password_mostly_of_symbols_isnt_taken_for_a_pattern(string password)
    {
        var check = PasswordRules.Check(password);
        Assert.True(check.Accepted, check.Problem);
    }
}

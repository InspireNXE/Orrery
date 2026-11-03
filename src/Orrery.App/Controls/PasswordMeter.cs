using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Orrery.Core.Credentials;

namespace Orrery.App.Controls;

/// <summary>
/// Under a new password's box: the rules until something is typed, then four bars and a word for how strong it is,
/// with what's wrong while it isn't accepted. A screen reader hears the same as the box's help text, rather than on
/// every key.
/// </summary>
sealed class PasswordMeter : StackPanel
{
    static readonly IBrush EmptyBar = new SolidColorBrush(Color.Parse("#2A3038")).ToImmutable();

    readonly TextBox _box;
    readonly IReadOnlyList<string> _personal;
    readonly Border[] _bars = [.. Enumerable.Range(0, 4).Select(_ => new Border { Height = 4, CornerRadius = new CornerRadius(2), Background = EmptyBar })];
    readonly TextBlock _text = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap, Foreground = Dialogs.MutedBrush };

    /// <param name="personal">Words close to the user the password shouldn't rest on; the account name is added.</param>
    public PasswordMeter(TextBox box, params string[] personal)
    {
        _box = box;
        _personal = [.. personal, .. NameParts(Environment.UserName)];
        Spacing = 6;
        var bars = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*,*,*"), ColumnSpacing = 4 };
        for (int i = 0; i < _bars.Length; i++)
        {
            Grid.SetColumn(_bars[i], i);
            bars.Children.Add(_bars[i]);
        }

        Children.Add(bars);
        Children.Add(_text);
        box.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBox.TextProperty)
            {
                Update();
            }
        };
        Update();
    }

    /// <summary>The password as the rules see it, for the window to check before it goes on.</summary>
    public PasswordCheck Check() => PasswordRules.Check(_box.Text ?? string.Empty, _personal);

    void Update()
    {
        string password = _box.Text ?? string.Empty;
        if (password.Length == 0)
        {
            Show(0, EmptyBar, PasswordRules.Requirements, Dialogs.MutedBrush);
            return;
        }

        var check = Check();
        var (filled, brush) = check.Strength switch
        {
            PasswordStrength.TooWeak => (1, Palette.Danger),
            PasswordStrength.Fair => (2, Palette.Warning),
            PasswordStrength.Good => (3, Palette.Success),
            _ => (4, Palette.Success),
        };
        string word = PasswordRules.Describe(check.Strength);
        Show(filled, brush, check.Problem is { } problem ? $"{word}. {problem}" : word, check.Accepted ? Dialogs.MutedBrush : Palette.Danger);
    }

    void Show(int filled, IBrush brush, string text, IBrush textBrush)
    {
        for (int i = 0; i < _bars.Length; i++)
        {
            _bars[i].Background = i < filled ? brush : EmptyBar;
        }

        _text.Text = text;
        _text.Foreground = textBrush;
        AutomationProperties.SetHelpText(_box, text);
    }

    /// <summary>"morgan.parker" and its parts, as words a password shouldn't be made of.</summary>
    static IEnumerable<string> NameParts(string name) =>
        new[] { name }.Concat(name.Split(['.', '-', '_', ' ', '\\', '@'], StringSplitOptions.RemoveEmptyEntries));
}

using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;

namespace Orrery.App.Sessions;

/// <summary>
/// A terminal as a screen reader sees it: a read-only edit box whose value is the text on screen, with the cursor's
/// line and column as its status. Avalonia 12.1 has no UI Automation text pattern, so a screen reader can read the
/// screen as a whole but can't move through it by line or word; new output is read out by the view itself.
/// </summary>
sealed class TerminalAutomationPeer(TerminalView owner) : ControlAutomationPeer(owner), IValueProvider
{
    new TerminalView Owner => (TerminalView)base.Owner;

    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Edit;

    protected override string GetLocalizedControlTypeCore() => "terminal";

    protected override string? GetNameCore() => base.GetNameCore() is { Length: > 0 } name ? name : "Terminal";

    protected override string? GetItemStatusCore()
    {
        var (line, column) = Owner.CursorPosition;
        return $"Line {line}, column {column}";
    }

    protected override bool IsContentElementCore() => true;

    protected override bool IsControlElementCore() => true;

    public bool IsReadOnly => true;

    public string? Value => Owner.ScreenText();

    public void SetValue(string? value) => throw new InvalidOperationException("A terminal's text can only be typed.");
}

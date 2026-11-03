using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;

namespace Orrery.App.Tests;

/// <summary>What a screen reader would find wrong with the controls under a window: focusable things without a name.</summary>
static class AccessibilityAudit
{
    /// <summary>
    /// Every focusable control on show whose automation name is missing or is a type name (what a button with only an
    /// icon in it reports), described so a failure says where it is.
    /// </summary>
    public static List<string> Unnamed(Visual root) =>
        root.GetVisualDescendants().OfType<Control>()
            .Where(c => c.Focusable && c.IsEffectivelyVisible)
            .Select(c => (Control: c, Peer: ControlAutomationPeer.CreatePeerForElement(c)))
            .Where(x => x.Peer.IsControlElement() && !Sensible(x.Peer.GetName()))
            .Select(x => Describe(x.Control))
            .ToList();

    /// <summary>Fails listing every unnamed control, one per line.</summary>
    public static void AssertAllNamed(Visual root)
    {
        var unnamed = Unnamed(root);
        Xunit.Assert.True(unnamed.Count == 0, "Unnamed controls:\n" + string.Join("\n", unnamed));
    }

    /// <summary>The controls Tab visits from <paramref name="start"/> until it comes back round, in order.</summary>
    public static List<Control> TabOrder(Control start)
    {
        var order = new List<Control>();
        IInputElement? at = start;
        for (int i = 0; i < 500 && at is not null; i++)
        {
            at = TopLevel.GetTopLevel(at as Visual)?.FocusManager?.FindNextElement(NavigationDirection.Next, new FindNextElementOptions { FocusedElement = at });
            if (at is not Control control || control == start || order.Contains(control))
            {
                break;
            }

            order.Add(control);
        }

        return order;
    }

    public static AutomationPeer Peer(Control control) => ControlAutomationPeer.CreatePeerForElement(control);

    static bool Sensible(string? name) =>
        !string.IsNullOrWhiteSpace(name) && !name.StartsWith("Avalonia.", StringComparison.Ordinal) && !name.StartsWith("Orrery.", StringComparison.Ordinal);

    static string Describe(Control control)
    {
        var path = control.GetVisualAncestors().OfType<Control>().Take(4).Select(c => c.Name is { Length: > 0 } n ? $"{c.GetType().Name}#{n}" : c.GetType().Name);
        return $"{control.GetType().Name}{(control.Name is { Length: > 0 } name ? "#" + name : "")} in {string.Join(" < ", path)}";
    }
}

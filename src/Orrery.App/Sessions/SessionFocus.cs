using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;

namespace Orrery.App.Sessions;

/// <summary>Decides whether a session that has just connected may take the keyboard.</summary>
static class SessionFocus
{
    /// <summary>
    /// Focuses <paramref name="view"/> unless the user is typing in a text box outside it, such as a password in the
    /// properties pane: a session that connects then (its timing is the server's) would be sent the rest of it.
    /// Focus on nothing, in the view, or on the tab strip or toolbar that opened it is taken.
    /// </summary>
    public static void TakeIfFree(InputElement view)
    {
        if (TopLevel.GetTopLevel(view)?.FocusManager?.GetFocusedElement() is TextBox typing
            && !ReferenceEquals(typing, view) && !view.IsVisualAncestorOf(typing))
        {
            return;
        }

        view.Focus();
    }
}

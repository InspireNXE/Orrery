using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Orrery.App.Connections;
using Orrery.App.Sessions;
using Orrery.App.Setup;

namespace Orrery.App.Input;

/// <summary>
/// Keys every prompt shares, as Windows' own dialogs have them: Left and Right move between the buttons in a row, a
/// prompt that opens with nothing focused focuses its default button (or Cancel), and Ctrl+Enter (Cmd+Enter on
/// macOS) presses the default button from a text box that takes Enter for new lines. Enter and Esc are the buttons'
/// own, through IsDefault and IsCancel.
/// </summary>
static class DialogKeys
{
    static bool _installed;

    public static void Install()
    {
        if (_installed)
        {
            return;
        }

        _installed = true;
        InputElement.KeyDownEvent.AddClassHandler<Window>(OnKeyDown, RoutingStrategies.Tunnel);
        Visual.IsVisibleProperty.Changed.AddClassHandler<Window>((window, e) =>
        {
            if (e.NewValue is true && IsPrompt(window))
            {
                // After the prompt's own Opened handlers, which choose its focus where it has a better one.
                Dispatcher.UIThread.Post(() => FocusDefault(window), DispatcherPriority.Background);
            }
        });
    }

    /// <summary>A prompt: not the main window, a popped-out session, quick connect or setup, which have keys of their own.</summary>
    static bool IsPrompt(Window window) => window is not (MainWindow or PopoutWindow or QuickConnectWindow or SetupWizard);

    static void OnKeyDown(Window window, KeyEventArgs e)
    {
        if (e.Handled || !IsPrompt(window))
        {
            return;
        }

        var focused = window.FocusManager?.GetFocusedElement();
        var command = OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;
        if (e.Key == Key.Enter && e.KeyModifiers == command && focused is TextBox { AcceptsReturn: true } && DefaultButton(window) is { } button)
        {
            e.Handled = true;
            Press(button);
        }
        else if (e.Key is Key.Left or Key.Right && e.KeyModifiers == KeyModifiers.None && focused is Button { Parent: Panel row } current && IsPlain(current)
                 && Beside(row, current, e.Key == Key.Right) is { } next)
        {
            e.Handled = true;
            next.Focus(NavigationMethod.Directional);
        }
    }

    /// <summary>The next button along the row from <paramref name="current"/>, left to right as they're drawn, or null at its end.</summary>
    static Button? Beside(Panel row, Button current, bool right)
    {
        var buttons = row.Children.OfType<Button>().Where(b => IsPlain(b) && b.IsEffectivelyVisible && b.IsEffectivelyEnabled)
            .OrderBy(b => b.Bounds.X).ToList();
        int at = buttons.IndexOf(current);
        int next = right ? at + 1 : at - 1;
        return at < 0 || next < 0 || next >= buttons.Count ? null : buttons[next];
    }

    /// <summary>An ordinary button: not a toggle, check box or radio button, whose arrow keys are their group's.</summary>
    static bool IsPlain(Button button) => button is not ToggleButton;

    static Button? DefaultButton(Window window) => Buttons(window).FirstOrDefault(b => b.IsDefault);

    static IEnumerable<Button> Buttons(Window window) =>
        window.GetVisualDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible && b.IsEffectivelyEnabled);

    /// <summary>A prompt that opened with nothing focused focuses its default button, or failing that its Cancel.</summary>
    static void FocusDefault(Window window)
    {
        if (!window.IsVisible || window.FocusManager?.GetFocusedElement() is Visual focused && window.IsVisualAncestorOf(focused))
        {
            return;
        }

        (DefaultButton(window) ?? Buttons(window).FirstOrDefault(b => b.IsCancel))?.Focus(NavigationMethod.Tab);
    }

    static void Press(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
}

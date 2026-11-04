using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Orrery.App.Input;

/// <summary>
/// A right-click that overlaps a left one opens the right-click menu, in every window. A quick left-then-right click
/// puts the right button down before the left is up; Avalonia folds that into the left press, so there's no right
/// release and no menu (Windows' own WM_CONTEXTMENU for it goes unheard). A press the right button joined asks for
/// the menu where it was released, as a right-click would, once the buttons are up.
/// </summary>
static class OverlappingRightClick
{
    static bool _installed;
    static bool _rightJoined;

    public static void Install()
    {
        if (_installed)
        {
            return;
        }

        _installed = true;
        InputElement.PointerPressedEvent.AddClassHandler<TopLevel>((_, _) => _rightJoined = false, RoutingStrategies.Tunnel, handledEventsToo: true);
        InputElement.PointerMovedEvent.AddClassHandler<TopLevel>((top, e) =>
        {
            var point = e.GetCurrentPoint(top).Properties;
            _rightJoined |= point.IsLeftButtonPressed && point.IsRightButtonPressed;
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
        InputElement.PointerReleasedEvent.AddClassHandler<TopLevel>(OnReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    static void OnReleased(TopLevel top, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton != MouseButton.Left || !_rightJoined || e.Source is not Control source)
        {
            return;
        }

        _rightJoined = false;
        // After the left release has done its work (selecting the row under it), as a right release comes after.
        Dispatcher.UIThread.Post(() =>
        {
            if (source.IsAttachedToVisualTree() && source.IsEffectivelyVisible && source.IsEffectivelyEnabled)
            {
                source.RaiseEvent(new ContextRequestedEventArgs(e));
            }
        });
    }
}

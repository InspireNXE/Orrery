using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Xunit;

namespace Orrery.App.Tests;

public class OverlappingRightClickTests
{
    /// <summary>
    /// A quick left-then-right click as Avalonia reports it on Windows: the right button goes down before the left is
    /// up, so it joins the left press as a move, and the release is the left one. Headless input can't hold two
    /// buttons, so the move is raised as the real app gets it.
    /// </summary>
    static void OverlappingClick(Window window, Control row)
    {
        var at = row.TranslatePoint(new Point(row.Bounds.Width / 2, row.Bounds.Height / 2), window)!.Value;
        var target = row.GetVisualDescendants().OfType<Control>().First(c => c.IsHitTestVisible);
        var pointer = new Pointer(Pointer.GetNextFreeId(), PointerType.Mouse, true);
        var both = new PointerPointProperties(RawInputModifiers.LeftMouseButton | RawInputModifiers.RightMouseButton, PointerUpdateKind.RightButtonPressed);
        window.MouseDown(at, MouseButton.Left);
        target.RaiseEvent(new PointerEventArgs(InputElement.PointerMovedEvent, target, pointer, window, at, 0, both, KeyModifiers.None));
        window.MouseUp(at, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void A_right_click_overlapping_a_left_one_opens_a_list_or_trees_menu_on_the_row()
    {
        var list = new ListBox { ItemsSource = new[] { "a", "b" }, ContextMenu = new ContextMenu { Items = { new MenuItem { Header = "List" } } } };
        var tree = new TreeView { ItemsSource = new[] { "x", "y" }, ContextMenu = new ContextMenu { Items = { new MenuItem { Header = "Tree" } } } };
        var opened = new List<string>();
        list.ContextMenu.Opening += (_, _) => opened.Add("list");
        tree.ContextMenu.Opening += (_, _) => opened.Add("tree");
        var panel = new StackPanel { Children = { list, tree } };
        var window = new Window { Content = panel, Width = 400, Height = 400 };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        OverlappingClick(window, list.GetVisualDescendants().OfType<ListBoxItem>().Last());
        Assert.True(list.ContextMenu.IsOpen);
        Assert.Equal("b", list.SelectedItem);
        list.ContextMenu.Close();

        OverlappingClick(window, tree.GetVisualDescendants().OfType<TreeViewItem>().Last());
        Assert.True(tree.ContextMenu.IsOpen);
        Assert.Equal("y", tree.SelectedItem);
        tree.ContextMenu.Close();
        Assert.Equal(["list", "tree"], opened);
        window.Close();
    }

    [AvaloniaFact]
    public void A_plain_left_click_opens_no_menu()
    {
        var list = new ListBox { ItemsSource = new[] { "a", "b" }, ContextMenu = new ContextMenu { Items = { new MenuItem { Header = "List" } } } };
        var window = new Window { Content = list, Width = 400, Height = 400 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var row = list.GetVisualDescendants().OfType<ListBoxItem>().Last();
        var at = row.TranslatePoint(new Point(row.Bounds.Width / 2, row.Bounds.Height / 2), window)!.Value;

        window.MouseDown(at, MouseButton.Left);
        window.MouseUp(at, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        Assert.False(list.ContextMenu.IsOpen);
        window.Close();
    }
}

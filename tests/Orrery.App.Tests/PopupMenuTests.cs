using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Orrery.App.Controls;
using Orrery.App.Input;
using Xunit;

namespace Orrery.App.Tests;

public class PopupMenuTests
{
    static MenuFlyoutPresenter Presenter(PopupFlyoutBase flyout) => (MenuFlyoutPresenter)flyout.Popup.Child!;

    static List<Control> Items(string last) =>
        [new MenuItem { Header = "View only" }, new Separator(), new MenuItem { Header = last }];

    [AvaloniaFact]
    public void The_more_menu_shows_its_items_every_time_it_opens()
    {
        var anchor = new Button { Content = "..." };
        var window = new Window { Content = new StackPanel { Children = { anchor } }, Width = 800, Height = 500 };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        foreach (var last in new[] { "Close", "Close other tabs" })
        {
            var flyout = PopupMenu.ShowAt(anchor, Items(last), PlacementMode.BottomEdgeAlignedRight);
            Dispatcher.UIThread.RunJobs();

            var presenter = Presenter(flyout);
            Assert.Equal(3, presenter.ItemCount);
            Assert.True(presenter.Bounds.Width > 20, $"The menu is {presenter.Bounds.Width} px wide.");
            Assert.Equal(last, ((MenuItem)presenter.Items[2]!).Header);
            flyout.Hide();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public void Menus_write_shortcuts_as_the_palette_and_tooltips_do()
    {
        if (FullScreenShortcut.CurrentPlatform == ShortcutPlatform.MacOS)
        {
            return; // macOS keeps Avalonia's symbols
        }

        var anchor = new Button { Content = "..." };
        var window = new Window { Content = new StackPanel { Children = { anchor } }, Width = 800, Height = 500 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var items = new List<Control>();
        foreach (var gesture in new[] { "Ctrl+Alt+Enter", "Ctrl+OemPlus", "Ctrl+D0", "Delete" })
        {
            var item = new MenuItem { Header = gesture };
            MenuShortcut.Show(item, KeyGesture.Parse(gesture));
            items.Add(item);
        }

        var flyout = PopupMenu.ShowAt(anchor, items, PlacementMode.BottomEdgeAlignedRight);
        Dispatcher.UIThread.RunJobs();

        // Avalonia itself writes the first two "Ctrl+Alt+Return" and "Ctrl++".
        var shown = Presenter(flyout).GetVisualDescendants().OfType<TextBlock>().Where(t => t.Name == "PART_InputGestureText").Select(t => t.Text);
        Assert.Equal(["Ctrl+Alt+Enter", "Ctrl+=", "Ctrl+0", "Delete"], shown);
        flyout.Hide();
        window.Close();
    }

    [AvaloniaFact]
    public void Items_swapped_in_while_a_flyout_opens_never_show()
    {
        // The pattern the More menu used, kept here so a newer Avalonia that fixes it is noticed.
        var anchor = new Button { Content = "..." };
        var window = new Window { Content = new StackPanel { Children = { anchor } }, Width = 800, Height = 500 };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var flyout = new MenuFlyout();
        flyout.Opening += (_, _) =>
        {
            flyout.Items.Clear();
            foreach (var item in Items("Close"))
            {
                flyout.Items.Add(item);
            }
        };
        flyout.ShowAt(anchor);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(0, Presenter(flyout).ItemCount);
    }
}

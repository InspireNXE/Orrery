using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Xunit;

namespace Orrery.App.Tests;

/// <summary>A click outside the text field being typed in leaves it.</summary>
public class TextFieldFocusTests
{
    // A field at the top (y 0-40), a plain panel that can't take focus (y 40-140), a number box (y 140-180).
    static (Window Window, TextBox Field, NumericUpDown Number) Open()
    {
        var field = new TextBox { Height = 40 };
        var number = new NumericUpDown { Height = 40, Value = 1 };
        var window = new Window
        {
            Width = 300,
            Height = 200,
            Content = new StackPanel { Children = { field, new Border { Height = 100, Background = Brushes.Gray }, number } },
        };
        window.AddHandler(InputElement.PointerPressedEvent, (_, e) => App.LeaveTextFieldOnClickOutside(window, e), RoutingStrategies.Tunnel, handledEventsToo: true);
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, field, number);
    }

    static void Click(Window window, Point point)
    {
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void A_click_on_something_that_cant_take_focus_leaves_the_field()
    {
        var (window, field, _) = Open();
        field.Focus();
        Assert.True(field.IsFocused);

        Click(window, new Point(150, 90));

        Assert.False(field.IsKeyboardFocusWithin);
    }

    [AvaloniaFact]
    public void A_click_in_the_field_keeps_it()
    {
        var (window, field, _) = Open();
        field.Focus();

        Click(window, new Point(150, 20));

        Assert.True(field.IsFocused);
    }

    [AvaloniaFact]
    public void A_click_on_a_number_boxs_own_parts_keeps_its_field()
    {
        var (window, _, number) = Open();
        Click(window, new Point(100, 160));
        Assert.True(number.IsKeyboardFocusWithin);

        // The spin buttons sit at the right-hand end of the box.
        Click(window, new Point(290, 160));

        Assert.True(number.IsKeyboardFocusWithin);
    }
}

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Xunit;

namespace Orrery.App.Tests;

public class TextBoxAlignmentTests
{
    [AvaloniaFact]
    public void A_one_line_box_has_its_text_in_the_middle()
    {
        var box = new TextBox { Text = "~/Downloads/data", FontFamily = Dialogs.MonoFont, MinHeight = 36 };
        var notes = new TextBox { Text = "line", AcceptsReturn = true, Height = 72 };
        var window = new Window { Content = new StackPanel { Children = { box, notes } }, Width = 300, Height = 200 };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var text = box.GetVisualDescendants().OfType<TextPresenter>().Single();
        var top = text.TranslatePoint(default, box)!.Value.Y;
        var bottom = box.Bounds.Height - top - text.Bounds.Height;
        Assert.InRange(Math.Abs(top - bottom), 0, 1.5);

        // Notes start at the top, as a page does.
        var lines = notes.GetVisualDescendants().OfType<TextPresenter>().Single();
        Assert.True(lines.TranslatePoint(default, notes)!.Value.Y < notes.Bounds.Height / 3);
    }
}

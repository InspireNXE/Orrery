using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Orrery.App.Connections;
using Orrery.Core.Connections;
using Xunit;

namespace Orrery.App.Tests;

public class DialogKeysTests
{
    static Button ButtonFor(Window window, string text) =>
        window.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, text));

    static object? Focused(Window window) => window.FocusManager!.GetFocusedElement();

    [AvaloniaFact]
    public void A_question_opens_on_its_default_and_left_and_right_move_along_the_buttons()
    {
        var owner = new Window();
        owner.Show();
        var answer = Dialogs.ConfirmAsync(owner, "Delete web-01?", "It goes from the tree.", "Delete");
        Dispatcher.UIThread.RunJobs();
        var window = owner.OwnedWindows.Single();

        // Cancel is the default, so a prompt that opens over typing doesn't confirm on a stray Enter.
        var cancel = ButtonFor(window, "Cancel");
        var delete = ButtonFor(window, "Delete");
        Assert.Same(cancel, Focused(window));

        window.KeyPress(Key.Left, RawInputModifiers.None, PhysicalKey.ArrowLeft, null);
        Assert.Same(delete, Focused(window));
        window.KeyPress(Key.Left, RawInputModifiers.None, PhysicalKey.ArrowLeft, null);
        Assert.Same(delete, Focused(window)); // The row's end: it stays.
        window.KeyPress(Key.Right, RawInputModifiers.None, PhysicalKey.ArrowRight, null);
        Assert.Same(cancel, Focused(window));

        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Dispatcher.UIThread.RunJobs();
        Assert.False(answer.Result);
        owner.Close();
    }

    [AvaloniaFact]
    public async Task Ctrl_enter_in_the_command_box_saves_the_command()
    {
        var owner = new Window();
        owner.Show();
        var tree = ConnectionTree.Empty();
        var window = new SavedCommandWindow(null, [new CommandPlace("Every connection", tree.Root)]);
        var result = window.ShowDialog<SavedCommand?>(owner);
        Dispatcher.UIThread.RunJobs();

        window.TextBox.Focus();
        window.TextBox.Text = "uptime";
        window.KeyPress(Key.Enter, OperatingSystem.IsMacOS() ? RawInputModifiers.Meta : RawInputModifiers.Control, PhysicalKey.Enter, null);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("uptime", (await result)?.Text);
        owner.Close();
    }

    [AvaloniaFact]
    public void A_prompt_that_chose_its_own_focus_keeps_it()
    {
        var owner = new Window();
        owner.Show();
        var tree = ConnectionTree.Empty();
        var window = new SavedCommandWindow(null, [new CommandPlace("Every connection", tree.Root)]);
        _ = window.ShowDialog<SavedCommand?>(owner);
        Dispatcher.UIThread.RunJobs();

        Assert.Same(window.NameBox, Focused(window));
        window.Close();
        owner.Close();
    }
}

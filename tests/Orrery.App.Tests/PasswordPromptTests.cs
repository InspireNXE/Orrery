using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Orrery.App.Controls;
using Xunit;

namespace Orrery.App.Tests;

public class PasswordPromptTests
{
    [AvaloniaFact]
    public async Task A_wrong_password_is_turned_down_in_the_same_window()
    {
        var owner = new Window();
        owner.Show();
        var tried = new List<string>();
        var prompt = Dialogs.PromptPasswordAsync(owner, "Master password", "Enter it", password =>
        {
            tried.Add(password);
            return Task.FromResult(password == "right" ? null : "That password is incorrect.");
        });
        await Settle(() => owner.OwnedWindows.Count == 1);
        var window = owner.OwnedWindows.Single();
        var box = window.GetVisualDescendants().OfType<PasswordBox>().Single();
        var ok = window.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "OK"));
        var notice = window.GetVisualDescendants().OfType<Notice>().Single();
        Assert.False(notice.IsVisible);

        box.Text = "wrong";
        ok.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Settle(() => notice.IsVisible);

        Assert.Same(window, owner.OwnedWindows.Single());
        Assert.Equal("That password is incorrect.", notice.Text);
        Assert.Equal("", box.Text);
        Assert.False(prompt.IsCompleted);

        box.Text = "right";
        ok.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Settle(() => prompt.IsCompleted);

        Assert.Equal("right", await prompt);
        Assert.Equal(["wrong", "right"], tried);
        Assert.Empty(owner.OwnedWindows);
    }

    static async Task Settle(Func<bool> done)
    {
        for (int i = 0; i < 500 && !done(); i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(10);
        }

        Assert.True(done());
    }
}

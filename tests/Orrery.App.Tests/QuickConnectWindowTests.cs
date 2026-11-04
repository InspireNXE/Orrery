using Avalonia;
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

public class QuickConnectWindowTests
{
    static (Task<QuickConnectChoice?> Result, QuickConnectWindow Window) Open(string query, out ConnectionEntry web01)
    {
        var tree = ConnectionTree.Empty();
        web01 = new ConnectionEntry { Name = "web-01", Host = "web-01.corp" };
        tree.Add(web01, tree.Root);

        var owner = new Window();
        owner.Show();
        var window = new QuickConnectWindow(tree, [new OpenSession("web-01", "admin@web-01.corp", Protocol.Ssh)]);
        var result = window.ShowOver(owner);
        Dispatcher.UIThread.RunJobs();

        window.GetVisualDescendants().OfType<TextBox>().Single().Text = query;
        Dispatcher.UIThread.RunJobs();
        return (result, window);
    }

    [AvaloniaFact]
    public void An_open_session_that_matches_comes_first()
    {
        var (result, window) = Open("web", out _);

        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        Dispatcher.UIThread.RunJobs();

        Assert.True(result.IsCompleted);
        Assert.Equal(0, result.Result!.SessionIndex);
    }

    [AvaloniaFact]
    public void Alt_enter_on_a_saved_connection_asks_for_its_settings()
    {
        var (result, window) = Open("web", out var web01);

        window.KeyPress(Key.Down, RawInputModifiers.None, PhysicalKey.ArrowDown, null);
        window.KeyPress(Key.Enter, RawInputModifiers.Alt, PhysicalKey.Enter, null);
        Dispatcher.UIThread.RunJobs();

        Assert.True(result.IsCompleted);
        Assert.Same(web01, result.Result!.Entry);
        Assert.True(result.Result.Edit);
    }

    [AvaloniaFact]
    public void The_top_result_stays_highlighted_until_the_pointer_moves_onto_another()
    {
        var (result, window) = Open("web", out _);
        var second = HoverableRows(window)[1].TranslatePoint(new Point(40, 10), window)!.Value;

        // The box came up under a pointer resting on the second row, which hasn't moved.
        window.MouseMove(second);
        window.MouseMove(second);
        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(0, result.Result!.SessionIndex);

        (result, window) = Open("web", out var web01);
        HoverableRows(window);
        window.MouseMove(second);
        window.MouseMove(second + new Point(5, 0));
        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        Dispatcher.UIThread.RunJobs();
        Assert.Same(web01, result.Result!.Entry);
    }

    /// <summary>The rows, with the transparent background App.axaml gives them so the pointer finds them.</summary>
    static List<Border> HoverableRows(QuickConnectWindow window)
    {
        var rows = window.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("row")).ToList();
        foreach (var row in rows)
        {
            row.Background = Avalonia.Media.Brushes.Transparent;
        }

        return rows;
    }
}

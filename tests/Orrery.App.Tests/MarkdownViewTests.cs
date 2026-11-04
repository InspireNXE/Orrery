using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Orrery.App.Controls;
using Xunit;

namespace Orrery.App.Tests;

/// <summary>Release notes as GitHub shows them: the blocks, GitHub's own links, and links that open on a click.</summary>
public class MarkdownViewTests
{
    static readonly Uri ReleasePage = new("https://github.com/InspireNXE/Orrery/releases/tag/v0.2.0");

    static MarkdownView View(string markdown, Uri? page = null, List<Uri>? opened = null) =>
        new(markdown, uri => opened?.Add(uri), page ?? ReleasePage, empty: "Nothing.");

    [AvaloniaFact]
    public void Blocks_read_as_their_text()
    {
        var view = View("""
            # Orrery 0.2.0

            Some **bold**, *italic*, ~~gone~~ and `code`.

            - one
              - nested
            - [x] done
            - [ ] not yet

            3. third
            4. fourth

            > quoted

            ```
            dotnet build
            ```

            | Copy | File |
            | --- | :---: |
            | Windows | `Orrery-Setup.exe` |

            ---
            """);

        Assert.Equal(
            "Orrery 0.2.0\nSome bold, italic, gone and code.\n• one\n  • nested\n☑ done\n☐ not yet\n3. third\n4. fourth\nquoted\n"
            + "dotnet build\nCopy | File\nWindows | Orrery-Setup.exe",
            view.Text);
    }

    [AvaloniaFact]
    public void Emphasis_code_and_tables_are_styled_not_marked()
    {
        var view = View("Some **bold** and `code`.\n\n| A | B |\n| --- | ---: |\n| 1 | 2 |");
        var window = Show(view);

        var paragraph = view.GetVisualDescendants().OfType<SelectableTextBlock>().First();
        Assert.Contains(paragraph.Inlines!, i => i is Avalonia.Controls.Documents.Bold);
        Assert.Contains(paragraph.Inlines!, i => i is Avalonia.Controls.Documents.Run { Text: "code" } run && run.FontFamily == Dialogs.MonoFont);

        var table = view.GetVisualDescendants().OfType<Grid>().Single(g => g.Children.OfType<Border>().Any(b => b.Child is SelectableTextBlock));
        var cells = table.Children.OfType<Border>().Select(b => (SelectableTextBlock)b.Child!).ToList();
        Assert.Equal(FontWeight.SemiBold, cells[0].FontWeight);
        Assert.Equal(TextAlignment.Right, cells[3].TextAlignment);
        window.Close();
    }

    [AvaloniaFact]
    public void A_GitHub_release_gets_GitHubs_short_links()
    {
        var view = View(
            "* Faster start by @octo-cat in https://github.com/InspireNXE/Orrery/pull/12, see #7\n"
            + "* Upstream: https://github.com/Devolutions/IronRDP/issues/3\n\n"
            + "**Full Changelog**: https://github.com/InspireNXE/Orrery/compare/v0.1.0...v0.2.0");

        Assert.Equal(
            "• Faster start by @octo-cat in #12, see #7\n• Upstream: Devolutions/IronRDP#3\nFull Changelog: v0.1.0...v0.2.0",
            view.Text);
        Assert.Equal(
            [
                ("@octo-cat", new Uri("https://github.com/octo-cat")),
                ("#12", new Uri("https://github.com/InspireNXE/Orrery/pull/12")),
                ("#7", new Uri("https://github.com/InspireNXE/Orrery/issues/7")),
                ("Devolutions/IronRDP#3", new Uri("https://github.com/Devolutions/IronRDP/issues/3")),
                ("v0.1.0...v0.2.0", new Uri("https://github.com/InspireNXE/Orrery/compare/v0.1.0...v0.2.0")),
            ],
            view.Links);
    }

    [AvaloniaFact]
    public void Notes_from_elsewhere_keep_their_text_as_written()
    {
        var view = View("Thanks @octo-cat for #7: https://github.com/InspireNXE/Orrery/pull/12", new Uri("https://example.com/notes"));

        Assert.Equal("Thanks @octo-cat for #7: https://github.com/InspireNXE/Orrery/pull/12", view.Text);
        Assert.Equal([("https://github.com/InspireNXE/Orrery/pull/12", new Uri("https://github.com/InspireNXE/Orrery/pull/12"))], view.Links);
    }

    [AvaloniaFact]
    public void Only_web_addresses_are_links()
    {
        var view = View("[run](javascript:alert(1)) [file](file:///C:/x) [docs](../blob/main/README.md) [site](http://example.com) user@example.com");

        Assert.Equal("run file docs site user@example.com", view.Text);
        Assert.Equal(
            [
                ("docs", new Uri("https://github.com/InspireNXE/Orrery/releases/blob/main/README.md")),
                ("site", new Uri("http://example.com")),
            ],
            view.Links);
    }

    [AvaloniaFact]
    public void Nothing_to_show_says_so()
    {
        Assert.Equal("Nothing.", View("").Text);
        Assert.Equal("Nothing.", View("<!-- a comment -->").Text);
    }

    [AvaloniaFact]
    public void Clicking_a_link_opens_it_and_the_text_around_it_doesnt()
    {
        var opened = new List<Uri>();
        var view = View("Fixed in https://github.com/InspireNXE/Orrery/pull/12 today.", opened: opened);
        var window = Show(view);
        var block = view.GetVisualDescendants().OfType<SelectableTextBlock>().Single();
        var target = new Uri("https://github.com/InspireNXE/Orrery/pull/12");

        var link = view.LinkBounds(block, target)!.Value;
        var onLink = block.TranslatePoint(link.Center, window)!.Value;
        Assert.Equal(target, view.LinkAt(block, link.Center));
        Assert.Same(block, window.InputHitTest(onLink));
        window.MouseDown(onLink, MouseButton.Left);
        window.MouseUp(onLink, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal([target], opened);

        var before = block.TranslatePoint(new Point(2, link.Center.Y), window)!.Value;
        window.MouseDown(before, MouseButton.Left);
        window.MouseUp(before, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        Assert.Single(opened);
        Assert.Null(view.LinkAt(block, new Point(2, link.Center.Y)));
        window.Close();
    }

    static Window Show(Control content)
    {
        var window = new Window { Width = 520, Height = 600, Content = content };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        // Pointer input is hit-tested against what was last rendered, so render once before clicking.
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
        return window;
    }
}

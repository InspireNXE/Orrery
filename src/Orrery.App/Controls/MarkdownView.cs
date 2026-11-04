using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Extensions.TaskLists;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using MarkdownTable = Markdig.Extensions.Tables.Table;

namespace Orrery.App.Controls;

/// <summary>
/// GitHub-flavored Markdown shown the way GitHub shows it, for release notes: headings, paragraphs with emphasis and
/// code, bullet, numbered and task lists, quotes, code blocks, tables and rules. Markdig parses it. Links are text that
/// opens in the browser on a click, and only http and https ones are links. For a GitHub release (<c>page</c> on
/// github.com), bare pull request, issue, compare and commit addresses shorten to <c>#12</c> and the like, and
/// <c>#12</c> and <c>@name</c> link up, as on GitHub. Raw HTML shows as its text.
/// </summary>
sealed partial class MarkdownView : StackPanel
{
    static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UsePipeTables()
        .UseTaskLists()
        .UseEmphasisExtras(Markdig.Extensions.EmphasisExtras.EmphasisExtraOptions.Strikethrough)
        .UseAutoLinks()
        .Build();

    static readonly IBrush CodeBrush = new SolidColorBrush(Color.Parse("#1C2028")).ToImmutable();
    static readonly IBrush HeaderBrush = new SolidColorBrush(Color.Parse("#171A20")).ToImmutable();
    static readonly IBrush QuoteBarBrush = new SolidColorBrush(Color.Parse("#3A404C")).ToImmutable();
    static readonly Cursor HandCursor = new(StandardCursorType.Hand);
    static readonly Cursor TextCursor = new(StandardCursorType.Ibeam);

    readonly Action<Uri> _open;
    readonly Uri? _page;
    readonly string? _repository;
    readonly List<string> _lines = [];
    readonly List<(string Text, Uri Target)> _allLinks = [];
    readonly Dictionary<SelectableTextBlock, List<LinkRange>> _links = [];
    string _prefix = "";

    /// <param name="markdown">The Markdown.</param>
    /// <param name="open">Opens a link that was clicked.</param>
    /// <param name="page">
    /// Where the Markdown is from: relative links resolve against it, and a github.com page turns on GitHub's own links.
    /// </param>
    /// <param name="empty">Shown, muted, when there's nothing to show.</param>
    public MarkdownView(string markdown, Action<Uri> open, Uri? page = null, string empty = "")
    {
        _open = open;
        _page = page;
        _repository = page is { Host: "github.com" } && page.Segments.Length >= 3
            ? $"{page.Segments[1].TrimEnd('/')}/{page.Segments[2].TrimEnd('/')}"
            : null;
        Spacing = 10;

        foreach (var block in Markdown.Parse(markdown ?? "", Pipeline))
        {
            if (Block(block, 0) is { } control)
            {
                Children.Add(control);
            }
        }

        if (Children.Count == 0 && empty.Length > 0)
        {
            Children.Add(new TextBlock { Text = empty, Foreground = Dialogs.MutedBrush, TextWrapping = TextWrapping.Wrap });
            _lines.Add(empty);
        }
    }

    /// <summary>What's shown, as plain text: a line per paragraph, heading, list item or table row, bullets as "• ".</summary>
    public string Text => string.Join('\n', _lines);

    /// <summary>Every link shown, in order: its text and where it goes.</summary>
    internal IReadOnlyList<(string Text, Uri Target)> Links => _allLinks;

    /// <summary>The link under <paramref name="point"/> (in <paramref name="block"/>'s coordinates), if any.</summary>
    internal Uri? LinkAt(SelectableTextBlock block, Point point)
    {
        if (!_links.TryGetValue(block, out var links))
        {
            return null;
        }

        var hit = block.TextLayout.HitTestPoint(point);
        if (!hit.IsInside)
        {
            return null;
        }

        return links.FirstOrDefault(l => hit.TextPosition >= l.Start && hit.TextPosition < l.Start + l.Length)?.Target;
    }

    /// <summary>Where a link's text is in <paramref name="block"/>, for tests: the bounds of its first line.</summary>
    internal Rect? LinkBounds(SelectableTextBlock block, Uri target) =>
        _links.TryGetValue(block, out var links) && links.FirstOrDefault(l => l.Target == target) is { } link
            ? block.TextLayout.HitTestTextRange(link.Start, link.Length).FirstOrDefault()
            : null;

    Control? Block(Block block, int depth)
    {
        switch (block)
        {
            case HeadingBlock heading:
            {
                var text = TextBlockFor(heading.Inline);
                text.FontWeight = FontWeight.SemiBold;
                text.FontSize = heading.Level switch { 1 => 22, 2 => 18, 3 => 16, _ => 14 };
                text.LineHeight = double.NaN;
                if (heading.Level > 2)
                {
                    return text;
                }

                return new Border
                {
                    BorderBrush = Pane.LineBrush,
                    BorderThickness = new Thickness(0, 0, 0, 1),
                    Padding = new Thickness(0, 0, 0, 5),
                    Margin = new Thickness(0, 4, 0, 0),
                    Child = text,
                };
            }

            case ParagraphBlock paragraph:
                return TextBlockFor(paragraph.Inline);

            case ListBlock list:
                return List(list, depth);

            case QuoteBlock quote:
            {
                var inner = Blocks(quote, depth);
                inner.SetValue(TextElement.ForegroundProperty, Dialogs.MutedBrush);
                return new Border
                {
                    BorderBrush = QuoteBarBrush,
                    BorderThickness = new Thickness(3, 0, 0, 0),
                    Padding = new Thickness(12, 0, 0, 0),
                    Child = inner,
                };
            }

            case CodeBlock code:
            {
                var source = string.Join('\n', code.Lines.Lines.Take(code.Lines.Count).Select(l => l.ToString()));
                AddLine(source);
                return new Border
                {
                    Background = CodeBrush,
                    CornerRadius = new CornerRadius(5),
                    Padding = new Thickness(12, 9),
                    Child = new ScrollViewer
                    {
                        HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
                        VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
                        Content = new SelectableTextBlock { Text = source, FontFamily = Dialogs.MonoFont, FontSize = 12.5 },
                    },
                };
            }

            case ThematicBreakBlock:
                return new Border { Height = 1, Background = Pane.LineBrush, Margin = new Thickness(0, 4) };

            case MarkdownTable table:
                return Table(table);

            case HtmlBlock html:
            {
                var source = string.Join('\n', html.Lines.Lines.Take(html.Lines.Count).Select(l => l.ToString()));
                var text = WebUtility.HtmlDecode(HtmlTag().Replace(source, "")).Trim();
                if (text.Length == 0)
                {
                    return null;
                }

                AddLine(text);
                return new SelectableTextBlock { Text = text, TextWrapping = TextWrapping.Wrap };
            }

            case ContainerBlock container:
                return container.Count == 0 ? null : Blocks(container, depth);

            default:
                return null;
        }
    }

    StackPanel Blocks(ContainerBlock container, int depth)
    {
        var panel = new StackPanel { Spacing = 8 };
        foreach (var child in container)
        {
            if (Block(child, depth) is { } control)
            {
                panel.Children.Add(control);
            }
        }

        return panel;
    }

    Control List(ListBlock list, int depth)
    {
        var panel = new StackPanel { Spacing = 4 };
        int number = list.IsOrdered && int.TryParse(list.OrderedStart, out var start) ? start : 1;
        foreach (var item in list.OfType<ListItemBlock>())
        {
            bool task = item.FirstOrDefault() is ParagraphBlock { Inline.FirstChild: TaskList };
            var marker = task ? "" : list.IsOrdered ? $"{number}." : "•";
            number++;

            var content = new StackPanel { Spacing = 6 };
            _prefix = new string(' ', depth * 2) + (marker.Length > 0 ? marker + " " : "");
            foreach (var child in item)
            {
                if (Block(child, depth + 1) is { } control)
                {
                    content.Children.Add(control);
                }
            }

            _prefix = "";
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
            if (marker.Length > 0)
            {
                row.Children.Add(new TextBlock
                {
                    Text = marker,
                    MinWidth = list.IsOrdered ? 20 : 14,
                    Margin = new Thickness(0, 0, 4, 0),
                    TextAlignment = list.IsOrdered ? TextAlignment.Right : TextAlignment.Left,
                    Foreground = Dialogs.MutedBrush,
                    LineHeight = 20,
                });
            }

            Grid.SetColumn(content, 1);
            row.Children.Add(content);
            panel.Children.Add(row);
        }

        return depth == 0 ? panel : new Border { Margin = new Thickness(4, 0, 0, 0), Child = panel };
    }

    /// <summary>
    /// A table as wide as the notes, its cells wrapping: each column gets a share of the width by the longest text in
    /// it, and at least room for its longest word, so a narrow window shows the whole table rather than scrolling it
    /// sideways.
    /// </summary>
    Control Table(MarkdownTable table)
    {
        var rows = table.OfType<TableRow>().ToList();
        int columns = Math.Max(table.ColumnDefinitions.Count, rows.Select(r => r.Count).DefaultIfEmpty(0).Max());
        var widest = new int[columns];
        var longestWord = new int[columns];
        var grid = new Grid();

        for (int r = 0; r < rows.Count; r++)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var cells = new string[columns];
            for (int i = 0; i < rows[r].Count; i++)
            {
                var cell = (TableCell)rows[r][i];
                int column = cell.ColumnIndex >= 0 && cell.ColumnIndex < columns ? cell.ColumnIndex : i;
                var paragraph = cell.OfType<ParagraphBlock>().FirstOrDefault();
                var text = TextBlockFor(paragraph?.Inline, out var plain);
                cells[column] = plain;
                widest[column] = Math.Max(widest[column], plain.Length);
                longestWord[column] = Math.Max(longestWord[column], WordBreak().Split(plain).Max(w => w.Length));
                text.TextAlignment = column < table.ColumnDefinitions.Count
                    ? table.ColumnDefinitions[column].Alignment switch
                    {
                        TableColumnAlign.Center => TextAlignment.Center,
                        TableColumnAlign.Right => TextAlignment.Right,
                        _ => TextAlignment.Left,
                    }
                    : TextAlignment.Left;
                if (rows[r].IsHeader)
                {
                    text.FontWeight = FontWeight.SemiBold;
                }

                var border = new Border
                {
                    BorderBrush = Pane.LineBrush,
                    BorderThickness = new Thickness(column == 0 ? 1 : 0, r == 0 ? 1 : 0, 1, 1),
                    Background = rows[r].IsHeader ? HeaderBrush : null,
                    Padding = new Thickness(10, 5),
                    Child = text,
                };
                Grid.SetRow(border, r);
                Grid.SetColumn(border, column);
                grid.Children.Add(border);
            }

            AddLine(string.Join(" | ", cells.Select(c => c ?? "")));
        }

        for (int c = 0; c < columns; c++)
        {
            // About 10 pixels a character (a little over Inter at 14), plus the cell's padding and borders. A word
            // longer than the cap breaks where it must.
            grid.ColumnDefinitions.Add(new ColumnDefinition(Math.Clamp(widest[c], 4, 40), GridUnitType.Star)
            {
                MinWidth = Math.Min(longestWord[c] * 10 + 24, 180),
            });
        }

        return grid;
    }

    SelectableTextBlock TextBlockFor(ContainerInline? inline)
    {
        var block = TextBlockFor(inline, out var plain);
        AddLine(plain);
        return block;
    }

    SelectableTextBlock TextBlockFor(ContainerInline? inline, out string plain)
    {
        var block = new SelectableTextBlock { TextWrapping = TextWrapping.Wrap, LineHeight = 20 };
        var inlines = new InlineCollection();
        var sink = new Sink();
        if (inline is not null)
        {
            AddInlines(inline, inlines, sink, linked: false);
        }

        block.Inlines = inlines;
        plain = sink.Text.ToString();
        if (sink.Links.Count > 0)
        {
            _links[block] = sink.Links;
            // Hit over its whole area, not only where glyphs are drawn, so a click on a link reaches it.
            block.Background = Brushes.Transparent;
            foreach (var link in sink.Links)
            {
                _allLinks.Add((plain.Substring(link.Start, link.Length), link.Target));
            }

            block.PointerMoved += (_, e) => block.Cursor = LinkAt(block, e.GetPosition(block)) is null ? TextCursor : HandCursor;
            block.AddHandler(PointerReleasedEvent, (_, e) =>
            {
                if (e.InitialPressMouseButton == MouseButton.Left && block.SelectionStart == block.SelectionEnd
                    && LinkAt(block, e.GetPosition(block)) is { } target)
                {
                    _open(target);
                }
            }, RoutingStrategies.Bubble, handledEventsToo: true);
        }

        return block;
    }

    void AddInlines(ContainerInline container, InlineCollection into, Sink sink, bool linked)
    {
        foreach (var inline in container)
        {
            switch (inline)
            {
                case LiteralInline literal:
                    AddLiteral(literal.Content.ToString(), into, sink, linked);
                    break;

                case HtmlEntityInline entity:
                    AddRun(entity.Transcoded.ToString(), into, sink);
                    break;

                case CodeInline code:
                {
                    var run = AddRun(code.Content, into, sink);
                    run.FontFamily = Dialogs.MonoFont;
                    run.FontSize = 12.5;
                    run.Background = CodeBrush;
                    break;
                }

                case LineBreakInline lineBreak:
                    AddRun(lineBreak.IsHard ? "\n" : " ", into, sink);
                    break;

                case TaskList task:
                    AddRun(task.Checked ? "☑" : "☐", into, sink);
                    break;

                case HtmlInline html:
                    if (BreakTag().IsMatch(html.Tag))
                    {
                        AddRun("\n", into, sink);
                    }

                    break;

                case AutolinkInline autolink:
                {
                    var url = autolink.IsEmail ? "mailto:" + autolink.Url : autolink.Url;
                    if (WebLink(url) is { } target)
                    {
                        AddLink(target, into, sink, (span, s) => AddRun(ShortLabel(target) ?? autolink.Url, span.Inlines, s));
                    }
                    else
                    {
                        AddRun(autolink.Url, into, sink);
                    }

                    break;
                }

                case LinkInline link when !linked && WebLink(link.Url) is { } target:
                {
                    var label = link.IsAutoLink ? ShortLabel(target) : null;
                    AddLink(target, into, sink, (span, s) =>
                    {
                        if (label is not null)
                        {
                            AddRun(label, span.Inlines, s);
                        }
                        else if (link.IsImage && link.FirstChild is null)
                        {
                            AddRun("image", span.Inlines, s);
                        }
                        else
                        {
                            AddInlines(link, span.Inlines, s, linked: true);
                        }
                    });
                    break;
                }

                case EmphasisInline emphasis:
                {
                    Span span = emphasis.DelimiterChar == '~'
                        ? new Span { TextDecorations = TextDecorations.Strikethrough }
                        : emphasis.DelimiterCount >= 2 ? new Bold() : new Italic();
                    AddInlines(emphasis, span.Inlines, sink, linked);
                    into.Add(span);
                    break;
                }

                case ContainerInline other:
                    AddInlines(other, into, sink, linked);
                    break;
            }
        }
    }

    /// <summary>Plain text, with GitHub's <c>#12</c> and <c>@name</c> as links on a GitHub release's notes.</summary>
    void AddLiteral(string text, InlineCollection into, Sink sink, bool linked)
    {
        if (_repository is null || linked)
        {
            AddRun(text, into, sink);
            return;
        }

        int at = 0;
        foreach (Match match in GitHubReference().Matches(text))
        {
            AddRun(text[at..match.Index], into, sink);
            var target = match.Groups["issue"].Success
                ? new Uri($"https://github.com/{_repository}/issues/{match.Groups["issue"].Value}")
                : new Uri($"https://github.com/{match.Groups["user"].Value}");
            AddLink(target, into, sink, (span, s) => AddRun(match.Value, span.Inlines, s));
            at = match.Index + match.Length;
        }

        AddRun(text[at..], into, sink);
    }

    void AddLink(Uri target, InlineCollection into, Sink sink, Action<Span, Sink> content)
    {
        var span = new Span { Foreground = Palette.Accent, TextDecorations = TextDecorations.Underline };
        int start = sink.Text.Length;
        content(span, sink);
        into.Add(span);
        if (sink.Text.Length > start)
        {
            sink.Links.Add(new LinkRange(start, sink.Text.Length - start, target));
        }
    }

    static Run AddRun(string text, InlineCollection into, Sink sink)
    {
        var run = new Run(text);
        if (text.Length > 0)
        {
            into.Add(run);
            sink.Text.Append(text);
        }

        return run;
    }

    void AddLine(string text)
    {
        _lines.Add(_prefix + text);
        _prefix = "";
    }

    /// <summary>An http or https address, relative ones resolved against the page; anything else isn't a link.</summary>
    Uri? WebLink(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var target) && (_page is null || !Uri.TryCreate(_page, url, out target)))
        {
            return null;
        }

        return target.Scheme == Uri.UriSchemeHttps || target.Scheme == Uri.UriSchemeHttp ? target : null;
    }

    /// <summary>
    /// How GitHub shows a bare address of a pull request, issue, compare or commit in a release's notes; null for
    /// other addresses, and for notes that aren't a GitHub release's.
    /// </summary>
    internal string? ShortLabel(Uri target)
    {
        if (_repository is null || target.Host != "github.com")
        {
            return null;
        }

        var parts = target.AbsolutePath.Trim('/').Split('/');
        if (parts.Length != 4)
        {
            return null;
        }

        var repository = $"{parts[0]}/{parts[1]}";
        bool here = string.Equals(repository, _repository, StringComparison.OrdinalIgnoreCase);
        return parts[2] switch
        {
            "pull" or "issues" when parts[3].All(char.IsAsciiDigit) => here ? $"#{parts[3]}" : $"{repository}#{parts[3]}",
            "compare" => here ? parts[3] : $"{repository}@{parts[3]}",
            "commit" when parts[3].Length >= 7 => here ? parts[3][..7] : $"{repository}@{parts[3][..7]}",
            _ => null,
        };
    }

    [GeneratedRegex(@"<[^>]*>")]
    private static partial Regex HtmlTag();

    /// <summary>Where a line can break: spaces, and after a hyphen or slash.</summary>
    [GeneratedRegex(@"[\s]+|(?<=[-/])")]
    private static partial Regex WordBreak();

    [GeneratedRegex(@"^<br\s*/?>$", RegexOptions.IgnoreCase)]
    private static partial Regex BreakTag();

    [GeneratedRegex(@"(?<![\w&/#])#(?<issue>\d+)\b|(?<![\w/@.])@(?<user>[A-Za-z0-9](?:[A-Za-z0-9]|-(?=[A-Za-z0-9])){0,38})\b(?!@|\.\w)")]
    private static partial Regex GitHubReference();

    sealed record LinkRange(int Start, int Length, Uri Target);

    sealed class Sink
    {
        public StringBuilder Text { get; } = new();
        public List<LinkRange> Links { get; } = [];
    }
}

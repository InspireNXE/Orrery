using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Media;
using Avalonia.Threading;
using Orrery.Core.Sessions;
using Orrery.Core.Terminal;

namespace Orrery.App.Sessions;

/// <summary>
/// Draws a <see cref="TerminalEmulator"/> and turns keyboard, mouse and clipboard into terminal input
/// for an <see cref="ITerminalSession"/>. Output is coalesced into one repaint per frame.
/// </summary>
public sealed class TerminalView : Control
{
    const double FontSize = 14;
    const int WheelLines = 3;
    static readonly TimeSpan ResizeDelay = TimeSpan.FromMilliseconds(120);

    static readonly FontFamily Mono = new("Cascadia Mono, Cascadia Code, Consolas, Menlo, SF Mono, DejaVu Sans Mono, Liberation Mono, monospace");
    static readonly Typeface Regular = new(Mono);
    static readonly Typeface Bold = new(Mono, FontStyle.Normal, FontWeight.Bold);
    static readonly Typeface Italic = new(Mono, FontStyle.Italic);
    static readonly Typeface BoldItalic = new(Mono, FontStyle.Italic, FontWeight.Bold);

    static readonly Color DefaultForeground = Color.Parse("#D6DCE4");
    static readonly Color DefaultBackground = Color.Parse("#0B0D10");
    static readonly Color SelectionColor = Color.Parse("#3A5A8C");
    static readonly Color CursorColor = Color.Parse("#E6E8EC");

    // A dark-friendly take on the 16 ANSI colours: black, red, green, yellow, blue, magenta, cyan, white, then bright.
    static readonly Color[] Ansi16 =
    [
        Color.Parse("#1B1E24"), Color.Parse("#E06C75"), Color.Parse("#98C379"), Color.Parse("#E5C07B"),
        Color.Parse("#61AFEF"), Color.Parse("#C678DD"), Color.Parse("#56B6C2"), Color.Parse("#C8CCD4"),
        Color.Parse("#5C6370"), Color.Parse("#FF7A85"), Color.Parse("#B5E890"), Color.Parse("#FFD68A"),
        Color.Parse("#82C4FF"), Color.Parse("#DE9BF0"), Color.Parse("#7FD8E3"), Color.Parse("#FFFFFF"),
    ];

    readonly TerminalEmulator _terminal;
    readonly DispatcherTimer _resizeTimer;
    readonly Dictionary<uint, IBrush> _brushes = [];
    double _cellWidth, _cellHeight, _baseline;
    bool _renderQueued;
    int _scrollOffset; // Lines scrolled back from the bottom.

    // Selection, in absolute rows (0 = oldest scrollback line).
    (int Row, int Column)? _selectionStart, _selectionEnd;
    bool _selecting;

    public TerminalView(ITerminalSession session)
    {
        Session = session;
        Focusable = true;
        ClipToBounds = true;
        Cursor = new Cursor(StandardCursorType.Ibeam);
        MeasureCell();

        _terminal = new TerminalEmulator(80, 24);
        _terminal.Reply += reply => Session.Send(Encoding.UTF8.GetBytes(reply));
        _terminal.Updated += QueueRender;
        _terminal.TitleChanged += title => Dispatcher.UIThread.Post(() => TitleChanged?.Invoke(this, title));

        _resizeTimer = new DispatcherTimer { Interval = ResizeDelay };
        _resizeTimer.Tick += (_, _) =>
        {
            _resizeTimer.Stop();
            Session.Resize(_terminal.Columns, _terminal.Rows);
        };

        session.DataReceived += data => _terminal.Feed(data);
        session.StateChanged += (_, e) => ShowState(e.State, e.Message);
        ShowState(session.State, null);
    }

    public ITerminalSession Session { get; }

    /// <summary>The remote shell set its window title (on the UI thread).</summary>
    public event EventHandler<string>? TitleChanged;

    void ShowState(SessionState state, string? message)
    {
        var target = $"{Session.Target.Host}:{Session.Target.Port}";
        var text = state switch
        {
            SessionState.Connecting => $"\u001b[90mConnecting to {target}…\u001b[0m\r\n",
            SessionState.Failed => $"\r\n\u001b[31mConnection failed: {message}\u001b[0m\r\n",
            SessionState.Disconnected => "\r\n\u001b[90m[Disconnected]\u001b[0m\r\n",
            _ => null,
        };

        if (text is not null)
        {
            _terminal.Feed(text);
        }

        if (state == SessionState.Connected)
        {
            Dispatcher.UIThread.Post(() => Focus());
        }
    }

    void MeasureCell()
    {
        var sample = new FormattedText("MMMMMMMMMM", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Regular, FontSize, Brushes.White);
        _cellWidth = sample.WidthIncludingTrailingWhitespace / 10;
        _cellHeight = Math.Ceiling(sample.Height);
        _baseline = sample.Baseline;
    }

    void QueueRender()
    {
        if (_renderQueued) return;
        _renderQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _renderQueued = false;
            InvalidateVisual();
        }, DispatcherPriority.Render);
    }

    // ---- Size ----

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        int columns = Math.Max(2, (int)(e.NewSize.Width / _cellWidth));
        int rows = Math.Max(1, (int)(e.NewSize.Height / _cellHeight));
        if (columns == _terminal.Columns && rows == _terminal.Rows)
        {
            return;
        }

        _terminal.Resize(columns, rows);
        _resizeTimer.Stop();
        _resizeTimer.Start();
    }

    // ---- Drawing ----

    public override void Render(DrawingContext context)
    {
        context.FillRectangle(BrushFor(DefaultBackground), new Rect(Bounds.Size));

        lock (_terminal.SyncRoot)
        {
            _scrollOffset = Math.Clamp(_scrollOffset, 0, _terminal.ScrollbackCount);
            int topRow = _terminal.ScrollbackCount - _scrollOffset;
            var (selStart, selEnd) = OrderedSelection();

            for (int r = 0; r < _terminal.Rows; r++)
            {
                int absolute = topRow + r;
                DrawLine(context, _terminal.LineAt(absolute), absolute, r * _cellHeight, selStart, selEnd);
            }

            if (_terminal.CursorVisible && _scrollOffset == 0)
            {
                var rect = new Rect(_terminal.CursorX * _cellWidth, _terminal.CursorY * _cellHeight, _cellWidth, _cellHeight);
                if (IsFocused)
                {
                    context.FillRectangle(new SolidColorBrush(CursorColor, 0.75), rect);
                }
                else
                {
                    context.DrawRectangle(new Pen(BrushFor(CursorColor), 1), rect.Deflate(0.5));
                }
            }
        }
    }

    void DrawLine(DrawingContext context, TerminalLine line, int absoluteRow, double y,
        (int Row, int Column)? selStart, (int Row, int Column)? selEnd)
    {
        var cells = line.Cells;
        int x = 0;
        while (x < cells.Length)
        {
            // A run of cells that look the same; wide characters are drawn on their own so they stay on grid.
            var style = cells[x].Style;
            bool selected = IsSelected(absoluteRow, x, selStart, selEnd);
            bool wide = x + 1 < cells.Length && cells[x + 1].CodePoint == TerminalCell.WideContinuation;
            int end = x + 1;
            var text = new StringBuilder(CellText(cells[x]));
            if (wide)
            {
                end = x + 2;
            }
            else
            {
                while (end < cells.Length && cells[end].Style == style && IsSelected(absoluteRow, end, selStart, selEnd) == selected
                       && !(end + 1 < cells.Length && cells[end + 1].CodePoint == TerminalCell.WideContinuation)
                       && cells[end].CodePoint != TerminalCell.WideContinuation)
                {
                    text.Append(CellText(cells[end]));
                    end++;
                }
            }

            var (fg, bg) = Colors(style);
            if (selected) bg = SelectionColor;

            var rect = new Rect(x * _cellWidth, y, (end - x) * _cellWidth, _cellHeight);
            if (bg != DefaultBackground)
            {
                context.FillRectangle(BrushFor(bg), rect);
            }

            var content = text.ToString();
            if (!string.IsNullOrWhiteSpace(content) && !style.Flags.HasFlag(CellFlags.Hidden))
            {
                var typeface = (style.Flags & (CellFlags.Bold | CellFlags.Italic)) switch
                {
                    CellFlags.Bold => Bold,
                    CellFlags.Italic => Italic,
                    CellFlags.Bold | CellFlags.Italic => BoldItalic,
                    _ => Regular,
                };
                var brush = style.Flags.HasFlag(CellFlags.Faint) ? new SolidColorBrush(fg, 0.6) : BrushFor(fg);
                context.DrawText(new FormattedText(content, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, FontSize, brush),
                    new Point(rect.X, y));
            }

            if (style.Flags.HasFlag(CellFlags.Underline))
            {
                double uy = y + _baseline + 2;
                context.DrawLine(new Pen(BrushFor(fg), 1), new Point(rect.Left, uy), new Point(rect.Right, uy));
            }

            if (style.Flags.HasFlag(CellFlags.Strike))
            {
                double sy = y + _cellHeight / 2;
                context.DrawLine(new Pen(BrushFor(fg), 1), new Point(rect.Left, sy), new Point(rect.Right, sy));
            }

            x = end;
        }
    }

    static string CellText(TerminalCell cell) => cell.CodePoint switch
    {
        0 or TerminalCell.WideContinuation => " ",
        var cp => char.ConvertFromUtf32(cp),
    };

    (Color Foreground, Color Background) Colors(CellStyle style)
    {
        var fgColor = style.Foreground;

        // Bold text in one of the 8 basic colours uses the bright version, as most terminals do.
        if (style.Flags.HasFlag(CellFlags.Bold) && fgColor.IsPalette && fgColor.PaletteIndex < 8)
        {
            fgColor = TerminalColor.Palette(fgColor.PaletteIndex + 8);
        }

        var fg = Resolve(fgColor, DefaultForeground);
        var bg = Resolve(style.Background, DefaultBackground);
        return style.Flags.HasFlag(CellFlags.Inverse) ? (bg, fg) : (fg, bg);
    }

    static Color Resolve(TerminalColor color, Color fallback)
    {
        if (color.IsDefault) return fallback;
        if (color.IsRgb) return Color.FromRgb((byte)(color.Rgb24 >> 16), (byte)(color.Rgb24 >> 8), (byte)color.Rgb24);

        int i = color.PaletteIndex;
        if (i < 16) return Ansi16[i];
        if (i < 232)
        {
            // 6×6×6 colour cube.
            i -= 16;
            static byte Level(int n) => (byte)(n == 0 ? 0 : 55 + n * 40);
            return Color.FromRgb(Level(i / 36), Level(i / 6 % 6), Level(i % 6));
        }

        var gray = (byte)(8 + (i - 232) * 10);
        return Color.FromRgb(gray, gray, gray);
    }

    IBrush BrushFor(Color color)
    {
        if (!_brushes.TryGetValue(color.ToUInt32(), out var brush))
        {
            brush = new SolidColorBrush(color).ToImmutable();
            _brushes[color.ToUInt32()] = brush;
        }

        return brush;
    }

    // ---- Keyboard ----

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        var mods = e.KeyModifiers;
        bool mac = OperatingSystem.IsMacOS();
        bool ctrl = mods.HasFlag(KeyModifiers.Control), alt = mods.HasFlag(KeyModifiers.Alt), shift = mods.HasFlag(KeyModifiers.Shift);

        // Copy and paste: Ctrl+Shift+C/V (Cmd+C/V on macOS), Shift+Insert, Ctrl+Insert.
        bool copyCombo = mac ? mods == KeyModifiers.Meta && e.Key == Key.C : (ctrl && shift && e.Key == Key.C) || (ctrl && !shift && e.Key == Key.Insert);
        bool pasteCombo = mac ? mods == KeyModifiers.Meta && e.Key == Key.V : (ctrl && shift && e.Key == Key.V) || (shift && !ctrl && e.Key == Key.Insert);
        if (copyCombo) { e.Handled = true; _ = CopyAsync(); return; }
        if (pasteCombo) { e.Handled = true; _ = PasteAsync(); return; }

        // Shift+PageUp/PageDown scroll the history locally.
        if (shift && !ctrl && e.Key is Key.PageUp or Key.PageDown)
        {
            e.Handled = true;
            ScrollBy(e.Key == Key.PageUp ? _terminal.Rows - 1 : -(_terminal.Rows - 1));
            return;
        }

        var terminalMods = (shift ? TerminalModifiers.Shift : 0) | (alt ? TerminalModifiers.Alt : 0) | (ctrl ? TerminalModifiers.Control : 0);
        if (SpecialKey(e.Key) is { } special)
        {
            e.Handled = true;
            Send(TerminalKeys.Encode(special, terminalMods, _terminal.CursorKeysApplication));
            return;
        }

        // Ctrl+Alt is AltGr on Windows keyboards: let it produce text instead.
        bool altGr = ctrl && alt && !mac;
        if (ctrl && !altGr && ControlKeyChar(e.Key) is { } c && TerminalKeys.ControlCharacter(c) is { } control)
        {
            e.Handled = true;
            Send(alt ? "\u001b" + control : control);
            return;
        }

        // Alt+key sends ESC then the key (what shells and Emacs-style bindings expect).
        if (alt && !ctrl && !mac && e.KeySymbol is { Length: 1 } symbol && !char.IsControl(symbol[0]))
        {
            e.Handled = true;
            Send("\u001b" + symbol);
        }
    }

    protected override void OnTextInput(TextInputEventArgs e)
    {
        base.OnTextInput(e);
        if (!string.IsNullOrEmpty(e.Text) && !e.Text.Any(char.IsControl))
        {
            e.Handled = true;
            Send(e.Text);
        }
    }

    static TerminalKey? SpecialKey(Key key) => key switch
    {
        Key.Enter => TerminalKey.Enter,
        Key.Back => TerminalKey.Backspace,
        Key.Tab => TerminalKey.Tab,
        Key.Escape => TerminalKey.Escape,
        Key.Up => TerminalKey.Up,
        Key.Down => TerminalKey.Down,
        Key.Left => TerminalKey.Left,
        Key.Right => TerminalKey.Right,
        Key.Home => TerminalKey.Home,
        Key.End => TerminalKey.End,
        Key.Insert => TerminalKey.Insert,
        Key.Delete => TerminalKey.Delete,
        Key.PageUp => TerminalKey.PageUp,
        Key.PageDown => TerminalKey.PageDown,
        >= Key.F1 and <= Key.F12 => TerminalKey.F1 + (key - Key.F1),
        _ => null,
    };

    static char? ControlKeyChar(Key key) => key switch
    {
        >= Key.A and <= Key.Z => (char)('a' + (key - Key.A)),
        Key.Space => ' ',
        Key.OemOpenBrackets => '[',
        Key.OemCloseBrackets => ']',
        Key.OemPipe or Key.OemBackslash => '\\',
        >= Key.D2 and <= Key.D8 => (char)('2' + (key - Key.D2)),
        Key.OemMinus => '_',
        _ => null,
    };

    void Send(string text)
    {
        if (_scrollOffset != 0)
        {
            _scrollOffset = 0;
            InvalidateVisual();
        }

        ClearSelection();
        Session.Send(Encoding.UTF8.GetBytes(text));
    }

    // ---- Mouse: focus, scrolling, selection ----

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        e.Handled = true;
        int lines = (int)Math.Round(e.Delta.Y * WheelLines);
        if (lines == 0) return;

        // Full-screen programs (less, vim, man) scroll with arrow keys; there is no history to show.
        if (_terminal.AlternateScreen)
        {
            var key = lines > 0 ? TerminalKey.Up : TerminalKey.Down;
            var sequence = TerminalKeys.Encode(key, TerminalModifiers.None, _terminal.CursorKeysApplication);
            Session.Send(Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat(sequence, Math.Abs(lines)))));
            return;
        }

        ScrollBy(lines);
    }

    void ScrollBy(int lines)
    {
        _scrollOffset = Math.Clamp(_scrollOffset + lines, 0, _terminal.ScrollbackCount);
        InvalidateVisual();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        var point = e.GetCurrentPoint(this);
        if (point.Properties.IsLeftButtonPressed)
        {
            _selectionStart = _selectionEnd = CellAt(point.Position);
            _selecting = true;
            e.Pointer.Capture(this);
            InvalidateVisual();
        }
        else if (point.Properties.IsRightButtonPressed)
        {
            // Right-click copies a selection, or pastes when there isn't one (as in Windows terminals).
            _ = HasSelection ? CopyAsync() : PasteAsync();
        }

        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_selecting)
        {
            _selectionEnd = CellAt(e.GetPosition(this));
            InvalidateVisual();
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_selecting)
        {
            _selecting = false;
            e.Pointer.Capture(null);
            if (_selectionStart == _selectionEnd)
            {
                ClearSelection();
            }
        }
    }

    (int Row, int Column) CellAt(Point position)
    {
        lock (_terminal.SyncRoot)
        {
            int row = Math.Clamp((int)(position.Y / _cellHeight), 0, _terminal.Rows - 1);
            int column = Math.Clamp((int)(position.X / _cellWidth), 0, _terminal.Columns - 1);
            return (_terminal.ScrollbackCount - _scrollOffset + row, column);
        }
    }

    bool HasSelection => _selectionStart is not null && _selectionEnd is not null && _selectionStart != _selectionEnd;

    ((int Row, int Column)? Start, (int Row, int Column)? End) OrderedSelection()
    {
        if (!HasSelection) return (null, null);
        var (a, b) = (_selectionStart!.Value, _selectionEnd!.Value);
        return a.Row < b.Row || (a.Row == b.Row && a.Column <= b.Column) ? (a, b) : (b, a);
    }

    static bool IsSelected(int row, int column, (int Row, int Column)? start, (int Row, int Column)? end)
    {
        if (start is not { } s || end is not { } e) return false;
        if (row < s.Row || row > e.Row) return false;
        if (row == s.Row && column < s.Column) return false;
        if (row == e.Row && column > e.Column) return false;
        return true;
    }

    void ClearSelection()
    {
        if (_selectionStart is null) return;
        _selectionStart = _selectionEnd = null;
        InvalidateVisual();
    }

    async Task CopyAsync()
    {
        var (start, end) = OrderedSelection();
        if (start is not { } s || end is not { } e || TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard)
        {
            return;
        }

        await clipboard.SetTextAsync(_terminal.GetText(s.Row, s.Column, e.Row, e.Column));
        ClearSelection();
    }

    async Task PasteAsync()
    {
        if (TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard || await clipboard.TryGetTextAsync() is not { Length: > 0 } text)
        {
            return;
        }

        Send(TerminalKeys.Paste(text, _terminal.BracketedPaste));
    }

    protected override void OnGotFocus(GotFocusEventArgs e)
    {
        base.OnGotFocus(e);
        InvalidateVisual();
    }

    protected override void OnLostFocus(Avalonia.Interactivity.RoutedEventArgs e)
    {
        base.OnLostFocus(e);
        InvalidateVisual();
    }
}

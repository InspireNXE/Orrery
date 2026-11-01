using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Media;
using Avalonia.Threading;
using Orrery.App.Controls;
using Orrery.Core.Sessions;
using Orrery.Core.Settings;
using Orrery.Core.Terminal;

namespace Orrery.App.Sessions;

/// <summary>
/// Draws a <see cref="TerminalEmulator"/> and turns keyboard, mouse and clipboard into terminal input
/// for an <see cref="ITerminalSession"/>. Output is coalesced into one repaint per frame.
/// </summary>
public sealed class TerminalView : Control
{
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

    TerminalColorScheme _colorScheme;
    TerminalPalette _palette = TerminalPalette.Standard;

    readonly TerminalEmulator _terminal;
    readonly DispatcherTimer _resizeTimer;
    readonly Dictionary<uint, IBrush> _brushes = [];
    double _fontSize = AppSettings.DefaultTerminalFontSize;
    double _cellWidth, _cellHeight, _baseline;
    bool _renderQueued;
    int _scrollOffset; // Lines scrolled back from the bottom.

    // Selection, in absolute rows (0 = oldest scrollback line).
    (int Row, int Column)? _selectionStart, _selectionEnd;
    bool _selecting;

    // A text cursor for selecting; the plain arrow while a program has the mouse (Shift brings selection back).
    static readonly Cursor TextCursor = new(StandardCursorType.Ibeam);
    static readonly Cursor ArrowCursor = new(StandardCursorType.Arrow);

    // Mouse reporting (htop, tmux, vim): the button held down, and the cell last reported while moving.
    TerminalMouseButton _reportedButton = TerminalMouseButton.None;
    (int Column, int Row)? _lastReportedCell;

    public TerminalView(ITerminalSession session)
    {
        Session = session;
        Focusable = true;
        ClipToBounds = true;
        Cursor = TextCursor;
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

        _terminal.Updated += QueueAnnounce;
        _announceTimer = new DispatcherTimer { Interval = AnnounceTick };
        _announceTimer.Tick += (_, _) => SayDue();
        VisualChildren.Add(_spoken);
        LogicalChildren.Add(_spoken);

        session.DataReceived += data => _terminal.Feed(data);
        session.StateChanged += (_, e) => ShowState(e.State, e.Message);
        ShowState(session.State, null);
    }

    // ---- Screen reader ----

    static readonly TimeSpan AnnounceTick = TimeSpan.FromMilliseconds(100);
    readonly TerminalAnnouncer _announcer = new();
    readonly LiveText _spoken = new("Orrery.Terminal", ScreenReader.CurrentThenMostRecent);
    readonly DispatcherTimer _announceTimer;
    volatile bool _announcements;
    bool _announceQueued;

    /// <summary>Read new output out through the screen reader, leaving out the echo of what's typed.</summary>
    public bool Announcements
    {
        get => _announcements;
        set
        {
            if (value == _announcements)
            {
                return;
            }

            _announcements = value;
            _announcer.Reset(Snapshot());
            if (!value)
            {
                _announceTimer.Stop();
            }
        }
    }

    /// <summary>What was read out last, for tests.</summary>
    internal string Spoken => _spoken.Text;

    // Called on the session's thread as output arrives; the comparing is done once per batch, on the UI thread.
    void QueueAnnounce()
    {
        if (!_announcements || _announceQueued)
        {
            return;
        }

        _announceQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _announceQueued = false;
            if (!_announcements)
            {
                return;
            }

            var screen = Snapshot();
            if (!IsEffectivelyVisible)
            {
                // A tab in the background keeps quiet; it starts from what's on screen when it's shown again.
                _announcer.Reset(screen);
                return;
            }

            _announcer.Update(screen, DateTime.UtcNow);
            if (_announcer.HasPending)
            {
                _announceTimer.Start();
            }
        }, DispatcherPriority.Background);
    }

    void SayDue()
    {
        if (_announcer.TakeIfDue(DateTime.UtcNow) is { } text)
        {
            _spoken.Say(text);
        }

        if (!_announcer.HasPending)
        {
            _announceTimer.Stop();
        }
    }

    void NoteTyped(string text)
    {
        if (_announcements)
        {
            _announcer.Typed(text, DateTime.UtcNow);
        }
    }

    TerminalScreenText Snapshot()
    {
        lock (_terminal.SyncRoot)
        {
            int count = _terminal.Rows;
            var rows = new string[count];
            var wrapped = new bool[count];
            for (int i = 0; i < count; i++)
            {
                var line = _terminal.ScreenLine(i);
                wrapped[i] = line.Wrapped;
                rows[i] = line.Wrapped ? line.Text() : line.Text().TrimEnd();
            }

            return new TerminalScreenText(rows, wrapped, _terminal.LinesScrolled, _terminal.CursorY);
        }
    }

    /// <summary>The rows on show (scrolled back or not), trailing blanks and blank rows at the end left off.</summary>
    internal string ScreenText()
    {
        lock (_terminal.SyncRoot)
        {
            var text = new StringBuilder();
            int top = _terminal.ScrollbackCount - _scrollOffset;
            for (int row = 0; row < _terminal.Rows; row++)
            {
                var line = _terminal.LineAt(top + row);
                text.Append(line.Wrapped ? line.Text() : line.Text().TrimEnd());
                if (!line.Wrapped)
                {
                    text.Append('\n');
                }
            }

            return text.ToString().TrimEnd();
        }
    }

    /// <summary>Where the cursor is, counting from 1 at the top left.</summary>
    internal (int Line, int Column) CursorPosition
    {
        get
        {
            lock (_terminal.SyncRoot)
            {
                return (_terminal.CursorY + 1, _terminal.CursorX + 1);
            }
        }
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new TerminalAutomationPeer(this);

    public ITerminalSession Session { get; }

    /// <summary>The remote shell set its window title (on the UI thread).</summary>
    public event EventHandler<string>? TitleChanged;

    /// <summary>
    /// Ctrl+= or Ctrl+wheel up (+1), Ctrl+- or Ctrl+wheel down (-1), Ctrl+0 (0, back to the default).
    /// The window applies the new size to every terminal, so they stay alike.
    /// </summary>
    public event EventHandler<int>? TextSizeRequested;

    /// <summary>The terminal's size in character cells.</summary>
    public (int Columns, int Rows) GridSize => (_terminal.Columns, _terminal.Rows);

    /// <summary>The 16 colors programs pick from. Changing it repaints what's on screen in the new colors.</summary>
    public TerminalColorScheme ColorScheme
    {
        get => _colorScheme;
        set
        {
            if (value == _colorScheme)
            {
                return;
            }

            _colorScheme = value;
            _palette = TerminalPalette.For(value, _colorVision);
            InvalidateVisual();
        }
    }

    ColorVision _colorVision;

    /// <summary>The user's color vision, which tunes the color-blind scheme. Changing it repaints too.</summary>
    public ColorVision ColorVision
    {
        get => _colorVision;
        set
        {
            if (value == _colorVision)
            {
                return;
            }

            _colorVision = value;
            _palette = TerminalPalette.For(_colorScheme, value);
            InvalidateVisual();
        }
    }

    /// <summary>The color a palette index (0 to 15) draws in, for tests.</summary>
    internal Color AnsiColor(int index) => _palette.Ansi[index];

    /// <summary>Text size in points. Changing it keeps the view's size and re-flows the rows and columns.</summary>
    public double TextSize
    {
        get => _fontSize;
        set
        {
            if (value == _fontSize)
            {
                return;
            }

            _fontSize = value;
            MeasureCell();
            FitToSize(Bounds.Size);
            InvalidateVisual();
        }
    }

    void ShowState(SessionState state, string? message)
    {
        // Each line resets attributes first, so a host's hidden or odd colors can't hide or disguise it.
        var target = $"{Session.Target.Host}:{Session.Target.Port}";
        var text = (state, Session as ILocalSession) switch
        {
            (SessionState.Connecting, { } local) => $"\u001b[0m\u001b[90mStarting {Printable(local.ProgramName)}…\u001b[0m\r\n",
            (SessionState.Connecting, _) => $"\u001b[0m\u001b[90mConnecting to {target}…\u001b[0m\r\n",
            (SessionState.Failed, { } local) => $"\r\n\u001b[0m\u001b[31mCouldn't start {Printable(local.ProgramName)}: {Printable(message)}\u001b[0m\r\n",
            (SessionState.Failed, _) => $"\r\n\u001b[0m\u001b[31mConnection failed: {Printable(message)}\u001b[0m\r\n",
            (SessionState.Disconnected, { ExitCode: { } code }) => $"\r\n\u001b[0m\u001b[90m[Exited with code {code}]\u001b[0m\r\n",
            (SessionState.Disconnected, { }) => "\r\n\u001b[0m\u001b[90m[Ended]\u001b[0m\r\n",
            (SessionState.Disconnected, _) => "\r\n\u001b[0m\u001b[90m[Disconnected]\u001b[0m\r\n",
            _ => null,
        };

        if (text is not null)
        {
            _terminal.Feed(text);
        }

        if (state == SessionState.Connected)
        {
            Dispatcher.UIThread.Post(() => SessionFocus.TakeIfFree(this));
        }
    }

    const int MaxMessageLength = 300;

    /// <summary>
    /// A failure message as one plain line: it can come from the server (its disconnect reason) or an agent's key
    /// comment, so control characters that would start escape sequences become '?', and it's kept short.
    /// </summary>
    static string Printable(string? message)
    {
        if (string.IsNullOrEmpty(message)) return "";

        bool cut = message.Length > MaxMessageLength;
        int length = cut ? MaxMessageLength - 1 : message.Length;
        if (cut && char.IsHighSurrogate(message[length - 1])) length--; // Don't leave half a surrogate pair behind.

        var text = new StringBuilder(MaxMessageLength);
        foreach (char c in message.AsSpan(0, length))
        {
            text.Append(c switch
            {
                '\r' or '\n' or '\t' => ' ',
                < ' ' or '\u007f' or (>= '\u0080' and <= '\u009f') => '?',
                _ => c,
            });
        }

        return cut ? text.Append('…').ToString() : text.ToString();
    }

    void MeasureCell()
    {
        var sample = new FormattedText("MMMMMMMMMM", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Regular, _fontSize, Brushes.White);
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
        FitToSize(e.NewSize);
    }

    void FitToSize(Size size)
    {
        if (size.Width <= 0 || size.Height <= 0)
        {
            return;
        }

        int columns = Math.Max(2, (int)(size.Width / _cellWidth));
        int rows = Math.Max(1, (int)(size.Height / _cellHeight));
        if (columns == _terminal.Columns && rows == _terminal.Rows)
        {
            return;
        }

        _terminal.Resize(columns, rows);
        _resizeTimer.Stop();
        _resizeTimer.Start();
        _announcer.Reset(Snapshot());
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
                context.DrawText(new FormattedText(content, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, _fontSize, brush),
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

        // Bold text in one of the 8 basic colors uses the bright version, as most terminals do.
        if (style.Flags.HasFlag(CellFlags.Bold) && fgColor.IsPalette && fgColor.PaletteIndex < 8)
        {
            fgColor = TerminalColor.Palette(fgColor.PaletteIndex + 8);
        }

        var fg = Resolve(fgColor, DefaultForeground);
        var bg = Resolve(style.Background, DefaultBackground);
        return style.Flags.HasFlag(CellFlags.Inverse) ? (bg, fg) : (fg, bg);
    }

    Color Resolve(TerminalColor color, Color fallback)
    {
        if (color.IsDefault) return fallback;
        if (color.IsRgb) return Color.FromRgb((byte)(color.Rgb24 >> 16), (byte)(color.Rgb24 >> 8), (byte)color.Rgb24);

        int i = color.PaletteIndex;
        if (i < 16) return _palette.Ansi[i];
        if (i < 232)
        {
            // 6×6×6 color cube.
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

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        UpdatePointerCursor(e.KeyModifiers);
    }

    void UpdatePointerCursor(KeyModifiers modifiers)
    {
        var cursor = _terminal.MouseTracking != MouseTracking.None && !modifiers.HasFlag(KeyModifiers.Shift) ? ArrowCursor : TextCursor;
        if (Cursor != cursor)
        {
            Cursor = cursor;
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        UpdatePointerCursor(e.KeyModifiers);
        var mods = e.KeyModifiers;
        bool mac = OperatingSystem.IsMacOS();
        bool ctrl = mods.HasFlag(KeyModifiers.Control), alt = mods.HasFlag(KeyModifiers.Alt), shift = mods.HasFlag(KeyModifiers.Shift);

        // Copy and paste: Ctrl+Shift+C/V (Cmd+C/V on macOS), Shift+Insert, Ctrl+Insert.
        bool copyCombo = mac ? mods == KeyModifiers.Meta && e.Key == Key.C : (ctrl && shift && e.Key == Key.C) || (ctrl && !shift && e.Key == Key.Insert);
        bool pasteCombo = mac ? mods == KeyModifiers.Meta && e.Key == Key.V : (ctrl && shift && e.Key == Key.V) || (shift && !ctrl && e.Key == Key.Insert);
        if (copyCombo) { e.Handled = true; _ = CopyAsync(); return; }
        if (pasteCombo) { e.Handled = true; _ = PasteAsync(); return; }

        // Select all: Ctrl+Shift+A (Cmd+A on macOS). Plain Ctrl+A goes to the session, where shells and tmux use it.
        if (mac ? mods == KeyModifiers.Meta && e.Key == Key.A : ctrl && shift && !alt && e.Key == Key.A)
        {
            e.Handled = true;
            SelectAll();
            return;
        }

        // Text size: Ctrl+=, Ctrl+-, Ctrl+0 (Cmd on macOS), as in most terminals.
        if (mods == (mac ? KeyModifiers.Meta : KeyModifiers.Control) && TextSizeStep(e.Key) is { } step)
        {
            e.Handled = true;
            TextSizeRequested?.Invoke(this, step);
            return;
        }

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
            if (special == TerminalKey.Enter)
            {
                NoteTyped("\n");
            }
            else if (special == TerminalKey.Backspace)
            {
                _announcer.Erased();
            }

            Send(TerminalKeys.Encode(special, terminalMods, _terminal.CursorKeysApplication, shellPrompt: !_terminal.AlternateScreen));
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
            NoteTyped(e.Text);
            Send(e.Text);
        }
    }

    static int? TextSizeStep(Key key) => key switch
    {
        Key.OemPlus or Key.Add => 1,
        Key.OemMinus or Key.Subtract => -1,
        Key.D0 or Key.NumPad0 => 0,
        _ => null,
    };

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
        if (e.KeyModifiers == (OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control))
        {
            if (e.Delta.Y != 0)
            {
                TextSizeRequested?.Invoke(this, Math.Sign(e.Delta.Y));
            }

            return;
        }

        if (Reporting(e))
        {
            // Each notch is one wheel "button" press for the program.
            int notches = (int)Math.Round(e.Delta.Y);
            if (notches == 0 && e.Delta.Y != 0) notches = Math.Sign(e.Delta.Y);
            for (int i = 0; i < Math.Abs(notches); i++)
            {
                Report(TerminalMouseEvent.Press, notches > 0 ? TerminalMouseButton.WheelUp : TerminalMouseButton.WheelDown, e);
            }

            return;
        }

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
        if (Reporting(e) && ToMouseButton(point.Properties.PointerUpdateKind) is { } button)
        {
            ClearSelection();
            _reportedButton = button;
            e.Pointer.Capture(this);
            Report(TerminalMouseEvent.Press, button, e);
            e.Handled = true;
            return;
        }

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
        UpdatePointerCursor(e.KeyModifiers);
        if (Reporting(e) && !_selecting)
        {
            if (TerminalMouse.Wants(_terminal.MouseTracking, TerminalMouseEvent.Motion, _reportedButton))
            {
                Report(TerminalMouseEvent.Motion, _reportedButton, e);
            }

            return;
        }

        if (_selecting)
        {
            _selectionEnd = CellAt(e.GetPosition(this));
            InvalidateVisual();
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_reportedButton != TerminalMouseButton.None && ToMouseButton(e.GetCurrentPoint(this).Properties.PointerUpdateKind) == _reportedButton)
        {
            var button = _reportedButton;
            _reportedButton = TerminalMouseButton.None;
            e.Pointer.Capture(null);
            if (_terminal.MouseTracking != MouseTracking.None)
            {
                Report(TerminalMouseEvent.Release, button, e);
            }

            return;
        }

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

    /// <summary>
    /// True when the program asked for the mouse. Shift keeps it for local selection and copy and paste,
    /// as in xterm.
    /// </summary>
    bool Reporting(PointerEventArgs e) =>
        _terminal.MouseTracking != MouseTracking.None && !e.KeyModifiers.HasFlag(KeyModifiers.Shift);

    void Report(TerminalMouseEvent kind, TerminalMouseButton button, PointerEventArgs e)
    {
        if (!TerminalMouse.Wants(_terminal.MouseTracking, kind, button))
        {
            return;
        }

        var position = e.GetPosition(this);
        int column = Math.Clamp((int)(position.X / _cellWidth), 0, _terminal.Columns - 1);
        int row = Math.Clamp((int)(position.Y / _cellHeight), 0, _terminal.Rows - 1);

        // Moving within one cell says nothing new.
        if (kind == TerminalMouseEvent.Motion && _lastReportedCell == (column, row))
        {
            return;
        }

        _lastReportedCell = (column, row);
        var mods = e.KeyModifiers;
        var terminalMods = (mods.HasFlag(KeyModifiers.Alt) ? TerminalModifiers.Alt : 0) | (mods.HasFlag(KeyModifiers.Control) ? TerminalModifiers.Control : 0);
        if (TerminalMouse.Encode(kind, button, column, row, terminalMods, _terminal.MouseSgr, _terminal.MouseTracking == MouseTracking.Press) is { } bytes)
        {
            // The program sees the live screen, so follow it.
            if (_scrollOffset != 0)
            {
                _scrollOffset = 0;
                InvalidateVisual();
            }

            Session.Send(bytes);
        }
    }

    static TerminalMouseButton? ToMouseButton(PointerUpdateKind kind) => kind switch
    {
        PointerUpdateKind.LeftButtonPressed or PointerUpdateKind.LeftButtonReleased => TerminalMouseButton.Left,
        PointerUpdateKind.MiddleButtonPressed or PointerUpdateKind.MiddleButtonReleased => TerminalMouseButton.Middle,
        PointerUpdateKind.RightButtonPressed or PointerUpdateKind.RightButtonReleased => TerminalMouseButton.Right,
        _ => null,
    };

    (int Row, int Column) CellAt(Point position)
    {
        lock (_terminal.SyncRoot)
        {
            int row = Math.Clamp((int)(position.Y / _cellHeight), 0, _terminal.Rows - 1);
            int column = Math.Clamp((int)(position.X / _cellWidth), 0, _terminal.Columns - 1);
            return (_terminal.ScrollbackCount - _scrollOffset + row, column);
        }
    }

    public bool HasSelection => _selectionStart is not null && _selectionEnd is not null && _selectionStart != _selectionEnd;

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

    /// <summary>Selects everything in the scrollback and on screen, for copying.</summary>
    public void SelectAll()
    {
        lock (_terminal.SyncRoot)
        {
            _selectionStart = (0, 0);
            _selectionEnd = (_terminal.ScrollbackCount + _terminal.Rows - 1, _terminal.Columns - 1);
        }

        InvalidateVisual();
        ScreenReader.Announce(this, $"All terminal text selected. {(OperatingSystem.IsMacOS() ? "Cmd+C" : "Ctrl+Shift+C")} copies it.",
            "Orrery.Terminal.Selection", ScreenReader.MostRecent);
    }

    void ClearSelection()
    {
        if (_selectionStart is null) return;
        _selectionStart = _selectionEnd = null;
        InvalidateVisual();
    }

    /// <summary>Copies the selection, if there is one, and clears it.</summary>
    public async Task CopyAsync()
    {
        var (start, end) = OrderedSelection();
        if (start is not { } s || end is not { } e || TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard)
        {
            return;
        }

        await clipboard.SetTextAsync(_terminal.GetText(s.Row, s.Column, e.Row, e.Column));
        ClearSelection();
    }

    /// <summary>Types the clipboard text into the session.</summary>
    public async Task PasteAsync()
    {
        if (TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard || await clipboard.TryGetTextAsync() is not { Length: > 0 } text)
        {
            return;
        }

        NoteTyped(text.ReplaceLineEndings("\n"));
        Send(TerminalKeys.Paste(text, _terminal.BracketedPaste));
    }

    /// <summary>Everything in the scrollback and on screen, as plain text.</summary>
    public string AllText() => _terminal.AllText();

    public void ClearScrollback()
    {
        _scrollOffset = 0;
        ClearSelection();
        _terminal.ClearScrollback();
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

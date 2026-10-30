using System.Text;

namespace Orrery.Core.Terminal;

/// <summary>
/// An xterm-compatible terminal: feed it the bytes a shell sends and it maintains the screen, the
/// alternate screen (full-screen programs) and scrollback. Covers what shells, vim, less, top, tmux and
/// htop use: cursor movement, erasing, insert/delete, scroll regions, SGR colours (16, 256, 24-bit),
/// DEC private modes, line-drawing characters, titles and status reports. No UI dependency.
/// <para>
/// Thread safety: <see cref="Feed"/>, <see cref="Resize"/> and all reads must hold <see cref="SyncRoot"/>
/// (Feed and Resize take it themselves). Events fire outside the lock.
/// </para>
/// </summary>
public sealed class TerminalEmulator
{
    const int MaxParams = 32;
    const int MaxOscLength = 4096;

    readonly Decoder _utf8 = new UTF8Encoding(false, false).GetDecoder();
    readonly char[] _charBuffer = new char[8192];
    readonly List<TerminalLine> _scrollback = [];
    readonly int _maxScrollback;
    readonly List<string> _pendingReplies = [];

    List<TerminalLine> _main;
    List<TerminalLine> _alt;
    bool _altActive;

    int _cx, _cy;
    bool _wrapPending;
    CellStyle _style = CellStyle.Default;
    int _top, _bottom;
    bool[] _tabs = [];
    int _lastPrinted = ' ';
    char _highSurrogate;

    bool _autoWrap = true, _originMode, _insertMode, _newLineMode;
    bool _g0Graphics, _g1Graphics, _shiftOut;
    SavedCursor _saved, _savedForAlt;

    // Parser.
    State _state = State.Ground;
    readonly List<int> _params = [];
    int _currentParam = -1;
    char _prefix;
    readonly StringBuilder _intermediates = new();
    readonly StringBuilder _osc = new();

    public TerminalEmulator(int columns, int rows, int maxScrollback = 5000)
    {
        Columns = Math.Max(2, columns);
        Rows = Math.Max(1, rows);
        _maxScrollback = maxScrollback;
        _main = NewScreen();
        _alt = NewScreen();
        _bottom = Rows - 1;
        ResetTabs();
    }

    public object SyncRoot { get; } = new();

    public int Columns { get; private set; }
    public int Rows { get; private set; }
    public int CursorX => _cx;
    public int CursorY => _cy;
    public bool CursorVisible { get; private set; } = true;
    public bool CursorKeysApplication { get; private set; }
    public bool KeypadApplication { get; private set; }
    public bool BracketedPaste { get; private set; }

    /// <summary>Which mouse events the program wants reported (htop, tmux, vim with mouse=a).</summary>
    public MouseTracking MouseTracking { get; private set; }

    /// <summary>Mode 1006: report the mouse as ESC[&lt;b;x;yM, which has no position limit.</summary>
    public bool MouseSgr { get; private set; }
    public bool AlternateScreen => _altActive;
    public string Title { get; private set; } = string.Empty;

    /// <summary>Lines that scrolled off the top of the main screen, oldest first.</summary>
    public int ScrollbackCount => _scrollback.Count;

    /// <summary>Bytes the terminal must send back to the host (status and device reports).</summary>
    public event Action<string>? Reply;
    public event Action<string>? TitleChanged;
    public event Action? Bell;

    /// <summary>Raised after input or a resize changed what's shown.</summary>
    public event Action? Updated;

    List<TerminalLine> Screen => _altActive ? _alt : _main;

    /// <summary>A line by absolute row: 0 is the oldest scrollback line, <see cref="ScrollbackCount"/> the top of the screen.</summary>
    public TerminalLine LineAt(int absoluteRow) =>
        absoluteRow < _scrollback.Count ? _scrollback[absoluteRow] : Screen[absoluteRow - _scrollback.Count];

    /// <summary>A screen line, 0 being the top row.</summary>
    public TerminalLine ScreenLine(int row) => Screen[row];

    /// <summary>Text between two absolute positions; wrapped lines join without a newline.</summary>
    public string GetText(int startRow, int startColumn, int endRow, int endColumn)
    {
        lock (SyncRoot)
        {
            var text = new StringBuilder();
            int total = _scrollback.Count + Rows;
            for (int row = Math.Max(0, startRow); row <= Math.Min(endRow, total - 1); row++)
            {
                var line = LineAt(row);
                int from = row == startRow ? startColumn : 0;
                int to = row == endRow ? endColumn + 1 : line.Length;
                text.Append(line.Text(from, to));
                if (row != endRow && !line.Wrapped)
                {
                    text.Append(Environment.NewLine);
                }
            }

            return text.ToString();
        }
    }

    public void Feed(ReadOnlySpan<byte> data)
    {
        string? title = null;
        bool bell = false;
        lock (SyncRoot)
        {
            var previousTitle = Title;
            int bells = _bellCount;
            while (data.Length > 0)
            {
                int take = Math.Min(data.Length, _charBuffer.Length / 2);
                int chars = _utf8.GetChars(data[..take], _charBuffer, flush: false);
                for (int i = 0; i < chars; i++)
                {
                    Process(_charBuffer[i]);
                }

                data = data[take..];
            }

            if (Title != previousTitle) title = Title;
            bell = _bellCount != bells;
        }

        FlushReplies();
        if (title is not null) TitleChanged?.Invoke(title);
        if (bell) Bell?.Invoke();
        Updated?.Invoke();
    }

    public void Feed(string text) => Feed(Encoding.UTF8.GetBytes(text));

    int _bellCount;

    void FlushReplies()
    {
        string[] replies;
        lock (SyncRoot)
        {
            if (_pendingReplies.Count == 0) return;
            replies = [.. _pendingReplies];
            _pendingReplies.Clear();
        }

        foreach (var reply in replies)
        {
            Reply?.Invoke(reply);
        }
    }

    // ---- Parser ----

    enum State
    {
        Ground,
        Escape,
        EscapeIntermediate,
        Csi,
        Osc,
        OscEscape,
        IgnoreString,
        IgnoreStringEscape,
    }

    void Process(char c)
    {
        // Rebuild code points from UTF-16 surrogate pairs.
        if (char.IsHighSurrogate(c))
        {
            _highSurrogate = c;
            return;
        }

        int cp = c;
        if (char.IsLowSurrogate(c))
        {
            if (_highSurrogate == 0) return;
            cp = char.ConvertToUtf32(_highSurrogate, c);
            _highSurrogate = '\0';
        }
        else
        {
            _highSurrogate = '\0';
        }

        // CAN and SUB abort any sequence; ESC starts a new one (except where it may begin a string terminator).
        if (cp is 0x18 or 0x1A)
        {
            _state = State.Ground;
            return;
        }

        switch (_state)
        {
            case State.Ground:
                if (cp == 0x1B) StartEscape();
                else if (cp < 0x20 || cp == 0x7F) Control(cp);
                else Print(cp);
                break;

            case State.Escape:
                if (cp < 0x20) { if (cp == 0x1B) StartEscape(); else Control(cp); }
                else Escape(cp);
                break;

            case State.EscapeIntermediate:
                if (cp < 0x20) { if (cp == 0x1B) StartEscape(); else Control(cp); }
                else if (cp is >= 0x20 and <= 0x2F) _intermediates.Append((char)cp);
                else { EscapeWithIntermediate(cp); _state = State.Ground; }
                break;

            case State.Csi:
                if (cp == 0x1B) StartEscape();
                else if (cp < 0x20) Control(cp);
                else Csi(cp);
                break;

            case State.Osc:
                if (cp == 0x07) { Osc(); _state = State.Ground; }
                else if (cp == 0x1B) _state = State.OscEscape;
                else if (_osc.Length < MaxOscLength) _osc.Append(char.ConvertFromUtf32(cp));
                break;

            case State.OscEscape:
                Osc();
                _state = State.Ground;
                if (cp != '\\') Process((char)cp);
                break;

            case State.IgnoreString:
                if (cp == 0x1B) _state = State.IgnoreStringEscape;
                else if (cp == 0x07) _state = State.Ground;
                break;

            case State.IgnoreStringEscape:
                _state = cp == '\\' ? State.Ground : State.IgnoreString;
                break;
        }
    }

    void StartEscape()
    {
        _state = State.Escape;
        _intermediates.Clear();
    }

    void Control(int cp)
    {
        switch (cp)
        {
            case 0x07: _bellCount++; break;
            case 0x08: Backspace(); break;
            case 0x09: Tab(1); break;
            case 0x0A or 0x0B or 0x0C:
                LineFeed();
                if (_newLineMode) CarriageReturn();
                break;
            case 0x0D: CarriageReturn(); break;
            case 0x0E: _shiftOut = true; break;
            case 0x0F: _shiftOut = false; break;
        }
    }

    void Escape(int cp)
    {
        _state = State.Ground;
        switch ((char)cp)
        {
            case '[':
                _state = State.Csi;
                _params.Clear();
                _currentParam = -1;
                _prefix = '\0';
                _intermediates.Clear();
                break;
            case ']':
                _state = State.Osc;
                _osc.Clear();
                break;
            case 'P' or 'X' or '^' or '_':
                _state = State.IgnoreString;
                break;
            case '(' or ')' or '*' or '+' or '#' or '%' or ' ':
                _intermediates.Append((char)cp);
                _state = State.EscapeIntermediate;
                break;
            case '7': SaveCursor(ref _saved); break;
            case '8': RestoreCursor(_saved); break;
            case 'D': LineFeed(); break;
            case 'E': CarriageReturn(); LineFeed(); break;
            case 'M': ReverseIndex(); break;
            case 'H': if (_cx < Columns) _tabs[_cx] = true; break;
            case 'c': FullReset(); break;
            case '=': KeypadApplication = true; break;
            case '>': KeypadApplication = false; break;
        }
    }

    void EscapeWithIntermediate(int cp)
    {
        var intermediate = _intermediates.Length > 0 ? _intermediates[0] : '\0';
        switch (intermediate)
        {
            case '(': _g0Graphics = cp == '0'; break;
            case ')': _g1Graphics = cp == '0'; break;
            case '#' when cp == '8':
                // DECALN: fill the screen with E (a test pattern).
                foreach (var line in Screen)
                {
                    for (int i = 0; i < Columns; i++) line.Cells[i] = new TerminalCell('E', CellStyle.Default);
                }
                break;
        }
    }

    void Csi(int cp)
    {
        char c = (char)cp;
        if (c is >= '0' and <= '9')
        {
            _currentParam = Math.Min(65535, Math.Max(0, _currentParam) * 10 + (c - '0'));
            return;
        }

        if (c is ';' or ':')
        {
            if (_params.Count < MaxParams) _params.Add(_currentParam);
            _currentParam = -1;
            return;
        }

        if (c is '?' or '>' or '<' or '=')
        {
            _prefix = c;
            return;
        }

        if (c is >= ' ' and <= '/')
        {
            _intermediates.Append(c);
            return;
        }

        if (c is >= '@' and <= '~')
        {
            if (_currentParam >= 0 || _params.Count > 0)
            {
                if (_params.Count < MaxParams) _params.Add(_currentParam);
            }

            _state = State.Ground;
            DispatchCsi(c);
        }
    }

    int P(int index, int fallback = 1)
    {
        var value = index < _params.Count ? _params[index] : -1;
        return value <= 0 ? fallback : value;
    }

    int P0(int index) => index < _params.Count && _params[index] > 0 ? _params[index] : 0;

    void DispatchCsi(char final)
    {
        var intermediate = _intermediates.ToString();
        if (_prefix == '?')
        {
            if (final is 'h' or 'l') SetPrivateModes(final == 'h');
            return;
        }

        if (_prefix == '>')
        {
            if (final == 'c') _pendingReplies.Add("\u001b[>0;10;1c");
            return;
        }

        if (intermediate == "!" && final == 'p')
        {
            SoftReset();
            return;
        }

        if (intermediate.Length > 0)
        {
            return; // DECSCUSR (cursor shape) and friends: not needed.
        }

        switch (final)
        {
            case '@': InsertChars(P(0)); break;
            case 'A': MoveCursor(_cx, _cy - P(0), clampToRegion: true); break;
            case 'B': MoveCursor(_cx, _cy + P(0), clampToRegion: true); break;
            case 'C': MoveCursor(_cx + P(0), _cy); break;
            case 'D': MoveCursor(_cx - P(0), _cy); break;
            case 'E': MoveCursor(0, _cy + P(0), clampToRegion: true); break;
            case 'F': MoveCursor(0, _cy - P(0), clampToRegion: true); break;
            case 'G' or '`': MoveCursor(P(0) - 1, _cy); break;
            case 'H' or 'f': CursorPosition(P(0), P(1)); break;
            case 'I': Tab(P(0)); break;
            case 'J': EraseDisplay(P0(0)); break;
            case 'K': EraseLine(P0(0)); break;
            case 'L': InsertLines(P(0)); break;
            case 'M': DeleteLines(P(0)); break;
            case 'P': DeleteChars(P(0)); break;
            case 'S': ScrollUp(P(0)); break;
            case 'T': ScrollDown(P(0)); break;
            case 'X': EraseChars(P(0)); break;
            case 'Z': BackTab(P(0)); break;
            case 'a': MoveCursor(_cx + P(0), _cy); break;
            case 'b': for (int i = 0; i < Math.Min(P(0), 65535); i++) Print(_lastPrinted); break;
            case 'c': if (P0(0) == 0) _pendingReplies.Add("\u001b[?62;22c"); break;
            case 'd': CursorPosition(P(0), 1, keepColumn: true); break;
            case 'e': MoveCursor(_cx, _cy + P(0)); break;
            case 'g': ClearTabs(P0(0)); break;
            case 'h' or 'l': SetAnsiModes(final == 'h'); break;
            case 'm': SelectGraphicRendition(); break;
            case 'n': DeviceStatusReport(P0(0)); break;
            case 'r': SetScrollRegion(P(0), P(1, Rows)); break;
            case 's': SaveCursor(ref _saved); break;
            case 'u': RestoreCursor(_saved); break;
        }
    }

    void Osc()
    {
        var text = _osc.ToString();
        int semicolon = text.IndexOf(';');
        if (semicolon > 0 && text[..semicolon] is "0" or "2")
        {
            Title = text[(semicolon + 1)..];
        }
    }

    // ---- Printing ----

    void Print(int cp)
    {
        if ((_shiftOut ? _g1Graphics : _g0Graphics) && cp is >= 0x5F and <= 0x7E)
        {
            cp = DecSpecialGraphics[cp - 0x5F];
        }

        int width = CharWidth.Of(cp);
        if (width == 0)
        {
            return;
        }

        _lastPrinted = cp;
        var screen = Screen;

        if (_wrapPending || (width == 2 && _cx == Columns - 1))
        {
            if (_autoWrap)
            {
                screen[_cy].Wrapped = true;
                CarriageReturn();
                LineFeed();
            }

            _wrapPending = false;
        }

        if (width == 2 && Columns < 2)
        {
            return;
        }

        var line = Screen[_cy];
        if (_insertMode)
        {
            ShiftRight(line, _cx, width);
        }

        ClearWideAt(line, _cx);
        line.Cells[_cx] = new TerminalCell(cp, _style);
        if (width == 2)
        {
            ClearWideAt(line, _cx + 1);
            line.Cells[_cx + 1] = new TerminalCell(TerminalCell.WideContinuation, _style);
        }

        if (_cx + width >= Columns)
        {
            _cx = Columns - 1;
            _wrapPending = _autoWrap;
        }
        else
        {
            _cx += width;
        }
    }

    /// <summary>Overwriting either half of a wide character blanks its other half.</summary>
    void ClearWideAt(TerminalLine line, int x)
    {
        if (x >= Columns) return;
        if (line.Cells[x].CodePoint == TerminalCell.WideContinuation && x > 0)
        {
            line.Cells[x - 1] = new TerminalCell(0, line.Cells[x - 1].Style);
        }

        if (x + 1 < Columns && line.Cells[x + 1].CodePoint == TerminalCell.WideContinuation)
        {
            line.Cells[x + 1] = new TerminalCell(0, line.Cells[x + 1].Style);
        }
    }

    static void ShiftRight(TerminalLine line, int from, int count)
    {
        var cells = line.Cells;
        for (int i = cells.Length - 1; i >= from + count; i--) cells[i] = cells[i - count];
    }

    // ---- Cursor ----

    void CarriageReturn()
    {
        _cx = 0;
        _wrapPending = false;
    }

    void Backspace()
    {
        if (_cx > 0) _cx--;
        _wrapPending = false;
    }

    void LineFeed()
    {
        _wrapPending = false;
        if (_cy == _bottom) ScrollUp(1);
        else if (_cy < Rows - 1) _cy++;
    }

    void ReverseIndex()
    {
        _wrapPending = false;
        if (_cy == _top) ScrollDown(1);
        else if (_cy > 0) _cy--;
    }

    void MoveCursor(int x, int y, bool clampToRegion = false)
    {
        _wrapPending = false;
        _cx = Math.Clamp(x, 0, Columns - 1);
        if (clampToRegion && _cy >= _top && _cy <= _bottom)
        {
            _cy = Math.Clamp(y, _top, _bottom);
        }
        else
        {
            _cy = Math.Clamp(y, 0, Rows - 1);
        }
    }

    /// <summary>CUP/HVP, 1-based; with origin mode, rows count from the top of the scroll region.</summary>
    void CursorPosition(int row, int column, bool keepColumn = false)
    {
        _wrapPending = false;
        int y = _originMode ? Math.Clamp(_top + row - 1, _top, _bottom) : Math.Clamp(row - 1, 0, Rows - 1);
        _cy = y;
        if (!keepColumn) _cx = Math.Clamp(column - 1, 0, Columns - 1);
    }

    void Tab(int count)
    {
        for (int n = 0; n < count && _cx < Columns - 1; n++)
        {
            do _cx++; while (_cx < Columns - 1 && !_tabs[_cx]);
        }

        _wrapPending = false;
    }

    void BackTab(int count)
    {
        for (int n = 0; n < count && _cx > 0; n++)
        {
            do _cx--; while (_cx > 0 && !_tabs[_cx]);
        }

        _wrapPending = false;
    }

    void ClearTabs(int mode)
    {
        if (mode == 0 && _cx < Columns) _tabs[_cx] = false;
        else if (mode == 3) Array.Clear(_tabs);
    }

    void ResetTabs()
    {
        _tabs = new bool[Columns];
        for (int i = 8; i < Columns; i += 8) _tabs[i] = true;
    }

    record struct SavedCursor(int X, int Y, CellStyle Style, bool OriginMode, bool AutoWrap, bool G0Graphics, bool G1Graphics, bool ShiftOut, bool Valid);

    void SaveCursor(ref SavedCursor slot) =>
        slot = new SavedCursor(_cx, _cy, _style, _originMode, _autoWrap, _g0Graphics, _g1Graphics, _shiftOut, true);

    void RestoreCursor(SavedCursor slot)
    {
        if (!slot.Valid)
        {
            CursorPosition(1, 1);
            return;
        }

        _cx = Math.Clamp(slot.X, 0, Columns - 1);
        _cy = Math.Clamp(slot.Y, 0, Rows - 1);
        (_style, _originMode, _autoWrap, _g0Graphics, _g1Graphics, _shiftOut) =
            (slot.Style, slot.OriginMode, slot.AutoWrap, slot.G0Graphics, slot.G1Graphics, slot.ShiftOut);
        _wrapPending = false;
    }

    // ---- Editing ----

    void EraseDisplay(int mode)
    {
        var screen = Screen;
        var erase = _style.ForErase;
        switch (mode)
        {
            case 0:
                screen[_cy].Fill(_cx, Columns, erase);
                for (int y = _cy + 1; y < Rows; y++) Blank(screen[y], erase);
                break;
            case 1:
                for (int y = 0; y < _cy; y++) Blank(screen[y], erase);
                screen[_cy].Fill(0, _cx + 1, erase);
                break;
            case 2:
                for (int y = 0; y < Rows; y++) Blank(screen[y], erase);
                break;
            case 3:
                _scrollback.Clear();
                break;
        }

        _wrapPending = false;
    }

    static void Blank(TerminalLine line, CellStyle style)
    {
        line.Fill(0, line.Length, style);
        line.Wrapped = false;
    }

    void EraseLine(int mode)
    {
        var line = Screen[_cy];
        var erase = _style.ForErase;
        switch (mode)
        {
            case 0: line.Fill(_cx, Columns, erase); line.Wrapped = false; break;
            case 1: line.Fill(0, _cx + 1, erase); break;
            case 2: Blank(line, erase); break;
        }

        _wrapPending = false;
    }

    void EraseChars(int count) => Screen[_cy].Fill(_cx, _cx + count, _style.ForErase);

    void InsertChars(int count)
    {
        var line = Screen[_cy];
        count = Math.Min(count, Columns - _cx);
        ShiftRight(line, _cx, count);
        line.Fill(_cx, _cx + count, _style.ForErase);
        _wrapPending = false;
    }

    void DeleteChars(int count)
    {
        var cells = Screen[_cy].Cells;
        count = Math.Min(count, Columns - _cx);
        Array.Copy(cells, _cx + count, cells, _cx, Columns - _cx - count);
        Screen[_cy].Fill(Columns - count, Columns, _style.ForErase);
        _wrapPending = false;
    }

    void InsertLines(int count)
    {
        if (_cy < _top || _cy > _bottom) return;
        var screen = Screen;
        count = Math.Min(count, _bottom - _cy + 1);
        for (int i = 0; i < count; i++)
        {
            screen.RemoveAt(_bottom);
            screen.Insert(_cy, new TerminalLine(Columns, _style.ForErase));
        }

        _cx = 0;
        _wrapPending = false;
    }

    void DeleteLines(int count)
    {
        if (_cy < _top || _cy > _bottom) return;
        var screen = Screen;
        count = Math.Min(count, _bottom - _cy + 1);
        for (int i = 0; i < count; i++)
        {
            screen.RemoveAt(_cy);
            screen.Insert(_bottom, new TerminalLine(Columns, _style.ForErase));
        }

        _cx = 0;
        _wrapPending = false;
    }

    /// <summary>Scrolls the region up; lines leaving the top of the main screen go to scrollback.</summary>
    void ScrollUp(int count)
    {
        var screen = Screen;
        count = Math.Min(count, _bottom - _top + 1);
        for (int i = 0; i < count; i++)
        {
            var leaving = screen[_top];
            screen.RemoveAt(_top);
            screen.Insert(_bottom, new TerminalLine(Columns, _style.ForErase));
            if (_top == 0 && !_altActive)
            {
                AddToScrollback(leaving);
            }
        }
    }

    void ScrollDown(int count)
    {
        var screen = Screen;
        count = Math.Min(count, _bottom - _top + 1);
        for (int i = 0; i < count; i++)
        {
            screen.RemoveAt(_bottom);
            screen.Insert(_top, new TerminalLine(Columns, _style.ForErase));
        }
    }

    void AddToScrollback(TerminalLine line)
    {
        if (_maxScrollback <= 0) return;
        _scrollback.Add(line);
        if (_scrollback.Count > _maxScrollback + 256)
        {
            _scrollback.RemoveRange(0, _scrollback.Count - _maxScrollback);
        }
    }

    void SetScrollRegion(int top, int bottom)
    {
        top = Math.Clamp(top, 1, Rows);
        bottom = Math.Clamp(bottom, 1, Rows);
        if (top >= bottom) return;
        (_top, _bottom) = (top - 1, bottom - 1);
        CursorPosition(1, 1);
    }

    // ---- Modes ----

    void SetAnsiModes(bool on)
    {
        foreach (var mode in _params)
        {
            if (mode == 4) _insertMode = on;
            else if (mode == 20) _newLineMode = on;
        }
    }

    void SetPrivateModes(bool on)
    {
        foreach (var mode in _params)
        {
            switch (mode)
            {
                case 1: CursorKeysApplication = on; break;
                case 6: _originMode = on; CursorPosition(1, 1); break;
                case 7: _autoWrap = on; break;
                case 25: CursorVisible = on; break;
                case 47: SwitchScreen(on, saveCursor: false, clear: false); break;
                case 1047: SwitchScreen(on, saveCursor: false, clear: !on); break;
                case 1048: if (on) SaveCursor(ref _saved); else RestoreCursor(_saved); break;
                case 1049: SwitchScreen(on, saveCursor: true, clear: on); break;
                case 2004: BracketedPaste = on; break;
                case 9: SetMouseTracking(MouseTracking.Press, on); break;
                case 1000: SetMouseTracking(MouseTracking.Normal, on); break;
                case 1002: SetMouseTracking(MouseTracking.ButtonMotion, on); break;
                case 1003: SetMouseTracking(MouseTracking.AnyMotion, on); break;
                case 1006: MouseSgr = on; break;
            }
        }
    }

    // Turning a mode on replaces the current one; turning off only clears it if it's the one in force.
    void SetMouseTracking(MouseTracking mode, bool on)
    {
        if (on) MouseTracking = mode;
        else if (MouseTracking == mode) MouseTracking = MouseTracking.None;
    }

    void SwitchScreen(bool alternate, bool saveCursor, bool clear)
    {
        if (alternate == _altActive) return;
        if (alternate)
        {
            if (saveCursor) SaveCursor(ref _savedForAlt);
            _altActive = true;
            if (clear) foreach (var line in _alt) Blank(line, CellStyle.Default);
        }
        else
        {
            if (clear) foreach (var line in _alt) Blank(line, CellStyle.Default);
            _altActive = false;
            if (saveCursor) RestoreCursor(_savedForAlt);
        }

        _wrapPending = false;
    }

    void DeviceStatusReport(int kind)
    {
        if (kind == 5)
        {
            _pendingReplies.Add("\u001b[0n");
        }
        else if (kind == 6)
        {
            int row = _originMode ? _cy - _top + 1 : _cy + 1;
            _pendingReplies.Add($"\u001b[{row};{_cx + 1}R");
        }
    }

    void SoftReset()
    {
        CursorVisible = true;
        _insertMode = _originMode = false;
        _autoWrap = true;
        CursorKeysApplication = KeypadApplication = false;
        _style = CellStyle.Default;
        (_top, _bottom) = (0, Rows - 1);
        _g0Graphics = _g1Graphics = _shiftOut = false;
        _saved = default;
    }

    void FullReset()
    {
        SoftReset();
        _altActive = false;
        _main = NewScreen();
        _alt = NewScreen();
        _scrollback.Clear();
        _cx = _cy = 0;
        _wrapPending = false;
        BracketedPaste = false;
        MouseTracking = MouseTracking.None;
        MouseSgr = false;
        _newLineMode = false;
        ResetTabs();
        Title = string.Empty;
    }

    // ---- SGR ----

    void SelectGraphicRendition()
    {
        if (_params.Count == 0)
        {
            _style = CellStyle.Default;
            return;
        }

        var (fg, bg, flags) = (_style.Foreground, _style.Background, _style.Flags);
        for (int i = 0; i < _params.Count; i++)
        {
            int p = Math.Max(0, _params[i]);
            switch (p)
            {
                case 0: (fg, bg, flags) = (TerminalColor.Default, TerminalColor.Default, CellFlags.None); break;
                case 1: flags |= CellFlags.Bold; break;
                case 2: flags |= CellFlags.Faint; break;
                case 3: flags |= CellFlags.Italic; break;
                case 4: flags |= CellFlags.Underline; break;
                case 5 or 6: flags |= CellFlags.Blink; break;
                case 7: flags |= CellFlags.Inverse; break;
                case 8: flags |= CellFlags.Hidden; break;
                case 9: flags |= CellFlags.Strike; break;
                case 21: flags |= CellFlags.Underline; break;
                case 22: flags &= ~(CellFlags.Bold | CellFlags.Faint); break;
                case 23: flags &= ~CellFlags.Italic; break;
                case 24: flags &= ~CellFlags.Underline; break;
                case 25: flags &= ~CellFlags.Blink; break;
                case 27: flags &= ~CellFlags.Inverse; break;
                case 28: flags &= ~CellFlags.Hidden; break;
                case 29: flags &= ~CellFlags.Strike; break;
                case >= 30 and <= 37: fg = TerminalColor.Palette(p - 30); break;
                case 38: fg = ExtendedColor(ref i, fg); break;
                case 39: fg = TerminalColor.Default; break;
                case >= 40 and <= 47: bg = TerminalColor.Palette(p - 40); break;
                case 48: bg = ExtendedColor(ref i, bg); break;
                case 49: bg = TerminalColor.Default; break;
                case >= 90 and <= 97: fg = TerminalColor.Palette(p - 90 + 8); break;
                case >= 100 and <= 107: bg = TerminalColor.Palette(p - 100 + 8); break;
            }
        }

        _style = new CellStyle(fg, bg, flags);
    }

    /// <summary>38/48;5;n (256 colours) or 38/48;2;r;g;b (24-bit). Colon forms arrive as the same list.</summary>
    TerminalColor ExtendedColor(ref int i, TerminalColor current)
    {
        int Param(int at) => at < _params.Count ? Math.Max(0, _params[at]) : 0;

        if (Param(i + 1) == 5 && i + 2 < _params.Count)
        {
            var color = TerminalColor.Palette(Param(i + 2));
            i += 2;
            return color;
        }

        if (Param(i + 1) == 2)
        {
            // "38:2::r:g:b" carries an empty colour-space id; skip it when six values follow.
            int start = i + 5 < _params.Count && _params.Count - i > 5 && _params[i + 2] < 0 ? i + 3 : i + 2;
            if (start + 2 < _params.Count)
            {
                var color = TerminalColor.Rgb(Param(start), Param(start + 1), Param(start + 2));
                i = start + 2;
                return color;
            }
        }

        return current;
    }

    // ---- Resize ----

    public void Resize(int columns, int rows)
    {
        lock (SyncRoot)
        {
            columns = Math.Max(2, columns);
            rows = Math.Max(1, rows);
            if (columns == Columns && rows == Rows) return;

            ResizeScreen(_main, columns, rows, cursorOnThis: !_altActive);
            ResizeScreen(_alt, columns, rows, cursorOnThis: _altActive);
            foreach (var line in _scrollback) line.Resize(columns, CellStyle.Default);

            Columns = columns;
            Rows = rows;
            _cx = Math.Clamp(_cx, 0, columns - 1);
            _cy = Math.Clamp(_cy, 0, rows - 1);
            (_top, _bottom) = (0, rows - 1);
            _wrapPending = false;

            var tabs = _tabs;
            ResetTabs();
            Array.Copy(tabs, _tabs, Math.Min(tabs.Length, _tabs.Length));
        }

        Updated?.Invoke();
    }

    /// <summary>
    /// Fewer rows: drop blank lines below the cursor first, then push lines from the top into scrollback
    /// (main screen only) so the cursor's line stays visible. More rows: pull lines back from scrollback.
    /// </summary>
    void ResizeScreen(List<TerminalLine> screen, int columns, int rows, bool cursorOnThis)
    {
        foreach (var line in screen) line.Resize(columns, CellStyle.Default);

        while (screen.Count > rows)
        {
            int last = screen.Count - 1;
            bool belowCursor = !cursorOnThis || last > _cy;
            if (belowCursor && screen[last].Cells.All(c => c.IsBlank))
            {
                screen.RemoveAt(last);
                continue;
            }

            var top = screen[0];
            screen.RemoveAt(0);
            if (screen == _main) AddToScrollback(top);
            if (cursorOnThis && _cy > 0) _cy--;
        }

        while (screen.Count < rows)
        {
            if (screen == _main && _scrollback.Count > 0 && cursorOnThis)
            {
                screen.Insert(0, _scrollback[^1]);
                _scrollback.RemoveAt(_scrollback.Count - 1);
                _cy++;
            }
            else
            {
                screen.Add(new TerminalLine(columns, CellStyle.Default));
            }
        }
    }

    List<TerminalLine> NewScreen() => Enumerable.Range(0, Rows).Select(_ => new TerminalLine(Columns, CellStyle.Default)).ToList();

    /// <summary>DEC special graphics for 0x5F–0x7E: the box-drawing set used by ncurses and tmux borders.</summary>
    static readonly int[] DecSpecialGraphics =
    [
        ' ', '◆', '▒', '␉', '␌', '␍', '␊', '°', '±', '␤', '␋', '┘', '┐', '┌', '└', '┼',
        '⎺', '⎻', '─', '⎼', '⎽', '├', '┤', '┴', '┬', '│', '≤', '≥', 'π', '≠', '£', '·',
    ];
}

/// <summary>How many cells a code point takes: 0 for combining marks, 2 for East Asian wide and emoji.</summary>
public static class CharWidth
{
    public static int Of(int cp)
    {
        if (cp is >= 0x0300 and <= 0x036F or >= 0x200B and <= 0x200F or >= 0xFE00 and <= 0xFE0F
            or >= 0x20D0 and <= 0x20FF or >= 0x1AB0 and <= 0x1AFF or >= 0x1DC0 and <= 0x1DFF or 0x200D)
        {
            return 0;
        }

        if (cp is >= 0x1100 and <= 0x115F or >= 0x2E80 and <= 0x303E or >= 0x3041 and <= 0x33FF
            or >= 0x3400 and <= 0x4DBF or >= 0x4E00 and <= 0x9FFF or >= 0xA000 and <= 0xA4CF
            or >= 0xAC00 and <= 0xD7A3 or >= 0xF900 and <= 0xFAFF or >= 0xFE30 and <= 0xFE4F
            or >= 0xFF00 and <= 0xFF60 or >= 0xFFE0 and <= 0xFFE6 or >= 0x1F300 and <= 0x1F64F
            or >= 0x1F900 and <= 0x1F9FF or >= 0x1FA70 and <= 0x1FAFF or >= 0x20000 and <= 0x3FFFD)
        {
            return 2;
        }

        return 1;
    }
}

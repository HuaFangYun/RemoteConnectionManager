using System.Text;

namespace RemoteConnectionManager.Core.SessionLogging;

/// <summary>
/// Turns terminal output (VT text as ConPTY or a telnet device sends it) into finished plain text
/// lines for the session log.
///
/// ConPTY does not send a simple stream of lines: conhost repaints the screen with cursor moves,
/// line erases and whole row rewrites. Stripping the escape codes would leave PSReadLine's redraws
/// and every repaint in the log. Instead this keeps a small model of the screen and writes a row
/// out once it is finished:
///   - when the cursor moves below it (Enter, the next line of output, a repaint further down),
///   - when it scrolls off the top, or
///   - when the session ends (<see cref="Flush"/>) or the screen is cleared.
/// A row is only written again if its text really changed since, so repaints of the same text,
/// which conhost does often, never repeat lines. Colours and other attributes are dropped. The
/// alternate screen (vim, less, top) is not logged.
/// </summary>
public sealed class VtScreenRecorder
{
    private readonly Action<string> _emit;
    private List<Row> _rows = new();
    private List<Row>? _savedMainScreen;
    private int _cols;
    private int _x;
    private int _y;
    private bool _pendingWrap;
    private int _top;
    private int _bottom;
    private int _savedX;
    private int _savedY;

    // Parser state
    private State _state = State.Ground;
    private readonly StringBuilder _params = new();
    private char _private;
    private bool _stringEscape;

    private enum State { Ground, Escape, EscapeIntermediate, Csi, Osc, String }

    private sealed class Row
    {
        public char[] Cells;
        /// <summary>True once written to the log and not changed since.</summary>
        public bool Committed;

        public Row(int cols)
        {
            Cells = new char[cols];
            Array.Fill(Cells, ' ');
        }

        public string Text => new string(Cells).TrimEnd();
        public bool IsBlank => Array.TrueForAll(Cells, c => c == ' ');

        public void Set(int x, char c)
        {
            if (Cells[x] == c) return;
            Cells[x] = c;
            Committed = false;
        }

        public void Resize(int cols)
        {
            if (cols == Cells.Length) return;
            var cells = new char[cols];
            Array.Fill(cells, ' ');
            Array.Copy(Cells, cells, Math.Min(cols, Cells.Length));
            Cells = cells;
        }
    }

    /// <param name="emit">Receives each finished line, trailing spaces removed. Called on the thread that calls <see cref="Feed"/>.</param>
    public VtScreenRecorder(int columns, int rows, Action<string> emit)
    {
        _emit = emit;
        Resize(columns, rows);
    }

    public int Columns => _cols;
    public int Rows => _rows.Count;

    /// <summary>Matches the screen to the terminal's size. Content is kept (cut or padded), as the terminal does before it repaints.</summary>
    public void Resize(int columns, int rows)
    {
        columns = Math.Clamp(columns, 2, 1000);
        rows = Math.Clamp(rows, 1, 500);
        _cols = columns;
        foreach (var row in _rows) row.Resize(columns);
        foreach (var row in _savedMainScreen ?? []) row.Resize(columns);
        while (_rows.Count > rows)
        {
            // Rows lost from the top count as scrolled away.
            Commit(_rows[0]);
            _rows.RemoveAt(0);
            _y = Math.Max(0, _y - 1);
        }
        while (_rows.Count < rows) _rows.Add(new Row(columns));
        _top = 0;
        _bottom = rows - 1;
        _x = Math.Min(_x, _cols - 1);
        _y = Math.Min(_y, _bottom);
        _pendingWrap = false;
    }

    public void Feed(ReadOnlySpan<char> text)
    {
        foreach (var c in text) Feed(c);
    }

    public void Feed(string text) => Feed(text.AsSpan());

    /// <summary>Writes every row not yet logged, top to bottom. Call when the session ends or restarts.</summary>
    public void Flush()
    {
        if (_savedMainScreen is not null) LeaveAlternateScreen();
        var last = _rows.FindLastIndex(r => !r.IsBlank);
        for (var i = 0; i <= last; i++) Commit(_rows[i]);
    }

    private void Commit(Row row, bool keepBlank = false)
    {
        if (row.Committed) return;
        row.Committed = true;
        if (_savedMainScreen is not null) return;
        if (row.IsBlank)
        {
            // A blank line only counts when output really moved past it with a line feed (a gap
            // between paragraphs or around a table), never for rows skipped by cursor moves. Runs
            // of blank lines collapse to one, and nothing blank is logged before the first text.
            if (!keepBlank || !_anyEmitted || _lastWasBlank) return;
            _lastWasBlank = true;
            _emit(string.Empty);
            return;
        }
        _anyEmitted = true;
        _lastWasBlank = false;
        _emit(row.Text);
    }

    private bool _anyEmitted;
    private bool _lastWasBlank;

    // ---------- parser ----------

    private void Feed(char c)
    {
        switch (_state)
        {
            case State.Ground:
                Ground(c);
                break;

            case State.Escape:
                Escape(c);
                break;

            case State.EscapeIntermediate:
                // ESC ( B and friends: one more character picks the character set. Ignored.
                if (c is >= ' ' and <= '/') return;
                _state = State.Ground;
                break;

            case State.Csi:
                if (c is >= '0' and <= '9' or ';' or ':') { _params.Append(c); return; }
                if (c is '?' or '>' or '<' or '=') { _private = c; return; }
                if (c is >= ' ' and <= '/') return; // intermediates, for example the space in CSI n SP q
                if (c == '\x1b') { _state = State.Escape; return; }
                if (c is >= '@' and <= '~') Csi(c);
                _state = State.Ground;
                break;

            case State.Osc:
            case State.String:
                // Window title, hyperlinks, DCS and the like end with BEL or ESC \. Nothing in them is logged.
                if (c == '\a' && _state == State.Osc) { _state = State.Ground; return; }
                if (_stringEscape)
                {
                    _stringEscape = false;
                    if (c == '\\') { _state = State.Ground; return; }
                }
                if (c == '\x1b') _stringEscape = true;
                break;
        }
    }

    private void Ground(char c)
    {
        switch (c)
        {
            case '\x1b':
                _state = State.Escape;
                return;
            case '\r':
                _x = 0;
                _pendingWrap = false;
                return;
            case '\n' or '\v' or '\f':
                LineFeed();
                return;
            case '\b':
                if (_x > 0) _x--;
                _pendingWrap = false;
                return;
            case '\t':
                _x = Math.Min(_cols - 1, (_x / 8 + 1) * 8);
                return;
            case '\u009b': // 8 bit CSI
                BeginCsi();
                return;
        }
        if (c < ' ' || c == '\x7f' || c is >= '\u0080' and <= '\u009f') return;

        if (_pendingWrap)
        {
            _pendingWrap = false;
            _x = 0;
            LineFeed();
        }
        _rows[_y].Set(_x, c);
        if (_x == _cols - 1) _pendingWrap = true;
        else _x++;
    }

    private void Escape(char c)
    {
        _state = State.Ground;
        switch (c)
        {
            case '[': BeginCsi(); break;
            case ']': _state = State.Osc; _stringEscape = false; break;
            case 'P' or 'X' or '^' or '_': _state = State.String; _stringEscape = false; break;
            case '(' or ')' or '*' or '+' or '#' or '%' or ' ': _state = State.EscapeIntermediate; break;
            case 'D': LineFeed(); break;                         // IND
            case 'E': _x = 0; LineFeed(); break;                 // NEL
            case 'M': ReverseIndex(); break;                     // RI
            case '7': _savedX = _x; _savedY = _y; break;         // DECSC
            case '8': MoveTo(_savedY, _savedX); break;           // DECRC
            case 'c': Reset(); break;                            // RIS
        }
    }

    private void BeginCsi()
    {
        _state = State.Csi;
        _params.Clear();
        _private = '\0';
    }

    private int Param(int index, int fallback)
    {
        var parts = _params.ToString().Split(';');
        if (index >= parts.Length) return fallback;
        var part = parts[index].Split(':')[0];
        return int.TryParse(part, out var v) && v > 0 ? v : fallback;
    }

    private int ParamOrZero(int index)
    {
        var parts = _params.ToString().Split(';');
        return index < parts.Length && int.TryParse(parts[index].Split(':')[0], out var v) ? v : 0;
    }

    private void Csi(char final)
    {
        if (_private == '?')
        {
            if (final is 'h' or 'l')
                foreach (var mode in _params.ToString().Split(';'))
                    if (mode is "1049" or "1047" or "47")
                    {
                        if (final == 'h') EnterAlternateScreen(); else LeaveAlternateScreen();
                    }
            return;
        }
        if (_private != '\0') return;

        var n = Param(0, 1);
        switch (final)
        {
            case 'A': MoveTo(_y - n, _x); break;                          // CUU
            case 'B' or 'e': MoveTo(_y + n, _x); break;                   // CUD, VPR
            case 'C' or 'a': MoveTo(_y, _x + n); break;                   // CUF, HPR
            case 'D': MoveTo(_y, _x - n); break;                          // CUB
            case 'E': MoveTo(_y + n, 0); break;                           // CNL
            case 'F': MoveTo(_y - n, 0); break;                           // CPL
            case 'G' or '`': MoveTo(_y, n - 1); break;                    // CHA, HPA
            case 'd': MoveTo(n - 1, _x); break;                           // VPA
            case 'H' or 'f': MoveTo(Param(0, 1) - 1, Param(1, 1) - 1); break; // CUP
            case 'J': EraseDisplay(ParamOrZero(0)); break;
            case 'K': EraseLine(ParamOrZero(0)); break;
            case 'X': for (var i = 0; i < n && _x + i < _cols; i++) _rows[_y].Set(_x + i, ' '); break; // ECH
            case 'P': DeleteChars(n); break;
            case '@': InsertChars(n); break;
            case 'L': InsertLines(n); break;
            case 'M': DeleteLines(n); break;
            case 'S': for (var i = 0; i < n; i++) ScrollUp(_top, _bottom); break;
            case 'T': for (var i = 0; i < n; i++) ScrollDown(_top, _bottom); break;
            case 'r':
                var top = Param(0, 1) - 1;
                var bottom = Param(1, _rows.Count) - 1;
                if (top < bottom && bottom < _rows.Count) { _top = top; _bottom = bottom; }
                else { _top = 0; _bottom = _rows.Count - 1; }
                MoveTo(0, 0);
                break;
            case 's': _savedX = _x; _savedY = _y; break;
            case 'u': MoveTo(_savedY, _savedX); break;
            // m (colours), h/l (modes), n (reports), t (window ops) and the rest change nothing that is logged.
        }
    }

    // ---------- screen operations ----------

    /// <summary>Moving the cursor below a row means that row is done: log it (once).</summary>
    private void MoveTo(int y, int x)
    {
        y = Math.Clamp(y, 0, _rows.Count - 1);
        if (y > _y)
            for (var row = _y; row < y; row++) Commit(_rows[row]);
        _y = y;
        _x = Math.Clamp(x, 0, _cols - 1);
        _pendingWrap = false;
    }

    private void LineFeed()
    {
        Commit(_rows[_y], keepBlank: true);
        if (_y == _bottom) ScrollUp(_top, _bottom);
        else if (_y < _rows.Count - 1) _y++;
    }

    private void ReverseIndex()
    {
        if (_y == _top) ScrollDown(_top, _bottom);
        else if (_y > 0) _y--;
    }

    private void ScrollUp(int top, int bottom)
    {
        // Only a row leaving the real top of the screen has left for good; inside a scroll region it is gone without being shown.
        if (top == 0) Commit(_rows[top]);
        _rows.RemoveAt(top);
        _rows.Insert(bottom, new Row(_cols));
    }

    private void ScrollDown(int top, int bottom)
    {
        _rows.RemoveAt(bottom);
        _rows.Insert(top, new Row(_cols));
    }

    private void EraseDisplay(int mode)
    {
        switch (mode)
        {
            case 0:
                EraseLine(0);
                for (var y = _y + 1; y < _rows.Count; y++) Clear(_rows[y]);
                break;
            case 1:
                EraseLine(1);
                for (var y = 0; y < _y; y++) Clear(_rows[y]);
                break;
            default:
                // cls and friends: what was on screen still happened, so log it before it goes.
                Flush();
                foreach (var row in _rows) Clear(row);
                break;
        }
    }

    private void EraseLine(int mode)
    {
        var row = _rows[_y];
        var (from, to) = mode switch
        {
            0 => (_x, _cols - 1),
            1 => (0, _x),
            _ => (0, _cols - 1),
        };
        for (var i = from; i <= to; i++) row.Set(i, ' ');
    }

    private static void Clear(Row row)
    {
        for (var i = 0; i < row.Cells.Length; i++) row.Set(i, ' ');
    }

    private void DeleteChars(int n)
    {
        var row = _rows[_y];
        for (var i = _x; i < _cols; i++) row.Set(i, i + n < _cols ? row.Cells[i + n] : ' ');
    }

    private void InsertChars(int n)
    {
        var row = _rows[_y];
        for (var i = _cols - 1; i >= _x; i--) row.Set(i, i - n >= _x ? row.Cells[i - n] : ' ');
    }

    private void InsertLines(int n)
    {
        if (_y < _top || _y > _bottom) return;
        for (var i = 0; i < n; i++) ScrollDown(_y, _bottom);
    }

    private void DeleteLines(int n)
    {
        if (_y < _top || _y > _bottom) return;
        for (var i = 0; i < n; i++)
        {
            Commit(_rows[_y]);
            _rows.RemoveAt(_y);
            _rows.Insert(_bottom, new Row(_cols));
        }
    }

    private void EnterAlternateScreen()
    {
        if (_savedMainScreen is not null) return;
        _savedMainScreen = _rows;
        _rows = Enumerable.Range(0, _savedMainScreen.Count).Select(_ => new Row(_cols)).ToList();
    }

    private void LeaveAlternateScreen()
    {
        if (_savedMainScreen is null) return;
        _rows = _savedMainScreen;
        _savedMainScreen = null;
    }

    private void Reset()
    {
        Flush();
        var rows = _rows.Count;
        _rows = Enumerable.Range(0, rows).Select(_ => new Row(_cols)).ToList();
        _x = _y = 0;
        _top = 0;
        _bottom = rows - 1;
        _pendingWrap = false;
    }
}

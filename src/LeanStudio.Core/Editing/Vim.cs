using System.Text;

namespace LeanStudio.Core.Editing;

/// <summary>The editor as the Vim engine sees it: text, a caret, a selection, and undo.</summary>
public interface IVimHost
{
    /// <summary>The whole text, with <c>\n</c> line breaks.</summary>
    string Text { get; }

    /// <summary>The caret offset.</summary>
    int Caret { get; set; }

    /// <summary>Replace <paramref name="length"/> characters at <paramref name="offset"/>.</summary>
    void Replace(int offset, int length, string text);

    /// <summary>Show a selection from <paramref name="start"/> to <paramref name="end"/> (exclusive), or none when they are equal.</summary>
    void Select(int start, int end);

    /// <summary>Group the edits between <see cref="BeginChange"/> and <see cref="EndChange"/> into one undo step.</summary>
    void BeginChange();

    /// <summary>See <see cref="BeginChange"/>.</summary>
    void EndChange();

    /// <summary>Undo the last change.</summary>
    void Undo();

    /// <summary>Redo the last undone change.</summary>
    void Redo();

    /// <summary>Run an ex command (<c>w</c>, <c>q</c>, <c>wq</c>…) the engine does not handle itself; false if unknown.</summary>
    bool Ex(string command);
}

/// <summary>The engine's mode.</summary>
public enum VimMode
{
    /// <summary>Keys are commands.</summary>
    Normal,
    /// <summary>Keys type text (the editor handles them).</summary>
    Insert,
    /// <summary>Characterwise selection.</summary>
    Visual,
    /// <summary>Linewise selection.</summary>
    VisualLine,
    /// <summary>Typing an ex command after <c>:</c>, or a search after <c>/</c> or <c>?</c>.</summary>
    CommandLine,
}

/// <summary>
/// A Vim emulation for the editor, as a pure state machine over <see cref="IVimHost"/>: normal, insert, visual and
/// linewise visual modes; counts; motions (<c>h j k l w b e W B E 0 ^ $ gg G f t F T ; , % { }</c>); operators
/// <c>d c y &gt; &lt;</c> with motions and text objects (<c>iw aw ip</c> and the bracket and quote pairs, Lean's
/// ⟨⟩ included); <c>x X D C Y s S r J ~ p P o O i a I A u Ctrl-R</c>; <c>.</c> to repeat; search with <c>/ ? n N * #</c>;
/// and <c>:w :q :wq :x :N</c>.
/// </summary>
/// <remarks>
/// Keys are strings: a character (<c>"d"</c>), or a name in angle brackets (<c>"&lt;Esc&gt;"</c>, <c>"&lt;CR&gt;"</c>,
/// <c>"&lt;BS&gt;"</c>, <c>"&lt;C-r&gt;"</c>, <c>"&lt;C-d&gt;"</c>, <c>"&lt;C-u&gt;"</c>). In insert mode only
/// <c>&lt;Esc&gt;</c> is the engine's; everything else is left to the editor.
/// </remarks>
public sealed class VimEngine(IVimHost host)
{
    private readonly StringBuilder _pending = new();
    private string _register = "";
    private bool _registerLinewise;
    private string _command = "";
    private char _commandKind;
    private int _visualAnchor;
    private (char Kind, char Target)? _lastFind;
    private string? _lastSearch;
    private bool _searchBackward;
    private string? _lastChange;
    private int _insertStart = -1;
    private string _insertPrefix = "";
    private bool _replaying;

    /// <summary>The current mode.</summary>
    public VimMode Mode { get; private set; } = VimMode.Normal;

    /// <summary>What the status bar shows: the mode, the pending keys, or the command being typed.</summary>
    public string Status => Mode switch
    {
        VimMode.Insert => "-- INSERT --",
        VimMode.Visual => "-- VISUAL --",
        VimMode.VisualLine => "-- VISUAL LINE --",
        VimMode.CommandLine => _commandKind + _command,
        _ => _pending.Length > 0 ? _pending.ToString() : "",
    };

    /// <summary>Raised when the mode or the status changes.</summary>
    public event Action? Changed;

    private string T => host.Text;

    // ---- keys ----

    /// <summary>Handle one key. Returns false when the editor should handle it itself (typing in insert mode).</summary>
    public bool Key(string key)
    {
        bool handled = Mode switch
        {
            VimMode.Insert => InsertKey(key),
            VimMode.CommandLine => CommandLineKey(key),
            _ => NormalOrVisualKey(key),
        };
        Changed?.Invoke();
        return handled;
    }

    /// <summary>Handle a sequence of keys (for tests and <c>.</c>); multi-character names in angle brackets count as one key.</summary>
    public void Keys(string keys)
    {
        for (int i = 0; i < keys.Length; i++)
        {
            if (keys[i] == '<' && keys.IndexOf('>', i) is int close and > 0 && close - i > 1 && !keys[(i + 1)..close].Contains('<'))
            {
                Key(keys[i..(close + 1)]);
                i = close;
            }
            else
            {
                Key(keys[i].ToString());
            }
        }
    }

    private bool InsertKey(string key)
    {
        if (key != "<Esc>")
        {
            if (!_replaying)
            {
                return false; // the editor types it
            }
            // Repeating with `.`: no one is typing, so type the recorded text here.
            string text = key switch { "<CR>" => "\n", "<lt>" => "<", _ => key };
            host.Replace(host.Caret, 0, text);
            host.Caret += text.Length;
            return true;
        }
        // Record what was typed, so `.` can type it again.
        if (!_replaying && _insertStart >= 0 && host.Caret >= _insertStart)
        {
            _lastChange = _insertPrefix + Escape(T[_insertStart..host.Caret]) + "<Esc>";
        }
        _insertStart = -1;
        host.EndChange();
        Mode = VimMode.Normal;
        int ls = LineStart(host.Caret);
        if (host.Caret > ls)
        {
            host.Caret--;
        }
        return true;
    }

    private static string Escape(string typed) => typed.Replace("<", "<lt>", StringComparison.Ordinal).Replace("\n", "<CR>", StringComparison.Ordinal);

    private bool CommandLineKey(string key)
    {
        switch (key)
        {
            case "<Esc>":
                Mode = VimMode.Normal;
                break;
            case "<CR>":
                Mode = VimMode.Normal;
                if (_commandKind == ':')
                {
                    ExCommand(_command.Trim());
                }
                else
                {
                    _lastSearch = _command.Length > 0 ? _command : _lastSearch;
                    _searchBackward = _commandKind == '?';
                    Search(_searchBackward, 1);
                }
                break;
            case "<BS>":
                if (_command.Length == 0)
                {
                    Mode = VimMode.Normal;
                }
                else
                {
                    _command = _command[..^1];
                }
                break;
            default:
                if (key.Length == 1 || key == "<lt>")
                {
                    _command += key == "<lt>" ? "<" : key;
                }
                break;
        }
        return true;
    }

    private void ExCommand(string c)
    {
        if (int.TryParse(c, out int line))
        {
            host.Caret = FirstNonBlank(OffsetOfLine(Math.Max(0, line - 1)));
            return;
        }
        if (c.Length > 0)
        {
            host.Ex(c);
        }
    }

    // ---- normal and visual ----

    private bool NormalOrVisualKey(string key)
    {
        if (key == "<Esc>")
        {
            _pending.Clear();
            if (Mode is VimMode.Visual or VimMode.VisualLine)
            {
                Mode = VimMode.Normal;
                host.Select(host.Caret, host.Caret);
            }
            return true;
        }
        _pending.Append(key == "<lt>" ? "<" : key);
        string p = _pending.ToString();
        CommandResult r = Run(p);
        if (r != CommandResult.NeedMore)
        {
            _pending.Clear();
        }
        if (Mode is VimMode.Visual or VimMode.VisualLine)
        {
            ShowVisual();
        }
        return true;
    }

    private enum CommandResult
    {
        Done,
        NeedMore,
        Invalid,
    }

    /// <summary>Parse and run a complete normal-mode command, or say more keys are needed.</summary>
    private CommandResult Run(string p)
    {
        int i = 0;
        int count1 = ReadCount(p, ref i);
        if (i == p.Length)
        {
            return CommandResult.NeedMore;
        }
        char c = p[i];
        string rest = p[(i + 1)..];
        int count = Math.Max(1, count1);
        bool visual = Mode is VimMode.Visual or VimMode.VisualLine;

        // Named keys (<C-r>, <C-d>, <C-u>) come whole; don't mistake their `<` for the shift operator.
        if (c == '<' && rest.Length > 1 && rest.EndsWith('>') && char.IsAsciiLetter(rest[0]))
        {
            switch (p[i..])
            {
                case "<C-r>":
                    for (int k = 0; k < count; k++)
                    {
                        host.Redo();
                    }
                    ClampCaret();
                    return CommandResult.Done;
                case "<C-d>":
                    host.Caret = FirstNonBlank(OffsetOfLine(Math.Min(LineOf(host.Caret) + 15 * count, LineCount - 1)));
                    return CommandResult.Done;
                case "<C-u>":
                    host.Caret = FirstNonBlank(OffsetOfLine(Math.Max(LineOf(host.Caret) - 15 * count, 0)));
                    return CommandResult.Done;
            }
            var named = Motion(p[i..], count, forOperator: false);
            if (named.Target is int nt)
            {
                host.Caret = nt;
                return CommandResult.Done;
            }
            return CommandResult.Invalid;
        }

        // Operators wait for a motion (or, doubled, act on lines); in visual mode they act on the selection.
        if (c is 'd' or 'c' or 'y' or '>' or '<')
        {
            if (visual)
            {
                ApplyToSelection(c);
                if (c != 'y' && !_replaying)
                {
                    _lastChange = null;
                }
                return CommandResult.Done;
            }
            if (rest.Length == 0)
            {
                return CommandResult.NeedMore;
            }
            if (rest.Length == 1 && rest[0] == c)
            {
                return Record(p, OperateLines(c, count));
            }
            int j = 0;
            int count2 = ReadCount(rest, ref j);
            string motion = rest[j..];
            if (motion.Length == 0)
            {
                return CommandResult.NeedMore;
            }
            int total = Math.Max(1, count1) * Math.Max(1, count2);
            if (motion[0] is 'i' or 'a')
            {
                if (motion.Length < 2)
                {
                    return CommandResult.NeedMore;
                }
                return TextObject(motion[0] == 'i', motion[1]) is (int s, int e, bool lw) ? Record(p, Operate(c, s, e, lw)) : CommandResult.Invalid;
            }
            // `cw` changes to the end of the word under the cursor (and then to the ends of the next words), not
            // up to the next word's start, and not past this word even when it has one letter.
            if (c == 'c' && motion is "w" or "W" && !char.IsWhiteSpace(CharAt(host.Caret)) && WordAt(host.Caret, motion == "W") is (_, int wordEnd))
            {
                int changeEnd = wordEnd;
                for (int k = 1; k < total; k++)
                {
                    changeEnd = WordEnd(changeEnd - 1, motion == "W") + 1;
                }
                return Record(p, Operate('c', host.Caret, changeEnd, false));
            }
            var m = Motion(motion, total, forOperator: true);
            if (m.NeedMore)
            {
                return CommandResult.NeedMore;
            }
            if (m.Target is not int target)
            {
                return CommandResult.Invalid;
            }
            int from = host.Caret;
            (int start, int end) = from <= target ? (from, target) : (target, from);
            if (m.Linewise)
            {
                return Record(p, Operate(c, LineStart(start), LineEndWithBreak(end), true));
            }
            if (m.Inclusive)
            {
                end = Math.Min(end + 1, T.Length);
            }
            return Record(p, Operate(c, start, end, false));
        }

        switch (c)
        {
            case 'i' or 'a' or 'I' or 'A' or 'o' or 'O' when !visual:
                StartInsert(c, p);
                return CommandResult.Done;
            case 'v':
                ToggleVisual(VimMode.Visual);
                return CommandResult.Done;
            case 'V':
                ToggleVisual(VimMode.VisualLine);
                return CommandResult.Done;
            case 'x' when !visual:
                return Record(p, DeleteChars(count, forward: true));
            case 'x' when visual:
                ApplyToSelection('d');
                return CommandResult.Done;
            case 'X':
                return Record(p, DeleteChars(count, forward: false));
            case 'D':
                return Record(p, Operate('d', host.Caret, LineEnd(host.Caret), false));
            case 'C':
                return Record(p, Operate('c', host.Caret, LineEnd(host.Caret), false));
            case 'Y':
                return Record(p, OperateLines('y', count));
            case 's':
                DeleteChars(count, forward: true);
                StartInsert('i', p);
                return CommandResult.Done;
            case 'S':
                return Record(p, OperateLines('c', count));
            case 'p' or 'P':
                Put(c == 'p', count);
                return Record(p, CommandResult.Done);
            case 'u':
                for (int k = 0; k < count; k++)
                {
                    host.Undo();
                }
                ClampCaret();
                return CommandResult.Done;
            case 'J':
                return Record(p, Join(Math.Max(2, count)));
            case '~':
                return Record(p, ToggleCase(count));
            case 'r':
                if (rest.Length == 0)
                {
                    return CommandResult.NeedMore;
                }
                return Record(p, ReplaceChars(rest == "<CR>" ? "\n" : rest, count));
            case '.':
                // Not while repeating already: a recorded change whose first command can't apply here (say `cw` at
                // the end of the text) leaves the text it typed to run as commands, and a `.` in it would repeat
                // the repeat forever, until the stack overflows and the app with it.
                if (_lastChange is string last && !_replaying)
                {
                    _pending.Clear(); // the replayed keys start a command of their own
                    _replaying = true;
                    try
                    {
                        for (int k = 0; k < count; k++)
                        {
                            Keys(last);
                        }
                    }
                    finally
                    {
                        _replaying = false;
                    }
                }
                return CommandResult.Done;
            case ':' or '/' or '?':
                Mode = VimMode.CommandLine;
                _commandKind = c;
                _command = "";
                return CommandResult.Done;
            case 'n' or 'N':
                Search(c == 'N' ? !_searchBackward : _searchBackward, count);
                return CommandResult.Done;
            case '*' or '#':
                if (WordAt(host.Caret) is (int ws, int we))
                {
                    _lastSearch = T[ws..we];
                    _searchBackward = c == '#';
                    Search(_searchBackward, count);
                }
                return CommandResult.Done;
        }
        // Otherwise, a motion.
        string motionKeys = p[i..];
        var mv = Motion(motionKeys, count, forOperator: false);
        if (mv.NeedMore)
        {
            return CommandResult.NeedMore;
        }
        if (mv.Target is int t)
        {
            host.Caret = t;
            if (!visual)
            {
                ClampCaret();
            }
            return CommandResult.Done;
        }
        return CommandResult.Invalid;
    }

    private CommandResult Record(string keys, CommandResult r)
    {
        if (r == CommandResult.Done && !_replaying && Mode == VimMode.Normal)
        {
            _lastChange = keys;
        }
        return r;
    }

    private static int ReadCount(string p, ref int i)
    {
        int start = i;
        while (i < p.Length && char.IsAsciiDigit(p[i]) && !(i == start && p[i] == '0'))
        {
            i++;
        }
        return i == start ? 0 : int.Parse(p[start..i], System.Globalization.CultureInfo.InvariantCulture);
    }

    // ---- motions ----

    private readonly record struct MotionResult(int? Target, bool Inclusive, bool Linewise, bool NeedMore);

    private MotionResult Motion(string m, int count, bool forOperator)
    {
        int pos = host.Caret;
        MotionResult Exclusive(int t) => new(t, false, false, false);
        MotionResult Inclusive(int t) => new(t, true, false, false);
        MotionResult Lines(int t) => new(t, false, true, false);
        var more = new MotionResult(null, false, false, true);
        switch (m)
        {
            case "h" or "<BS>":
                return Exclusive(Math.Max(LineStart(pos), pos - count));
            case "l" or " ":
                return Exclusive(Math.Min(forOperator ? LineEnd(pos) : LastCharOfLine(pos), pos + count));
            case "j" or "<CR>" or "+":
            {
                int line = Math.Min(LineOf(pos) + count, LineCount - 1);
                return Lines(m == "j" ? ColumnOnLine(line, ColumnOf(pos)) : FirstNonBlank(OffsetOfLine(line)));
            }
            case "k" or "-":
            {
                int line = Math.Max(LineOf(pos) - count, 0);
                return Lines(m == "k" ? ColumnOnLine(line, ColumnOf(pos)) : FirstNonBlank(OffsetOfLine(line)));
            }
            case "0":
                return Exclusive(LineStart(pos));
            case "^":
                return Exclusive(FirstNonBlank(LineStart(pos)));
            case "$":
            {
                int line = Math.Min(LineOf(pos) + count - 1, LineCount - 1);
                int end = LineEnd(OffsetOfLine(line));
                return forOperator ? Exclusive(end) : Inclusive(Math.Max(OffsetOfLine(line), end - 1));
            }
            case "w" or "W":
            {
                int t = pos;
                for (int k = 0; k < count; k++)
                {
                    t = NextWordStart(t, m == "W");
                }
                // An operator never crosses to the next line for `w`: `dw` on the last word stops at the line's end.
                if (forOperator && LineOf(t) > LineOf(pos) && LineEnd(pos) > pos)
                {
                    t = LineEnd(pos);
                }
                return Exclusive(t);
            }
            case "b" or "B":
            {
                int t = pos;
                for (int k = 0; k < count; k++)
                {
                    t = PrevWordStart(t, m == "B");
                }
                return Exclusive(t);
            }
            case "e" or "E":
            {
                int t = pos;
                for (int k = 0; k < count; k++)
                {
                    t = WordEnd(t, m == "E");
                }
                return Inclusive(t);
            }
            case "G":
                return Lines(FirstNonBlank(OffsetOfLine(count > 1 || _pending.ToString().Any(char.IsAsciiDigit) ? Math.Min(count - 1, LineCount - 1) : LineCount - 1)));
            case "g":
                return more;
            case "gg":
                return Lines(FirstNonBlank(OffsetOfLine(Math.Min(count - 1, LineCount - 1))));
            case "%":
            {
                int from = pos;
                while (from < LineEnd(pos) && LeanText.MatchingBracket(T, from) < 0)
                {
                    from++;
                }
                int match = LeanText.MatchingBracket(T, from);
                return match < 0 ? new MotionResult(null, false, false, false) : Inclusive(match);
            }
            case "}":
            {
                // Line by line from the cursor (not by line numbers, which are counted from the top each time:
                // that made } take seconds in a large file).
                int ls = LineStart(pos);
                for (int k = 0; k < count; k++)
                {
                    int next = T.IndexOf('\n', ls);
                    if (next < 0)
                    {
                        return Exclusive(T.Length);
                    }
                    ls = next + 1;
                    while (T.IndexOf('\n', ls) >= 0 && !IsBlankAt(ls))
                    {
                        ls = T.IndexOf('\n', ls) + 1;
                    }
                    if (T.IndexOf('\n', ls) < 0)
                    {
                        return Exclusive(T.Length); // the last line: the end of the text
                    }
                }
                return Exclusive(ls);
            }
            case "{":
            {
                int ls = LineStart(pos);
                for (int k = 0; k < count && ls > 0; k++)
                {
                    ls = LineStart(ls - 1);
                    while (ls > 0 && !IsBlankAt(ls))
                    {
                        ls = LineStart(ls - 1);
                    }
                }
                return Exclusive(ls);
            }
            case ";" or ",":
                if (_lastFind is (char kind, char target))
                {
                    char k2 = m == ";" ? kind : kind switch { 'f' => 'F', 'F' => 'f', 't' => 'T', _ => 't' };
                    return Find(k2, target, count, repeat: true);
                }
                return new MotionResult(null, false, false, false);
        }
        if (m.Length >= 1 && m[0] is 'f' or 't' or 'F' or 'T')
        {
            if (m.Length == 1)
            {
                return more;
            }
            string target = m[1..];
            if (target.Length != 1)
            {
                return new MotionResult(null, false, false, false);
            }
            _lastFind = (m[0], target[0]);
            return Find(m[0], target[0], count, repeat: false);
        }
        return new MotionResult(null, false, false, false);
    }

    private MotionResult Find(char kind, char target, int count, bool repeat)
    {
        int pos = host.Caret, ls = LineStart(pos), le = LineEnd(pos);
        int found = pos;
        for (int k = 0; k < count; k++)
        {
            int next = -1;
            if (kind is 'f' or 't')
            {
                int from = found + 1 + (kind == 't' && (repeat || k > 0) ? 1 : 0);
                for (int q = from; q < le; q++)
                {
                    if (T[q] == target)
                    {
                        next = q;
                        break;
                    }
                }
            }
            else
            {
                int from = found - 1 - (kind == 'T' && (repeat || k > 0) ? 1 : 0);
                for (int q = from; q >= ls; q--)
                {
                    if (T[q] == target)
                    {
                        next = q;
                        break;
                    }
                }
            }
            if (next < 0)
            {
                return new MotionResult(null, false, false, false);
            }
            found = next;
        }
        int t = kind switch { 't' => found - 1, 'T' => found + 1, _ => found };
        return kind is 'f' or 't' ? new MotionResult(t, true, false, false) : new MotionResult(t, false, false, false);
    }

    // ---- text objects ----

    private (int Start, int End, bool Linewise)? TextObject(bool inner, char kind)
    {
        int pos = host.Caret;
        switch (kind)
        {
            case 'w' or 'W':
            {
                if (CharAt(pos) is ' ' or '\t')
                {
                    // On blanks, `iw` is the run of blanks (and `aw` the blanks and the word after them).
                    int bs = pos, be = pos;
                    while (bs > LineStart(pos) && T[bs - 1] is ' ' or '\t')
                    {
                        bs--;
                    }
                    while (be < LineEnd(pos) && T[be] is ' ' or '\t')
                    {
                        be++;
                    }
                    if (!inner && WordAt(be, kind == 'W') is (_, int we))
                    {
                        be = we;
                    }
                    return (bs, be, false);
                }
                if (WordAt(pos, kind == 'W') is not (int s, int e))
                {
                    return null;
                }
                if (!inner)
                {
                    // A word and the space after it (or before it, at the end of a line).
                    int e2 = e;
                    while (e2 < LineEnd(pos) && T[e2] is ' ' or '\t')
                    {
                        e2++;
                    }
                    if (e2 == e)
                    {
                        while (s > LineStart(pos) && T[s - 1] is ' ' or '\t')
                        {
                            s--;
                        }
                    }
                    e = e2;
                }
                return (s, e, false);
            }
            case 'p':
            {
                int line = LineOf(pos);
                int first = line, last = line;
                while (first > 0 && !IsBlankLine(first - 1))
                {
                    first--;
                }
                while (last < LineCount - 1 && !IsBlankLine(last + 1))
                {
                    last++;
                }
                if (!inner && last < LineCount - 1)
                {
                    last++;
                }
                return (OffsetOfLine(first), LineEndWithBreak(OffsetOfLine(last)), true);
            }
            case '"' or '\'' or '`':
            {
                int ls = LineStart(pos), le = LineEnd(pos);
                var quotes = new List<int>();
                for (int q = ls; q < le; q++)
                {
                    if (T[q] == kind && (q == 0 || T[q - 1] != '\\'))
                    {
                        quotes.Add(q);
                    }
                }
                for (int k = 0; k + 1 < quotes.Count; k += 2)
                {
                    if (quotes[k] <= pos && pos <= quotes[k + 1] || (k == 0 && pos < quotes[0]))
                    {
                        return inner ? (quotes[k] + 1, quotes[k + 1], false) : (quotes[k], quotes[k + 1] + 1, false);
                    }
                }
                return null;
            }
        }
        (char open, char close)? pair = kind switch
        {
            '(' or ')' or 'b' => ('(', ')'),
            '[' or ']' => ('[', ']'),
            '{' or '}' or 'B' => ('{', '}'),
            '<' or '>' => ('<', '>'),
            '⟨' or '⟩' => ('⟨', '⟩'),
            _ => null,
        };
        if (pair is not (char o, char c))
        {
            return null;
        }
        // The innermost pair around the caret.
        int depth = 0, openAt = -1;
        // (At the very end of the text, on the empty last line, the search starts at the last character.)
        for (int q = CharAt(pos) == c ? pos - 1 : Math.Min(pos, T.Length - 1); q >= 0; q--)
        {
            if (T[q] == c && q != pos)
            {
                depth++;
            }
            else if (T[q] == o)
            {
                if (depth == 0)
                {
                    openAt = q;
                    break;
                }
                depth--;
            }
        }
        if (openAt < 0)
        {
            return null;
        }
        int closeAt = LeanText.MatchingBracket(T, openAt);
        if (closeAt < 0)
        {
            return null;
        }
        return inner ? (openAt + 1, closeAt, false) : (openAt, closeAt + 1, false);
    }

    // ---- operations ----

    private CommandResult Operate(char op, int start, int end, bool linewise)
    {
        if (end < start)
        {
            (start, end) = (end, start);
        }
        end = Math.Min(end, T.Length);
        string text = T[start..end];
        switch (op)
        {
            case 'y':
                Yank(text, linewise);
                if (!linewise)
                {
                    host.Caret = start;
                }
                return CommandResult.Done;
            case 'd':
                Yank(text, linewise);
                host.BeginChange();
                if (linewise && end == T.Length && start > 0 && !text.EndsWith('\n'))
                {
                    // The last line: take the line break before it too.
                    start--;
                }
                host.Replace(start, end - start, "");
                host.EndChange();
                host.Caret = linewise ? FirstNonBlank(LineStart(Math.Min(start, T.Length))) : start;
                ClampCaret();
                return CommandResult.Done;
            case 'c':
                Yank(text, linewise);
                host.BeginChange();
                if (linewise)
                {
                    // Keep the line (and its indentation), empty it, and type there.
                    string indent = LeadingWhitespace(start);
                    int bodyEnd = text.EndsWith('\n') ? end - 1 : end;
                    host.Replace(start, bodyEnd - start, indent);
                    host.Caret = start + indent.Length;
                }
                else
                {
                    host.Replace(start, end - start, "");
                    host.Caret = start;
                }
                BeginInsert("");
                return CommandResult.Done;
            case '>' or '<':
            {
                host.BeginChange();
                int first = LineOf(start), last = LineOf(Math.Max(start, end - 1));
                for (int line = last; line >= first; line--)
                {
                    int ls = OffsetOfLine(line);
                    if (op == '>')
                    {
                        if (LineEnd(ls) > ls)
                        {
                            host.Replace(ls, 0, "  ");
                        }
                    }
                    else
                    {
                        int n = 0;
                        while (n < 2 && ls + n < T.Length && T[ls + n] == ' ')
                        {
                            n++;
                        }
                        host.Replace(ls, n, "");
                    }
                }
                host.EndChange();
                host.Caret = FirstNonBlank(OffsetOfLine(first));
                return CommandResult.Done;
            }
        }
        return CommandResult.Invalid;
    }

    private CommandResult OperateLines(char op, int count)
    {
        int first = LineOf(host.Caret);
        int last = Math.Min(first + count - 1, LineCount - 1);
        return Operate(op, OffsetOfLine(first), LineEndWithBreak(OffsetOfLine(last)), true);
    }

    private void ApplyToSelection(char op)
    {
        (int s, int e) = VisualRange();
        bool linewise = Mode == VimMode.VisualLine;
        Mode = VimMode.Normal;
        host.Select(host.Caret, host.Caret);
        if (op == 'c' && linewise)
        {
            Operate('c', s, e, true);
            return;
        }
        Operate(op, s, e, linewise);
    }

    private void Yank(string text, bool linewise)
    {
        _register = linewise && !text.EndsWith('\n') ? text + "\n" : text;
        _registerLinewise = linewise;
    }

    /// <summary>The unnamed register: what the last delete, change or yank took, and whether it was whole lines.</summary>
    public (string Text, bool Linewise) Register => (_register, _registerLinewise);

    private void Put(bool after, int count)
    {
        if (_register.Length == 0)
        {
            return;
        }
        string text = string.Concat(Enumerable.Repeat(_register, count));
        host.BeginChange();
        if (_registerLinewise)
        {
            int at = after ? LineEndWithBreak(host.Caret) : LineStart(host.Caret);
            if (after && at == T.Length && !T.EndsWith('\n'))
            {
                host.Replace(at, 0, "\n" + text.TrimEnd('\n'));
                host.Caret = at + 1;
            }
            else
            {
                host.Replace(at, 0, text);
                host.Caret = at;
            }
            host.Caret = FirstNonBlank(host.Caret);
        }
        else
        {
            int at = after && host.Caret < LineEnd(host.Caret) ? host.Caret + 1 : host.Caret;
            host.Replace(at, 0, text);
            host.Caret = at + text.Length - 1;
        }
        host.EndChange();
    }

    private CommandResult DeleteChars(int count, bool forward)
    {
        int pos = host.Caret;
        if (forward)
        {
            int end = Math.Min(pos + count, LineEnd(pos));
            if (end == pos)
            {
                return CommandResult.Done;
            }
            Operate('d', pos, end, false);
        }
        else
        {
            int start = Math.Max(LineStart(pos), pos - count);
            if (start == pos)
            {
                return CommandResult.Done;
            }
            Operate('d', start, pos, false);
        }
        return CommandResult.Done;
    }

    private CommandResult ReplaceChars(string with, int count)
    {
        int pos = host.Caret;
        if (pos + count > LineEnd(pos))
        {
            return CommandResult.Done;
        }
        host.BeginChange();
        host.Replace(pos, count, with == "\n" ? "\n" : string.Concat(Enumerable.Repeat(with, count)));
        host.EndChange();
        host.Caret = with == "\n" ? pos + 1 : pos + count - 1;
        return CommandResult.Done;
    }

    private CommandResult Join(int lines)
    {
        host.BeginChange();
        for (int k = 1; k < lines && LineOf(host.Caret) < LineCount - 1; k++)
        {
            int le = LineEnd(host.Caret);
            int next = le + 1, ns = next;
            while (ns < T.Length && T[ns] is ' ' or '\t')
            {
                ns++;
            }
            bool emptyNext = ns >= T.Length || T[ns] == '\n';
            int trim = le;
            while (trim > LineStart(host.Caret) && T[trim - 1] is ' ' or '\t')
            {
                trim--;
            }
            string sep = emptyNext || T[ns] == ')' ? "" : " ";
            host.Replace(trim, ns - trim, sep);
            host.Caret = trim;
        }
        host.EndChange();
        return CommandResult.Done;
    }

    private CommandResult ToggleCase(int count)
    {
        int pos = host.Caret, end = Math.Min(pos + count, LineEnd(pos));
        if (end <= pos)
        {
            return CommandResult.Done;
        }
        string s = new(T[pos..end].Select(ch => char.IsUpper(ch) ? char.ToLowerInvariant(ch) : char.ToUpperInvariant(ch)).ToArray());
        host.BeginChange();
        host.Replace(pos, end - pos, s);
        host.EndChange();
        host.Caret = Math.Min(end, LastCharOfLine(pos));
        return CommandResult.Done;
    }

    private void StartInsert(char how, string keys)
    {
        int pos = host.Caret;
        host.BeginChange();
        switch (how)
        {
            case 'a':
                host.Caret = Math.Min(pos + 1, LineEnd(pos));
                break;
            case 'I':
                host.Caret = FirstNonBlank(LineStart(pos));
                break;
            case 'A':
                host.Caret = LineEnd(pos);
                break;
            case 'o':
            {
                int le = LineEnd(pos);
                string indent = LeadingWhitespace(LineStart(pos));
                host.Replace(le, 0, "\n" + indent);
                host.Caret = le + 1 + indent.Length;
                break;
            }
            case 'O':
            {
                int ls = LineStart(pos);
                string indent = LeadingWhitespace(ls);
                host.Replace(ls, 0, indent + "\n");
                host.Caret = ls + indent.Length;
                break;
            }
        }
        BeginInsert(keys);
    }

    private void BeginInsert(string keys)
    {
        Mode = VimMode.Insert;
        _insertStart = host.Caret;
        _insertPrefix = keys;
    }

    private void ToggleVisual(VimMode mode)
    {
        if (Mode == mode)
        {
            Mode = VimMode.Normal;
            host.Select(host.Caret, host.Caret);
            return;
        }
        if (Mode == VimMode.Normal)
        {
            _visualAnchor = host.Caret;
        }
        Mode = mode;
        ShowVisual();
    }

    private (int Start, int End) VisualRange()
    {
        int a = _visualAnchor, c = host.Caret;
        (int s, int e) = a <= c ? (a, c) : (c, a);
        return Mode == VimMode.VisualLine ? (LineStart(s), LineEndWithBreak(e)) : (s, Math.Min(e + 1, T.Length));
    }

    private void ShowVisual()
    {
        (int s, int e) = VisualRange();
        host.Select(s, e);
    }

    private void Search(bool backward, int count)
    {
        if (string.IsNullOrEmpty(_lastSearch))
        {
            return;
        }
        int pos = host.Caret;
        for (int k = 0; k < count; k++)
        {
            int found = backward
                ? (pos > 0 ? T.LastIndexOf(_lastSearch, pos - 1, StringComparison.Ordinal) : -1)
                : T.IndexOf(_lastSearch, Math.Min(pos + 1, T.Length), StringComparison.Ordinal);
            if (found < 0)
            {
                // Wrap around, as Vim does.
                found = backward ? T.LastIndexOf(_lastSearch, StringComparison.Ordinal) : T.IndexOf(_lastSearch, StringComparison.Ordinal);
            }
            if (found < 0)
            {
                return;
            }
            pos = found;
        }
        host.Caret = pos;
    }

    // ---- text helpers ----

    private char CharAt(int i) => i >= 0 && i < T.Length ? T[i] : '\n';

    private int LineStart(int pos)
    {
        int i = T.LastIndexOf('\n', Math.Max(0, Math.Min(pos, T.Length) - 1));
        return pos == 0 || i < 0 ? 0 : i + 1;
    }

    private int LineEnd(int pos)
    {
        int i = T.IndexOf('\n', Math.Min(pos, T.Length));
        return i < 0 ? T.Length : i;
    }

    private int LineEndWithBreak(int pos) => Math.Min(LineEnd(pos) + 1, T.Length);

    private int LastCharOfLine(int pos) => Math.Max(LineStart(pos), LineEnd(pos) - 1);

    private int LineCount => T.Count(ch => ch == '\n') + 1;

    private int LineOf(int pos) => T.AsSpan(0, Math.Min(pos, T.Length)).Count('\n');

    private int ColumnOf(int pos) => pos - LineStart(pos);

    private int OffsetOfLine(int line)
    {
        int off = 0;
        for (int k = 0; k < line; k++)
        {
            int n = T.IndexOf('\n', off);
            if (n < 0)
            {
                return T.Length;
            }
            off = n + 1;
        }
        return off;
    }

    private int ColumnOnLine(int line, int column)
    {
        int ls = OffsetOfLine(line);
        return Math.Min(ls + column, LastCharOfLine(ls));
    }

    private int FirstNonBlank(int lineStart)
    {
        int i = lineStart;
        while (i < T.Length && T[i] is ' ' or '\t')
        {
            i++;
        }
        return i < T.Length && T[i] == '\n' ? lineStart : i;
    }

    private string LeadingWhitespace(int lineStart)
    {
        int i = lineStart;
        while (i < T.Length && T[i] is ' ' or '\t')
        {
            i++;
        }
        return T[lineStart..i];
    }

    /// <summary>Whether the line starting at <paramref name="lineStart"/> is empty or only blanks.</summary>
    private bool IsBlankAt(int lineStart) => T.AsSpan(lineStart, LineEnd(lineStart) - lineStart).IsWhiteSpace();

    private bool IsBlankLine(int line)
    {
        int ls = OffsetOfLine(line);
        return T.AsSpan(ls, LineEnd(ls) - ls).IsWhiteSpace();
    }

    private void ClampCaret()
    {
        int c = Math.Min(host.Caret, T.Length);
        if (Mode == VimMode.Normal && c == LineEnd(c) && c > LineStart(c))
        {
            c--;
        }
        host.Caret = Math.Max(0, c);
    }

    // Words: runs of letters, digits, _ and ' (Lean's identifiers), or runs of other non-blank characters. WORDs:
    // runs of non-blank characters.
    private static int Class(char ch, bool big) =>
        char.IsWhiteSpace(ch) ? 0 : big || char.IsLetterOrDigit(ch) || ch is '_' or '\'' ? 1 : 2;

    private int ClassAt(int i, bool big) => i >= T.Length ? 0 : Class(T[i], big);

    private int NextWordStart(int pos, bool big)
    {
        int n = T.Length;
        if (pos >= n)
        {
            return n;
        }
        int i = pos;
        int k = ClassAt(i, big);
        if (k > 0)
        {
            while (i < n && ClassAt(i, big) == k)
            {
                i++;
            }
        }
        // Skip blanks and line breaks, but stop at an empty line: it counts as a word.
        while (i < n && char.IsWhiteSpace(T[i]))
        {
            if (T[i] == '\n' && i + 1 < n && T[i + 1] == '\n')
            {
                return i + 1;
            }
            i++;
        }
        return i;
    }

    private int PrevWordStart(int pos, bool big)
    {
        int i = pos - 1;
        while (i > 0 && char.IsWhiteSpace(T[i]) && !(T[i] == '\n' && T[i - 1] == '\n'))
        {
            i--;
        }
        if (i <= 0)
        {
            return 0;
        }
        int k = Class(T[i], big);
        while (i > 0 && Class(T[i - 1], big) == k && T[i - 1] != '\n')
        {
            i--;
        }
        return i;
    }

    private int WordEnd(int pos, bool big)
    {
        int n = T.Length, i = pos + 1;
        while (i < n && char.IsWhiteSpace(T[i]))
        {
            i++;
        }
        if (i >= n)
        {
            return Math.Max(0, n - 1);
        }
        int k = Class(T[i], big);
        while (i + 1 < n && Class(T[i + 1], big) == k && !char.IsWhiteSpace(T[i + 1]))
        {
            i++;
        }
        return i;
    }

    private (int Start, int End)? WordAt(int pos, bool big = false)
    {
        if (pos >= T.Length || char.IsWhiteSpace(T[pos]))
        {
            return null;
        }
        int k = Class(T[pos], big), s = pos, e = pos;
        while (s > 0 && Class(T[s - 1], big) == k && !char.IsWhiteSpace(T[s - 1]))
        {
            s--;
        }
        while (e < T.Length && Class(T[e], big) == k && !char.IsWhiteSpace(T[e]))
        {
            e++;
        }
        return (s, e);
    }
}

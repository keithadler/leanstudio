namespace LeanStudio.Core.Editing;

/// <summary>
/// Emacs's keys for an editor, as a state machine over the same host the Vim engine uses. Keys are written as Emacs
/// writes them: <c>C-f</c> (Control), <c>M-f</c> (Meta, the Alt or ⌥ key), and <c>&lt;backspace&gt;</c>.
/// <list type="bullet">
/// <item>Moving: <c>C-f C-b C-n C-p C-a C-e M-f M-b M-&lt; M-&gt; C-v M-v</c>.</item>
/// <item>Killing and yanking: <c>C-d M-d M-&lt;backspace&gt; C-k C-w M-w C-y</c>. Kills in a row add up.</item>
/// <item>The mark: <c>C-SPC</c> sets it and the region follows the cursor; <c>C-x C-x</c> swaps them; <c>C-g</c> clears it.</item>
/// <item>Also <c>C-t</c> (transpose), <c>C-o</c> (open line), <c>C-/ C-_ C-x u</c> (undo), <c>C-x h</c> (select all).</item>
/// <item>For the app: <c>C-x C-s</c> saves, <c>C-x C-f</c> opens a file, <c>C-s</c> and <c>C-r</c> search (through <see cref="IVimHost.Ex"/>).</item>
/// </list>
/// Keys it doesn't know are left to the editor.
/// </summary>
public sealed class EmacsEngine(IVimHost host)
{
    private int? _mark;
    private string _prefix = "";
    private string _kill = "";
    private bool _lastWasKill;

    /// <summary>What was last killed (the latest entry of the kill ring).</summary>
    public string Killed => _kill;

    /// <summary>Raised with the text of every kill and copy, so the app can put it on the clipboard.</summary>
    public event Action<string>? KilledText;

    /// <summary>What the status bar shows: a pending <c>C-x</c>, or that the mark is set.</summary>
    public string Status => _prefix.Length > 0 ? _prefix + "-" : _mark is not null ? "Mark set" : "";

    /// <summary>Raised after each key that changed <see cref="Status"/>.</summary>
    public event Action? Changed;

    /// <summary>Handle a key. Returns whether it was an Emacs key (true) or should go to the editor (false).</summary>
    public bool Key(string key)
    {
        string before = Status;
        bool wasKill = _lastWasKill;
        _lastWasKill = false;
        bool handled = _prefix == "C-x" ? ControlX(key) : Plain(key, wasKill);
        if (Status != before)
        {
            Changed?.Invoke();
        }
        return handled;
    }

    private bool ControlX(string key)
    {
        _prefix = "";
        switch (key)
        {
            case "C-s":
                host.Ex("w");
                return true;
            case "C-f":
                host.Ex("open");
                return true;
            case "C-x":
                if (_mark is int m)
                {
                    int caret = host.Caret;
                    _mark = caret;
                    host.Caret = m;
                    Region();
                }
                return true;
            case "u":
                host.Undo();
                return true;
            case "h":
                _mark = 0;
                host.Caret = host.Text.Length;
                Region();
                return true;
            case "k":
                host.Ex("close");
                return true;
            case "C-g":
                return true;
            default:
                return true; // an unknown C-x sequence does nothing, as in Emacs
        }
    }

    private bool Plain(string key, bool afterKill)
    {
        string t = host.Text;
        int c = host.Caret;
        switch (key)
        {
            case "C-x":
                _prefix = "C-x";
                return true;
            case "C-g":
                _mark = null;
                host.Select(c, c);
                return true;
            case "C-SPC" or "C-@":
                _mark = c;
                host.Select(c, c);
                return true;
            case "C-f":
                return Move(Math.Min(t.Length, c + 1));
            case "C-b":
                return Move(Math.Max(0, c - 1));
            case "C-a":
                return Move(LineStart(t, c));
            case "C-e":
                return Move(LineEnd(t, c));
            case "C-n":
                return Move(LineBelow(t, c, 1));
            case "C-p":
                return Move(LineBelow(t, c, -1));
            case "C-v":
                return Move(LineBelow(t, c, 20));
            case "M-v":
                return Move(LineBelow(t, c, -20));
            case "M-f":
                return Move(WordEnd(t, c));
            case "M-b":
                return Move(WordStart(t, c));
            case "M-<":
                return Move(0);
            case "M->":
                return Move(t.Length);
            case "C-d":
                if (c < t.Length)
                {
                    host.Replace(c, 1, "");
                }
                return true;
            case "M-d":
                Kill(c, WordEnd(t, c), afterKill, append: true);
                return true;
            case "M-<backspace>":
                Kill(WordStart(t, c), c, afterKill, append: false);
                return true;
            case "C-k":
            {
                int end = LineEnd(t, c);
                Kill(c, end == c && end < t.Length ? end + 1 : end, afterKill, append: true);
                return true;
            }
            case "C-w":
                if (_mark is int m)
                {
                    Kill(Math.Min(m, c), Math.Max(m, c), afterKill: false, append: true);
                    _mark = null;
                }
                return true;
            case "M-w":
                if (_mark is int from)
                {
                    _kill = t[Math.Min(from, c)..Math.Max(from, c)];
                    KilledText?.Invoke(_kill);
                    _mark = null;
                    host.Select(c, c);
                }
                return true;
            case "C-y":
                if (_kill.Length > 0)
                {
                    host.Replace(c, 0, _kill);
                    host.Caret = c + _kill.Length;
                }
                return true;
            case "C-t":
                if (c > 0 && c < t.Length && t[c] != '\n' && t[c - 1] != '\n')
                {
                    host.BeginChange();
                    host.Replace(c - 1, 2, $"{t[c]}{t[c - 1]}");
                    host.EndChange();
                    host.Caret = c + 1;
                }
                return true;
            case "C-o":
                host.Replace(c, 0, "\n");
                host.Caret = c;
                return true;
            case "C-/" or "C-_":
                host.Undo();
                return true;
            case "C-s":
                host.Ex("find");
                return true;
            case "C-r":
                host.Ex("find-backward");
                return true;
            case "C-l":
                return true;
            default:
                return false;
        }
    }

    private bool Move(int to)
    {
        host.Caret = to;
        Region();
        return true;
    }

    /// <summary>With the mark set, the region runs from it to the cursor.</summary>
    private void Region()
    {
        if (_mark is int m)
        {
            host.Select(Math.Min(m, host.Caret), Math.Max(m, host.Caret));
        }
    }

    private void Kill(int from, int to, bool afterKill, bool append)
    {
        if (to <= from)
        {
            return;
        }
        string text = host.Text[from..to];
        _kill = afterKill ? (append ? _kill + text : text + _kill) : text;
        host.Replace(from, to - from, "");
        host.Caret = from;
        _lastWasKill = true;
        KilledText?.Invoke(_kill);
    }

    private static int LineStart(string t, int c) => c == 0 ? 0 : t.LastIndexOf('\n', c - 1) + 1;

    private static int LineEnd(string t, int c)
    {
        int n = t.IndexOf('\n', c);
        return n < 0 ? t.Length : n;
    }

    private static int LineBelow(string t, int c, int lines)
    {
        int column = c - LineStart(t, c);
        int pos = c;
        for (int i = 0; i < Math.Abs(lines); i++)
        {
            if (lines > 0)
            {
                int end = LineEnd(t, pos);
                if (end >= t.Length)
                {
                    break;
                }
                pos = end + 1;
            }
            else
            {
                int start = LineStart(t, pos);
                if (start == 0)
                {
                    break;
                }
                pos = LineStart(t, start - 1);
            }
        }
        int lineStart = LineStart(t, pos);
        return Math.Min(lineStart + column, LineEnd(t, lineStart));
    }

    private static bool IsWord(char ch) => char.IsLetterOrDigit(ch) || ch is '_' or '\'' || char.IsSurrogate(ch);

    private static int WordEnd(string t, int c)
    {
        while (c < t.Length && !IsWord(t[c]))
        {
            c++;
        }
        while (c < t.Length && IsWord(t[c]))
        {
            c++;
        }
        return c;
    }

    private static int WordStart(string t, int c)
    {
        while (c > 0 && !IsWord(t[c - 1]))
        {
            c--;
        }
        while (c > 0 && IsWord(t[c - 1]))
        {
            c--;
        }
        return c;
    }
}

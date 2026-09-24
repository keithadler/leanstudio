using LeanStudio.Core.Editing;

namespace LeanStudio.Tests;

/// <summary>The Vim engine, over an in-memory buffer: what a Vim user types, and what the text becomes.</summary>
public sealed class VimTests
{
    /// <summary>A buffer with undo by snapshots, and the text as the editor would type it in insert mode.</summary>
    private sealed class Buffer : IVimHost
    {
        private readonly Stack<(string, int)> _undo = new(), _redo = new();
        private int _depth;

        public Buffer(string text, int caret = 0)
        {
            Text = text;
            Caret = caret;
        }

        public string Text { get; private set; }
        public int Caret { get; set; }
        public (int Start, int End) Selection { get; private set; }
        public List<string> Ex { get; } = [];

        public void Replace(int offset, int length, string text)
        {
            if (_depth == 0)
            {
                _undo.Push((Text, Caret));
                _redo.Clear();
            }
            Text = Text[..offset] + text + Text[(offset + length)..];
        }

        public void Select(int start, int end) => Selection = (start, end);

        public void BeginChange()
        {
            if (_depth++ == 0)
            {
                _undo.Push((Text, Caret));
                _redo.Clear();
            }
        }

        public void EndChange()
        {
            _depth = Math.Max(0, _depth - 1);
            if (_depth == 0 && _undo.Count > 0 && _undo.Peek().Item1 == Text)
            {
                _undo.Pop(); // nothing changed
            }
        }

        public void Undo()
        {
            if (_undo.TryPop(out var s))
            {
                _redo.Push((Text, Caret));
                (Text, Caret) = s;
            }
        }

        public void Redo()
        {
            if (_redo.TryPop(out var s))
            {
                _undo.Push((Text, Caret));
                (Text, Caret) = s;
            }
        }

        bool IVimHost.Ex(string command)
        {
            Ex.Add(command);
            return true;
        }

        /// <summary>What the editor does with a typed character in insert mode.</summary>
        public void Type(string s)
        {
            Text = Text[..Caret] + s + Text[Caret..];
            Caret += s.Length;
        }
    }

    private static (Buffer B, VimEngine V) Vim(string text, int caret = 0)
    {
        var b = new Buffer(text, caret);
        return (b, new VimEngine(b));
    }

    /// <summary>Keys in normal mode; in insert mode, text between <c>i…&lt;Esc&gt;</c> is typed by the "editor".</summary>
    private static void Press(Buffer b, VimEngine v, string keys)
    {
        for (int i = 0; i < keys.Length; i++)
        {
            string key = keys[i].ToString();
            int close = keys[i] == '<' ? keys.IndexOf('>', i) : -1;
            if (close > i + 1 && keys[(i + 1)..close].All(ch => char.IsAsciiLetter(ch) || ch == '-'))
            {
                key = keys[i..(close + 1)]; // a named key, like <Esc> or <C-r>
                i = close;
            }
            if (!v.Key(key))
            {
                b.Type(key == "<CR>" ? "\n" : key);
            }
        }
    }

    [Fact]
    public void MovesByCharactersWordsAndLines()
    {
        var (b, v) = Vim("theorem foo (n : Nat) : n = n := by\n  rfl\n");
        Press(b, v, "w");
        Assert.Equal(8, b.Caret);
        Press(b, v, "w");
        Assert.Equal(12, b.Caret); // (
        Press(b, v, "b");
        Assert.Equal(8, b.Caret);
        Press(b, v, "e");
        Assert.Equal(10, b.Caret);
        Press(b, v, "$");
        Assert.Equal(34, b.Caret); // the y of `by`, the line's last character
        Press(b, v, "j");
        Assert.Equal(b.Text.IndexOf("rfl", StringComparison.Ordinal) + 2, b.Caret); // clamped to the line
        Press(b, v, "^");
        Assert.Equal(b.Text.IndexOf("rfl", StringComparison.Ordinal), b.Caret);
        Press(b, v, "gg");
        Assert.Equal(0, b.Caret);
        Press(b, v, "G");
        Assert.Equal(b.Text.Length, b.Caret); // the text ends with a line break: its last line is the empty one after it
    }

    [Fact]
    public void FindsCharactersAndMatchingBrackets()
    {
        var (b, v) = Vim("f (g ⟨a, b⟩) = c");
        Press(b, v, "f⟨");
        Assert.Equal(5, b.Caret);
        Press(b, v, "%");
        Assert.Equal(10, b.Caret);
        Press(b, v, "0t=");
        Assert.Equal(12, b.Caret);
        Press(b, v, "0f,;");
        Assert.Equal(7, b.Caret); // ; finds no second comma and stays
    }

    [Fact]
    public void DeletesChangesAndYanksWithOperators()
    {
        var (b, v) = Vim("example : 1 + 1 = 2 := rfl");
        Press(b, v, "dw");
        Assert.Equal(": 1 + 1 = 2 := rfl", b.Text);
        Press(b, v, "$d0");
        Assert.Equal("l", b.Text);

        (b, v) = Vim("have h : a = b := foo");
        Press(b, v, "wcwkey<Esc>");
        Assert.Equal("have key : a = b := foo", b.Text);
        Assert.Equal(VimMode.Normal, v.Mode);

        (b, v) = Vim("one\ntwo\nthree\n");
        Press(b, v, "jdd");
        Assert.Equal("one\nthree\n", b.Text);
        Press(b, v, "p");
        Assert.Equal("one\nthree\ntwo\n", b.Text);
        Press(b, v, "ggyyP");
        Assert.Equal("one\none\nthree\ntwo\n", b.Text);
        Press(b, v, "2dd");
        Assert.Equal("three\ntwo\n", b.Text);
        Press(b, v, "u");
        Assert.Equal("one\none\nthree\ntwo\n", b.Text);
        Press(b, v, "<C-r>");
        Assert.Equal("three\ntwo\n", b.Text);
    }

    [Fact]
    public void ActsOnTextObjects()
    {
        var (b, v) = Vim("exact ⟨foo x, bar y⟩", 9);
        Press(b, v, "ci⟨hp, hq<Esc>");
        Assert.Equal("exact ⟨hp, hq⟩", b.Text);

        (b, v) = Vim("simp [add_comm (x + y), mul_one]", 17);
        Press(b, v, "da(");
        Assert.Equal("simp [add_comm , mul_one]", b.Text);

        (b, v) = Vim("#eval \"hello world\"", 9);
        Press(b, v, "di\"");
        Assert.Equal("#eval \"\"", b.Text);

        (b, v) = Vim("rw [foo_bar] at h", 6);
        Press(b, v, "diw");
        Assert.Equal("rw [] at h", b.Text);
        Press(b, v, "0daw");
        Assert.Equal("[] at h", b.Text);

        (b, v) = Vim("    exact h", 1);
        Press(b, v, "ciw  <Esc>");
        Assert.Equal("  exact h", b.Text); // on blanks, iw is the blanks
    }

    [Fact]
    public void InsertsOpensLinesAndRepeatsWithDot()
    {
        var (b, v) = Vim("theorem t : True := by\n  trivial");
        Press(b, v, "o  sorry<Esc>");
        Assert.Equal("theorem t : True := by\n  sorry\n  trivial", b.Text); // indented like the line above
        Press(b, v, "j");
        Press(b, v, "A -- done<Esc>");
        Assert.Equal("theorem t : True := by\n  sorry\n  trivial -- done", b.Text);
        Press(b, v, "k.");
        Assert.Equal("theorem t : True := by\n  sorry -- done\n  trivial -- done", b.Text);

        (b, v) = Vim("a b c d");
        Press(b, v, "x..");
        Assert.Equal(" c d", b.Text); // three characters gone, as in Vim
    }

    [Fact]
    public void JoinsReplacesTogglesAndIndents()
    {
        var (b, v) = Vim("intro x\n    exact x");
        Press(b, v, "J");
        Assert.Equal("intro x exact x", b.Text);
        Press(b, v, "0rI");
        Assert.Equal("Intro x exact x", b.Text);
        Press(b, v, "~");
        Assert.Equal("intro x exact x", b.Text);

        (b, v) = Vim("rfl\nsimp");
        Press(b, v, ">>j>>");
        Assert.Equal("  rfl\n  simp", b.Text);
        Press(b, v, "<<");
        Assert.Equal("  rfl\nsimp", b.Text);
    }

    [Fact]
    public void SelectsVisuallyAndSearches()
    {
        var (b, v) = Vim("one two three");
        Press(b, v, "wve");
        Assert.Equal(VimMode.Visual, v.Mode);
        Assert.Equal((4, 7), b.Selection);
        Press(b, v, "y");
        Assert.Equal(("two", false), v.Register);
        Assert.Equal(VimMode.Normal, v.Mode);

        (b, v) = Vim("a\nb\nc\nd");
        Press(b, v, "jVjd");
        Assert.Equal("a\nd", b.Text);

        (b, v) = Vim("foo bar foo baz foo");
        Press(b, v, "/foo<CR>");
        Assert.Equal(8, b.Caret);
        Press(b, v, "n");
        Assert.Equal(16, b.Caret);
        Press(b, v, "n");
        Assert.Equal(0, b.Caret); // wraps
        Press(b, v, "N");
        Assert.Equal(16, b.Caret);
        Press(b, v, "0w*");
        Assert.Equal(4, b.Caret); // only one `bar`: stays
    }

    [Fact]
    public void RunsExCommandsAndCounts()
    {
        var (b, v) = Vim("1\n2\n3\n4\n5");
        Press(b, v, ":4<CR>");
        Assert.Equal(b.Text.IndexOf('4', StringComparison.Ordinal), b.Caret);
        Press(b, v, ":w<CR>");
        Assert.Equal(["w"], b.Ex);
        Press(b, v, "gg3j");
        Assert.Equal(b.Text.IndexOf('4', StringComparison.Ordinal), b.Caret);
        Press(b, v, "2G");
        Assert.Equal(b.Text.IndexOf('2', StringComparison.Ordinal), b.Caret);
        Assert.Equal("", v.Status);
        Press(b, v, "d");
        Assert.Equal("d", v.Status);
        Press(b, v, "<Esc>i");
        Assert.Equal("-- INSERT --", v.Status);
    }
}

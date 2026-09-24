using LeanStudio.Core.Editing;

namespace LeanStudio.Tests;

/// <summary>Keyboard shortcuts of one's own, and Emacs's keys.</summary>
public sealed class KeymapTests
{
    private sealed class Buffer(string text, int caret = 0) : IVimHost
    {
        private readonly Stack<string> _undo = new();

        public string Text { get; private set; } = text;
        public int Caret { get; set; } = caret;
        public (int Start, int End) Selection { get; private set; }
        public List<string> Ex { get; } = [];
        private int _depth;

        public void Replace(int offset, int length, string text)
        {
            if (_depth == 0)
            {
                _undo.Push(Text);
            }
            Text = Text[..offset] + text + Text[(offset + length)..];
        }

        public void Select(int start, int end) => Selection = (start, end);
        public void BeginChange() { if (_depth++ == 0) { _undo.Push(Text); } }
        public void EndChange() => _depth--;
        public void Undo() { if (_undo.TryPop(out string? t)) { Text = t; } }
        public void Redo() { }
        bool IVimHost.Ex(string command) { Ex.Add(command); return true; }
    }

    private static void Keys(EmacsEngine e, params string[] keys)
    {
        foreach (string k in keys)
        {
            Assert.True(e.Key(k), k);
        }
    }

    [Fact]
    public void MovesLikeEmacs()
    {
        var b = new Buffer("theorem foo_bar : True :=\n  trivial\n");
        var e = new EmacsEngine(b);
        Keys(e, "M-f");
        Assert.Equal(7, b.Caret);
        Keys(e, "M-f");
        Assert.Equal(15, b.Caret); // foo_bar is one word
        Keys(e, "M-b", "C-e");
        Assert.Equal(25, b.Caret);
        Keys(e, "C-n");
        Assert.Equal(35, b.Caret); // the end of the shorter next line
        Keys(e, "C-a", "C-f", "C-f", "C-p");
        Assert.Equal(2, b.Caret);
        Keys(e, "M->");
        Assert.Equal(b.Text.Length, b.Caret);
        Keys(e, "M-<");
        Assert.Equal(0, b.Caret);
        Assert.False(e.Key("C-z")); // not Emacs's: left to the editor
    }

    [Fact]
    public void KillsAndYanks()
    {
        var b = new Buffer("line one\nline two\nrest");
        var e = new EmacsEngine(b);
        string? clipboard = null;
        e.KilledText += t => clipboard = t;
        Keys(e, "C-k");
        Assert.Equal("\nline two\nrest", b.Text);
        Keys(e, "C-k", "C-k"); // kills in a row add up: the newline, then the next line
        Assert.Equal("\nrest", b.Text);
        Assert.Equal("line one\nline two", e.Killed);
        Assert.Equal(e.Killed, clipboard);
        Keys(e, "M->", "C-y");
        Assert.Equal("\nrestline one\nline two", b.Text);

        b = new Buffer("exact foo_bar baz", 6);
        e = new EmacsEngine(b);
        Keys(e, "M-d");
        Assert.Equal("exact  baz", b.Text);
        Keys(e, "C-e", "M-<backspace>");
        Assert.Equal("exact  ", b.Text);
        Keys(e, "C-a", "C-d");
        Assert.Equal("xact  ", b.Text);
        Keys(e, "C-f", "C-t");
        Assert.Equal("axct  ", b.Text);
        Keys(e, "C-/");
        Assert.Equal("xact  ", b.Text);
    }

    [Fact]
    public void TheRegionFollowsTheMark()
    {
        var b = new Buffer("simp [foo, bar]");
        var e = new EmacsEngine(b);
        Keys(e, "M-f", "C-f", "C-f", "C-SPC");
        Assert.Equal("Mark set", e.Status);
        Keys(e, "M-f");
        Assert.Equal((6, 9), b.Selection); // foo
        Keys(e, "M-w");
        Assert.Equal("foo", e.Killed);
        Assert.Equal("", e.Status);
        Keys(e, "C-SPC", "M-f", "C-w");
        Assert.Equal("simp [foo]", b.Text);
        Assert.Equal(", bar", e.Killed);
        Keys(e, "C-x");
        Assert.Equal("C-x-", e.Status);
        Keys(e, "h");
        Assert.Equal((0, b.Text.Length), b.Selection);
        Keys(e, "C-g", "C-x", "C-s", "C-s", "C-x", "C-f");
        Assert.Equal(["w", "find", "open"], b.Ex);
    }

    [Fact]
    public void ReadsKeybindings()
    {
        Assert.Equal(new KeyChord(false, true, false, true, "P"), KeyChord.Parse("Cmd+Alt+P", mac: true));
        Assert.Equal(new KeyChord(true, true, false, false, "P"), KeyChord.Parse("cmd+option+p", mac: false));
        Assert.Equal(new KeyChord(true, false, true, false, "OemPeriod"), KeyChord.Parse("Ctrl+Shift+.", mac: true));
        Assert.Equal(new KeyChord(false, false, false, true, "OemPlus"), KeyChord.Parse("Cmd++", mac: true));
        Assert.Equal(new KeyChord(false, false, false, false, "F5"), KeyChord.Parse("F5", mac: true));
        Assert.Equal(new KeyChord(true, false, false, false, "D1"), KeyChord.Parse("Ctrl+1", mac: true));
        Assert.Null(KeyChord.Parse("Hyper+P", mac: true));
        Assert.Null(KeyChord.Parse("Cmd+", mac: true));

        var (bindings, problems) = KeyBindingsFile.Parse("""
            // mine
            [
              { "key": "Cmd+Alt+L", "command": "Lean: Lint File (the linters CI runs)" },
              { "key": "Hyper+X", "command": "nope" },
              { "command": "no key" },
            ]
            """);
        Assert.Equal([new KeyBinding("Cmd+Alt+L", "Lean: Lint File (the linters CI runs)")], bindings);
        Assert.Equal(2, problems.Count);
        Assert.Contains("Hyper+X", problems[0], StringComparison.Ordinal);
        Assert.Single(KeyBindingsFile.Parse("not json").Problems);

        string template = KeyBindingsFile.Template([("Lean: Prove It", "⌘⌥P"), ("View: Split Editor", "")]);
        Assert.Contains("//   Lean: Prove It    (⌘⌥P)", template, StringComparison.Ordinal);
        Assert.Empty(KeyBindingsFile.Parse(template).Bindings);
        Assert.Empty(KeyBindingsFile.Parse(template).Problems);
    }
}

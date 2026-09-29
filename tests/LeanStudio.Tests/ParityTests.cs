using LeanStudio.Core.Ai;
using LeanStudio.Core.Editing;

namespace LeanStudio.Tests;

/// <summary>What an editor is expected to do (sticky scroll, breadcrumbs, merge conflicts…), on the Core side.</summary>
public sealed class ParityTests
{
    private const string Nested = """
        import Mathlib

        /-! A module doc: `namespace Fake` in here is not code. -/

        namespace Nat.Prime
        -- namespace Commented
        section Helpers

        variable (n : Nat)

        /-- A doc comment. -/
        @[simp]
        theorem two_le (h : n.Prime) : 2 ≤ n := by
          have := h
          omega

        end Helpers

        noncomputable section

        instance : Inhabited Nat := ⟨0⟩

        end
        end Nat.Prime

        example : True := trivial
        """;

    [Fact]
    public void FindsTheScopesAroundALine()
    {
        string[] lines = Nested.Split('\n');
        int omega = Array.FindIndex(lines, l => l.Trim() == "omega");
        IReadOnlyList<LeanScope> at = LeanScopes.At(Nested, omega);
        Assert.Equal(["Nat.Prime", "Helpers", "two_le"], at.Select(s => s.Name));
        Assert.Equal([ScopeKind.Namespace, ScopeKind.Section, ScopeKind.Declaration], at.Select(s => s.Kind));
        Assert.Equal(Array.FindIndex(lines, l => l.StartsWith("theorem", StringComparison.Ordinal)), at[2].Line);

        // Between declarations: only the blocks. After `end Helpers`: the anonymous section, then the instance.
        Assert.Equal(["Nat.Prime", "Helpers"], LeanScopes.At(Nested, Array.FindIndex(lines, l => l.StartsWith("variable", StringComparison.Ordinal))).Select(s => s.Name));
        IReadOnlyList<LeanScope> inst = LeanScopes.At(Nested, Array.FindIndex(lines, l => l.StartsWith("instance", StringComparison.Ordinal)));
        Assert.Equal(["Nat.Prime", "section", "instance : Inhabited Nat"], inst.Select(s => s.Name));
        // Past both ends, at the example.
        Assert.Equal(["example : True"], LeanScopes.At(Nested, lines.Length - 1).Select(s => s.Name));
        // The line that closes a namespace is still in it.
        Assert.Equal(["Nat.Prime"], LeanScopes.At(Nested, Array.FindIndex(lines, l => l == "end Nat.Prime")).Select(s => s.Name));
    }

    [Fact]
    public void PinsOnlyTheScopesScrolledOutOfView()
    {
        string[] lines = Nested.Split('\n');
        int have = Array.FindIndex(lines, l => l.Trim() == "have := h");
        int theorem = Array.FindIndex(lines, l => l.StartsWith("theorem", StringComparison.Ordinal));
        Assert.Equal(["Nat.Prime", "Helpers", "two_le"], LeanScopes.Sticky(Nested, have).Select(s => s.Name));
        // With the theorem's first line itself at the top, it is not pinned.
        Assert.Equal(["Nat.Prime", "Helpers"], LeanScopes.Sticky(Nested, theorem).Select(s => s.Name));
        Assert.Equal(["Helpers", "two_le"], LeanScopes.Sticky(Nested, have, max: 2).Select(s => s.Name));
        Assert.Empty(LeanScopes.Sticky(Nested, 0));
    }

    private const string Conflicted = """
        theorem a : True := trivial
        <<<<<<< HEAD
        theorem b : 1 = 1 := rfl
        =======
        theorem b : 1 = 1 := by decide
        theorem c : 2 = 2 := rfl
        >>>>>>> feature/decide
        theorem d : True := trivial
        <<<<<<< HEAD
        def x := 1
        ||||||| merged common ancestors
        def x := 0
        =======
        def x := 2
        >>>>>>> other
        """;

    [Fact]
    public void FindsAndSettlesMergeConflicts()
    {
        IReadOnlyList<ConflictBlock> blocks = MergeConflicts.Find(Conflicted);
        Assert.Equal(2, blocks.Count);
        Assert.Equal(new ConflictBlock(1, -1, 3, 6, "HEAD", "feature/decide"), blocks[0]);
        Assert.Equal((2, 3), blocks[0].Mine);
        Assert.Equal((4, 6), blocks[0].Theirs);
        Assert.Equal(10, blocks[1].Base);
        Assert.Equal((9, 10), blocks[1].Mine);

        string mine = MergeConflicts.Resolve(Conflicted, blocks[0], ConflictChoice.Mine);
        Assert.StartsWith("theorem a : True := trivial\ntheorem b : 1 = 1 := rfl\ntheorem d", mine, StringComparison.Ordinal);
        string theirs = MergeConflicts.Resolve(Conflicted, blocks[0], ConflictChoice.Theirs);
        Assert.StartsWith("theorem a : True := trivial\ntheorem b : 1 = 1 := by decide\ntheorem c : 2 = 2 := rfl\ntheorem d", theirs, StringComparison.Ordinal);
        // The ancestor's side goes with the markers.
        string both = MergeConflicts.Resolve(Conflicted, blocks[1], ConflictChoice.Both);
        Assert.EndsWith("theorem d : True := trivial\ndef x := 1\ndef x := 2", both, StringComparison.Ordinal);
        Assert.Single(MergeConflicts.Find(both));

        // Not conflicts: a lone marker, eight of a character, a separator without a start.
        Assert.Empty(MergeConflicts.Find("<<<<<<< HEAD\nx\n"));
        Assert.Empty(MergeConflicts.Find("<<<<<<<< not\n=======\n>>>>>>> x\n"));
        Assert.Empty(MergeConflicts.Find("=======\n>>>>>>> x\n"));
    }

    [Fact]
    public void ReadsMarkdownIntoBlocks()
    {
        const string md = """
            # Lean *Studio*

            A **bold** claim, `code`, ~~gone~~, and [a link](https://lean-lang.org).

            ```lean
            theorem t : True := trivial
            ```

            > quoted

            - [x] done
            - [ ] to do

            3. third
            4. fourth

            | Name | Heartbeats |
            |---|---:|
            | `slow` | 14,681 |

            ![the panel](docs/images/timing.png)

            ---
            """;
        IReadOnlyList<MdBlock> blocks = MarkdownModel.Parse(md);
        Assert.Equal([typeof(MdHeading), typeof(MdParagraph), typeof(MdCode), typeof(MdQuote), typeof(MdList), typeof(MdList), typeof(MdTable), typeof(MdImage), typeof(MdRule)],
            blocks.Select(b => b.GetType()));
        var h = (MdHeading)blocks[0];
        Assert.Equal(1, h.Level);
        Assert.Equal([new MdSpan("Lean "), new MdSpan("Studio", Italic: true)], h.Spans);
        var p = (MdParagraph)blocks[1];
        Assert.Contains(new MdSpan("bold", Bold: true), p.Spans);
        Assert.Contains(new MdSpan("code", Code: true), p.Spans);
        Assert.Contains(new MdSpan("gone", Strike: true), p.Spans);
        Assert.Contains(new MdSpan("a link", Link: "https://lean-lang.org"), p.Spans);
        Assert.Equal(new MdCode("lean", "theorem t : True := trivial") { Line = 4 }, blocks[2]);
        var tasks = (MdList)blocks[4];
        Assert.Equal([true, false], tasks.Items.Select(i => i.Done));
        var ordered = (MdList)blocks[5];
        Assert.True(ordered.Ordered);
        Assert.Equal(3, ordered.Start);
        var table = (MdTable)blocks[6];
        Assert.Equal(["Name", "Heartbeats"], table.Header.Select(c => string.Concat(c.Select(s => s.Text))));
        Assert.Equal(new MdSpan("slow", Code: true), table.Rows[0][0][0]);
        Assert.Equal(new MdImage("the panel", "docs/images/timing.png") { Line = 20 }, blocks[7]);
    }

    [Fact]
    public void DiffsTwoTextsSideBySide()
    {
        IReadOnlyList<DiffRow> rows = TextDiff.SideBySide("a\nb\nc\nd\ne\n", "a\nB\nc\ne\nf\n");
        Assert.Equal(
        [
            new DiffRow(DiffKind.Same, 0, 0),
            new DiffRow(DiffKind.Changed, 1, 1),
            new DiffRow(DiffKind.Same, 2, 2),
            new DiffRow(DiffKind.Removed, 3, null),
            new DiffRow(DiffKind.Same, 4, 3),
            new DiffRow(DiffKind.Added, null, 4),
        ], rows);
        Assert.Equal((2, 2), TextDiff.Count(rows));
        Assert.All(TextDiff.SideBySide("same\r\ntext", "same\ntext"), r => Assert.Equal(DiffKind.Same, r.Kind));
        Assert.Equal([new DiffRow(DiffKind.Added, null, 0)], TextDiff.SideBySide("", "x"));
    }

    [Fact]
    public void DiffIsAShortestEditScriptOnRandomTexts()
    {
        var r = new Random(7);
        for (int t = 0; t < 300; t++)
        {
            string[] a = Enumerable.Range(0, r.Next(0, 40)).Select(_ => "l" + r.Next(6)).ToArray();
            string[] b = Enumerable.Range(0, r.Next(0, 40)).Select(_ => "l" + r.Next(6)).ToArray();
            var ops = TextDiff.Edits(a, b);
            // It turns a into b...
            Assert.Equal(a, ops.Where(o => o.Op != '+').Select(o => a[o.I]));
            Assert.Equal(b, ops.Where(o => o.Op != '-').Select(o => b[o.J]));
            Assert.All(ops.Where(o => o.Op == '='), o => Assert.Equal(a[o.I], b[o.J]));
            // ...keeping as many lines as the longest common subsequence has.
            int[,] lcs = new int[a.Length + 1, b.Length + 1];
            for (int i = a.Length - 1; i >= 0; i--)
            {
                for (int j = b.Length - 1; j >= 0; j--)
                {
                    lcs[i, j] = a[i] == b[j] ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
                }
            }
            Assert.Equal(lcs[0, 0], ops.Count(o => o.Op == '='));
        }
    }

    [Fact]
    public void CleansModelAnswersIntoSuggestions()
    {
        const string before = "theorem t (n : Nat) : n + 0 = n := by\n  sim";
        const string after = "\n\ntheorem u : True := trivial\n";
        // An echo of the line's start is dropped, and so is a rewrite of what follows.
        Assert.Equal("p", InlineCompletion.Clean("simp", before, after));
        Assert.Equal("p", InlineCompletion.Clean("<think>it is simp</think>```lean\nsimp\n```", before, after));
        Assert.Equal("p", InlineCompletion.Clean("simp\n\ntheorem u : True := trivial", before, after));
        Assert.Equal("", InlineCompletion.Clean("   \n  ", before, after));
        Assert.Equal(InlineCompletion.MaxLines, InlineCompletion.Clean(string.Join("\n", Enumerable.Range(0, 20).Select(i => $"  have h{i} := rfl")), "by\n", "").Split('\n').Length);
        // On an empty line, a suggestion starting with a line break starts right there.
        Assert.Equal("  omega", InlineCompletion.Clean("\n  omega", "by\n", ""));
        Assert.True(InlineCompletion.Acceptable("a\nb\nc", 2, "b", [new Lsp.Diagnostic(new Lsp.Range(new Lsp.Position(2, 0), new Lsp.Position(2, 1)), Lsp.DiagnosticSeverity.Error, "later")]));
        Assert.False(InlineCompletion.Acceptable("a\nb\nc", 2, "b", [new Lsp.Diagnostic(new Lsp.Range(new Lsp.Position(1, 0), new Lsp.Position(1, 1)), Lsp.DiagnosticSeverity.Error, "here")]));
    }
}

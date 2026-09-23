using LeanStudio.Core.Editing;
using LeanStudio.Lsp;

namespace LeanStudio.Tests;

public sealed class EditingTests
{
    [Theory]
    [InlineData("theorem t : True := by", "  ")]
    [InlineData("  have h : p := by", "    ")]
    [InlineData("  | succ n ih =>", "    ")]
    [InlineData("structure S where", "  ")]
    [InlineData("  exact h", "  ")]
    [InlineData("  simp -- done := by", "  ")]
    [InlineData("", "")]
    public void IndentsAfterBlockOpeners(string previous, string expected) => Assert.Equal(expected, LeanText.IndentAfter(previous));

    [Fact]
    public void MatchesBracketsButNotInCommentsOrStrings()
    {
        string text = "f ⟨a, (b)⟩ -- (\n\"(\" [x]";
        Assert.Equal(text.IndexOf('⟩'), LeanText.MatchingBracket(text, text.IndexOf('⟨')));
        Assert.Equal(text.IndexOf('⟨'), LeanText.MatchingBracket(text, text.IndexOf('⟩')));
        Assert.Equal(text.IndexOf("b)", StringComparison.Ordinal) + 1, LeanText.MatchingBracket(text, text.IndexOf("(b", StringComparison.Ordinal)));
        Assert.Equal(-1, LeanText.MatchingBracket(text, text.LastIndexOf("-- (", StringComparison.Ordinal) + 3)); // inside a comment
        Assert.Equal(text.IndexOf(']'), LeanText.MatchingBracket(text, text.IndexOf('[')));
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData(' ', true)]
    [InlineData(')', true)]
    [InlineData('x', false)]
    public void AutoClosesOnlyBeforeSpaceOrCloser(char? next, bool expected) => Assert.Equal(expected, LeanText.ShouldAutoClose(next));

    [Fact]
    public void FuzzyPrefersWordStartsAndTheFileName()
    {
        Assert.NotNull(Fuzzy.Score("gtd", "Go: Go to Definition"));
        Assert.Null(Fuzzy.Score("xyz", "Go: Go to Definition"));
        var ranked = Fuzzy.Filter(["Proofs/Basic.lean", "README.md", "Proofs.lean", "lakefile.toml"], "basic", s => s).ToList();
        Assert.Equal("Proofs/Basic.lean", ranked[0]);
    }

    [Fact]
    public void FindsTextInProjectFilesAndSkipsBuildOutput()
    {
        var hits = ProjectSearch.Search(Lean.Sample("Proofs"), "theorem", ct: TestContext.Current.CancellationToken);
        Assert.Contains(hits, h => h.Path.EndsWith("Basic.lean", StringComparison.Ordinal) && h.LineText.Contains("and_swap", StringComparison.Ordinal));
        Assert.DoesNotContain(hits, h => h.Path.Contains(".lake", StringComparison.Ordinal));
        var regex = ProjectSearch.Search(Lean.Sample("Proofs"), @"theorem \w+_swap", regex: true, ct: TestContext.Current.CancellationToken);
        Assert.Single(regex);
    }

    [Fact]
    public void AppliesEditsAgainstTheOriginalPositions()
    {
        string text = "theorem foo : foo = foo\nexample := foo";
        var edits = new[]
        {
            new TextEdit(new Lsp.Range(new Position(0, 8), new Position(0, 11)), "bar"),
            new TextEdit(new Lsp.Range(new Position(1, 11), new Position(1, 14)), "bar"),
        };
        Assert.Equal("theorem bar : foo = foo\nexample := bar", WorkspaceEdit.Apply(text, edits));
    }

    [Fact]
    public void SaysHowToTypeASymbol()
    {
        Assert.Contains("|-", Abbreviations.NamesFor("⊢"));
        Assert.Equal("N", Abbreviations.NamesFor("ℕ")[0]);
        Assert.Empty(Abbreviations.NamesFor("x"));
    }
}

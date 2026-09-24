using LeanStudio.Core.Editing;

namespace LeanStudio.Tests;

/// <summary>Math in docstrings as readable text, and Unicode abbreviations of one's own.</summary>
public sealed class TextTests
{
    [Theory]
    [InlineData("The sum $\\sum_{i < n} x_i^2 \\le C$ holds.", "The sum ∑_(i < n) xᵢ² ≤ C holds.")]
    [InlineData("A map $f : \\mathbb{R} \\to \\mathbb{C}$ is nice.", "A map f : ℝ → ℂ is nice.")]
    [InlineData("$\\frac{1}{2} + \\sqrt{x}$", "1/2 + √x")]
    [InlineData("$$\\forall \\epsilon > 0, \\exists \\delta$$", "∀ ε > 0, ∃ δ")]
    [InlineData("See `x $ y` and $a_n$", "See `x $ y` and aₙ")] // code in backticks is left alone
    [InlineData("costs \\$5, no math", "costs \\$5, no math")]
    [InlineData("$\\operatorname{rank} A^{-1}$", "rank A⁻¹")]
    [InlineData("$x^{q}$", "x^q")] // no Unicode superscript q: kept readable
    public void TurnsDocstringMathIntoText(string docstring, string shown) => Assert.Equal(shown, LatexText.ToUnicode(docstring));

    [Fact]
    public void AddsAbbreviationsOfOnesOwn()
    {
        try
        {
            var (custom, problems) = Abbreviations.ParseCustom("""
                // mine
                { "\\\\ok": "✅", "zeta5": "ζ(5)", "bad name": "x", "empty": "", }
                """);
            Assert.Equal(new Dictionary<string, string> { ["ok"] = "✅", ["zeta5"] = "ζ(5)" }, custom);
            Assert.Equal(2, problems.Count);
            Abbreviations.SetCustom(custom);
            Assert.Equal("ζ(5)", Abbreviations.Lookup("zeta5"));
            Assert.True(Abbreviations.IsPrefix("zet")); // zeta and zeta5
            Assert.True(Abbreviations.HasLongerMatch("zeta"));
            Assert.Equal("α", Abbreviations.Lookup("alpha")); // the built-in ones stay
            Abbreviations.SetCustom(new Dictionary<string, string> { ["alpha"] = "𝛼" });
            Assert.Equal("𝛼", Abbreviations.Lookup("alpha")); // one's own win
            Assert.Null(Abbreviations.Lookup("zeta5"));
        }
        finally
        {
            Abbreviations.SetCustom(new Dictionary<string, string>());
        }
    }

    [Fact]
    public void FindsTheCommentsToFold()
    {
        const string text = "/-! Module\n  doc -/\ndef a := \"/- not a comment\"\n-- /- nor this\n/-- Doc\n  /- nested\n  -/\n-/\ndef b := 1\n/- one line -/\n";
        Assert.Equal([(0, 1), (4, 7)], Core.Editing.LeanText.CommentFolds(text));
    }
}

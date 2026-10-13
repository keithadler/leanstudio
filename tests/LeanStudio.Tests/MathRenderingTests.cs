using LeanStudio.Core.Editing;

namespace LeanStudio.Tests;

/// <summary>The formula parser and the finder of math in Lean comments behind typeset docstrings in the editor.</summary>
public sealed class MathRenderingTests
{
    private static string Show(string latex) => TexMath.Flatten(TexMath.Parse(latex));

    [Theory]
    [InlineData(@"\sum_{k=1}^n k", "∑_(k=1)^(n)k")]
    [InlineData(@"\frac{n(n+1)}{2}", "(n(n+1))/(2)")]
    [InlineData(@"1^3 + 2^3 + \cdots + n^3", "1^(3)+2^(3)+⋯+n^(3)")]
    [InlineData(@"\mathbb{N}", "ℕ")]
    [InlineData(@"\sqrt{x}", "√x")]
    [InlineData(@"\left( a + b \right)^2", "(a+b)^(2)")]
    public void ReadsFormulas(string latex, string flat) => Assert.Equal(flat, Show(latex));

    [Fact]
    public void ClassifiesSymbolsForSpacing()
    {
        var row = Assert.IsType<TexRow>(TexMath.Parse("a = b + c"));
        Assert.Equal([TexKind.Var, TexKind.Rel, TexKind.Var, TexKind.Bin, TexKind.Var], row.Items.Select(n => ((TexAtom)n).Kind));
    }

    [Fact]
    public void BigOperatorsKeepTheirLimits()
    {
        var script = Assert.IsType<TexScript>(TexMath.Parse(@"\sum_{k=1}^n"));
        Assert.Equal(TexKind.BigOp, Assert.IsType<TexAtom>(script.Base).Kind);
        Assert.NotNull(script.Sub);
        Assert.NotNull(script.Sup);
    }

    [Theory]
    [InlineData(@"\frac{1}")]
    [InlineData(@"{{{{")]
    [InlineData(@"}}}")]
    [InlineData(@"x^")]
    [InlineData(@"\left( x")]
    [InlineData(@"\unknowncommand{x}")]
    [InlineData(@"^^^___")]
    [InlineData(@"\")]
    public void NeverThrowsOnBrokenInput(string latex) => Assert.NotNull(TexMath.Parse(latex));

    [Fact]
    public void UnknownCommandsAreKeptAsTyped() => Assert.Equal(@"\foo", Show(@"\foo"));

    private static string[] Spans(string source) =>
        [.. DocMarkup.Find(source).Select(s => $"{s.Kind}:{source.Substring(s.ContentStart, s.ContentLength)}")];

    [Fact]
    public void FindsMathAndEmphasisInDocstrings()
    {
        const string source = "/--\n  **Gauss**, aged $n$ and *nine*:\n  $$\\sum_{k=1}^n k$$\n-/\ndef f := 1\n";
        Assert.Equal(["Bold:Gauss", "InlineMath:n", "Italic:nine", @"DisplayMath:\sum_{k=1}^n k"], Spans(source));
    }

    [Fact]
    public void IgnoresCodeStringsAndLineComments()
    {
        const string source = "def s := \"$x$ and /- $y$ -/\" -- $z$ **w**\ndef t := 2 * 3 * 4\n/- ok $a$ -/\n";
        Assert.Equal(["InlineMath:a"], Spans(source));
    }

    [Fact]
    public void IgnoresBackticksAndPricesAndLoneStars()
    {
        const string source = "/-- `$x$` costs $5 or $6, 2 * 3 * 4, a*b, and \\$y\\$ stays text. -/\n";
        Assert.Empty(Spans(source));
    }

    [Fact]
    public void HandlesNestedCommentsAndOffsets()
    {
        const string source = "/- outer /- inner $a$ -/ $b$ -/ $c$";
        DocSpan[] spans = [.. DocMarkup.Find(source)];
        Assert.Equal(2, spans.Length);
        Assert.Equal("$a$", source.Substring(spans[0].Start, spans[0].Length));
        Assert.Equal("$b$", source.Substring(spans[1].Start, spans[1].Length));
    }

    [Fact]
    public void SpansNeverCrossALine() => Assert.Empty(Spans("/-- $a\nb$ and **x\ny** -/"));

    [Fact]
    public void NeverThrowsOnArbitraryText()
    {
        var rng = new Random(7);
        const string alphabet = "/-*$`\"\\ \na{}_^b";
        for (int n = 0; n < 2000; n++)
        {
            string s = new([.. Enumerable.Range(0, rng.Next(0, 40)).Select(_ => alphabet[rng.Next(alphabet.Length)])]);
            Assert.All(DocMarkup.Find(s), sp => Assert.True(sp.Start >= 0 && sp.End <= s.Length));
            _ = TexMath.Parse(s);
        }
    }

    [Fact]
    public void ReadsMatrices()
    {
        var m = Assert.IsType<TexMatrix>(TexMath.Parse(@"\begin{pmatrix} a & b \\ c & d \end{pmatrix}"));
        Assert.Equal(2, m.Rows.Count);
        Assert.All(m.Rows, r => Assert.Equal(2, r.Count));
        Assert.Equal(("(", ")", TexColumns.Center), (m.Open, m.Close, m.Columns));
        Assert.Equal("(a, b; c, d)", TexMath.Flatten(m));
    }

    [Theory]
    [InlineData("bmatrix", "[", "]")]
    [InlineData("Bmatrix", "{", "}")]
    [InlineData("vmatrix", "|", "|")]
    [InlineData("Vmatrix", "‖", "‖")]
    [InlineData("matrix", "", "")]
    public void PicksTheBracketsOfAMatrix(string env, string open, string close)
    {
        var m = Assert.IsType<TexMatrix>(TexMath.Parse($@"\begin{{{env}}} 1 & 2 \\ 3 & 4 \end{{{env}}}"));
        Assert.Equal((open, close), (m.Open, m.Close));
    }

    [Fact]
    public void ReadsCasesAndAlignedLines()
    {
        var cases = Assert.IsType<TexMatrix>(TexMath.Parse(@"f(x) = \begin{cases} 0 & x < 0 \\ 1 & x \ge 0 \end{cases}") is TexRow r ? r.Items[^1] : null);
        Assert.Equal(("{", "", TexColumns.Left), (cases.Open, cases.Close, cases.Columns));
        Assert.Equal(2, cases.Rows.Count);
        var aligned = Assert.IsType<TexMatrix>(TexMath.Parse(@"\begin{aligned} a &= b + c \\ &= d \end{aligned}"));
        Assert.Equal(TexColumns.Aligned, aligned.Columns);
        Assert.Equal(2, aligned.Rows.Count);
        Assert.Equal(2, aligned.Rows[1].Count);
    }

    [Fact]
    public void NestsEnvironmentsAndKeepsTheTextAroundThem()
    {
        var row = Assert.IsType<TexRow>(TexMath.Parse(@"A = \begin{pmatrix} \begin{matrix} 1 & 2 \end{matrix} & 0 \\ 0 & 1 \end{pmatrix} + B"));
        var outer = Assert.IsType<TexMatrix>(row.Items[2]);
        Assert.Equal(2, outer.Rows.Count);
        Assert.IsType<TexMatrix>(outer.Rows[0][0]);
        Assert.Equal("B", TexMath.Flatten(row.Items[^1]));
    }

    [Theory]
    [InlineData(@"\begin{pmatrix} a & b")]
    [InlineData(@"\begin{pmatrix}")]
    [InlineData(@"\begin{")]
    [InlineData(@"\begin{cases} 1 \\ \end{cases}")]
    [InlineData(@"\end{pmatrix}")]
    [InlineData(@"a & b \\ c")]
    [InlineData(@"\begin{pmatrix} \left( a & b \end{pmatrix}")]
    [InlineData(@"\begin{unknownenv} x & y \end{unknownenv}")]
    [InlineData(@"\begin{array}{cc} 1 & 2 \end{array}")]
    [InlineData(@"\begin{aligned} \begin{aligned} \begin{aligned}")]
    public void SurvivesBrokenEnvironments(string latex) => Assert.NotNull(TexMath.Parse(latex));

    [Fact]
    public void ReadsStacksAndBraces()
    {
        var under = Assert.IsType<TexStack>(TexMath.Parse(@"\underbrace{a + b}_{n}"));
        Assert.True(under.Brace);
        Assert.NotNull(under.Under);
        Assert.Null(under.Over);
        var over = Assert.IsType<TexStack>(TexMath.Parse(@"\overbrace{a+b}^{\text{two}}"));
        Assert.NotNull(over.Over);
        var set = Assert.IsType<TexStack>(TexMath.Parse(@"\overset{!}{=}"));
        Assert.False(set.Brace);
        Assert.NotNull(set.Over);
        Assert.NotNull(Assert.IsType<TexStack>(TexMath.Parse(@"\underset{x \to 0}{\lim}")).Under);
    }

    [Theory]
    [InlineData(@"\mathcal{A}B", "𝒜B")]
    [InlineData(@"\mathcal{L}", "ℒ")]
    [InlineData(@"\mathfrak{g}", "𝔤")]
    [InlineData(@"\mathfrak{R}", "ℜ")]
    [InlineData(@"\mathbf{x}", "𝐱")]
    [InlineData(@"\mathbf{12}", "𝟏𝟐")]
    public void UsesTheMathematicalAlphabets(string latex, string flat) => Assert.Equal(flat, Show(latex));

    [Fact]
    public void AlphabetsLeaveOtherCharactersAlone() => Assert.Equal("𝐀-é", MathAlphabet.Map("A-é", MathAlphabet.Bold));

    [Fact]
    public void NamesFunctionsUpright()
    {
        Assert.Equal(TexKind.Func, Assert.IsType<TexAtom>(TexMath.Parse(@"\arcsin")).Kind);
        Assert.Equal(TexKind.Func, Assert.IsType<TexAtom>(TexMath.Parse(@"\limsup")).Kind);
        Assert.Equal(TexKind.Func, Assert.IsType<TexAtom>(TexMath.Parse(@"\bmod")).Kind);
    }

    [Fact]
    public void FuzzesEnvironmentsWithoutThrowing()
    {
        var rng = new Random(23);
        string[] pieces = [@"\begin{pmatrix}", @"\end{pmatrix}", @"\begin{cases}", @"\end{cases}", "&", @"\\", "a", "{", "}", @"\left(", @"\right)", @"\underbrace{x}_{y}", @"\frac{1}{2}", "^", "_", @"\begin{", @"\end{"];
        for (int n = 0; n < 3000; n++)
        {
            string s = string.Concat(Enumerable.Range(0, rng.Next(0, 14)).Select(_ => pieces[rng.Next(pieces.Length)] + " "));
            Assert.NotNull(TexMath.Parse(s));
        }
    }
}

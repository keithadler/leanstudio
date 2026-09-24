using LeanStudio.Core.Editing;

namespace LeanStudio.Tests;

public sealed class AbbreviationTests
{
    [Theory]
    [InlineData("alpha", "α")]
    [InlineData("to", "→")]
    [InlineData("N", "ℕ")]
    [InlineData("forall", "∀")]
    [InlineData("_1", "₁")]
    [InlineData("<", "⟨")]
    [InlineData("|-", "⊢")]
    public void LooksUpCommonSymbols(string abbrev, string symbol) => Assert.Equal(symbol, Abbreviations.Lookup(abbrev));

    [Fact]
    public void WaitsWhileALongerAbbreviationIsStillPossible()
    {
        // \a could become \alpha or \and: typing 'l' keeps going.
        Assert.Null(Abbreviations.OnType("a", 'l'));
        // ...and a space settles it as α.
        Assert.Equal("α", Abbreviations.OnType("a", ' '));
    }

    [Fact]
    public void AnUnknownAbbreviationIsLeftAlone() => Assert.Null(Abbreviations.OnType("qqq", ' '));

    [Fact]
    public void CandidatesAreShortestFirst()
    {
        var c = Abbreviations.Candidates("al").Select(kv => kv.Key).ToList();
        Assert.Equal("all", c[0]);
        Assert.Contains("alpha", c);
    }

    [Fact]
    public void ThereAreAboutFourHundredThirtyAbbreviations() =>
        Assert.InRange(Abbreviations.BuiltInTable.Count, 400, 470); // the README says "about 430"
}

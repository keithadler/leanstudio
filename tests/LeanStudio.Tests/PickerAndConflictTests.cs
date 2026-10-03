using LeanStudio.Core.Editing;

namespace LeanStudio.Tests;

/// <summary>Edge cases of fuzzy matching and merge-conflict settling.</summary>
public sealed class PickerAndConflictTests
{
    [Fact]
    public void AnEmptyQueryMatchesEverythingWithZero()
    {
        Assert.Equal(0, Fuzzy.Score("", "anything"));
        Assert.Equal(0, Fuzzy.Score("", ""));
    }

    [Theory]
    [InlineData("ba", "ab")] // order matters
    [InlineData("abcd", "abc")] // longer than the candidate
    [InlineData("z", "")]
    public void RejectsWhatDoesNotMatchInOrder(string query, string candidate) => Assert.Null(Fuzzy.Score(query, candidate));

    [Fact]
    public void IgnoresCaseAndSpacesInTheQuery()
    {
        Assert.NotNull(Fuzzy.Score("BASIC", "basic.lean"));
        Assert.Equal(Fuzzy.Score("gtd", "Go to Definition"), Fuzzy.Score("g t d", "Go to Definition"));
    }

    [Fact]
    public void ScoresAnExactCaseAndAContiguousRunHigher()
    {
        Assert.True(Fuzzy.Score("a", "a") > Fuzzy.Score("A", "a"));
        Assert.True(Fuzzy.Score("abc", "abc") > Fuzzy.Score("abc", "a_b_c"));
    }

    [Fact]
    public void FilterDropsNonMatchesAndPutsShorterTextFirstOnTies()
    {
        Assert.Equal(["a", "bb", "ccc"], Fuzzy.Filter(["bb", "a", "ccc"], "", s => s));
        Assert.Empty(Fuzzy.Filter(["alpha", "beta"], "zzz", s => s));
    }

    [Fact]
    public void FindsAThreeWayConflictWithItsLabels()
    {
        const string text = "a\n<<<<<<< HEAD\nmine\n||||||| base\nold\n=======\ntheirs\n>>>>>>> feature\nz\n";
        ConflictBlock block = Assert.Single(MergeConflicts.Find(text));
        Assert.Equal(new ConflictBlock(1, 3, 5, 7, "HEAD", "feature"), block);
        Assert.Equal((2, 3), block.Mine);
        Assert.Equal((6, 7), block.Theirs);
        Assert.Equal("a\nmine\nz\n", MergeConflicts.Resolve(text, block, ConflictChoice.Mine));
        Assert.Equal("a\ntheirs\nz\n", MergeConflicts.Resolve(text, block, ConflictChoice.Theirs));
        Assert.Equal("a\nmine\ntheirs\nz\n", MergeConflicts.Resolve(text, block, ConflictChoice.Both)); // the ancestor's side is dropped
    }

    [Fact]
    public void SettlesAConflictWithAnEmptySideAndNoLabels()
    {
        const string text = "<<<<<<<\n=======\nx\n>>>>>>>\n";
        ConflictBlock block = Assert.Single(MergeConflicts.Find(text));
        Assert.Equal("", block.MineLabel);
        Assert.Equal("", block.TheirsLabel);
        Assert.Equal("", MergeConflicts.Resolve(text, block, ConflictChoice.Mine));
        Assert.Equal("x\n", MergeConflicts.Resolve(text, block, ConflictChoice.Theirs));
        Assert.Equal("x\n", MergeConflicts.Resolve(text, block, ConflictChoice.Both));
    }

    [Fact]
    public void FindsSeveralConflictsInOrderAndTrimsCarriageReturnsFromLabels()
    {
        const string text = "<<<<<<< HEAD\r\na\r\n=======\r\nb\r\n>>>>>>> topic\r\nmid\r\n<<<<<<< HEAD\r\nc\r\n=======\r\nd\r\n>>>>>>> topic\r\n";
        IReadOnlyList<ConflictBlock> blocks = MergeConflicts.Find(text);
        Assert.Equal(2, blocks.Count);
        Assert.Equal(0, blocks[0].Start);
        Assert.Equal(6, blocks[1].Start);
        Assert.All(blocks, b => Assert.Equal(("HEAD", "topic"), (b.MineLabel, b.TheirsLabel)));
    }
}

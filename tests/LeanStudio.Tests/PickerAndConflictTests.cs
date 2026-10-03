using LeanStudio.Core.Editing;
using LeanStudio.Core.Workflow;

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

/// <summary>Sorting the imports of a file's header.</summary>
public sealed class ImportOrderTests
{
    [Fact]
    public void SortsTheRunByModuleNameAndDropsRepeats()
    {
        const string text = "import Mathlib.Data.Nat.Basic\nimport Batteries\nimport Mathlib.Data.Nat\nimport Batteries\n\ntheorem t : True := trivial\n";
        Assert.Equal("import Batteries\nimport Mathlib.Data.Nat\nimport Mathlib.Data.Nat.Basic\n\ntheorem t : True := trivial\n", ImportOrder.Sort(text));
    }

    [Fact]
    public void KeepsGroupsApartAndLeavesTheBodyAlone()
    {
        const string text = "/-\nCopyright (c) me\n-/\nimport B\nimport A\n\n-- the others\nimport D\nimport C\n\nimport Z\nimport Y -- keeps its comment\n";
        Assert.Equal("/-\nCopyright (c) me\n-/\nimport A\nimport B\n\n-- the others\nimport C\nimport D\n\nimport Y -- keeps its comment\nimport Z\n", ImportOrder.Sort(text));
        const string body = "import B\ndef f := 1\nimport A\n";
        Assert.Equal(body, ImportOrder.Sort(body)); // an import after the header's end is not part of it
    }

    [Fact]
    public void KeepsModifiersLineEndingsAndAnAlreadySortedFile()
    {
        Assert.Equal("module\n\npublic import A\nimport all B\n", ImportOrder.Sort("module\n\nimport all B\npublic import A\n"));
        Assert.Equal("import A\r\nimport B\r\n", ImportOrder.Sort("import B\r\nimport A\r\n"));
        const string sorted = "import A\nimport B\n";
        Assert.Equal(sorted, ImportOrder.Sort(sorted));
        Assert.Equal("", ImportOrder.Sort(""));
    }
}

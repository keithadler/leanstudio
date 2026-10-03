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

/// <summary>The text rules of Mathlib's style linter.</summary>
public sealed class StyleCheckTests
{
    [Fact]
    public void FindsEachKindOfProblemAtItsLine()
    {
        string text = "def a := 1  \n\tdef b := 2\n-- " + new string('x', 100) + "\ndef c := 3";
        IReadOnlyList<StyleProblem> found = StyleCheck.Find(text);
        Assert.Equal([(0, "trailing-whitespace"), (1, "tab"), (2, "long-line"), (3, "final-newline")], found.Select(p => (p.Line, p.Rule)));
        Assert.Contains("103 characters", found[2].Message);
    }

    [Fact]
    public void AcceptsACleanFileAndALineOfExactlyTheLimit()
    {
        Assert.Empty(StyleCheck.Find("def a := 1\n-- " + new string('x', 97) + "\n"));
        Assert.Empty(StyleCheck.Find(""));
    }

    [Fact]
    public void ReportsCrlfAndExtraFinalNewlines()
    {
        Assert.Equal(["crlf"], StyleCheck.Find("a\r\nb\r\n").Select(p => p.Rule));
        Assert.Equal([(1, "final-newline")], StyleCheck.Find("a\n\n").Select(p => (p.Line, p.Rule)));
    }

    [Fact]
    public void FixesWhatHasOneFixAndLeavesLongLinesAlone()
    {
        Assert.Equal("a\n  b\nc\n", StyleCheck.Fix("a  \r\n\tb\t\r\nc\n\n\n"));
        Assert.Equal("a\n", StyleCheck.Fix("a"));
        string longLine = "-- " + new string('y', 120) + "\n";
        Assert.Equal(longLine, StyleCheck.Fix(longLine));
        Assert.Equal("", StyleCheck.Fix(""));
        Assert.Empty(StyleCheck.Find(StyleCheck.Fix("x \t\r\n\r\n\r\ny")));
    }
}

/// <summary>The text tools assistants get over MCP; they need no running Lean.</summary>
public sealed class TextToolTests
{
    [Fact]
    public async Task AssistantsCanSortImportsAndCheckStyle()
    {
        string dir = Directory.CreateTempSubdirectory("leanstudio-text-").FullName;
        try
        {
            string file = Path.Combine(dir, "A.lean");
            await File.WriteAllTextAsync(file, "import B\nimport A \n\ndef x := 1\t", TestContext.Current.CancellationToken);
            await using var bench = new Core.Agents.Workbench(dir);
            Mcp.McpServer server = Mcp.LeanTools.Create(bench, "test");
            async Task<string> Call(string tool, System.Text.Json.Nodes.JsonObject args)
            {
                var r = await server.HandleAsync(new System.Text.Json.Nodes.JsonObject
                {
                    ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "tools/call",
                    ["params"] = new System.Text.Json.Nodes.JsonObject { ["name"] = tool, ["arguments"] = args },
                }, TestContext.Current.CancellationToken);
                var result = r!["result"]!;
                Assert.False(result["isError"]?.GetValue<bool>() ?? false, result.ToJsonString());
                return result["content"]![0]!["text"]!.GetValue<string>();
            }

            string preview = await Call("sort_imports", new() { ["path"] = file });
            Assert.Contains("import A \nimport B", preview, StringComparison.Ordinal);
            Assert.StartsWith("import B\n", await File.ReadAllTextAsync(file, TestContext.Current.CancellationToken), StringComparison.Ordinal); // not written

            await Call("sort_imports", new() { ["path"] = file, ["apply"] = true });
            Assert.StartsWith("import A \nimport B\n", await File.ReadAllTextAsync(file, TestContext.Current.CancellationToken), StringComparison.Ordinal);
            Assert.Contains("already in order", await Call("sort_imports", new() { ["path"] = file, ["apply"] = true }), StringComparison.Ordinal);

            string report = await Call("style_check", new() { ["path"] = file });
            Assert.Contains("A.lean:1: trailing-whitespace", report, StringComparison.Ordinal);
            Assert.Contains("A.lean:4: tab", report, StringComparison.Ordinal);
            Assert.Contains("final-newline", report, StringComparison.Ordinal);
            await Call("style_check", new() { ["path"] = file, ["apply"] = true });
            Assert.Equal("import A\nimport B\n\ndef x := 1\n", await File.ReadAllTextAsync(file, TestContext.Current.CancellationToken));
            Assert.Equal("no style problems", await Call("style_check", new() { ["path"] = file }));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}

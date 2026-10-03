using LeanStudio.Core.Editing;
using LeanStudio.Core.Proofs;
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

            Assert.Equal("add_comm", await Call("suggest_name", new() { ["statement"] = "(a b : ℕ) : a + b = b + a" }));
            Assert.StartsWith("no name", await Call("suggest_name", new() { ["statement"] = "True" }), StringComparison.Ordinal);

            string proof = Path.Combine(dir, "P.lean");
            await File.WriteAllTextAsync(proof, "theorem t : P := by\n  rw [a]\n  rw [b]\n  exact h\n", TestContext.Current.CancellationToken);
            Assert.StartsWith("1 line(s) can be merged", await Call("merge_tactics", new() { ["path"] = proof }), StringComparison.Ordinal);
            await Call("merge_tactics", new() { ["path"] = proof, ["apply"] = true });
            Assert.Equal("theorem t : P := by\n  rw [a, b]\n  exact h\n", await File.ReadAllTextAsync(proof, TestContext.Current.CancellationToken));
            Assert.Equal("nothing to merge", await Call("merge_tactics", new() { ["path"] = proof }));

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
            string conventions = await Call("style_check", new() { ["path"] = file, ["mathlib"] = true });
            Assert.Contains("copyright-header", conventions, StringComparison.Ordinal);
            Assert.Contains("module-doc", conventions, StringComparison.Ordinal);
            Assert.Contains("A.lean:4: missing-doc: `x` has no doc comment.", conventions, StringComparison.Ordinal);

            await File.WriteAllTextAsync(file, "-- " + string.Join(' ', Enumerable.Repeat("word", 40)) + "\n", TestContext.Current.CancellationToken);
            await Call("style_check", new() { ["path"] = file, ["apply"] = true, ["wrap"] = true });
            Assert.All((await File.ReadAllTextAsync(file, TestContext.Current.CancellationToken)).Split('\n'), l => Assert.True(l.Length <= 100, l));
            Assert.Equal("no style problems", await Call("style_check", new() { ["path"] = file }));

            await File.WriteAllTextAsync(file, "@[deprecated (since := \"2020-01-01\")] alias old := new\n\ndef new := 1\n", TestContext.Current.CancellationToken);
            Assert.Contains("old, deprecated 2020-01-01", await Call("stale_deprecations", new() { ["path"] = file }), StringComparison.Ordinal);
            Assert.Contains("no deprecation is that old", await Call("stale_deprecations", new() { ["path"] = file, ["months"] = 1000 }), StringComparison.Ordinal);
            await Call("stale_deprecations", new() { ["path"] = file, ["apply"] = true });
            Assert.Equal("def new := 1\n", await File.ReadAllTextAsync(file, TestContext.Current.CancellationToken));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}

/// <summary>Mathlib's file conventions: the header, the module docstring, theorem names.</summary>
public sealed class MathlibConventionsTests
{
    private const string Header = "/-\nCopyright (c) 2025 Ada Lovelace. All rights reserved.\nReleased under Apache 2.0 license as described in the file LICENSE.\nAuthors: Ada Lovelace\n-/\n";

    [Fact]
    public void AcceptsAFileWithAHeaderAndAModuleDocstring() =>
        Assert.Empty(MathlibConventions.Find(Header + "import Mathlib.Data.Nat.Basic\n\n/-! # Facts -/\n\ntheorem foo_bar : True := trivial\n"));

    [Fact]
    public void ReportsAMissingHeaderAndModuleDocstring() =>
        Assert.Equal([(0, "copyright-header"), (0, "module-doc")], MathlibConventions.Find("import A\n\ndef x := 1\n").Select(p => (p.Line, p.Rule)));

    [Theory]
    [InlineData("/-\nCopyright (c) 2025 Ada. All rights reserved.\nReleased under MIT.\nAuthors: Ada\n-/\n")]
    [InlineData("/-\nCopyright 2025 Ada.\nReleased under Apache 2.0 license as described in the file LICENSE.\nAuthors: Ada\n-/\n")]
    [InlineData("/-\nCopyright (c) 2025 Ada. All rights reserved.\nReleased under Apache 2.0 license as described in the file LICENSE.\nAuthors: \n-/\n")]
    public void ReportsAMalformedHeader(string header) =>
        Assert.Contains(MathlibConventions.Find(header + "/-! Doc -/\n"), p => p.Rule == "copyright-header");

    [Fact]
    public void FlagsTheoremNamesThatStartWithACapital()
    {
        string text = Header + "/-! Doc -/\n\ntheorem Foo : True := trivial\n@[simp] protected lemma Nat.Bar_baz (n : Nat) : n = n := rfl\ntheorem Nat.ok_name : True := trivial\ntheorem isOpen_iff : True := trivial\n";
        Assert.Equal([(7, "theorem-name"), (8, "theorem-name")], MathlibConventions.Find(text).Select(p => (p.Line, p.Rule)));
    }

    [Fact]
    public void FlagsTypeNamesThatStartLowercase()
    {
        string text = Header + "/-! Doc -/\n\nstructure point where\n  x : Nat\n\nclass N.isGood (a : Nat) : Prop\ninductive Tree\n@[simp] structure Foo.bar\n";
        Assert.Equal([(7, "type-name"), (10, "type-name"), (12, "type-name")], MathlibConventions.Find(text).Select(p => (p.Line, p.Rule)));
    }

    [Fact]
    public void AddsTheHeaderOnlyWhereThereIsNoCommentFirst()
    {
        string added = MathlibConventions.AddHeader("import A\n", 2026, "Grace Hopper");
        Assert.Equal("/-\nCopyright (c) 2026 Grace Hopper. All rights reserved.\nReleased under Apache 2.0 license as described in the file LICENSE.\nAuthors: Grace Hopper\n-/\nimport A\n", added);
        Assert.DoesNotContain(MathlibConventions.Find(added + "/-! Doc -/\n"), p => p.Rule == "copyright-header");
        Assert.Equal(added, MathlibConventions.AddHeader(added, 2030, "Someone Else"));
    }
}

/// <summary>Finding and deleting the deprecated declarations that are old enough.</summary>
public sealed class StaleDeprecationTests
{
    private static readonly DateOnly Today = new(2026, 10, 3);

    private const string File =
        "theorem keep : True := trivial\n\n"
        + "/-- Old. -/\n@[deprecated (since := \"2025-12-31\")] alias oldName := keep\n\n"
        + "@[deprecated keep (since := \"2024-01-15\")]\ntheorem older : True := keep\n\n"
        + "@[deprecated (since := \"2026-08-01\")] alias recent := keep\n\n"
        + "def last := 1\n";

    [Fact]
    public void FindsOnlyTheDeprecationsPastTheAge()
    {
        IReadOnlyList<StaleDeprecation> stale = StaleDeprecations.Find(File, Today);
        Assert.Equal(["oldName", "older"], stale.Select(s => s.Name));
        Assert.Equal([9, 32], stale.Select(s => s.AgeMonths)); // whole months: 2025-12-31 to 2026-10-03; 2024-01-15 to 2026-10-03
        Assert.Equal([2, 5], stale.Select(s => s.StartLine)); // the first starts at its doc comment
        Assert.Equal(["oldName", "older", "recent"], StaleDeprecations.Find(File, Today, months: 1).Select(s => s.Name));
        Assert.Equal(["older"], StaleDeprecations.Find(File, Today, months: 12).Select(s => s.Name));
        Assert.Equal(["older"], StaleDeprecations.Find(File, new DateOnly(2026, 9, 30), 9).Select(s => s.Name)); // oldName is 8 months old on 9-30
    }

    [Fact]
    public void DeletesThemWithTheirDocCommentsWithoutDoublingBlankLines()
    {
        string result = StaleDeprecations.Remove(File, StaleDeprecations.Find(File, Today));
        Assert.Equal("theorem keep : True := trivial\n\n@[deprecated (since := \"2026-08-01\")] alias recent := keep\n\ndef last := 1\n", result);
        Assert.Empty(StaleDeprecations.Find(result, Today));
    }

    [Fact]
    public void ReadsAYearAndMonthAndIgnoresWhatIsNotADeprecation()
    {
        const string text = "@[deprecated (since := \"2025-01\")] alias a := b\n-- since := \"2020-01-01\"\ndef b := 1\n";
        Assert.Equal(["a"], StaleDeprecations.Find(text, Today).Select(s => s.Name));
        Assert.Empty(StaleDeprecations.Find("def b := 1\n", Today));
    }
}

/// <summary>Declarations without a doc comment.</summary>
public sealed class DocCoverageTests
{
    [Fact]
    public void FindsPublicDefinitionsWithoutADocComment()
    {
        const string text = "/-- Documented. -/\ndef a := 1\n\ndef b := 2\n\n/-- Multi\n  line. -/\n@[simp]\ndef c := 3\n\n@[simp]\nstructure D where\n  x : Nat\n\nprivate def e := 5\n\n/-! Module doc is not a doc comment. -/\ndef f := 6\n\ntheorem t : True := trivial\n";
        Assert.Equal([(3, "missing-doc"), (11, "missing-doc"), (17, "missing-doc")], DocCoverage.Find(text).Select(p => (p.Line, p.Rule)));
        Assert.Contains("`b`", DocCoverage.Find(text)[0].Message, StringComparison.Ordinal);
    }

    [Fact]
    public void LeavesOutIndentedCommentedAndTheoremsUnlessAsked()
    {
        const string text = "namespace N\n  def indented := 1\nend N\n/-\ndef commented := 1\n-/\n-- def line := 1\ntheorem t : True := trivial\n";
        Assert.Empty(DocCoverage.Find(text));
        Assert.Equal([7], DocCoverage.Find(text, includeTheorems: true).Select(p => p.Line));
    }
}

/// <summary>Breaking long comment lines.</summary>
public sealed class WrapCommentsTests
{
    [Fact]
    public void WrapsALineCommentOntoAnotherLineComment()
    {
        string text = "-- one two three four five six seven eight nine ten eleven twelve thirteen fourteen fifteen sixteen\n";
        string wrapped = StyleCheck.WrapComments(text, 90);
        Assert.Equal("-- one two three four five six seven eight nine ten eleven twelve thirteen fourteen\n-- fifteen sixteen\n", wrapped);
        Assert.All(wrapped.Split('\n'), l => Assert.True(l.Length <= 90, l));
        Assert.Equal("  /- one two three four -/\n", StyleCheck.WrapComments("  /- one two three four -/\n", 90));
    }

    [Fact]
    public void WrapsProseInADocCommentAndKeepsItsEnd()
    {
        string text = "/-- alpha beta gamma delta epsilon zeta eta theta iota kappa lambda mu nu xi omicron pi rho sigma tau. -/\ndef x := 1\n";
        Assert.Equal("/-- alpha beta gamma delta epsilon zeta eta theta iota kappa lambda mu nu xi omicron pi rho sigma\ntau. -/\ndef x := 1\n", StyleCheck.WrapComments(text));
        string multi = "/-!\n# Title\n" + string.Join(' ', Enumerable.Range(0, 30).Select(i => "word" + i)) + "\n-/\n";
        string wrapped = StyleCheck.WrapComments(multi);
        Assert.All(wrapped.Split('\n'), l => Assert.True(l.Length <= 100, l));
        Assert.Equal(multi.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries), wrapped.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)); // no word lost or changed
    }

    [Fact]
    public void LeavesCodeFencesIndentedCodeUrlsAndCrlfAlone()
    {
        string code = "def " + string.Join(" ", Enumerable.Repeat("verylongidentifier", 8)) + " := 1\n";
        Assert.Equal(code, StyleCheck.WrapComments(code));
        string fence = "/--\n```\n" + new string('a', 40) + " " + new string('b', 70) + "\n```\n-/\n";
        Assert.Equal(fence, StyleCheck.WrapComments(fence));
        string url = "-- see https://example.com/" + new string('x', 120) + "\n";
        Assert.Equal("-- see\n-- https://example.com/" + new string('x', 120) + "\n", StyleCheck.WrapComments(url)); // the URL goes whole onto a line of its own
        string bare = "-- https://example.com/" + new string('x', 120) + "\n";
        Assert.Equal(bare, StyleCheck.WrapComments(bare));
        Assert.Equal("-- " + string.Join(' ', Enumerable.Repeat("word", 25)) + "\r\n", StyleCheck.WrapComments("-- " + string.Join(' ', Enumerable.Repeat("word", 25)) + "\r\n", 200));
        string crlf = "-- " + string.Join(' ', Enumerable.Repeat("word", 30)) + "\r\n";
        Assert.All(StyleCheck.WrapComments(crlf, 60).Split('\n').Where(l => l.Length > 0), l => Assert.EndsWith("\r", l, StringComparison.Ordinal));
        Assert.DoesNotContain(StyleCheck.Find(StyleCheck.WrapComments("-- " + string.Join(' ', Enumerable.Repeat("word", 40)) + "\n")), p => p.Rule == "long-line");
    }
}

/// <summary>A question ready to paste into the Lean Zulip chat.</summary>
public sealed class ZulipPostTests
{
    [Fact]
    public void PutsTheCodeInAFenceAndErrorsFirstInAQuote()
    {
        string post = ZulipPost.Build("example : 1 = 2 := by\n  simp\n\n",
            [new ZulipMessage(2, 3, "warning", "unused"), new ZulipMessage(2, 3, "error", "simp made no progress\n"), new ZulipMessage(1, 1, "info", "hello")]);
        Assert.Equal("```lean\nexample : 1 = 2 := by\n  simp\n```\n\n```quote\nerror (2:3): simp made no progress\n\nwarning (2:3): unused\n\ninfo (1:1): hello\n```\n", post);
    }

    [Fact]
    public void LeavesOutTheQuoteWithoutMessagesAndLengthensFencesAroundBackticks()
    {
        Assert.Equal("```lean\n#eval 1\n```\n", ZulipPost.Build("#eval 1\n", []));
        string post = ZulipPost.Build("/-- Uses ``` fences. -/\ndef a := 1", [new ZulipMessage(1, 1, "error", "see ````x````")]);
        Assert.StartsWith("````lean\n", post, StringComparison.Ordinal);
        Assert.Contains("`````quote\n", post, StringComparison.Ordinal);
    }
}

/// <summary>Naming a theorem the way Mathlib would, from its statement.</summary>
public sealed class TheoremNamerTests
{
    [Theory]
    [InlineData("(a b : ℕ) : a + b = b + a", "add_comm")]
    [InlineData("(a b : ℕ) : a * b = b * a", "mul_comm")]
    [InlineData("(a b c : ℕ) : a + b + c = a + (b + c)", "add_assoc")]
    [InlineData("(a : ℕ) : a + 0 = a", "add_zero")]
    [InlineData("(a : ℕ) : 0 + a = a", "zero_add")]
    [InlineData("(a : ℕ) : a * 1 = a", "mul_one")]
    [InlineData("(a : ℤ) : - -a = a", "neg_neg")]
    [InlineData("(a : ℤ) : a - a = 0", "sub_self")]
    [InlineData("(a b c d : ℕ) : a + b ≤ c + d", "add_le_add")]
    [InlineData("(a b c : ℕ) : a ≤ b → b < c → a < c", "lt_of_le_of_lt")]
    [InlineData("(a b : ℕ) (h : a < b) : a ≤ b", "le_of_lt")]
    [InlineData("a ≤ a + b", "le_add_self")]
    [InlineData("¬ a < b", "not_lt")]
    public void NamesTheStatementTheMathlibWay(string statement, string name) => Assert.Equal(name, TheoremNamer.Suggest(statement));

    [Fact]
    public void FindsTheDeclarationAroundALine()
    {
        const string text = "def x := 1\n\n@[simp] theorem foo (a b : ℕ) :\n    a + b = b + a := by\n  omega\n\ntheorem bar : True := trivial\n";
        Assert.Equal(("foo", "(a b : ℕ) : a + b = b + a", "add_comm"), TheoremNamer.SuggestAt(text, 3));
        Assert.Equal(("foo", "(a b : ℕ) : a + b = b + a", "add_comm"), TheoremNamer.SuggestAt(text, 2));
        Assert.Null(TheoremNamer.SuggestAt(text, 0)); // a def
        Assert.Null(TheoremNamer.SuggestAt(text, 6)); // no relation to read
        Assert.Null(TheoremNamer.SuggestAt(text, 5)); // between declarations: the one above ended
    }

    [Theory]
    [InlineData("(a : ℕ) : True")]
    [InlineData("")]
    public void SaysNothingWhenThereIsNoRelationToRead(string statement) => Assert.Null(TheoremNamer.Suggest(statement));

    [Fact]
    public void KeepsAssumptionsInTheirOrderAndIgnoresBindersThatStateNothing()
    {
        Assert.Equal("add_le_add_of_le_of_lt", TheoremNamer.Suggest("{α : Type} (a b c d : α) (h₁ : a ≤ b) (h₂ : c < d) : a + c ≤ b + d"));
        Assert.Equal("mul_comm", TheoremNamer.Suggest("∀ a b : ℕ, a * b = b * a"));
    }
}

/// <summary>Merging tactics that follow each other, where that cannot change the proof.</summary>
public sealed class TacticGolfTests
{
    [Fact]
    public void MergesRewritesAndIntrosInARow()
    {
        const string text = "theorem t : P := by\n  intro x\n  intro y z\n  rw [a]\n  rw [← b, c]\n  rw [d]\n  simp_rw [e]\n  simp_rw [f]\n  exact h\n";
        var (merged, count) = TacticGolf.Merge(text);
        Assert.Equal("theorem t : P := by\n  intro x y z\n  rw [a, ← b, c, d]\n  simp_rw [e, f]\n  exact h\n", merged);
        Assert.Equal(4, count);
        Assert.Equal((merged, 0), TacticGolf.Merge(merged)); // nothing left to merge
    }

    [Fact]
    public void MergesRewritesAtTheSameLocationOnly()
    {
        var (merged, count) = TacticGolf.Merge("  rw [a] at h\n  rw [b] at h\n  rw [c] at g\n  rw [d]\n  rw [e] at h ⊢\n");
        Assert.Equal("  rw [a, b] at h\n  rw [c] at g\n  rw [d]\n  rw [e] at h ⊢\n", merged);
        Assert.Equal(1, count);
    }

    [Fact]
    public void LeavesWhatItCannotMergeSafely()
    {
        foreach (string text in new[]
        {
            "  rw [a] -- why\n  rw [b]\n", // a comment
            "  rw [a]; simp\n  rw [b]\n", // another tactic on the line
            "  rw [a]\n    rw [b]\n", // different indentation
            "  intro x\n  rw [b]\n", // different tactics
            "  simp_rw [a]\n  rw [b]\n",
            "  intro x <;> simp\n  intro y\n",
            "  rw [a]\n\n  rw [b]\n", // a blank line between
        })
        {
            Assert.Equal((text, 0), TacticGolf.Merge(text));
        }
    }

    [Fact]
    public void KeepsCrlfLineEndings()
    {
        Assert.Equal(("  intro x y\r\n  exact h\r\n", 1), TacticGolf.Merge("  intro x\r\n  intro y\r\n  exact h\r\n"));
    }
}

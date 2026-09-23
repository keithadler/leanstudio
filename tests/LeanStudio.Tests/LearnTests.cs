using System.Text.Json;
using LeanStudio.Core.Learn;
using LeanStudio.Core.Projects;
using LeanStudio.Core.Updates;

namespace LeanStudio.Tests;

[Collection(Lean.Collection)]
public sealed class LearnTests
{
    private static string TempProject()
    {
        string dir = Directory.CreateTempSubdirectory("leanstudio-learn").FullName;
        File.WriteAllText(Path.Combine(dir, "lean-toolchain"), Lean.Toolchain + "\n");
        return dir;
    }

    public static TheoryData<string> LessonFiles => new(Tutorial.Lessons.Select(l => l.FileName));

    [Theory]
    [MemberData(nameof(LessonFiles))]
    public void EveryLessonStartsWithOnlySorrysAndIsSolvable(string fileName)
    {
        Lean.RequireLean();
        Lesson lesson = Tutorial.Lessons.Single(l => l.FileName == fileName);
        string dir = TempProject();

        File.WriteAllText(Path.Combine(dir, "Start.lean"), lesson.Content);
        string start = Lean.RunLean(dir, "Start.lean");
        Assert.DoesNotContain("error", start, StringComparison.Ordinal);
        Assert.Equal(lesson.Content.Split("sorry").Length - 1 - CountInComments(lesson.Content), lesson.Exercises);
        Assert.True(Tutorial.FirstCodeSorry(lesson.Content) > 0);
        Assert.Contains("declaration uses `sorry`", start, StringComparison.Ordinal);

        File.WriteAllText(Path.Combine(dir, "Solved.lean"), Tutorial.Solved(lesson));
        string solved = Lean.RunLean(dir, "Solved.lean");
        Assert.DoesNotContain("error", solved, StringComparison.Ordinal);
        Assert.DoesNotContain("sorry", solved, StringComparison.Ordinal);
        Assert.DoesNotContain("warning", solved, StringComparison.Ordinal);
    }

    /// <summary>Lesson prose mentions sorry by name; only the ones in code are exercises.</summary>
    private static int CountInComments(string text)
    {
        int n = 0;
        bool inBlock = false;
        foreach (string line in text.Split('\n'))
        {
            string code = Core.Proofs.ProofSteps.StripComments(line, ref inBlock);
            n += line.Split("sorry").Length - 1 - (code.Split("sorry").Length - 1);
        }
        return n;
    }

    [Fact]
    public void EveryCoreTheoremInTheGalleryExists()
    {
        Lean.RequireLean();
        string dir = TempProject();
        string code = string.Join('\n', TheoremGallery.All.Where(t => !t.NeedsMathlib).Select(t => $"#check @{t.LeanName}"));
        File.WriteAllText(Path.Combine(dir, "Gallery.lean"), code);
        string output = Lean.RunLean(dir, "Gallery.lean");
        Assert.DoesNotContain("error", output, StringComparison.Ordinal);
        Assert.True(TheoremGallery.All.Count(t => !t.NeedsMathlib) >= 8);
    }

    [Fact]
    public async Task ThePlaygroundStarterCompiles()
    {
        Lean.RequireLean();
        string dir = TempProject();
        string file = await Playground.CreateAsync("#eval 6 * 7", dir, TestContext.Current.CancellationToken);
        string output = Lean.RunLean(dir, Path.GetFileName(file));
        Assert.DoesNotContain("error", output, StringComparison.Ordinal);
        Assert.Contains("42", output, StringComparison.Ordinal);
        Assert.Contains("'my_first' does not depend on any axioms", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheProgramSnippetRuns()
    {
        Lean.RequireLean();
        string dir = TempProject();
        Snippet s = Snippets.All.Single(x => x.Name == "Program with main");
        (string text, _) = s.Expand("");
        string file = Path.Combine(dir, "Main.lean");
        File.WriteAllText(file, text);
        Assert.True(ProgramRunner.HasMain(text));
        var r = await ProgramRunner.RunAsync(new LeanProject(dir), file, ct: TestContext.Current.CancellationToken);
        Assert.True(r.Success, r.Output);
        Assert.Contains("Hello from Lean!", r.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void SnippetsIndentToTheirLineAndPlaceTheCaret()
    {
        Snippet s = Snippets.All.Single(x => x.Name == "Proof by cases");
        (string text, int cursor) = s.Expand("    ");
        Assert.Contains("\n      | inr h2", text, StringComparison.Ordinal);
        Assert.DoesNotContain("$0", text, StringComparison.Ordinal);
        Assert.Equal(text.IndexOf("| inl h1 => ", StringComparison.Ordinal) + "| inl h1 => ".Length, cursor);
    }

    [Theory]
    [InlineData("∀ (p q : Prop), p ∧ q → q ∧ p", "For all propositions p and q: if p and q, then q and p")]
    [InlineData("∀ (n : ℕ), n ≤ n + 1", "For all natural number n: n is at most n + 1")]
    [InlineData("∃ n, n * n = 16", "There is an n such that n * n equals 16")]
    [InlineData("¬p", "It is not the case that p")]
    [InlineData("p ↔ q", "p exactly when q")]
    [InlineData("a + b = b + a", "a + b equals b + a")]
    [InlineData("q ∧ p", "q and p")]
    [InlineData("p → q → r", "If p and q, then r")]
    public void ReadsStatementsInEnglish(string lean, string english) => Assert.Equal(english, PlainEnglish.Read(lean));

    [Theory]
    [InlineData("unsolved goals\ncase succ\n⊢ p", "not finished")]
    [InlineData("unknown identifier 'foo'", "does not know this name")]
    [InlineData("declaration uses `sorry`", "placeholder")]
    [InlineData("type mismatch\n  h\nhas type", "different type")]
    [InlineData("omega could not prove the goal", "omega only handles")]
    [InlineData("unexpected token ':='; expected term", "syntax error")]
    public void ExplainsCommonErrors(string message, string contains) =>
        Assert.Contains(contains, ErrorGuide.Explain(message), StringComparison.Ordinal);

    [Fact]
    public void UnknownMessagesGetNoExplanation() => Assert.Null(ErrorGuide.Explain("something only this test says"));

    [Theory]
    [InlineData("  intro hp", "intro")]
    [InlineData("  · exact hp", "exact")]
    [InlineData("  | succ k ih => simp [ih]", "simp")]
    [InlineData("  exact?", "exact?")]
    public void FindsTheTacticOfALine(string line, string tactic)
    {
        Assert.Equal(tactic, TacticGuide.TacticOf(line));
        Assert.NotNull(TacticGuide.Explain(tactic));
    }

    [Fact]
    public void ExplainsTheTacticsTheTutorialUses()
    {
        foreach (string t in new[] { "intro", "exact", "apply", "rfl", "decide", "constructor", "left", "right", "cases", "rw", "simp", "induction", "omega", "theorem", "def", "example", "by" })
        {
            Assert.NotNull(TacticGuide.Explain(t));
        }
    }
}

public sealed class UpdateTests
{
    private static JsonElement Release(string tag, bool prerelease = false, params string[] assets) => JsonDocument.Parse($$"""
        {"tag_name":"{{tag}}","name":"Lean Studio {{tag}}","body":"notes","html_url":"https://github.com/keithadler/leanstudio/releases/tag/{{tag}}",
         "draft":false,"prerelease":{{(prerelease ? "true" : "false")}},
         "assets":[{{string.Join(",", assets.Select(a => $$"""{"name":"{{a}}","browser_download_url":"https://example.invalid/{{a}}","size":123}"""))}}]}
        """).RootElement;

    [Fact]
    public void FindsANewerReleaseAndItsBuildForThisComputer()
    {
        UpdateInfo? u = UpdateChecker.Evaluate(Release("v0.3.0", false, "LeanStudio-0.3.0-osx-arm64.zip", "LeanStudio-0.3.0-win-x64.zip"), new Version(0, 2, 0), "win-x64");
        Assert.NotNull(u);
        Assert.Equal(new Version(0, 3, 0), u.Version);
        Assert.Equal("LeanStudio-0.3.0-win-x64.zip", u.AssetName);
        Assert.True(u.HasDownload);
    }

    [Fact]
    public void SameOlderOrPreReleaseIsNotAnUpdate()
    {
        Assert.Null(UpdateChecker.Evaluate(Release("v0.2.0"), new Version(0, 2, 0), "osx-arm64"));
        Assert.Null(UpdateChecker.Evaluate(Release("v0.1.9"), new Version(0, 2, 0), "osx-arm64"));
        Assert.Null(UpdateChecker.Evaluate(Release("v9.0.0", prerelease: true), new Version(0, 2, 0), "osx-arm64"));
    }

    [Fact]
    public void AReleaseWithoutThisPlatformStillReportsItsPage()
    {
        UpdateInfo? u = UpdateChecker.Evaluate(Release("v1.0.0", false, "LeanStudio-1.0.0-linux-x64.tar.gz"), new Version(0, 2, 0), "osx-arm64");
        Assert.NotNull(u);
        Assert.False(u.HasDownload);
        Assert.EndsWith("/v1.0.0", u.PageUrl, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("v0.2.0", "0.2.0")]
    [InlineData("0.10.1", "0.10.1")]
    [InlineData("v1.2.3-beta.1", "1.2.3")]
    [InlineData("v1.2", "1.2.0")]
    public void ParsesTags(string tag, string version) => Assert.Equal(Version.Parse(version), UpdateChecker.ParseTag(tag));

    [Fact]
    public void KnowsThisComputersRuntime() => Assert.Matches(@"^(osx|win|linux)-(x64|arm64)$", UpdateChecker.CurrentRuntime);
}

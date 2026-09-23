using System.Text.Json;
using LeanStudio.Core.Git;
using LeanStudio.Core.Projects;
using LeanStudio.Core.Workflow;

namespace LeanStudio.Tests;

public sealed class WorkflowTests
{
    [Fact]
    public void FindsSorriesAdmitsAndTodosButNotSorryInComments()
    {
        string[] lines =
        [
            "/-- This mentions sorry in a doc comment. -/",
            "theorem a : True := by",
            "  sorry",
            "-- TODO: prove b properly",
            "lemma b (n : Nat) : n = n := by admit",
            "def c := 1 -- FIXME rename",
            "example : 1 = 1 := by exact (sorry)",
        ];
        var found = Markers.ScanText("F.lean", lines).ToList();
        Assert.Equal(5, found.Count);
        Assert.Contains(found, m => m.Kind == MarkerKind.Sorry && m.Line == 2 && m.Declaration == "a");
        Assert.Contains(found, m => m.Kind == MarkerKind.Admit && m.Declaration == "b");
        Assert.Contains(found, m => m.Kind == MarkerKind.Todo && m.Line == 3);
        Assert.Contains(found, m => m.Kind == MarkerKind.Todo && m.Line == 5);
        Assert.Contains(found, m => m.Kind == MarkerKind.Sorry && m.Line == 6 && m.Declaration == "example");
        Assert.DoesNotContain(found, m => m.Line == 0);
    }

    [Fact]
    public void ScansAProjectOnDisk()
    {
        var found = Markers.Scan(Lean.Sample("Proofs"), TestContext.Current.CancellationToken);
        Marker m = Assert.Single(found, x => x.Kind == MarkerKind.Sorry);
        Assert.Equal("unfinished", m.Declaration);
        Assert.EndsWith("Basic.lean", m.Path, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadsLakeBuildErrorsWithTheirContinuationLines()
    {
        const string log = """
            ⚠ [2/5] Replayed Proofs.Basic
            warning: Proofs/Basic.lean:20:8: declaration uses `sorry`
            ✖ [3/5] Building Proofs.Broken (262ms)
            trace: .> LEAN_PATH=/x lean /x/Proofs/Broken.lean -o /x/Broken.olean
            error: Proofs/Broken.lean:1:23: unsolved goals
            ⊢ False
            Some required targets logged failures:
            - Proofs.Broken
            error: build failed
            """;
        var messages = LakeOutput.Parse(log, "/proj");
        Assert.Equal(2, messages.Count);
        BuildMessage warn = messages[0];
        Assert.False(warn.IsError);
        Assert.Equal(Path.GetFullPath("/proj/Proofs/Basic.lean"), warn.Path);
        Assert.Equal(19, warn.Line);
        BuildMessage err = messages[1];
        Assert.True(err.IsError);
        Assert.Equal("unsolved goals\n⊢ False", err.Message);
        Assert.Equal(0, err.Line);
        Assert.Equal(23, err.Column);
    }

    [Fact]
    public void KeepsLocalHistoryOfSaves()
    {
        string dir = Directory.CreateTempSubdirectory("leanstudio-history").FullName;
        var history = new LocalHistory(Path.Combine(dir, "h"));
        string file = Path.Combine(dir, "A.lean");
        history.Record(file, "one");
        Thread.Sleep(5);
        history.Record(file, "one"); // unchanged: not recorded again
        Thread.Sleep(5);
        history.Record(file, "two");
        var versions = history.Versions(file);
        Assert.Equal(2, versions.Count);
        Assert.Equal("two", File.ReadAllText(versions[0].SnapshotFile));
        Assert.Equal("one", File.ReadAllText(versions[1].SnapshotFile));
        Assert.Empty(history.Versions(Path.Combine(dir, "Other.lean")));
    }

    [Fact]
    public void ParsesLoogleResults()
    {
        using var doc = JsonDocument.Parse("""
            {"count": 14, "header": "Found 14 declarations mentioning Nat.add_comm.\n", "hits": [
              {"doc": null, "module": "Init.Data.Nat.Basic", "name": "Nat.add_comm", "type": " (n m : ℕ) : n + m = m + n"}]}
            """);
        var (hits, error, count) = Loogle.Parse(doc.RootElement);
        Assert.Null(error);
        Assert.Equal(14, count);
        LoogleHit h = Assert.Single(hits);
        Assert.Equal("(n m : ℕ) : n + m = m + n", h.Type);
        using var bad = JsonDocument.Parse("""{"error": "Unknown identifier 'foo'"}""");
        Assert.Equal("Unknown identifier 'foo'", Loogle.Parse(bad.RootElement).Error);
    }

    [Fact]
    public void LinksToTheDocumentationSite() =>
        Assert.Equal("https://leanprover-community.github.io/mathlib4_docs/Init/Data/Nat/Basic.html#Nat.add_comm", DocLinks.For("Init.Data.Nat.Basic", "Nat.add_comm"));

    [Fact]
    public async Task BlamesALine()
    {
        Assert.SkipWhen(!GitRepository.IsGitInstalled, "git is not installed");
        var ct = TestContext.Current.CancellationToken;
        string dir = Directory.CreateTempSubdirectory("leanstudio-blame").FullName;
        await GitRepository.InitAsync(dir, ct);
        GitRepository repo = GitRepository.Find(dir)!;
        await repo.RunAsync(["config", "user.email", "ada@example.com"], ct: ct);
        await repo.RunAsync(["config", "user.name", "Ada"], ct: ct);
        await repo.RunAsync(["config", "commit.gpgsign", "false"], ct: ct);
        string file = Path.Combine(dir, "A.lean");
        await File.WriteAllTextAsync(file, "def a := 1\n", ct);
        await repo.CommitAsync("Add a", ct);
        BlameLine? b = await Blame.LineAsync(repo, file, 1, ct);
        Assert.NotNull(b);
        Assert.Equal("Ada", b.Author);
        Assert.Equal("Add a", b.Summary);
        Assert.StartsWith("Ada, just now · Add a", b.Describe(DateTimeOffset.Now.AddSeconds(20)), StringComparison.Ordinal);
        await File.AppendAllTextAsync(file, "def b := 2\n", ct);
        Assert.True((await Blame.LineAsync(repo, file, 2, ct))!.IsUncommitted);
    }

    [Theory]
    [InlineData(0.5, "just now")]
    [InlineData(30, "30 min ago")]
    [InlineData(60 * 5, "5 h ago")]
    [InlineData(60 * 24 * 3, "3 days ago")]
    [InlineData(60 * 24 * 200, "6 months ago")]
    [InlineData(60 * 24 * 800, "2 years ago")]
    public void SaysHowLongAgo(double minutes, string expected) => Assert.Equal(expected, BlameLine.Ago(TimeSpan.FromMinutes(minutes)));

    [Fact]
    public void OffersTheProjectsTasks()
    {
        string dir = Directory.CreateTempSubdirectory("leanstudio-tasks").FullName;
        File.WriteAllText(Path.Combine(dir, "lakefile.toml"), "name = \"p\"\n\n[[lean_exe]]\nname = \"hello\"\nroot = \"Main\"\n");
        var tasks = ProjectTasks.For(new LeanProject(dir));
        Assert.Contains(tasks, t => t.Title == "lake build");
        Assert.Contains(tasks, t => t.Title == "lake test");
        Assert.Contains(tasks, t => t.Title == "lake exe hello" && t.Arguments.SequenceEqual(["exe", "hello"]));
        Assert.DoesNotContain(tasks, t => t.Title.Contains("cache", StringComparison.Ordinal));

        File.WriteAllText(Path.Combine(dir, "lakefile.toml"), "");
        File.WriteAllText(Path.Combine(dir, "lakefile.lean"), "import Lake\nopen Lake DSL\npackage p\nlean_exe tool\nscript greet do\n  return 0\n");
        File.Delete(Path.Combine(dir, "lakefile.toml"));
        tasks = ProjectTasks.For(new LeanProject(dir));
        Assert.Contains(tasks, t => t.Title == "lake exe tool");
        Assert.Contains(tasks, t => t.Title == "lake script run greet");
    }

    [Fact]
    public async Task RunsShellCommands()
    {
        var ct = TestContext.Current.CancellationToken;
        ProjectTask t = ProjectTasks.Shell("echo lean-studio-shell");
        var r = await Core.Processes.ProcessRunner.RunAsync(t.FileName, t.Arguments, Path.GetTempPath(), ct: ct);
        Assert.True(r.Success, r.Output);
        Assert.Contains("lean-studio-shell", r.Output, StringComparison.Ordinal);
    }
}

/// <summary>Against the real GitHub and Loogle; run with LEANSTUDIO_NETWORK_TESTS=1 (CI does not).</summary>
public sealed class NetworkTests
{
    private static void RequireNetwork() =>
        Assert.SkipUnless(Environment.GetEnvironmentVariable("LEANSTUDIO_NETWORK_TESTS") == "1", "set LEANSTUDIO_NETWORK_TESTS=1 to run");

    [Fact]
    public async Task AnOldVersionIsOfferedTheLatestReleaseAndCanDownloadIt()
    {
        RequireNetwork();
        var ct = TestContext.Current.CancellationToken;
        var checker = new Core.Updates.UpdateChecker();
        Core.Updates.UpdateInfo? u = await checker.CheckAsync(new Version(0, 1, 0), ct: ct);
        Assert.NotNull(u);
        Assert.True(u.Version >= new Version(0, 2, 0));
        Assert.True(u.HasDownload, "a build for " + Core.Updates.UpdateChecker.CurrentRuntime);
        string dir = Directory.CreateTempSubdirectory("leanstudio-update").FullName;
        string file = await checker.DownloadAsync(u, folder: dir, ct: ct);
        Assert.True(new FileInfo(file).Length > 10_000_000);
        using var zip = System.IO.Compression.ZipFile.OpenRead(file);
        Assert.Contains(zip.Entries, e => e.FullName.Contains("LeanStudio", StringComparison.Ordinal));
        Assert.Null(await checker.CheckAsync(u.Version, ct: ct)); // the newest is not an update to itself
    }

    [Fact]
    public async Task LoogleFindsMathlib()
    {
        RequireNetwork();
        var (hits, error, _) = await new Core.Workflow.Loogle().SearchAsync("Nat.add_comm", TestContext.Current.CancellationToken);
        Assert.Null(error);
        Assert.Contains(hits, h => h.Name == "Nat.add_comm");
    }
}

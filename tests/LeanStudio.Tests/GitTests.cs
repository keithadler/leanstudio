using LeanStudio.Core.Git;

namespace LeanStudio.Tests;

public sealed class GitTests
{
    [Fact]
    public void ParsesPorcelainStatus()
    {
        string output = "# branch.oid abc\0# branch.head main\0# branch.upstream origin/main\0# branch.ab +2 -1\0"
            + "1 .M N... 100644 100644 100644 a b Proofs/Basic.lean\0"
            + "1 A. N... 000000 100644 100644 0 b New.lean\0"
            + "2 R. N... 100644 100644 100644 a b R100 Renamed.lean\0Old.lean\0"
            + "? scratch.lean\0";
        GitStatus s = GitRepository.ParseStatus(output);
        Assert.Equal("main", s.Branch);
        Assert.Equal("origin/main", s.Upstream);
        Assert.Equal(2, s.Ahead);
        Assert.Equal(1, s.Behind);
        Assert.Equal(4, s.Changes.Count);
        GitChange basic = Assert.Single(s.Changes, c => c.Path == "Proofs/Basic.lean");
        Assert.False(basic.IsStaged);
        Assert.Equal("M", basic.Letter);
        Assert.True(Assert.Single(s.Changes, c => c.Path == "New.lean").IsStaged);
        Assert.Equal("Old.lean", Assert.Single(s.Changes, c => c.Path == "Renamed.lean").OriginalPath);
        Assert.True(Assert.Single(s.Changes, c => c.Path == "scratch.lean").IsUntracked);
    }

    [Fact]
    public void ParsesHunkHeadersIntoLineChanges()
    {
        string diff = "@@ -3,0 +4,2 @@\n+a\n+b\n@@ -10 +12 @@\n-x\n+y\n@@ -20,3 +21,0 @@\n-p\n-q\n-r\n";
        var changes = GitRepository.ParseLineChanges(diff);
        Assert.Equal(new LineChange(4, 2, LineChangeKind.Added), changes[0]);
        Assert.Equal(new LineChange(12, 1, LineChangeKind.Modified), changes[1]);
        Assert.Equal(new LineChange(21, 0, LineChangeKind.Deleted), changes[2]);
    }

    [Theory]
    [InlineData("https://github.com/keithadler/leanstudio.git", "keithadler/leanstudio")]
    [InlineData("git@github.com:keithadler/tenet.git", "keithadler/tenet")]
    [InlineData("https://github.com/leanprover-community/mathlib4", "leanprover-community/mathlib4")]
    [InlineData("https://gitlab.com/a/b.git", null)]
    public void RecognisesGitHubRemotes(string url, string? repo) => Assert.Equal(repo, GitHub.RepositoryOf(url));

    [Fact]
    public void BuildsGitHubLinksAndExpandsShorthand()
    {
        Assert.Equal("https://github.com/keithadler/leanstudio/blob/main/Proofs/Basic.lean#L12", GitHub.FileUrl("keithadler/leanstudio", "main", "Proofs/Basic.lean", 12));
        Assert.Equal("https://github.com/leanprover-community/mathlib4.git", GitHub.ExpandShorthand("leanprover-community/mathlib4"));
        Assert.Equal("git@github.com:a/b.git", GitHub.ExpandShorthand("git@github.com:a/b.git"));
    }

    [Fact]
    public async Task WorksWithARealRepository()
    {
        Assert.SkipWhen(!GitRepository.IsGitInstalled, "git is not installed");
        string dir = Directory.CreateTempSubdirectory("leanstudio-git").FullName;
        var ct = TestContext.Current.CancellationToken;
        var (init, _) = await GitRepository.InitAsync(dir, ct);
        Assert.True(init.Success, init.Output);
        GitRepository repo = GitRepository.Find(dir)!;
        await repo.RunAsync(["config", "user.email", "test@example.com"], ct: ct);
        await repo.RunAsync(["config", "user.name", "Test"], ct: ct);
        await repo.RunAsync(["config", "commit.gpgsign", "false"], ct: ct);

        string file = Path.Combine(dir, "A.lean");
        await File.WriteAllTextAsync(file, "def a := 1\ndef b := 2\ndef c := 3\n", ct);
        GitStatus s = await repo.StatusAsync(ct);
        Assert.Equal("main", s.Branch);
        Assert.True(Assert.Single(s.Changes).IsUntracked);

        var commit = await repo.CommitAsync("first", ct);
        Assert.True(commit.Success, commit.Output);
        Assert.True((await repo.StatusAsync(ct)).IsClean);

        await File.WriteAllTextAsync(file, "def a := 1\ndef b := 20\ndef c := 3\ndef d := 4\n", ct);
        var lines = await repo.LineChangesAsync(file, ct);
        Assert.Contains(lines, l => l.Kind == LineChangeKind.Modified && l.StartLine == 2);
        Assert.Contains(lines, l => l.Kind == LineChangeKind.Added && l.StartLine == 4);
        Assert.Contains("+def b := 20", await repo.DiffAsync("A.lean", ct: ct), StringComparison.Ordinal);

        var branch = await repo.CreateBranchAsync("feature", ct);
        Assert.True(branch.Success, branch.Output);
        Assert.Contains("feature", await repo.BranchesAsync(ct));
        Assert.Contains("first", (await repo.LogAsync(5, ct))[0], StringComparison.Ordinal);

        string? workflow = GitHub.AddLeanWorkflow(dir);
        Assert.NotNull(workflow);
        Assert.Contains("leanprover/lean-action", File.ReadAllText(workflow), StringComparison.Ordinal);
        Assert.Null(GitHub.AddLeanWorkflow(dir)); // only once
    }
}

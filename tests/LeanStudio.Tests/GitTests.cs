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

    [Fact]
    public async Task StagesDiscardsSwitchesPushesAndPullsWithARemote()
    {
        Assert.SkipWhen(!GitRepository.IsGitInstalled, "git is not installed");
        string dir = Directory.CreateTempSubdirectory("leanstudio-remote").FullName;
        var ct = TestContext.Current.CancellationToken;
        try
        {
            // A remote (a bare repository) and two clones of it: one pushes, the other pulls.
            string remote = Path.Combine(dir, "proofs.git");
            Directory.CreateDirectory(remote);
            Assert.True((await Core.Processes.ProcessRunner.RunAsync("git", ["init", "--bare", "-b", "main"], remote, ct: ct)).Success);
            async Task<GitRepository> CloneAs(string who)
            {
                var (clone, path) = await GitRepository.CloneAsync(remote, Path.Combine(dir, who), ct: ct);
                Assert.True(clone.Success, clone.Output);
                GitRepository r = GitRepository.Find(path!)!;
                await r.RunAsync(["config", "user.email", "test@example.com"], ct: ct);
                await r.RunAsync(["config", "user.name", "Test"], ct: ct);
                await r.RunAsync(["config", "commit.gpgsign", "false"], ct: ct);
                await r.RunAsync(["checkout", "-B", "main"], ct: ct);
                return r;
            }
            GitRepository a = await CloneAs("a");
            string file = Path.Combine(a.Root, "A.lean");
            await File.WriteAllTextAsync(file, "def a := 1\n", ct);
            Assert.True((await a.CommitAsync("first", ct)).Success);
            var push = await a.PushAsync(ct: ct); // no upstream yet: published to origin and tracked
            Assert.True(push.Success, push.Output);
            Assert.Equal("origin/main", (await a.StatusAsync(ct)).Upstream);

            // Stage, unstage, discard.
            await File.WriteAllTextAsync(file, "def a := 2\n", ct);
            await a.StageAsync(["A.lean"], ct);
            Assert.True(Assert.Single((await a.StatusAsync(ct)).Changes).IsStaged);
            await a.UnstageAsync(["A.lean"], ct);
            Assert.False(Assert.Single((await a.StatusAsync(ct)).Changes).IsStaged);
            await a.DiscardAsync(Assert.Single((await a.StatusAsync(ct)).Changes), ct);
            Assert.Equal("def a := 1\n", (await File.ReadAllTextAsync(file, ct)).Replace("\r\n", "\n", StringComparison.Ordinal));
            await File.WriteAllTextAsync(Path.Combine(a.Root, "Scratch.lean"), "-- scratch\n", ct);
            await a.DiscardAsync(Assert.Single((await a.StatusAsync(ct)).Changes), ct); // untracked: deleted
            Assert.False(File.Exists(Path.Combine(a.Root, "Scratch.lean")));
            Assert.True((await a.StatusAsync(ct)).IsClean);

            // Switch branches, and back.
            Assert.True((await a.CreateBranchAsync("feature", ct)).Success);
            Assert.Equal("feature", (await a.StatusAsync(ct)).Branch);
            Assert.True((await a.CheckoutAsync("main", ct)).Success);
            Assert.Equal("main", (await a.StatusAsync(ct)).Branch);

            // The other clone pulls what was pushed.
            GitRepository b = await CloneAs("b");
            await File.WriteAllTextAsync(file, "def a := 1\ndef b := 2\n", ct);
            Assert.True((await a.CommitAsync("second", ct)).Success);
            Assert.True((await a.PushAsync(ct: ct)).Success);
            var pull = await b.PullAsync(ct: ct);
            Assert.True(pull.Success, pull.Output);
            Assert.Contains("def b := 2", await File.ReadAllTextAsync(Path.Combine(b.Root, "A.lean"), ct), StringComparison.Ordinal);
        }
        finally
        {
            Lean.DeleteTree(dir);
        }
    }

    [Fact]
    public async Task PublishesToGitHubAndOpensAPullRequestWithGh()
    {
        // A stand-in for gh on the PATH, which does what the real one does for these commands: `repo create --push`
        // adds the remote and pushes; `pr create` answers with the pull request's URL.
        Assert.SkipWhen(OperatingSystem.IsWindows() || !GitRepository.IsGitInstalled, "the stand-in for gh is a shell script");
        string dir = Directory.CreateTempSubdirectory("leanstudio-gh").FullName;
        var ct = TestContext.Current.CancellationToken;
        string oldPath = Environment.GetEnvironmentVariable("PATH") ?? "";
        try
        {
            string bin = Path.Combine(dir, "bin"), log = Path.Combine(dir, "gh.log"), remote = Path.Combine(dir, "remote.git");
            Directory.CreateDirectory(bin);
            File.WriteAllText(Path.Combine(bin, "gh"), $$"""
                #!/bin/sh
                echo "$@" >> '{{log}}'
                case "$1 $2" in
                  "repo create")
                    git init -q --bare -b main '{{remote}}'
                    while [ $# -gt 0 ]; do [ "$1" = "--source" ] && src="$2"; shift; done
                    git -C "$src" remote add origin '{{remote}}' && git -C "$src" push -q -u origin HEAD ;;
                  "pr create") echo "https://github.com/me/proofs/pull/1" ;;
                esac
                """);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(Path.Combine(bin, "gh"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
            Environment.SetEnvironmentVariable("PATH", bin + Path.PathSeparator + oldPath);
            Assert.Equal(Path.Combine(bin, "gh"), GitHub.Gh);

            string work = Path.Combine(dir, "proofs");
            Directory.CreateDirectory(work);
            await GitRepository.InitAsync(work, ct);
            GitRepository repo = GitRepository.Find(work)!;
            await repo.RunAsync(["config", "user.email", "test@example.com"], ct: ct);
            await repo.RunAsync(["config", "user.name", "Test"], ct: ct);
            await repo.RunAsync(["config", "commit.gpgsign", "false"], ct: ct);
            await File.WriteAllTextAsync(Path.Combine(work, "A.lean"), "def a := 1\n", ct);
            await repo.CommitAsync("first", ct);

            var published = await GitHub.PublishAsync(repo, "proofs", isPrivate: true, description: null, ct: ct);
            Assert.True(published.Success, published.Output);
            Assert.Contains($"repo create proofs --private --source {work} --remote origin --push", File.ReadAllText(log), StringComparison.Ordinal);
            Assert.Equal("origin/main", (await repo.StatusAsync(ct)).Upstream);

            var pr = await GitHub.CreatePullRequestAsync(repo, "Prove it", null, draft: false, ct: ct);
            Assert.Contains("https://github.com/me/proofs/pull/1", pr.Output, StringComparison.Ordinal);
            Assert.Contains("pr create --title Prove it --body", File.ReadAllText(log), StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", oldPath);
            Lean.DeleteTree(dir);
        }
    }
}

using LeanStudio.Core.Git;
using LeanStudio.Core.Projects;
using LeanStudio.Core.Proofs;

namespace LeanStudio.Tests;

/// <summary>The profiler's saved profiles and its regression check.</summary>
[Collection(Lean.Collection)]
public sealed class ProfileCheckTests
{
    [Fact]
    public void FindsEachDeclarationsHeartbeatLimit()
    {
        string[] lines =
        [
            "theorem a : True := trivial",
            "set_option maxHeartbeats 400000",
            "theorem b : True := trivial",
            "set_option maxHeartbeats 50000 in",
            "/-- doc -/",
            "@[simp]",
            "theorem c : True := trivial",
            "set_option maxHeartbeats 0 in theorem d : True := trivial",
        ];
        Assert.Equal(200_000, ProfileCheck.LimitAt(lines, 0));
        Assert.Equal(400_000, ProfileCheck.LimitAt(lines, 2));
        Assert.Equal(50_000, ProfileCheck.LimitAt(lines, Profiler.OwnerOf(lines, 4)));
        Assert.Equal(0, ProfileCheck.LimitAt(lines, 7));
    }

    [Fact]
    public void JudgesRegressionsAndLimits()
    {
        static ProfileReport R(params (string Name, int Line, double Hb)[] ds) =>
            new(ds.Select(d => new DeclarationTiming(d.Line, "theorem " + d.Name + " : p", d.Hb, null, 0) { Unit = ProfileUnit.Heartbeats }).ToList(), ProfileUnit.Heartbeats);
        string[] lines = ["theorem grew : p", "theorem noise : p", "set_option maxHeartbeats 10000 in", "theorem tight : p", "theorem same : p", "theorem faster : p"];
        ProfileReport before = R(("grew", 0, 10_000), ("noise", 1, 500), ("tight", 3, 1_000), ("same", 4, 3_000), ("faster", 5, 9_000));
        ProfileReport after = R(("grew", 0, 12_000), ("noise", 1, 900), ("tight", 3, 6_000), ("same", 4, 3_000), ("faster", 5, 4_000));
        var judged = ProfileCheck.Judge("/p/A.lean", lines, before, after, new CheckOptions()).ToDictionary(d => d.Name);
        Assert.Equal("20% more heartbeats than at main", judged["grew"].Problem);
        Assert.Null(judged["noise"].Problem); // 80% more, but only 400 heartbeats
        Assert.Equal("uses 60% of its maxHeartbeats (10,000)", judged["tight"].Problem);
        Assert.False(judged.ContainsKey("same")); // unchanged and fine
        Assert.Null(judged["faster"].Problem);
        Assert.Equal("−5,000 hb (−56%)", judged["faster"].Change);

        var report = new CheckReport("main", "abc1234", [("/p/A.lean", null)], [.. judged.Values.OrderByDescending(d => d.Problem is not null)]);
        Assert.False(report.Passed);
        string md = ProfileCheck.ToMarkdown(report, "/p");
        Assert.Contains("### Lean heartbeat check against `main` (abc1234)", md, StringComparison.Ordinal);
        Assert.Contains("**Failed**: 2 declarations over the limits.", md, StringComparison.Ordinal);
        Assert.Contains("| `tight` | A.lean:4 | 1,000 | 6,000 | +5,000 hb (+500%) | 60% | ❌ uses 60% of its maxHeartbeats (10,000) |", md, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SavesProfilesForLater()
    {
        string dir = Directory.CreateTempSubdirectory("leanstudio-profiles").FullName;
        try
        {
            var project = new LeanProject(dir);
            string file = Path.Combine(dir, "A.lean");
            await File.WriteAllTextAsync(file, "theorem t : True := trivial\n", TestContext.Current.CancellationToken);
            var report = new ProfileReport([new DeclarationTiming(0, "theorem t : True := trivial", 1234, "omega", 1000) { Unit = ProfileUnit.Heartbeats }], ProfileUnit.Heartbeats) { Path = file };
            SavedProfile? saved = await ProfileStore.SaveAsync(project, report, null, TestContext.Current.CancellationToken);
            Assert.NotNull(saved);
            Assert.Null(await ProfileStore.SaveAsync(project, report with { OnlyLine = 0 }, null, TestContext.Current.CancellationToken));
            SavedProfile back = Assert.Single(ProfileStore.List(project, file));
            Assert.Equal(("A.lean", ProfileUnit.Heartbeats, 1234.0), (back.File, back.Unit, back.Total));
            Assert.Contains("no commit", back.Describe(), StringComparison.Ordinal);
            DeclarationTiming t = Assert.Single(back.ToReport(file).Declarations);
            Assert.Equal(("t", "omega", ProfileUnit.Heartbeats), (t.Name, t.HotSpot, t.Unit));
            Assert.StartsWith(Path.Combine(dir, ".lake", "leanstudio", "profiles"), ProfileStore.FolderFor(project, file), StringComparison.Ordinal);
        }
        finally
        {
            Lean.DeleteTree(dir);
        }
    }

    [Fact(Timeout = 600_000)]
    public async Task CatchesASlowdownInAFileThatDidNotChange()
    {
        Lean.RequireLean();
        Assert.SkipWhen(!GitRepository.IsGitInstalled, "git is not installed");
        var ct = TestContext.Current.CancellationToken;
        string dir = Directory.CreateTempSubdirectory("leanstudio-dependents").FullName;
        try
        {
            await File.WriteAllTextAsync(Path.Combine(dir, "lean-toolchain"), Lean.Toolchain + "\n", ct);
            await File.WriteAllTextAsync(Path.Combine(dir, "lakefile.toml"), "name = \"dep\"\ndefaultTargets = [\"Dep\"]\n\n[[lean_lib]]\nname = \"Dep\"\n", ct);
            await File.WriteAllTextAsync(Path.Combine(dir, ".gitignore"), ".lake\n", ct);
            Directory.CreateDirectory(Path.Combine(dir, "Dep"));
            string basePath = Path.Combine(dir, "Dep", "Base.lean"), userPath = Path.Combine(dir, "Dep", "User.lean");
            await File.WriteAllTextAsync(basePath, "def n : Nat := 5\n", ct);
            // Its proof costs more the larger `n` is, but the file never changes.
            await File.WriteAllTextAsync(userPath, "import Dep.Base\n\ntheorem t : (List.range n).length = n := by decide\n", ct);
            await File.WriteAllTextAsync(Path.Combine(dir, "Dep.lean"), "import Dep.User\n", ct);
            await GitRepository.InitAsync(dir, ct);
            GitRepository repo = GitRepository.Find(dir)!;
            await repo.RunAsync(["config", "user.email", "test@example.com"], ct: ct);
            await repo.RunAsync(["config", "user.name", "Test"], ct: ct);
            await repo.RunAsync(["config", "commit.gpgsign", "false"], ct: ct);
            await repo.RunAsync(["add", "--all"], ct: ct);
            Assert.True((await repo.RunAsync(["commit", "-m", "base"], ct: ct)).Success);

            await File.WriteAllTextAsync(basePath, "def n : Nat := 40\n", ct);
            var project = new LeanProject(dir);
            Assert.True((await Lake.BuildAsync(project, null, null, ct)).Success);
            Assert.Equal([userPath, Path.Combine(dir, "Dep.lean")], ProfileCheck.NearestDependents(project, [basePath], 20).Order(StringComparer.Ordinal).Reverse());

            var steps = new List<string>();
            CheckReport report = await ProfileCheck.RunAsync(project, new CheckOptions("main", MinDelta: 10), new SyncProgress(steps), ct).WaitAsync(Lean.Patience, ct);
            Assert.True(report.Built);
            Assert.Equal(2, report.DependentFiles);
            Assert.Contains(steps, st => st.StartsWith("building main", StringComparison.Ordinal));
            CheckedDeclaration t = Assert.Single(report.Declarations, d => d.Name == "t");
            // Too cheap to measure then (n was 5), costly now (n is 40): a regression, not a new declaration.
            Assert.True(t.Dependent);
            Assert.True(t.WasUnder && t.Before is null && t.After > 20, $"{t.Before} then, {t.After} now");
            Assert.EndsWith("more heartbeats than at main, where it was too cheap to measure", t.Problem, StringComparison.Ordinal);
            Assert.StartsWith("+", t.Change, StringComparison.Ordinal);
            Assert.EndsWith("hb (was under 20)", t.Change, StringComparison.Ordinal);
            Assert.False(report.Passed);
            Assert.Contains("Dep/User.lean:3 (unchanged; imports changed)", ProfileCheck.ToMarkdown(report, dir), StringComparison.Ordinal);
            Assert.True(File.Exists(Path.Combine(dir, ".lake", "leanstudio", "check", report.Commit, "Dep", "Base.lean")));

            // Without the build, only the changed file is checked, and it did not get costlier.
            CheckReport quick = await ProfileCheck.RunAsync(project, new CheckOptions("main", MinDelta: 10, Dependents: 0), null, ct).WaitAsync(Lean.Patience, ct);
            Assert.False(quick.Built);
            Assert.Equal([basePath], quick.Files.Select(f => f.Path));
            Assert.True(quick.Passed);

            // Opened as "dir/" (a path from Finder or a shell can end so), it is the same project: it found no
            // changed file at all, as every path was compared with "dir//".
            var slashed = new LeanProject(dir + Path.DirectorySeparatorChar);
            Assert.Equal(project.Root, slashed.Root);
            CheckReport again = await ProfileCheck.RunAsync(slashed, new CheckOptions("main", MinDelta: 10, Dependents: 0), null, ct).WaitAsync(Lean.Patience, ct);
            Assert.Equal([basePath], again.Files.Select(f => f.Path));
        }
        finally
        {
            Lean.DeleteTree(dir);
        }
    }

    /// <summary>Records progress as it is reported, on the reporting thread (Progress would post it later).</summary>
    private sealed class SyncProgress(List<string> into) : IProgress<string>
    {
        public void Report(string value)
        {
            lock (into)
            {
                into.Add(value);
            }
        }
    }

    [Fact(Timeout = 600_000)]
    public async Task ChecksAChangeAgainstGitFromTheCommandLine()
    {
        Lean.RequireLean();
        Assert.SkipWhen(!GitRepository.IsGitInstalled, "git is not installed");
        var ct = TestContext.Current.CancellationToken;
        string dir = Directory.CreateTempSubdirectory("leanstudio-check").FullName;
        try
        {
            await File.WriteAllTextAsync(Path.Combine(dir, "lean-toolchain"), Lean.Toolchain + "\n", ct);
            string file = Path.Combine(dir, "Check.lean");
            await File.WriteAllTextAsync(file, "theorem t (a b : Nat) : a + b = b + a := by omega\n", ct);
            await GitRepository.InitAsync(dir, ct);
            GitRepository repo = GitRepository.Find(dir)!;
            await repo.RunAsync(["config", "user.email", "test@example.com"], ct: ct);
            await repo.RunAsync(["config", "user.name", "Test"], ct: ct);
            await repo.RunAsync(["config", "commit.gpgsign", "false"], ct: ct);
            await repo.RunAsync(["add", "--all"], ct: ct);
            Assert.True((await repo.RunAsync(["commit", "-m", "base"], ct: ct)).Success);

            // Uncommitted: a costly theorem under a tight limit, and a new file.
            await File.AppendAllTextAsync(file, "\nset_option maxHeartbeats 20000 in\n"
                + "theorem slow (x y z w : Int) (h1 : 3*x + 5*y - 7*z + 11*w = 13) (h2 : 2*x - 9*y + 4*z - w = 8)\n"
                + "    (h3 : x + y + z + w = 1) (h4 : 6*x - 2*y + 3*z - 5*w = 21) : 17*x + 3*y - 2*z + w ≠ 1000 := by\n  omega\n", ct);
            await File.WriteAllTextAsync(Path.Combine(dir, "New.lean"), "theorem n : 2 + 2 = 4 := by decide\n", ct);
            Assert.Equal([file, Path.Combine(dir, "New.lean")], (await repo.ChangedSinceAsync("main", ct)).Order(StringComparer.Ordinal));
            Assert.StartsWith("theorem t", await repo.FileAtAsync("main", file, ct) ?? "", StringComparison.Ordinal);

            string md = Path.Combine(dir, "report.md");
            var output = new StringWriter();
            int code = await ProfileCheck.RunCommandLineAsync(["--profile-check", "--project", dir, "--markdown", md], output, TextWriter.Null, ct)
                .WaitAsync(Lean.Patience, ct);
            Assert.Equal(1, code);
            Assert.Contains("**Failed**: 1 declaration over the limits.", output.ToString(), StringComparison.Ordinal);
            Assert.Contains("| `slow` | Check.lean:4 |", output.ToString(), StringComparison.Ordinal);
            Assert.Contains("of its maxHeartbeats (20,000)", output.ToString(), StringComparison.Ordinal);
            Assert.Equal(output.ToString() + "\n", File.ReadAllText(md));

            // A looser limit passes; a revision git doesn't know cannot run.
            Assert.Equal(0, await ProfileCheck.RunCommandLineAsync(["--profile-check", "--project", dir, "--max-share", "95"], TextWriter.Null, TextWriter.Null, ct).WaitAsync(Lean.Patience, ct));
            var error = new StringWriter();
            Assert.Equal(2, await ProfileCheck.RunCommandLineAsync(["--profile-check", "--project", dir, "--against", "nope"], TextWriter.Null, error, ct));
            Assert.Contains("git has no revision named nope", error.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Lean.DeleteTree(dir);
        }
    }
}

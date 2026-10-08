using LeanStudio.Core.Projects;
using LeanStudio.Core.Toolchains;
using LeanStudio.Core.Workflow;

namespace LeanStudio.Tests;

/// <summary>Trying a newer Lean on a copy of the project (<c>leanstudio --try-upgrade</c>, Lean ▸ Try a Newer Lean).</summary>
public sealed class UpgradeTrialTests
{
    [Theory]
    [InlineData("v4.35.0", "leanprover/lean4:v4.35.0")]
    [InlineData("4.35.0", "leanprover/lean4:v4.35.0")]
    [InlineData("leanprover/lean4:nightly-2026-10-01", "leanprover/lean4:nightly-2026-10-01")]
    [InlineData("stable", "leanprover/lean4:stable")]
    public void NamesAToolchainTheWayLeanDoes(string written, string expected) => Assert.Equal(expected, UpgradeTrial.Normalize(written));

    [Fact]
    public void CopiesTheSourcesButNotTheBuild()
    {
        string root = Directory.CreateTempSubdirectory("leanstudio-copy").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "A", "B"));
            Directory.CreateDirectory(Path.Combine(root, ".lake", "build"));
            Directory.CreateDirectory(Path.Combine(root, ".git"));
            File.WriteAllText(Path.Combine(root, "lean-toolchain"), "leanprover/lean4:v4.34.0\n");
            File.WriteAllText(Path.Combine(root, "A", "B", "C.lean"), "def c := 1\n");
            File.WriteAllText(Path.Combine(root, ".lake", "build", "x.olean"), "");
            File.WriteAllText(Path.Combine(root, ".git", "HEAD"), "");
            // The trial lives inside .lake; copying into it must not copy it into itself.
            string to = Path.Combine(root, ".lake", "leanstudio", "upgrade");
            UpgradeTrial.CopyProject(root, to, TestContext.Current.CancellationToken);
            UpgradeTrial.CopyProject(root, to, TestContext.Current.CancellationToken); // again: emptied first
            Assert.True(File.Exists(Path.Combine(to, "A", "B", "C.lean")));
            Assert.True(File.Exists(Path.Combine(to, "lean-toolchain")));
            Assert.False(Directory.Exists(Path.Combine(to, ".lake")));
            Assert.False(Directory.Exists(Path.Combine(to, ".git")));
        }
        finally
        {
            Lean.DeleteTree(root);
        }
    }

    [Fact]
    public void MeasuresHowFarApartTwoNamesAre()
    {
        Assert.Equal(0, UpgradeTrial.Levenshtein("Nat.add_comm", "Nat.add_comm"));
        Assert.Equal(1, UpgradeTrial.Levenshtein("Nat.add_com", "Nat.add_comm"));
        Assert.Equal(3, UpgradeTrial.Levenshtein("Foo.add_zero_old", "Foo.add_zero_new"));
    }
}

[Collection(Lean.Collection)]
public sealed class UpgradeTrialLeanTests
{
    private static string NewProject(string name, string toolchain, params (string File, string Text)[] files)
    {
        string root = Directory.CreateTempSubdirectory("leanstudio-upgrade").FullName;
        File.WriteAllText(Path.Combine(root, "lean-toolchain"), toolchain + "\n");
        File.WriteAllText(Path.Combine(root, "lakefile.toml"), $"name = \"{name}\"\ndefaultTargets = [\"{name}\"]\n\n[[lean_lib]]\nname = \"{name}\"\n");
        foreach ((string file, string text) in files)
        {
            string path = Path.Combine(root, file);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text);
        }
        return root;
    }

    [Fact]
    public async Task TriesTheNewVersionOnACopyAndAdoptsItOnlyWhenAsked()
    {
        Lean.RequireLean();
        const string old = "leanprover/lean4:v4.33.1";
        // Written for the old version: the guard fails on any other, at a line the report has to point at.
        string root = NewProject("Up", old,
            ("Up.lean", "import Up.Basic\n"),
            ("Up/Basic.lean", "theorem fine : 1 + 1 = 2 := rfl\n\n#guard Lean.versionString == \"4.33.1\"\n"));
        var project = new LeanProject(root);
        try
        {
            UpgradeReport r = await UpgradeTrial.RunAsync(project, new UpgradeOptions(Lean.Toolchain), latestStable: null, ct: TestContext.Current.CancellationToken);
            Assert.Null(r.Problem);
            Assert.True(r.Built);
            Assert.False(r.Clean);
            BuildMessage e = Assert.Single(r.Errors);
            Assert.Equal(Path.Combine(root, "Up", "Basic.lean"), e.Path); // the project's file, not the copy's
            Assert.Equal(2, e.Line);
            Assert.Equal(old, project.Toolchain); // the project was not touched
            Assert.Contains("❌ 1 error in 1 file.", UpgradeTrial.ToMarkdown(r, root), StringComparison.Ordinal);

            IReadOnlyList<string> renamed = await UpgradeTrial.AdoptAsync(project, r, renameDeprecated: true, TestContext.Current.CancellationToken);
            Assert.Empty(renamed);
            Assert.Equal(Lean.Toolchain, project.Toolchain);
            Assert.Equal(old, File.ReadAllText(Path.Combine(UpgradeTrial.BackupFolder(project), "lean-toolchain")).Trim());

            // Nothing to try when the project is already there.
            UpgradeReport same = await UpgradeTrial.RunAsync(project, new UpgradeOptions(Lean.Toolchain), latestStable: null, ct: TestContext.Current.CancellationToken);
            Assert.Contains("already uses", same.Problem, StringComparison.Ordinal);
            var output = new StringWriter();
            Assert.Equal(2, await UpgradeTrial.RunCommandLineAsync(["--try-upgrade", "--project", root, "--to", Lean.Toolchain], output, new StringWriter(), TestContext.Current.CancellationToken));
        }
        finally
        {
            Lean.DeleteTree(root);
        }
    }

    [Fact]
    public async Task SaysWhatAMissingNameBecame()
    {
        Lean.RequireLean();
        const string use = "import Rn.Lemmas\n\ntheorem three : 3 + 0 = 3 := Foo.add_zero_old 3\n";
        string before = NewProject("Rn", Lean.Toolchain,
            ("Rn.lean", "import Rn.Use\n"),
            ("Rn/Lemmas.lean", "theorem Foo.add_zero_old (n : Nat) : n + 0 = n := rfl\n"),
            ("Rn/Use.lean", use));
        // The "new version": the same lemma under a new name, and a near-miss name with a different statement.
        string after = NewProject("Rn", Lean.Toolchain,
            ("Rn.lean", "import Rn.Use\n"),
            ("Rn/Lemmas.lean", "theorem Foo.add_zero_new (n : Nat) : n + 0 = n := rfl\ntheorem Foo.add_zero_odd (n : Nat) : 0 + n = n := Nat.zero_add n\n"),
            ("Rn/Use.lean", use));
        try
        {
            var b = new LeanProject(before);
            var a = new LeanProject(after);
            Assert.True((await Lake.BuildAsync(b, ct: TestContext.Current.CancellationToken)).Success);
            var build = await Lake.BuildAsync(a, ct: TestContext.Current.CancellationToken);
            Assert.False(build.Success);
            List<BuildMessage> errors = LakeOutput.Parse(build.Output, after).Where(m => m.IsError)
                .Select(m => m with { Path = Path.Combine(before, Path.GetRelativePath(after, m.Path)) }).ToList();

            NameSuggestion s = Assert.Single(UpgradeTrial.SuggestNames(b, a, errors, TestContext.Current.CancellationToken));
            Assert.Equal("Foo.add_zero_old", s.Missing);
            Assert.Equal("Foo.add_zero_new", s.Candidates[0]);
            Assert.True(s.SameStatement);
            Assert.Equal(Path.Combine(before, "Rn", "Use.lean"), s.File);
        }
        finally
        {
            Lean.DeleteTree(before);
            Lean.DeleteTree(after);
        }
    }
}

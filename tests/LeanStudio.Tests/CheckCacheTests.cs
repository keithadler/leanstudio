using LeanStudio.Core.Projects;
using LeanStudio.Core.Verification;

namespace LeanStudio.Tests;

/// <summary>Verifying again re-checks only what changed (<see cref="CheckCache"/>).</summary>
[Collection(Lean.Collection)]
public sealed class CheckCacheTests
{
    private const string Lemmas = """
        theorem base (n : Nat) : n + 0 = n := rfl

        theorem other (n : Nat) : 0 + n = n := Nat.zero_add n

        def dbl (n : Nat) : Nat := n + n
        """;

    private const string Uses = """
        import Cache.Lemmas

        theorem usesBase (n : Nat) : n + 0 + 0 = n := (congrArg (· + 0) (base n)).trans (base n)

        theorem usesOther (n : Nat) : 0 + (0 + n) = n := (other (0 + n)).trans (other n)

        theorem alone : 2 + 2 = 4 := rfl

        theorem holey : 1 = 1 := sorry
        """;

    [Fact]
    public async Task ReChecksWhatChangedAndWhatRestsOnIt()
    {
        Lean.RequireLean();
        string root = Directory.CreateTempSubdirectory("leanstudio-cache").FullName;
        File.WriteAllText(Path.Combine(root, "lean-toolchain"), Lean.Toolchain + "\n");
        File.WriteAllText(Path.Combine(root, "lakefile.toml"), "name = \"Cache\"\ndefaultTargets = [\"Cache\"]\n\n[[lean_lib]]\nname = \"Cache\"\n");
        Directory.CreateDirectory(Path.Combine(root, "Cache"));
        File.WriteAllText(Path.Combine(root, "Cache.lean"), "import Cache.Uses\nimport Cache.A\nimport Cache.B\n");
        // Two modules that do not import each other, each unfolding `dbl`: Lean makes `dbl.eq_1` in both, so the
        // project declares that name twice. The cache has to work there too (real projects are full of these).
        File.WriteAllText(Path.Combine(root, "Cache", "A.lean"), "import Cache.Lemmas\n\ntheorem a1 (n : Nat) : dbl n = 2 * n := by rw [dbl]; omega\n");
        File.WriteAllText(Path.Combine(root, "Cache", "B.lean"), "import Cache.Lemmas\n\ntheorem b1 (n : Nat) : dbl n = n + n := by rw [dbl]\n");
        File.WriteAllText(Path.Combine(root, "Cache", "Lemmas.lean"), Lemmas);
        File.WriteAllText(Path.Combine(root, "Cache", "Uses.lean"), Uses);
        var project = new LeanProject(root);
        CancellationToken ct = TestContext.Current.CancellationToken;
        async Task<VerificationReport> VerifyAsync(bool useCache = true)
        {
            var build = await Lake.BuildAsync(project, ct: ct);
            Assert.True(build.Success, build.Output);
            using TenetWorkspace ws = TenetWorkspace.Open(project);
            return await ws.VerifyAsync(ct: ct, useCache: useCache);
        }
        static IEnumerable<string> Verdicts(VerificationReport r) =>
            r.Declarations.Select(d => $"{d.Name} {d.Status} {string.Join(',', d.Assumptions)}").Order(StringComparer.Ordinal);
        try
        {
            VerificationReport first = await VerifyAsync();
            using (TenetWorkspace ws = TenetWorkspace.Open(project))
            {
                Assert.Equal(2, ws.ModulesDeclaring("dbl.eq_1").Count);
            }
            Assert.Equal(0, first.UnitsReused);
            Assert.True(first.UnitsChecked >= 6, $"{first.UnitsChecked} checked, {first.UnitsReused} reused");
            Assert.True(File.Exists(CheckCache.PathFor(root)));

            // Nothing changed: nothing is checked, and every verdict is the same, sorry included.
            VerificationReport second = await VerifyAsync();
            Assert.Equal(0, second.UnitsChecked);
            Assert.Equal(first.UnitsChecked, second.UnitsReused);
            Assert.Equal(Verdicts(first), Verdicts(second));
            Assert.Equal(["sorryAx"], Assert.Single(second.Declarations, d => d.Name == "holey").Assumptions);

            // A new proof of `base` (same statement): `base`, and the theorem that uses it, are checked again;
            // `other`, `usesOther`, `alone` and the rest are not.
            File.WriteAllText(Path.Combine(root, "Cache", "Lemmas.lean"), Lemmas.Replace("n + 0 = n := rfl", "n + 0 = n := Nat.add_zero n", StringComparison.Ordinal));
            VerificationReport third = await VerifyAsync();
            Assert.True(third.UnitsChecked >= 2, $"{third.UnitsChecked} re-checked");
            Assert.True(third.UnitsChecked < first.UnitsChecked / 2, $"{third.UnitsChecked} re-checked of {first.UnitsChecked}");
            Assert.Equal(first.UnitsChecked, third.UnitsChecked + third.UnitsReused);
            Assert.Equal(Verdicts(first), Verdicts(third));

            // Asked to, it checks everything.
            VerificationReport fresh = await VerifyAsync(useCache: false);
            Assert.Equal(0, fresh.UnitsReused);
            Assert.Equal(Verdicts(first), Verdicts(fresh));
        }
        finally
        {
            Lean.DeleteTree(root);
        }
    }
}

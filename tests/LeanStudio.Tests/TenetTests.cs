using LeanStudio.Core.Projects;
using LeanStudio.Core.Verification;

namespace LeanStudio.Tests;

[Collection(Lean.Collection)]
public sealed class TenetTests
{
    private static readonly SemaphoreSlim BuildLock = new(1, 1);

    private static async Task<LeanProject> BuiltProofsAsync()
    {
        var p = new LeanProject(Lean.Sample("Proofs"));
        await BuildLock.WaitAsync(TestContext.Current.CancellationToken);
        try
        {
            if (p.OleanOf(Path.Combine(p.Root, "Proofs", "Basic.lean")) is null)
            {
                var r = await Lake.BuildAsync(p, ct: TestContext.Current.CancellationToken);
                Assert.True(r.Success, r.Output);
            }
        }
        finally
        {
            BuildLock.Release();
        }
        return p;
    }

    [Fact]
    public async Task SortsDeclarationsIntoVerifiedConditionalAndRejected()
    {
        Lean.RequireLean();
        using TenetWorkspace ws = TenetWorkspace.Open(await BuiltProofsAsync());
        Assert.Contains(ws.OwnModules, m => m.ToString() == "Proofs.Basic");

        VerificationReport r = await ws.VerifyAsync(ct: TestContext.Current.CancellationToken);
        Assert.Equal(0, r.Rejected);
        DeclarationVerdict V(string n) => Assert.Single(r.Declarations, d => d.Name == n);
        Assert.Equal(VerificationStatus.Verified, V("double_eq_two_mul").Status);
        Assert.Equal(VerificationStatus.Verified, V("and_swap").Status);
        Assert.Equal(["em'"], V("not_not_elim").Assumptions);
        Assert.Equal(["sorryAx"], V("unfinished").Assumptions);
        Assert.Equal(4, V("double_eq_two_mul").Line);
        Assert.Equal(2, V("double").Line); // past its doc comment, on the `def`
    }

    [Fact]
    public async Task ASorryBehindAStructureFieldIsNotVerified()
    {
        // A structure's fields live on its constructor, not on the structure, and nothing in the structure's own
        // type mentions its constructor. A walk that only follows types never reaches a field, so a theorem that
        // takes a `Holed` got a green check while `Holed` cannot be built without sorry. That was a real bug in
        // Tenet's axiom walk (keithadler/tenet 54dbb20). This pins it from Lean Studio's side, so a submodule bump
        // that brought it back fails here instead of putting ✓ on a proof that rests on sorry.
        Lean.RequireLean();
        string root = Directory.CreateTempSubdirectory("leanstudio-holes").FullName;
        File.WriteAllText(Path.Combine(root, "lean-toolchain"), Lean.Toolchain + "\n");
        File.WriteAllText(Path.Combine(root, "lakefile.toml"),
            "name = \"Holes\"\ndefaultTargets = [\"Holes\"]\n\n[[lean_lib]]\nname = \"Holes\"\n");
        File.WriteAllText(Path.Combine(root, "Holes.lean"), """
            def holed : Nat := sorry

            structure Holed where
              y : Fin (holed + 1)

            -- Mentions the type only: not sorry, not the constructor, not `holed`.
            theorem usesHoled (h : Holed) : h = h := rfl

            structure Clean where
              x : Nat

            theorem usesClean (c : Clean) : c = c := rfl
            """);
        var project = new LeanProject(root);
        try
        {
            var build = await Lake.BuildAsync(project, ct: TestContext.Current.CancellationToken);
            Assert.True(build.Success, build.Output);
            using TenetWorkspace ws = TenetWorkspace.Open(project);

            VerificationReport r = await ws.VerifyAsync(ct: TestContext.Current.CancellationToken);
            DeclarationVerdict V(string n) => Assert.Single(r.Declarations, d => d.Name == n);
            Assert.Equal(VerificationStatus.RestsOnAssumption, V("usesHoled").Status);
            Assert.Equal(["sorryAx"], V("usesHoled").Assumptions);
            Assert.Contains("sorryAx", ws.AxiomsOf("Holed"));

            // And "Why?" has to lead somewhere a person can fix: the chain ends at `holed`, the declaration that
            // uses sorry, not at the structure that merely carries it.
            AssumptionTrail trail = Assert.Single(ws.WhyNotProved("usesHoled", TestContext.Current.CancellationToken));
            Assert.True(trail.IsSorry);
            Assert.Equal("holed", trail.Culprit?.Name);

            // The other half, without which the rest proves nothing: a walk that reported everything would pass.
            Assert.Equal(VerificationStatus.Verified, V("usesClean").Status);
            Assert.Empty(ws.AxiomsOf("Clean"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task TheNavigatorReadsStatementsDocsAndAxioms()
    {
        Lean.RequireLean();
        using TenetWorkspace ws = TenetWorkspace.Open(await BuiltProofsAsync());
        DeclarationDetails? d = ws.Details("double");
        Assert.NotNull(d);
        Assert.Equal("def", d.Kind);
        Assert.Equal("Proofs.Basic", d.Module);
        Assert.Contains("long way round", d.DocString, StringComparison.Ordinal);
        Assert.Equal(Lean.Sample("Proofs", "Proofs", "Basic.lean"), d.SourceFile);

        Assert.Equal(["em'"], ws.AxiomsOf("not_not_elim"));
        Assert.Contains("double_eq_two_mul", ws.UsedBy("double", ct: TestContext.Current.CancellationToken));
        Assert.Contains(ws.Search("Nat.add_comm", ct: TestContext.Current.CancellationToken), s => s.Name == "Nat.add_comm");
    }
}

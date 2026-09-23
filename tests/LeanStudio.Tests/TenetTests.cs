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

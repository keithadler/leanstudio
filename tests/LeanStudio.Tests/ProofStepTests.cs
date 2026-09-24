using LeanStudio.Core.Proofs;
using LeanStudio.Lsp;

namespace LeanStudio.Tests;

[Collection(Lean.Collection)]
public sealed class ProofStepTests
{
    private static readonly string[] Demo = File.ReadAllLines(Lean.Sample("Demo", "Demo.lean"));

    [Fact]
    public void FindsTheTacticLinesOfTheEnclosingProof()
    {
        TacticProof? p = ProofSteps.Find(Demo, 4);
        Assert.NotNull(p);
        Assert.Equal("add_comm'", p.Declaration);
        Assert.Equal(["induction a with", "| zero => simp", "| succ n ih =>", "rw [Nat.succ_add]", "omega"], p.Steps.Select(s => s.Text));
        Assert.Equal(new Position(0, 51), p.Start);
    }

    [Fact]
    public void ExampleIsADeclarationToo()
    {
        TacticProof? p = ProofSteps.Find(Demo, 9);
        Assert.NotNull(p);
        Assert.Equal("example", p.Declaration);
        Assert.Equal(3, p.Steps.Count);
    }

    [Fact]
    public void AOneLineProofHasOneStep()
    {
        TacticProof? p = ProofSteps.Find(Demo, 12);
        Assert.NotNull(p);
        Assert.Equal("sorry", Assert.Single(p.Steps).Text);
    }

    [Fact]
    public void OutsideAnyDeclarationThereIsNoProof()
    {
        string[] lines = ["namespace Foo", "", "theorem t : True := by", "  trivial", "end Foo"];
        Assert.Null(ProofSteps.Find(lines, 0));
        Assert.Null(ProofSteps.Find(lines, 4));
        Assert.NotNull(ProofSteps.Find(lines, 3));
    }

    [Fact]
    public void CommentsAreNotSteps()
    {
        string[] lines = ["theorem t (p : Prop) (h : p) : p := by", "  -- just use it", "  /- a", "  block -/", "  exact h -- done"];
        TacticProof? p = ProofSteps.Find(lines, 4);
        Assert.NotNull(p);
        Assert.Equal("exact h", Assert.Single(p.Steps).Text);
    }

    [Fact]
    public async Task EveryStepHasAStateAndTheChangesAddUp()
    {
        Lean.RequireLean();
        string dir = Lean.Sample("Demo");
        await using var server = new LeanServer(new LeanServerCommand(Lean.Executable!, ["--server"], dir));
        string uri = LeanServer.UriOf(Path.Combine(dir, "Demo.lean"));
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        server.FileProgress += (u, p) =>
        {
            if (u == uri && p.Count == 0)
            {
                done.TrySetResult();
            }
        };
        await server.StartAsync(TestContext.Current.CancellationToken);
        await server.OpenAsync(uri, string.Join('\n', Demo));
        await done.Task.WaitAsync(Lean.Patience, TestContext.Current.CancellationToken);

        TacticProof p = ProofSteps.Find(Demo, 9)!; // constructor; · exact hp; · exact hq
        InteractiveGoals before = await server.InteractiveGoalsAsync(uri, p.Start, TestContext.Current.CancellationToken);
        Assert.Single(before.Goals);
        var changes = new List<StepChange>();
        foreach (ProofStep s in p.Steps)
        {
            InteractiveGoals after = await server.InteractiveGoalsAsync(uri, s.After, TestContext.Current.CancellationToken);
            changes.Add(StepChange.Between(before, after));
            before = after;
        }
        Assert.Equal(1, changes[0].GoalsBefore);
        Assert.Equal(2, changes[0].GoalsAfter);
        Assert.True(changes[^1].ClosedAll, changes[^1].Summary);
    }

    [Fact]
    public async Task StepsSayWhatTheyAddedRemovedAndChanged()
    {
        Lean.RequireLean();
        string[] lines =
        [
            "theorem steps (p q : Prop) (x : Nat) (h : p ∧ q) (hx : x = 1) : q ∧ x = 1 := by",
            "  obtain ⟨hp, hq⟩ := h",
            "  rw [← Nat.add_zero x] at hx",
            "  constructor",
            "  · exact hq",
            "  · simpa using hx",
        ];
        string dir = Lean.Sample("Demo");
        await using var server = new LeanServer(new LeanServerCommand(Lean.Executable!, ["--server"], dir));
        string uri = LeanServer.UriOf(Path.Combine(dir, "Steps.lean"));
        var ct = TestContext.Current.CancellationToken;
        await server.StartAsync(ct);
        await server.OpenAsync(uri, string.Join('\n', lines));
        await server.WaitForElaborationAsync(uri, ct).WaitAsync(Lean.Patience, ct);
        Assert.DoesNotContain(server.DiagnosticsOf(uri), d => d.Severity == DiagnosticSeverity.Error);

        TacticProof proof = ProofSteps.Find(lines, 1)!;
        InteractiveGoals before = await server.InteractiveGoalsAsync(uri, proof.Start, ct);
        var summaries = new List<string>();
        foreach (ProofStep step in proof.Steps.Take(3))
        {
            InteractiveGoals after = await server.InteractiveGoalsAsync(uri, step.After, ct);
            summaries.Add(StepChange.Between(before, after).Summary);
            before = after;
        }
        Assert.Equal("+hp  +hq  −h", summaries[0]);
        Assert.Equal("~hx", summaries[1]);
        Assert.Equal("+1 goal", summaries[2]);

        // In the Tactic State: after `obtain`, hp and hq are marked as added; before it, h as removed.
        InteractiveGoal afterObtain = Assert.Single((await server.InteractiveGoalsAsync(uri, proof.Steps[0].After, ct)).Goals);
        Assert.Contains(afterObtain.Hypotheses, x => x.Names.Contains("hp") && x.IsInserted);
        Assert.DoesNotContain(afterObtain.Hypotheses, x => x.Names.Contains("x") && x.IsInserted);
        InteractiveGoal beforeObtain = Assert.Single((await server.InteractiveGoalsAsync(uri, new Position(1, 2), ct)).Goals);
        Assert.Contains(beforeObtain.Hypotheses, x => x.Names.Contains("h") && x.IsRemoved);
    }
}

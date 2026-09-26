using System.Text.Json;
using LeanStudio.Core.Proofs;
using LeanStudio.Lsp;

namespace LeanStudio.Tests;

[Collection(Lean.Collection)]
public sealed class ProofStateTests
{
    private static StateGoal G(string target, params (string Names, string Type)[] hyps) =>
        new(hyps.Select(h => new StateHypothesis(h.Names.Split(' '), h.Type)).ToList(), target);

    private static string Key(StateMatch m, params StateGoal[] goals) => ProofStates.Key(goals, m);

    [Fact]
    public void LocalNamesDoNotMatterButTheirRolesDo()
    {
        Assert.Equal(Key(StateMatch.Exact, G("n + 0 = n", ("n", "ℕ"))), Key(StateMatch.Exact, G("m + 0 = m", ("m", "ℕ"))));
        // the same letters in swapped roles are a different state
        Assert.NotEqual(Key(StateMatch.Exact, G("a ≤ b", ("a b", "ℕ"))), Key(StateMatch.Exact, G("b ≤ a", ("a b", "ℕ"))));
    }

    [Fact]
    public void RenamingRespectsIdentifierBoundaries()
    {
        string k = Key(StateMatch.Exact, G("n.succ = n' + Nat.n", ("n n'", "ℕ")));
        Assert.EndsWith("⊢ v0.succ = v1 + Nat.n", k, StringComparison.Ordinal);
    }

    [Fact]
    public void EachLevelIsLooserThanTheLast()
    {
        StateGoal a = G("n + 2 ≤ n * 5", ("n", "ℕ"), ("h", "0 < n"));
        StateGoal b = G("k + 3 ≤ k * 7", ("k", "ℕ"));
        StateGoal c = G("n + 2 ≤ n * 5", ("n", "ℕ"));
        Assert.NotEqual(Key(StateMatch.Exact, a), Key(StateMatch.Exact, c)); // the hypothesis h differs
        Assert.Equal(Key(StateMatch.Goal, a), Key(StateMatch.Goal, c));
        Assert.NotEqual(Key(StateMatch.Goal, a), Key(StateMatch.Goal, b)); // different numbers
        Assert.Equal(Key(StateMatch.Shape, a), Key(StateMatch.Shape, b));
        Assert.Equal("⊢ _ + # ≤ _ * #", Key(StateMatch.Shape, b));
    }

    [Fact]
    public void NoGoalsIsItsOwnState()
    {
        Assert.Equal(ProofStates.NoGoals, ProofStates.Key([], StateMatch.Exact));
    }

    [Theory]
    [InlineData("False", true)]
    [InlineData("True", true)]
    [InlineData("0 = 0", true)]
    [InlineData("f x = f x", true)]
    [InlineData("2 < 5", true)]
    [InlineData("0 < n", false)]
    [InlineData("n + 0 = n", false)]
    [InlineData("a ∧ b", false)]
    public void TrivialGoalsAreNotWorthALemma(string target, bool trivial) =>
        Assert.Equal(trivial, ProofStates.IsTrivial(G(target)));

    [Fact]
    public void AStateTwoProofsPassThroughIsShared()
    {
        StateGoal start1 = G("p ∧ q", ("p q", "Prop"), ("hp", "p"), ("hq", "q"));
        StateGoal start2 = G("a ∧ b", ("a b", "Prop"), ("ha", "a"), ("hb", "b"));
        StateStep[] steps =
        [
            new("A.lean", "t1", 1, 2, "constructor", [start1], [G("p", ("p q", "Prop")), G("q", ("p q", "Prop"))]),
            new("A.lean", "t2", 5, 2, "refine ⟨?_, ?_⟩", [start2], []),
            new("A.lean", "t3", 9, 2, "exfalso", [G("False", ("h", "False"))], []),
            new("A.lean", "t4", 12, 2, "exfalso", [G("False", ("h", "False"))], []),
        ];
        ProofStateMap map = ProofStateMap.Build(steps, StateMatch.Exact);
        StateNode shared = Assert.Single(map.Shared); // False is reached twice too, but it is trivial
        Assert.Equal(["A.lean · t1", "A.lean · t2"], shared.Declarations);
        Assert.Equal(2, shared.Visits.Count);
        Assert.Contains("⊢ p ∧ q", shared.Example, StringComparison.Ordinal);
        Assert.Contains(map.Nodes, n => n.Done);

        // the view leaves out the no-goals state and every step into it
        using JsonDocument view = JsonDocument.Parse(JsonSerializer.Serialize(map.ToView()));
        Assert.DoesNotContain(view.RootElement.GetProperty("nodes").EnumerateArray(), n => n.GetProperty("text").GetString() == "no goals");
        Assert.Equal(shared.Id, Assert.Single(view.RootElement.GetProperty("shared").EnumerateArray()).GetInt32());
    }

    [Fact]
    public async Task CollectsStatesFromLeanAndFindsTheOnesProofsShare()
    {
        Lean.RequireLean();
        string dir = Directory.CreateTempSubdirectory("leanstudio-states").FullName;
        try
        {
            string path = Path.Combine(dir, "Shared.lean");
            string text = """
                theorem t1 (p q : Prop) (hp : p) (hq : q) : p ∧ q := by
                  constructor
                  · exact hp
                  · exact hq

                theorem t2 (a b : Prop) (ha : a) (hb : b) : a ∧ b := by
                  refine ⟨?_, ?_⟩
                  · exact ha
                  · exact hb

                """.ReplaceLineEndings("\n"); // a Windows checkout gives the literal CRLF lines
            await File.WriteAllTextAsync(path, text, TestContext.Current.CancellationToken);
            var server = new LeanServer(new LeanServerCommand(Lean.Executable!, ["--server"], dir));
            await using (server)
            {
                await server.StartAsync(TestContext.Current.CancellationToken);
                string uri = LeanServer.UriOf(path);
                await server.OpenAsync(uri, text);
                await server.WaitForElaborationAsync(uri, TestContext.Current.CancellationToken).WaitAsync(Lean.Patience, TestContext.Current.CancellationToken);
                IReadOnlyList<StateStep> steps = await ProofStates.CollectAsync(server, uri, "Shared.lean", text.Split('\n'), TestContext.Current.CancellationToken);

                Assert.Equal(["constructor", "· exact hp", "· exact hq", "refine ⟨?_, ?_⟩", "· exact ha", "· exact hb"], steps.Select(s => s.Tactic));
                Assert.Equal(2, steps[0].After!.Count); // constructor splits the conjunction

                // Written differently, both proofs start in the same state and split into the same two goals.
                ProofStateMap exact = ProofStateMap.Build(steps, StateMatch.Exact);
                Assert.Contains(exact.Shared, n => n.Example.Contains("⊢ p ∧ q", StringComparison.Ordinal)
                                                   && n.Declarations.SequenceEqual(["Shared.lean · t1", "Shared.lean · t2"]));
                Assert.Contains(exact.Shared, n => n.Key.Split("\n\n").Length == 2); // the two goals after the split

                using JsonDocument view = JsonDocument.Parse(ProofStates.ViewJson(steps));
                Assert.Equal(["exact", "goal", "shape"], view.RootElement.EnumerateObject().Select(p => p.Name));

                // Extracting the state both proofs start from: Lean writes its lemma from the goal at the probe.
                StateNode start = exact.Shared.First(n => n.Example.Contains("⊢ p ∧ q", StringComparison.Ordinal));
                (string probe, SorrySite site) = ProofStates.ProbeAt(text, start.Visits[0]);
                Assert.Equal("sorry", probe.Substring(site.Offset, site.Length));
                Assert.StartsWith("theorem t1 (p q : Prop) (hp : p) (hq : q) : p ∧ q := by\n  sorry\n  constructor", probe, StringComparison.Ordinal);
                ExtractedLemma? lemma = await ExtractLemma.RunAsync(server, path, probe, site, "and_intro_step", TestContext.Current.CancellationToken);
                Assert.NotNull(lemma);
                Assert.StartsWith("theorem and_intro_step", lemma.Text, StringComparison.Ordinal);
                Assert.Contains("p ∧ q", lemma.Text, StringComparison.Ordinal);
                Assert.Equal(0, lemma.InsertLine); // above t1
            }
        }
        finally
        {
            Lean.DeleteTree(dir);
        }
    }
}

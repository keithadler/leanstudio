using LeanStudio.Core.Proofs;
using LeanStudio.Lsp;

namespace LeanStudio.Tests;

/// <summary>Prove It, why not proved, the performance heat map, walkthroughs and LeanSearch, against real Lean.</summary>
[Collection(Lean.Collection)]
public sealed class AssistTests
{
    private const string Goals = """
        /-- A comment with sorry in it, which is not a sorry. -/
        theorem comm (a b : Nat) : a + b = b + a := by
          sorry

        theorem conj (p q : Prop) (hp : p) (hq : q) : p ∧ q := sorry

        theorem lt (n : Nat) : n < n + 1 := by
          have h : n ≤ n := sorry
          omega

        theorem hard (f : Nat → Nat) : f 1 = f 2 := by sorry
        """;

    [Fact]
    public void FindsSorriesInCodeOnly()
    {
        IReadOnlyList<SorrySite> sites = ProofSearch.Sites(Goals);
        Assert.Equal([2, 4, 7, 10], sites.Select(s => s.Line));
        Assert.Equal(["comm", "conj", "lt", "hard"], sites.Select(s => s.Declaration));
        Assert.Equal(7, ProofSearch.At(sites, 6, 0)!.Line);
        Assert.Equal(4, ProofSearch.At(sites, 4, 3)!.Line);
    }

    [Fact]
    public void InstrumentsAfterTheImports()
    {
        string text = "/-! module doc -/\nimport Std\n-- note\nimport Lean.Elab\n\ntheorem t : True := sorry\n";
        IReadOnlyList<SorrySite> sites = ProofSearch.Sites(text);
        string inst = ProofSearch.Instrument(text, sites);
        Assert.StartsWith("/-! module doc -/\nimport Std\n-- note\nimport Lean.Elab\nimport Lean\nopen Lean Elab", inst);
        Assert.Contains("theorem t : True := leanstudio_try 0", inst);
        Assert.Contains("import Lean\n", ProofSearch.Instrument("theorem t : True := sorry", ProofSearch.Sites("theorem t : True := sorry")));
    }

    [Fact]
    public void ParsesTrialsAndFillsSorries()
    {
        string text = "theorem a : 1 = 1 := sorry\ntheorem b (n : Nat) : n = n := by\n  sorry\n";
        IReadOnlyList<SorrySite> sites = ProofSearch.Sites(text);
        string[] portfolio = ["rfl", "linarith", "exact?"];
        var results = ProofSearch.Parse(
        [
            "⟪leanstudio⟫\t0\tterm\n0\tok\t3\t\n1\tunavailable\t0\t\n2\tok\t900\trfl",
            "⟪leanstudio⟫\t1\ttactic\n0\tfail\t1\t\n1\tunavailable\t0\t\n2\tok\t1200\tNat.le_refl  n",
            "unrelated message",
        ], sites, portfolio);
        Assert.True(results[0].TermMode);
        Assert.Equal("rfl", results[0].Best!.Replacement);
        Assert.Equal(TrialOutcome.Unavailable, results[0].Trials[1].Outcome);
        Assert.Equal("exact Nat.le_refl n", results[1].Best!.Replacement);
        string filled = ProofSearch.Apply(text, results.Select(r => (r, r.Best!)));
        Assert.Equal("theorem a : 1 = 1 := by rfl\ntheorem b (n : Nat) : n = n := by\n  exact Nat.le_refl n\n", filled);
    }

    private static async Task<LeanServer> StartAsync(string dir)
    {
        var server = new LeanServer(new LeanServerCommand(Lean.Executable!, ["--server"], dir));
        await server.StartAsync(TestContext.Current.CancellationToken);
        return server;
    }

    [Fact]
    public async Task ProveItClosesWhatItCanAndSaysWhatItCannot()
    {
        Lean.RequireLean();
        string dir = Lean.Sample("Demo");
        await using LeanServer server = await StartAsync(dir);
        IReadOnlyList<SorrySite> sites = ProofSearch.Sites(Goals);
        IReadOnlyList<SearchResult> results = await ProofSearch.RunAsync(server, Path.Combine(dir, "Goals.lean"), Goals, sites, TestContext.Current.CancellationToken)
            .WaitAsync(Lean.Patience, TestContext.Current.CancellationToken);

        Assert.All(results, r => Assert.True(r.Reached, $"sorry at {r.Site.Where} was not reached"));
        Assert.Contains(results[0].Successes, t => t.Tactic == "omega");
        Assert.False(results[0].TermMode);
        Assert.True(results[1].TermMode);
        Assert.NotNull(results[1].Best);
        Assert.NotNull(results[2].Best);
        Assert.Null(results[3].Best); // f 1 = f 2 is not provable
        Assert.Contains(results[0].Trials, t => t.Tactic == "linarith" && t.Outcome == TrialOutcome.Unavailable); // no Mathlib here

        // Filling every sorry that has a proof leaves a file Lean accepts except for the one it could not prove.
        string filled = ProofSearch.Apply(Goals, results.Where(r => r.Best is not null).Select(r => (r, r.Best!)));
        IReadOnlyList<Diagnostic> diags = await Scratch.CheckAsync(server, Path.Combine(dir, "Goals.lean"), "Filled", filled, TestContext.Current.CancellationToken)
            .WaitAsync(Lean.Patience, TestContext.Current.CancellationToken);
        Assert.DoesNotContain(diags, d => d.Severity == DiagnosticSeverity.Error);
        Diagnostic sorry = Assert.Single(diags, d => d.Message.Contains("sorry", StringComparison.Ordinal));
        Assert.Equal(10, sorry.Range.Start.Line);
    }

    [Fact]
    public async Task ProfilerFindsTheSlowDeclaration()
    {
        Lean.RequireLean();
        string dir = Lean.Sample("Demo");
        string text = """
            def fib : Nat → Nat
              | 0 => 0
              | 1 => 1
              | n + 2 => fib n + fib (n + 1)

            theorem quick (a b : Nat) : a + b = b + a := Nat.add_comm a b

            theorem slow (x y z w : Int) (h1 : 3*x + 5*y - 7*z + 11*w = 13) (h2 : 2*x - 9*y + 4*z - w = 8)
                (h3 : x + y + z + w = 1) (h4 : 6*x - 2*y + 3*z - 5*w = 21) : 17*x + 3*y - 2*z + w ≠ 1000 := by
              omega
            """;
        var project = new Core.Projects.LeanProject(dir);
        try
        {
            var (timings, error) = await Profiler.RunAsync(project, Path.Combine(dir, "Slow.lean"), text, TestContext.Current.CancellationToken)
                .WaitAsync(Lean.Patience, TestContext.Current.CancellationToken);
            Assert.Null(error);
            DeclarationTiming top = timings[0];
            Assert.Equal(7, top.Line);
            Assert.Contains("slow", top.Declaration, StringComparison.Ordinal);
            Assert.Contains("omega", top.HotSpot ?? "", StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(Path.Combine(dir, ".lake"), true);
        }
    }

    [Fact]
    public void ProfilerReadsNestedTraces()
    {
        string trace = "[Elab.command] [0.500000] ✅️ theorem t : x := by\n    simp\n    omega\n  [Elab.step] [0.450000] ✅️ simp\n    omega\n    [Elab.step] [0.400000] ✅️ omega\n  [Meta.synthInstance] [0.010000] ✅️ Foo";
        var timings = Profiler.Parse([(3, trace), (5, "[Elab.async] [0.25] ✅️ checking\n  [Kernel] [0.2] ✅️ typechecking declarations [t._proof_1]"), (9, "not a trace")],
            ["", "", "", "theorem t : x := by", "  simp", "  omega"]);
        DeclarationTiming t = Assert.Single(timings);
        Assert.Equal(0.75, t.Seconds, 3);
        Assert.Equal("omega", t.HotSpot);
        Assert.Equal(1, t.Heat);
    }

    [Fact]
    public void ReadsLeanSearchAnswers()
    {
        using var doc = System.Text.Json.JsonDocument.Parse("""
            [[{"result":{"module_name":["Mathlib","Algebra","BigOperators","Intervals"],"kind":"theorem","name":["Finset","sum_range_id"],
               "signature":"(n : ℕ) : ∑ i ∈ range n, i = n * (n - 1) / 2","type":"∀ (n : ℕ), ∑ i ∈ Finset.range n, i = n * (n - 1) / 2",
               "informal_name":"Sum of Natural Numbers","informal_description":"The sum of 0 to n-1 is n(n-1)/2."},"distance":0.25},
              {"result":{"name":[]},"distance":0.3}]]
            """);
        var hits = Core.Workflow.LeanSearch.Parse(doc.RootElement);
        var h = Assert.Single(hits);
        Assert.Equal("Finset.sum_range_id", h.Name);
        Assert.Equal("Mathlib.Algebra.BigOperators.Intervals", h.Module);
        Assert.StartsWith("∀ (n : ℕ)", h.Type, StringComparison.Ordinal);
        Assert.Equal(75, h.Relevance);
        Assert.Equal("Sum of Natural Numbers", h.InformalName);
    }

    [Fact]
    public void ShareLinksOpenTheCodeInTheWebEditor()
    {
        string url = Walkthrough.ShareUrl("theorem t : 1 + 1 = 2 := by rfl -- (a & b)");
        Assert.StartsWith("https://live.lean-lang.org/#code=theorem%20t%20%3A%201%20%2B%201", url, StringComparison.Ordinal);
        Assert.DoesNotContain("&", url[url.IndexOf('#', StringComparison.Ordinal)..], StringComparison.Ordinal);
        Assert.Equal("theorem t : 1 + 1 = 2 := by rfl -- (a & b)", Uri.UnescapeDataString(url[(url.IndexOf("#code=", StringComparison.Ordinal) + 6)..]));
    }

    [Fact]
    public async Task WalkthroughFollowsEveryProofStepByStep()
    {
        Lean.RequireLean();
        string dir = Lean.Sample("Demo");
        string path = Path.Combine(dir, "Demo.lean");
        string text = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);
        string[] lines = text.Split('\n');
        Assert.Equal(["add_comm'", "example", "bad"], Walkthrough.Proofs(lines).Select(p => p.Declaration));

        await using LeanServer server = await StartAsync(dir);
        string uri = LeanServer.UriOf(path);
        await server.OpenAsync(uri, text);
        await server.WaitForElaborationAsync(uri, TestContext.Current.CancellationToken).WaitAsync(Lean.Patience, TestContext.Current.CancellationToken);
        IReadOnlyList<WalkProof> proofs = await Walkthrough.BuildAsync(server, uri, lines, TestContext.Current.CancellationToken);

        WalkProof ex = proofs[1];
        Assert.Contains("p ∧ q", ex.Statement, StringComparison.Ordinal);
        Assert.Contains(ex.GoalsBefore, g => g.Contains("⊢ p ∧ q", StringComparison.Ordinal));
        Assert.Equal(["constructor", "· exact hp", "· exact hq"], ex.Steps.Select(s => s.Tactic));
        Assert.Equal(2, ex.Steps[0].GoalsAfter.Count); // constructor splits the conjunction
        Assert.Equal("goals accomplished", ex.Steps[^1].Change);
        Assert.NotNull(ex.Steps[0].Explanation);

        string html = Walkthrough.Html("Demo.lean", proofs, text);
        Assert.Contains("<title>Demo.lean · proof walkthrough</title>", html, StringComparison.Ordinal);
        Assert.Contains("⊢ p ∧ q", html, StringComparison.Ordinal);
        Assert.Contains("live.lean-lang.org/#code=", html, StringComparison.Ordinal);
        Assert.Contains("@keithadler", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<script src", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TenetTracesASorryThroughTheLemmasThatUseIt()
    {
        Lean.RequireLean();
        string root = Directory.CreateTempSubdirectory("leanstudio-trail").FullName;
        try
        {
            File.WriteAllText(Path.Combine(root, "lean-toolchain"), Lean.Toolchain + "\n");
            File.WriteAllText(Path.Combine(root, "lakefile.toml"), "name = \"Trail\"\ndefaultTargets = [\"Trail\"]\n\n[[lean_lib]]\nname = \"Trail\"\n");
            File.WriteAllText(Path.Combine(root, "Trail.lean"), """
                theorem base (n : Nat) : n + 0 = n := by
                  sorry

                theorem middle (n : Nat) : n + 0 = n ∧ True := ⟨base n, trivial⟩

                theorem top (n : Nat) : (n + 0 = n ∧ True) ∧ True := by
                  exact ⟨middle n, trivial⟩

                axiom magic : 2 + 2 = 5

                theorem uses_magic : 2 + 2 = 5 := magic

                theorem fine (n : Nat) : n + 0 = n := rfl
                """);
            var project = new Core.Projects.LeanProject(root);
            var build = await Core.Projects.Lake.BuildAsync(project, ct: TestContext.Current.CancellationToken);
            Assert.True(build.Success, build.Output);
            using var ws = Core.Verification.TenetWorkspace.Open(project);

            var trails = ws.WhyNotProved("top", TestContext.Current.CancellationToken);
            var t = Assert.Single(trails);
            Assert.True(t.IsSorry);
            Assert.Equal(["top", "middle", "base", "sorry"], t.Path.Select(l => l.Display));
            Assert.Equal("base", t.Culprit!.Name);
            Assert.Equal(1, t.Culprit.Line);
            Assert.Equal(Path.Combine(root, "Trail.lean"), t.Culprit.SourceFile);

            var m = Assert.Single(ws.WhyNotProved("uses_magic", TestContext.Current.CancellationToken));
            Assert.Equal("magic", m.Assumption);
            Assert.Equal(["uses_magic", "magic"], m.Path.Select(l => l.Display));
            Assert.Empty(ws.WhyNotProved("fine", TestContext.Current.CancellationToken));

            Assert.Equal("foo", Core.Verification.TenetWorkspace.UserFacingOwner("foo._proof_1"));
            Assert.Equal("Bar.foo", Core.Verification.TenetWorkspace.UserFacingOwner("_private.Mod.A.0.Bar.foo.match_1"));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}

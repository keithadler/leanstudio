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
        Assert.StartsWith("/-! module doc -/\nimport Std\n-- note\nimport Lean.Elab\nimport Lean\nopen Lean", inst);
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
    public void ReadsCounterexamples()
    {
        IReadOnlyList<SorrySite> sites = ProofSearch.Sites("theorem w (n : Nat) : n < 3 := sorry");
        var r = Assert.Single(ProofSearch.Parse(["⟪leanstudio⟫\t0\tterm\n0\tfail\t1\t\ncex\tn = 3"], sites, ["rfl"]));
        Assert.Null(r.Best);
        Assert.Equal("n = 3", r.Counterexample);
    }

    [Fact]
    public async Task ProveItSaysWhenAGoalIsFalse()
    {
        Lean.RequireLean();
        const string text = """
            theorem wrong (n : Nat) (h : n > 2) : n * n < 10 := by
              sorry

            theorem wrongBool : ∀ a b : Bool, (a && b) = (a || b) := by
              sorry

            theorem right (x : Int) (h : x > 3) : 2 * x > 6 := by
              sorry
            """;
        string dir = Lean.Sample("Demo");
        await using LeanServer server = await StartAsync(dir);
        IReadOnlyList<SearchResult> results = await ProofSearch.RunAsync(server, Path.Combine(dir, "Wrong.lean"), text, ProofSearch.Sites(text), TestContext.Current.CancellationToken)
            .WaitAsync(Lean.Patience, TestContext.Current.CancellationToken);
        Assert.Null(results[0].Best);
        Assert.Equal("n = 4", results[0].Counterexample); // 3 * 3 = 9 is still below 10
        Assert.Null(results[1].Best);
        Assert.Equal("a = false, b = true", results[1].Counterexample);
        Assert.Null(results[2].Counterexample); // true (and proved by omega)
        Assert.NotNull(results[2].Best);

        // An Int variable: the counterexample can be negative or zero.
        const string ints = "theorem sq (x : Int) (h : x < 1) : x * x > 0 := by\n  sorry\n";
        IReadOnlyList<SearchResult> intResults = await ProofSearch.RunAsync(server, Path.Combine(dir, "Wrong.lean"), ints, ProofSearch.Sites(ints), TestContext.Current.CancellationToken)
            .WaitAsync(Lean.Patience, TestContext.Current.CancellationToken);
        Assert.Null(intResults[0].Best);
        Assert.Equal("x = 0", intResults[0].Counterexample);
    }

    [Fact]
    public async Task ExactQuestionsAnswerGoesIntoTheProof()
    {
        // Lean's real "Try this" from exact?, read and put in place of the sorry, with how long the search took.
        Lean.RequireLean();
        const string text = "theorem swapMul (a b : Nat) : a * b = b * a := by\n  sorry\n";
        string dir = Lean.Sample("Demo");
        await using LeanServer server = await StartAsync(dir);
        IReadOnlyList<SorrySite> sites = ProofSearch.Sites(text);
        string[] only = ["exact?"];
        IReadOnlyList<Diagnostic> diags = await Scratch.CheckAsync(server, Path.Combine(dir, "Exact.lean"), "Prove", ProofSearch.Instrument(text, sites, only), TestContext.Current.CancellationToken)
            .WaitAsync(Lean.Patience, TestContext.Current.CancellationToken);
        SearchResult result = Assert.Single(ProofSearch.Parse(diags.Where(d => d.Severity == DiagnosticSeverity.Information).Select(d => d.Message), sites, only));
        TacticTrial found = Assert.Single(result.Trials);
        Assert.Equal(TrialOutcome.Closes, found.Outcome);
        Assert.Equal("Nat.mul_comm a b", found.Term);
        Assert.True(found.Milliseconds >= 0);
        string filled = ProofSearch.Apply(text, [(result, found)]);
        Assert.Contains("exact Nat.mul_comm a b", filled, StringComparison.Ordinal);
        IReadOnlyList<Diagnostic> check = await Scratch.CheckAsync(server, Path.Combine(dir, "Exact.lean"), "Filled", filled, TestContext.Current.CancellationToken)
            .WaitAsync(Lean.Patience, TestContext.Current.CancellationToken);
        Assert.DoesNotContain(check, d => d.Severity <= DiagnosticSeverity.Warning);
    }

    [Fact]
    public async Task ExtractsAGoalAsALemmaThatLeanAccepts()
    {
        Lean.RequireLean();
        const string text = """
            /-- The main result. -/
            theorem big (a b c : Nat) (f : Nat → Nat) (h1 : a > 2) (h2 : b = a + 1) : ∀ x : Nat, f x + a + b > 4 := by
              intro x
              have key : a + b > 4 := by sorry
              omega

            example {α : Type} [Inhabited α] (xs : List α) (h : xs ≠ []) : xs.length > 0 := by
              cases xs with
              | nil => contradiction
              | cons y ys => exact sorry
            """;
        string dir = Lean.Sample("Demo");
        string path = Path.Combine(dir, "Extract.lean");
        await using LeanServer server = await StartAsync(dir);
        IReadOnlyList<SorrySite> sites = ProofSearch.Sites(text);

        ExtractedLemma? key = await ExtractLemma.RunAsync(server, path, text, sites[0], "key_step", TestContext.Current.CancellationToken)
            .WaitAsync(Lean.Patience, TestContext.Current.CancellationToken);
        Assert.NotNull(key);
        Assert.Equal("theorem key_step {a b : Nat} (h1 : a > 2) (h2 : b = a + 1) : a + b > 4 := by\n  sorry\n\n", key.Text);
        Assert.Equal("exact key_step (by assumption) (by assumption)", key.Call);
        Assert.Equal(0, key.InsertLine); // above the doc comment

        ExtractedLemma? cons = await ExtractLemma.RunAsync(server, path, text, sites[1], "cons_case", TestContext.Current.CancellationToken)
            .WaitAsync(Lean.Patience, TestContext.Current.CancellationToken);
        Assert.NotNull(cons);
        Assert.StartsWith("theorem cons_case {α : Type} [Inhabited α] {y : α} {ys : List α} (h : y :: ys ≠ []) :", cons.Text, StringComparison.Ordinal);
        Assert.Equal("(cons_case (by assumption))", cons.Call); // a term: `exact sorry`

        // Both extractions applied: the file checks, with sorry only in the two new lemmas.
        string once = ExtractLemma.Apply(text, sites[1], cons);
        string twice = ExtractLemma.Apply(once, ProofSearch.Sites(once).First(s => s.Declaration == "big"), key);
        IReadOnlyList<Diagnostic> diags = await Scratch.CheckAsync(server, path, "Extracted", twice, TestContext.Current.CancellationToken)
            .WaitAsync(Lean.Patience, TestContext.Current.CancellationToken);
        Assert.DoesNotContain(diags, d => d.Severity == DiagnosticSeverity.Error);
        string[] lines = twice.Split('\n');
        // In file order, not arrival order: Lean elaborates declarations in parallel, so the two warnings can come
        // back either way round, and on a CI Mac cons_case's sometimes came first.
        Assert.Equal(["key_step", "cons_case"], diags.Where(d => d.Message.Contains("sorry", StringComparison.Ordinal))
            .OrderBy(d => d.Range.Start.Line)
            .Select(d => lines[d.Range.Start.Line].Split(' ')[1]));
    }

    [Fact]
    public void ReplReadsInputsAsCommandsOrExpressions()
    {
        Assert.Equal("#eval 2 + 2", LeanRepl.AsCommand(" 2 + 2 "));
        Assert.Equal("#check Nat.add_comm", LeanRepl.AsCommand("#check Nat.add_comm"));
        Assert.Equal("example : 1 = 1 := rfl", LeanRepl.AsCommand("example : 1 = 1 := rfl"));
        Assert.Equal("open Nat", LeanRepl.AsCommand("open Nat"));
        const string text = "def a := 1\n\ntheorem t : a = 1 := by\n  rfl\n\ndef b := 2\n";
        Assert.Equal("def a := 1\n\ntheorem t : a = 1 := by\n  rfl\n", LeanRepl.Context(text, 3));
    }

    [Fact]
    public async Task ReplEvaluatesInTheFilesContext()
    {
        Lean.RequireLean();
        string dir = Lean.Sample("Demo");
        const string text = "def double (n : Nat) : Nat := n + n\n\ntheorem later : 1 = 1 := rfl\n\ndef secret := 7\n";
        await using LeanServer server = await StartAsync(dir);
        var repl = new LeanRepl();
        string context = LeanRepl.Context(text, 2);
        string path = Path.Combine(dir, "Repl.lean");

        ReplResult r = await repl.EvalAsync(server, path, context, "double 21", TestContext.Current.CancellationToken).WaitAsync(Lean.Patience, TestContext.Current.CancellationToken);
        Assert.False(r.IsError, r.Output);
        Assert.Equal("42", r.Output);

        r = await repl.EvalAsync(server, path, context, "#check later", TestContext.Current.CancellationToken).WaitAsync(Lean.Patience, TestContext.Current.CancellationToken);
        Assert.Contains("later : 1 = 1", r.Output, StringComparison.Ordinal);

        r = await repl.EvalAsync(server, path, context, "secret", TestContext.Current.CancellationToken).WaitAsync(Lean.Patience, TestContext.Current.CancellationToken);
        Assert.True(r.IsError); // defined after the cursor, so not in scope

        r = await repl.EvalAsync(server, path, context, "example : double 2 = 4 := rfl", TestContext.Current.CancellationToken).WaitAsync(Lean.Patience, TestContext.Current.CancellationToken);
        Assert.Equal("✓ accepted", r.Output);

        // Inside a namespace, with a `variable` above the cursor: both are in scope.
        const string scoped = "namespace Foo\n\nvariable (k : Nat)\n\ndef triple : Nat := 3 * k\n\ntheorem t : triple 1 = 3 := rfl\n\nend Foo\n";
        string inside = LeanRepl.Context(scoped, 6);
        r = await repl.EvalAsync(server, path, inside, "triple 2", TestContext.Current.CancellationToken).WaitAsync(Lean.Patience, TestContext.Current.CancellationToken);
        Assert.False(r.IsError, r.Output);
        Assert.Equal("6", r.Output);
        r = await repl.EvalAsync(server, path, inside, "#check k", TestContext.Current.CancellationToken).WaitAsync(Lean.Patience, TestContext.Current.CancellationToken);
        Assert.Contains("k : Nat", r.Output, StringComparison.Ordinal);
        await repl.CloseAsync(server);
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
        // The file on disk says something else: the unsaved text is what is profiled, and the file is left as it is.
        string slowFile = Path.Combine(dir, "Slow.lean");
        const string onDisk = "-- an older version\n";
        File.WriteAllText(slowFile, onDisk);
        DateTime written = File.GetLastWriteTimeUtc(slowFile);
        try
        {
            var (report, error) = await Profiler.RunAsync(project, slowFile, text, new ProfileOptions(Counters: true), null, TestContext.Current.CancellationToken)
                .WaitAsync(Lean.Patience, TestContext.Current.CancellationToken);
            Assert.Null(error);
            Assert.Equal(onDisk, File.ReadAllText(slowFile));
            Assert.Equal(written, File.GetLastWriteTimeUtc(slowFile));
            DeclarationTiming top = report.Declarations[0];
            Assert.Equal(7, top.Line);
            Assert.Equal("slow", top.Name);
            Assert.Contains("omega", top.HotSpot ?? "", StringComparison.Ordinal);
            // The trace goes down to omega, and its time is put on the line omega is written on.
            Assert.Equal("omega", top.HotPath()[^1].Label);
            Assert.Equal(9, top.LineCosts.MaxBy(kv => kv.Value).Key);
            Assert.True(top.LineCosts[9] > top.Value * 0.5, $"omega's line has {top.LineCosts[9]} of {top.Value}");
            // Lean's own profiler: the tactic by name, and the file's categories.
            Assert.Contains(top.Steps, s => s.What == "tactic execution of omega");
            Assert.Contains(report.Categories, c => c.Name == "tactic execution" && c.Seconds > 0);
            Assert.True(report.ImportSeconds > 0);
            // The counters run: what omega made Lean unfold and which instances it used.
            Assert.Contains(top.Counters, c => c.Kind == "instances used");
            Assert.Contains(top.Counters, c => c.Kind == "definitions unfolded");

            // Heartbeats: the same declaration is the costliest, counted in maxHeartbeats units.
            var (beats, berr) = await Profiler.RunAsync(project, slowFile, text, new ProfileOptions(ProfileUnit.Heartbeats), null, TestContext.Current.CancellationToken)
                .WaitAsync(Lean.Patience, TestContext.Current.CancellationToken);
            Assert.Null(berr);
            Assert.Equal(ProfileUnit.Heartbeats, beats.Unit);
            Assert.Equal(7, beats.Declarations[0].Line);
            Assert.True(beats.Declarations[0].Value > 1000, beats.Declarations[0].Time);
            Assert.EndsWith(" hb", beats.Declarations[0].Time, StringComparison.Ordinal);

            // One declaration: the file is cut after it, so the slow theorem below is never checked.
            var (one, oerr) = await Profiler.RunAsync(project, slowFile, text, new ProfileOptions(Line: 3, Runs: 3), null, TestContext.Current.CancellationToken)
                .WaitAsync(Lean.Patience, TestContext.Current.CancellationToken);
            Assert.Null(oerr);
            Assert.Equal(0, one.OnlyLine);
            Assert.Equal(3, one.Runs);
            DeclarationTiming fib = Assert.Single(one.Declarations);
            Assert.Equal("fib", fib.Name);
            Assert.NotNull(fib.Spread);
            Assert.DoesNotContain(one.Categories, c => c.Name.Contains("omega", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(Path.Combine(dir, ".lake"), true);
            File.Delete(slowFile);
        }
    }

    [Fact]
    public void ProfilerReadsNestedTraces()
    {
        string trace = "[Elab.command] [0.500000] ✅️ theorem t : x := by\n    simp\n    omega\n  [Elab.step] [0.450000] ✅️ simp\n    omega\n    [Elab.step] [0.400000] ✅️ omega\n  [Meta.synthInstance] [0.010000] ❌️ Foo";
        var timings = Profiler.Parse([(3, trace), (5, "[Elab.async] [0.25] ✅️ checking\n  [Kernel] [0.2] ✅️ typechecking declarations [t._proof_1]"), (9, "not a trace")],
            ["", "", "", "theorem t : x := by", "  simp", "  omega"]);
        DeclarationTiming t = Assert.Single(timings);
        Assert.Equal(0.75, t.Value, 3);
        Assert.Equal("omega", t.HotSpot);
        Assert.Equal(1, t.Heat);
        Assert.Equal("t", t.Name);

        // The trace as a tree: each entry under the one it is indented under, its text continued on the lines after.
        Assert.Equal(2, t.Trace.Count);
        ProfileNode command = t.Trace[0];
        Assert.Equal("Elab.command", command.Category);
        Assert.Equal("theorem t : x := by\nsimp\nomega", command.Text);
        Assert.Equal("theorem t : x := by …", command.Label);
        Assert.Equal(2, command.Children.Count);
        Assert.Equal(0.04, command.Self, 3);
        Assert.Equal("omega", Assert.Single(command.Children[0].Children).Text);
        Assert.True(command.Children[1].Failed);
        Assert.Equal("instances", command.Children[1].Kind);
        Assert.Equal(["theorem t : x := by …", "simp …", "omega"], t.HotPath().Select(n => n.Label));

        // omega's step is matched to its line; the kernel's work was reported there too.
        Assert.Equal(0.4 + 0.25, t.LineCosts[5], 3);
        Assert.False(t.LineCosts.ContainsKey(3));
    }

    [Fact]
    public void ProfilerPutsEachTacticOnItsLine()
    {
        string[] lines = ["theorem t : p := by", "  · simp", "  · omega  -- done", "  omega"];
        string trace = "[Elab.async] [1.0] ✅️ elaborating proof of t\n  [Elab.step] [0.9] ✅️ · simp\n    · omega\n    omega\n"
            + "    [Elab.step] [0.2] ✅️ · simp\n      [Elab.step] [0.15] ✅️ simp\n    [Elab.step] [0.3] ✅️ · omega\n      [Elab.step] [0.3] ✅️ omega\n    [Elab.step] [0.35] ✅️ omega";
        DeclarationTiming t = Assert.Single(Profiler.Parse([(0, trace)], lines));
        // The bullet and the tactic inside it are on one line; two omegas are told apart by the order they ran in.
        Assert.Equal(0.2, t.LineCosts[1], 3);
        Assert.Equal(0.3, t.LineCosts[2], 3);
        Assert.Equal(0.35, t.LineCosts[3], 3);
    }

    [Fact]
    public void ProfilerReadsCategoriesStepsAndCounters()
    {
        string output = string.Join("\n",
            "import took 1.55s",
            """{"data":"[Elab.command] [0.034643] ✅️ def f : Nat := 1","pos":{"line":1,"column":0},"severity":"information","kind":"trace"}""",
            """{"data":"[Elab.async] [0.398317] ✅️ elaborating proof of slow\n  [Elab.step] [0.39] ✅️ omega","pos":{"line":3,"column":0},"severity":"information","kind":"trace"}""",
            """{"data":"typeclass inference of Decidable took 1.04ms\ntypeclass inference of Decidable took 1.1ms\ntactic execution of Lean.Parser.Tactic.omega took 251ms\n","pos":{"line":3,"column":0},"severity":"information","kind":"[anonymous]"}""",
            """{"data":"[simp] Diagnostics\n  [simp] tried theorems (max: 9, num: 2):\n    [simp] eq_self ↦ 9, succeeded: 4\n    [simp] Nat.add_zero ↦ 6, succeeded: 6\n  use `set_option diagnostics.threshold <num>` to control threshold for reporting counters","pos":{"line":4,"column":2},"severity":"information","kind":"simp"}""",
            """{"data":"[diag] Diagnostics\n  [type_class] used instances (max: 52, num: 1):\n    [type_class] Int.instAdd ↦ 52","pos":{"line":3,"column":0},"severity":"information","kind":"diag"}""",
            """{"data":"unknown identifier 'x'","pos":{"line":6,"column":0},"severity":"error","kind":"[anonymous]"}""",
            "cumulative profiling times:",
            "\tattribute application 0.0602ms",
            "\timport 1.55s",
            "\ttactic execution 366ms",
            "\ttype checking 50.8ms");
        ProfileReport r = Profiler.Parse(output, ["def f : Nat := 1", "", "theorem slow : p := by", "  omega", "", "x"]);
        Assert.Equal(1.55, r.ImportSeconds, 3);
        Assert.Equal(1, r.Errors);
        Assert.Equal(["import", "tactic execution", "type checking", "attribute application"], r.Categories.Select(c => c.Name));
        Assert.Equal(0.366, r.Categories[1].Seconds, 4);
        DeclarationTiming slow = r.Declarations[0];
        Assert.Equal("slow", slow.Name);
        Assert.Equal(["tactic execution of omega", "typeclass inference of Decidable"], slow.Steps.Select(s => s.What));
        Assert.Equal(2, slow.Steps[1].Count);
        Assert.Equal("tactic execution", slow.Steps[0].Category);
        Assert.Equal(["instances used", "simp lemmas tried", "simp lemmas tried"], slow.Counters.Select(c => c.Kind));
        Assert.Equal("9 (4 succeeded)", slow.Counters[1].Detail);
        Assert.Equal(52, r.Counters()[0].Count);
        Assert.Equal(("Elab.step", "omega"), (r.HotSteps()[0].Category, r.HotSteps()[0].Label));

        // The counters run on its own.
        var counters = Profiler.ParseCounters(output, ["def f : Nat := 1", "", "theorem slow : p := by", "  omega"]);
        Assert.Equal(3, counters[2].Count);
    }

    [Fact]
    public void ProfilerMergesRunsAndComparesProfiles()
    {
        static ProfileReport Run(double a, double b) => new([new DeclarationTiming(0, "theorem a : p", a, null, 0), new DeclarationTiming(2, "theorem b : q", b, "simp", b / 2)], ProfileUnit.Seconds);
        ProfileReport merged = Profiler.Merge([Run(0.30, 0.10), Run(0.20, 0.50), Run(0.25, 0.12)]);
        DeclarationTiming a = merged.Declarations.Single(d => d.Name == "a");
        Assert.Equal(0.25, a.Value, 3);
        Assert.Equal((0.20, 0.30), a.Spread);
        Assert.Equal("± 50 ms", a.SpreadText);

        ProfileReport before = Run(0.40, 0.10), after = new([new DeclarationTiming(0, "theorem a : p", 0.10, null, 0), new DeclarationTiming(4, "theorem c : r", 0.2, null, 0)], ProfileUnit.Seconds);
        var changes = Profiler.Compare(before, after);
        Assert.Equal(["a", "c", "b"], changes.Select(c => c.Name));
        Assert.Equal("−300 ms (−75%)", changes[0].Describe(ProfileUnit.Seconds));
        Assert.Equal("new", changes[1].Describe(ProfileUnit.Seconds));
        Assert.Equal("gone", changes[2].Describe(ProfileUnit.Seconds));
        Assert.Equal("+1,500 hb (+50%)", new TimingChange("x", 3000, 4500).Describe(ProfileUnit.Heartbeats));

        string md = Profiler.ToMarkdown(after with { Path = "/p/Slow.lean" }, before);
        Assert.Contains("### Lean profile of `Slow.lean`", md, StringComparison.Ordinal);
        Assert.Contains("| 100 ms | −300 ms (−75%) | `a` | 1 |", md, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("theorem foo (n : Nat) : n = n := rfl", "foo")]
    [InlineData("@[simp] private lemma Nat.bar : True := trivial", "Nat.bar")]
    [InlineData("noncomputable def baz.{u} (α : Sort u) : Nat := 0", "baz")]
    [InlineData("instance (priority := low) instFoo : Foo := ⟨⟩", "instFoo")]
    [InlineData("instance : Foo Nat := ⟨⟩", null)]
    [InlineData("example : 1 = 1 := rfl", null)]
    public void ProfilerNamesDeclarations(string line, string? name) => Assert.Equal(name, Profiler.DeclarationName(line));

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
        // Step through with ← and → (or j and k): the page's own script, no library.
        Assert.Contains("e.key === 'ArrowRight' || e.key === 'j') { go(at + 1)", html, StringComparison.Ordinal);
        Assert.Contains("e.key === 'ArrowLeft' || e.key === 'k') { go(at - 1)", html, StringComparison.Ordinal);
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

            // The project map: status spreads along uses, and base blocks the most.
            var map = ws.Map(TestContext.Current.CancellationToken);
            Core.Verification.MapNode N(string n) => Assert.Single(map.Nodes, x => x.Name == n);
            Assert.Equal(Core.Verification.MapStatus.RestsOnSorry, N("top").Status);
            Assert.Equal(Core.Verification.MapStatus.Proved, N("fine").Status);
            Assert.Equal(Core.Verification.MapStatus.Axiom, N("magic").Status);
            Assert.Equal(Core.Verification.MapStatus.RestsOnAxiom, N("uses_magic").Status);
            Assert.True(N("base").IsSource && !N("middle").IsSource);
            Assert.Equal(2, N("base").UsedBy);
            Assert.Equal((0, 1, 2), (N("base").Level, N("middle").Level, N("top").Level));
            Assert.Equal("base", map.Blockers.First().Name);
            Assert.Contains(map.Edges, e => map.Nodes[e.From].Name == "top" && map.Nodes[e.To].Name == "middle");

            Assert.Equal("foo", Core.Verification.TenetWorkspace.UserFacingOwner("foo._proof_1"));
            Assert.Equal("Bar.foo", Core.Verification.TenetWorkspace.UserFacingOwner("_private.Mod.A.0.Bar.foo.match_1"));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}

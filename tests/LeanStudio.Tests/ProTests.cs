using LeanStudio.Core.Processes;
using LeanStudio.Core.Projects;
using LeanStudio.Core.Workflow;

namespace LeanStudio.Tests;

/// <summary>What people who write a lot of Lean need: tidy imports, the linters, deprecations and the like.</summary>
[Collection(Lean.Collection)]
public sealed class ProTests
{
    /// <summary>A Lake project in a temporary folder with these files (paths relative to its root), built.</summary>
    internal static async Task<LeanProject> BuiltProjectAsync(string name, params (string Path, string Text)[] files)
    {
        string root = Directory.CreateTempSubdirectory("leanstudio-pro").FullName;
        File.WriteAllText(Path.Combine(root, "lean-toolchain"), Lean.Toolchain + "\n");
        File.WriteAllText(Path.Combine(root, "lakefile.toml"), $"name = \"{name}\"\ndefaultTargets = [\"{name}\"]\n\n[[lean_lib]]\nname = \"{name}\"\n");
        foreach ((string path, string text) in files)
        {
            string full = Path.Combine(root, path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, text);
        }
        var project = new LeanProject(root);
        var build = await Lake.BuildAsync(project, ct: TestContext.Current.CancellationToken);
        Assert.True(build.Success, build.Output);
        return project;
    }

    private static readonly (string, string)[] ImportFiles =
    [
        ("Tidy.lean", "import Tidy.A\nimport Tidy.B\nimport Tidy.Syntax\nimport Tidy.Unused\n"),
        ("Tidy/A.lean", "def foo : Nat := 3\n"),
        ("Tidy/B.lean", "import Tidy.A\ndef bar : Nat := foo + 1\n"),
        ("Tidy/Unused.lean", "def lonely : Nat := 0\n"),
        ("Tidy/Syntax.lean", "notation \"⦃\" x \"⦄\" => x + 1\nmacro \"mytac\" : tactic => `(tactic| rfl)\n"),
    ];

    [Fact]
    public async Task FindsTheImportsAFileDoesNotNeed()
    {
        Lean.RequireLean();
        LeanProject project = await BuiltProjectAsync("Tidy", ImportFiles);
        try
        {
            const string text = """
                import Tidy.Unused
                import Tidy.A
                import Tidy.B
                import Tidy.Syntax

                def baz : Nat := bar + foo + ⦃ 1 ⦄

                theorem baz_eq : 2 = 2 := by mytac
                """;
            string file = Path.Combine(project.Root, "Tidy", "C.lean");
            ImportReport report = await ImportCheck.RunAsync(project, file, text, TestContext.Current.CancellationToken);
            Assert.Equal(0, report.Errors);
            Assert.Equal(["Tidy.Unused", "Tidy.A", "Tidy.B", "Tidy.Syntax"], report.Imports.Select(i => i.Module));

            ImportVerdict unused = report.Imports[0], a = report.Imports[1], b = report.Imports[2], syntax = report.Imports[3];
            Assert.False(unused.Keep);
            Assert.Equal("Nothing in this file uses Tidy.Unused", unused.Explanation);
            Assert.False(a.Keep); // B brings it in
            Assert.Equal("Tidy.A is already imported by Tidy.B", a.Explanation);
            Assert.True(b.Keep);
            Assert.Contains("bar", b.Uses);
            Assert.True(syntax.Keep); // only a notation and a tactic come from it, and they count

            string tidied = ImportCheck.Remove(text, report.Removable.Select(r => r.Module));
            Assert.StartsWith("import Tidy.B\nimport Tidy.Syntax\n\ndef baz", tidied, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(project.Root, true);
        }
    }

    [Fact]
    public void RemovesOnlyTheNamedImportLines()
    {
        const string text = "import A.B -- keep me\nimport A.C\npublic import A.D\n/- import A.C -/\ndef x := 1\n";
        Assert.Equal(new Dictionary<string, int> { ["A.B"] = 0, ["A.C"] = 1, ["A.D"] = 2 }, ImportCheck.ImportLines(text));
        Assert.Equal("import A.B -- keep me\n/- import A.C -/\ndef x := 1\n", ImportCheck.Remove(text, ["A.C", "A.D"]));
    }

    [Fact]
    public async Task LintsAFileTheWayCiDoes()
    {
        Lean.RequireLean();
        LeanProject project = await BuiltProjectAsync("Linty",
            ("Linty.lean", "import Linty.Basic\n"),
            ("Linty/Basic.lean", "/-- Documented. -/\ndef fine : Nat := 1\n\ndef undocumented (n : Nat) (unusedArg : Nat) : Nat := n\n"));
        try
        {
            string file = Path.Combine(project.Root, "Linty", "Basic.lean");
            Assert.Equal("linter.all", Lint.LintersFor(project));
            var (findings, error) = await Lint.RunAsync(project, file, TestContext.Current.CancellationToken);
            Assert.Null(error);
            LintFinding docs = Assert.Single(findings, f => f.Linter == "linter.missingDocs");
            Assert.Equal((file, 3, 4), (docs.Path, docs.Line, docs.Column));
            Assert.Contains("missing doc string", docs.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("Note:", docs.Message, StringComparison.Ordinal);
            LintFinding unused = Assert.Single(findings, f => f.Linter == "linter.unusedVariables");
            Assert.Equal(3, unused.Line);
            Assert.DoesNotContain(findings, f => f.Line < 3);
        }
        finally
        {
            Directory.Delete(project.Root, true);
        }
    }

    [Fact]
    public void ReadsLakeLintOutput()
    {
        const string output = """
            ✔ [6/7] Built Demo.L (243ms)
            -- Text linter diagnostics in Demo.L:
            Demo/L.lean:1:4: warning: missing doc string for public def undocumented
            Note: This linter can be disabled with `set_option linter.missingDocs false`
            -- Demo.L
            /abs/Demo/L.lean:9:1: error: Demo.L.bad The declaration's name repeats a namespace
            spanning two lines
            -- Found 1 error in 1 declaration
            """;
        var f = Lint.Parse(output, "/root");
        Assert.Equal(2, f.Count);
        Assert.Equal(new LintFinding(Path.GetFullPath("/root/Demo/L.lean"), 0, 4, false, "missing doc string for public def undocumented", "linter.missingDocs"), f[0]);
        Assert.Equal(("/abs/Demo/L.lean", 8, 0, true, null), (f[1].Path, f[1].Line, f[1].Column, f[1].IsError, f[1].Linter));
        Assert.Equal("Demo.L.bad The declaration's name repeats a namespace\nspanning two lines", f[1].Message);

        // Batteries' runLinter: a block per linter, findings counted from column 1.
        const string env = """
            Running linter on specified modules: [Mproj.Tidy]
            -- Found 2 errors in 3 declarations (plus 0 automatically generated ones) in Mproj.Tidy with 15 linters
            /- The `docBlame` linter reports:
            DEFINITIONS ARE MISSING DOCUMENTATION STRINGS:
            This linter can be disabled with `@[nolint docBlame]`. -/
            -- Mproj.Tidy
            /p/Mproj/Tidy.lean:10:1: error: noDoc definition missing documentation string
            /- The `unusedArguments` linter reports:
            UNUSED ARGUMENTS. -/
            -- Mproj.Tidy
            /p/Mproj/Tidy.lean:12:5: error: f argument 2 n : ℕ
            """;
        var e = Lint.Parse(env, "/p");
        Assert.Equal([("docBlame", 9, 0, "noDoc definition missing documentation string"), ("unusedArguments", 11, 4, "f argument 2 n : ℕ")],
            e.Select(x => (x.Linter, x.Line, x.Column, x.Message)));
    }

    [Fact]
    public void AddsADeprecatedAliasAfterARenamedDeclaration()
    {
        var since = new DateOnly(2026, 9, 23);
        const string text = """
            theorem add_zero' (n : Nat) : n + 0 = n := by
              simp

            def twice : Nat → Nat
            | 0 => 0
            | n + 1 => twice n + 2

            theorem other : True := trivial
            """;
        Assert.Equal("theorem", Deprecation.DeclarationKeyword("@[simp] protected theorem add_zero' (n : Nat) : n + 0 = n := by", "add_zero'"));
        Assert.Null(Deprecation.DeclarationKeyword("  exact add_zero' n", "add_zero'"));

        string mathlib = Deprecation.AddAlias(text, 0, "zero_add'", "add_zero'", batteries: true, since);
        Assert.Contains("  simp\n\n@[deprecated (since := \"2026-09-23\")] alias zero_add' := add_zero'\n\ndef twice", mathlib, StringComparison.Ordinal);

        string core = Deprecation.AddAlias(text, 3, "double", "twice", batteries: false, since);
        Assert.Contains("| n + 1 => twice n + 2\n\n@[deprecated twice (since := \"2026-09-23\")] def double : type_of% @twice := @twice\n\ntheorem other", core, StringComparison.Ordinal);

        Assert.Equal(text, Deprecation.AddAlias(text, 1, "x", "y", true, since)); // not a declaration of y
    }

    [Fact]
    public async Task TheCoreAliasKeepsTheOldNameWorking()
    {
        Lean.RequireLean();
        string text = Deprecation.AddAlias("theorem append_nil' {α : Type} (xs : List α) : xs ++ [] = xs := List.append_nil xs\n", 0,
            "nil_append'", "append_nil'", batteries: false, new DateOnly(2026, 9, 23)) + "\nexample : [1] ++ [] = [1] := nil_append' [1]\n";
        string dir = Lean.Sample("Demo");
        await using var server = new Lsp.LeanServer(new Lsp.LeanServerCommand(Lean.Executable!, ["--server"], dir));
        var ct = TestContext.Current.CancellationToken;
        await server.StartAsync(ct);
        string uri = Lsp.LeanServer.UriOf(Path.Combine(dir, "Deprecated.lean"));
        await server.OpenAsync(uri, text);
        await server.WaitForElaborationAsync(uri, ct).WaitAsync(Lean.Patience, ct);
        IReadOnlyList<Lsp.Diagnostic> diags = server.DiagnosticsOf(uri);
        Assert.DoesNotContain(diags, d => d.Severity == Lsp.DiagnosticSeverity.Error);
        Assert.Contains(diags, d => d.Message.Contains("`nil_append'` has been deprecated", StringComparison.Ordinal) && d.Message.Contains("append_nil'", StringComparison.Ordinal));
    }

    [Fact]
    public void KeepsTheLibraryRootImportingEverything()
    {
        const string root = "/-! The library. -/\nimport Lib.A\nimport Lib.C\n";
        Assert.True(LibraryRoot.ImportsEverything(root));
        Assert.False(LibraryRoot.ImportsEverything("import Lib.A\ndef x := 1\n"));
        Assert.False(LibraryRoot.ImportsEverything("-- nothing\n"));
        Assert.Equal("/-! The library. -/\nimport Lib.A\nimport Lib.B\nimport Lib.C\n", LibraryRoot.AddImport(root, "Lib.B"));
        Assert.Equal("/-! The library. -/\nimport Lib.A\nimport Lib.C\nimport Lib.D\n", LibraryRoot.AddImport(root, "Lib.D"));
        Assert.Equal("/-! The library. -/\nimport Lib.0\nimport Lib.A\nimport Lib.C\n", LibraryRoot.AddImport(root, "Lib.0"));
        Assert.Equal(root, LibraryRoot.AddImport(root, "Lib.C"));
        Assert.Equal("/-! The library. -/\nimport Lib.C\n", LibraryRoot.RemoveImport(root, "Lib.A"));
        Assert.Equal("import Lib.Z\nimport Lib.A\nimport Lib.M\n", LibraryRoot.AddImport("import Lib.Z\nimport Lib.A\n", "Lib.M")); // unsorted: at the end

        string dir = Directory.CreateTempSubdirectory("leanstudio-root").FullName;
        try
        {
            File.WriteAllText(Path.Combine(dir, "lakefile.toml"), "name = \"Lib\"\n[[lean_lib]]\nname = \"Lib\"\n");
            File.WriteAllText(Path.Combine(dir, "Lib.lean"), root);
            Directory.CreateDirectory(Path.Combine(dir, "Lib", "Sub"));
            foreach (string f in new[] { "A", "C", "B", "Sub/D" })
            {
                File.WriteAllText(Path.Combine(dir, "Lib", f + ".lean"), "");
            }
            var project = new LeanProject(dir);
            Assert.Equal(Path.Combine(dir, "Lib.lean"), LibraryRoot.RootFileOf(project, "Lib.Sub.D"));
            Assert.Null(LibraryRoot.RootFileOf(project, "Lib"));
            Assert.Equal(["Lib.B", "Lib.Sub.D"], LibraryRoot.Missing(project, Path.Combine(dir, "Lib.lean")));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task AssistantsCanTidyImportsAndLint()
    {
        Lean.RequireLean();
        LeanProject project = await BuiltProjectAsync("Tidy", [.. ImportFiles, ("Tidy/C.lean", "import Tidy.Unused\nimport Tidy.B\n\n/-- Doc. -/\ndef baz : Nat := bar\n")]);
        try
        {
            await using var bench = new Core.Agents.Workbench(project.Root);
            Mcp.McpServer server = Mcp.LeanTools.Create(bench, "test");
            string file = Path.Combine(project.Root, "Tidy", "C.lean");
            async Task<string> Call(string tool, System.Text.Json.Nodes.JsonObject args)
            {
                var r = await server.HandleAsync(new System.Text.Json.Nodes.JsonObject
                {
                    ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "tools/call",
                    ["params"] = new System.Text.Json.Nodes.JsonObject { ["name"] = tool, ["arguments"] = args },
                }, TestContext.Current.CancellationToken);
                var result = r!["result"]!;
                Assert.False(result["isError"]?.GetValue<bool>() ?? false, result.ToJsonString());
                return result["content"]![0]!["text"]!.GetValue<string>();
            }

            string report = await Call("unused_imports", new() { ["path"] = file });
            Assert.Contains("remove Tidy.Unused: Nothing in this file uses Tidy.Unused", report, StringComparison.Ordinal);
            Assert.Contains("keep   Tidy.B (for bar)", report, StringComparison.Ordinal);
            await Call("unused_imports", new() { ["path"] = file, ["apply"] = true });
            Assert.StartsWith("import Tidy.B\n", File.ReadAllText(file), StringComparison.Ordinal);

            string lint = await Call("lint", new() { ["path"] = Path.Combine(project.Root, "Tidy", "A.lean") });
            Assert.Contains("A.lean:1:5: warning [linter.missingDocs]: missing doc string", lint, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(project.Root, true);
        }
    }

    [Fact]
    public void FiltersTheLocalContextAsTheInfoviewDoes()
    {
        static Lsp.TaggedString T(string s) => new(s, []);
        var goal = new Lsp.InteractiveGoal(null, "⊢ ", [
            new(["α"], T("Type"), null, false, true, false, false),
            new(["inst"], T("Group α"), null, true, false, false, false),
            new(["a✝", "b"], T("α"), null, false, false, false, false),
            new(["n✝"], T("ℕ"), null, false, false, false, false),
            new(["k"], T("ℕ"), T("n✝ + 1"), false, false, false, false),
        ], T("b = b"), "", false, false);
        Assert.Same(goal, Lsp.GoalFilter.None.Apply(goal));
        var all = new Lsp.GoalFilter(HideTypes: true, HideInstances: true, HideInaccessible: true, HideLetValues: true).Apply(goal);
        Assert.Equal(["b", "k"], all.Hypotheses.Select(h => string.Join(' ', h.Names)));
        Assert.Null(all.Hypotheses[1].Value);
        Assert.Equal(5, new Lsp.GoalFilter(HideLetValues: true).Apply(goal).Hypotheses.Count);
        Assert.Equal(["α", "a✝ b", "n✝", "k"], new Lsp.GoalFilter(HideInstances: true).Apply(goal).Hypotheses.Select(h => string.Join(' ', h.Names)));
    }

    [Fact]
    public async Task MarksWhereProofsEndFinishedOrNot()
    {
        Lean.RequireLean();
        var ct = TestContext.Current.CancellationToken;
        string dir = Lean.Sample("Demo");
        await using var server = new Lsp.LeanServer(new Lsp.LeanServerCommand(Lean.Executable!, ["--server"], dir));
        await server.StartAsync(ct);
        string uri = Lsp.LeanServer.UriOf(Path.Combine(dir, "Marks.lean"));
        await server.OpenAsync(uri, "theorem done : True := by\n  trivial\n\ntheorem notyet (p q : Prop) (hp : p) : p ∧ q := by\n  constructor\n  exact hp\n\ntheorem term : True := trivial\n");
        await server.WaitForElaborationAsync(uri, ct).WaitAsync(Lean.Patience, ct);
        IReadOnlyList<Lsp.Diagnostic> shown = server.DiagnosticsOf(uri), silent = server.SilentDiagnosticsOf(uri);
        Assert.DoesNotContain(shown, d => d.IsSilent == true); // "Goals accomplished!" is not a message to show
        Assert.Equal(2, silent.Count(d => d.IsGoalsAccomplished));
        Assert.Single(shown, d => d.IsUnsolvedGoals);
        Assert.Equal([new Lsp.ProofMark(1, true), new Lsp.ProofMark(5, false), new Lsp.ProofMark(7, true)], Lsp.ProofMark.From(shown, silent));
    }

    [Fact]
    public async Task TenetKeepsAChallengeAndItsSolutionApart()
    {
        // As in a benchmark: two modules that don't import each other declare the same theorem, the challenge
        // with sorry and the solution with a proof. Neither may hide the other, or lend it its axioms.
        Lean.RequireLean();
        string root = Directory.CreateTempSubdirectory("leanstudio-pro").FullName;
        File.WriteAllText(Path.Combine(root, "lean-toolchain"), Lean.Toolchain + "\n");
        File.WriteAllText(Path.Combine(root, "lakefile.toml"),
            "name = \"Bench\"\ndefaultTargets = [\"Proof\", \"Challenge\", \"Solution\"]\n\n[[lean_lib]]\nname = \"Proof\"\n\n[[lean_lib]]\nname = \"Challenge\"\n\n[[lean_lib]]\nname = \"Solution\"\n");
        File.WriteAllText(Path.Combine(root, "Proof.lean"), "theorem Proof.main (n : Nat) : n + 0 = n := by simp\n");
        File.WriteAllText(Path.Combine(root, "Challenge.lean"), "namespace Bench\ntheorem statement (n : Nat) : n + 0 = n := by\n  sorry\nend Bench\n");
        File.WriteAllText(Path.Combine(root, "Solution.lean"), "import Proof\nnamespace Bench\ntheorem statement (n : Nat) : n + 0 = n := Proof.main n\nend Bench\n");
        var project = new LeanProject(root);
        try
        {
            var build = await Lake.BuildAsync(project, ct: TestContext.Current.CancellationToken);
            Assert.True(build.Success, build.Output);
            using var ws = Core.Verification.TenetWorkspace.Open(project);
            Assert.Equal(["Challenge", "Solution"], ws.ModulesDeclaring("Bench.statement").Order());

            var report = await ws.VerifyAsync(ct: TestContext.Current.CancellationToken);
            var verdicts = report.Declarations.Where(d => d.Name == "Bench.statement").ToDictionary(d => d.Module);
            Assert.Equal(Core.Verification.VerificationStatus.RestsOnAssumption, verdicts["Challenge"].Status);
            Assert.Equal(["sorryAx"], verdicts["Challenge"].Assumptions);
            Assert.Equal(Core.Verification.VerificationStatus.Verified, verdicts["Solution"].Status);

            Assert.Contains("sorryAx", ws.AxiomsOf("Bench.statement", "Challenge"));
            Assert.DoesNotContain("sorryAx", ws.AxiomsOf("Bench.statement", "Solution"));
            Assert.Throws<InvalidOperationException>(() => ws.AxiomsOf("Bench.statement")); // which one?
            Assert.Throws<KeyNotFoundException>(() => ws.AxiomsOf("Bench.nothing")); // not "no axioms"
            Assert.Empty(ws.WhyNotProved("Bench.statement", TestContext.Current.CancellationToken, "Solution"));
            Assert.Single(ws.WhyNotProved("Bench.statement", TestContext.Current.CancellationToken, "Challenge"));
            Assert.Equal(ws.Details("Bench.statement", "Challenge")!.Type, ws.Details("Bench.statement", "Solution")!.Type);
            Assert.Equal("Solution", ws.Details("Bench.statement", "Solution")!.Module);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void KnowsWhatImportsWhat()
    {
        string root = Directory.CreateTempSubdirectory("leanstudio-graph").FullName;
        try
        {
            File.WriteAllText(Path.Combine(root, "lakefile.toml"), "name = \"G\"\n[[lean_lib]]\nname = \"G\"\n");
            Directory.CreateDirectory(Path.Combine(root, "G"));
            File.WriteAllText(Path.Combine(root, "G.lean"), "import G.C\n");
            File.WriteAllText(Path.Combine(root, "G", "A.lean"), "import Mathlib.Tactic\n");
            File.WriteAllText(Path.Combine(root, "G", "B.lean"), "import G.A\n");
            File.WriteAllText(Path.Combine(root, "G", "C.lean"), "-- C\nimport G.B\nimport G.A\n");
            var g = ImportGraph.Build(new LeanProject(root));
            Assert.Equal(["G.B", "G.A"], g.ImportsOf("G.C").Select(e => e.Imported));
            Assert.Equal(2, g.ImportsOf("G.C")[1].Line);
            Assert.Equal(["G.B", "G.C"], g.ImportedBy("G.A").Select(e => e.Module));
            Assert.Equal(["G", "G.B", "G.C"], g.Dependents("G.A")); // what rebuilds when A changes
            Assert.Equal(["G", "G.A", "G.B", "G.C"], g.Dependents("Mathlib.Tactic"));
            Assert.Empty(g.Dependents("G"));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task PassesArgumentsToLeanAndLogsItsMessages()
    {
        Lean.RequireLean();
        var ct = TestContext.Current.CancellationToken;
        string root = Directory.CreateTempSubdirectory("leanstudio-args").FullName;
        string log = Path.Combine(root, "logs", "server.log");
        try
        {
            File.WriteAllText(Path.Combine(root, "lean-toolchain"), Lean.Toolchain + "\n");
            var project = new LeanProject(root); // no lakefile: lean --server
            Lsp.LeanServerCommand cmd = project.ServerCommand(null, ["-DautoImplicit=false"]);
            Assert.Contains("-DautoImplicit=false", cmd.Arguments);
            await using var server = new Lsp.LeanServer(cmd) { MessageLogPath = log };
            await server.StartAsync(ct);
            string uri = Lsp.LeanServer.UriOf(Path.Combine(root, "Args.lean"));
            await server.OpenAsync(uri, "def f (x : α) : α := x\n");
            await server.WaitForElaborationAsync(uri, ct).WaitAsync(Lean.Patience, ct);
            // With autoImplicit off, α is not bound for us: Lean says so.
            Assert.Contains(server.DiagnosticsOf(uri), d => d.Severity == Lsp.DiagnosticSeverity.Error && d.Message.Contains("α", StringComparison.Ordinal));
            await server.DisposeAsync();
            string logged = File.ReadAllText(log);
            Assert.Contains("→ {", logged, StringComparison.Ordinal);
            Assert.Contains("\"method\":\"initialize\"", logged, StringComparison.Ordinal);
            Assert.Contains("← {", logged, StringComparison.Ordinal);
            Assert.Contains("textDocument/publishDiagnostics", logged, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void FindsLeansFileWorkers()
    {
        const string ps = """
            29717 343296       00:08 /Users/me/.elan/toolchains/leanprover--lean4---v4.34.0/bin/lean --server
            29719 413504    01:02:03 /Users/me/.elan/toolchains/leanprover--lean4---v4.34.0/bin/lean --worker file:///Users/me/p/P/Basic.lean
            29720 2202009 2-03:04:05 /Users/me/.elan/toolchains/x/bin/lean --worker file:///Users/me/p/P/Big%20File.lean
              812   1024       00:01 /usr/bin/vim --worker notes.txt
            """;
        IReadOnlyList<Core.Toolchains.LeanWorker> w = Core.Toolchains.LeanProcesses.ParsePs(ps);
        Assert.Equal([29719, 29720], w.Select(x => x.Pid));
        Assert.Equal("/Users/me/p/P/Basic.lean", w[0].File);
        Assert.Equal("/Users/me/p/P/Big File.lean", w[1].File);
        Assert.Equal(new TimeSpan(1, 2, 3), w[0].Running);
        Assert.Equal(new TimeSpan(2, 3, 4, 5), w[1].Running);
        Assert.Equal("403 MB", w[0].Memory);
        Assert.Equal("2.1 GB", w[1].Memory);

        var win = Core.Toolchains.LeanProcesses.ParseWindows(
            """{"ProcessId":7,"WorkingSetSize":104857600,"CreationDate":"2026-09-24T01:00:00","CommandLine":"C:\\lean\\bin\\lean.exe --worker file:///C:/p/A.lean"}""",
            new DateTime(2026, 9, 24, 1, 5, 0));
        Assert.Equal((7, "100 MB", TimeSpan.FromMinutes(5)), (win[0].Pid, win[0].Memory, win[0].Running));
    }

    [Fact]
    public async Task ListsTheInstancesOfAClass()
    {
        Lean.RequireLean();
        var ct = TestContext.Current.CancellationToken;
        LeanProject project = await BuiltProjectAsync("Inst", ("Inst.lean", "import Inst.Shape\n"),
            ("Inst/Shape.lean", "class Shape (α : Type) where\n  sides : Nat\n\ninstance : Shape Unit := ⟨3⟩\ninstance squares : Shape Bool := ⟨4⟩\n"));
        try
        {
            string file = Path.Combine(project.Root, "Inst", "Use.lean");
            var (cls, found) = await Instances.OfAsync(project, file, "import Inst.Shape\n\n#check Shape\n", "Shape", ct);
            Assert.Equal("Shape", cls);
            Assert.Equal(["instShapeUnit", "squares"], found.Select(x => x.Name));
            Assert.Equal("Shape Bool", found[1].Type);

            var (inh, all) = await Instances.OfAsync(project, file, "", "Inhabited", ct); // core's, from Lean itself
            Assert.Equal("Inhabited", inh);
            Assert.Contains(all, x => x.Name == "Array.instInhabited");
            await Assert.ThrowsAsync<InvalidOperationException>(() => Instances.OfAsync(project, file, "", "NoSuchClass", ct));
        }
        finally
        {
            Directory.Delete(project.Root, true);
        }
    }

    [Fact]
    public async Task CountsHeartbeatsPerDeclaration()
    {
        Lean.RequireLean();
        var ct = TestContext.Current.CancellationToken;
        const string text = """
            /-- Cheap. -/
            theorem easy : 1 + 1 = 2 := rfl

            @[simp]
            theorem heavier (n : Nat) (h : n < 60) : n * n < 3600 := by
              omega

            mutual
            def isEven : Nat → Bool
              | 0 => true
              | n + 1 => isOdd n
            def isOdd : Nat → Bool
              | 0 => false
              | n + 1 => isEven n
            end

            example : (List.range 200).sum = 19900 := by decide
            """;
        (string instrumented, _, _) = Core.Proofs.Heartbeats.Instrument(text);
        Assert.StartsWith("import Lean\n", instrumented, StringComparison.Ordinal);
        Assert.Contains("#leanstudio_heartbeats /-- Cheap. -/", instrumented, StringComparison.Ordinal);
        Assert.Contains("#leanstudio_heartbeats @[simp]", instrumented, StringComparison.Ordinal);
        Assert.DoesNotContain("#leanstudio_heartbeats def isEven", instrumented, StringComparison.Ordinal); // inside mutual

        string root = Directory.CreateTempSubdirectory("leanstudio-hb").FullName;
        try
        {
            File.WriteAllText(Path.Combine(root, "lean-toolchain"), Lean.Toolchain + "\n");
            var (counts, error) = await Core.Proofs.Heartbeats.RunAsync(new LeanProject(root), Path.Combine(root, "Hb.lean"), text, ct);
            Assert.Null(error);
            Assert.Equal([0, 3, 16], counts.Select(c => c.Line).Order());
            Assert.All(counts, c => Assert.True(c.Heartbeats >= 0));
            Assert.Equal("example : (List.range 200).sum = 19900 := by decide", counts.Single(c => c.Line == 16).Declaration);
            Assert.True(counts.Single(c => c.Line == 16).Heartbeats > counts.Single(c => c.Line == 0).Heartbeats);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void RenamesDeprecatedNamesAsWritten()
    {
        const string text = "theorem t := by\n  simp [Nat.foo, foo, Nat.foo_bar]\n  exact foo\n";
        DeprecatedUse[] uses =
        [
            new("F.lean", 1, 8, "Nat.foo", "Nat.bar"),   // written in full
            new("F.lean", 1, 17, "Nat.foo", "Nat.bar"),  // written short, in its namespace
            new("F.lean", 2, 8, "Nat.foo", "Int.baz"),   // short, but the new name is elsewhere: in full
            new("F.lean", 1, 22, "Nat.gone", "Nat.x"),   // the position doesn't hold that name: left alone
        ];
        Assert.Equal("theorem t := by\n  simp [Nat.bar, bar, Nat.foo_bar]\n  exact Int.baz\n", DependencyBump.Rename(text, uses));

        BuildMessage[] messages =
        [
            new("F.lean", 3, 8, false, "`Nat.foo` has been deprecated: Use `Nat.bar` instead"),
            new("F.lean", 4, 2, false, "`List.get?` has been deprecated, use `List.get?Internal` instead"),
            new("F.lean", 5, 0, true, "unknown identifier 'x'"),
        ];
        Assert.Equal([("Nat.foo", "Nat.bar"), ("List.get?", "List.get?Internal")], DependencyBump.DeprecatedUses(messages).Select(d => (d.Old, d.New)));
    }

    [Fact]
    public async Task FixesTheDeprecationsAnUpdateBrings()
    {
        // The library renamed foo to bar and kept foo as a deprecated alias; the project still uses foo.
        Lean.RequireLean();
        var ct = TestContext.Current.CancellationToken;
        LeanProject project = await BuiltProjectAsync("Bump", ("Bump.lean", "import Bump.Lib\nimport Bump.Use\n"),
            ("Bump/Lib.lean", "namespace Lib\ntheorem bar (n : Nat) : n + 0 = n := rfl\n@[deprecated bar (since := \"2026-09-24\")] theorem foo (n : Nat) : n + 0 = n := rfl\nend Lib\n"),
            ("Bump/Use.lean", "import Bump.Lib\nexample : 3 + 0 = 3 := Lib.foo 3\nopen Lib in\nexample : 4 + 0 = 4 := foo 4\n"));
        try
        {
            ProcessResult build = await Lake.BuildAsync(project, ct: ct);
            IReadOnlyList<DeprecatedUse> uses = DependencyBump.DeprecatedUses(LakeOutput.Parse(build.Output, project.Root));
            Assert.Equal(2, uses.Count);
            Assert.All(uses, u => Assert.Equal(("Lib.foo", "Lib.bar"), (u.Old, u.New)));
            string use = Path.Combine(project.Root, "Bump", "Use.lean");
            File.WriteAllText(use, DependencyBump.Rename(File.ReadAllText(use), uses.Where(u => u.File == use)));
            Assert.Equal("import Bump.Lib\nexample : 3 + 0 = 3 := Lib.bar 3\nopen Lib in\nexample : 4 + 0 = 4 := bar 4\n", File.ReadAllText(use));
            ProcessResult again = await Lake.BuildAsync(project, ct: ct);
            Assert.True(again.Success, again.Output);
            Assert.Empty(DependencyBump.DeprecatedUses(LakeOutput.Parse(again.Output, project.Root)));
        }
        finally
        {
            Directory.Delete(project.Root, true);
        }
    }

    [Fact]
    public async Task UpdatesADependencyAndFindsWhatItDeprecated()
    {
        Lean.RequireLean();
        var ct = TestContext.Current.CancellationToken;
        string lib = Directory.CreateTempSubdirectory("leanstudio-lib").FullName;
        string root = Directory.CreateTempSubdirectory("leanstudio-app").FullName;
        async Task Git(string dir, params string[] args)
        {
            ProcessResult r = await ProcessRunner.RunAsync("git", ["-c", "user.email=t@t", "-c", "user.name=t", .. args], dir, ct: ct);
            Assert.True(r.Success, r.Output);
        }
        try
        {
            // Version 1 of a library, in its own git repository.
            File.WriteAllText(Path.Combine(lib, "lean-toolchain"), Lean.Toolchain + "\n");
            File.WriteAllText(Path.Combine(lib, "lakefile.toml"), "name = \"Lib\"\n[[lean_lib]]\nname = \"Lib\"\n");
            File.WriteAllText(Path.Combine(lib, "Lib.lean"), "theorem Lib.foo (n : Nat) : n + 0 = n := rfl\n");
            await Git(lib, "init", "-q", "-b", "main");
            await Git(lib, "add", ".");
            await Git(lib, "commit", "-q", "-m", "v1");

            // A project that uses it.
            File.WriteAllText(Path.Combine(root, "lean-toolchain"), Lean.Toolchain + "\n");
            File.WriteAllText(Path.Combine(root, "lakefile.toml"),
                $"name = \"App\"\ndefaultTargets = [\"App\"]\n\n[[require]]\nname = \"Lib\"\ngit = \"{new Uri(lib).AbsoluteUri}\"\nrev = \"main\"\n\n[[lean_lib]]\nname = \"App\"\n");
            File.WriteAllText(Path.Combine(root, "App.lean"), "import Lib\nexample : 2 + 0 = 2 := Lib.foo 2\n");
            var project = new LeanProject(root);
            ProcessResult first = await Lake.BuildAsync(project, ct: ct);
            Assert.True(first.Success, first.Output);
            string? before = DependencyBump.ManifestRev(project, "Lib");
            Assert.NotNull(before);

            // Version 2 renames foo to bar and keeps foo, deprecated.
            File.WriteAllText(Path.Combine(lib, "Lib.lean"),
                "theorem Lib.bar (n : Nat) : n + 0 = n := rfl\n@[deprecated Lib.bar (since := \"2026-09-24\")] theorem Lib.foo (n : Nat) : n + 0 = n := rfl\n");
            await Git(lib, "commit", "-q", "-am", "v2");

            ProcessResult update = await Lake.UpdateAsync(project, ct: ct, package: "Lib");
            Assert.True(update.Success, update.Output);
            string? after = DependencyBump.ManifestRev(project, "Lib");
            Assert.NotEqual(before, after);

            ProcessResult build = await Lake.BuildAsync(project, ct: ct);
            DeprecatedUse use = Assert.Single(DependencyBump.DeprecatedUses(LakeOutput.Parse(build.Output, root)));
            Assert.Equal(("Lib.foo", "Lib.bar"), (use.Old, use.New));
            Assert.Equal(Lint.RealPath(Path.Combine(root, "App.lean")), Lint.RealPath(use.File));
        }
        finally
        {
            Directory.Delete(lib, true);
            Directory.Delete(root, true);
        }
    }
}

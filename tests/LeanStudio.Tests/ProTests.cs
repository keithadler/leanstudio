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
}

using System.Text.Json;
using LeanStudio.Core.Git;
using LeanStudio.Core.Projects;
using LeanStudio.Core.Verification;

namespace LeanStudio.Tests;

/// <summary>The assurance report (<c>leanstudio --verify</c>, the Tenet menu's Assurance Report, <c>assurance_report</c> over MCP).</summary>
public sealed class AssuranceTests
{
    private static readonly LeanProject Fake = new(Path.Combine(Path.GetTempPath(), "Fake"));

    private static DeclarationVerdict V(string name, VerificationStatus status, string[]? assumptions = null, string? message = null) =>
        new(name, "Fake.Basic", status, assumptions ?? [], message, 1);

    private static AssuranceReport Report(AssurancePolicy? policy = null, IReadOnlyList<TrustMark>? marks = null, params DeclarationVerdict[] verdicts) =>
        Assurance.Build(Fake, new VerificationReport("4.34.0", 1, 1, TimeSpan.FromSeconds(1), verdicts), marks ?? [], policy ?? AssurancePolicy.Default,
            now: new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero));

    [Fact]
    public void NamesTheCompiledCodeAProofTrusts()
    {
        // Lean 4.24 and later: native_decide and bv_decide add an axiom under the declaration's `_native` namespace.
        Assert.Equal("native_decide", Assurance.CompiledCodeOf("Foo.bar._native.native_decide.ax_1_1"));
        Assert.Equal("bv_decide", Assurance.CompiledCodeOf("_private.Foo.0.baz._native.bv_decide.ax_2"));
        // Earlier: the library's own axioms.
        Assert.Equal("Lean.ofReduceBool", Assurance.CompiledCodeOf("Lean.ofReduceBool"));
        Assert.Equal("Lean.trustCompiler", Assurance.CompiledCodeOf("Lean.trustCompiler"));
        Assert.Null(Assurance.CompiledCodeOf("knownin1980s"));
        Assert.Null(Assurance.CompiledCodeOf("sorryAx"));
        Assert.Null(Assurance.CompiledCodeOf("Foo.native"));
    }

    [Fact]
    public void SortsEachDeclarationByTheWorstThingItRestsOn()
    {
        AssuranceReport r = Report(null, null,
            V("good", VerificationStatus.Verified),
            V("native", VerificationStatus.RestsOnAssumption, ["good._native.native_decide.ax_1"]),
            V("axiomatic", VerificationStatus.RestsOnAssumption, ["knownin1980s", "Lean.ofReduceBool"]),
            V("holey", VerificationStatus.RestsOnAssumption, ["knownin1980s", "sorryAx"]),
            V("wrong", VerificationStatus.Rejected, message: "type mismatch"),
            // Tenet refuses to run compiled code; that is not a wrong proof but one no external kernel can follow.
            V("oldNative", VerificationStatus.Rejected, message: "'Lean.reduceBool' requires running compiled code, which an external checker cannot trust; the declaration cannot be checked"));
        AssuredDeclaration D(string n) => Assert.Single(r.Declarations, d => d.Name == n);

        Assert.Equal(AssuranceLevel.Proved, D("good").Level);
        Assert.Equal(AssuranceLevel.TrustsCompiledCode, D("native").Level);
        Assert.Equal(["native_decide"], D("native").CompiledCode);
        Assert.Empty(D("native").Axioms);
        Assert.Equal(AssuranceLevel.RestsOnAxiom, D("axiomatic").Level);
        Assert.Equal(["Lean.ofReduceBool"], D("axiomatic").CompiledCode);
        Assert.Equal(AssuranceLevel.RestsOnSorry, D("holey").Level);
        Assert.Equal(AssuranceLevel.Rejected, D("wrong").Level);
        Assert.Equal(AssuranceLevel.TrustsCompiledCode, D("oldNative").Level);

        // Worst first, so the top of every list is what to look at.
        Assert.Equal(["wrong", "holey", "axiomatic"], r.Declarations.Take(3).Select(d => d.Name));
        // native_decide's own axiom is not one the project chose to add.
        Assert.Equal(["knownin1980s"], r.Axioms.Select(a => a.Name));
        Assert.Equal(2, r.Axioms[0].Declarations);

        Assert.False(r.Passed);
        Assert.Equal([("rejected", 1), ("sorry", 1)], r.Failures);
    }

    [Fact]
    public void ThePolicyDecidesWhatFails()
    {
        DeclarationVerdict[] verdicts =
        [
            V("a", VerificationStatus.RestsOnAssumption, ["knownin1980s"]),
            V("b", VerificationStatus.RestsOnAssumption, ["b._native.native_decide.ax_1"]),
        ];
        TrustMark[] marks = [new("f", "Fake.Basic", TrustKind.Partial, 3, null)];
        Assert.True(Report(null, marks, verdicts).Passed);

        var strict = new AssurancePolicy(AssurancePolicy.ParseCategories("all"), new HashSet<string>());
        Assert.Equal([("axiom", 1), ("native", 1), ("partial", 1)], Report(strict, marks, verdicts).Failures);

        // A documented axiom can be accepted by name, and then only the others count.
        var allowing = new AssurancePolicy(AssurancePolicy.ParseCategories("axiom"), new HashSet<string> { "knownin1980s" });
        AssuranceReport r = Report(allowing, marks, verdicts);
        Assert.True(r.Passed);
        Assert.True(Assert.Single(r.Axioms).Allowed);

        Assert.Equal(["implemented_by", "sorry"], AssurancePolicy.ParseCategories(" Sorry , implemented-by ").Order());
        Assert.Empty(AssurancePolicy.ParseCategories("none"));
        Assert.Throws<FormatException>(() => AssurancePolicy.ParseCategories("sorry,typo"));
    }

    [Fact]
    public void WritesEachFormatForWhoReadsIt()
    {
        string file = Path.Combine(Fake.Root, "Fake", "Basic.lean");
        AssuranceReport r = Assurance.Build(Fake,
            new VerificationReport("4.34.0", 1, 1, TimeSpan.FromSeconds(1),
                [V("good", VerificationStatus.Verified), V("holey", VerificationStatus.RestsOnAssumption, ["sorryAx"]), V("axiomatic", VerificationStatus.RestsOnAssumption, ["myAx"])]),
            [new TrustMark("ffi", "Fake.Basic", TrustKind.Extern, 7, file)],
            AssurancePolicy.Default, commit: "abc123", sourceFileOf: _ => file);

        string md = Assurance.ToMarkdown(r);
        Assert.Contains("### Assurance report: Fake at `abc123`", md, StringComparison.Ordinal);
        Assert.Contains("❌ Failed: sorry (1). 1 declaration of 3 proved outright (33.3%).", md, StringComparison.Ordinal);
        Assert.Contains("| `ffi` | `@[extern]` | Fake/Basic.lean:7 |", md, StringComparison.Ordinal);

        using JsonDocument json = JsonDocument.Parse(Assurance.ToJson(r));
        Assert.False(json.RootElement.GetProperty("passed").GetBoolean());
        Assert.Equal(1, json.RootElement.GetProperty("counts").GetProperty("restsOnSorry").GetInt32());
        Assert.Equal("Fake/Basic.lean", json.RootElement.GetProperty("trustSurface")[0].GetProperty("location").GetProperty("file").GetString());

        // SARIF: what the policy fails on is an error, the rest are notes, each at its line.
        using JsonDocument sarif = JsonDocument.Parse(Assurance.ToSarif(r));
        var results = sarif.RootElement.GetProperty("runs")[0].GetProperty("results").EnumerateArray()
            .Select(x => (Rule: x.GetProperty("ruleId").GetString(), Level: x.GetProperty("level").GetString())).ToList();
        Assert.Contains(("sorry", "error"), results);
        Assert.Contains(("axiom", "note"), results);
        Assert.Contains(("extern", "note"), results);
        Assert.Equal("2.1.0", sarif.RootElement.GetProperty("version").GetString());

        using JsonDocument badge = JsonDocument.Parse(Assurance.ToBadge(r));
        Assert.Equal("1 proved, 1 sorry, 1 on axioms", badge.RootElement.GetProperty("message").GetString());
        Assert.Equal("red", badge.RootElement.GetProperty("color").GetString());

        string html = Assurance.ToHtml(r);
        Assert.StartsWith("<!doctype html>", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<script", html, StringComparison.Ordinal);
        Assert.Contains("<code>@[extern]</code>", html, StringComparison.Ordinal);
    }

    [Fact]
    public void AnAssuranceWorkflowIsAddedOnce()
    {
        string root = Directory.CreateTempSubdirectory("leanstudio-assurance-ci").FullName;
        try
        {
            string? path = GitHub.AddAssuranceWorkflow(root, "1.1.0");
            Assert.NotNull(path);
            string text = File.ReadAllText(path);
            Assert.Contains("uses: keithadler/leanstudio@v1.1.0", text, StringComparison.Ordinal);
            Assert.Contains("security-events: write", text, StringComparison.Ordinal);
            Assert.Null(GitHub.AddAssuranceWorkflow(root, "1.1.0"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task TheCommandLineSaysWhatItNeeds()
    {
        var output = new StringWriter();
        var error = new StringWriter();
        Assert.Equal(0, await Assurance.RunCommandLineAsync(["--verify", "--help"], output, error, TestContext.Current.CancellationToken));
        Assert.Contains("--fail-on LIST", output.ToString(), StringComparison.Ordinal);
        Assert.Equal(2, await Assurance.RunCommandLineAsync(["--verify", "--fail-on", "sory"], output, error, TestContext.Current.CancellationToken));
        Assert.Contains("'sory' is not one of", error.ToString(), StringComparison.Ordinal);
        Assert.Equal(2, await Assurance.RunCommandLineAsync(["--verify", "--project", "/no/such/folder"], output, error, TestContext.Current.CancellationToken));
    }
}

/// <summary>The assurance report against a real build: every kind of trust Lean lets a project widen, read back.</summary>
[Collection(Lean.Collection)]
public sealed class AssuranceLeanTests
{
    private const string Source = """
        theorem byNative : 2 ^ 20 = 1048576 := by native_decide

        def slowAdd (a b : Nat) : Nat := a + b
        def fastAdd (a b : Nat) : Nat := b + a
        attribute [implemented_by fastAdd] slowAdd

        @[extern "lean_trust_add"]
        def externAdd (a b : Nat) : Nat := a + b

        unsafe def peek (n : Nat) : Nat := n

        partial def loop (n : Nat) : Nat := if n = 0 then 0 else loop (n - 1)

        opaque secret : Nat

        axiom myAxiom : 1 = 1

        theorem usesAxiom : 1 = 1 := myAxiom

        theorem unfinished : 3 = 3 := sorry

        theorem usesNative : 2 ^ 20 = 1048576 ∧ 1 = 1 := ⟨byNative, rfl⟩

        theorem clean : 1 + 1 = 2 := rfl

        -- Recursive, but not partial: Lean compiles these through `._unsafe_rec` helpers too.
        def countdown : Nat → Nat → Nat
          | 0, a => a
          | n + 1, a => countdown n (a + 1)

        def halve (n : Nat) : Nat := if h : n < 2 then 0 else 1 + halve (n / 2)
        termination_by n
        decreasing_by omega
        """;

    [Fact]
    public async Task FindsSorryAxiomsNativeDecideAndTheTrustSurface()
    {
        Lean.RequireLean();
        string root = Directory.CreateTempSubdirectory("leanstudio-trust").FullName;
        File.WriteAllText(Path.Combine(root, "lean-toolchain"), Lean.Toolchain + "\n");
        File.WriteAllText(Path.Combine(root, "lakefile.toml"), "name = \"Trust\"\ndefaultTargets = [\"Trust\"]\n\n[[lean_lib]]\nname = \"Trust\"\n");
        File.WriteAllText(Path.Combine(root, "Trust.lean"), Source);
        try
        {
            // The command line builds the project itself.
            var output = new StringWriter();
            string json = Path.Combine(root, "out", "report.json");
            int code = await Assurance.RunCommandLineAsync(["--verify", "--project", root, "--json", json], output, new StringWriter(), TestContext.Current.CancellationToken);
            Assert.Equal(1, code); // the sorry fails the default policy
            Assert.Contains("❌ Failed: sorry (1).", output.ToString(), StringComparison.Ordinal);
            Assert.True(File.Exists(json));

            AssuranceReport r = await Assurance.RunAsync(new LeanProject(root), AssurancePolicy.Default, ct: TestContext.Current.CancellationToken);
            AssuredDeclaration D(string n) => Assert.Single(r.Declarations, d => d.Name == n);
            Assert.Equal(AssuranceLevel.Proved, D("clean").Level);
            Assert.Equal(AssuranceLevel.Proved, D("fastAdd").Level);
            Assert.Equal(AssuranceLevel.Proved, D("countdown").Level);
            Assert.Equal(AssuranceLevel.TrustsCompiledCode, D("byNative").Level);
            Assert.Equal(["native_decide"], D("byNative").CompiledCode);
            Assert.Equal(AssuranceLevel.TrustsCompiledCode, D("usesNative").Level);
            Assert.Equal(AssuranceLevel.RestsOnAxiom, D("usesAxiom").Level);
            Assert.Equal(AssuranceLevel.RestsOnSorry, D("unfinished").Level);
            Assert.Equal(20, D("unfinished").Line);
            Assert.Equal(0, r.Count(AssuranceLevel.Rejected));
            Assert.Equal(["myAxiom"], r.Axioms.Select(a => a.Name));

            Assert.Equal(
                [("slowAdd", TrustKind.ImplementedBy), ("externAdd", TrustKind.Extern), ("peek", TrustKind.Unsafe), ("loop", TrustKind.Partial), ("secret", TrustKind.Opaque)],
                r.Marks.Select(m => (m.Name, m.Kind)));
            Assert.Equal([3, 8, 10, 12, 14], r.Marks.Select(m => m.Line ?? 0));
            Assert.All(r.Marks, m => Assert.Equal(Path.Combine(root, "Trust.lean"), m.SourceFile));

            var strict = new AssurancePolicy(AssurancePolicy.ParseCategories("all"), new HashSet<string> { "myAxiom" });
            AssuranceReport s = await Assurance.RunAsync(new LeanProject(root), strict, ct: TestContext.Current.CancellationToken);
            Assert.Equal(
                ["sorry", "native", "implemented_by", "extern", "unsafe", "partial", "opaque", "unstated"],
                s.Failures.Select(f => f.Category));
        }
        finally
        {
            Lean.DeleteTree(root);
        }
    }
}

/// <summary>What the theorems are about: which definitions some theorem's statement mentions, read from a real build.</summary>
[Collection(Lean.Collection)]
public sealed class SpecCoverageTests
{
    private const string Source = """
        structure Point where
          x : Nat
          y : Nat
        deriving Repr, BEq, DecidableEq

        instance : Add Point := ⟨fun a b => ⟨a.x + b.x, a.y + b.y⟩⟩

        def Point.swap (p : Point) : Point := ⟨p.y, p.x⟩

        def double (n : Nat) : Nat := n + n

        def helper (n : Nat) : Nat := double n + 1

        abbrev Pair := Nat × Nat

        def IsEven (n : Nat) : Prop := n % 2 = 0

        theorem swap_swap (p : Point) : p.swap.swap = p := rfl

        theorem double_eq (n : Nat) : double n = 2 * n := by unfold double; omega

        theorem double_even (n : Nat) : IsEven (double n) := by unfold IsEven double; omega

        -- `helper` only in the proof: that says nothing about it.
        theorem three : 3 = 3 := by have := helper 1; rfl
        """;

    [Fact]
    public async Task ListsWhatEachDefinitionHasTheoremsAbout()
    {
        Lean.RequireLean();
        string root = Directory.CreateTempSubdirectory("leanstudio-coverage").FullName;
        File.WriteAllText(Path.Combine(root, "lean-toolchain"), Lean.Toolchain + "\n");
        File.WriteAllText(Path.Combine(root, "lakefile.toml"), "name = \"Cov\"\ndefaultTargets = [\"Cov\"]\n\n[[lean_lib]]\nname = \"Cov\"\n");
        File.WriteAllText(Path.Combine(root, "Cov.lean"), Source);
        var project = new LeanProject(root);
        try
        {
            var build = await Lake.BuildAsync(project, ct: TestContext.Current.CancellationToken);
            Assert.True(build.Success, build.Output);
            using TenetWorkspace ws = TenetWorkspace.Open(project);

            // Projections, instances (written or derived), a type abbreviation and a predicate are not code to state things about.
            IReadOnlyList<StatedDefinition> coverage = ws.Coverage(TestContext.Current.CancellationToken);
            Assert.Equal(["Point.swap", "double", "helper"], coverage.Select(c => c.Definition.Name));
            Assert.Equal(["swap_swap"], coverage[0].Theorems.Select(t => t.Name));
            Assert.Equal(["double_eq", "double_even"], coverage[1].Theorems.Select(t => t.Name));
            Assert.Empty(coverage[2].Theorems);
            Assert.Equal(12, coverage[2].Definition.Line);

            Assert.Equal(["double_even"], ws.TheoremsAbout("IsEven", TestContext.Current.CancellationToken).Select(t => t.Name));
            string said = SpecCoverage.ProvedAbout("double", ws.TheoremsAbout("double", TestContext.Current.CancellationToken), root);
            Assert.Contains("2 theorems state something about `double`", said, StringComparison.Ordinal);
            Assert.Contains("`double_eq` (Cov.lean:20): `double_eq` is a theorem. It says that for any n (a natural number), double n equals 2 * n.", said, StringComparison.Ordinal);
            Assert.Contains("No theorem in the project states anything about `helper`", SpecCoverage.ProvedAbout("helper", [], root), StringComparison.Ordinal);

            // In the assurance report: informational by default, a failure when asked for.
            AssuranceReport r = await Assurance.RunAsync(ws, AssurancePolicy.Default, ct: TestContext.Current.CancellationToken);
            Assert.True(r.Passed);
            Assert.Equal(3, r.Definitions);
            Assert.Equal(["helper"], r.UnstatedDefinitions.Select(d => d.Name));
            Assert.Contains("2 of 3 definitions have a theorem", Assurance.ToMarkdown(r), StringComparison.Ordinal);
            var strict = new AssurancePolicy(AssurancePolicy.ParseCategories("unstated"), new HashSet<string>());
            Assert.Equal([("unstated", 1)], (await Assurance.RunAsync(ws, strict, ct: TestContext.Current.CancellationToken)).Failures);
        }
        finally
        {
            Lean.DeleteTree(root);
        }
    }
}

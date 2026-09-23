using LeanStudio.Core.Projects;
using LeanStudio.Core.Workflow;
using LeanStudio.Lsp;

namespace LeanStudio.Tests;

[Collection(Lean.Collection)]
public sealed class PowerTests
{
    [Theory]
    [InlineData("double", "l_double")]
    [InlineData("Foo.greet", "l_Foo_greet")]
    [InlineData("add_comm'", "l_add__comm_x27")]
    [InlineData("μ", "l_00_u03bc")]
    [InlineData("Nat.«weird name»", "l_Nat_weird_x20name")]
    public void MangleNamesTheWayLeanDoes(string lean, string c) => Assert.Equal(c, EmittedC.Mangle(lean));

    private const string Source = """
        def double (n : Nat) : Nat := n + n
        def add_comm' (a b : Nat) : Nat := a + b
        namespace Foo
        def sumList : List Nat → Nat
          | [] => 0
          | x :: xs => x + sumList xs
        theorem t : 1 = 1 := rfl
        end Foo
        def μ (x : Nat) := x * 2
        """;

    [Fact]
    public async Task EmitsCAndFindsEachDefinitionsFunctions()
    {
        Lean.RequireLean();
        string dir = Directory.CreateTempSubdirectory("leanstudio-c").FullName;
        File.WriteAllText(Path.Combine(dir, "lean-toolchain"), Lean.Toolchain + "\n");
        string file = Path.Combine(dir, "C.lean");
        File.WriteAllText(file, Source);
        var (c, error) = await EmittedC.EmitAsync(new LeanProject(dir), file, Source, TestContext.Current.CancellationToken);
        Assert.True(c is not null, error);

        var dbl = EmittedC.For(c, "double");
        Assert.Equal("l_double", dbl[0].Name);
        Assert.Contains(dbl, f => f.Name == "l_double___boxed");
        Assert.Contains("lean_nat_add", dbl[0].Code, StringComparison.Ordinal);
        Assert.EndsWith("}", dbl[0].Code, StringComparison.Ordinal);
        Assert.Equal("l_Foo_sumList", EmittedC.For(c, "Foo.sumList")[0].Name);
        Assert.Equal("l_add__comm_x27", EmittedC.For(c, "add_comm'")[0].Name);
        Assert.Equal("l_00_u03bc", EmittedC.For(c, "μ")[0].Name);
        Assert.Empty(EmittedC.For(c, "Foo.t"));

        string[] lines = Source.Split('\n');
        Assert.Equal(("def", "double"), EmittedC.DeclarationAt(lines, 0));
        Assert.Equal(("def", "Foo.sumList"), EmittedC.DeclarationAt(lines, 5));
        Assert.Equal(("theorem", "Foo.t"), EmittedC.DeclarationAt(lines, 6));
        Assert.Equal(("def", "μ"), EmittedC.DeclarationAt(lines, 8));
    }

    [Fact]
    public async Task ASubtermOfAGoalHasATypeAndDocs()
    {
        Lean.RequireLean();
        string dir = Lean.Sample("Demo");
        await using var server = new LeanServer(new LeanServerCommand(Lean.Executable!, ["--server"], dir));
        string uri = LeanServer.UriOf(Path.Combine(dir, "Demo.lean"));
        var ct = TestContext.Current.CancellationToken;
        await server.StartAsync(ct);
        await server.OpenAsync(uri, File.ReadAllText(Path.Combine(dir, "Demo.lean")));
        await server.WaitForElaborationAsync(uri, ct).WaitAsync(Lean.Patience, ct);
        var pos = new Position(5, 4);
        InteractiveGoals goals = await server.InteractiveGoalsAsync(uri, pos, ct, keepReferences: true);
        TaggedString target = goals.Goals[0].Type;
        // The whole target (an equation) and a variable inside it.
        TaggedSpan whole = target.Spans.MaxBy(s => s.Length)!;
        SubtermInfo? eq = await server.InspectAsync(uri, pos, whole.Reference!, ct);
        Assert.NotNull(eq);
        Assert.Equal("Prop", eq.Type);
        Assert.Contains("equality", eq.Doc, StringComparison.OrdinalIgnoreCase);
        TaggedSpan b = target.Spans.First(s => target.Text.Substring(s.Start, s.Length) == "b");
        SubtermInfo? var = await server.InspectAsync(uri, pos, b.Reference!, ct);
        Assert.Equal("Nat", var?.Type);
        await server.ReleaseAsync(uri, goals.References().ToList());
    }

    [Fact]
    public void ReplacesAcrossTextWithRegexGroups()
    {
        Assert.Equal(("bar baz bar", 2), Refactor.ReplaceInText("foo baz foo", "foo", "bar", true, false));
        Assert.Equal(("Bar", 1), Refactor.ReplaceInText("foo", "FOO", "Bar", false, false));
        Assert.Equal(("foo", 0), Refactor.ReplaceInText("foo", "FOO", "Bar", true, false));
        Assert.Equal(("theorem add_zero_left", 1), Refactor.ReplaceInText("lemma add_zero_left", @"^lemma (\w+)", "theorem $1", true, true));
    }

    [Fact]
    public void PlansReplacementsAndPrefersOpenText()
    {
        string dir = Directory.CreateTempSubdirectory("leanstudio-replace").FullName;
        File.WriteAllText(Path.Combine(dir, "A.lean"), "def oldName := 1\n#eval oldName\n");
        File.WriteAllText(Path.Combine(dir, "B.lean"), "def other := 2\n");
        File.WriteAllText(Path.Combine(dir, "C.lean"), "-- oldName on disk\n");
        var plan = Refactor.Plan(dir, "oldName", "newName", true, false,
            p => p.EndsWith("C.lean", StringComparison.Ordinal) ? "unsaved text without the name" : null, TestContext.Current.CancellationToken);
        FileReplacement a = Assert.Single(plan);
        Assert.Equal(2, a.Count);
        Assert.Equal("def newName := 1\n#eval newName\n", a.NewText);
    }

    [Fact]
    public void RenamingAModuleRewritesItsImports()
    {
        string dir = Directory.CreateTempSubdirectory("leanstudio-rename").FullName;
        Directory.CreateDirectory(Path.Combine(dir, "Proj"));
        File.WriteAllText(Path.Combine(dir, "Proj", "Util.lean"), "def u := 1\n");
        File.WriteAllText(Path.Combine(dir, "Proj", "Main.lean"), "import Proj.Util\nimport Proj.Utility\n#eval u\n");
        File.WriteAllText(Path.Combine(dir, "Proj.lean"), "import Proj.Main Proj.Util\n");
        var changed = Refactor.RenameModule(dir, Path.Combine(dir, "Proj", "Util.lean"), Path.Combine(dir, "Proj", "Tools", "Basic.lean"));
        Assert.Equal(2, changed.Count);
        Assert.Equal("import Proj.Tools.Basic\nimport Proj.Utility\n#eval u\n", File.ReadAllText(Path.Combine(dir, "Proj", "Main.lean")));
        Assert.Equal("import Proj.Main Proj.Tools.Basic\n", File.ReadAllText(Path.Combine(dir, "Proj.lean")));
        Assert.True(File.Exists(Path.Combine(dir, "Proj", "Tools", "Basic.lean")));
        Assert.False(File.Exists(Path.Combine(dir, "Proj", "Util.lean")));
    }
}

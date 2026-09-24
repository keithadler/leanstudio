using LeanStudio.Core.Workflow;
using LeanStudio.Lsp;

namespace LeanStudio.Tests;

/// <summary>Lean's C FFI: finding bindings and C functions, checking them, stubs that compile, and clangd.</summary>
[Collection(Lean.Collection)]
public sealed class FfiTests
{
    private const string LeanSource = """
        namespace Native

        /-- Adds in C. -/
        @[extern "my_add"]
        opaque add (a b : UInt32) : UInt32

        @[extern "my_greet"]
        opaque greet (name : @& String) : IO Unit

        @[extern "my_missing"] opaque missing : Nat → Nat

        @[extern "lean_nat_add"] opaque natAdd : Nat → Nat → Nat

        -- @[extern "not_real"] in a comment
        end Native
        """;

    private const string CSource = """
        #include <lean/lean.h>

        /* uint32_t not_a_function(void); in a comment */
        LEAN_EXPORT uint32_t my_add(uint32_t a, uint32_t b) {
            if (a > b) { return a - b + b; }
            return a + b;
        }

        LEAN_EXPORT lean_obj_res my_greet(b_lean_obj_arg name) {
            return lean_io_result_mk_ok(lean_box(0));
        }

        static int helper(void);
        """;

    [Fact]
    public void FindsBindingsAndFunctions()
    {
        var externs = Ffi.ExternsIn("A.lean", LeanSource.Split('\n')).ToList();
        Assert.Equal(["add", "greet", "missing", "natAdd"], externs.Select(e => e.LeanName));
        Assert.Equal(["my_add", "my_greet", "my_missing", "lean_nat_add"], externs.Select(e => e.CName));
        Assert.Equal("(a b : UInt32) : UInt32", externs[0].Signature);
        Assert.Equal(3, externs[0].Line);

        var fns = Ffi.CFunctionsIn("a.c", CSource).ToList();
        Assert.Equal(["my_add", "my_greet", "helper"], fns.Select(f => f.Name));
        Assert.Equal([2, 1, 0], fns.Select(f => f.Parameters));
        Assert.Equal([true, true, false], fns.Select(f => f.IsDefinition));
        Assert.Equal(3, fns[0].Line);
    }

    [Fact]
    public void ReadsTheCallingConvention()
    {
        var (ps, result, io) = Ffi.Parse("(name : @& String) (n : UInt64) : IO UInt32");
        Assert.True(io);
        Assert.Equal(["name", "n"], ps.Select(p => p.Name));
        Assert.Equal("b_lean_obj_arg", ps[0].Type.C(false));
        Assert.Equal("uint64_t", ps[1].Type.C(false));
        Assert.Equal("uint32_t", result.C(true));
        Assert.Equal(3, Ffi.Arity(new ExternBinding("f", "f", "", 0, 0, "(name : @& String) (n : UInt64) : IO UInt32")));
        Assert.Equal(2, Ffi.Arity(new ExternBinding("g", "g", "", 0, 0, ": Nat → @& Array Nat → Nat")));
        Assert.Equal("my_add", Ffi.CNameAt(LeanSource.Split('\n'), 4)); // on the opaque under the attribute
        Assert.Equal("my_add", Ffi.CNameAt(LeanSource.Split('\n'), 3)); // on the attribute
        Assert.Null(Ffi.CNameAt(LeanSource.Split('\n'), 0));
    }

    [Fact]
    public void ChecksBindingsAgainstTheCFiles()
    {
        string root = Directory.CreateTempSubdirectory("leanstudio-ffi").FullName;
        try
        {
            File.WriteAllText(Path.Combine(root, "A.lean"), LeanSource);
            Directory.CreateDirectory(Path.Combine(root, "c"));
            // my_greet forgets the IO world argument.
            File.WriteAllText(Path.Combine(root, "c", "native.c"), CSource);
            var (externs, functions, problems) = Ffi.Check(root);
            Assert.Equal(4, externs.Count);
            Assert.Contains(functions, f => f.Name == "my_add");
            Assert.Equal(2, problems.Count);
            FfiProblem missing = Assert.Single(problems, p => !p.IsError);
            Assert.Contains("No C function my_missing", missing.Message, StringComparison.Ordinal);
            FfiProblem arity = Assert.Single(problems, p => p.IsError);
            Assert.Contains("my_greet takes 1 argument in C (native.c:9), but Lean passes 2 for greet", arity.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task StubsCompileAgainstLeansHeaders()
    {
        Lean.RequireLean();
        string leanc = Path.Combine(Path.GetDirectoryName(Lean.Executable!)!, OperatingSystem.IsWindows() ? "leanc.exe" : "leanc");
        Assert.SkipWhen(!File.Exists(leanc), "leanc is not next to lean (elan's lean is a proxy)");
        string dir = Directory.CreateTempSubdirectory("leanstudio-stubs").FullName;
        try
        {
            string c = "#include <lean/lean.h>\n\n" + string.Join("\n", new[]
            {
                new ExternBinding("add", "s_add", "", 0, 0, "(a b : UInt32) : UInt32"),
                new ExternBinding("greet", "s_greet", "", 0, 0, "(name : @& String) : IO Unit"),
                new ExternBinding("count", "s_count", "", 0, 0, "(xs : Array Nat) (f : Float) : IO UInt64"),
                new ExternBinding("mk", "s_mk", "", 0, 0, ": Nat → String"),
            }.Select(Ffi.Stub));
            File.WriteAllText(Path.Combine(dir, "lean-toolchain"), Lean.Toolchain + "\n");
            string file = Path.Combine(dir, "stubs.c");
            File.WriteAllText(file, c);
            var r = await Core.Processes.ProcessRunner.RunAsync(leanc, ["-c", file, "-o", Path.Combine(dir, "stubs.o"), "-Werror", "-Wall", "-Wno-unused-parameter"], dir, ct: TestContext.Current.CancellationToken);
            Assert.True(r.Success, c + "\n" + r.Output);
            Assert.Contains("LEAN_EXPORT lean_obj_res s_greet(b_lean_obj_arg name, lean_obj_arg world)", c, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task ClangdKnowsLeansRuntime()
    {
        Lean.RequireLean();
        string? clangd = CLanguageServer.Find();
        Assert.SkipWhen(clangd is null, "clangd is not installed");
        string dir = Lean.Sample("Demo");
        var project = new Core.Projects.LeanProject(dir);
        var filesBefore = Directory.EnumerateFileSystemEntries(dir).Order().ToList();
        await using var server = new CLanguageServer(clangd!, dir, Ffi.CompileFlags(project));
        await server.StartAsync(TestContext.Current.CancellationToken);
        string uri = LeanServer.UriOf(Path.Combine(dir, "native.c"));
        var got = new TaskCompletionSource<IReadOnlyList<Diagnostic>>(TaskCreationOptions.RunContinuationsAsynchronously);
        server.DiagnosticsPublished += (u, d) => { if (u == uri) { got.TrySetResult(d); } };
        await server.OpenAsync(uri, "#include <lean/lean.h>\n\nlean_obj_res f(void) {\n    return lean_box(0);\n}\n\nint g(void) { return undefined_thing; }\n");
        IReadOnlyList<Diagnostic> diags = await got.Task.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
        Diagnostic d = Assert.Single(diags, x => x.Severity == DiagnosticSeverity.Error);
        Assert.Contains("undefined_thing", d.Message, StringComparison.Ordinal); // and nothing about lean.h: the header was found
        Hover? h = await server.HoverAsync(uri, new Position(3, 12), TestContext.Current.CancellationToken);
        Assert.Contains("lean_box", h?.Contents ?? "", StringComparison.Ordinal);

        // Completion and go to definition, into Lean's own header.
        IReadOnlyList<CompletionItem> items = await server.CompletionAsync(uri, new Position(3, 14), TestContext.Current.CancellationToken);
        Assert.Contains(items, i => i.Label.Contains("lean_box", StringComparison.Ordinal));
        IReadOnlyList<Location> def = await server.DefinitionAsync(uri, new Position(3, 12), TestContext.Current.CancellationToken);
        Assert.Contains(def, l => l.Uri.EndsWith("/lean/lean.h", StringComparison.Ordinal));

        // Nothing was written into the project (no compile_commands.json, no .clangd).
        Assert.Equal(filesBefore, Directory.EnumerateFileSystemEntries(dir).Order().ToList());
    }
}

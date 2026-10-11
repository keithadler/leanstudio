using System.Diagnostics;
using LeanOut;
using LeanStudio.Plugins;

namespace LeanStudio.Tests;

public sealed class DeclarationLocatorTests
{
    private const string Source = """
        import Mathlib

        namespace Foo
        namespace Bar

        theorem baz (n : Nat) : n = n := by
          rfl

        end Bar

        section
        protected def qux : Nat := 1
        end

        private theorem hidden : True := trivial

        instance : Inhabited Nat := ⟨0⟩

        theorem _root_.Top.level : True := trivial
        end Foo

        lemma outside : True := trivial
        """;

    private static DeclarationLocator.Located At(int line) => DeclarationLocator.At(Source, line);

    [Fact]
    public void NamesThe_declaration_with_its_namespaces_applied_most_specific_first()
    {
        Assert.Equal(["Foo.Bar.baz", "Foo.baz", "baz"], At(5).Candidates);
    }

    [Fact]
    public void The_caret_inside_the_proof_still_belongs_to_the_declaration()
    {
        Assert.Equal(At(5).Candidates, At(6).Candidates);
    }

    [Fact]
    public void End_pops_a_namespace_and_a_section_does_not_add_a_name()
    {
        Assert.Equal(["Foo.qux", "qux"], At(12).Candidates);
    }

    [Fact]
    public void Private_and_anonymous_declarations_are_explained_not_guessed()
    {
        Assert.Empty(At(15).Candidates);
        Assert.Contains("private", At(15).Reason);
        Assert.Empty(At(17).Candidates);
        Assert.Contains("no name", At(17).Reason);
    }

    [Fact]
    public void Root_names_ignore_the_namespaces()
    {
        Assert.Equal(["Top.level"], At(19).Candidates);
    }

    [Fact]
    public void After_the_namespace_closes_names_are_bare()
    {
        Assert.Equal(["outside"], At(22).Candidates);
    }

    [Fact]
    public void Above_every_declaration_there_is_nothing()
    {
        Assert.Empty(At(0).Candidates);
        Assert.Contains("not inside", At(0).Reason);
    }

    [Fact]
    public void Module_names_come_from_the_path_with_leading_folders_dropped_as_fallbacks()
    {
        string root = Path.Combine(Path.GetTempPath(), "proj");
        string file = Path.Combine(root, "src", "Demo", "Basic.lean");
        Assert.Equal(["src.Demo.Basic", "Demo.Basic", "Basic"], DeclarationLocator.ModuleCandidates(root, file));
        Assert.Empty(DeclarationLocator.ModuleCandidates(root, Path.Combine(Path.GetTempPath(), "elsewhere", "X.lean")));
    }
}

/// <summary>
/// The plugin against a real Lake project, a real exporter build and the real processes. Needs elan, git and
/// <c>LEANOUT_REPO</c> (a local clone of https://github.com/keithadler/leanout, so nothing is fetched), and is
/// skipped otherwise.
/// </summary>
[Collection(Lean.Collection)]
public sealed class LeanOutPluginEndToEndTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "leanout-e2e-" + Guid.NewGuid().ToString("N"));

    public void Dispose() => Lean.DeleteTree(_root);

    private sealed class Doc(string path, string text, int caretLine) : IPluginDocument
    {
        public string Path => path;
        public string Text => text;
        public int CaretLine => caretLine;
        public int CaretColumn => 0;
        public string Selection => "";
        public void Replace(int offset, int length, string t) { }
        public void Insert(string t) { }
    }

    private sealed class Host(string projectRoot) : IPluginHost
    {
        public List<(string Title, Func<Task> Run)> Commands { get; } = [];
        public List<string> Lines { get; } = [];
        public IPluginDocument? Active { get; set; }
        public string AppVersion => "0.0.0";
        public string? ProjectRoot => projectRoot;
        public IPluginDocument? ActiveDocument => Active;
        public void AddCommand(string title, Func<Task> run) => Commands.Add((title, run));
        public void Log(string line) => Lines.Add(line);
        public Task OpenFileAsync(string path, int line = 0, int column = 0) => Task.CompletedTask;
        public IReadOnlyList<PluginMessage> MessagesOf(string path) => [];
        public Task<PluginProcessResult> CheckLeanAsync(string code, CancellationToken cancellationToken = default) =>
            Task.FromResult(new PluginProcessResult(0, ""));
        public event Action<string>? FileOpened { add { } remove { } }
        public event Action<string>? FileSaved { add { } remove { } }

        public async Task<PluginProcessResult> RunAsync(string program, IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
        {
            string exe = Path.IsPathRooted(program) ? program : Core.Toolchains.Elan.FindExecutable(program) ?? program;
            var psi = new ProcessStartInfo(exe) { WorkingDirectory = projectRoot, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (string a in arguments)
            {
                psi.ArgumentList.Add(a);
            }
            using var p = Process.Start(psi)!;
            Task<string> o = p.StandardOutput.ReadToEndAsync(cancellationToken);
            Task<string> e = p.StandardError.ReadToEndAsync(cancellationToken);
            await p.WaitForExitAsync(cancellationToken);
            return new PluginProcessResult(p.ExitCode, await o + await e);
        }
    }

    [Fact]
    public async Task Axioms_at_the_caret_tell_a_sorry_from_a_clean_proof()
    {
        Lean.RequireLean();
        string? repo = Environment.GetEnvironmentVariable("LEANOUT_REPO");
        Assert.SkipWhen(string.IsNullOrEmpty(repo), "set LEANOUT_REPO to a local clone of leanout to run this");

        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "lean-toolchain"), Lean.Toolchain + "\n");
        File.WriteAllText(Path.Combine(_root, "lakefile.toml"), "name = \"demo\"\ndefaultTargets = [\"Demo\"]\n\n[[lean_lib]]\nname = \"Demo\"\n");
        string source = "theorem clean (n : Nat) : n + 0 = n := rfl\n\ntheorem classical' (p : Prop) : p ∨ ¬p := Classical.em p\n\ntheorem unfinished : 1 = 2 := by sorry\n";
        string file = Path.Combine(_root, "Demo.lean");
        File.WriteAllText(file, source);
        Environment.SetEnvironmentVariable("LEANOUT_HOME", Path.Combine(_root, "leanout-home"));

        var host = new Host(_root);
        new LeanOutPlugin().Initialize(host);
        Func<Task> axioms = host.Commands.Single(c => c.Title.StartsWith("Export: Axioms", StringComparison.Ordinal)).Run;

        PluginProcessResult build = await host.RunAsync("lake", ["build"], TestContext.Current.CancellationToken);
        Assert.True(build.Success, build.Output);

        host.Active = new Doc(file, source, 0);
        await axioms();
        Assert.Contains(host.Lines, l => l.Contains("No axioms", StringComparison.Ordinal));

        host.Lines.Clear();
        host.Active = new Doc(file, source, 2);
        await axioms();
        Assert.Contains(host.Lines, l => l.Contains("axiom Classical.choice", StringComparison.Ordinal));
        Assert.DoesNotContain(host.Lines, l => l.Contains("sorryAx", StringComparison.Ordinal));

        host.Lines.Clear();
        host.Active = new Doc(file, source, 4);
        await axioms();
        Assert.Contains(host.Lines, l => l.Contains("axiom sorryAx", StringComparison.Ordinal) && l.Contains("sorry", StringComparison.Ordinal));

        // A second kernel, when one is installed, re-checks the declaration and what it depends on.
        string tenet = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dotnet", "tools", "tenet");
        if (File.Exists(tenet))
        {
            host.Lines.Clear();
            host.Active = new Doc(file, source, 2);
            await host.Commands.Single(c => c.Title.Contains("Another Kernel", StringComparison.Ordinal)).Run();
            Assert.Contains(host.Lines, l => l.Contains("Tenet accepted classical'", StringComparison.Ordinal));
        }

        // A module export is written next to the build and is well formed.
        host.Lines.Clear();
        host.Active = new Doc(file, source, 0);
        await host.Commands.Single(c => c.Title.Contains("Module to NDJSON", StringComparison.Ordinal)).Run();
        Assert.Contains(host.Lines, l => l.Contains("well formed", StringComparison.Ordinal) && !l.Contains("NOT", StringComparison.Ordinal));
        Assert.True(File.Exists(Path.Combine(_root, ".lake", "leanout", "module.ndjson")));
    }
}

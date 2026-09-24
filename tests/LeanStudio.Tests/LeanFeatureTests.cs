using LeanStudio.Lsp;

namespace LeanStudio.Tests;

[Collection(Lean.Collection)]
public sealed class LeanFeatureTests
{
    // `simp?` rather than `exact?` for the Try this: a library search can take minutes on a cold CI machine.
    private const string Source = """
        def foo (n : Nat) : Nat := n + 1

        theorem t1 (xs : List Nat) : (xs ++ []).length = xs.length := by
          simp?

        theorem t2 : foo 2 = 3 := rfl

        theorem t3 : foo 1 = 2 := rfl
        """;

    private static async Task<(LeanServer Server, string Uri)> OpenAsync()
    {
        string dir = Directory.CreateTempSubdirectory("leanstudio-features").FullName;
        File.WriteAllText(Path.Combine(dir, "lean-toolchain"), Lean.Toolchain + "\n");
        string file = Path.Combine(dir, "T.lean");
        File.WriteAllText(file, Source);
        var server = new LeanServer(new LeanServerCommand(Lean.Executable!, ["--server"], dir));
        string uri = LeanServer.UriOf(file);
        await server.StartAsync(TestContext.Current.CancellationToken);
        await server.OpenAsync(uri, Source);
        await server.WaitForElaborationAsync(uri, TestContext.Current.CancellationToken).WaitAsync(Lean.Patience, TestContext.Current.CancellationToken);
        return (server, uri);
    }

    [Fact]
    public async Task TryThisBecomesAnApplicableEdit()
    {
        Lean.RequireLean();
        var (server, uri) = await OpenAsync();
        await using var _s = server;
        Diagnostic tryThis = Assert.Single(server.DiagnosticsOf(uri), d => d.Message.Contains("Try this", StringComparison.Ordinal));
        IReadOnlyList<CodeAction> actions = await server.CodeActionsAsync(uri, tryThis.Range, [tryThis], TestContext.Current.CancellationToken);
        CodeAction a = Assert.Single(actions, x => x.Title.StartsWith("Try this", StringComparison.Ordinal));
        a = await server.ResolveAsync(a, TestContext.Current.CancellationToken);
        Assert.NotNull(a.Edit);
        string after = WorkspaceEdit.Apply(Source, a.Edit.Changes[uri]);
        Assert.DoesNotContain("simp?", after, StringComparison.Ordinal);
        Assert.Contains("simp only", after, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReferencesRenameSymbolsAndFolding()
    {
        Lean.RequireLean();
        var (server, uri) = await OpenAsync();
        await using var _s = server;
        var ct = TestContext.Current.CancellationToken;
        var foo = new Position(0, 5);
        Assert.Equal(3, (await server.ReferencesAsync(uri, foo, ct: ct)).Count); // def, t2, t3
        Assert.NotNull(await server.PrepareRenameAsync(uri, foo, ct));
        WorkspaceEdit rename = await server.RenameAsync(uri, foo, "bar", ct);
        string renamed = WorkspaceEdit.Apply(Source, rename.Changes[uri]);
        Assert.Contains("def bar", renamed, StringComparison.Ordinal);
        Assert.Contains("bar 2 = 3", renamed, StringComparison.Ordinal);
        Assert.Contains(await server.WorkspaceSymbolsAsync("foo", ct), s => s.Name == "foo");
        Assert.NotEmpty(await server.FoldingRangesAsync(uri, ct));
        Assert.Contains(await server.DocumentSymbolsAsync(uri, ct), s => s.Name == "t2");
    }

    [Fact]
    public async Task FoldsDeclarationsNamespacesAndCommentsAndFindsSymbolsInDependencies()
    {
        Lean.RequireLean();
        string dir = Lean.Sample("Demo");
        await using var server = new LeanServer(new LeanServerCommand(Lean.Executable!, ["--server"], dir));
        var ct = TestContext.Current.CancellationToken;
        await server.StartAsync(ct);
        string uri = LeanServer.UriOf(Path.Combine(dir, "Folds.lean"));
        string[] lines =
        [
            "/-",                                   // 0: a block comment
            "  A long comment,",
            "  over several lines.",
            "-/",
            "namespace Folds",                      // 4: a namespace
            "",
            "theorem t (n : Nat) : n + 0 = n := by", // 6: a declaration
            "  simp",
            "  done",
            "",
            "end Folds",                            // 10
        ];
        await server.OpenAsync(uri, string.Join('\n', lines));
        await server.WaitForElaborationAsync(uri, ct).WaitAsync(Lean.Patience, ct);
        IReadOnlyList<FoldingRange> folds = await server.FoldingRangesAsync(uri, ct);
        // Lean folds the namespace and the declaration; the editor adds the comment, which Lean doesn't fold.
        Assert.Contains(folds, f => f.StartLine == 4 && f.EndLine >= 9);
        Assert.Contains(folds, f => f.StartLine == 6 && f.EndLine >= 7);
        Assert.Equal([(0, 3)], Core.Editing.LeanText.CommentFolds(string.Join('\n', lines)));

        // Go to Symbol searches what the file can see, dependencies and core Lean included.
        // Lean reads its index of core Lean in the background after it starts: ask until it has (as the picker does,
        // asking again on every keystroke).
        IReadOnlyList<SymbolLocation> symbols = [];
        for (int tries = 0; tries < 120 && !symbols.Any(s => s.Name == "Nat.add_comm"); tries++)
        {
            symbols = await server.WorkspaceSymbolsAsync("Nat.add_comm", ct);
            if (!symbols.Any(s => s.Name == "Nat.add_comm"))
            {
                await Task.Delay(500, ct);
            }
        }
        Assert.Contains(symbols, s => s.Name == "Nat.add_comm" && s.Location.Uri.Replace("%5C", "/", StringComparison.OrdinalIgnoreCase).Contains("/Init/", StringComparison.Ordinal));
    }
}

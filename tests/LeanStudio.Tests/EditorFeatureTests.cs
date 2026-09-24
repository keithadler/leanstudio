using LeanStudio.Lsp;

namespace LeanStudio.Tests;

/// <summary>What Lean's server gives an editor beyond goals: semantic tokens, inlay hints, highlights, calls, traces.</summary>
[Collection(Lean.Collection)]
public sealed class EditorFeatureTests
{
    private const string Source = """
        def square (n : Nat) : Nat := n * n

        theorem squareEq (x : Nat) : square x = x * x := by
          rfl

        def usesSquare := square 3 + square 4

        example : Inhabited (Nat × Bool) := by
          set_option trace.Meta.synthInstance true in
          exact inferInstance

        def lengthOf (xs : List α) : Nat := xs.length

        theorem later : 2 + 2 = 4 := sorry
        """;

    private static async Task<(LeanServer Server, string Uri)> OpenAsync()
    {
        string dir = Lean.Sample("Demo");
        var server = new LeanServer(new LeanServerCommand(Lean.Executable!, ["--server"], dir));
        await server.StartAsync(TestContext.Current.CancellationToken);
        string uri = LeanServer.UriOf(Path.Combine(dir, "Features.lean"));
        await server.OpenAsync(uri, Source);
        await server.WaitForElaborationAsync(uri, TestContext.Current.CancellationToken).WaitAsync(Lean.Patience, TestContext.Current.CancellationToken);
        return (server, uri);
    }

    [Fact]
    public void DecodesSemanticTokens()
    {
        // Two tokens on line 1 (columns 2 and 7) and one on line 3, column 4.
        var tokens = LeanServer.DecodeSemanticTokens([1, 2, 3, 0, 1, 0, 5, 4, 1, 0, 2, 4, 1, 2, 3], ["keyword", "variable", "function"], ["declaration", "readonly"]);
        Assert.Equal([(1, 2, "keyword"), (1, 7, "variable"), (3, 4, "function")], tokens.Select(t => (t.Line, t.Start, t.Type)));
        Assert.Equal(["declaration"], tokens[0].Modifiers);
        Assert.Equal(["declaration", "readonly"], tokens[2].Modifiers);
    }

    [Fact]
    public async Task LeanServesTheEditorFeatures()
    {
        Lean.RequireLean();
        var (server, uri) = await OpenAsync();
        await using var _ = server;
        var ct = TestContext.Current.CancellationToken;
        string[] lines = Source.Split('\n');
        string Text(SemanticToken t) => lines[t.Line].Substring(t.Start, t.Length);

        IReadOnlyList<SemanticToken> tokens = await server.SemanticTokensAsync(uri, ct);
        Assert.Contains(tokens, t => t.Line == 0 && Text(t) == "n" && t.Type is "variable" or "parameter");
        Assert.Contains(tokens, t => t.Line == 11 && Text(t) == "length" && t.Type == "property");

        // Every use of `square`, from the cursor on one of them.
        IReadOnlyList<Lsp.Range> uses = await server.DocumentHighlightsAsync(uri, new Position(5, 20), ct);
        Assert.True(uses.Count >= 3, $"{uses.Count} highlights");
        Assert.Contains(uses, r => r.Start.Line == 0);

        // Who uses `square`, and what squareEq uses.
        IReadOnlyList<CallHierarchyItem> roots = await server.PrepareCallHierarchyAsync(uri, new Position(0, 5), ct);
        CallHierarchyItem square = Assert.Single(roots);
        Assert.Equal("square", square.Name);
        IReadOnlyList<CallSite> callers = await server.IncomingCallsAsync(square, ct);
        Assert.Contains(callers, c => c.Item.Name == "usesSquare");

        // The implicit `α` that Lean binds automatically.
        IReadOnlyList<InlayHint> hints = await server.InlayHintsAsync(uri, new Lsp.Range(new Position(0, 0), new Position(lines.Length, 0)), ct);
        Assert.Contains(hints, h => h.Position.Line == 11 && h.Label.Contains('α', StringComparison.Ordinal));

        // The trace tree of instance search, with children fetched when asked for.
        IReadOnlyList<InteractiveMessage> messages = await server.InteractiveMessagesAsync(uri, 7, 10, ct);
        TraceNode root = Assert.Single(messages.SelectMany(m => m.Traces));
        Assert.Equal("Meta.synthInstance", root.Class);
        Assert.Contains("Inhabited (Nat × Bool)", root.Text, StringComparison.Ordinal);
        Assert.True(root.HasChildren);
        IReadOnlyList<TraceNode> children = root.Children.Count > 0 ? root.Children
            : await server.TraceChildrenAsync(uri, new Position(9, 2), root.LazyChildren!, ct);
        Assert.NotEmpty(children);
        Assert.All(children, c => Assert.NotEmpty(c.Class));
    }

    [Fact]
    public async Task FindsTheUserWidgetsAtAPosition()
    {
        Lean.RequireLean();
        var ct = TestContext.Current.CancellationToken;
        string dir = Lean.Sample("Demo");
        await using var server = new LeanServer(new LeanServerCommand(Lean.Executable!, ["--server"], dir));
        await server.StartAsync(ct);
        string uri = LeanServer.UriOf(Path.Combine(dir, "Widgets.lean"));
        await server.OpenAsync(uri, """
            import Lean
            open Lean Widget

            @[widget_module]
            def helloWidget : Widget.Module where
              javascript := "
                import * as React from 'react';
                export default function(props) { return React.createElement('p', {}, 'Hello ' + props.name) }"

            #widget helloWidget with Json.mkObj [("name", Json.str "Lean Studio")]

            theorem plain : True := trivial
            """);
        await server.WaitForElaborationAsync(uri, ct).WaitAsync(Lean.Patience, ct);

        IReadOnlyList<UserWidget> at = await server.WidgetsAtAsync(uri, new Position(9, 3), ct);
        UserWidget w = Assert.Single(at);
        Assert.Equal("helloWidget", w.Id);
        Assert.Empty(await server.WidgetsAtAsync(uri, new Position(11, 3), ct));
    }
}

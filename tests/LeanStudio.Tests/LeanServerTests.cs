using LeanStudio.Lsp;

namespace LeanStudio.Tests;

public sealed class LeanServerTests
{
    private static async Task<(LeanServer Server, string Uri, List<Diagnostic> Diagnostics, TaskCompletionSource Done)> OpenDemoAsync()
    {
        string dir = Lean.Sample("Demo");
        var server = new LeanServer(new LeanServerCommand(Lean.Executable!, ["--server"], dir));
        var diags = new List<Diagnostic>();
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        string uri = LeanServer.UriOf(Path.Combine(dir, "Demo.lean"));
        server.DiagnosticsPublished += (u, d) =>
        {
            if (u == uri)
            {
                lock (diags)
                {
                    diags.Clear();
                    diags.AddRange(d);
                }
            }
        };
        server.FileProgress += (u, p) =>
        {
            if (u == uri && p.Count == 0)
            {
                done.TrySetResult();
            }
        };
        await server.StartAsync(TestContext.Current.CancellationToken);
        await server.OpenAsync(uri, await File.ReadAllTextAsync(Path.Combine(dir, "Demo.lean"), TestContext.Current.CancellationToken));
        await done.Task.WaitAsync(TimeSpan.FromSeconds(90), TestContext.Current.CancellationToken);
        return (server, uri, diags, done);
    }

    [Fact]
    public async Task ReportsSorryAsAWarning()
    {
        Lean.RequireLean();
        var (server, _, diags, _) = await OpenDemoAsync();
        await using var _s = server;
        // Diagnostics can trail the final progress notification by a moment.
        for (int i = 0; i < 50 && diags.Count == 0; i++)
        {
            await Task.Delay(100, TestContext.Current.CancellationToken);
        }
        Diagnostic d = Assert.Single(diags);
        Assert.Equal(DiagnosticSeverity.Warning, d.Severity);
        Assert.Contains("sorry", d.Message, StringComparison.Ordinal);
        Assert.Equal(12, d.Range.Start.Line);
    }

    [Fact]
    public async Task InteractiveGoalsCarryHypothesesAndTheirDiff()
    {
        Lean.RequireLean();
        var (server, uri, _, _) = await OpenDemoAsync();
        await using var _s = server;

        // Before `omega` in the succ case: n, b and the induction hypothesis are in scope.
        InteractiveGoals goals = await server.InteractiveGoalsAsync(uri, new Position(5, 4), TestContext.Current.CancellationToken);
        InteractiveGoal g = Assert.Single(goals.Goals);
        Assert.Equal("succ", g.UserName);
        Assert.Contains(g.Hypotheses, h => h.Names.Contains("ih") && h.Type.Text == "n + b = b + n");
        Assert.True(g.IsRemoved, "omega closes this goal, so Lean marks it removed");

        PlainGoal? plain = await server.PlainGoalAsync(uri, new Position(4, 4), TestContext.Current.CancellationToken);
        Assert.NotNull(plain);
        Assert.Contains("ih : n + b = b + n", Assert.Single(plain.Goals), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConstructorSplitsTheGoalInTwo()
    {
        Lean.RequireLean();
        var (server, uri, _, _) = await OpenDemoAsync();
        await using var _s = server;
        // End of the `constructor` line: the state after it.
        InteractiveGoals after = await server.InteractiveGoalsAsync(uri, new Position(8, 13), TestContext.Current.CancellationToken);
        Assert.Equal(2, after.Goals.Count);
        Assert.Equal("p", after.Goals[0].Type.Text);
        Assert.Equal("q", after.Goals[1].Type.Text);
    }

    [Fact]
    public async Task HoverAndDefinitionAnswer()
    {
        Lean.RequireLean();
        var (server, uri, _, _) = await OpenDemoAsync();
        await using var _s = server;
        Hover? h = await server.HoverAsync(uri, new Position(4, 10), TestContext.Current.CancellationToken); // Nat.succ_add
        Assert.NotNull(h);
        Assert.Contains("succ_add", h.Contents, StringComparison.Ordinal);
        IReadOnlyList<Location> defs = await server.DefinitionAsync(uri, new Position(4, 10), TestContext.Current.CancellationToken);
        Assert.NotEmpty(defs);
    }
}

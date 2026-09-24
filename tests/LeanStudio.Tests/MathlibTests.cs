using System.Text.Json.Nodes;
using LeanStudio.Core.Agents;
using LeanStudio.Mcp;

namespace LeanStudio.Tests;

/// <summary>
/// Against a real Mathlib project: set LEANSTUDIO_MATHLIB_PROJECT to one (made with <c>lake new Demo math</c>, which
/// fetches Mathlib's cache). The weekly Mathlib workflow does; ordinary runs skip these.
/// </summary>
[Collection(Lean.Collection)]
public sealed class MathlibTests
{
    private static string RequireMathlib()
    {
        string? root = Environment.GetEnvironmentVariable("LEANSTUDIO_MATHLIB_PROJECT");
        Assert.SkipWhen(root is null || !Directory.Exists(root), "set LEANSTUDIO_MATHLIB_PROJECT to a Mathlib project to run");
        return root!;
    }

    private static async Task<string> CallAsync(McpServer server, string tool, JsonObject args)
    {
        JsonObject? r = await server.HandleAsync(new JsonObject
        {
            ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "tools/call",
            ["params"] = new JsonObject { ["name"] = tool, ["arguments"] = args },
        }, TestContext.Current.CancellationToken);
        JsonNode result = r!["result"]!;
        string text = result["content"]![0]!["text"]!.GetValue<string>();
        Assert.False(result["isError"]!.GetValue<bool>(), text);
        return text;
    }

    [Fact]
    public async Task ProveItCounterexamplesAndTenetWorkWithMathlib()
    {
        string root = RequireMathlib();
        string dir = Path.Combine(root, "LeanStudioCheck");
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, "Real.lean");
        File.WriteAllText(file, """
            import Mathlib

            theorem sq_nonneg' (x : ℝ) : 0 ≤ x ^ 2 := by
              sorry

            theorem lin (a b : ℝ) (h1 : a < b) (h2 : 0 < a) : 0 < b := by
              sorry

            theorem ring_id (a b : ℚ) : (a + b) ^ 2 = a ^ 2 + 2 * a * b + b ^ 2 := by
              sorry

            theorem false_one (n : ℕ) (h : 3 < n) : n ^ 2 < 20 := by
              sorry
            """);
        try
        {
            await using var bench = new Workbench(root);
            McpServer server = LeanTools.Create(bench, "test");
            string prove = await CallAsync(server, "prove", new JsonObject { ["path"] = file });
            Assert.Contains("best: positivity", prove, StringComparison.Ordinal);
            Assert.Contains("best: linarith", prove, StringComparison.Ordinal);
            Assert.Contains("best: ring", prove, StringComparison.Ordinal);
            Assert.Contains("FALSE as stated: counterexample n = 5", prove, StringComparison.Ordinal);

            string profile = await CallAsync(server, "profile", new JsonObject { ["path"] = file });
            Assert.Contains("in total", profile, StringComparison.Ordinal);

            string hits = await CallAsync(server, "search_declarations", new JsonObject { ["query"] = "sq_nonneg" });
            Assert.Contains("sq_nonneg", hits, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task ImportsAndLintersWorkWithMathlib()
    {
        string root = RequireMathlib();
        var project = new Core.Projects.LeanProject(root);
        // The project's library folder (Name/ next to Name.lean), so the file is a module Lake can lint.
        string lib = Directory.GetFiles(root, "*.lean").Select(Path.GetFileNameWithoutExtension).First(n => Directory.Exists(Path.Combine(root, n!)))!;
        string file = Path.Combine(root, lib, "LeanStudioPro.lean");
        const string text = """
            import Mathlib.Tactic.Ring
            import Mathlib.Data.Nat.Prime.Basic
            import Mathlib.Order.Basic
            import Mathlib.Topology.Basic

            theorem sq_expand (a b : ℕ) : (a + b) ^ 2 = a ^ 2 + 2 * a * b + b ^ 2 := by ring

            theorem two_prime : Nat.Prime 2 := Nat.prime_two

            def noDoc (n : ℕ) : ℕ := n + 1
            """;
        File.WriteAllText(file, text);
        try
        {
            var ct = TestContext.Current.CancellationToken;
            Core.Workflow.ImportReport report = await Core.Workflow.ImportCheck.RunAsync(project, file, text, ct);
            Assert.Equal([("Mathlib.Tactic.Ring", true), ("Mathlib.Data.Nat.Prime.Basic", true), ("Mathlib.Order.Basic", false), ("Mathlib.Topology.Basic", false)],
                report.Imports.Select(i => (i.Module, i.Keep)));
            Assert.Equal("Mathlib.Order.Basic is already imported by Mathlib.Tactic.Ring", report.Imports[2].Explanation);

            Assert.Equal("linter.mathlibStandardSet", Core.Workflow.Lint.LintersFor(project));
            var (findings, error) = await Core.Workflow.Lint.RunAsync(project, file, ct);
            Assert.Null(error);
            Assert.Contains(findings, f => f.Linter == "linter.style.header" && f.Path == file);          // Mathlib's style linters
            Assert.Contains(findings, f => f.Linter == "docBlame" && f.Line == 9 && f.Path == file);      // and Batteries' environment linters
        }
        finally
        {
            File.Delete(file);
        }
    }
}

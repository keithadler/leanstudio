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
}

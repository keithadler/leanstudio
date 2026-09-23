using System.Text.Json.Nodes;
using LeanStudio.Core.Agents;
using LeanStudio.Mcp;

namespace LeanStudio.Tests;

[Collection(Lean.Collection)]
public sealed class McpTests
{
    private static JsonObject Request(int id, string method, JsonObject? p = null) =>
        new() { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = p ?? new JsonObject() };

    private static async Task<(string Text, bool IsError)> CallAsync(McpServer server, string tool, JsonObject args)
    {
        JsonObject? r = await server.HandleAsync(Request(9, "tools/call", new JsonObject { ["name"] = tool, ["arguments"] = args }), TestContext.Current.CancellationToken);
        JsonNode result = r!["result"]!;
        return (result["content"]![0]!["text"]!.GetValue<string>(), result["isError"]!.GetValue<bool>());
    }

    [Fact]
    public async Task SpeaksTheProtocol()
    {
        await using var bench = new Workbench(Lean.Sample("Proofs"));
        McpServer server = LeanTools.Create(bench, "test");

        JsonObject init = (await server.HandleAsync(Request(1, "initialize", new JsonObject
        {
            ["protocolVersion"] = "2025-03-26",
            ["capabilities"] = new JsonObject(),
            ["clientInfo"] = new JsonObject { ["name"] = "test", ["version"] = "0" },
        }), TestContext.Current.CancellationToken))!;
        Assert.Equal("2025-03-26", init["result"]!["protocolVersion"]!.GetValue<string>());
        Assert.Equal("leanstudio", init["result"]!["serverInfo"]!["name"]!.GetValue<string>());
        Assert.Contains("check_file", init["result"]!["instructions"]!.GetValue<string>(), StringComparison.Ordinal);

        Assert.Null(await server.HandleAsync(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "notifications/initialized" }, TestContext.Current.CancellationToken));

        JsonObject list = (await server.HandleAsync(Request(2, "tools/list"), TestContext.Current.CancellationToken))!;
        var names = list["result"]!["tools"]!.AsArray().Select(t => t!["name"]!.GetValue<string>()).ToList();
        Assert.Contains("check_file", names);
        Assert.Contains("goals", names);
        Assert.Contains("verify", names);
        Assert.Contains("studio_context", names);
        Assert.All(list["result"]!["tools"]!.AsArray(), t => Assert.Equal("object", t!["inputSchema"]!["type"]!.GetValue<string>()));

        JsonObject unknown = (await server.HandleAsync(Request(3, "no/such/method"), TestContext.Current.CancellationToken))!;
        Assert.Equal(-32601, unknown["error"]!["code"]!.GetValue<int>());

        var (text, isError) = await CallAsync(server, "check_file", new JsonObject());
        Assert.True(isError);
        Assert.Contains("path", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ChecksFilesReadsGoalsAndRunsSnippets()
    {
        Lean.RequireLean();
        await using var bench = new Workbench(Lean.Sample("Proofs"));
        McpServer server = LeanTools.Create(bench, "test");

        var (report, err) = await CallAsync(server, "check_file", new JsonObject { ["path"] = "Proofs/Basic.lean" });
        Assert.False(err, report);
        Assert.Contains("sorry", report, StringComparison.Ordinal);
        Assert.Contains("Basic.lean:20:9: warning", report, StringComparison.Ordinal);

        // Unsaved text: replace the sorry with a proof, and Lean accepts the file without it being written.
        string fixedText = File.ReadAllText(Lean.Sample("Proofs", "Proofs", "Basic.lean")).Replace("  sorry", "  exact Nat.mul_comm a b", StringComparison.Ordinal);
        var (fixedReport, _) = await CallAsync(server, "check_file", new JsonObject { ["path"] = "Proofs/Basic.lean", ["content"] = fixedText });
        Assert.Contains("Lean accepts the file", fixedReport, StringComparison.Ordinal);

        // A broken edit is reported against the text that was sent, not the previous version.
        var (broken, _) = await CallAsync(server, "check_file", new JsonObject { ["path"] = "Proofs/Basic.lean", ["content"] = fixedText.Replace("Nat.mul_comm a b", "Nat.add_comm a b", StringComparison.Ordinal) });
        Assert.Contains("error", broken, StringComparison.Ordinal);

        var (goals, _) = await CallAsync(server, "goals", new JsonObject { ["path"] = "Proofs/Basic.lean", ["line"] = 17, ["column"] = 15 });
        Assert.Contains("hp : p", goals, StringComparison.Ordinal);
        Assert.Contains("⊢ p", goals, StringComparison.Ordinal);

        var (steps, _) = await CallAsync(server, "proof_steps", new JsonObject { ["path"] = "Proofs/Basic.lean", ["line"] = 16 });
        Assert.Contains("proof of not_not_elim", steps, StringComparison.Ordinal);
        Assert.Contains("continues on the lines below", steps, StringComparison.Ordinal);
        Assert.Contains("goals accomplished", steps, StringComparison.Ordinal);

        var (refs, _) = await CallAsync(server, "references", new JsonObject { ["path"] = "Proofs/Basic.lean", ["line"] = 2, ["column"] = 6 });
        Assert.Contains("Proofs/Basic.lean:5:10", refs.Replace('\\', '/'), StringComparison.Ordinal);

        var (snippet, _) = await CallAsync(server, "run_lean", new JsonObject { ["code"] = "#eval 6 * 7\n#check Nat.add_comm" });
        Assert.Contains("info: 42", snippet, StringComparison.Ordinal);
        Assert.Contains("Nat.add_comm", snippet, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SuggestionsAreListedAndApplied()
    {
        Lean.RequireLean();
        string dir = Directory.CreateTempSubdirectory("leanstudio-suggest").FullName;
        File.WriteAllText(Path.Combine(dir, "lean-toolchain"), Lean.Toolchain + "\n");
        string file = Path.Combine(dir, "S.lean");
        File.WriteAllText(file, "theorem len (xs : List Nat) : (xs ++ []).length = xs.length := by\n  simp?\n");
        await using var bench = new Workbench(dir);
        McpServer server = LeanTools.Create(bench, "test");
        var (list, _) = await CallAsync(server, "suggestions", new JsonObject { ["path"] = "S.lean", ["line"] = 2 });
        Assert.StartsWith("1. Try this:", list, StringComparison.Ordinal);
        var (applied, err) = await CallAsync(server, "suggestions", new JsonObject { ["path"] = "S.lean", ["line"] = 2, ["apply"] = 1 });
        Assert.False(err, applied);
        Assert.Contains("Lean accepts the file", applied, StringComparison.Ordinal);
        Assert.DoesNotContain("simp?", File.ReadAllText(file), StringComparison.Ordinal);
    }

    [Fact]
    public async Task BuildsAndVerifiesWithTenet()
    {
        Lean.RequireLean();
        await using var bench = new Workbench(Lean.Sample("Proofs"));
        McpServer server = LeanTools.Create(bench, "test");
        var (build, buildErr) = await CallAsync(server, "build", new JsonObject());
        Assert.False(buildErr, build);
        Assert.StartsWith("Build succeeded", build, StringComparison.Ordinal);

        var (verify, _) = await CallAsync(server, "verify", new JsonObject());
        Assert.Contains("3 verified", verify, StringComparison.Ordinal);
        Assert.Contains("unfinished (Proofs.Basic:20) rests on sorry", verify, StringComparison.Ordinal);
        Assert.Contains("em' (Proofs.Basic:13) is an axiom the project introduces", verify, StringComparison.Ordinal);

        var (axioms, _) = await CallAsync(server, "axioms", new JsonObject { ["name"] = "not_not_elim" });
        Assert.Contains("em'", axioms, StringComparison.Ordinal);

        var (search, _) = await CallAsync(server, "search_declarations", new JsonObject { ["query"] = "and_swap" });
        Assert.Contains("and_swap", search, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheBridgeCarriesRequestsToTheWindow()
    {
        Environment.SetEnvironmentVariable("LEANSTUDIO_PIPE", "leanstudio-test-" + Environment.ProcessId);
        try
        {
            using var cts = new CancellationTokenSource();
            Assert.True(StudioBridge.TryServe(req => Task.FromResult(new JsonObject
            {
                ["file"] = "/tmp/A.lean",
                ["line"] = 3,
                ["column"] = 5,
                ["echo"] = req["method"]?.GetValue<string>(),
            }), cts.Token));
            JsonObject? r = await StudioBridge.RequestAsync(new JsonObject { ["method"] = "context" }, ct: TestContext.Current.CancellationToken);
            Assert.NotNull(r);
            Assert.Equal("context", r["echo"]!.GetValue<string>());
            // A second window does not take the pipe from the first.
            Assert.False(StudioBridge.TryServe(_ => Task.FromResult(new JsonObject()), cts.Token));
            cts.Cancel();
        }
        finally
        {
            Environment.SetEnvironmentVariable("LEANSTUDIO_PIPE", null);
        }
    }

    [Fact]
    public async Task NoWindowMeansNoContext()
    {
        Environment.SetEnvironmentVariable("LEANSTUDIO_PIPE", "leanstudio-nobody-" + Environment.ProcessId);
        try
        {
            Assert.Null(await StudioBridge.RequestAsync(new JsonObject { ["method"] = "context" }, TimeSpan.FromMilliseconds(300), TestContext.Current.CancellationToken));
        }
        finally
        {
            Environment.SetEnvironmentVariable("LEANSTUDIO_PIPE", null);
        }
    }
}

public sealed class AgentSetupTests
{
    private static readonly AgentSetup Setup = new("/Applications/Lean Studio.app/Contents/MacOS/LeanStudio", ["--mcp"]);

    [Fact]
    public void EveryClientGetsASnippet()
    {
        Assert.Equal("claude mcp add --scope user leanstudio -- \"/Applications/Lean Studio.app/Contents/MacOS/LeanStudio\" --mcp", Setup.ClaudeCommand);
        JsonNode json = JsonNode.Parse(Setup.McpServersJson())!;
        Assert.Equal("/Applications/Lean Studio.app/Contents/MacOS/LeanStudio", json["mcpServers"]!["leanstudio"]!["command"]!.GetValue<string>());
        Assert.Contains("args = [\"--mcp\"]", Setup.CodexToml(), StringComparison.Ordinal);
        Assert.Contains(Setup.Clients(), c => c.Name.StartsWith("Grok", StringComparison.Ordinal));
    }

    [Fact]
    public void GeminiSettingsKeepWhatWasThere()
    {
        string dir = Directory.CreateTempSubdirectory().FullName;
        string path = Path.Combine(dir, "settings.json");
        File.WriteAllText(path, "{ \"theme\": \"Dracula\", \"mcpServers\": { \"other\": { \"command\": \"x\" } } }");
        Setup.InstallGemini(path);
        JsonNode s = JsonNode.Parse(File.ReadAllText(path))!;
        Assert.Equal("Dracula", s["theme"]!.GetValue<string>());
        Assert.NotNull(s["mcpServers"]!["other"]);
        Assert.Equal("--mcp", s["mcpServers"]!["leanstudio"]!["args"]![0]!.GetValue<string>());
        Assert.True(File.Exists(path + ".bak"));
    }

    [Fact]
    public void CodexConfigReplacesOnlyItsOwnTable()
    {
        string dir = Directory.CreateTempSubdirectory().FullName;
        string path = Path.Combine(dir, "config.toml");
        File.WriteAllText(path, "model = \"o3\"\n\n[mcp_servers.leanstudio]\ncommand = \"old\"\n\n[mcp_servers.other]\ncommand = \"y\"\n");
        Setup.InstallCodex(path);
        string t = File.ReadAllText(path);
        Assert.Contains("model = \"o3\"", t, StringComparison.Ordinal);
        Assert.Contains("[mcp_servers.other]", t, StringComparison.Ordinal);
        Assert.DoesNotContain("\"old\"", t, StringComparison.Ordinal);
        Assert.Single(t.Split("[mcp_servers.leanstudio]").Skip(1));
    }
}

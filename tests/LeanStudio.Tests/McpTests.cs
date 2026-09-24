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
        Assert.Contains("prove", names);
        Assert.Contains("search_mathlib", names);
        Assert.All(list["result"]!["tools"]!.AsArray(), t => Assert.Equal("object", t!["inputSchema"]!["type"]!.GetValue<string>()));

        JsonObject unknown = (await server.HandleAsync(Request(3, "no/such/method"), TestContext.Current.CancellationToken))!;
        Assert.Equal(-32601, unknown["error"]!["code"]!.GetValue<int>());

        var (text, isError) = await CallAsync(server, "check_file", new JsonObject());
        Assert.True(isError);
        Assert.Contains("path", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnswersEveryRequestWhenAToolThrowsUnexpectedly()
    {
        var boom = new McpTool("boom", "Throws a bug, not a ToolException.", new JsonObject { ["type"] = "object" },
            (_, _) => throw new NullReferenceException("a bug in the tool"));
        var server = new McpServer("test", "0", "", [boom]);
        string input = string.Join('\n',
            Request(1, "tools/call", new JsonObject { ["name"] = "boom" }).ToJsonString(),
            new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 2, ["method"] = 42 }.ToJsonString(),
            Request(3, "ping").ToJsonString());
        using var output = new StringWriter();

        // The server neither throws nor stops: every request gets its answer.
        await server.RunAsync(new StringReader(input), output, TestContext.Current.CancellationToken);

        var replies = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => JsonNode.Parse(l)!.AsObject())
            .ToDictionary(r => r["id"]!.GetValue<int>());
        Assert.Equal(3, replies.Count);
        Assert.Equal(-32603, replies[1]["error"]!["code"]!.GetValue<int>());
        Assert.Contains("a bug in the tool", replies[1]["error"]!["message"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal(-32600, replies[2]["error"]!["code"]!.GetValue<int>());
        Assert.NotNull(replies[3]["result"]);
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
    public async Task AnswersTheOtherQuestionsAboutAProject()
    {
        Lean.RequireLean();
        await using var bench = new Workbench(Lean.Sample("Proofs"));
        McpServer server = LeanTools.Create(bench, "test");

        var (info, _) = await CallAsync(server, "project_info", new JsonObject());
        Assert.Contains("Proofs", info, StringComparison.Ordinal);
        Assert.Contains(Lean.Toolchain, info, StringComparison.Ordinal);

        var (toolchains, _) = await CallAsync(server, "toolchains", new JsonObject());
        Assert.Contains(Lean.Toolchain, toolchains, StringComparison.Ordinal);

        var (hover, _) = await CallAsync(server, "hover", new JsonObject { ["path"] = "Proofs/Basic.lean", ["line"] = 2, ["column"] = 6 });
        Assert.Contains("double (n : Nat) : Nat", hover, StringComparison.Ordinal);
        Assert.Contains("Doubling a number", hover, StringComparison.Ordinal);

        var (build, buildErr) = await CallAsync(server, "build", new JsonObject());
        Assert.False(buildErr, build);
        var (decl, _) = await CallAsync(server, "declaration", new JsonObject { ["name"] = "double_eq_two_mul" });
        Assert.Contains("module: Proofs.Basic", decl, StringComparison.Ordinal);
        Assert.Contains("theorem double_eq_two_mul", decl, StringComparison.Ordinal);
        Assert.Contains("Eq.{1} Nat (double n)", decl, StringComparison.Ordinal); // the type, in the kernel's form
        Assert.Contains("Basic.lean:4", decl.Replace('\\', '/'), StringComparison.Ordinal);
        Assert.Matches(@"uses \(\d+\): .*\bdouble\b", decl);
        var (missing, missingErr) = await CallAsync(server, "declaration", new JsonObject { ["name"] = "no_such_thing" });
        Assert.True(missingErr, missing);

        var (beats, _) = await CallAsync(server, "heartbeats", new JsonObject { ["path"] = "Proofs/Basic.lean" });
        Assert.Contains("of the default limit", beats, StringComparison.Ordinal);
        Assert.Contains("theorem double_eq_two_mul", beats, StringComparison.Ordinal);

        var (inst, _) = await CallAsync(server, "instances", new JsonObject { ["path"] = "Proofs/Basic.lean", ["class"] = "Inhabited" });
        Assert.Matches(@"^\d+ instances of Inhabited:", inst);
        Assert.Contains("instInhabitedNat", inst, StringComparison.Ordinal);

        var (noBlueprint, noBlueprintErr) = await CallAsync(server, "blueprint", new JsonObject());
        Assert.True(noBlueprintErr);
        Assert.Contains("no blueprint folder", noBlueprint, StringComparison.Ordinal);
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

        var (why, _) = await CallAsync(server, "why_not_proved", new JsonObject { ["name"] = "unfinished" });
        Assert.Contains("rests on sorry", why, StringComparison.Ordinal);
        Assert.Contains("→ fix unfinished", why, StringComparison.Ordinal);
        var (map, _) = await CallAsync(server, "project_map", new JsonObject());
        Assert.Contains("1 rest on sorry", map, StringComparison.Ordinal);
        Assert.Contains("unfinished  (uses sorry;", map, StringComparison.Ordinal);
        var (proved, _) = await CallAsync(server, "why_not_proved", new JsonObject { ["name"] = "and_swap" });
        Assert.Contains("fully proved", proved, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProvesSorriesAndProfiles()
    {
        Lean.RequireLean();
        string dir = Directory.CreateTempSubdirectory("leanstudio-prove").FullName;
        try
        {
            File.WriteAllText(Path.Combine(dir, "lean-toolchain"), Lean.Toolchain + "\n");
            string file = Path.Combine(dir, "P.lean");
            File.WriteAllText(file, "theorem t (a b : Nat) : a + b = b + a := by\n  sorry\n");
            await using var bench = new Workbench(dir);
            McpServer server = LeanTools.Create(bench, "test");
            var (report, err) = await CallAsync(server, "prove", new JsonObject { ["path"] = "P.lean", ["apply"] = true });
            Assert.False(err, report);
            Assert.Contains("closes  omega", report, StringComparison.Ordinal);
            Assert.Contains("wrote 1 proof", report, StringComparison.Ordinal);
            Assert.DoesNotContain("sorry", File.ReadAllText(file), StringComparison.Ordinal);

            string slow = "theorem slow (x y z w : Int) (h1 : 3*x + 5*y - 7*z + 11*w = 13) (h2 : 2*x - 9*y + 4*z - w = 8)\n"
                + "    (h3 : x + y + z + w = 1) (h4 : 6*x - 2*y + 3*z - 5*w = 21) : 17*x + 3*y - 2*z + w ≠ 1000 := by\n  omega\n";
            var (profile, perr) = await CallAsync(server, "profile", new JsonObject { ["path"] = "P.lean", ["content"] = slow });
            Assert.False(perr, profile);
            Assert.Contains("line 1: ", profile, StringComparison.Ordinal);
            Assert.Contains("slowest part: omega", profile, StringComparison.Ordinal);

            File.WriteAllText(file, "theorem u (a b : Nat) (h : a < b) : a + 1 ≤ b := by\n  sorry\n");
            var (ext, eerr) = await CallAsync(server, "extract_lemma", new JsonObject { ["path"] = "P.lean", ["line"] = 2, ["name"] = "step" });
            Assert.False(eerr, ext);
            Assert.StartsWith("theorem step {a b : Nat} (h : a < b) : a + 1 ≤ b := by", File.ReadAllText(file), StringComparison.Ordinal);
            Assert.Contains("exact step (by assumption)", File.ReadAllText(file), StringComparison.Ordinal);
            File.WriteAllText(file, "theorem t (a b : Nat) : a + b = b + a := by\n  omega\n");

            File.WriteAllText(Path.Combine(dir, "N.lean"), "@[extern \"n_twice\"]\nopaque twice (x : UInt32) : UInt32\n");
            var (ffi, ferr) = await CallAsync(server, "ffi_bindings", new JsonObject());
            Assert.False(ferr, ffi);
            Assert.Contains("n_twice: MISSING", ffi, StringComparison.Ordinal);
            Assert.Contains("LEAN_EXPORT uint32_t n_twice(uint32_t x)", ffi, StringComparison.Ordinal);
            File.Delete(Path.Combine(dir, "N.lean"));

            var (walk, werr) = await CallAsync(server, "export_walkthrough", new JsonObject { ["path"] = "P.lean" });
            Assert.False(werr, walk);
            Assert.Contains("live.lean-lang.org/#code=", walk, StringComparison.Ordinal);
            Assert.Contains("<h2>t", File.ReadAllText(Path.Combine(dir, "P-walkthrough.html")), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
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
            // A second window does not take the pipe from the first, not even right after a request, when the
            // first is closing one connection and opening the next.
            for (int i = 0; i < 20; i++)
            {
                JsonObject? again = await StudioBridge.RequestAsync(new JsonObject { ["method"] = "context" }, ct: TestContext.Current.CancellationToken);
                Assert.Equal("context", again?["echo"]?.GetValue<string>());
                Assert.False(StudioBridge.TryServe(_ => Task.FromResult(new JsonObject()), cts.Token), $"the pipe was free after request {i + 1}");
            }
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

using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using LeanStudio.Core.Ai;
using LeanStudio.Core.Proofs;
using LeanStudio.Lsp;

namespace LeanStudio.Tests;

/// <summary>The built-in AI: talking to local and cloud models, finding them, and reading their proofs.</summary>
public sealed class AiTests
{
    /// <summary>Answers requests by path, as the servers the AI talks to would, and remembers the last body sent.</summary>
    private sealed class Stub(Func<string, (HttpStatusCode Status, string Type, string Body)> answer) : HttpMessageHandler
    {
        public string LastBody { get; private set; } = "";

        public List<string> Asked { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Asked.Add(request.RequestUri!.AbsoluteUri);
            LastBody = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            (HttpStatusCode status, string type, string body) = answer(request.RequestUri!.AbsolutePath);
            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, type) };
        }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static (HttpStatusCode, string, string) Sse(params string[] data) =>
        (HttpStatusCode.OK, "text/event-stream", string.Concat(data.Select(d => "data: " + d + "\n\n")));

    [Fact]
    public async Task StreamsFromAnOpenAiCompatibleServerAndHidesThinking()
    {
        var stub = new Stub(_ => Sse(
            """{"choices":[{"delta":{"content":"<think>hmm"}}]}""",
            """{"choices":[{"delta":{"content":" ok</think>Hello"}}]}""",
            """{"choices":[{"delta":{"content":", world"}}]}""",
            "[DONE]"));
        var model = new OpenAiCompatibleModel("Test", "http://127.0.0.1:1234/v1/", "m", AiLocation.Local, 4096, "key", new HttpClient(stub));
        Assert.Equal("Hello, world", await model.CompleteAsync([ChatMessage.User("hi")], ct: Ct));
        Assert.Contains("\"stream\":true", stub.LastBody, StringComparison.Ordinal);
        Assert.Equal("http://127.0.0.1:1234/v1/chat/completions", stub.Asked[^1]);

        var streamed = new StringBuilder();
        await foreach (string piece in model.StreamAnswerAsync([ChatMessage.User("hi")], ct: Ct))
        {
            streamed.Append(piece);
        }
        Assert.Equal("Hello, world", streamed.ToString());
    }

    [Fact]
    public async Task ReadsAnAnswerSentAllAtOnceAndExplainsErrors()
    {
        var once = new OpenAiCompatibleModel("T", "http://x/v1", "m", AiLocation.Local, 4096, http: new HttpClient(new Stub(_ =>
            (HttpStatusCode.OK, "application/json", """{"choices":[{"message":{"content":"all at once"}}]}"""))));
        Assert.Equal("all at once", await once.CompleteAsync([ChatMessage.User("x")], ct: Ct));

        var missing = new OpenAiCompatibleModel("LM Studio", "http://x/v1", "nope", AiLocation.Local, 4096, http: new HttpClient(new Stub(_ =>
            (HttpStatusCode.NotFound, "application/json", """{"error":{"message":"model 'nope' not found"}}"""))));
        AiException e = await Assert.ThrowsAsync<AiException>(() => missing.CompleteAsync([ChatMessage.User("x")], ct: Ct));
        Assert.Contains("LM Studio", e.Message, StringComparison.Ordinal);
        Assert.Contains("model 'nope' not found", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AsksAgainPlainlyWhenAServerRefusesSettings()
    {
        Stub? stub = null;
        stub = new Stub(_ => stub!.LastBody.Contains("max_tokens", StringComparison.Ordinal)
            ? (HttpStatusCode.BadRequest, "application/json", """{"error":{"message":"unknown parameter max_tokens"}}""")
            : Sse("""{"choices":[{"delta":{"content":"fine"}}]}""", "[DONE]"));
        var model = new OpenAiCompatibleModel("Picky", "http://x/v1", "system", AiLocation.OnDevice, 4096, http: new HttpClient(stub));
        Assert.Equal("fine", await model.CompleteAsync([ChatMessage.User("x")], ct: Ct));
        Assert.Equal("fine", await model.CompleteAsync([ChatMessage.User("y")], ct: Ct));
        Assert.Equal(3, stub.Asked.Count); // refused once, then plain every time
    }

    [Fact]
    public async Task TalksToOllamaAndClaude()
    {
        var ollamaStub = new Stub(_ => (HttpStatusCode.OK, "application/x-ndjson",
            "{\"message\":{\"content\":\"Hi\"},\"done\":false}\n{\"message\":{\"content\":\" there\"},\"done\":false}\n{\"done\":true}\n"));
        Assert.Equal("Hi there", await new OllamaModel("http://127.0.0.1:11434", "qwen3", http: new HttpClient(ollamaStub)).CompleteAsync([ChatMessage.User("x")], ct: Ct));
        Assert.Contains("\"num_ctx\":8192", ollamaStub.LastBody, StringComparison.Ordinal);

        var claudeStub = new Stub(_ => Sse(
            """{"type":"message_start"}""",
            """{"type":"content_block_delta","delta":{"type":"text_delta","text":"Claude "}}""",
            """{"type":"content_block_delta","delta":{"type":"text_delta","text":"says hi"}}""",
            """{"type":"message_stop"}"""));
        var claude = new AnthropicModel("sk-test", http: new HttpClient(claudeStub));
        Assert.Equal("Claude says hi", await claude.CompleteAsync([ChatMessage.System("be brief"), ChatMessage.User("x")], ct: Ct));
        Assert.Contains("\"system\":\"be brief\"", claudeStub.LastBody, StringComparison.Ordinal);
        Assert.Contains("\"model\":\"" + AnthropicModel.DefaultModel + "\"", claudeStub.LastBody, StringComparison.Ordinal);
        Assert.Equal(AiLocation.Cloud, claude.Location);
    }

    [Fact]
    public async Task FindsLocalModelsFirstAndKeepsCodeLocalUnlessAllowed()
    {
        var stub = new Stub(path => path switch
        {
            "/api/tags" => (HttpStatusCode.OK, "application/json", """{"models":[{"name":"llama3.2:3b"},{"name":"deepseek-prover-v2:7b"},{"name":"nomic-embed-text"}]}"""),
            "/lm/v1/models" => (HttpStatusCode.OK, "application/json", """{"data":[{"id":"qwen2.5-coder-7b"}]}"""),
            _ => (HttpStatusCode.NotFound, "text/plain", "no"),
        });
        string dir = Path.Combine(Path.GetTempPath(), "leanstudio-ai-" + Guid.NewGuid().ToString("N"));
        try
        {
            var secrets = new SecretStore(dir) { FilesOnly = true };
            var discovery = new AiDiscovery(secrets, new HttpClient(stub));
            var config = new AiConfig { OllamaUrl = "http://ollama", LmStudioUrl = "http://lm/lm/v1", LlamaCppUrl = "http://none/v1" };
            IReadOnlyList<AiCandidate> found = await discovery.FindAsync(config, Ct);
            // A Mac with Apple Intelligence on would rightly be chosen first; this test is about the servers.
            Assert.SkipWhen(found.Single(c => c.Id == AiProviders.Apple).Available, "Apple's on-device model is available here, and would be chosen first");

            AiCandidate ollama = found.Single(c => c.Id == AiProviders.Ollama);
            Assert.True(ollama.Available);
            Assert.Equal(["deepseek-prover-v2:7b", "llama3.2:3b"], ollama.Models); // a prover first; the embedding model left out
            Assert.False(found.Single(c => c.Id == AiProviders.LlamaCpp).Available);

            (IChatModel? auto, _) = await discovery.ChooseAsync(config, Ct);
            Assert.Equal("Ollama · deepseek-prover-v2:7b", auto?.DisplayName);
            (IChatModel? chosen, _) = await discovery.ChooseAsync(config with { Provider = AiProviders.LmStudio }, Ct);
            Assert.Equal("LM Studio · qwen2.5-coder-7b", chosen?.DisplayName);

            // With nothing local, a cloud key is not used unless the person allowed it.
            var nothing = config with { OllamaUrl = "http://none/nothing", LmStudioUrl = "http://none/v1" };
            (IChatModel? none, string why) = await discovery.ChooseAsync(nothing, Ct);
            Assert.Null(none);
            Assert.Contains("No local model", why, StringComparison.Ordinal);
            await secrets.SetAsync("anthropic", "sk-test", Ct);
            (none, why) = await discovery.ChooseAsync(nothing, Ct);
            Assert.Null(none);
            Assert.Contains("allow cloud", why, StringComparison.Ordinal);
            (IChatModel? cloud, _) = await discovery.ChooseAsync(nothing with { AllowCloud = true }, Ct);
            Assert.Equal(AiLocation.Cloud, cloud?.Location);

            Assert.Equal("sk-test", await secrets.GetAsync("anthropic", Ct));
            await secrets.SetAsync("anthropic", "", Ct);
            Assert.Equal(Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY"), await secrets.GetAsync("anthropic", Ct));
        }
        finally
        {
            Lean.DeleteTree(dir);
        }
    }

    [Fact]
    public void TellsLocalAddressesFromCloudOnes()
    {
        Assert.True(AiDiscovery.IsLocalAddress("http://127.0.0.1:8080/v1"));
        Assert.True(AiDiscovery.IsLocalAddress("http://localhost:11434"));
        Assert.True(AiDiscovery.IsLocalAddress("http://192.168.1.20:1234/v1"));
        Assert.True(AiDiscovery.IsLocalAddress("http://studio.local:1234/v1"));
        Assert.False(AiDiscovery.IsLocalAddress("https://api.openai.com/v1"));
        Assert.False(AiDiscovery.IsLocalAddress("not a url"));
    }

    [Fact]
    public void ReadsProofsOutOfAnswers()
    {
        const string answer = """
            <think>let me think</think>
            Here are some:
            ```lean
            simp
            ```
            ```lean
            by omega
            ```
            ```lean
            induction n with
            | zero => rfl
            | succ k ih =>
              simp [ih]
              omega
            ```
            ```lean
            theorem foo (n : Nat) : n + 0 = n := by
              simp
            ```
            ```lean
            sorry
            ```
            ```lean
            Nat.add_comm a b
            ```
            ```lean
            native_decide
            ```
            """;
        IReadOnlyList<AiProver.Candidate> c = AiProver.Candidates(answer);
        Assert.Equal(["simp", "omega", "(\n  induction n with\n  | zero => rfl\n  | succ k ih =>\n    simp [ih]\n    omega)", "exact Nat.add_comm a b"], c.Select(x => x.Tactic));
        Assert.Equal("induction n with\n| zero => rfl\n| succ k ih =>\n  simp [ih]\n  omega", c[2].Display);

        // A model that ignored the format and wrote a list.
        Assert.Equal(["simp", "omega", "linarith"], AiProver.Candidates("1. simp\n2. `omega`\nTry these:\n- linarith").Select(x => x.Tactic));
    }

    [Fact]
    public void FillsSuggestedProofsOfSeveralLines()
    {
        const string text = "theorem t (n : Nat) : 0 + n = n := by\n  cases n with\n  | zero => rfl\n  | succ k => sorry\n";
        SorrySite site = ProofSearch.Sites(text)[0];
        var trial = new TacticTrial("(\n  simp\n  omega)", TrialOutcome.Closes, 5, null) { Display = "simp\nomega", SuggestedBy = "Test" };
        var result = new SearchResult(site, false, [trial]);
        Assert.Equal("simp\n              omega", result.Fill(trial)); // under the first line, at the sorry's column
        Assert.EndsWith("✦", trial.Label, StringComparison.Ordinal);

        SorrySite term = ProofSearch.Sites("  theorem c (p q : Prop) (hp : p) (hq : q) : p ∧ q := sorry")[0];
        var t2 = new TacticTrial("x", TrialOutcome.Closes, 1, null) { Display = "constructor\nexact hp\nexact hq" };
        Assert.Equal("by\n    constructor\n    exact hp\n    exact hq", new SearchResult(term, true, [t2]).Fill(t2));

        // The instrumented file carries a suggestion of several lines as one Lean string.
        string inst = ProofSearch.Instrument(text, [site], ["simp", "(\n  simp\n  omega)"]);
        Assert.Contains("\"(\\n  simp\\n  omega)\"", inst, StringComparison.Ordinal);
    }

    [Fact]
    public void PromptsFitTheOnDeviceModel()
    {
        var apple = new OpenAiCompatibleModel(AppleIntelligence.DisplayName, "http://x/v1", "system", AiLocation.OnDevice, AppleIntelligence.ContextTokens);
        string body = string.Concat(Enumerable.Repeat("  have h : 1 + 1 = 2 := by norm_num\n", 800));
        string text = "import Mathlib\nopen Nat\n\ntheorem z : True := by\n" + body + "  sorry\n";
        SorrySite site = ProofSearch.Sites(text)[^1];
        IReadOnlyList<ChatMessage> prompt = AiProver.Prompt(apple, text, site, string.Concat(Enumerable.Repeat("h : x = y\n", 900)) + "⊢ True", ["simp"]);
        int tokens = prompt.Sum(m => AiText.EstimateTokens(m.Text));
        Assert.True(tokens + AiProver.AnswerTokens(apple) <= AppleIntelligence.ContextTokens, $"{tokens} tokens of prompt");
        Assert.Contains("sorry /- ← this one -/", prompt[1].Text, StringComparison.Ordinal);
        Assert.Contains("import Mathlib", prompt[1].Text, StringComparison.Ordinal);
        Assert.Contains("Mathlib", prompt[0].Text, StringComparison.Ordinal);
    }

    [Fact]
    public void ContextIsTheDeclarationWithItsImports()
    {
        const string file = "import Mathlib\nopen Nat\n\n/-- The doc. -/\ntheorem a (n : ℕ) : n + 0 = n := by\n  simp\n\n/-- Zero on the left. -/\ntheorem b (n : ℕ) : 0 + n = n := by\n  induction n with\n  | zero => sorry\n  | succ k ih => sorry\n\ndef c := 1\n";
        IReadOnlyList<SorrySite> sites = ProofSearch.Sites(file);
        string context = AiProver.Context(file, sites[1], 1000);
        Assert.Contains("import Mathlib", context, StringComparison.Ordinal);
        Assert.Contains("open Nat", context, StringComparison.Ordinal);
        Assert.Contains("/-- Zero on the left. -/", context, StringComparison.Ordinal);
        Assert.Contains("| succ k ih => sorry /- ← this one -/", context, StringComparison.Ordinal);
        Assert.DoesNotContain("theorem a", context, StringComparison.Ordinal);
        Assert.DoesNotContain("def c", context, StringComparison.Ordinal);
    }

    [Fact]
    public void KeepsLongConversationsInsideTheModel()
    {
        var small = new OpenAiCompatibleModel("S", "http://x/v1", "m", AiLocation.OnDevice, 4096);
        var conversation = new List<ChatMessage> { ChatMessage.System("s"), ChatMessage.User(new string('a', 3000)) };
        for (int i = 0; i < 20; i++)
        {
            conversation.Add(ChatMessage.Assistant(new string('b', 3000)));
            conversation.Add(ChatMessage.User("question " + i));
        }
        IReadOnlyList<ChatMessage> fit = AiAssistant.Fit(small, conversation);
        Assert.Equal(ChatRole.System, fit[0].Role);
        Assert.Equal("question 19", fit[^1].Text);
        Assert.True(fit.Sum(m => AiText.EstimateTokens(m.Text)) < 4096);
        for (int i = 2; i < fit.Count; i++)
        {
            Assert.NotEqual(fit[i - 1].Role, fit[i].Role);
        }
    }

    [Fact]
    public void ExplainsWhyTheOnDeviceModelIsUnavailable()
    {
        Assert.Contains("Apple Intelligence", AppleIntelligence.ExplainUnavailable("System model unavailable: appleIntelligenceNotEnabled"), StringComparison.Ordinal);
        Assert.Contains("downloading", AppleIntelligence.ExplainUnavailable("System model unavailable: modelNotReady"), StringComparison.Ordinal);
        Assert.Equal("only line", AppleFmCliModel.Transcript([ChatMessage.System("s"), ChatMessage.User("only line")]));
        Assert.EndsWith("Assistant:", AppleFmCliModel.Transcript([ChatMessage.User("a"), ChatMessage.Assistant("b"), ChatMessage.User("c")]), StringComparison.Ordinal);
    }
}

/// <summary>The AI's suggestions checked by real Lean, with a stand-in model that always suggests the same proofs.</summary>
[Collection(Lean.Collection)]
public sealed class AiLeanTests
{
    private sealed class Scripted(params string[] answers) : IChatModel
    {
        private int _next;

        public string DisplayName => "Scripted";

        public AiLocation Location => AiLocation.OnDevice;

        public int ContextTokens => 4096;

        public List<string> Prompts { get; } = [];

        public async IAsyncEnumerable<string> StreamAsync(IReadOnlyList<ChatMessage> messages, ChatOptions options, [EnumeratorCancellation] CancellationToken ct = default)
        {
            Prompts.Add(messages[^1].Text);
            await Task.Yield();
            yield return answers[Math.Min(_next++, answers.Length - 1)];
        }
    }

    private const string Text = """
        theorem comm (a b : Nat) : a + b = b + a := by
          sorry

        theorem conj (p q : Prop) (hp : p) (hq : q) : p ∧ q := sorry

        theorem hard (f : Nat → Nat) : f 1 = f 2 := by
          sorry
        """;

    [Fact]
    public async Task OffersOnlyWhatLeanAccepts()
    {
        Lean.RequireLean();
        string dir = Lean.Sample("Demo");
        await using var server = new LeanServer(new LeanServerCommand(Lean.Executable!, ["--server"], dir));
        await server.StartAsync(TestContext.Current.CancellationToken);
        string path = Path.Combine(dir, "Ai.lean");
        IReadOnlyList<SorrySite> sites = ProofSearch.Sites(Text);

        var model = new Scripted("""
            ```lean
            exact Nat.add_comm_invented a b
            ```
            ```lean
            induction a with
            | zero => simp
            | succ k ih => omega
            ```
            ```lean
            by exact Nat.add_comm a b
            ```
            """);
        AiProofResult r = await AiProver.RunAsync(server, model, path, Text, sites[0], ct: TestContext.Current.CancellationToken)
            .WaitAsync(Lean.Patience, TestContext.Current.CancellationToken);
        Assert.Contains("⊢ a + b = b + a", model.Prompts[0], StringComparison.Ordinal);
        Assert.Equal(3, r.Suggested);
        Assert.Equal(["exact Nat.add_comm a b", "induction a with\n| zero => simp\n| succ k ih => omega"], r.Result.Successes.Select(t => t.Replacement));
        Assert.Contains(r.Result.Trials, t => t.Tactic == "exact Nat.add_comm_invented a b" && t.Outcome == TrialOutcome.Fails);
        Assert.All(r.Result.Trials, t => Assert.Equal("Scripted", t.SuggestedBy));

        // A proof of several lines where a term was expected, filled in: Lean accepts the file.
        var conj = new Scripted("```lean\nconstructor\nexact hp\nexact hq\n```");
        AiProofResult c = await AiProver.RunAsync(server, conj, path, Text, sites[1], ct: TestContext.Current.CancellationToken)
            .WaitAsync(Lean.Patience, TestContext.Current.CancellationToken);
        Assert.True(c.Result.TermMode);
        string filled = ProofSearch.Apply(Text, [(c.Result, c.Result.Best!)]);
        Assert.Contains("p ∧ q := by\n  constructor\n  exact hp\n  exact hq", filled, StringComparison.Ordinal);
        IReadOnlyList<Diagnostic> diags = await Scratch.CheckAsync(server, path, "Filled", filled, TestContext.Current.CancellationToken)
            .WaitAsync(Lean.Patience, TestContext.Current.CancellationToken);
        Assert.DoesNotContain(diags, d => d.Severity == DiagnosticSeverity.Error);

        // Nothing works: asked again, with what failed, and nothing is offered.
        var wrong = new Scripted("```lean\nrfl\n```", "```lean\nsimp\n```");
        AiProofResult h = await AiProver.RunAsync(server, wrong, path, Text, sites[2], ct: TestContext.Current.CancellationToken)
            .WaitAsync(Lean.Patience, TestContext.Current.CancellationToken);
        Assert.Equal(2, h.Rounds);
        Assert.Null(h.Result.Best);
        Assert.Contains("- rfl", wrong.Prompts[1], StringComparison.Ordinal);
    }
}

using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;
using System.Text.Json.Nodes;
using LeanStudio.Core.Agents;
using LeanStudio.Core.Processes;
using LeanStudio.Core.Workflow;
using LeanStudio.Lsp;
using LeanStudio.Mcp;

namespace LeanStudio.Tests;

/// <summary>
/// Under duress: many requests at once, floods of edits, huge messages, garbage on the wire, the other side dying
/// mid-request, and cancellation storms. Each must end in the right answer or a clean failure, never a hang or a
/// crossed answer.
/// </summary>
public sealed class StressTests
{
    /// <summary>Two connections talking to each other through a pair of in-process pipes.</summary>
    private sealed class Wire : IAsyncDisposable
    {
        private readonly AnonymousPipeServerStream _aOut = new(PipeDirection.Out), _bOut = new(PipeDirection.Out);
        private readonly AnonymousPipeClientStream _bIn, _aIn;

        public Wire()
        {
            _bIn = new AnonymousPipeClientStream(PipeDirection.In, _aOut.ClientSafePipeHandle);
            _aIn = new AnonymousPipeClientStream(PipeDirection.In, _bOut.ClientSafePipeHandle);
            A = new JsonRpcConnection(_aIn, _aOut);
            B = new JsonRpcConnection(_bIn, _bOut);
        }

        public JsonRpcConnection A { get; }
        public JsonRpcConnection B { get; }

        /// <summary>The raw stream into A, for writing garbage at it.</summary>
        public Stream IntoA => _bOut;

        /// <summary>B's side goes away, as a crashed server would.</summary>
        public void KillB()
        {
            _bOut.Dispose();
            _bIn.Dispose();
        }

        public async ValueTask DisposeAsync()
        {
            await A.DisposeAsync();
            await B.DisposeAsync();
            foreach (Stream s in new Stream[] { _aOut, _bOut, _aIn, _bIn })
            {
                s.Dispose();
            }
        }
    }

    [Fact]
    public async Task ThousandsOfConcurrentRequestsEachGetTheirOwnAnswer()
    {
        await using var wire = new Wire();
        wire.B.RequestHandler = async (method, p) =>
        {
            int n = p.GetProperty("n").GetInt32();
            await Task.Delay(n % 7); // answers come back out of order
            if (n % 97 == 0)
            {
                throw new InvalidOperationException("no " + n);
            }
            return new JsonObject { ["twice"] = 2 * n };
        };
        wire.A.Start();
        wire.B.Start();
        var ct = TestContext.Current.CancellationToken;
        var answers = await Task.WhenAll(Enumerable.Range(1, 3000).Select(n => Task.Run(async () =>
        {
            try
            {
                JsonElement r = await wire.A.RequestAsync("double", new { n }, ct);
                return r.GetProperty("twice").GetInt32() == 2 * n && n % 97 != 0;
            }
            catch (JsonRpcException e)
            {
                return n % 97 == 0 && e.Message.Contains("no " + n, StringComparison.Ordinal);
            }
        }, ct))).WaitAsync(TimeSpan.FromSeconds(60), ct);
        Assert.All(answers, Assert.True);
    }

    [Fact]
    public async Task AnEightMegabyteMessageGoesThroughWhole()
    {
        await using var wire = new Wire();
        wire.B.RequestHandler = (_, p) => Task.FromResult<JsonNode?>(new JsonObject { ["back"] = p.GetProperty("text").GetString() });
        wire.A.Start();
        wire.B.Start();
        string big = string.Concat(Enumerable.Range(0, 8 * 1024 * 1024 / 16).Select(i => $"∀ x, x = x {i % 10}\n"));
        JsonElement r = await wire.A.RequestAsync("echo", new { text = big }, TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
        Assert.Equal(big, r.GetProperty("back").GetString());
    }

    [Fact]
    public async Task WhenTheOtherSideDiesEveryWaitingRequestFailsPromptly()
    {
        await using var wire = new Wire();
        var never = new TaskCompletionSource<JsonNode?>();
        wire.B.RequestHandler = (_, _) => never.Task; // a server that never answers, then crashes
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        wire.A.Closed += _ => closed.TrySetResult();
        wire.A.Start();
        wire.B.Start();
        var ct = TestContext.Current.CancellationToken;
        Task[] waiting = Enumerable.Range(0, 200).Select(i => (Task)wire.A.RequestAsync("hang", new { i }, ct)).ToArray();
        await Task.Delay(200, ct);
        wire.KillB();
        await closed.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
        foreach (Task t in waiting)
        {
            await Assert.ThrowsAsync<IOException>(() => t.WaitAsync(TimeSpan.FromSeconds(10), ct));
        }
        // And a new request fails at once instead of waiting forever.
        await Assert.ThrowsAnyAsync<Exception>(() => wire.A.RequestAsync("late", null, ct).WaitAsync(TimeSpan.FromSeconds(10), ct));
    }

    [Fact]
    public async Task AfterClosingEveryCallFailsTheWayCallersExpect()
    {
        // Lean restarted while requests and edits were still on their way: each must fail with IOException (what
        // callers catch for a closed connection), not with whatever the disposed parts would throw.
        var wire = new Wire();
        wire.A.Start();
        wire.B.Start();
        await wire.A.DisposeAsync();
        var ct = TestContext.Current.CancellationToken;
        await Assert.ThrowsAsync<IOException>(() => wire.A.RequestAsync("late", null, ct));
        await Assert.ThrowsAsync<IOException>(() => wire.A.NotifyAsync("late", null));
        await wire.A.DisposeAsync(); // twice is fine
        await wire.DisposeAsync();
    }

    [Fact]
    public async Task GarbageJsonIsSkippedAndTheConversationGoesOn()
    {
        await using var wire = new Wire();
        var got = new List<string>();
        var second = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        wire.A.NotificationReceived += (m, _) =>
        {
            lock (got)
            {
                got.Add(m);
            }
            if (m == "after")
            {
                second.TrySetResult();
            }
        };
        wire.A.Start();
        var ct = TestContext.Current.CancellationToken;
        async Task Frame(string body)
        {
            byte[] b = System.Text.Encoding.UTF8.GetBytes(body);
            await wire.IntoA.WriteAsync(System.Text.Encoding.ASCII.GetBytes($"Content-Length: {b.Length}\r\n\r\n"), ct);
            await wire.IntoA.WriteAsync(b, ct);
            await wire.IntoA.FlushAsync(ct);
        }
        await Frame("""{"jsonrpc":"2.0","method":"before"}""");
        await Frame("{this is not json");
        await Frame("""{"jsonrpc":"2.0","id":999999,"result":1}"""); // an answer to nothing
        await Frame("""[1, 2, 3]""");
        await Frame("""{"jsonrpc":"2.0","method":"after","params":{"ok":true}}""");
        await second.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
        Assert.Equal(["before", "after"], got);
    }

    [Fact]
    public async Task ABrokenHeaderClosesTheConnectionAndFailsWhatWasWaiting()
    {
        await using var wire = new Wire();
        var closed = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        wire.A.Closed += e => closed.TrySetResult(e);
        wire.A.Start();
        var ct = TestContext.Current.CancellationToken;
        Task waiting = wire.A.RequestAsync("anything", null, ct);
        await wire.IntoA.WriteAsync(System.Text.Encoding.ASCII.GetBytes("Content-Length: lots\r\n\r\n{}"), ct);
        await wire.IntoA.FlushAsync(ct);
        await closed.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
        await Assert.ThrowsAsync<IOException>(() => waiting.WaitAsync(TimeSpan.FromSeconds(10), ct));
    }

    [Fact]
    public async Task ACancellationStormLeavesEveryRequestFinished()
    {
        await using var wire = new Wire();
        int cancelsSeen = 0;
        wire.B.NotificationReceived += (m, _) =>
        {
            if (m == "$/cancelRequest")
            {
                Interlocked.Increment(ref cancelsSeen);
            }
        };
        wire.B.RequestHandler = async (_, p) =>
        {
            await Task.Delay(p.GetProperty("wait").GetInt32());
            return new JsonObject { ["done"] = true };
        };
        wire.A.Start();
        wire.B.Start();
        var rng = new Random(20260924);
        var ct = TestContext.Current.CancellationToken;
        var sources = new List<CancellationTokenSource>();
        var tasks = new List<Task<bool>>();
        for (int i = 0; i < 1000; i++)
        {
            var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            sources.Add(cts);
            int wait = rng.Next(0, 40);
            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    await wire.A.RequestAsync("slow", new { wait }, cts.Token);
                    return true;
                }
                catch (OperationCanceledException)
                {
                    return false;
                }
            }, ct));
        }
        foreach (CancellationTokenSource cts in sources.Where((_, i) => i % 2 == 0))
        {
            cts.CancelAfter(rng.Next(0, 30));
        }
        bool[] results = await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(60), ct);
        Assert.Contains(results, r => r);
        Assert.Contains(results, r => !r);
        Assert.True(await WaitUntil(() => Volatile.Read(ref cancelsSeen) == results.Count(r => !r), 10),
            $"every cancelled request told the other side ({cancelsSeen} of {results.Count(r => !r)})");
        sources.ForEach(s => s.Dispose());
    }

    private static async Task<bool> WaitUntil(Func<bool> condition, double seconds)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.Elapsed.TotalSeconds > seconds)
            {
                return false;
            }
            await Task.Delay(20);
        }
        return true;
    }

    [Fact]
    public async Task ManyProcessesAtOnceKeepTheirOwnOutput()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "uses /bin/sh");
        var ct = TestContext.Current.CancellationToken;
        ProcessResult[] results = await Task.WhenAll(Enumerable.Range(0, 60).Select(i =>
            ProcessRunner.RunAsync("/bin/sh", ["-c", $"for k in 1 2 3; do echo {i}-$k; done; echo err{i} >&2"], ct: ct)));
        for (int i = 0; i < results.Length; i++)
        {
            Assert.True(results[i].Success);
            var lines = results[i].Output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Order(StringComparer.Ordinal).ToArray();
            Assert.Equal(new[] { $"{i}-1", $"{i}-2", $"{i}-3", $"err{i}" }.Order(StringComparer.Ordinal), lines);
        }
    }

    [Fact]
    public async Task AFloodOfOutputIsCapturedWhole()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "uses /bin/sh");
        int lines = 0;
        ProcessResult r = await ProcessRunner.RunAsync("/bin/sh", ["-c", "i=0; while [ $i -lt 200000 ]; do echo \"line $i of the build\"; i=$((i+1)); done"],
            onLine: _ => Interlocked.Increment(ref lines), ct: TestContext.Current.CancellationToken);
        Assert.True(r.Success);
        Assert.Equal(200000, lines);
        string[] got = r.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(200000, got.Length);
        Assert.Equal("line 199999 of the build", got[^1]);
    }

    [Fact]
    public async Task CancellingAProcessStopsItsChildrenToo()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "uses /bin/sh and ps");
        string marker = "leanstudio_stress_" + Environment.ProcessId;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ProcessRunner.RunAsync("/bin/sh", ["-c", $"(exec -a {marker} sleep 300) & (exec -a {marker} sleep 300) & wait"], ct: cts.Token));
        Assert.True(await WaitUntil(() =>
        {
            ProcessResult ps = ProcessRunner.RunAsync("ps", ["-axo", "args="]).GetAwaiter().GetResult();
            return !ps.Output.Contains(marker, StringComparison.Ordinal);
        }, 10), "the child processes were stopped with their parent");
    }

    [Fact]
    public void LocalHistoryTakesSavesFromManyThreadsAtOnce()
    {
        string dir = Directory.CreateTempSubdirectory("leanstudio-history-stress").FullName;
        try
        {
            var history = new LocalHistory(Path.Combine(dir, "h"));
            string file = Path.Combine(dir, "A.lean");
            var errors = new List<Exception>();
            Parallel.For(0, 400, new ParallelOptions { MaxDegreeOfParallelism = 16 }, i =>
            {
                try
                {
                    history.Record(file, "version " + i);
                }
                catch (IOException e)
                {
                    lock (errors)
                    {
                        errors.Add(e);
                    }
                }
            });
            // Two saves writing the same snapshot at once may collide on the disk (one fails, the other wins);
            // what must hold: nothing else throws, at most Keep versions stay, and each is readable.
            var kept = history.Versions(file);
            Assert.InRange(kept.Count, 1, LocalHistory.Keep);
            Assert.All(kept, v => Assert.StartsWith("version ", File.ReadAllText(v.SnapshotFile), StringComparison.Ordinal));
            Assert.True(errors.Count < 40, $"{errors.Count} saves failed");
        }
        finally
        {
            Lean.DeleteTree(dir);
        }
    }
}

/// <summary>Lean itself under duress: floods of edits, crashes mid-request, and assistants calling in parallel.</summary>
[Collection(Lean.Collection)]
public sealed class LeanStressTests
{
    [Fact]
    public async Task AFloodOfEditsEndsWithTheMessagesForTheLastText()
    {
        // Typing, one keystroke at a time and faster than Lean can keep up, with the goals asked for as it goes.
        Lean.RequireLean();
        string dir = Lean.Sample("Demo");
        await using var server = new LeanServer(new LeanServerCommand(Lean.Executable!, ["--server"], dir));
        var ct = TestContext.Current.CancellationToken;
        await server.StartAsync(ct);
        string uri = LeanServer.UriOf(Path.Combine(dir, "Flood.lean"));
        await server.OpenAsync(uri, "");
        const string final = "theorem ok : 2 + 2 = 4 := by decide\n\ntheorem bad : 2 + 2 = 5 := by decide\n";
        var asks = new List<Task>();
        for (int i = 1; i <= final.Length; i++)
        {
            await server.ChangeAsync(uri, final[..i]);
            if (i % 10 == 0)
            {
                int line = final[..i].Count(c => c == '\n');
                asks.Add(server.InteractiveGoalsAsync(uri, new Position(line, 0), ct));
            }
        }
        await server.WaitForElaborationAsync(uri, ct).WaitAsync(Lean.Patience, ct);
        foreach (Task a in asks)
        {
            // Each question about an older version is answered or refused, never left hanging.
            try
            {
                await a.WaitAsync(TimeSpan.FromSeconds(60), ct);
            }
            catch (JsonRpcException)
            {
            }
        }
        IReadOnlyList<Diagnostic> diags = server.DiagnosticsOf(uri);
        Diagnostic error = Assert.Single(diags, d => d.Severity == DiagnosticSeverity.Error);
        Assert.Equal(2, error.Range.Start.Line); // `bad`, in the final text, and nothing from the versions before
    }

    [Fact]
    public async Task WhenLeanDiesMidRequestTheRequestFailsInsteadOfHanging()
    {
        Lean.RequireLean();
        string dir = Lean.Sample("Demo");
        await using var server = new LeanServer(new LeanServerCommand(Lean.Executable!, ["--server"], dir));
        var ct = TestContext.Current.CancellationToken;
        var crashed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        server.StateChanged += s =>
        {
            if (s == LeanServerState.Crashed)
            {
                crashed.TrySetResult();
            }
        };
        await server.StartAsync(ct);
        string uri = LeanServer.UriOf(Path.Combine(dir, "Slow.lean"));
        // Slow enough to still be elaborating when Lean is killed.
        await server.OpenAsync(uri, "def fib : Nat → Nat\n  | 0 => 0\n  | 1 => 1\n  | n + 2 => fib n + fib (n + 1)\n\ntheorem big : fib 27 = 196418 := by decide\n");
        Task<InteractiveGoals> goals = server.InteractiveGoalsAsync(uri, new Position(5, 40), ct);
        Process.GetProcessById(server.ProcessId!.Value).Kill(entireProcessTree: true);
        await crashed.Task.WaitAsync(TimeSpan.FromSeconds(20), ct);
        await Assert.ThrowsAnyAsync<Exception>(() => goals.WaitAsync(TimeSpan.FromSeconds(20), ct));
        Assert.Equal(LeanServerState.Crashed, server.State);
    }

    [Fact]
    public async Task TwentyFilesOpenedAtOnceAreEachCheckedRightly()
    {
        Lean.RequireLean();
        string dir = Lean.Sample("Demo");
        await using var server = new LeanServer(new LeanServerCommand(Lean.Executable!, ["--server"], dir));
        var ct = TestContext.Current.CancellationToken;
        await server.StartAsync(ct);
        // File i has its error on line i (and the others none), so a mix-up would show.
        string Text(int i) => string.Concat(Enumerable.Range(0, 21).Select(k => k == i ? $"example : {k} = {k + 1} := rfl\n" : $"example : {k} = {k} := rfl\n"));
        string[] uris = Enumerable.Range(0, 20).Select(i => LeanServer.UriOf(Path.Combine(dir, $"Many{i}.lean"))).ToArray();
        await Task.WhenAll(uris.Select((u, i) => server.OpenAsync(u, Text(i))));
        await Task.WhenAll(uris.Select(u => server.WaitForElaborationAsync(u, ct))).WaitAsync(TimeSpan.FromMinutes(3), ct);
        for (int i = 0; i < uris.Length; i++)
        {
            Diagnostic error = Assert.Single(server.DiagnosticsOf(uris[i]), d => d.Severity == DiagnosticSeverity.Error);
            Assert.Equal(i, error.Range.Start.Line);
        }
        foreach (string u in uris)
        {
            await server.CloseAsync(u);
        }
    }

    [Fact]
    public async Task OpeningAndClosingAFile200TimesLeavesLeanWell()
    {
        Lean.RequireLean();
        string dir = Lean.Sample("Demo");
        await using var server = new LeanServer(new LeanServerCommand(Lean.Executable!, ["--server"], dir));
        var ct = TestContext.Current.CancellationToken;
        await server.StartAsync(ct);
        string uri = LeanServer.UriOf(Path.Combine(dir, "Churn.lean"));
        for (int i = 0; i < 200; i++)
        {
            await server.OpenAsync(uri, $"example : {i} = {i} := rfl\n");
            if (i % 3 == 0)
            {
                await server.ChangeAsync(uri, $"example : {i} = {i + 1} := rfl\n");
            }
            await server.CloseAsync(uri);
            Assert.False(server.IsOpen(uri));
        }
        await server.OpenAsync(uri, "example : 7 = 8 := rfl\n");
        await server.WaitForElaborationAsync(uri, ct).WaitAsync(Lean.Patience, ct);
        Diagnostic error = Assert.Single(server.DiagnosticsOf(uri), d => d.Severity == DiagnosticSeverity.Error);
        Assert.Contains("7 = 8", error.Message.Replace('\n', ' '), StringComparison.Ordinal);
        Assert.Equal(LeanServerState.Running, server.State);
    }

    [Fact]
    public async Task ChecksOfTheSameFileAtOnceEachGetTheirOwnAnswer()
    {
        // Two assistants (or one, in parallel) check different texts of the same file at the same moment.
        Lean.RequireLean();
        await using var bench = new Workbench(Lean.Sample("Proofs"));
        string file = Path.Combine(Lean.Sample("Proofs"), "Proofs", "Concurrent.lean");
        var ct = TestContext.Current.CancellationToken;
        string Text(int i) => $"theorem t{i} : {i} + 0 = {i} := rfl\n" + (i % 2 == 0 ? "" : $"example : {i} = {i + 1} := rfl\n");
        FileReport[] reports = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => bench.Session(file).CheckAsync(file, Text(i), ct)))
            .WaitAsync(TimeSpan.FromMinutes(3), ct);
        for (int i = 0; i < reports.Length; i++)
        {
            Assert.Equal(Text(i), reports[i].Text);
            bool hasError = reports[i].Diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error);
            Assert.True(hasError == (i % 2 == 1), $"check {i} got the messages of another text ({string.Join("; ", reports[i].Diagnostics.Select(d => d.Message))})");
        }
    }

    [Fact]
    public async Task ScratchChecksAtOnceEachGetTheirOwnMessages()
    {
        // Prove It, Extract Lemma and the rest check text in a scratch document beside the file; several at once.
        Lean.RequireLean();
        string dir = Lean.Sample("Demo");
        await using var server = new LeanServer(new LeanServerCommand(Lean.Executable!, ["--server"], dir));
        var ct = TestContext.Current.CancellationToken;
        await server.StartAsync(ct);
        string source = Path.Combine(dir, "Demo.lean");
        string Text(int i) => $"theorem s{i} : {i} = {i} := rfl\n" + (i % 2 == 1 ? $"example : {i} = {i + 1} := rfl\n" : "");
        IReadOnlyList<Diagnostic>[] results = await Task.WhenAll(Enumerable.Range(0, 8).Select(i =>
            Core.Proofs.Scratch.CheckAsync(server, source, "Stress", Text(i), ct))).WaitAsync(TimeSpan.FromMinutes(3), ct);
        for (int i = 0; i < results.Length; i++)
        {
            Assert.True(results[i].Any(d => d.Severity == DiagnosticSeverity.Error) == (i % 2 == 1),
                $"scratch check {i} got another text's messages ({string.Join("; ", results[i].Select(d => d.Message))})");
        }
    }

    [Fact]
    public async Task SnippetsRunAtOnceEachGetTheirOwnResult()
    {
        Lean.RequireLean();
        await using var bench = new Workbench(Lean.Sample("Proofs"));
        McpServer server = LeanTools.Create(bench, "stress");
        var ct = TestContext.Current.CancellationToken;
        string[] answers = await Task.WhenAll(Enumerable.Range(2, 10).Select(async i =>
        {
            JsonNode? r = await server.HandleAsync(new JsonObject
            {
                ["jsonrpc"] = "2.0", ["id"] = i, ["method"] = "tools/call",
                ["params"] = new JsonObject { ["name"] = "run_lean", ["arguments"] = new JsonObject { ["code"] = $"#eval {i} * 1000" } },
            }, ct);
            return r!["result"]!["content"]![0]!["text"]!.GetValue<string>();
        })).WaitAsync(TimeSpan.FromMinutes(3), ct);
        for (int k = 0; k < answers.Length; k++)
        {
            int i = k + 2;
            Assert.Contains($"info: {i * 1000}", answers[k], StringComparison.Ordinal);
        }
    }
}

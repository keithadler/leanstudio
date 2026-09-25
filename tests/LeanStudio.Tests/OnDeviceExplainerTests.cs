using LeanStudio.Core.Learn;
using LeanStudio.Core.Processes;

namespace LeanStudio.Tests;

public sealed class OnDeviceExplainerTests
{
    /// <summary>A stand-in for fm that records each call and answers with <paramref name="answer"/>.</summary>
    private sealed class FakeFm(bool available, Func<string, ProcessResult> answer)
    {
        public List<(IReadOnlyList<string> Args, string? Input)> Calls { get; } = [];

        public Task<ProcessResult> Run(IReadOnlyList<string> args, string? input, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            lock (Calls)
            {
                Calls.Add((args, input));
            }
            if (args[0] == "available")
            {
                return Task.FromResult(available
                    ? new ProcessResult(0, "System model available\n")
                    : new ProcessResult(69, "YOU HAVE NOT AGREED TO THE APPLE FOUNDATION MODELS CLI LEGAL NOTICE & TERMS.\n"));
            }
            return Task.FromResult(answer(input!));
        }

        public int Responds => Calls.Count(c => c.Args[0] == "respond");
    }

    [Fact]
    public async Task SendsTheMessageOnStdinWithTheInstructionsAndKeepsTheAnswer()
    {
        var fm = new FakeFm(true, m => new ProcessResult(0, "  Lean expected a Nat here but got a Bool.\n"));
        var explainer = new OnDeviceExplainer(fm.Run);

        string? answer = await explainer.ExplainAsync("type mismatch\n  true\nhas type Bool", TestContext.Current.CancellationToken);

        Assert.Equal("Lean expected a Nat here but got a Bool.", answer);
        var respond = fm.Calls.Single(c => c.Args[0] == "respond");
        Assert.Equal(["respond", "--no-stream", "--instructions", OnDeviceExplainer.Instructions], respond.Args);
        Assert.Equal("type mismatch\n  true\nhas type Bool", respond.Input);
        Assert.Equal(answer, explainer.Cached("type mismatch\n  true\nhas type Bool"));

        // Asked again: the kept answer, and fm is not run a second time.
        Assert.Equal(answer, await explainer.ExplainAsync("type mismatch\n  true\nhas type Bool", TestContext.Current.CancellationToken));
        Assert.Equal(1, fm.Responds);
        Assert.Single(fm.Calls, c => c.Args[0] == "available");
    }

    [Fact]
    public async Task SaysNothingBeforeTheTermsAreAccepted()
    {
        var fm = new FakeFm(false, _ => throw new InvalidOperationException("respond must not run"));
        var explainer = new OnDeviceExplainer(fm.Run);

        Assert.False(await explainer.IsAvailableAsync());
        Assert.Null(await explainer.ExplainAsync("unknown identifier 'foo'", TestContext.Current.CancellationToken));
        Assert.Equal(0, fm.Responds);
    }

    [Theory]
    [InlineData(1, "something went wrong")]
    [InlineData(0, "Error: The model is not available. Try again later.")]
    [InlineData(0, "   \n")]
    public async Task AFailureIsNoAnswerAndIsNotKept(int exitCode, string output)
    {
        var fm = new FakeFm(true, _ => new ProcessResult(exitCode, output));
        var explainer = new OnDeviceExplainer(fm.Run);

        Assert.Null(await explainer.ExplainAsync("some message", TestContext.Current.CancellationToken));
        Assert.Null(explainer.Cached("some message"));
        Assert.Null(await explainer.ExplainAsync("some message", TestContext.Current.CancellationToken));
        Assert.Equal(2, fm.Responds); // tried again, since nothing was kept
    }

    [Fact]
    public async Task CutsLongMessagesAndIgnoresEmptyOnes()
    {
        var fm = new FakeFm(true, m => new ProcessResult(0, "a long one"));
        var explainer = new OnDeviceExplainer(fm.Run);

        string huge = new('x', OnDeviceExplainer.MaxMessageLength * 3);
        Assert.Equal("a long one", await explainer.ExplainAsync(huge, TestContext.Current.CancellationToken));
        Assert.Equal(OnDeviceExplainer.MaxMessageLength, fm.Calls.Single(c => c.Args[0] == "respond").Input!.Length);

        Assert.Null(await explainer.ExplainAsync("  \n ", TestContext.Current.CancellationToken));
        Assert.Equal(1, fm.Responds);
    }

    [Fact]
    public async Task AsksAboutOneMessageAtATime()
    {
        int running = 0, most = 0;
        var fm = new FakeFm(true, m =>
        {
            int now = Interlocked.Increment(ref running);
            most = Math.Max(most, now);
            Thread.Sleep(20);
            Interlocked.Decrement(ref running);
            return new ProcessResult(0, "about " + m);
        });
        var explainer = new OnDeviceExplainer((a, i, ct) => Task.Run(() => fm.Run(a, i, ct), ct));

        string?[] answers = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => explainer.ExplainAsync("message " + i, TestContext.Current.CancellationToken)));

        Assert.Equal(Enumerable.Range(0, 8).Select(i => "about message " + i), answers);
        Assert.Equal(1, most);
    }

    [Fact]
    public async Task ProcessRunnerWritesInputToStdin()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "uses /bin/cat");
        string text = "first line\nsecond ∀ line\n" + new string('y', 200_000); // more than a pipe holds
        ProcessResult r = await ProcessRunner.RunAsync("/bin/cat", [], input: text, ct: TestContext.Current.CancellationToken);
        Assert.True(r.Success);
        Assert.Equal(text + "\n", r.Output);
    }

    [Fact]
    public async Task TheRealModelExplainsAnError()
    {
        OnDeviceExplainer? explainer = OnDeviceExplainer.ForThisMachine;
        Assert.SkipWhen(explainer is null, "no fm (it comes with macOS 27)");
        Assert.SkipWhen(!await explainer!.IsAvailableAsync(), "fm's model is not available (run sudo fm license, and let it download)");

        string? answer = await explainer.ExplainAsync(
            "failed to synthesize\n  Decidable (∀ n : Nat, n + 0 = n)", TestContext.Current.CancellationToken);

        Assert.False(string.IsNullOrWhiteSpace(answer));
    }
}

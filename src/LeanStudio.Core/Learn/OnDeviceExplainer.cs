using System.Collections.Concurrent;
using LeanStudio.Core.Processes;

namespace LeanStudio.Core.Learn;

/// <summary>
/// Explains the Lean messages <see cref="ErrorGuide"/> has no rule for, with Apple's on-device language model
/// through the <c>fm</c> command macOS 27 ships in /usr/bin. The message never leaves the Mac, and there is no
/// account or key. The model is small: its answers are labelled as its own, and it is asked only for what a
/// message means, not for a proof.
/// </summary>
public sealed class OnDeviceExplainer
{
    /// <summary>Where macOS 27 puts the command.</summary>
    public const string FmPath = "/usr/bin/fm";

    /// <summary>What the model is told to do with a message.</summary>
    public const string Instructions =
        "You explain Lean 4 error and warning messages to someone new to Lean. The input is one message from Lean. "
        + "In at most three short sentences of plain English, say what it means and one thing to try. "
        + "Do not write a proof. Do not repeat the message. If you are not sure what it means, say so.";

    /// <summary>Messages longer than this are cut: their start says what went wrong, and the model's context is small.</summary>
    public const int MaxMessageLength = 2000;

    private readonly Func<IReadOnlyList<string>, string?, CancellationToken, Task<ProcessResult>> _run;
    private readonly ConcurrentDictionary<string, string> _answers = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _one = new(1, 1);
    private Task<bool>? _available;

    /// <summary>Explain with <paramref name="run"/>, which runs <c>fm</c> with the given arguments and stdin.</summary>
    /// <param name="run">Runs <c>fm</c>; tests pass a stand-in.</param>
    public OnDeviceExplainer(Func<IReadOnlyList<string>, string?, CancellationToken, Task<ProcessResult>> run) => _run = run;

    /// <summary>The explainer for this Mac, or null where there is no <c>fm</c> (before macOS 27, and off macOS).</summary>
    public static OnDeviceExplainer? ForThisMachine { get; } = OperatingSystem.IsMacOS() && File.Exists(FmPath)
        ? new OnDeviceExplainer((args, input, ct) => ProcessRunner.RunAsync(FmPath, args, input: input, ct: ct))
        : null;

    /// <summary>
    /// Whether the model can answer: its terms have been accepted (<c>sudo fm license</c>) and it has been downloaded.
    /// Asked once; the answer is kept.
    /// </summary>
    public Task<bool> IsAvailableAsync() => _available ??= CheckAvailableAsync();

    private async Task<bool> CheckAvailableAsync()
    {
        // Prints "System model available", or "System model unavailable: <reason>"; exits 69 before the terms are accepted.
        ProcessResult r = await _run(["available"], null, CancellationToken.None).ConfigureAwait(false);
        return r.Success && r.Output.Contains("model available", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>An answer already given for <paramref name="message"/>, or null.</summary>
    public string? Cached(string message) => _answers.TryGetValue(Key(message), out string? a) ? a : null;

    /// <summary>
    /// The model's explanation of <paramref name="message"/>, or null when it is unavailable, fails or says nothing.
    /// One message at a time; answers are kept, so asking again is free.
    /// </summary>
    public async Task<string?> ExplainAsync(string message, CancellationToken ct = default)
    {
        string key = Key(message);
        if (key.Length == 0)
        {
            return null;
        }
        if (_answers.TryGetValue(key, out string? known))
        {
            return known;
        }
        if (!await IsAvailableAsync().ConfigureAwait(false))
        {
            return null;
        }
        await _one.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_answers.TryGetValue(key, out known))
            {
                return known;
            }
            // The message goes on stdin: fm ignores stdin when a prompt is also given as an argument.
            ProcessResult r = await _run(["respond", "--no-stream", "--instructions", Instructions], key, ct).ConfigureAwait(false);
            string answer = r.Success ? r.Output.Trim() : "";
            if (answer.Length == 0 || answer.StartsWith("Error:", StringComparison.Ordinal))
            {
                return null;
            }
            _answers[key] = answer;
            return answer;
        }
        finally
        {
            _one.Release();
        }
    }

    private static string Key(string message)
    {
        string m = message.Trim();
        return m.Length > MaxMessageLength ? m[..MaxMessageLength] : m;
    }
}

using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;

namespace LeanStudio.Core.Ai;

/// <summary>
/// Apple's on-device model, which macOS 27 opens to every app through the <c>fm</c> command: <c>fm available</c>
/// says whether it is ready, <c>fm serve</c> puts it behind an OpenAI-compatible API on this computer, and
/// <c>fm respond</c> answers one prompt. Nothing leaves the Mac. The model is small (about 3 billion parameters, and
/// 4096 tokens for prompt and answer together), so prompts to it carry the goal and the nearby code, not whole files.
/// </summary>
public static class AppleIntelligence
{
    /// <summary>Where macOS 27 puts the <c>fm</c> command.</summary>
    public const string FmPath = "/usr/bin/fm";

    /// <summary>The port <c>fm serve</c> listens on when not told otherwise.</summary>
    public const int DefaultPort = 1976;

    /// <summary>How many tokens the on-device model sees at once, prompt and answer together.</summary>
    public const int ContextTokens = 4096;

    /// <summary>The name people see.</summary>
    public const string DisplayName = "Apple on-device model";

    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static Process? _server;
    private static string? _serverUrl;

    /// <summary>This is a Mac with the <c>fm</c> command (macOS 27 or later).</summary>
    public static bool IsPresent => OperatingSystem.IsMacOS() && File.Exists(FmPath);

    /// <summary>
    /// Whether the model can answer now, and if not, why, in plain words: Apple Intelligence may be off, or the model
    /// still downloading.
    /// </summary>
    public static async Task<(bool Ready, string Detail)> CheckAsync(CancellationToken ct = default)
    {
        if (!IsPresent)
        {
            return (false, OperatingSystem.IsMacOS() ? "needs macOS 27 or later" : "only on a Mac");
        }
        Processes.ProcessResult r = await Processes.ProcessRunner.RunAsync(FmPath, ["available"], ct: ct).ConfigureAwait(false);
        string output = r.Output.Trim();
        bool ready = r.Success && output.Contains("available", StringComparison.OrdinalIgnoreCase)
            && !output.Contains("unavailable", StringComparison.OrdinalIgnoreCase);
        return (ready, ready ? "ready" : ExplainUnavailable(output));
    }

    /// <summary>What <c>fm available</c>'s reason means for the person.</summary>
    public static string ExplainUnavailable(string output)
    {
        if (output.Contains("appleIntelligenceNotEnabled", StringComparison.OrdinalIgnoreCase))
        {
            return "turn on Apple Intelligence in System Settings to use it";
        }
        if (output.Contains("modelNotReady", StringComparison.OrdinalIgnoreCase))
        {
            return "the model is still downloading; try again in a few minutes";
        }
        if (output.Contains("deviceNotEligible", StringComparison.OrdinalIgnoreCase))
        {
            return "this Mac cannot run Apple Intelligence";
        }
        if (output.Contains("license", StringComparison.OrdinalIgnoreCase))
        {
            return "accept the model's license first: run `sudo fm license` in Terminal";
        }
        return output.Length > 0 ? output : "not available";
    }

    /// <summary>
    /// The on-device model, served by <c>fm serve</c>: one already running is used, otherwise Lean Studio starts one
    /// on a free port (and stops it when it quits, see <see cref="Shutdown"/>). When no server can be started, the
    /// model is reached through <c>fm respond</c> instead, one answer per run.
    /// </summary>
    public static async Task<IChatModel> ModelAsync(CancellationToken ct = default)
    {
        string? url = await ServerAsync(ct).ConfigureAwait(false);
        return url is not null
            ? new OpenAiCompatibleModel(DisplayName, url, "system", AiLocation.OnDevice, ContextTokens)
            : new AppleFmCliModel();
    }

    private static async Task<string?> ServerAsync(CancellationToken ct)
    {
        await Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_serverUrl is not null && _server is { HasExited: false } && await RespondsAsync(_serverUrl, ct).ConfigureAwait(false))
            {
                return _serverUrl;
            }
            string standard = $"http://127.0.0.1:{DefaultPort}/v1";
            if (await RespondsAsync(standard, ct).ConfigureAwait(false))
            {
                return _serverUrl = standard;
            }
            if (!IsPresent)
            {
                return null;
            }
            int port = FreePort();
            string mine = $"http://127.0.0.1:{port}/v1";
            if (await StartAsync(["serve", "--port", port.ToString(System.Globalization.CultureInfo.InvariantCulture)], [mine], ct).ConfigureAwait(false) is string a)
            {
                return _serverUrl = a;
            }
            // An fm that does not take --port: its own default port.
            return _serverUrl = await StartAsync(["serve"], [standard, "http://127.0.0.1:8000/v1"], ct).ConfigureAwait(false);
        }
        finally
        {
            Gate.Release();
        }
    }

    private static async Task<string?> StartAsync(string[] args, string[] urls, CancellationToken ct)
    {
        Stop();
        var psi = new ProcessStartInfo(FmPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (string a in args)
        {
            psi.ArgumentList.Add(a);
        }
        Process p;
        try
        {
            p = Process.Start(psi) ?? throw new InvalidOperationException();
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return null;
        }
        // Drain its output so it never blocks writing to a full pipe.
        p.OutputDataReceived += (_, _) => { };
        p.ErrorDataReceived += (_, _) => { };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        _server = p;
        for (int i = 0; i < 40 && !p.HasExited; i++)
        {
            foreach (string u in urls)
            {
                if (await RespondsAsync(u, ct).ConfigureAwait(false))
                {
                    return u;
                }
            }
            await Task.Delay(250, ct).ConfigureAwait(false);
        }
        Stop();
        return null;
    }

    private static async Task<bool> RespondsAsync(string baseUrl, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            using HttpResponseMessage r = await AiHttp.Shared.GetAsync(baseUrl + "/models", cts.Token).ConfigureAwait(false);
            return r.IsSuccessStatusCode;
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            return false;
        }
    }

    private static int FreePort()
    {
        using var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        return ((IPEndPoint)l.LocalEndpoint).Port;
    }

    private static void Stop()
    {
        if (_server is Process p)
        {
            try
            {
                if (!p.HasExited)
                {
                    p.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException)
            {
            }
            p.Dispose();
            _server = null;
            _serverUrl = null;
        }
    }

    /// <summary>Stop the <c>fm serve</c> Lean Studio started, if it did. One that was already running is left alone.</summary>
    public static void Shutdown() => Stop();
}

/// <summary>
/// Apple's on-device model through <c>fm respond</c>, for when <c>fm serve</c> cannot be started: the instructions go
/// in <c>--instructions</c>, the conversation on standard input, and the answer streams from standard output.
/// </summary>
public sealed class AppleFmCliModel : IChatModel
{
    /// <inheritdoc/>
    public string DisplayName => AppleIntelligence.DisplayName;

    /// <inheritdoc/>
    public AiLocation Location => AiLocation.OnDevice;

    /// <inheritdoc/>
    public int ContextTokens => AppleIntelligence.ContextTokens;

    /// <summary>The conversation as one prompt: earlier turns labelled, the last message as it is.</summary>
    public static string Transcript(IReadOnlyList<ChatMessage> messages)
    {
        var turns = messages.Where(m => m.Role != ChatRole.System).ToList();
        if (turns.Count == 1)
        {
            return turns[0].Text;
        }
        var sb = new StringBuilder();
        foreach (ChatMessage m in turns)
        {
            sb.Append(m.Role == ChatRole.User ? "User: " : "Assistant: ").AppendLine(m.Text).AppendLine();
        }
        sb.Append("Assistant:");
        return sb.ToString();
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<string> StreamAsync(IReadOnlyList<ChatMessage> messages, ChatOptions options, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo(AppleIntelligence.FmPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        psi.ArgumentList.Add("respond");
        string system = string.Join("\n\n", messages.Where(m => m.Role == ChatRole.System).Select(m => m.Text));
        if (system.Length > 0)
        {
            psi.ArgumentList.Add("--instructions");
            psi.ArgumentList.Add(system);
        }
        Process p;
        try
        {
            p = Process.Start(psi) ?? throw new InvalidOperationException("fm did not start");
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            throw new AiException("Could not run Apple's fm command: " + e.Message, e);
        }
        using (p)
        using (ct.Register(() =>
        {
            try
            {
                p.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }
        }))
        {
            Task<string> errors = p.StandardError.ReadToEndAsync(CancellationToken.None);
            await p.StandardInput.WriteAsync(Transcript(messages)).ConfigureAwait(false);
            p.StandardInput.Close();
            char[] buffer = new char[256];
            while (true)
            {
                int n = await p.StandardOutput.ReadAsync(buffer, ct).ConfigureAwait(false);
                if (n == 0)
                {
                    break;
                }
                yield return new string(buffer, 0, n);
            }
            await p.WaitForExitAsync(ct).ConfigureAwait(false);
            if (p.ExitCode != 0)
            {
                string e = (await errors.ConfigureAwait(false)).Trim();
                throw new AiException("Apple's on-device model could not answer: " + AppleIntelligence.ExplainUnavailable(e));
            }
        }
    }
}

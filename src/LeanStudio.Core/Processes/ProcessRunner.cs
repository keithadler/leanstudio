using System.Diagnostics;
using System.Text;

namespace LeanStudio.Core.Processes;

/// <summary>How a process run by <see cref="ProcessRunner"/> ended.</summary>
/// <param name="ExitCode">The process's exit code, or <c>-1</c> when it could not be started.</param>
/// <param name="Output">
/// Everything the process wrote to stdout and stderr, interleaved in arrival order, one line per <c>\n</c>
/// (never <c>\r\n</c>). When the process could not be started, the error message instead.
/// </param>
public sealed record ProcessResult(int ExitCode, string Output)
{
    /// <summary>The exit code is <c>0</c>.</summary>
    public bool Success => ExitCode == 0;
}

/// <summary>Runs a command to completion, streaming each line of stdout and stderr as it arrives.</summary>
public static class ProcessRunner
{
    /// <summary>
    /// Start <paramref name="fileName"/> without a shell or window, close its stdin, and wait for it to exit.
    /// Arguments are passed as a list, so they need no quoting. Stdout and stderr are decoded as UTF-8.
    /// </summary>
    /// <param name="fileName">The executable to run: a path, or a name looked up on <c>PATH</c>.</param>
    /// <param name="arguments">The arguments, one per element.</param>
    /// <param name="workingDirectory">The directory to run in; <see langword="null"/> for the current one.</param>
    /// <param name="onLine">
    /// Called with each line of stdout or stderr as it arrives, on a thread-pool thread, and with the error
    /// message when the process cannot be started.
    /// </param>
    /// <param name="environment">Variables to set (or override) in the process's environment.</param>
    /// <param name="ct">Cancelling kills the whole process tree and throws <see cref="OperationCanceledException"/>.</param>
    /// <returns>
    /// The exit code and combined output. A process that cannot be started does not throw: the result has exit
    /// code <c>-1</c> and the error message as its output.
    /// </returns>
    public static async Task<ProcessResult> RunAsync(
        string fileName,
        IEnumerable<string> arguments,
        string? workingDirectory = null,
        Action<string>? onLine = null,
        IReadOnlyDictionary<string, string>? environment = null,
        CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        if (workingDirectory is not null)
        {
            psi.WorkingDirectory = workingDirectory;
        }
        foreach (string a in arguments)
        {
            psi.ArgumentList.Add(a);
        }
        if (environment is not null)
        {
            foreach ((string k, string v) in environment)
            {
                psi.Environment[k] = v;
            }
        }

        var all = new StringBuilder();
        using var p = new Process { StartInfo = psi };
        void Line(string? s)
        {
            if (s is null)
            {
                return;
            }
            lock (all)
            {
                // "\n", not AppendLine: on Windows that adds "\r\n", and every parser here splits on "\n".
                all.Append(s).Append('\n');
            }
            onLine?.Invoke(s);
        }
        p.OutputDataReceived += (_, e) => Line(e.Data);
        p.ErrorDataReceived += (_, e) => Line(e.Data);
        try
        {
            p.Start();
        }
        catch (System.ComponentModel.Win32Exception e)
        {
            string msg = $"could not run {fileName}: {e.Message}";
            onLine?.Invoke(msg);
            return new ProcessResult(-1, msg);
        }
        p.StandardInput.Close();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        try
        {
            await p.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                p.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }
            throw;
        }
        // WaitForExitAsync returns once the process exits; this drains the redirected streams.
        p.WaitForExit();
        lock (all)
        {
            return new ProcessResult(p.ExitCode, all.ToString());
        }
    }
}

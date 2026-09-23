using System.Diagnostics;
using System.Text;

namespace LeanStudio.Core.Processes;

public sealed record ProcessResult(int ExitCode, string Output)
{
    public bool Success => ExitCode == 0;
}

/// <summary>Runs a command to completion, streaming each line of stdout and stderr as it arrives.</summary>
public static class ProcessRunner
{
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

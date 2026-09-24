using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace LeanStudio.Core.Toolchains;

/// <summary>One of Lean's file workers: the process that elaborates one open file.</summary>
/// <param name="Pid">The process id.</param>
/// <param name="File">The file it elaborates.</param>
/// <param name="MemoryBytes">Its resident memory.</param>
/// <param name="Running">How long it has run.</param>
public sealed record LeanWorker(int Pid, string File, long MemoryBytes, TimeSpan Running)
{
    /// <summary>The memory, as a person reads it: <c>413 MB</c>, <c>2.1 GB</c>.</summary>
    public string Memory => MemoryBytes >= 1L << 30 ? $"{MemoryBytes / (double)(1L << 30):0.0} GB" : $"{MemoryBytes >> 20} MB";
}

/// <summary>
/// Lean's file workers on this machine (<c>lean --worker file:///…</c>, one per open file): which file each is on,
/// how much memory it uses and for how long, so a runaway one can be found and stopped.
/// </summary>
public static class LeanProcesses
{
    /// <summary>Every running file worker, optionally only those whose file is under <paramref name="root"/>, biggest first.</summary>
    public static async Task<IReadOnlyList<LeanWorker>> ListAsync(string? root = null, CancellationToken ct = default)
    {
        IReadOnlyList<LeanWorker> all;
        if (OperatingSystem.IsWindows())
        {
            var r = await Processes.ProcessRunner.RunAsync("powershell",
                ["-NoProfile", "-Command", "Get-CimInstance Win32_Process -Filter \"name='lean.exe'\" | Select-Object ProcessId,WorkingSetSize,CreationDate,CommandLine | ConvertTo-Json -Compress"],
                ct: ct).ConfigureAwait(false);
            all = ParseWindows(r.Output, DateTime.Now);
        }
        else
        {
            var r = await Processes.ProcessRunner.RunAsync("ps", ["-axo", "pid=,rss=,etime=,args="], ct: ct).ConfigureAwait(false);
            all = ParsePs(r.Output);
        }
        string? prefix = root is null ? null : Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return all.Where(w => prefix is null || w.File.StartsWith(prefix, StringComparison.Ordinal))
                  .OrderByDescending(w => w.MemoryBytes).ToList();
    }

    /// <summary>Read <c>ps -axo pid=,rss=,etime=,args=</c>: the Lean workers in it (rss is in KiB).</summary>
    public static IReadOnlyList<LeanWorker> ParsePs(string output)
    {
        var list = new List<LeanWorker>();
        foreach (string raw in output.Split('\n'))
        {
            string[] f = raw.Trim().Split(' ', 4, StringSplitOptions.RemoveEmptyEntries);
            if (f.Length < 4 || !int.TryParse(f[0], CultureInfo.InvariantCulture, out int pid) || !long.TryParse(f[1], CultureInfo.InvariantCulture, out long kb)
                || WorkerFile(f[3]) is not string file)
            {
                continue;
            }
            list.Add(new LeanWorker(pid, file, kb * 1024, Elapsed(f[2])));
        }
        return list;
    }

    /// <summary>Read PowerShell's JSON for Win32_Process (one object, or a list).</summary>
    public static IReadOnlyList<LeanWorker> ParseWindows(string json, DateTime now)
    {
        var list = new List<LeanWorker>();
        if (string.IsNullOrWhiteSpace(json))
        {
            return list;
        }
        using var doc = JsonDocument.Parse(json);
        IEnumerable<JsonElement> items = doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement.EnumerateArray() : [doc.RootElement];
        foreach (JsonElement p in items)
        {
            if (p.TryGetProperty("CommandLine", out JsonElement cl) && cl.GetString() is string cmd && WorkerFile(cmd) is string file)
            {
                DateTime started = p.TryGetProperty("CreationDate", out JsonElement cd) && cd.ValueKind == JsonValueKind.String
                    && DateTime.TryParse(cd.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime d) ? d : now;
                list.Add(new LeanWorker(p.GetProperty("ProcessId").GetInt32(), file, p.GetProperty("WorkingSetSize").GetInt64(), now - started));
            }
        }
        return list;
    }

    /// <summary>The file a <c>lean --worker file:///…</c> command line elaborates, or null for any other process.</summary>
    private static string? WorkerFile(string args)
    {
        int at = args.IndexOf("--worker ", StringComparison.Ordinal);
        if (at < 0 || !args[..at].Contains("lean", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        string uri = args[(at + "--worker ".Length)..].Trim().Trim('"');
        return Uri.TryCreate(uri, UriKind.Absolute, out Uri? u) && u.IsFile ? u.LocalPath : null;
    }

    /// <summary><c>ps</c>'s elapsed time: <c>[[dd-]hh:]mm:ss</c>.</summary>
    private static TimeSpan Elapsed(string etime)
    {
        int days = 0;
        if (etime.Contains('-', StringComparison.Ordinal))
        {
            string[] d = etime.Split('-', 2);
            days = int.Parse(d[0], CultureInfo.InvariantCulture);
            etime = d[1];
        }
        int[] parts = etime.Split(':').Select(x => int.Parse(x, CultureInfo.InvariantCulture)).Reverse().ToArray();
        return new TimeSpan(days, parts.Length > 2 ? parts[2] : 0, parts.Length > 1 ? parts[1] : 0, parts[0]);
    }

    /// <summary>Stop a worker. Lean's server notices, and restarting the file (Lean ▸ Restart File) starts a new one.</summary>
    public static bool Kill(int pid)
    {
        try
        {
            using Process p = Process.GetProcessById(pid);
            p.Kill();
            return true;
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }
}

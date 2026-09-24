using System.Globalization;
using System.Text.RegularExpressions;

namespace LeanStudio.Core.Workflow;

/// <summary>How far a long task has got, as read from what it prints.</summary>
public sealed class TaskProgress
{
    /// <summary>What is happening: <c>Building</c>, <c>Fetching Mathlib's cache</c>, <c>Downloading Lean …</c>.</summary>
    public string Stage { get; internal set; } = "";

    /// <summary>Jobs, files or declarations done.</summary>
    public int Done { get; internal set; }

    /// <summary>Out of how many; 0 while unknown.</summary>
    public int Total { get; internal set; }

    /// <summary>Of the jobs done, how many Lean compiled here (the rest came from a cache or an earlier build).</summary>
    public int Compiled { get; internal set; }

    /// <summary>Of the jobs done, how many were replayed from a cache or an earlier build.</summary>
    public int Replayed { get; internal set; }

    /// <summary>The last thing finished, such as a module name.</summary>
    public string? Last { get; internal set; }

    /// <summary>How long the last thing took, when the tool said.</summary>
    public TimeSpan? LastTook { get; internal set; }

    /// <summary>A download's speed as the tool reports it (<c>137 KB/s</c>), when it does.</summary>
    public string? Rate { get; internal set; }

    /// <summary>A rough estimate of the time left, when there is enough to go on.</summary>
    public TimeSpan? Remaining { get; internal set; }

    /// <summary>The slowest things so far (modules and how long each took), slowest first, at most five.</summary>
    public IReadOnlyList<(string Name, TimeSpan Took)> Slowest { get; internal set; } = [];

    /// <summary>Warnings and errors seen so far.</summary>
    public int Warnings { get; internal set; }

    /// <summary>Errors seen so far.</summary>
    public int Errors { get; internal set; }

    /// <summary>The fraction done, 0 to 1, or null while the total is unknown.</summary>
    public double? Fraction => Total > 0 ? Math.Clamp(Done / (double)Total, 0, 1) : null;

    /// <summary>
    /// One line for a status bar: <c>8,864 / 8,951 · 212 built, 8,652 from cache · about 12 min left · Apery.Table.V02 (83 s)</c>.
    /// </summary>
    public string Detail
    {
        get
        {
            var parts = new List<string>();
            if (Total > 0)
            {
                parts.Add(string.Create(CultureInfo.InvariantCulture, $"{Done:N0} / {Total:N0}"));
            }
            if (Replayed > 0)
            {
                parts.Add(string.Create(CultureInfo.InvariantCulture, $"{Compiled:N0} built, {Replayed:N0} from cache"));
            }
            if (Rate is not null)
            {
                parts.Add(Rate);
            }
            if (Remaining is TimeSpan r)
            {
                parts.Add("about " + Format(r) + " left");
            }
            if (Last is not null)
            {
                parts.Add(LastTook is TimeSpan t ? $"{Last} ({Format(t)})" : Last);
            }
            return string.Join(" · ", parts);
        }
    }

    /// <summary>A duration the way a person says it: <c>45 s</c>, <c>12 min</c>, <c>1 h 5 min</c>.</summary>
    public static string Format(TimeSpan t) =>
        t.TotalSeconds < 90 ? $"{Math.Max(1, (int)Math.Round(t.TotalSeconds))} s"
        : t.TotalMinutes < 90 ? $"{(int)Math.Round(t.TotalMinutes)} min"
        : $"{(int)t.TotalHours} h {t.Minutes} min";
}

/// <summary>
/// Reads progress out of what long tasks print, one line at a time: Lake's <c>✔ [8864/8951] Built X (83s)</c>, the
/// Mathlib cache's <c>Downloaded: 334 file(s) [attempted 334/8700 = 3%, 137 KB/s]</c>, and elan's
/// <c>info: downloading …/lean-4.34.0-rc1-….tar.zst</c>. The time left is estimated from the recent rate of jobs
/// Lean actually compiled, since jobs replayed from a cache take no time and would make it look almost done.
/// </summary>
public sealed class ProgressReader
{
    private static readonly Regex LakeJob = new(
        @"^(?<mark>\S)?\s*\[(?<done>\d+)/(?<total>\d+)\]\s+(?<verb>\w+)\s+(?<what>\S+)(?:\s+\((?<time>[\d.]+)(?<unit>ms|s|m|h)\))?",
        RegexOptions.Compiled);
    private static readonly Regex CacheLine = new(@"attempted (?<done>\d+)/(?<total>\d+)\s*=\s*\d+%(?:,\s*(?<rate>[\d.]+\s*[KMG]?B/s))?", RegexOptions.Compiled);
    private static readonly Regex Toolchain = new(@"info: (?<verb>downloading|installing)\b.*?(?:lean-(?<version>[\w.\-]+?)-(?:darwin|linux|windows)|$)", RegexOptions.Compiled);
    private static readonly Regex Message = new(@"^(?<sev>warning|error):", RegexOptions.Compiled);

    private readonly List<DateTimeOffset> _compiledAt = [];
    private readonly List<(string, TimeSpan)> _took = [];

    /// <summary>What has been read so far.</summary>
    public TaskProgress Progress { get; } = new();

    /// <summary>Read one line printed at <paramref name="now"/>. Returns whether the progress changed.</summary>
    public bool Feed(string line, DateTimeOffset now)
    {
        TaskProgress p = Progress;
        string l = line.Trim();
        Match m = LakeJob.Match(l);
        if (m.Success)
        {
            p.Stage = "Building";
            p.Done = int.Parse(m.Groups["done"].Value, CultureInfo.InvariantCulture);
            p.Total = int.Parse(m.Groups["total"].Value, CultureInfo.InvariantCulture);
            string verb = m.Groups["verb"].Value;
            p.Last = m.Groups["what"].Value;
            p.LastTook = m.Groups["time"].Success ? Took(m.Groups["time"].Value, m.Groups["unit"].Value) : null;
            if (verb == "Replayed" || verb == "Fetched" || verb == "Unpacked")
            {
                p.Replayed++;
            }
            else
            {
                p.Compiled++;
                _compiledAt.Add(now);
                if (p.LastTook is TimeSpan t)
                {
                    _took.Add((p.Last, t));
                    p.Slowest = _took.OrderByDescending(x => x.Item2).Take(5).ToList();
                }
            }
            Estimate(now);
            return true;
        }
        m = CacheLine.Match(l);
        if (m.Success)
        {
            p.Stage = "Fetching Mathlib's cache";
            p.Done = int.Parse(m.Groups["done"].Value, CultureInfo.InvariantCulture);
            p.Total = int.Parse(m.Groups["total"].Value, CultureInfo.InvariantCulture);
            p.Rate = m.Groups["rate"].Success ? m.Groups["rate"].Value : p.Rate;
            _compiledAt.Add(now);
            Estimate(now);
            return true;
        }
        m = Toolchain.Match(l);
        if (m.Success)
        {
            string version = m.Groups["version"].Success && m.Groups["version"].Value.Length > 0 ? " " + m.Groups["version"].Value : "";
            p.Stage = (m.Groups["verb"].Value == "downloading" ? "Downloading Lean" : "Installing Lean") + version;
            return true;
        }
        m = Message.Match(l);
        if (m.Success)
        {
            p.Warnings++;
            if (m.Groups["sev"].Value == "error")
            {
                p.Errors++;
            }
            return true;
        }
        return false;
    }

    private static TimeSpan Took(string value, string unit)
    {
        double v = double.Parse(value, CultureInfo.InvariantCulture);
        return unit switch { "ms" => TimeSpan.FromMilliseconds(v), "m" => TimeSpan.FromMinutes(v), "h" => TimeSpan.FromHours(v), _ => TimeSpan.FromSeconds(v) };
    }

    /// <summary>The time left, from the rate of real work (compiled jobs, downloaded files) in the last ten minutes.</summary>
    private void Estimate(DateTimeOffset now)
    {
        TaskProgress p = Progress;
        _compiledAt.RemoveAll(t => now - t > TimeSpan.FromMinutes(10));
        if (_compiledAt.Count < 3 || p.Total == 0)
        {
            p.Remaining = null;
            return;
        }
        TimeSpan window = now - _compiledAt[0];
        if (window < TimeSpan.FromSeconds(20))
        {
            p.Remaining = null;
            return;
        }
        double perSecond = (_compiledAt.Count - 1) / window.TotalSeconds;
        int left = p.Total - p.Done;
        p.Remaining = left <= 0 ? TimeSpan.Zero : TimeSpan.FromSeconds(left / perSecond);
    }
}

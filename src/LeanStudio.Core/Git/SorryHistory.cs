using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace LeanStudio.Core.Git;

/// <summary>One commit and how many <c>sorry</c>s the project had in it.</summary>
/// <param name="Hash">The commit's hash.</param>
/// <param name="Date">Its author date, <c>yyyy-MM-dd</c>.</param>
/// <param name="Subject">Its first line.</param>
/// <param name="Sorries">The <c>sorry</c> and <c>admit</c> words in its Lean files, outside <c>--</c> comments.</param>
public sealed record SorryPoint(string Hash, string Date, string Subject, int Sorries);

/// <summary>
/// How a formalization is coming along, read from Git: the number of <c>sorry</c>s the project had at each of its latest
/// commits, oldest first, drawn as a sparkline. Counted with <c>git grep</c> on each commit, so nothing is checked out or
/// built and a long history takes seconds. A <c>sorry</c> inside a block comment still counts; one after <c>--</c> does not.
/// </summary>
public static class SorryHistory
{
    private static readonly Regex Word = new(@"\b(?:sorry|admit)\b", RegexOptions.Compiled);

    /// <summary>The sorries in <paramref name="grepOutput"/>, the lines <c>git grep -n</c> printed (<c>commit:path:line:text</c>).</summary>
    public static int Count(string grepOutput)
    {
        int count = 0;
        foreach (string line in grepOutput.Split('\n'))
        {
            string[] parts = line.TrimEnd('\r').Split(':', 4);
            if (parts.Length < 4)
            {
                continue;
            }
            string text = parts[3];
            int comment = text.IndexOf("--", StringComparison.Ordinal);
            count += Word.Matches(comment >= 0 ? text[..comment] : text).Count;
        }
        return count;
    }

    /// <summary>The commits <c>git log --format=%H%x09%as%x09%s</c> printed, newest first, as (hash, date, subject).</summary>
    public static IReadOnlyList<(string Hash, string Date, string Subject)> ParseLog(string log) =>
        [.. log.Split('\n').Select(l => l.TrimEnd('\r').Split('\t', 3)).Where(p => p.Length == 3 && p[0].Length >= 7).Select(p => (p[0], p[1], p[2]))];

    /// <summary>
    /// The sorries at each of the latest <paramref name="commits"/> commits of <paramref name="repo"/>,
    /// oldest first. Empty when it has no history.
    /// </summary>
    public static async Task<IReadOnlyList<SorryPoint>> ReadAsync(GitRepository repo, int commits = 30, CancellationToken ct = default)
    {
        Processes.ProcessResult log = await repo.RunAsync(["log", $"-n{Math.Clamp(commits, 1, 500)}", "--format=%H%x09%as%x09%s"], ct: ct).ConfigureAwait(false);
        if (!log.Success)
        {
            return [];
        }
        var points = new List<SorryPoint>();
        foreach ((string hash, string date, string subject) in ParseLog(log.Output).Reverse())
        {
            ct.ThrowIfCancellationRequested();
            // Exit code 1 means no match: the commit has no sorry (or no Lean file), which is a count of 0.
            Processes.ProcessResult grep = await repo.RunAsync(["grep", "-n", "-I", "-E", @"\b(sorry|admit)\b", hash, "--", "*.lean"], ct: ct).ConfigureAwait(false);
            points.Add(new SorryPoint(hash, date, subject, grep.ExitCode is 0 or 1 ? Count(grep.Output) : 0));
        }
        return points;
    }

    /// <summary>The counts as one character each, <c>▁</c> for the lowest to <c>█</c> for the highest; all <c>▁</c> when they are equal.</summary>
    public static string Sparkline(IEnumerable<int> counts)
    {
        const string Bars = "▁▂▃▄▅▆▇█";
        List<int> list = [.. counts];
        if (list.Count == 0)
        {
            return "";
        }
        int min = list.Min(), max = list.Max();
        return string.Concat(list.Select(c => max == min ? Bars[0] : Bars[(int)Math.Round((c - min) * (Bars.Length - 1.0) / (max - min))]));
    }

    /// <summary>A plain-text summary: the sparkline, the first and the latest count, and a line for each commit that changed it.</summary>
    public static string ToText(IReadOnlyList<SorryPoint> points)
    {
        if (points.Count == 0)
        {
            return "No commits to read.";
        }
        var sb = new StringBuilder();
        SorryPoint first = points[0], last = points[^1];
        sb.Append(CultureInfo.InvariantCulture, $"Sorries over the last {points.Count} commit{(points.Count == 1 ? "" : "s")}: {Sparkline(points.Select(p => p.Sorries))}  {first.Sorries} → {last.Sorries}");
        int change = last.Sorries - first.Sorries;
        sb.Append(change == 0 ? " (no change)\n" : string.Create(CultureInfo.InvariantCulture, $" ({(change > 0 ? "+" : "−")}{Math.Abs(change)})\n"));
        int previous = first.Sorries;
        foreach (SorryPoint p in points.Skip(1))
        {
            if (p.Sorries != previous)
            {
                sb.Append(CultureInfo.InvariantCulture, $"  {p.Date}  {p.Hash[..7]}  {(p.Sorries > previous ? "+" : "−")}{Math.Abs(p.Sorries - previous)}  {p.Subject}\n");
            }
            previous = p.Sorries;
        }
        return sb.ToString().TrimEnd();
    }
}

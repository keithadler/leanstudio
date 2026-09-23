using System.Globalization;
using System.Text.RegularExpressions;
using LeanStudio.Core.Processes;
using LeanStudio.Core.Projects;
using LeanStudio.Core.Workflow;

namespace LeanStudio.Core.Proofs;

/// <summary>How long Lean spent on one declaration, and the part of it that took longest.</summary>
public sealed record DeclarationTiming(int Line, string Declaration, double Seconds, string? HotSpot, double HotSpotSeconds)
{
    public string Time => Format(Seconds);

    public static string Format(double s) => s < 1 ? $"{s * 1000:F0} ms" : $"{s:F2} s";

    /// <summary>0 cool, 1 warm, 2 hot: under 100 ms, under a second, or more.</summary>
    public int Heat => Seconds >= 1 ? 2 : Seconds >= 0.1 ? 1 : 0;

    public string Detail => HotSpot is null ? Time : $"{Time}   slowest part: {HotSpot} ({Format(HotSpotSeconds)})";
}

/// <summary>
/// A performance heat map of a file: Lean's own profiler (<c>trace.profiler</c>) run over a copy of the file,
/// read back as a time per declaration and the step inside it that cost the most. The same numbers Mathlib's
/// maintainers look at when a file is slow, without editing the file or reading raw trace output.
/// </summary>
public static partial class Profiler
{
    /// <summary>Steps faster than this (in seconds) are not reported by Lean at all.</summary>
    public const double Threshold = 0.005;

    [GeneratedRegex(@"^(?<indent>\s*)\[(?<cls>[\w.]+)\] \[(?<s>[0-9.]+)\]\s*(?:✅️|❌️|💥️|✅|❌|💥)?\s*(?<what>.*)$")]
    private static partial Regex TraceLine();

    /// <summary>
    /// Turn Lean's <c>--json</c> output with the profiler on into one timing per declaration (by 0-based line).
    /// <paramref name="lines"/> is the file's text, for naming each declaration.
    /// </summary>
    public static IReadOnlyList<DeclarationTiming> Parse(string jsonLines, IReadOnlyList<string> lines)
    {
        var messages = new List<(int Line, string Text)>();
        foreach (string l in jsonLines.Split('\n'))
        {
            if (!l.StartsWith('{'))
            {
                continue;
            }
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(l);
                System.Text.Json.JsonElement r = doc.RootElement;
                if (r.TryGetProperty("data", out var data) && r.TryGetProperty("pos", out var pos) && pos.TryGetProperty("line", out var line))
                {
                    messages.Add((line.GetInt32() - 1, data.GetString() ?? ""));
                }
            }
            catch (System.Text.Json.JsonException)
            {
            }
        }
        return Parse(messages, lines);
    }

    /// <summary>Timings from trace messages, each at the 0-based line Lean reported it.</summary>
    public static IReadOnlyList<DeclarationTiming> Parse(IEnumerable<(int Line, string Text)> messages, IReadOnlyList<string> lines)
    {
        var byLine = new Dictionary<int, (double Total, string? Hot, double HotSeconds)>();
        foreach ((int line, string text) in messages)
        {
            string[] ml = text.Replace("\r", "", StringComparison.Ordinal).Split('\n');
            Match top = TraceLine().Match(ml[0]);
            if (!top.Success || line < 0)
            {
                continue;
            }
            double total = double.Parse(top.Groups["s"].Value, CultureInfo.InvariantCulture);
            (string? hot, double hotS) = HotSpot(ml, total);
            // Some work is reported where it happened (the kernel checking a proof, at its tactic): it belongs
            // to the declaration around it.
            int owner = OwnerOf(lines, line);
            (double t, string? h, double hs) = byLine.GetValueOrDefault(owner);
            byLine[owner] = (t + total, hotS > hs ? hot : h, Math.Max(hs, hotS));
        }
        return byLine
            .Select(kv => new DeclarationTiming(kv.Key, NameAt(lines, kv.Key), kv.Value.Total, kv.Value.Hot, kv.Value.HotSeconds))
            .OrderByDescending(t => t.Seconds)
            .ToList();
    }

    /// <summary>
    /// The innermost step that still accounts for most of the time: follow the slowest child down while it holds
    /// at least 40% of the whole, skipping the wrappers that only restate the declaration.
    /// </summary>
    private static (string? What, double Seconds) HotSpot(string[] lines, double total)
    {
        var entries = new List<(int Depth, double Seconds, string What)>();
        foreach (string l in lines.Skip(1))
        {
            Match m = TraceLine().Match(l);
            if (m.Success)
            {
                entries.Add((m.Groups["indent"].Value.Length, double.Parse(m.Groups["s"].Value, CultureInfo.InvariantCulture), m.Groups["what"].Value.Trim()));
            }
        }
        (string? What, double Seconds) best = (null, 0);
        int bestDepth = -1;
        foreach (var e in entries)
        {
            if (e.Seconds < total * 0.4 || e.What.Length == 0 || e.What.StartsWith("expected type", StringComparison.Ordinal))
            {
                continue;
            }
            if (e.Depth > bestDepth)
            {
                best = (e.What.Length > 80 ? e.What[..80] + "…" : e.What, e.Seconds);
                bestDepth = e.Depth;
            }
        }
        return best;
    }

    /// <summary>The line of the top-level command containing <paramref name="line"/>: the nearest one at column 0.</summary>
    private static int OwnerOf(IReadOnlyList<string> lines, int line)
    {
        for (int i = Math.Min(line, lines.Count - 1); i >= 0; i--)
        {
            string l = lines[i];
            if (l.Length > 0 && !char.IsWhiteSpace(l[0]) && !l.StartsWith("--", StringComparison.Ordinal))
            {
                return i;
            }
        }
        return line;
    }

    private static string NameAt(IReadOnlyList<string> lines, int line)
    {
        if (line < 0 || line >= lines.Count)
        {
            return "";
        }
        string s = lines[line].Trim();
        return s.Length > 70 ? s[..70] + "…" : s;
    }

    /// <summary>
    /// Profile a file's text with the <c>lean</c> command line (a copy in the project's mirror, so unsaved text is
    /// what is measured). Proofs are elaborated one after another there, so each time is that declaration's own.
    /// </summary>
    public static async Task<(IReadOnlyList<DeclarationTiming> Timings, string? Error)> RunAsync(LeanProject project, string sourcePath, string text, CancellationToken ct = default)
    {
        string file = await LeanCli.MirrorAsync(project, sourcePath, text, "profile", ct).ConfigureAwait(false);
        string threshold = ((int)(Threshold * 1000)).ToString(CultureInfo.InvariantCulture);
        ProcessResult r = await LeanCli.RunAsync(project, ["--json", "-Dtrace.profiler=true", "-Dtrace.profiler.threshold=" + threshold, file], ct).ConfigureAwait(false);
        IReadOnlyList<DeclarationTiming> timings = Parse(r.Output, text.Split('\n'));
        if (timings.Count == 0 && !r.Success)
        {
            string err = string.Join("\n", r.Output.Split('\n').Where(l => !l.StartsWith('{')).Take(20)).Trim();
            return ([], err.Length > 0 ? err : "Lean could not check this file (build its imports first).");
        }
        return (timings, null);
    }
}

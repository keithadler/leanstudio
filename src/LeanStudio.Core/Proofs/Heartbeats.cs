using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using LeanStudio.Core.Processes;
using LeanStudio.Core.Projects;
using LeanStudio.Core.Workflow;

namespace LeanStudio.Core.Proofs;

/// <summary>How many heartbeats a declaration took to elaborate, in <c>maxHeartbeats</c>' units (thousands).</summary>
/// <param name="Line">The 0-based line the declaration starts on (its doc comment or attributes, if any).</param>
/// <param name="Declaration">The declaration's first line, as written.</param>
/// <param name="Heartbeats">Heartbeats used, in the units <c>set_option maxHeartbeats</c> takes (the default limit is 200000).</param>
public sealed record DeclarationHeartbeats(int Line, string Declaration, long Heartbeats)
{
    /// <summary>Lean's default limit per declaration.</summary>
    public const long DefaultLimit = 200_000;

    /// <summary>How much of the default limit it uses, 0 to 1 and beyond.</summary>
    public double OfLimit => Heartbeats / (double)DefaultLimit;
}

/// <summary>
/// Heartbeats per declaration (Lean ▸ Count Heartbeats): what <c>maxHeartbeats</c> limits, so a proof close to the
/// limit can be seen before it fails on someone else's machine. Core Lean has no counter (Mathlib's
/// <c>#count_heartbeats in</c> needs Mathlib), so a copy of the file gets a tiny one: each top-level declaration is
/// elaborated inside <c>#leanstudio_heartbeats</c>, which reads Lean's heartbeat counter before and after.
/// </summary>
public static partial class Heartbeats
{
    private const string Counter = """
        open Lean Elab Command in
        elab "#leanstudio_heartbeats " cmd:command : command => do
          let start ← IO.getNumHeartbeats
          elabCommand cmd
          logInfo m!"leanstudio-heartbeats {((← IO.getNumHeartbeats) - start) / 1000}"
        """;

    [GeneratedRegex(@"^(?:@\[[^\]]*\]\s*)*(?:(?:private|protected|public|noncomputable|partial|unsafe|nonrec)\s+)*(?:theorem|lemma|def|abbrev|instance|example|structure|inductive|class|opaque|axiom)\b")]
    private static partial Regex DeclarationStart();

    /// <summary>
    /// The copy of <paramref name="text"/> that counts: <c>import Lean</c> first, the counter after the imports,
    /// and each top-level declaration (outside <c>mutual</c> blocks) wrapped. Also returns the 0-based line after
    /// which lines were added, and how many, to map Lean's positions back.
    /// </summary>
    public static (string Text, int InsertedAfter, int Inserted) Instrument(string text)
    {
        var lines = text.Split('\n').ToList();
        // Where each counted declaration starts: its keyword line, or the doc comment or attributes above it.
        var starts = new List<int>();
        int mutualDepth = 0;
        for (int i = 0; i < lines.Count; i++)
        {
            string l = lines[i].TrimEnd('\r');
            if (l.StartsWith("mutual", StringComparison.Ordinal))
            {
                mutualDepth++;
            }
            else if (mutualDepth > 0 && l.StartsWith("end", StringComparison.Ordinal) && l.Trim() == "end")
            {
                mutualDepth--;
            }
            if (mutualDepth > 0 || !DeclarationStart().IsMatch(l))
            {
                continue;
            }
            int s = i;
            while (s > 0)
            {
                string above = lines[s - 1].TrimEnd('\r');
                if (above.StartsWith("@[", StringComparison.Ordinal))
                {
                    s--;
                }
                else if (above.TrimEnd().EndsWith("-/", StringComparison.Ordinal))
                {
                    int open = s - 1;
                    while (open >= 0 && !lines[open].Contains("/-", StringComparison.Ordinal))
                    {
                        open--;
                    }
                    if (open >= 0 && lines[open].TrimStart().StartsWith("/--", StringComparison.Ordinal))
                    {
                        s = open; // a doc comment belongs to the declaration
                    }
                    break;
                }
                else
                {
                    break;
                }
            }
            starts.Add(s);
        }
        foreach (int s in starts)
        {
            lines[s] = "#leanstudio_heartbeats " + lines[s];
        }
        int lastImport = lines.FindLastIndex(l => l.TrimStart().StartsWith("import ", StringComparison.Ordinal));
        int headerEnd = lastImport >= 0 ? lastImport : lines.FindLastIndex(l => l.Trim() is "module" or "prelude");
        string[] counter = Counter.Split('\n');
        lines.InsertRange(headerEnd + 1, ["", .. counter, ""]);
        lines.Insert(0, "import Lean");
        // Lines 0..headerEnd move down by one; everything after moves down by 1 + the counter.
        return (string.Join('\n', lines), headerEnd, counter.Length + 2);
    }

    /// <summary>
    /// A warning for the heaviest declaration past half of Lean's default limit (a small change could push it over),
    /// or null when every one is below half.
    /// </summary>
    public static string? Warning(IEnumerable<DeclarationHeartbeats> counts) =>
        counts.Where(c => c.OfLimit >= 0.5).MaxBy(c => c.Heartbeats) is { } heavy
            ? string.Create(CultureInfo.InvariantCulture, $"line {heavy.Line + 1} uses {heavy.OfLimit * 100:0}% of the default maxHeartbeats ({DeclarationHeartbeats.DefaultLimit:N0}): a small change could push it over.")
            : null;

    /// <summary>Count the heartbeats of each top-level declaration of <paramref name="text"/> (the contents of <paramref name="sourcePath"/>).</summary>
    public static async Task<(IReadOnlyList<DeclarationHeartbeats> Counts, string? Error)> RunAsync(LeanProject project, string sourcePath, string text, CancellationToken ct = default)
    {
        (string instrumented, int after, int inserted) = Instrument(text);
        string file = await LeanCli.MirrorAsync(project, sourcePath, instrumented, "heartbeats", ct).ConfigureAwait(false);
        ProcessResult r = await LeanCli.RunAsync(project, ["--json", file], ct).ConfigureAwait(false);
        IReadOnlyList<DeclarationHeartbeats> counts = Parse(r.Output, text.Split('\n'), after, inserted);
        if (counts.Count == 0 && !r.Success)
        {
            string err = string.Join("\n", r.Output.Split('\n').Where(l => !l.StartsWith('{')).Take(20)).Trim();
            return ([], err.Length > 0 ? err : "Lean could not check this file (build its imports first).");
        }
        return (counts, null);
    }

    /// <summary>Read <c>lean --json</c>'s messages for the counter's reports, at the lines of the original text.</summary>
    public static IReadOnlyList<DeclarationHeartbeats> Parse(string jsonLines, IReadOnlyList<string> original, int insertedAfter, int inserted)
    {
        var list = new List<DeclarationHeartbeats>();
        foreach (string line in jsonLines.Split('\n'))
        {
            if (!line.StartsWith('{') || !line.Contains("leanstudio-heartbeats", StringComparison.Ordinal))
            {
                continue;
            }
            using JsonDocument doc = JsonDocument.Parse(line);
            JsonElement root = doc.RootElement;
            string data = root.TryGetProperty("data", out JsonElement d) ? d.GetString() ?? "" : "";
            Match m = Regex.Match(data, @"leanstudio-heartbeats (\d+)");
            if (!m.Success || !root.TryGetProperty("pos", out JsonElement pos))
            {
                continue;
            }
            int shown = pos.GetProperty("line").GetInt32() - 1; // Lean counts lines from 1
            int at = shown - 1; // the added `import Lean`
            if (at > insertedAfter)
            {
                at -= inserted;
            }
            if (at < 0 || at >= original.Count)
            {
                continue;
            }
            list.Add(new DeclarationHeartbeats(at, original[at].TrimEnd('\r'), long.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)));
        }
        return list.OrderByDescending(h => h.Heartbeats).ToList();
    }
}

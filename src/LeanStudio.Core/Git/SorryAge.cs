using System.Globalization;
using LeanStudio.Core.Workflow;

namespace LeanStudio.Core.Git;

/// <summary>A <c>sorry</c> and how long it has stood there.</summary>
/// <param name="Path">The file.</param>
/// <param name="Line">0-based line of the <c>sorry</c>.</param>
/// <param name="Declaration">The declaration it is in, when known.</param>
/// <param name="Author">Who wrote the line.</param>
/// <param name="Date">When the line was written (its author date), or null when it is not committed yet.</param>
/// <param name="DaysOld">Days from <paramref name="Date"/> to the day asked; 0 when not committed.</param>
/// <param name="Commit">The commit that wrote the line.</param>
/// <param name="Summary">That commit's first line.</param>
public sealed record SorryAgeEntry(string Path, int Line, string? Declaration, string Author, DateOnly? Date, int DaysOld, string Commit, string Summary);

/// <summary>One line of a <c>git blame</c>: who wrote it, when, and in which commit.</summary>
/// <param name="Line">1-based line of the file as it is now.</param>
/// <param name="Commit">The commit hash (all zeros for a line not committed yet).</param>
/// <param name="Author">Who wrote it.</param>
/// <param name="Time">When, or null when not committed.</param>
/// <param name="Summary">The commit's first line.</param>
public sealed record BlamedLine(int Line, string Commit, string Author, DateTimeOffset? Time, string Summary);

/// <summary>
/// In a project with thousands of <c>sorry</c>s, the ones that have stood longest are the ones nobody wants: found from
/// <c>git blame</c> of each file that has any, the oldest first. A <c>sorry</c> that has sat for a year is either hard,
/// forgotten, or waiting on something: worth a person's look.
/// </summary>
public static class SorryAge
{
    /// <summary>The lines of the output of <c>git blame --line-porcelain</c>.</summary>
    public static IReadOnlyList<BlamedLine> ParseBlame(string porcelain)
    {
        var lines = new List<BlamedLine>();
        string commit = "", author = "", summary = "";
        long time = 0;
        int final = 0;
        foreach (string raw in porcelain.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            if (line.StartsWith('\t'))
            {
                bool uncommitted = commit.Length > 0 && commit.All(c => c == '0');
                lines.Add(new BlamedLine(final, commit, author, uncommitted || time == 0 ? null : DateTimeOffset.FromUnixTimeSeconds(time), summary));
                continue;
            }
            string[] parts = line.Split(' ', 4);
            if (parts.Length >= 3 && parts[0].Length == 40 && parts[0].All(Uri.IsHexDigit) && int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out int f))
            {
                commit = parts[0];
                final = f;
            }
            else if (line.StartsWith("author ", StringComparison.Ordinal))
            {
                author = line[7..];
            }
            else if (line.StartsWith("author-time ", StringComparison.Ordinal) && long.TryParse(line[12..], NumberStyles.None, CultureInfo.InvariantCulture, out long t))
            {
                time = t;
            }
            else if (line.StartsWith("summary ", StringComparison.Ordinal))
            {
                summary = line[8..];
            }
        }
        return lines;
    }

    /// <summary>
    /// The age of each of <paramref name="sorries"/> (the <c>sorry</c> markers of the project) in <paramref name="repo"/>, oldest
    /// first. A file git cannot blame (not tracked) is left out.
    /// </summary>
    public static async Task<IReadOnlyList<SorryAgeEntry>> ReadAsync(GitRepository repo, IEnumerable<Marker> sorries, DateOnly today, CancellationToken ct = default)
    {
        var entries = new List<SorryAgeEntry>();
        foreach (IGrouping<string, Marker> file in sorries.Where(m => m.Kind != MarkerKind.Todo).GroupBy(m => m.Path, StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            string relative = Path.GetRelativePath(repo.Root, file.Key).Replace('\\', '/');
            Processes.ProcessResult blame = await repo.RunAsync(["blame", "--line-porcelain", "--", relative], ct: ct).ConfigureAwait(false);
            if (!blame.Success)
            {
                continue;
            }
            Dictionary<int, BlamedLine> byLine = ParseBlame(blame.Output).GroupBy(b => b.Line).ToDictionary(g => g.Key, g => g.First());
            foreach (Marker m in file)
            {
                if (byLine.TryGetValue(m.Line + 1, out BlamedLine? b))
                {
                    DateOnly? date = b.Time is { } t ? DateOnly.FromDateTime(t.UtcDateTime) : null;
                    entries.Add(new SorryAgeEntry(m.Path, m.Line, m.Declaration, b.Author, date, date is { } d ? Math.Max(0, today.DayNumber - d.DayNumber) : 0, b.Commit, b.Summary));
                }
            }
        }
        return [.. entries.OrderByDescending(e => e.DaysOld).ThenBy(e => e.Path, StringComparer.Ordinal).ThenBy(e => e.Line)];
    }
}

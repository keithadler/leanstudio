using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LeanStudio.Core.Workflow;

/// <summary>A theorem whose statement is held fixed.</summary>
/// <param name="Name">The theorem's name, as written.</param>
/// <param name="Statement">Its statement when it was locked.</param>
/// <param name="Hash">A hash of that statement with the names of bound variables and the spacing left out.</param>
public sealed record LockedStatement(string Name, string Statement, string Hash);

/// <summary>What became of a locked statement.</summary>
public enum LockStatus
{
    /// <summary>The same statement (up to the names of bound variables and spacing).</summary>
    Same,

    /// <summary>The theorem is there, but says something else.</summary>
    Changed,

    /// <summary>No theorem of that name is left.</summary>
    Missing,
}

/// <summary>The result of checking one locked statement.</summary>
/// <param name="Name">The theorem.</param>
/// <param name="Status">What became of it.</param>
/// <param name="Was">The statement as locked.</param>
/// <param name="Now">The statement now, when it is still there.</param>
public sealed record LockResult(string Name, LockStatus Status, string Was, string? Now);

/// <summary>
/// The statement of the main theorem is the one thing a long formalization must never change by accident: a refactor that
/// weakens it a little still compiles, still verifies, and proves the wrong thing. Locks the statements that matter (kept in
/// <c>.leanstudio/statement-locks.json</c>, to be committed) and says at once if one has changed, however the proof or the
/// names of its variables did.
/// </summary>
public static class StatementLock
{
    /// <summary>Where the locks of the project at <paramref name="root"/> are kept.</summary>
    public static string FileFor(string root) => Path.Combine(root, ".leanstudio", "statement-locks.json");

    /// <summary>The hash of <paramref name="statement"/> with the names of bound variables and the spacing left out.</summary>
    public static string HashOf(string statement) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(DuplicateStatements.Normalize(statement)))).ToLowerInvariant()[..16];

    /// <summary><paramref name="locks"/> with <paramref name="theorem"/> locked (replacing its earlier lock, if any), by name order.</summary>
    public static IReadOnlyList<LockedStatement> Lock(IReadOnlyList<LockedStatement> locks, TheoremStatement theorem) =>
        [.. locks.Where(l => l.Name != theorem.Name).Append(new LockedStatement(theorem.Name, theorem.Statement, HashOf(theorem.Statement))).OrderBy(l => l.Name, StringComparer.Ordinal)];

    /// <summary>What became of each of <paramref name="locks"/>, given the theorems <paramref name="current"/> of the project.</summary>
    public static IReadOnlyList<LockResult> Check(IReadOnlyList<LockedStatement> locks, IEnumerable<TheoremStatement> current)
    {
        Dictionary<string, TheoremStatement> byName = [];
        foreach (TheoremStatement t in current)
        {
            byName.TryAdd(t.Name, t);
        }
        return [.. locks.Select(l => !byName.TryGetValue(l.Name, out TheoremStatement? now) ? new LockResult(l.Name, LockStatus.Missing, l.Statement, null)
            : HashOf(now.Statement) == l.Hash ? new LockResult(l.Name, LockStatus.Same, l.Statement, now.Statement)
            : new LockResult(l.Name, LockStatus.Changed, l.Statement, now.Statement))];
    }

    /// <summary>The locks in the file of the project at <paramref name="root"/>; none when there is no file or it is not readable.</summary>
    public static IReadOnlyList<LockedStatement> Read(string root)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(FileFor(root)));
            return [.. doc.RootElement.GetProperty("locks").EnumerateArray()
                .Select(e => new LockedStatement(e.GetProperty("name").GetString()!, e.GetProperty("statement").GetString()!, e.GetProperty("hash").GetString()!))];
        }
        catch (Exception e) when (e is IOException or JsonException or KeyNotFoundException or InvalidOperationException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>Write <paramref name="locks"/> to the project's file, creating its folder.</summary>
    public static void Write(string root, IReadOnlyList<LockedStatement> locks)
    {
        string file = FileFor(root);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, JsonSerializer.Serialize(new { locks = locks.Select(l => new { name = l.Name, statement = l.Statement, hash = l.Hash }) },
            new JsonSerializerOptions { WriteIndented = true }) + "\n");
    }
}

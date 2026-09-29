namespace LeanStudio.Core.Editing;

/// <summary>What a row of a side-by-side diff is.</summary>
public enum DiffKind
{
    /// <summary>The same on both sides.</summary>
    Same,

    /// <summary>Only on the left: removed.</summary>
    Removed,

    /// <summary>Only on the right: added.</summary>
    Added,

    /// <summary>On both sides, but different: a line changed.</summary>
    Changed,
}

/// <summary>One row of a side-by-side diff: a line from each side (or none), by 0-based line number.</summary>
/// <param name="Kind">What changed.</param>
/// <param name="Left">The left side's line, or null when the row has none there.</param>
/// <param name="Right">The right side's line, or null.</param>
public sealed record DiffRow(DiffKind Kind, int? Left, int? Right);

/// <summary>
/// A line diff of two texts (Myers' algorithm, in linear space, so large files with many changes stay cheap), laid
/// out as rows for two panes side by side: equal lines level with each other, a run of removed lines beside the run
/// of added lines that replaced them (as changed lines), and blank space on the other side for the rest.
/// </summary>
public static class TextDiff
{
    /// <summary>The rows of a side-by-side diff of <paramref name="left"/> and <paramref name="right"/>.</summary>
    public static IReadOnlyList<DiffRow> SideBySide(string left, string right) =>
        SideBySide(Lines(left), Lines(right));

    /// <summary>The rows of a side-by-side diff of two lists of lines.</summary>
    public static IReadOnlyList<DiffRow> SideBySide(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        var rows = new List<DiffRow>();
        var removed = new List<int>();
        var added = new List<int>();
        void Flush()
        {
            int both = Math.Min(removed.Count, added.Count);
            for (int k = 0; k < both; k++)
            {
                rows.Add(new DiffRow(DiffKind.Changed, removed[k], added[k]));
            }
            rows.AddRange(removed.Skip(both).Select(l => new DiffRow(DiffKind.Removed, l, null)));
            rows.AddRange(added.Skip(both).Select(r => new DiffRow(DiffKind.Added, null, r)));
            removed.Clear();
            added.Clear();
        }
        foreach ((char op, int i, int j) in Edits(a, b))
        {
            switch (op)
            {
                case '=':
                    Flush();
                    rows.Add(new DiffRow(DiffKind.Same, i, j));
                    break;
                case '-':
                    removed.Add(i);
                    break;
                default:
                    added.Add(j);
                    break;
            }
        }
        Flush();
        return rows;
    }

    /// <summary>How many lines were added and removed (a changed line counts as one of each).</summary>
    public static (int Added, int Removed) Count(IReadOnlyList<DiffRow> rows) =>
        (rows.Count(r => r.Kind is DiffKind.Added or DiffKind.Changed), rows.Count(r => r.Kind is DiffKind.Removed or DiffKind.Changed));

    /// <summary>A text's lines, without their line endings (none for an empty text).</summary>
    public static string[] Lines(string text)
    {
        if (text.Length == 0)
        {
            return [];
        }
        string[] lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        return lines.Length > 1 && lines[^1].Length == 0 ? lines[..^1] : lines;
    }

    /// <summary>
    /// The shortest edit script from <paramref name="a"/> to <paramref name="b"/>, in order: <c>=</c> for a line in
    /// both (at <c>i</c> in a and <c>j</c> in b), <c>-</c> for a line of a removed, <c>+</c> for a line of b added.
    /// </summary>
    public static IReadOnlyList<(char Op, int I, int J)> Edits(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        // Lines as numbers, so comparing them is cheap.
        var ids = new Dictionary<string, int>(StringComparer.Ordinal);
        int[] x = a.Select(l => ids.TryGetValue(l, out int id) ? id : ids[l] = ids.Count).ToArray();
        int[] y = b.Select(l => ids.TryGetValue(l, out int id) ? id : ids[l] = ids.Count).ToArray();
        var ops = new List<(char, int, int)>();
        int max = x.Length + y.Length + 1;
        var vf = new int[2 * max + 2];
        var vb = new int[2 * max + 2];
        Diff(x, 0, x.Length, y, 0, y.Length, vf, vb, ops);
        return ops;
    }

    private static void Diff(int[] a, int a0, int a1, int[] b, int b0, int b1, int[] vf, int[] vb, List<(char, int, int)> ops)
    {
        // Equal ends first: most diffs are a small change in a long text.
        while (a0 < a1 && b0 < b1 && a[a0] == b[b0])
        {
            ops.Add(('=', a0++, b0++));
        }
        var tail = new List<(char, int, int)>();
        while (a1 > a0 && b1 > b0 && a[a1 - 1] == b[b1 - 1])
        {
            tail.Add(('=', --a1, --b1));
        }
        if (a0 == a1)
        {
            for (int j = b0; j < b1; j++)
            {
                ops.Add(('+', a0, j));
            }
        }
        else if (b0 == b1)
        {
            for (int i = a0; i < a1; i++)
            {
                ops.Add(('-', i, b0));
            }
        }
        else
        {
            (int x, int y, int u, int v) = MiddleSnake(a, a0, a1, b, b0, b1, vf, vb);
            Diff(a, a0, x, b, b0, y, vf, vb, ops);
            for (int i = x, j = y; i < u; i++, j++)
            {
                ops.Add(('=', i, j));
            }
            Diff(a, u, a1, b, v, b1, vf, vb, ops);
        }
        tail.Reverse();
        ops.AddRange(tail);
    }

    /// <summary>Myers' middle snake: where an optimal path crosses the middle of the edit graph, as (x, y) to (u, v).</summary>
    private static (int X, int Y, int U, int V) MiddleSnake(int[] a, int a0, int a1, int[] b, int b0, int b1, int[] vf, int[] vb)
    {
        int n = a1 - a0, m = b1 - b0, delta = n - m, offset = vf.Length / 2;
        bool odd = (delta & 1) != 0;
        int dmax = (n + m + 1) / 2;
        vf[offset + 1] = 0;
        vb[offset + 1] = 0;
        for (int d = 0; d <= dmax; d++)
        {
            for (int k = -d; k <= d; k += 2)
            {
                int x = k == -d || (k != d && vf[offset + k - 1] < vf[offset + k + 1]) ? vf[offset + k + 1] : vf[offset + k - 1] + 1;
                int y = x - k, sx = x;
                while (x < n && y < m && a[a0 + x] == b[b0 + y])
                {
                    x++;
                    y++;
                }
                vf[offset + k] = x;
                if (odd && k >= delta - (d - 1) && k <= delta + (d - 1) && x + vb[offset + delta - k] >= n)
                {
                    return (a0 + sx, b0 + sx - k, a0 + x, b0 + y);
                }
            }
            for (int k = -d; k <= d; k += 2)
            {
                int x = k == -d || (k != d && vb[offset + k - 1] < vb[offset + k + 1]) ? vb[offset + k + 1] : vb[offset + k - 1] + 1;
                int y = x - k, sx = x;
                while (x < n && y < m && a[a1 - 1 - x] == b[b1 - 1 - y])
                {
                    x++;
                    y++;
                }
                vb[offset + k] = x;
                int kf = delta - k;
                if (!odd && kf >= -d && kf <= d && x + vf[offset + kf] >= n)
                {
                    return (a1 - x, b1 - y, a1 - sx, b1 - (sx - k));
                }
            }
        }
        return (a0, b0, a0, b0); // unreachable: the paths always meet by dmax
    }
}

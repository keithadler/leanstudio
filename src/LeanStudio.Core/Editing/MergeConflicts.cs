namespace LeanStudio.Core.Editing;

/// <summary>How to settle one merge conflict.</summary>
public enum ConflictChoice
{
    /// <summary>Keep this branch's side (between <c>&lt;&lt;&lt;&lt;&lt;&lt;&lt;</c> and <c>=======</c>).</summary>
    Mine,

    /// <summary>Take the other branch's side (between <c>=======</c> and <c>&gt;&gt;&gt;&gt;&gt;&gt;&gt;</c>).</summary>
    Theirs,

    /// <summary>Keep both, this branch's first.</summary>
    Both,
}

/// <summary>
/// One merge conflict git left in a file, by 0-based line: the <c>&lt;&lt;&lt;&lt;&lt;&lt;&lt;</c> line, the
/// <c>|||||||</c> line of the common ancestor when git was asked for it (diff3), the <c>=======</c> line and the
/// <c>&gt;&gt;&gt;&gt;&gt;&gt;&gt;</c> line, with the labels git put on them.
/// </summary>
/// <param name="Start">The <c>&lt;&lt;&lt;&lt;&lt;&lt;&lt;</c> line.</param>
/// <param name="Base">The <c>|||||||</c> line, or -1 without one.</param>
/// <param name="Separator">The <c>=======</c> line.</param>
/// <param name="End">The <c>&gt;&gt;&gt;&gt;&gt;&gt;&gt;</c> line.</param>
/// <param name="MineLabel">What git called this side (<c>HEAD</c>, a branch).</param>
/// <param name="TheirsLabel">What git called the other side (a branch, a commit).</param>
public sealed record ConflictBlock(int Start, int Base, int Separator, int End, string MineLabel, string TheirsLabel)
{
    /// <summary>The lines of this branch's side.</summary>
    public (int From, int To) Mine => (Start + 1, Base >= 0 ? Base : Separator);

    /// <summary>The lines of the other side.</summary>
    public (int From, int To) Theirs => (Separator + 1, End);
}

/// <summary>Merge conflicts in a file's text: finding them, and settling one, the way a person would by hand.</summary>
public static class MergeConflicts
{
    /// <summary>Every complete conflict in <paramref name="text"/>, in order. Half-written or nested markers are ignored.</summary>
    public static IReadOnlyList<ConflictBlock> Find(string text)
    {
        string[] lines = text.Split('\n');
        var blocks = new List<ConflictBlock>();
        for (int i = 0; i < lines.Length; i++)
        {
            if (!IsMarker(lines[i], '<'))
            {
                continue;
            }
            int b = -1, sep = -1, end = -1;
            for (int j = i + 1; j < lines.Length; j++)
            {
                if (IsMarker(lines[j], '<'))
                {
                    break; // another start before this one ended: not a conflict git wrote
                }
                if (sep < 0 && b < 0 && IsMarker(lines[j], '|'))
                {
                    b = j;
                }
                else if (sep < 0 && IsMarker(lines[j], '='))
                {
                    sep = j;
                }
                else if (sep >= 0 && IsMarker(lines[j], '>'))
                {
                    end = j;
                    break;
                }
            }
            if (sep >= 0 && end >= 0)
            {
                blocks.Add(new ConflictBlock(i, b, sep, end, Label(lines[i]), Label(lines[end])));
                i = end;
            }
        }
        return blocks;
    }

    /// <summary>
    /// <paramref name="text"/> with the conflict <paramref name="block"/> settled by <paramref name="choice"/>: its
    /// marker lines (and the ancestor's side) removed, and one side or both kept.
    /// </summary>
    public static string Resolve(string text, ConflictBlock block, ConflictChoice choice)
    {
        string[] lines = text.Split('\n');
        IEnumerable<string> Take((int From, int To) r) => lines[r.From..r.To];
        IEnumerable<string> kept = choice switch
        {
            ConflictChoice.Mine => Take(block.Mine),
            ConflictChoice.Theirs => Take(block.Theirs),
            _ => Take(block.Mine).Concat(Take(block.Theirs)),
        };
        return string.Join('\n', lines[..block.Start].Concat(kept).Concat(lines[(block.End + 1)..]));
    }

    /// <summary>A marker line: seven of <paramref name="c"/> at the start, then the end of the line or a space.</summary>
    private static bool IsMarker(string line, char c)
    {
        string l = line.TrimEnd('\r');
        if (l.Length < 7 || l.AsSpan(0, 7).IndexOfAnyExcept(c) >= 0)
        {
            return false;
        }
        return l.Length == 7 || l[7] == ' ';
    }

    private static string Label(string line)
    {
        string l = line.TrimEnd('\r');
        return l.Length > 8 ? l[8..].Trim() : "";
    }
}

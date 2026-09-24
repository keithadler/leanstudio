namespace LeanStudio.Core.Editing;

/// <summary>A cursor, with the selection it extends: from <see cref="Anchor"/> to <see cref="Position"/> (offsets).</summary>
/// <param name="Anchor">Where the selection starts (where the cursor was when selecting began).</param>
/// <param name="Position">Where the cursor is.</param>
public readonly record struct Cursor(int Anchor, int Position)
{
    /// <summary>A cursor with nothing selected.</summary>
    public static Cursor At(int offset) => new(offset, offset);

    /// <summary>The start of the selection.</summary>
    public int Start => Math.Min(Anchor, Position);

    /// <summary>The end of the selection.</summary>
    public int End => Math.Max(Anchor, Position);

    /// <summary>Whether nothing is selected.</summary>
    public bool IsEmpty => Anchor == Position;
}

/// <summary>One replacement in a text: <see cref="Length"/> characters at <see cref="Offset"/> become <see cref="Text"/>.</summary>
/// <param name="Offset">Where, in the text before any of the edits.</param>
/// <param name="Length">How many characters are replaced.</param>
/// <param name="Text">What replaces them.</param>
public readonly record struct Replacement(int Offset, int Length, string Text);

/// <summary>
/// Editing with several cursors at once, as a text operation: what each keystroke does at every cursor, and where
/// the cursors are after it. The editor applies the replacements (last first, so the offsets hold) as one undoable
/// change. Also finds where to add cursors: the next occurrence of the selection, every occurrence, the line above
/// or below.
/// </summary>
public static class MultiCursor
{
    /// <summary>The cursors in order, with overlapping or touching selections merged and duplicates dropped.</summary>
    public static IReadOnlyList<Cursor> Normalize(IEnumerable<Cursor> cursors)
    {
        var sorted = cursors.OrderBy(c => c.Start).ThenBy(c => c.End).ToList();
        var result = new List<Cursor>();
        foreach (Cursor c in sorted)
        {
            Cursor? prev = result.Count > 0 ? result[^1] : null;
            bool overlaps = prev is Cursor p1 && c.Start < p1.End;
            bool sameCursor = prev is Cursor p2 && c.IsEmpty && p2.IsEmpty && c.Start == p2.Start;
            bool touching = prev is Cursor p3 && !c.IsEmpty && !p3.IsEmpty && c.Start == p3.End;
            if (overlaps || sameCursor || touching)
            {
                Cursor last = result[^1];
                int start = Math.Min(last.Start, c.Start), end = Math.Max(last.End, c.End);
                result[^1] = last.Anchor <= last.Position ? new Cursor(start, end) : new Cursor(end, start);
                continue;
            }
            result.Add(c);
        }
        return result;
    }

    /// <summary>Typing <paramref name="input"/> at every cursor: it replaces each selection, and each cursor ends after what it typed.</summary>
    public static (IReadOnlyList<Replacement> Edits, IReadOnlyList<Cursor> Cursors) Type(IReadOnlyList<Cursor> cursors, string input) =>
        Apply(Normalize(cursors), c => new Replacement(c.Start, c.End - c.Start, input));

    /// <summary>Backspace at every cursor: a selection goes, else the character before the cursor.</summary>
    public static (IReadOnlyList<Replacement> Edits, IReadOnlyList<Cursor> Cursors) Backspace(string text, IReadOnlyList<Cursor> cursors) =>
        Apply(Normalize(cursors), c => !c.IsEmpty ? new Replacement(c.Start, c.End - c.Start, "")
            : c.Start == 0 ? new Replacement(0, 0, "")
            : new Replacement(c.Start - CharLengthBefore(text, c.Start), CharLengthBefore(text, c.Start), ""));

    /// <summary>Delete at every cursor: a selection goes, else the character after the cursor.</summary>
    public static (IReadOnlyList<Replacement> Edits, IReadOnlyList<Cursor> Cursors) Delete(string text, IReadOnlyList<Cursor> cursors) =>
        Apply(Normalize(cursors), c => !c.IsEmpty ? new Replacement(c.Start, c.End - c.Start, "")
            : c.Start >= text.Length ? new Replacement(c.Start, 0, "")
            : new Replacement(c.Start, CharLengthAfter(text, c.Start), ""));

    // A surrogate pair (𝔽, 𝕜 and other letters Lean uses) is one character to delete.
    private static int CharLengthBefore(string text, int offset) =>
        offset >= 2 && char.IsLowSurrogate(text[offset - 1]) && char.IsHighSurrogate(text[offset - 2]) ? 2 : 1;

    private static int CharLengthAfter(string text, int offset) =>
        offset + 1 < text.Length && char.IsHighSurrogate(text[offset]) && char.IsLowSurrogate(text[offset + 1]) ? 2 : 1;

    private static (IReadOnlyList<Replacement>, IReadOnlyList<Cursor>) Apply(IReadOnlyList<Cursor> cursors, Func<Cursor, Replacement> edit)
    {
        var edits = new List<Replacement>();
        var after = new List<Cursor>();
        int shift = 0;
        foreach (Cursor c in cursors)
        {
            Replacement r = edit(c);
            edits.Add(r);
            int end = r.Offset + shift + r.Text.Length;
            after.Add(Cursor.At(end));
            shift += r.Text.Length - r.Length;
        }
        edits.Reverse(); // last first: applying them in this order keeps every offset valid
        return (edits, after);
    }

    /// <summary>Apply <paramref name="edits"/> (last first, as the operations return them) to <paramref name="text"/>.</summary>
    public static string ApplyTo(string text, IReadOnlyList<Replacement> edits)
    {
        foreach (Replacement r in edits)
        {
            text = text[..r.Offset] + r.Text + text[(r.Offset + r.Length)..];
        }
        return text;
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c is '_' or '\'' or '.' or '!' or '?' || char.IsSurrogate(c);

    /// <summary>The identifier at <paramref name="offset"/> (Lean's: letters, digits, <c>_ ' . ! ?</c>), or null.</summary>
    public static Cursor? WordAt(string text, int offset)
    {
        int s = Math.Clamp(offset, 0, text.Length), e = s;
        while (s > 0 && IsWordChar(text[s - 1]))
        {
            s--;
        }
        while (e < text.Length && IsWordChar(text[e]))
        {
            e++;
        }
        // A name doesn't end in a dot: `h.` at the end of `exact h.` is `h`.
        while (e > s && text[e - 1] == '.')
        {
            e--;
        }
        return e > s ? new Cursor(s, e) : null;
    }

    /// <summary>
    /// Add the next occurrence (⌘D): with nothing selected, select the word at the last cursor; otherwise add a
    /// cursor on the next place the last selection's text appears, wrapping around. Returns the new cursors (the
    /// same ones when there is nothing more to add).
    /// </summary>
    public static IReadOnlyList<Cursor> AddNextOccurrence(string text, IReadOnlyList<Cursor> cursors)
    {
        if (cursors.Count == 0)
        {
            return cursors;
        }
        Cursor last = cursors[^1];
        if (last.IsEmpty)
        {
            return WordAt(text, last.Position) is Cursor w ? Normalize([.. cursors.Take(cursors.Count - 1), w]) : cursors;
        }
        string needle = text[last.Start..last.End];
        int from = cursors.Max(c => c.End);
        int found = text.IndexOf(needle, from, StringComparison.Ordinal);
        if (found < 0)
        {
            found = text.IndexOf(needle, StringComparison.Ordinal);
        }
        while (found >= 0 && cursors.Any(c => c.Start == found))
        {
            found = text.IndexOf(needle, found + 1, StringComparison.Ordinal);
        }
        return found < 0 ? cursors : [.. cursors, new Cursor(found, found + needle.Length)];
    }

    /// <summary>Every occurrence of the selection's text (or of the word at the cursor), each selected.</summary>
    public static IReadOnlyList<Cursor> AllOccurrences(string text, Cursor selection)
    {
        Cursor? what = selection.IsEmpty ? WordAt(text, selection.Position) : selection;
        if (what is not Cursor w)
        {
            return [selection];
        }
        string needle = text[w.Start..w.End];
        var all = new List<Cursor>();
        for (int i = text.IndexOf(needle, StringComparison.Ordinal); i >= 0; i = text.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
        {
            all.Add(new Cursor(i, i + needle.Length));
        }
        return all;
    }

    /// <summary>
    /// A cursor on the line above (<paramref name="down"/> false) or below the given one, at the same column (or the
    /// end of that line, if it is shorter). Null at the first or last line.
    /// </summary>
    public static Cursor? OnAdjacentLine(string text, Cursor cursor, bool down)
    {
        int lineStart = cursor.Position == 0 ? 0 : text.LastIndexOf('\n', cursor.Position - 1) + 1;
        int column = cursor.Position - lineStart;
        if (down)
        {
            int next = text.IndexOf('\n', cursor.Position);
            if (next < 0)
            {
                return null;
            }
            int nextEnd = text.IndexOf('\n', next + 1);
            nextEnd = nextEnd < 0 ? text.Length : nextEnd;
            return Cursor.At(Math.Min(next + 1 + column, nextEnd));
        }
        if (lineStart == 0)
        {
            return null;
        }
        int prevStart = lineStart == 1 ? 0 : text.LastIndexOf('\n', lineStart - 2) + 1;
        return Cursor.At(Math.Min(prevStart + column, lineStart - 1));
    }
}

namespace LeanStudio.Core.Editing;

/// <summary>What a marked-up span of a comment is.</summary>
public enum DocSpanKind
{
    /// <summary>Math between single dollar signs.</summary>
    InlineMath,
    /// <summary>Math between double dollar signs.</summary>
    DisplayMath,
    /// <summary><c>**bold**</c>.</summary>
    Bold,
    /// <summary><c>*italic*</c>.</summary>
    Italic,
}

/// <summary>
/// A span of a block comment an editor can draw instead of its source: <paramref name="Start"/> and
/// <paramref name="Length"/> cover the whole span, markers included; the content is between them.
/// </summary>
public readonly record struct DocSpan(int Start, int Length, DocSpanKind Kind, int ContentStart, int ContentLength)
{
    /// <summary>The offset just past the span.</summary>
    public int End => Start + Length;
}

/// <summary>
/// Finds the math and the emphasis inside the block comments of a Lean file (<c>/-- docstrings -/</c>,
/// <c>/-! module docs -/</c>, plain <c>/- -/</c>, nested ones too) so an editor can show them typeset, the way the docstring
/// would read in the documentation. Only spans that open and close on the same line are found, never ones in code,
/// strings, line comments or in backticks, and a dollar sign next to a space (<c>costs $5 or $6</c>) is not math.
/// </summary>
public static class DocMarkup
{
    /// <summary>The spans in <paramref name="text"/>, in order, none overlapping.</summary>
    public static IReadOnlyList<DocSpan> Find(string text)
    {
        var spans = new List<DocSpan>();
        int i = 0, depth = 0;
        while (i < text.Length)
        {
            char c = text[i];
            if (depth == 0)
            {
                if (c == '/' && Next(text, i) == '-')
                {
                    depth = 1;
                    i += 2;
                    continue;
                }
                if (c == '-' && Next(text, i) == '-')
                {
                    i = LineEnd(text, i);
                    continue;
                }
                if (c == '"')
                {
                    i = SkipString(text, i);
                    continue;
                }
                i++;
                continue;
            }
            // inside a block comment
            if (c == '/' && Next(text, i) == '-')
            {
                depth++;
                i += 2;
                continue;
            }
            if (c == '-' && Next(text, i) == '/')
            {
                depth--;
                i += 2;
                continue;
            }
            if (c == '`')
            {
                int close = text.IndexOf('`', i + 1);
                int eol = LineEnd(text, i);
                i = close > 0 && close < eol ? close + 1 : i + 1;
                continue;
            }
            if (c == '\\' )
            {
                i += 2; // an escaped dollar or star is text
                continue;
            }
            if (c == '$')
            {
                if (TryMath(text, i, out DocSpan m))
                {
                    spans.Add(m);
                    i = m.End;
                    continue;
                }
            }
            else if (c == '*' && TryEmphasis(text, i, out DocSpan e))
            {
                spans.Add(e);
                i = e.End;
                continue;
            }
            i++;
        }
        return spans;
    }

    private static char Next(string t, int i) => i + 1 < t.Length ? t[i + 1] : '\0';

    private static int LineEnd(string t, int i)
    {
        int n = t.IndexOf('\n', i);
        return n < 0 ? t.Length : n;
    }

    private static int SkipString(string t, int i)
    {
        i++;
        while (i < t.Length && t[i] != '"')
        {
            i += t[i] == '\\' ? 2 : 1;
        }
        return Math.Min(i + 1, t.Length);
    }

    private static bool TryMath(string t, int i, out DocSpan span)
    {
        span = default;
        bool display = Next(t, i) == '$';
        int open = i + (display ? 2 : 1);
        int eol = LineEnd(t, i);
        int close = -1;
        for (int j = open; j < eol; j++)
        {
            if (t[j] == '\\')
            {
                j++;
            }
            else if (t[j] == '$' && (!display || Next(t, j) == '$'))
            {
                close = j;
                break;
            }
        }
        if (close <= open)
        {
            return false;
        }
        if (!display && (char.IsWhiteSpace(t[open]) || char.IsWhiteSpace(t[close - 1])))
        {
            return false;
        }
        span = new DocSpan(i, close + (display ? 2 : 1) - i, display ? DocSpanKind.DisplayMath : DocSpanKind.InlineMath, open, close - open);
        return true;
    }

    private static bool TryEmphasis(string t, int i, out DocSpan span)
    {
        span = default;
        bool bold = Next(t, i) == '*';
        int mark = bold ? 2 : 1;
        int open = i + mark;
        int eol = LineEnd(t, i);
        if (open >= eol || char.IsWhiteSpace(t[open]) || t[open] == '*' || (i > 0 && char.IsLetterOrDigit(t[i - 1])))
        {
            return false;
        }
        for (int j = open + 1; j < eol; j++)
        {
            if (t[j] == '*' && !char.IsWhiteSpace(t[j - 1]) && (!bold || Next(t, j) == '*') && (bold || Next(t, j) != '*'))
            {
                int end = j + mark;
                if (end < t.Length && char.IsLetterOrDigit(t[end]))
                {
                    return false;
                }
                span = new DocSpan(i, end - i, bold ? DocSpanKind.Bold : DocSpanKind.Italic, open, j - open);
                return true;
            }
        }
        return false;
    }
}

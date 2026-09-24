namespace LeanStudio.Core.Editing;

/// <summary>Small facts about Lean text the editor needs as you type: brackets and indentation.</summary>
public static class LeanText
{
    /// <summary>Opening brackets and their closers, including Lean's anonymous-constructor and other Unicode pairs.</summary>
    public static IReadOnlyDictionary<char, char> Pairs { get; } = new Dictionary<char, char>
    {
        ['('] = ')', ['['] = ']', ['{'] = '}', ['⟨'] = '⟩', ['⦃'] = '⦄', ['⟦'] = '⟧', ['«'] = '»', ['‹'] = '›', ['⌊'] = '⌋', ['⌈'] = '⌉', ['⟪'] = '⟫',
    };

    private static readonly Dictionary<char, char> Closers = Pairs.ToDictionary(kv => kv.Value, kv => kv.Key);

    /// <summary>Whether <paramref name="c"/> is one of the opening brackets in <see cref="Pairs"/>.</summary>
    public static bool IsOpener(char c) => Pairs.ContainsKey(c);

    /// <summary>Whether <paramref name="c"/> is one of the closing brackets in <see cref="Pairs"/>.</summary>
    public static bool IsCloser(char c) => Closers.ContainsKey(c);

    /// <summary>
    /// Whether typing an opener before <paramref name="next"/> should also insert its closer: only where nothing
    /// that could belong inside follows (end of line, space, or another closer), as in most editors.
    /// </summary>
    public static bool ShouldAutoClose(char? next) => next is null || char.IsWhiteSpace(next.Value) || IsCloser(next.Value) || next is ',' or ';';

    /// <summary>
    /// The offset of the bracket matching the one at <paramref name="offset"/>, or -1. Skips brackets inside
    /// strings and comments, which is what makes a naive scan wrong in Lean files full of doc comments.
    /// </summary>
    public static int MatchingBracket(string text, int offset)
    {
        if (offset < 0 || offset >= text.Length)
        {
            return -1;
        }
        char c = text[offset];
        bool forward = IsOpener(c);
        if (!forward && !IsCloser(c))
        {
            return -1;
        }
        bool[] code = CodeMask(text);
        if (!code[offset])
        {
            return -1;
        }
        char open = forward ? c : Closers[c];
        char close = forward ? c == open ? Pairs[c] : c : c;
        int depth = 0;
        for (int i = offset; forward ? i < text.Length : i >= 0; i += forward ? 1 : -1)
        {
            if (!code[i])
            {
                continue;
            }
            if (text[i] == open)
            {
                depth += forward ? 1 : -1;
            }
            else if (text[i] == close)
            {
                depth += forward ? -1 : 1;
            }
            if (depth == 0)
            {
                return i;
            }
        }
        return -1;
    }

    /// <summary>
    /// For each character, whether it is code (not inside a string or comment). Handles <c>--</c> line comments, nested
    /// <c>/- -/</c> block comments and escapes in string literals; the delimiters themselves count as not code.
    /// </summary>
    public static bool[] CodeMask(string text)
    {
        var mask = new bool[text.Length];
        int blockDepth = 0;
        bool inString = false, inLine = false;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            char n = i + 1 < text.Length ? text[i + 1] : '\0';
            if (inLine)
            {
                if (c == '\n')
                {
                    inLine = false;
                }
                continue;
            }
            if (blockDepth > 0)
            {
                if (c == '/' && n == '-')
                {
                    blockDepth++;
                    i++;
                }
                else if (c == '-' && n == '/')
                {
                    blockDepth--;
                    i++;
                }
                continue;
            }
            if (inString)
            {
                if (c == '\\')
                {
                    i++;
                }
                else if (c == '"')
                {
                    inString = false;
                }
                continue;
            }
            if (c == '-' && n == '-')
            {
                inLine = true;
                continue;
            }
            if (c == '/' && n == '-')
            {
                blockDepth = 1;
                i++;
                continue;
            }
            if (c == '"')
            {
                inString = true;
                continue;
            }
            mask[i] = true;
        }
        return mask;
    }

    private static readonly string[] IndentingEndings = [" by", ":= by", "where", "=>", " do", " then", " else", ":=", "calc", "·", "fun", "with", "from", "←"];

    /// <summary>
    /// The indentation for the line after <paramref name="previous"/>: the same as it, plus two spaces when it
    /// opens a block (`:= by`, `where`, `=>`, `do`, …) the way Lean code is conventionally laid out.
    /// </summary>
    public static string IndentAfter(string previous)
    {
        string indent = previous[..(previous.Length - previous.TrimStart().Length)];
        bool stripped = false;
        string code = Proofs.ProofSteps.StripComments(previous, ref stripped).TrimEnd();
        if (code.Length == 0)
        {
            return indent;
        }
        bool opens = code == "by" || IndentingEndings.Any(e => code.EndsWith(e, StringComparison.Ordinal))
                     || code.EndsWith('(') || code.EndsWith('⟨') || code.EndsWith('[') || code.EndsWith('{');
        return opens ? indent + "  " : indent;
    }

    /// <summary>
    /// The block comments (<c>/- … -/</c>, doc and module comments too) that span more than one line, as 0-based
    /// first and last lines, for folding: Lean's server folds declarations and namespaces but not comments. Nested
    /// comments count as one; <c>/-</c> inside a string or after <c>--</c> is not a comment.
    /// </summary>
    public static IReadOnlyList<(int StartLine, int EndLine)> CommentFolds(string text)
    {
        var folds = new List<(int, int)>();
        int line = 0, depth = 0, start = 0;
        bool inString = false, lineComment = false;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            char next = i + 1 < text.Length ? text[i + 1] : '\0';
            if (c == '\n')
            {
                line++;
                lineComment = false;
                continue;
            }
            if (depth == 0)
            {
                if (lineComment)
                {
                    continue;
                }
                if (inString)
                {
                    if (c == '\\')
                    {
                        i++;
                    }
                    else if (c == '"')
                    {
                        inString = false;
                    }
                    continue;
                }
                if (c == '"')
                {
                    inString = true;
                }
                else if (c == '-' && next == '-')
                {
                    lineComment = true;
                }
                else if (c == '/' && next == '-')
                {
                    depth = 1;
                    start = line;
                    i++;
                }
                continue;
            }
            if (c == '/' && next == '-')
            {
                depth++;
                i++;
            }
            else if (c == '-' && next == '/')
            {
                depth--;
                i++;
                if (depth == 0 && line > start)
                {
                    folds.Add((start, line));
                }
            }
        }
        return folds;
    }
}

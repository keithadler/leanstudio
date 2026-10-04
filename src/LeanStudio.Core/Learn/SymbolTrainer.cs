using System.Text;
using System.Text.RegularExpressions;
using LeanStudio.Core.Editing;

namespace LeanStudio.Core.Learn;

/// <summary>One symbol to practise: what it is called, the abbreviation that types it, and the symbol.</summary>
/// <param name="Spoken">What it is called.</param>
/// <param name="Abbreviation">The word after the backslash.</param>
/// <param name="Symbol">The symbol it makes.</param>
public sealed record PracticeSymbol(string Spoken, string Abbreviation, string Symbol);

/// <summary>
/// Typing <c>∀</c> is the first wall a beginner hits: you type <c>\forall</c> and a space. A worksheet to practise in the
/// editor itself, where the real abbreviation input works, and a check that marks what was typed.
/// </summary>
public static class SymbolTrainer
{
    /// <summary>The symbols a beginner meets first, with the abbreviation for each (every one is checked against <see cref="Abbreviations"/>).</summary>
    public static IReadOnlyList<PracticeSymbol> Symbols { get; } = new (string, string)[]
    {
        ("for all", "forall"), ("there exists", "exists"), ("implies (an arrow)", "to"), ("and", "and"), ("or", "or"), ("not", "not"),
        ("if and only if", "iff"), ("natural numbers", "N"), ("integers", "Z"), ("real numbers", "R"), ("less than or equal", "le"),
        ("greater than or equal", "ge"), ("not equal", "ne"), ("is an element of", "in"), ("is a subset of", "sub"),
        ("alpha", "alpha"), ("beta", "beta"), ("lambda (a function)", "lambda"), ("times", "times"), ("infinity", "infty"),
    }.Select(x => new PracticeSymbol(x.Item1, x.Item2, Abbreviations.Lookup(x.Item2) ?? "?")).ToList();

    /// <summary>
    /// A worksheet of <paramref name="count"/> symbols picked by <paramref name="seed"/>: a Lean file in which each line names a
    /// symbol, shows what to type, and has an arrow to type it after.
    /// </summary>
    public static string Worksheet(int count = 8, int seed = 0)
    {
        int n = Math.Clamp(count, 1, Symbols.Count);
        int start = (int)((uint)seed % (uint)Symbols.Count);
        List<PracticeSymbol> pick = [.. Enumerable.Range(0, n).Select(i => Symbols[(start + i) % Symbols.Count])];
        var sb = new StringBuilder("/-\nSymbol practice\n\nType the symbol after each arrow. Type a backslash and the word, then a space:\n\\forall  then a space  turns into  ∀\n\nWhen you are done, run  Learn ▸ Check My Symbol Practice.\n\n");
        foreach (PracticeSymbol s in pick)
        {
            sb.Append(s.Spoken).Append("  (type \\").Append(s.Abbreviation).Append(")  →  \n");
        }
        return sb.Append("-/\n").ToString();
    }

    /// <summary>How the worksheet in <paramref name="text"/> was done: the right and wrong lines.</summary>
    /// <returns>The number right, the number of lines with an answer, and a description of each wrong one.</returns>
    public static (int Right, int Answered, IReadOnlyList<string> Wrong) Grade(string text)
    {
        int right = 0, answered = 0;
        var wrong = new List<string>();
        foreach (Match m in Regex.Matches(text, @"^(?<spoken>[^\n]+?)[ \t]+\(type \\(?<abbr>\S+?)\)[ \t]+→[ \t]*(?<typed>[^\n]*)$", RegexOptions.Multiline))
        {
            string typed = m.Groups["typed"].Value.Trim();
            if (typed.Length == 0)
            {
                continue;
            }
            answered++;
            string expected = Abbreviations.Lookup(m.Groups["abbr"].Value) ?? "?";
            if (typed == expected)
            {
                right++;
            }
            else
            {
                wrong.Add($"{m.Groups["spoken"].Value.Trim()}: you typed {typed}; it is {expected} (type \\{m.Groups["abbr"].Value} and a space)");
            }
        }
        return (right, answered, wrong);
    }
}

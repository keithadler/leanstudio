using System.Globalization;
using System.Text.RegularExpressions;

namespace LeanStudio.Core.Learn;

/// <summary>A surprise in Lean's arithmetic or syntax that a beginner is about to meet, and why.</summary>
/// <param name="Line">0-based line.</param>
/// <param name="Title">The surprise, in a few words.</param>
/// <param name="Explanation">Why it is so, and what to do instead.</param>
public sealed record Gotcha(int Line, string Title, string Explanation);

/// <summary>
/// The things that surprise someone who has programmed before: natural numbers do not go below zero, whole-number division
/// rounds down, <c>==</c> is not <c>=</c>. Found in the code of a file (the <c>#eval</c>s that would show them, and statements that
/// use the wrong equals sign), each with the reason, so the surprise teaches instead of confuses.
/// </summary>
public static class Gotchas
{
    private static readonly Regex Subtract = new(@"^\s*#eval\s+(\d+)\s*-\s*(\d+)\s*$", RegexOptions.Compiled);
    private static readonly Regex Divide = new(@"^\s*#eval\s+(\d+)\s*/\s*(\d+)\s*$", RegexOptions.Compiled);
    private static readonly Regex Decimals = new(@"^\s*#eval\s+\d*\.\d+\s*[-+*/]\s*\d*\.\d+", RegexOptions.Compiled);
    private static readonly Regex Power = new(@"^\s*#eval\s+(\d+)\s*\^\s*(\d+)\s*$", RegexOptions.Compiled);
    private static readonly Regex Statement = new(@"^\s*(theorem|lemma|example)\b[^:=]*:[^=]*==", RegexOptions.Compiled);

    /// <summary>The surprises in <paramref name="text"/>, in line order. Comments and strings are not looked at.</summary>
    public static IReadOnlyList<Gotcha> Find(string text)
    {
        string[] lines = CodeText.Lines(text);
        var found = new List<Gotcha>();
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            if (Subtract.Match(line) is { Success: true } sub && TryInt(sub.Groups[1].Value, out long a) && TryInt(sub.Groups[2].Value, out long b) && a < b)
            {
                found.Add(new Gotcha(i, $"{a} - {b} is 0, not {a - b}",
                    "Natural numbers (Nat) have no negatives, so subtracting more than you have stops at 0. For negative numbers use integers: `(" + a + " : Int) - " + b + "`."));
            }
            else if (Divide.Match(line) is { Success: true } div && TryInt(div.Groups[1].Value, out long n) && TryInt(div.Groups[2].Value, out long d))
            {
                if (d == 0)
                {
                    found.Add(new Gotcha(i, $"{n} / 0 is 0", "Lean has no error for dividing by zero: on natural numbers the answer is simply 0. Prove the divisor is not zero when it matters."));
                }
                else if (n % d != 0)
                {
                    found.Add(new Gotcha(i, $"{n} / {d} is {n / d}, not {((double)n / d).ToString("0.###", CultureInfo.InvariantCulture)}",
                        "Dividing natural numbers rounds down. For a decimal answer use `Float`: `(" + n + " : Float) / " + d + "`."));
                }
            }
            else if (Decimals.IsMatch(line))
            {
                found.Add(new Gotcha(i, "decimals are approximate", "Numbers with a decimal point are `Float`s, which are stored in binary, so small rounding errors appear (0.1 + 0.2 is not exactly 0.3). Don't compare them with `=`, and prove things about `Nat`, `Int` or `Rat` instead."));
            }
            else if (Power.Match(line) is { Success: true } pow && TryInt(pow.Groups[2].Value, out long e) && e >= 64)
            {
                found.Add(new Gotcha(i, "natural numbers never overflow", "Unlike a 64-bit integer in most languages, a `Nat` can be as big as memory allows, so this is exact: no wrap-around, no error."));
            }
            if (Statement.IsMatch(line))
            {
                found.Add(new Gotcha(i, "== in a statement", "`==` is a yes/no test your program runs (it gives a `Bool`). A claim to prove uses `=`. Write `a = b` in a theorem."));
            }
        }
        return found;
    }

    private static bool TryInt(string s, out long value) => long.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out value);
}

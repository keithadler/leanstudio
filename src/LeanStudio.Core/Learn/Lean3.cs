using System.Text.RegularExpressions;

namespace LeanStudio.Core.Learn;

/// <summary>Something in a file that is Lean 3, and how Lean 4 says it.</summary>
/// <param name="Line">0-based line.</param>
/// <param name="Found">What was found.</param>
/// <param name="Lean4">What it is in Lean 4.</param>
public sealed record Lean3Finding(int Line, string Found, string Lean4);

/// <summary>
/// Most of the Lean tutorials, forum answers and blog posts on the web are about Lean 3, which looks a lot like Lean 4 and
/// does not run in it. This spots the Lean 3 habits a beginner pastes in (<c>begin … end</c>, <c>λ x,</c>, <c>cases h with</c>…)
/// and says what they are now.
/// </summary>
public static class Lean3
{
    private static readonly (Regex Pattern, string Found, string Lean4)[] Lines =
    [
        (new(@"^\s*assume\b"), "assume", "`assume` is gone: use `intro` (in a term, `fun x => …`)."),
        (new(@"λ\s*[\p{L}\p{N}_'\s()]+,"), "λ x,", "Lean 4 writes a function as `fun x => body` (or `λ x => body`): an arrow `=>`, not a comma."),
        (new(@"^\s*import\s+(data|tactic|algebra|order|logic|analysis|topology|set_theory|linear_algebra|number_theory|ring_theory|group_theory|measure_theory)\."), "an import in lower case",
            "Mathlib 4 modules are capitalised: `import Mathlib.Data.Nat.Basic`, or `import Mathlib` for everything."),
        (new(@"\bopen_locale\b"), "open_locale", "`open_locale` is now `open scoped`."),
        (new(@"\bcases\s+[\p{L}\p{N}_'.]+\s+with\b"), "cases h with x hx", "Lean 4 takes the names in a pattern: `obtain ⟨x, hx⟩ := h` or `rcases h with ⟨x, hx⟩`; `cases h with | inl a => …` for a sum."),
        (new(@"^\s*variables\b"), "variables", "`variables` is `variable` in Lean 4."),
        (new(@"^\s*universes?\b"), "universes", "Write `universe u` (one at a time, or `universe u v`); Lean 4 usually works out universes for you."),
        (new(@"\b(nat|int|list|finset|set|option)\.[a-z_]+"), "a lower-case library name (nat.succ)", "Lean 4 capitalises type names in qualified names: `Nat.succ`, `List.length`."),
        (new(@"^\s*\{\s*(intro|simp|exact|apply|rw|cases|induction|norm_num|linarith|split|refl|existsi)\b"), "{ tactic }", "Lean 4 groups the goals of a tactic with `·` bullets on their own lines, not `{ … }`."),
        (new(@"^\s*refl\b"), "refl", "The tactic is `rfl` in Lean 4."),
        (new(@"^\s*existsi\b"), "existsi", "Use `exists x` or `refine ⟨x, ?_⟩` (or `exact ⟨x, proof⟩`)."),
    ];

    /// <summary>The Lean 3 habits in <paramref name="text"/>, in line order. Comments and strings are not looked at.</summary>
    public static IReadOnlyList<Lean3Finding> Find(string text)
    {
        string[] lines = CodeText.Lines(text);
        var found = new List<Lean3Finding>();
        bool inBegin = false;
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            if (Regex.IsMatch(line, @"^\s*begin\s*$"))
            {
                found.Add(new Lean3Finding(i, "begin", "Lean 3 wrote a proof between `begin` and `end`. Lean 4 writes `:= by` at the end of the statement and the tactics on the lines after it, indented."));
                inBegin = true;
                continue;
            }
            if (inBegin && Regex.IsMatch(line, @"^\s*end\s*$"))
            {
                found.Add(new Lean3Finding(i, "end (closing a begin)", "Nothing closes a `by` block in Lean 4: the indentation ends it. (`end` only closes a `namespace` or a `section`.)"));
                inBegin = false;
                continue;
            }
            bool import = Regex.IsMatch(line, @"^\s*import\b");
            foreach ((Regex pattern, string what, string lean4) in Lines)
            {
                // A module path (data.nat.basic) is not a qualified name: the import rule above says what is wrong with it.
                if (pattern.IsMatch(line) && !(import && what.StartsWith("a lower-case library name", StringComparison.Ordinal)))
                {
                    found.Add(new Lean3Finding(i, what, lean4));
                }
            }
            if (inBegin && Regex.IsMatch(line, @"\S\s*,\s*$"))
            {
                found.Add(new Lean3Finding(i, "a comma after a tactic", "Lean 3 separated tactics with commas. Lean 4 starts a new line (or uses `;`)."));
            }
        }
        return found;
    }
}

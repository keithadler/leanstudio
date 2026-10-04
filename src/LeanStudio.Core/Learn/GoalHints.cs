namespace LeanStudio.Core.Learn;

/// <summary>
/// "I'm stuck" for a beginner: three hints for a goal, each a little more than the one before. First what the goal says in
/// words, then what kind of goal it is and what that suggests, then the tactic to try and how it is written. Read from the
/// shape of the goal alone, so it is a nudge and not a proof.
/// </summary>
public static class GoalHints
{
    /// <summary>The hints for the goal <paramref name="goal"/> (what is to be proved), gentlest first: always three.</summary>
    public static IReadOnlyList<string> For(string goal)
    {
        string g = System.Text.RegularExpressions.Regex.Replace(goal.Trim(), @"\s+", " ");
        (string kind, string nudge, string tactic) = Shape(g);
        return
        [
            "In words: " + PlainEnglish.Read(g),
            $"This is {kind}. {nudge}",
            $"Try: {tactic}",
        ];
    }

    private static (string Kind, string Nudge, string Tactic) Shape(string g)
    {
        string s = StripParens(g);
        if (s.StartsWith('∀'))
        {
            return ("a \"for all\" goal", "To prove something for every x, take an arbitrary x and prove it for that one.", "`intro x`, then go on with what is left");
        }
        if (s.StartsWith('∃'))
        {
            return ("a \"there exists\" goal", "You have to name the thing that exists, then prove it does what is claimed.", "`exact ⟨the_thing, proof⟩`, or `refine ⟨the_thing, ?_⟩` to prove the rest afterwards");
        }
        if (HasTop(s, "↔"))
        {
            return ("an \"if and only if\" goal", "That is two goals in one: prove it in each direction.", "`constructor`, then prove each direction (the first with `intro h`)");
        }
        if (HasTop(s, "→"))
        {
            return ("an \"if … then …\" goal", "To prove \"if A then B\", assume A (give it a name) and prove B.", "`intro h`, then prove the part after the arrow, using `h`");
        }
        if (HasTop(s, "∨"))
        {
            return ("an \"or\" goal", "You only have to prove one side. Pick the side you can prove.", "`left` or `right`, then prove that side (or `exact Or.inl h` / `exact Or.inr h`)");
        }
        if (HasTop(s, "∧"))
        {
            return ("an \"and\" goal", "You have to prove both parts, one after the other.", "`constructor`, then prove each part (or `exact ⟨first_proof, second_proof⟩`)");
        }
        if (s.StartsWith('¬'))
        {
            return ("a \"not\" goal", "Not P means \"P leads to a contradiction\", so assume P and show that is impossible.", "`intro h`, then derive `False`");
        }
        if (s == "True")
        {
            return ("the goal True", "True is always provable.", "`trivial`");
        }
        if (s == "False")
        {
            return ("the goal False", "You cannot prove False directly: look for a contradiction in the assumptions.", "`contradiction`, or `exact absurd h h'` with two assumptions that disagree");
        }
        if (HasTop(s, "≠") || HasTop(s, "≤") || HasTop(s, "≥") || HasTop(s, "<") || HasTop(s, ">"))
        {
            return ("a comparison of numbers", "Lean has a tactic that decides arithmetic facts like this on its own.", "`omega` (for whole numbers), or `simp`, or `decide` for small concrete numbers");
        }
        if (HasTop(s, "="))
        {
            return ("an equation", "If both sides are the same once you compute them, Lean checks it by itself; otherwise simplify or rewrite.", "`rfl` if both sides compute to the same thing, `simp` to tidy up, `omega` for number facts, or `rw [lemma]` to use an equation you know");
        }
        return ("a goal with no obvious shape", "Look at the assumptions above the line: one of them may be exactly the goal, or may become it after one step.", "`assumption`, `simp`, or `exact?` to ask Lean to search for the proof");
    }

    private static string StripParens(string s)
    {
        s = s.Trim();
        while (s.Length > 1 && s[0] == '(' && Matching(s, 0) == s.Length - 1)
        {
            s = s[1..^1].Trim();
        }
        return s;
    }

    private static int Matching(string s, int open)
    {
        int depth = 0;
        for (int i = open; i < s.Length; i++)
        {
            depth += s[i] is '(' or '[' or '{' or '⟨' ? 1 : s[i] is ')' or ']' or '}' or '⟩' ? -1 : 0;
            if (depth == 0)
            {
                return i;
            }
        }
        return -1;
    }

    /// <summary>Whether <paramref name="symbol"/> occurs outside any brackets.</summary>
    private static bool HasTop(string s, string symbol)
    {
        int depth = 0;
        for (int i = 0; i < s.Length; i++)
        {
            depth += s[i] is '(' or '[' or '{' or '⟨' ? 1 : s[i] is ')' or ']' or '}' or '⟩' ? -1 : 0;
            if (depth == 0 && string.CompareOrdinal(s, i, symbol, 0, symbol.Length) == 0
                && !(symbol == "=" && i > 0 && s[i - 1] is '<' or '>' or '≠' or ':' or '=')
                && !(symbol is "<" or ">" && i + 1 < s.Length && s[i + 1] == '-'))
            {
                return true;
            }
        }
        return false;
    }
}

using System.Text.RegularExpressions;

namespace LeanStudio.Core.Learn;

/// <summary>One proof puzzle: a statement with a <c>sorry</c> to replace, hints, and an answer.</summary>
/// <param name="Title">What it is called.</param>
/// <param name="Level">1 (a first taste) to 3 (needs two ideas).</param>
/// <param name="Statement">The Lean text, with exactly one <c>sorry</c>.</param>
/// <param name="Hints">Hints, each a little bigger than the one before.</param>
/// <param name="Solution">What replaces the <c>sorry</c>.</param>
public sealed record Puzzle(string Title, int Level, string Statement, IReadOnlyList<string> Hints, string Solution)
{
    /// <summary>The statement with the solution in place of the <c>sorry</c>: a complete proof.</summary>
    public string Solved => Statement.Replace("sorry", Solution, StringComparison.Ordinal);
}

/// <summary>
/// A short run of proof puzzles for a beginner who has finished a lesson and wants to play: small statements in core Lean,
/// each with a <c>sorry</c> to replace, from one-liners to ones that need two ideas, with hints that get gradually bigger.
/// Every solution is checked against real Lean by the tests.
/// </summary>
public static class Puzzles
{
    /// <summary>The puzzles, easiest first.</summary>
    public static IReadOnlyList<Puzzle> All { get; } =
    [
        new("Two plus two", 1, "example : 2 + 2 = 4 := by\n  sorry\n",
            ["Both sides are numbers Lean can compute.", "When both sides compute to the same thing, one short tactic proves it: `rfl`.", "Replace `sorry` with `rfl`."], "rfl"),
        new("Adding nothing", 1, "example (n : Nat) : n + 0 = n := by\n  sorry\n",
            ["Adding 0 changes nothing, and Lean knows it by computing.", "Try `rfl`, or let `simp` tidy it up.", "Replace `sorry` with `rfl`."], "rfl"),
        new("Both at once", 1, "example (p q : Prop) (hp : p) (hq : q) : p ∧ q := by\n  sorry\n",
            ["The goal has two parts, joined by ∧ (and). You have a proof of each.", "`constructor` splits an ∧ into two goals; or write both proofs together in ⟨ ⟩.", "Replace `sorry` with `exact ⟨hp, hq⟩`."], "exact ⟨hp, hq⟩"),
        new("Pick a side", 1, "example (p : Prop) (hp : p) : p ∨ False := by\n  sorry\n",
            ["The goal is an \"or\": you only need to prove one side.", "`left` chooses the left side, which is exactly what you have.", "Replace `sorry` with `left` and then `exact hp` on the next line."], "left\n  exact hp"),
        new("Zero in front", 1, "example (n : Nat) : 0 + n = n := by\n  sorry\n",
            ["This one is not just computing: 0 + n has an n in it that Lean cannot compute.", "A tactic that knows facts about arithmetic will do: `simp` or `omega`.", "Replace `sorry` with `omega`."], "omega"),
        new("Swap them", 2, "example (p q : Prop) : p ∧ q → q ∧ p := by\n  sorry\n",
            ["The goal is an if-then. Start by assuming the left side.", "`intro h` gives you h : p ∧ q. Then take it apart with `obtain ⟨hp, hq⟩ := h`.", "Then prove q ∧ p with `exact ⟨hq, hp⟩`."], "intro h\n  obtain ⟨hp, hq⟩ := h\n  exact ⟨hq, hp⟩"),
        new("Commuting", 2, "example (a b : Nat) : a + b = b + a := by\n  sorry\n",
            ["Neither side can be computed, since a and b are unknown.", "Lean has a tactic for arithmetic like this, and a lemma named for exactly this fact.", "Replace `sorry` with `omega`, or with `exact Nat.add_comm a b`."], "omega"),
        new("A little bigger", 2, "example (a b : Nat) (h : a ≤ b) : a < b + 1 := by\n  sorry\n",
            ["You have an assumption h and a goal about the same numbers.", "`omega` uses the assumptions in its context.", "Replace `sorry` with `omega`."], "omega"),
        new("A chain of ifs", 2, "example (p q r : Prop) (hpq : p → q) (hqr : q → r) : p → r := by\n  sorry\n",
            ["Assume p, then use the two arrows one after the other.", "`intro hp` gives you p. Then hpq hp is a proof of q, and hqr of that is a proof of r.", "Replace `sorry` with `intro hp` and then `exact hqr (hpq hp)`."], "intro hp\n  exact hqr (hpq hp)"),
        new("Lists and nothing", 2, "example (xs : List Nat) : (xs ++ []).length = xs.length := by\n  sorry\n",
            ["Appending the empty list changes nothing; Lean knows a lemma for that.", "`simp` knows it, and knows what `length` does.", "Replace `sorry` with `simp`."], "simp"),
        new("Twice", 3, "theorem double_eq (n : Nat) : n + n = 2 * n := by\n  sorry\n",
            ["n + n and 2 * n are two ways to write the same number.", "A tactic that handles + and * by a constant will see it.", "Replace `sorry` with `omega`."], "omega"),
        new("There is a square", 3, "example : ∃ n : Nat, n * n = 49 := by\n  sorry\n",
            ["To prove \"there exists\", you name the thing that exists, then prove it works.", "Which number times itself is 49? Give it with `exact ⟨that_number, proof⟩`.", "Replace `sorry` with `exact ⟨7, rfl⟩`: 7 * 7 computes to 49, so `rfl` proves it."], "exact ⟨7, rfl⟩"),
    ];

    /// <summary>The 0-based index of the puzzle for <paramref name="date"/>: the same all day, another tomorrow.</summary>
    public static int DailyIndex(DateOnly date) => date.DayNumber % All.Count;

    /// <summary>The puzzle for <paramref name="date"/>.</summary>
    public static Puzzle Daily(DateOnly date) => All[DailyIndex(date)];

    /// <summary>The file to play a puzzle in: a short header, then the puzzle.</summary>
    public static string File(int index)
    {
        Puzzle p = All[index];
        return $"-- Puzzle {index + 1} of {All.Count}: {p.Title}  (level {p.Level})\n"
            + "-- Replace the sorry so Lean accepts it. Stuck? Learn ▸ Puzzle Hint. Give up? Show Puzzle Solution.\n\n"
            + p.Statement;
    }

    /// <summary>The 0-based index of the puzzle a file of <see cref="File"/> was made from, or null when it was not.</summary>
    public static int? IndexOf(string text)
    {
        Match m = Regex.Match(text, @"^-- Puzzle (\d+) of \d+:", RegexOptions.Multiline);
        return m.Success && int.TryParse(m.Groups[1].Value, out int n) && n >= 1 && n <= All.Count ? n - 1 : null;
    }
}

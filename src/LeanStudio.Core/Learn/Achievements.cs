using System.Text.RegularExpressions;

namespace LeanStudio.Core.Learn;

/// <summary>A thing a learner can do for the first time, and what to try to do it.</summary>
/// <param name="Id">A short name.</param>
/// <param name="Title">What it is called.</param>
/// <param name="Description">What it means.</param>
/// <param name="Try">What to write to earn it.</param>
public sealed record Badge(string Id, string Title, string Description, string Try);

/// <summary>
/// Small firsts, in the order a beginner meets them: a first <c>#eval</c>, a first function, a first proof Lean accepts, a
/// first induction. Worked out from the Lean files a person has written, so there is nothing to track and nothing to cheat;
/// and the first one not yet earned is what to try next.
/// </summary>
public static class Achievements
{
    private sealed record Rule(Badge Badge, Func<string, string[], bool> Earned);

    private static bool Uses(string[] code, string pattern) => code.Any(l => Regex.IsMatch(l, pattern));

    private static readonly Rule[] Rules =
    [
        new(new("hello", "Hello, Lean", "You asked Lean to compute something.", "#eval 2 + 2"), (_, c) => Uses(c, @"^\s*#eval\b")),
        new(new("check", "Type detective", "You asked Lean what type something has.", "#check 42"), (_, c) => Uses(c, @"^\s*#check\b")),
        new(new("def", "Function maker", "You defined something of your own.", "def double (n : Nat) : Nat := n + n"), (_, c) => Uses(c, @"^\s*(def|abbrev)\s")),
        new(new("if", "Decision maker", "You used if … then … else.", "if n > 3 then \"big\" else \"small\""), (_, c) => Uses(c, @"\bif\b.*\bthen\b|\bthen\b")),
        new(new("match", "Pattern matcher", "You took a value apart with match.", "match n with | 0 => … | k + 1 => …"), (_, c) => Uses(c, @"\bmatch\b")),
        new(new("structure", "Record keeper", "You bundled data together in a structure.", "structure Point where x : Nat  y : Nat"), (_, c) => Uses(c, @"^\s*structure\s")),
        new(new("unicode", "Symbol wizard", "You typed a logic symbol (∀ ∃ → ∧ ∨).", "type \\forall and a space"), (t, _) => Regex.IsMatch(t, @"[∀∃∧∨↔]|→")),
        new(new("theorem", "Theorem writer", "You stated a theorem (even if it is not proved yet).", "theorem two_plus_two : 2 + 2 = 4 := by sorry"), (_, c) => Uses(c, @"^\s*(theorem|lemma|example)\b")),
        new(new("proved", "First proof", "You wrote a theorem and Lean has no sorry left in the file.", "theorem t : 2 + 2 = 4 := by rfl"), (t, c) => Uses(c, @"^\s*(theorem|lemma|example)\b") && !Uses(c, @"\bsorry\b")),
        new(new("intro", "Assumer", "You used intro to assume the left side of an if-then.", "intro h"), (_, c) => Uses(c, @"^\s*(·\s*)?intros?\b")),
        new(new("simp", "Simplifier", "You let simp tidy up a goal.", "simp"), (_, c) => Uses(c, @"^\s*(·\s*)?simp\b|\bby\s+simp\b")),
        new(new("rw", "Rewriter", "You rewrote a goal with an equation.", "rw [Nat.add_comm]"), (_, c) => Uses(c, @"\brw\s*\[")),
        new(new("constructor", "Splitter", "You split an and (or if-and-only-if) into two goals.", "constructor"), (_, c) => Uses(c, @"^\s*(·\s*)?constructor\b")),
        new(new("cases", "Case cracker", "You took a hypothesis apart by cases.", "cases h with | inl a => … | inr b => …"), (_, c) => Uses(c, @"^\s*(·\s*)?(cases|rcases|obtain)\b")),
        new(new("omega", "Number cruncher", "You let omega prove a fact about numbers.", "omega"), (_, c) => Uses(c, @"\bomega\b")),
        new(new("induction", "Inductor", "You proved something by induction.", "induction n with | zero => … | succ k ih => …"), (_, c) => Uses(c, @"^\s*(·\s*)?induction\b")),
        new(new("calc", "Chain builder", "You proved something step by step with calc.", "calc a = b := … \n _ = c := …"), (_, c) => Uses(c, @"\bcalc\b")),
    ];

    /// <summary>All the badges, in the order a beginner meets them.</summary>
    public static IReadOnlyList<Badge> All { get; } = [.. Rules.Select(r => r.Badge)];

    /// <summary>
    /// Every badge and whether the Lean files <paramref name="files"/> earn it. A proof counts only in a file that has no
    /// <c>sorry</c> in it.
    /// </summary>
    public static IReadOnlyList<(Badge Badge, bool Earned)> Earned(IEnumerable<string> files)
    {
        List<(string Text, string[] Code)> parsed = [.. files.Select(f => (f, CodeText.Lines(f)))];
        return [.. Rules.Select(r => (r.Badge, parsed.Any(p => r.Earned(p.Text, p.Code))))];
    }

    /// <summary>The first badge not yet earned: what to try next, or null when there are none left.</summary>
    public static Badge? Next(IReadOnlyList<(Badge Badge, bool Earned)> earned) => earned.FirstOrDefault(e => !e.Earned).Badge;

    /// <summary>The badges as text: a line each with a mark, then what to try next.</summary>
    public static string ToText(IReadOnlyList<(Badge Badge, bool Earned)> earned)
    {
        var lines = earned.Select(e => $"{(e.Earned ? "✓" : "·")} {e.Badge.Title}: {e.Badge.Description}").ToList();
        int count = earned.Count(e => e.Earned);
        lines.Insert(0, $"{count} of {earned.Count} badges");
        Badge? next = Next(earned);
        lines.Add(next is null ? "You have every badge. Try a puzzle, or a famous theorem from the Learn tab." : $"Next: {next.Title}. Try: {next.Try.Replace("\n", " ", StringComparison.Ordinal)}");
        return string.Join('\n', lines);
    }
}

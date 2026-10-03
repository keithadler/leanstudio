namespace LeanStudio.Core.Learn;

/// <summary>Small things that make Lean nicer, one a day: the habit of a tip of the day, for someone who has not read the manual.</summary>
public static class Tips
{
    /// <summary>Every tip, each short enough to read at a glance.</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        "Type \\forall and a space to get ∀. Every symbol has a word: \\exists \\to \\and \\or \\not \\le \\ne.",
        "Hover any tactic (intro, simp, omega) to read what it does in plain words, with an example.",
        "A sorry is a promise to prove something later. Lean accepts it with a warning, so you can build the shape of a proof first.",
        "Put your cursor on a line of a proof to see the goals at that exact point in the Tactic State.",
        "`#eval` runs code and shows the answer at the end of the line. Try `#eval 2 ^ 100`.",
        "`#check` tells you the type of anything: `#check Nat.add_comm` shows what that theorem says.",
        "If Lean says \"unsolved goals\", the proof is not finished: read what is left below the line of dashes.",
        "`exact?` asks Lean to search for a lemma that closes the goal. It can find proofs you do not know the name of.",
        "`omega` proves facts about whole numbers with +, -, *, ≤ and <. When a number goal is stuck, try it first.",
        "`simp` tidies a goal using hundreds of known facts. If it closes the goal, you are done; if not, look at what it left.",
        "`rfl` proves an equation when both sides compute to the same thing: `2 + 2 = 4` is `rfl`.",
        "Use a bullet · for each goal after `constructor` or `cases`, so every sub-proof sits in its own block.",
        "`intro h` assumes the left side of an if-then and calls it h. Then you only have to prove the right side.",
        "Natural numbers stop at zero: `3 - 5` is `0`. Use `Int` when you need negatives.",
        "`theorem` and `def` are the same shape: a name, what it takes, what it gives, `:=`, and a body. One is a proof, the other a value.",
        "Name your hypotheses (`intro hp hq`) so the goals you read later say what each assumption is.",
        "A proof by induction has two parts: the starting case (0), and the step from k to k + 1. The step gets an assumption for k.",
        "`calc` lets you write a proof as a chain: a = b, then b = c, so a = c, one line per step.",
        "`rw [h]` replaces the left side of the equation h with its right side in the goal. `rw [← h]` goes the other way.",
        "When you cannot see why Lean is unhappy, `Prove It` tries seventeen tactics at once and shows what works.",
        "Play in the Playground: a file with no project to set up, where `#eval` and `#check` answer at once.",
        "Every lesson in the tutorial has exercises with a sorry to replace. The Learn tab ticks a lesson off when Lean accepts it.",
        "`Learn ▸ Hint for This Goal` gives a nudge, then a bigger one, without giving the proof away.",
        "Pasted something from a Lean 3 tutorial? `Learn ▸ Is This Lean 3?` says what changed.",
        "`decide` settles small facts by checking every case: `decide` proves `10 < 20` or that 7 is not even.",
        "Indentation matters: the lines of a proof after `by` are indented the same amount, and a smaller indent ends the block.",
    ];

    /// <summary>The tip for <paramref name="date"/>: the same all day, a different one tomorrow.</summary>
    public static string ForDate(DateOnly date) => All[date.DayNumber % All.Count];
}

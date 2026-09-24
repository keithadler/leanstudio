using LeanStudio.Core.Toolchains;

namespace LeanStudio.Core.Learn;

/// <summary>
/// One lesson: a Lean file that teaches by example and ends in exercises, each a <c>sorry</c> to replace.
/// <see cref="Solutions"/> holds a model answer for each <c>sorry</c> in order; the tests use them to prove every
/// exercise can be done in plain Lean, so a newcomer is never stuck on one that cannot.
/// </summary>
/// <param name="FileName">The lesson's file name, such as <c>01_Hello.lean</c>, which also orders the lessons.</param>
/// <param name="Title">The lesson's title, for lists.</param>
/// <param name="Summary">One sentence on what the lesson covers.</param>
/// <param name="Content">The lesson file's full starting text.</param>
/// <param name="Solutions">A model answer for each <c>sorry</c> in the code, in order.</param>
public sealed record Lesson(string FileName, string Title, string Summary, string Content, IReadOnlyList<string> Solutions)
{
    /// <summary>The number of exercises (<c>sorry</c>s to replace) in the lesson.</summary>
    public int Exercises => Solutions.Count;
}

/// <summary>
/// "Learn Lean in Lean Studio": ten lessons for someone who has never used Lean, written as Lean files so the
/// person learns in the editor itself, with the tactic state, hovers and error explanations helping. Only core
/// Lean is used, so the tutorial works the moment a toolchain is installed; no Mathlib download is needed.
/// </summary>
public static class Tutorial
{
    /// <summary>Where the tutorial and playground live: Documents/Lean Studio, or $LEANSTUDIO_HOME for tests.</summary>
    public static string Home =>
        Environment.GetEnvironmentVariable("LEANSTUDIO_HOME") is { Length: > 0 } h
            ? h
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Lean Studio");

    /// <summary>Where the tutorial is written unless a folder is given: <c>Tutorial</c> under <see cref="Home"/>.</summary>
    public static string DefaultFolder => Path.Combine(Home, "Tutorial");

    /// <summary>
    /// Write the lessons into <paramref name="folder"/>, keeping any lesson already there (it holds the person's
    /// work). Pins a toolchain so Lean knows which version to run, unless a <c>lean-toolchain</c> file is already
    /// there or none is installed. Returns the folder (<see cref="DefaultFolder"/> when none is given).
    /// </summary>
    public static async Task<string> CreateAsync(string? folder = null, CancellationToken ct = default)
    {
        folder ??= DefaultFolder;
        Directory.CreateDirectory(folder);
        string toolchainFile = Path.Combine(folder, "lean-toolchain");
        if (!File.Exists(toolchainFile) && await Playground.PreferredToolchainAsync(ct).ConfigureAwait(false) is string tc)
        {
            await File.WriteAllTextAsync(toolchainFile, tc + "\n", ct).ConfigureAwait(false);
        }
        foreach (Lesson l in Lessons)
        {
            string path = Path.Combine(folder, l.FileName);
            if (!File.Exists(path))
            {
                await File.WriteAllTextAsync(path, l.Content, ct).ConfigureAwait(false);
            }
        }
        return folder;
    }

    /// <summary>Put a lesson back the way it started, overwriting the person's work in its file.</summary>
    public static void Reset(string folder, Lesson lesson) => File.WriteAllText(Path.Combine(folder, lesson.FileName), lesson.Content);

    /// <summary>The lesson whose file name matches <paramref name="path"/>'s (in any folder), or null.</summary>
    public static Lesson? LessonFor(string path) =>
        Lessons.FirstOrDefault(l => string.Equals(Path.GetFileName(path), l.FileName, StringComparison.Ordinal));

    /// <summary>The lesson with every sorry in its code replaced by its model answer (the prose mentions sorry too).</summary>
    public static string Solved(Lesson lesson)
    {
        string text = lesson.Content;
        foreach (string answer in lesson.Solutions)
        {
            int at = FirstCodeSorry(text);
            if (at < 0)
            {
                break;
            }
            text = text[..at] + answer + text[(at + "sorry".Length)..];
        }
        return text;
    }

    /// <summary>The offset of the first <c>sorry</c> outside comments, or -1. Assumes <c>\n</c> line endings.</summary>
    public static int FirstCodeSorry(string text)
    {
        bool inBlock = false;
        int offset = 0;
        foreach (string line in text.Split('\n'))
        {
            string code = Proofs.ProofSteps.StripComments(line, ref inBlock);
            int at = code.IndexOf("sorry", StringComparison.Ordinal);
            if (at >= 0)
            {
                return offset + at;
            }
            offset += line.Length + 1;
        }
        return -1;
    }

    /// <summary>The lessons, in order.</summary>
    public static IReadOnlyList<Lesson> Lessons { get; } =
    [
        new("01_Hello.lean", "Hello, Lean", "Run code with #eval, ask for types with #check, name things with def.", """
            /-!
            # Lesson 1: Hello, Lean

            Lean is two things at once: a programming language, and a language for mathematical proofs
            that the computer checks for you. This lesson is about the programming side.

            Put your cursor on a line that starts with `#eval`. Lean runs it, and the result appears at
            the end of the line. Hover over anything to see what it is.
            -/

            #eval 2 + 2

            #eval "Hello, " ++ "Lean!"

            #eval [1, 2, 3].map (fun x => x * 10)

            /-! `#check` asks Lean for the type of something. Everything in Lean has a type:
            `Nat` is the natural numbers 0, 1, 2, …, `String` is text, `List Nat` is a list of numbers. -/

            #check 42
            #check "text"
            #check [1, 2, 3]

            /-! `def` gives a name to a value or a function. After the colon comes the type. -/

            def greeting : String := "Welcome to Lean"

            def double (n : Nat) : Nat := n + n

            #eval double 21

            /-!
            ## Exercise 1

            Replace `sorry` with the body of a function that adds one to its input.
            The yellow line under `addOne` means "this uses sorry": when it goes away, you are done.
            Then delete the `--` in front of `#eval` to try your function.
            -/

            def addOne (n : Nat) : Nat := sorry

            -- #eval addOne 41

            /-!
            ## Exercise 2

            Write a `String` that says hello to you.
            -/

            def myGreeting : String := sorry
            """,
            ["n + 1", "\"Hello, me!\""]),

        new("02_Functions.lean", "Functions and recursion", "Functions with several inputs, pattern matching, and functions that call themselves.", """
            /-!
            # Lesson 2: Functions and recursion

            A function takes inputs and gives an output. You list the inputs with their types.
            -/

            def square (n : Nat) : Nat := n * n

            #eval square 7

            def add (a b : Nat) : Nat := a + b

            #eval add 3 4

            /-! A function without a name: `fun x => …` -/

            #eval (fun x => x + 1) 5

            /-! Pattern matching: say what to do for each shape of input. `_` means "anything else". -/

            def isZero : Nat → Bool
              | 0 => true
              | _ => false

            #eval isZero 0
            #eval isZero 7

            /-! Recursion: a function can use itself on a smaller input. Lean checks that it always
            finishes, which matters later: a proof must never run forever. -/

            def factorial : Nat → Nat
              | 0 => 1
              | n + 1 => (n + 1) * factorial n

            #eval factorial 5

            /-!
            ## Exercise 1

            `sumUpTo n` should be 0 + 1 + 2 + … + n. The case for 0 is done; fill in the other one.
            Hint: the sum up to n + 1 is (n + 1) plus the sum up to n.
            -/

            def sumUpTo : Nat → Nat
              | 0 => 0
              | n + 1 => sorry

            -- #eval sumUpTo 10   -- should be 55

            /-!
            ## Exercise 2

            Return how many items a list has. Type `xs.` and wait: Lean suggests what a list can do.
            -/

            def howMany (xs : List Nat) : Nat := sorry
            """,
            ["(n + 1) + sumUpTo n", "xs.length"]),

        new("03_FirstTheorems.lean", "Your first theorems", "A theorem is a statement and a proof. Start with ones Lean can check by computing.", """
            /-!
            # Lesson 3: Your first theorems

            A theorem is a statement plus a proof that it is true:

                theorem name : statement := proof

            Lean checks the proof. If it is wrong, you get a red underline; if it is right, nothing
            happens, which is exactly what you want.
            -/

            theorem two_plus_two : 2 + 2 = 4 := rfl

            /-! `rfl` ("reflexivity") says: both sides compute to the same thing. -/

            theorem double_three : 3 + 3 = 6 := by decide

            /-! `by` starts a proof made of steps, called tactics. `decide` works out a statement
            about specific numbers. `example` is a theorem without a name. -/

            example : 10 * 10 = 100 := by decide

            /-!
            ## Exercise 1

            Prove it. Try `rfl`.
            -/

            theorem seven_times_six : 7 * 6 = 42 := sorry

            /-!
            ## Exercise 2

            This one is about every number `n`, not one number. `rfl` still works: Lean's addition
            is defined so that `n + 0` computes to `n`.
            -/

            theorem add_zero' (n : Nat) : n + 0 = n := sorry
            """,
            ["rfl", "rfl"]),

        new("04_Tactics.lean", "Tactics: intro, exact, apply", "Build proofs step by step, watching the goal in the Tactic State panel.", """
            /-!
            # Lesson 4: Tactics

            Put your cursor inside a proof below and look at the Tactic State panel on the right.
            It shows what you have (the hypotheses, above the line) and what you must prove (the goal,
            after ⊢). Each tactic changes that picture.

            `p → q` means "if p then q". To prove it, assume `p` with `intro`, then prove `q`.
            -/

            theorem imp_self' (p : Prop) : p → p := by
              intro hp
              exact hp

            /-! `exact h` finishes the goal when `h` is exactly what is needed. -/

            /-!
            ## Exercise 1

            You already have `hp : p`, and the goal is `p`.
            -/

            theorem use_hypothesis (p : Prop) (hp : p) : p := by
              sorry

            /-!
            ## Exercise 2

            `hpq : p → q` turns a proof of `p` into a proof of `q`. Apply it: `exact hpq hp`,
            or `apply hpq` and then prove what is left.
            -/

            theorem modus_ponens (p q : Prop) (hp : p) (hpq : p → q) : q := by
              sorry
            """,
            ["exact hp", "exact hpq hp"]),

        new("05_AndOr.lean", "And, or", "Split goals with constructor, choose a side with left and right, take apart hypotheses.", """
            /-!
            # Lesson 5: And, or

            `p ∧ q` is "p and q" (type `\and`). `p ∨ q` is "p or q" (type `\or`).
            -/

            theorem and_intro' (p q : Prop) (hp : p) (hq : q) : p ∧ q := by
              constructor
              · exact hp
              · exact hq

            /-! `constructor` splits "p and q" into two goals. The `·` focuses on one goal at a time.
            A proof of `p ∧ q` holds two proofs: `h.1` for `p` and `h.2` for `q`. -/

            theorem and_left' (p q : Prop) (h : p ∧ q) : p := by
              exact h.1

            /-! To prove "p or q", pick a side. -/

            theorem or_left' (p q : Prop) (hp : p) : p ∨ q := by
              left
              exact hp

            /-!
            ## Exercise 1

            Swap the two sides of an "and". You can build a pair with angle brackets: `⟨first, second⟩`
            (type `\<`).
            -/

            theorem and_swap' (p q : Prop) (h : p ∧ q) : q ∧ p := by
              sorry

            /-!
            ## Exercise 2

            Swap an "or". Either `p` holds or `q` holds, and you must handle both. `cases h` splits
            into the two situations; or use `h.elim` with a way out of each.
            -/

            theorem or_swap' (p q : Prop) (h : p ∨ q) : q ∨ p := by
              sorry
            """,
            ["exact ⟨h.2, h.1⟩", "exact h.elim Or.inr Or.inl"]),

        new("06_Rewriting.lean", "Rewriting and simp", "Replace equals by equals with rw; let simp tidy things up.", """
            /-!
            # Lesson 6: Rewriting

            If you know `h : a = b`, then `rw [h]` replaces `a` with `b` in the goal.
            -/

            theorem rw_example (a b : Nat) (h : a = b) : a + 1 = b + 1 := by
              rw [h]

            /-! You can rewrite with theorems that are already proved, like `Nat.add_comm : a + b = b + a`.
            Hover `Nat.add_comm` to read it. -/

            example (a b : Nat) : a + b = b + a := by
              rw [Nat.add_comm]

            /-! `simp` knows hundreds of simplifications and applies them until it is stuck. -/

            example (xs : List Nat) : (xs ++ []).length = xs.length := by
              simp

            /-!
            ## Exercise 1

            Rewrite with both hypotheses, one after the other: `rw [h1, h2]`.
            -/

            theorem rw_twice (a b c : Nat) (h1 : a = b) (h2 : b = c) : a = c := by
              sorry

            /-!
            ## Exercise 2

            Let `simp` do it.
            -/

            theorem simp_ex (n : Nat) : n * 1 + 0 = n := by
              sorry
            """,
            ["rw [h1, h2]", "simp"]),

        new("07_Induction.lean", "Proof by induction", "Prove something for every number: show it for 0, then from n to n + 1.", """
            /-!
            # Lesson 7: Induction

            To prove something about every natural number, prove it for 0, and prove that if it
            holds for `k` it holds for `k + 1`. The `induction` tactic sets up both goals.
            -/

            def sumTo : Nat → Nat
              | 0 => 0
              | n + 1 => (n + 1) + sumTo n

            #eval sumTo 10

            theorem zero_add' (n : Nat) : 0 + n = n := by
              induction n with
              | zero => rfl
              | succ k ih => rw [Nat.add_succ, ih]

            /-! `ih` is the induction hypothesis: the statement for `k`, which you may use for `k + 1`.

            A famous one, proved: twice the sum 0 + 1 + … + n is n × (n + 1) (Gauss, as a schoolboy).
            `omega` is a tactic that solves arithmetic with + and numbers. -/

            theorem gauss (n : Nat) : 2 * sumTo n = n * (n + 1) := by
              induction n with
              | zero => rfl
              | succ k ih =>
                simp only [sumTo, Nat.mul_add, ih]
                simp only [Nat.add_mul, Nat.mul_one, Nat.one_mul]
                omega

            /-!
            ## Exercise

            The sum up to `n` is at least `n`. Try:
            `induction n <;> simp [sumTo] <;> omega`
            (`<;>` means "then do this to every goal the last step left").
            -/

            theorem sumTo_ge (n : Nat) : sumTo n ≥ n := by
              sorry
            """,
            ["induction n <;> simp [sumTo] <;> omega"]),

        new("08_Programs.lean", "Proving your programs correct", "Write a function, then prove what it does, for every input.", """
            /-!
            # Lesson 8: Proving programs correct

            Tests check a program on some inputs. A proof checks it on all of them.
            Here is our own list reversal:
            -/

            def reverse {α : Type} : List α → List α
              | [] => []
              | x :: xs => reverse xs ++ [x]

            #eval reverse [1, 2, 3]

            /-! Reversing never changes the length, whatever the list. -/

            theorem reverse_length {α : Type} (xs : List α) : (reverse xs).length = xs.length := by
              induction xs with
              | nil => rfl
              | cons x xs ih => simp [reverse, ih]

            /-!
            ## Exercise

            `countdown n` is the list n, n - 1, …, 0. Prove it has n + 1 elements.
            Try: `induction n <;> simp [countdown, *]` (the `*` lets simp use the induction hypothesis).
            -/

            def countdown : Nat → List Nat
              | 0 => [0]
              | n + 1 => (n + 1) :: countdown n

            #eval countdown 5

            theorem countdown_length (n : Nat) : (countdown n).length = n + 1 := by
              sorry
            """,
            ["induction n <;> simp [countdown, *]"]),

        new("09_ForallExists.lean", "For all, there exists", "State things about every value or some value; give a witness for 'there exists'.", """
            /-!
            # Lesson 9: For all, there exists

            `∀ n, P n` is "for every n, P n" (type `\forall`).
            `∃ n, P n` is "there is some n with P n" (type `\exists`).

            To prove "there exists", show one: the witness, and a proof that it works.
            -/

            example : ∃ n : Nat, n * n = 16 := ⟨4, rfl⟩

            theorem exists_even : ∃ n : Nat, n % 2 = 0 ∧ n > 5 := by
              exact ⟨6, by decide⟩

            /-! To prove "for all", take any `n` with `intro` and prove the statement for it. -/

            theorem all_le_succ : ∀ n : Nat, n ≤ n + 1 := by
              intro n
              omega

            /-!
            ## Exercise 1

            Find a number bigger than 100: `exact ⟨101, by decide⟩`, or pick your own.
            -/

            theorem exists_big : ∃ n : Nat, n > 100 := by
              sorry

            /-!
            ## Exercise 2

            No natural number is below zero. `¬` is "not" (type `\not`).
            -/

            theorem no_nat_below_zero : ∀ n : Nat, ¬ (n < 0) := by
              sorry
            """,
            ["exact ⟨101, by decide⟩", "intro n\n  omega"]),

        new("10_WhereNext.lean", "Where next", "A last challenge, and where to go from here.", """
            /-!
            # Lesson 10: Where next

            You can now read and write Lean proofs. Some places to go from here, all inside Lean Studio:

            * **Famous theorems** (Learn tab): real theorems, stated in English, to try in the playground.
            * **Playground** (Learn ▸ Open Playground): a file to experiment in.
            * **exact?** Write `exact?` where you are stuck and Lean searches for a proof; click the
              suggestion in the Tactic State panel to use it.
            * **Mathlib**, Lean's mathematics library: File ▸ New Project, template "Library using Mathlib".
            * **Tenet**: build a project (⌘B / Ctrl+B) and a second, independent checker confirms every
              proof, and tells you which ones still rest on `sorry`.
            * **AI assistants**: AI ▸ Connect an AI Assistant lets Claude, Gemini and others use Lean too.

            ## Final challenge

            Prove it any way you like. (`omega` knows a lot about `<` and `≤`.)
            -/

            theorem challenge (a b : Nat) (h : a ≤ b) : a < b + 1 := by
              sorry
            """,
            ["omega"]),
    ];
}

/// <summary>The playground: one Lean file to try things in, with no project to set up.</summary>
public static class Playground
{
    /// <summary>Where the playground is created unless a folder is given: <c>Playground</c> under <see cref="Tutorial.Home"/>.</summary>
    public static string DefaultFolder => Path.Combine(Tutorial.Home, "Playground");

    /// <summary>The text a new <c>Playground.lean</c> starts with.</summary>
    public const string Starter = """
        /-!
        # Playground

        A place to try things. Everything here runs as you type: results of `#eval` and `#check`
        appear at the end of their line. Nothing here needs a project.
        -/

        #eval 1 + 1

        #eval (List.range 10).map (· ^ 2)

        #check Nat.add_comm

        def fib : Nat → Nat
          | 0 => 0
          | 1 => 1
          | n + 2 => fib n + fib (n + 1)

        #eval (List.range 15).map fib

        theorem my_first : 2 + 3 = 5 := rfl

        #print axioms my_first

        -- Your turn: write below.

        """;

    /// <summary>
    /// The toolchain to pin a new folder to: elan's default, else the newest stable toolchain installed, else the
    /// newest of any kind, or null when there is none. Runs <c>elan</c> to list them.
    /// </summary>
    public static async Task<string?> PreferredToolchainAsync(CancellationToken ct = default)
    {
        IReadOnlyList<Toolchain> list = await Elan.ListAsync(ct).ConfigureAwait(false);
        Toolchain? def = list.FirstOrDefault(t => t.IsDefault);
        if (def is not null)
        {
            return def.Name;
        }
        static Version? V(Toolchain t)
        {
            string v = t.Version.TrimStart('v').Split('-')[0];
            return Version.TryParse(v, out Version? r) ? r : null;
        }
        return list.Where(t => !t.Name.Contains("-rc", StringComparison.Ordinal) && !t.Name.Contains("nightly", StringComparison.Ordinal))
                   .OrderByDescending(V).Select(t => t.Name).FirstOrDefault()
               ?? list.OrderByDescending(V).Select(t => t.Name).FirstOrDefault();
    }

    /// <summary>
    /// Create the playground (once) and return the full path of its <c>Playground.lean</c>. Adds
    /// <paramref name="append"/> at the end of the file if given, so it is written to disk.
    /// </summary>
    public static async Task<string> CreateAsync(string? append = null, string? folder = null, CancellationToken ct = default)
    {
        folder ??= DefaultFolder;
        Directory.CreateDirectory(folder);
        string toolchainFile = Path.Combine(folder, "lean-toolchain");
        if (!File.Exists(toolchainFile) && await PreferredToolchainAsync(ct).ConfigureAwait(false) is string tc)
        {
            await File.WriteAllTextAsync(toolchainFile, tc + "\n", ct).ConfigureAwait(false);
        }
        string file = Path.Combine(folder, "Playground.lean");
        if (!File.Exists(file))
        {
            await File.WriteAllTextAsync(file, Starter, ct).ConfigureAwait(false);
        }
        if (!string.IsNullOrWhiteSpace(append))
        {
            await File.AppendAllTextAsync(file, "\n" + append.TrimEnd() + "\n", ct).ConfigureAwait(false);
        }
        return file;
    }
}

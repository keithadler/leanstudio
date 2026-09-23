using LeanStudio.Core.Processes;
using LeanStudio.Core.Projects;
using LeanStudio.Core.Toolchains;

namespace LeanStudio.Core.Learn;

/// <summary>A piece of code to insert: a name, what it is for, and the text (with $0 where the cursor goes).</summary>
public sealed record Snippet(string Name, string Description, string Body)
{
    /// <summary>The body with every line after the first indented to match the line it is inserted into.</summary>
    public (string Text, int CursorOffset) Expand(string indent)
    {
        string text = Body.Replace("\n", "\n" + indent, StringComparison.Ordinal);
        int cursor = text.IndexOf("$0", StringComparison.Ordinal);
        text = text.Replace("$0", "", StringComparison.Ordinal);
        return (text, cursor < 0 ? text.Length : cursor);
    }
}

public static class Snippets
{
    public static IReadOnlyList<Snippet> All { get; } =
    [
        new("Function", "A function with typed inputs", "def name (x : Nat) : Nat :=\n  $0"),
        new("Recursive function", "A function defined by cases on a number", "def name : Nat → Nat\n  | 0 => $0\n  | n + 1 => name n"),
        new("Structure", "A record type with named fields", "structure Name where\n  field1 : Nat\n  field2 : String\n  deriving Repr$0"),
        new("Inductive type", "A type listing the ways to build its values", "inductive Shape where\n  | circle (r : Nat)\n  | square (side : Nat)\n  deriving Repr$0"),
        new("Pattern match", "Different results for different shapes of input", "match x with\n  | 0 => $0\n  | _ => _"),
        new("Theorem", "A named statement and its proof", "theorem name : statement := by\n  $0"),
        new("Example", "An unnamed statement to try", "example : 2 + 2 = 4 := by\n  $0"),
        new("Proof by induction", "Prove a statement for every natural number", "theorem name (n : Nat) : statement := by\n  induction n with\n  | zero => $0\n  | succ k ih => sorry"),
        new("Proof by cases", "Handle each case of an ∨", "cases h with\n  | inl h1 => $0\n  | inr h2 => sorry"),
        new("Calculation", "A chain of equalities, like on paper", "calc a = b := by $0\n  _ = c := by sorry"),
        new("Program with main", "An executable: prints a line (run it with ▶ Run)", "def main : IO Unit := do\n  IO.println \"Hello from Lean!\"$0"),
        new("For loop", "Loop over a range inside IO", "for i in [0:10] do\n  IO.println s!\"i = {i}\"$0"),
        new("Evaluate", "Run an expression and see the result", "#eval $0"),
        new("Check a type", "Ask Lean for the type of something", "#check $0"),
        new("Print axioms", "What a theorem ultimately depends on", "#print axioms $0"),
    ];
}

/// <summary>A famous or pleasing theorem: what it says in English, and its name in Lean.</summary>
public sealed record FamousTheorem(string Title, string English, string LeanName, bool NeedsMathlib, string? Story = null)
{
    /// <summary>What to put in the playground to meet it: its statement, and what it rests on.</summary>
    public string PlaygroundCode => (NeedsMathlib ? "import Mathlib\n\n" : "")
        + $"-- {Title}: {English}\n#check @{LeanName}\n#print axioms {LeanName}\n";
}

/// <summary>
/// Theorems worth meeting early. The core ones are in Lean itself, so they work in any project (the tests check
/// every name); the Mathlib ones need a project that depends on Mathlib.
/// </summary>
public static class TheoremGallery
{
    public static IReadOnlyList<FamousTheorem> All { get; } =
    [
        new("Addition is commutative", "For all natural numbers a and b, a + b = b + a.", "Nat.add_comm", false, "Proved by induction, from the definition of addition."),
        new("Multiplication distributes", "a × (b + c) = a × b + a × c, for natural numbers.", "Nat.mul_add", false),
        new("Reversing twice", "Reversing a list twice gives back the list you started with.", "List.reverse_reverse", false),
        new("Length of a joined list", "The length of xs ++ ys is the length of xs plus the length of ys.", "List.length_append", false),
        new("No number is its own successor", "For every natural number n, n + 1 ≠ n.", "Nat.succ_ne_self", false),
        new("Less-than is irreflexive", "No number is less than itself.", "Nat.lt_irrefl", false),
        new("Antisymmetry", "If a ≤ b and b ≤ a, then a = b.", "Nat.le_antisymm", false),
        new("Division with remainder", "Every n equals (n / k) × k + n % k: the quotient times k, plus the remainder.", "Nat.div_add_mod", false),
        new("gcd is symmetric", "The greatest common divisor of a and b is that of b and a.", "Nat.gcd_comm", false),
        new("The law of excluded middle", "Every statement is true or false.", "Classical.em", false, "It uses the axiom of choice: #print axioms shows it."),
        new("Infinitely many primes", "For every n there is a prime number at least n (Euclid).", "Nat.exists_infinite_primes", true, "Euclid's proof: multiply the primes you have, add one."),
        new("√2 is irrational", "The square root of 2 is not a fraction.", "irrational_sqrt_two", true),
        new("Cantor's theorem", "No function from a set onto the set of all its subsets exists: there is always a bigger infinity.", "Function.cantor_surjective", true),
        new("Fermat's little theorem", "If p is prime and a is not a multiple of p, then a^(p−1) leaves remainder 1 when divided by p.", "ZMod.pow_card_sub_one_eq_one", true),
        new("Gauss's sum", "0 + 1 + … + (n − 1), doubled, is n × (n − 1).", "Finset.sum_range_id_mul_two", true, "Lesson 7 of the tutorial proves a version of it by induction."),
    ];
}

/// <summary>
/// Runs a Lean file's <c>main</c> as a program, the way a programmer expects a Run button to work. Inside a Lake
/// project it runs with the project's dependencies (<c>lake env lean --run</c>); elsewhere with plain Lean.
/// </summary>
public static class ProgramRunner
{
    public static bool HasMain(string text) =>
        System.Text.RegularExpressions.Regex.IsMatch(text, @"(^|\n)\s*(unsafe\s+|partial\s+)*def\s+main\b");

    public static Task<ProcessResult> RunAsync(LeanProject project, string file, Action<string>? onLine = null, CancellationToken ct = default)
    {
        string lean = Elan.FindExecutable("lean") ?? "lean";
        string lake = Elan.FindExecutable("lake") ?? "lake";
        return project.IsLakeProject
            ? ProcessRunner.RunAsync(lake, ["env", "lean", "--run", file], project.Root, onLine, ct: ct)
            : ProcessRunner.RunAsync(lean, ["--run", file], project.Root, onLine, ct: ct);
    }
}

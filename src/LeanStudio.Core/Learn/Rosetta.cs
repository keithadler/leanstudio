using System.Text;

namespace LeanStudio.Core.Learn;

/// <summary>One idea written in another language and in Lean.</summary>
/// <param name="Concept">What it is.</param>
/// <param name="Theirs">The code in the other language.</param>
/// <param name="Lean">The same idea in Lean.</param>
public sealed record RosettaRow(string Concept, string Theirs, string Lean);

/// <summary>
/// "I know Python (or Haskell, JavaScript, Rust). How do I say that in Lean?" A short table for each: functions, if, lists,
/// map and filter, records, printing, and "maybe nothing". Lean is a programming language first, and most people arrive from
/// another one.
/// </summary>
public static class Rosetta
{
    /// <summary>The languages with a table.</summary>
    public static IReadOnlyList<string> Languages { get; } = ["python", "haskell", "javascript", "rust"];

    private static RosettaRow R(string c, string t, string l) => new(c, t, l);

    private static readonly Dictionary<string, RosettaRow[]> Tables = new(StringComparer.Ordinal)
    {
        ["python"] =
        [
            R("a function", "def add(a, b): return a + b", "def add (a b : Nat) : Nat := a + b"),
            R("if / else", "x if c else y", "if c then x else y"),
            R("a list", "[1, 2, 3]", "[1, 2, 3]"),
            R("map", "[x * 2 for x in xs]", "xs.map (fun x => x * 2)"),
            R("filter", "[x for x in xs if x > 1]", "xs.filter (fun x => x > 1)"),
            R("a record", "@dataclass class P: x: int; y: int", "structure P where\n  x : Int\n  y : Int"),
            R("print", "print(\"hi\")", "IO.println \"hi\""),
            R("maybe nothing", "None", "none  -- an Option, written (some 3) or none"),
            R("a loop", "for x in xs: print(x)", "for x in xs do IO.println x  -- inside a function that returns IO"),
            R("a comment", "# a comment", "-- a comment"),
        ],
        ["haskell"] =
        [
            R("a function", "add :: Int -> Int -> Int\nadd a b = a + b", "def add (a b : Int) : Int := a + b"),
            R("if / else", "if c then x else y", "if c then x else y"),
            R("a list", "[1, 2, 3]", "[1, 2, 3]"),
            R("map", "map (\\x -> x * 2) xs", "xs.map (fun x => x * 2)"),
            R("filter", "filter (> 1) xs", "xs.filter (fun x => x > 1)"),
            R("a record", "data P = P { x :: Int, y :: Int }", "structure P where\n  x : Int\n  y : Int"),
            R("print", "putStrLn \"hi\"", "IO.println \"hi\""),
            R("maybe nothing", "Nothing / Just 3", "none / some 3"),
            R("pattern match", "f 0 = 1\nf n = n * f (n - 1)", "def f : Nat → Nat\n  | 0 => 1\n  | n + 1 => (n + 1) * f n"),
            R("a comment", "-- a comment", "-- a comment"),
        ],
        ["javascript"] =
        [
            R("a function", "const add = (a, b) => a + b;", "def add (a b : Nat) : Nat := a + b"),
            R("if / else", "c ? x : y", "if c then x else y"),
            R("an array", "[1, 2, 3]", "#[1, 2, 3]  -- a list is [1, 2, 3]"),
            R("map", "xs.map(x => x * 2)", "xs.map (fun x => x * 2)"),
            R("filter", "xs.filter(x => x > 1)", "xs.filter (fun x => x > 1)"),
            R("an object", "const p = { x: 1, y: 2 };", "structure P where\n  x : Nat\n  y : Nat\ndef p : P := { x := 1, y := 2 }"),
            R("print", "console.log(\"hi\")", "IO.println \"hi\""),
            R("null / undefined", "null", "none  -- an Option: some 3 or none"),
            R("a loop", "for (const x of xs) console.log(x);", "for x in xs do IO.println x  -- inside a function that returns IO"),
            R("a comment", "// a comment", "-- a comment"),
        ],
        ["rust"] =
        [
            R("a function", "fn add(a: i64, b: i64) -> i64 { a + b }", "def add (a b : Int) : Int := a + b"),
            R("if / else", "if c { x } else { y }", "if c then x else y"),
            R("a vector", "vec![1, 2, 3]", "[1, 2, 3]"),
            R("map", "xs.iter().map(|x| x * 2)", "xs.map (fun x => x * 2)"),
            R("filter", "xs.iter().filter(|x| **x > 1)", "xs.filter (fun x => x > 1)"),
            R("a struct", "struct P { x: i64, y: i64 }", "structure P where\n  x : Int\n  y : Int"),
            R("print", "println!(\"hi\");", "IO.println \"hi\""),
            R("Option", "Some(3) / None", "some 3 / none"),
            R("match", "match n { 0 => 1, k => k }", "match n with\n  | 0 => 1\n  | k => k"),
            R("a comment", "// a comment", "-- a comment"),
        ],
    };

    /// <summary>The table for <paramref name="language"/> (python, haskell, javascript or rust; any case), or null.</summary>
    public static IReadOnlyList<RosettaRow>? For(string language) => Tables.TryGetValue(language.Trim().ToLowerInvariant(), out RosettaRow[]? t) ? t : null;

    /// <summary>The table for <paramref name="language"/> as Markdown, or null when there is none.</summary>
    public static string? ToMarkdown(string language)
    {
        if (For(language) is not { } rows)
        {
            return null;
        }
        string name = char.ToUpperInvariant(language.Trim()[0]) + language.Trim()[1..].ToLowerInvariant();
        var sb = new StringBuilder($"## Coming from {name}\n\n");
        foreach (RosettaRow r in rows)
        {
            sb.Append("**").Append(r.Concept).Append("**\n\n```text\n").Append(r.Theirs).Append("\n```\n```lean\n").Append(r.Lean).Append("\n```\n\n");
        }
        return sb.ToString().TrimEnd() + "\n";
    }
}

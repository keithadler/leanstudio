using System.Text.RegularExpressions;

namespace LeanStudio.Core.Learn;

/// <summary>What a tactic or keyword does, in plain words, with a small example.</summary>
/// <param name="Name">The tactic or keyword, as written in Lean.</param>
/// <param name="Kind"><c>tactic</c> or <c>keyword</c>.</param>
/// <param name="Explanation">What it does, for a beginner.</param>
/// <param name="Example">A short example of its use; may span lines.</param>
public sealed record GuideEntry(string Name, string Kind, string Explanation, string Example);

/// <summary>
/// Plain-English explanations of Lean's tactics and keywords, for hovers and the proof-step list. Written for
/// someone meeting Lean for the first time: what it does to the goal, when to reach for it, and one example.
/// </summary>
public static class TacticGuide
{
    /// <summary>The entry for a tactic or keyword (exact, case-sensitive), or null when there is none.</summary>
    public static GuideEntry? Explain(string word) => Entries.TryGetValue(word, out GuideEntry? e) ? e : null;

    /// <summary>The first word of a tactic line (after bullets, case arms and <c>&lt;;&gt;</c>), which names the tactic.</summary>
    public static string? TacticOf(string line)
    {
        string t = line.Trim().TrimStart('·', '.', ' ');
        if (t.StartsWith('|'))
        {
            int arrow = t.IndexOf("=>", StringComparison.Ordinal);
            t = arrow >= 0 ? t[(arrow + 2)..].Trim() : "";
        }
        Match m = Regex.Match(t, @"^[A-Za-z_][A-Za-z0-9_'!?]*\??");
        return m.Success ? m.Value : null;
    }

    private static GuideEntry T(string name, string explanation, string example) => new(name, "tactic", explanation, example);
    private static GuideEntry K(string name, string explanation, string example) => new(name, "keyword", explanation, example);

    /// <summary>Every entry, keyed by <see cref="GuideEntry.Name"/> (case-sensitive).</summary>
    public static IReadOnlyDictionary<string, GuideEntry> Entries { get; } = new[]
    {
        T("intro", "Proves \"if … then …\" or \"for all …\" by assuming the premise: moves it from the goal into your hypotheses, under the name you give.", "theorem t (p : Prop) : p → p := by\n  intro hp\n  exact hp"),
        T("intros", "Like intro, for as many premises as there are, with names Lean picks.", "intros"),
        T("exact", "Finishes the goal with a term that proves exactly it. The most common last step.", "exact hp"),
        T("apply", "Works backwards: uses a fact of the form \"… → goal\"; what the fact needs becomes your new goals.", "apply Nat.le_of_lt"),
        T("refine", "Like exact, but with holes (?_) that become new goals.", "refine ⟨?_, ?_⟩"),
        T("rfl", "Proves an equation whose two sides are the same, or compute to the same thing.", "example : 2 + 2 = 4 := by rfl"),
        T("rw", "Rewrites: given h : a = b, replaces a by b in the goal. Several at once: rw [h1, h2]. Right to left: rw [← h].", "rw [Nat.add_comm]"),
        T("simp", "Simplifies the goal with hundreds of known rules, often finishing it. Give it extra facts in brackets: simp [h].", "simp [List.length_append]"),
        T("simp_all", "Simplifies the goal and every hypothesis, using each to simplify the others.", "simp_all"),
        T("dsimp", "Simplifies only by unfolding definitions, never by rewriting with theorems.", "dsimp [double]"),
        T("omega", "Solves arithmetic with + , −, numbers, ≤, < and = over natural numbers and integers. It gives up on x * y with both unknown.", "example (a b : Nat) (h : a < b) : a + 1 ≤ b := by omega"),
        T("decide", "Proves a statement by computing its answer, for statements about specific values (2 + 2 = 4, 7 is prime…).", "example : 10 * 10 = 100 := by decide"),
        T("norm_num", "Proves numeric facts: arithmetic, inequalities and divisibility with specific numbers. (Mathlib.)", "norm_num"),
        T("ring", "Proves equations that hold by the rules of algebra: expand, collect terms, compare. (Mathlib.)", "example (a b : ℤ) : (a + b)^2 = a^2 + 2*a*b + b^2 := by ring"),
        T("linarith", "Proves a linear inequality from your hypotheses by combining them. (Mathlib.)", "linarith"),
        T("positivity", "Proves that an expression is positive or nonnegative. (Mathlib.)", "positivity"),
        T("constructor", "Splits a goal built from parts: \"p ∧ q\" becomes two goals, p and q; \"p ↔ q\" becomes both directions.", "constructor"),
        T("cases", "Splits on how something was made: a proof of p ∨ q gives two cases, a number is 0 or k + 1, and so on.", "cases h with\n  | inl hp => …\n  | inr hq => …"),
        T("rcases", "Takes apart a hypothesis in one go with a pattern: rcases h with ⟨x, hx⟩ | h'.", "rcases h with ⟨hp, hq⟩"),
        T("obtain", "Takes apart a hypothesis and names the pieces: obtain ⟨x, hx⟩ := h.", "obtain ⟨hp, hq⟩ := h"),
        T("induction", "Proves a statement for every number (or list, or tree): prove it for the base case, then for the next one assuming the previous (the induction hypothesis).", "induction n with\n  | zero => rfl\n  | succ k ih => simp [ih]"),
        T("left", "To prove \"p ∨ q\", commit to proving p.", "left"),
        T("right", "To prove \"p ∨ q\", commit to proving q.", "right"),
        T("exfalso", "Changes the goal to False: useful when your hypotheses contradict each other.", "exfalso"),
        T("contradiction", "Finishes the goal when two hypotheses contradict each other (or one is plainly false).", "contradiction"),
        T("absurd", "From h : p and h' : ¬p, proves anything.", "exact absurd hp hnp"),
        T("assumption", "Finishes the goal if one of your hypotheses is exactly it.", "assumption"),
        T("trivial", "Tries a few easy tactics (rfl, assumption, decide…) on an easy goal.", "trivial"),
        T("use", "To prove \"there exists x such that …\", proposes the x; you then prove the rest. (Mathlib.)", "use 4"),
        T("exists", "To prove \"there exists …\", gives the witness and tries to finish the rest.", "exists 4"),
        T("have", "States and proves an intermediate fact, then adds it to your hypotheses.", "have h2 : 0 < n := by omega"),
        T("show", "Restates the goal in a form you choose (it must mean the same thing); also helps readers.", "show 2 * n = n + n"),
        T("calc", "A chain of steps: a = b := proof, _ = c := proof, … Reads like a proof on paper.", "calc a = b := h1\n  _ = c := h2"),
        T("unfold", "Replaces a defined name by its definition.", "unfold double"),
        T("specialize", "Plugs values into a \"for all\" hypothesis.", "specialize h 3"),
        T("by_cases", "Splits into two cases: the statement holds, or it does not.", "by_cases h : n = 0"),
        T("by_contra", "Proof by contradiction: assume the goal is false and derive False.", "by_contra h"),
        T("push_neg", "Pushes \"not\" inward: ¬∀ becomes ∃¬, ¬(a < b) becomes b ≤ a, and so on. (Mathlib.)", "push_neg at h"),
        T("exact?", "Searches the library for a theorem that finishes the goal. Click its \"Try this\" suggestion to use it.", "exact?"),
        T("apply?", "Searches for theorems that could apply to the goal, and lists them.", "apply?"),
        T("simp?", "Runs simp, then suggests the exact simp only […] call it used, which is faster and sturdier.", "simp?"),
        T("rw?", "Searches for equations that could rewrite the goal.", "rw?"),
        T("aesop", "An automatic prover: tries many steps and backtracks. Good for goals that are \"just logic\". (Mathlib.)", "aesop"),
        T("grind", "An automatic prover for goals that follow from equalities, cases and arithmetic.", "grind"),
        T("split", "Splits a goal that contains if-then-else or match into its cases.", "split"),
        T("funext", "Proves two functions are equal by showing they agree on every input.", "funext x"),
        T("ext", "Proves two things are equal by showing they agree everywhere (functions, sets, structures). (Mathlib.)", "ext x"),
        T("sorry", "A placeholder that pretends the goal is proved. Lean warns \"declaration uses sorry\" until you replace it.", "sorry"),
        T("subst", "Given h : x = e, replaces x by e everywhere and removes x.", "subst h"),
        T("symm", "Turns a goal a = b into b = a.", "symm"),
        T("congr", "Proves f a = f b by proving a = b.", "congr 1"),
        T("repeat", "Runs a tactic again and again until it fails.", "repeat constructor"),
        T("first", "Tries tactics in order and uses the first that works.", "first | rfl | simp"),
        T("all_goals", "Runs a tactic on every open goal.", "all_goals simp"),
        T("any_goals", "Runs a tactic on every goal it works on.", "any_goals simp"),
        T("next", "Focuses on the next goal, like the · bullet.", "next => simp"),
        T("case", "Focuses on the goal with a given case name.", "case zero => rfl"),
        T("native_decide", "Like decide, but compiles the computation: fast, but trusts the compiler.", "native_decide"),
        T("clear", "Removes a hypothesis you no longer need.", "clear h"),
        T("rename_i", "Names hypotheses that Lean left unnamed (the ones shown with ✝).", "rename_i h"),
        T("norm_cast", "Moves type casts (↑n) around so that ℕ and ℤ facts line up. (Mathlib.)", "norm_cast"),
        T("gcongr", "Proves an inequality between two expressions of the same shape by comparing their parts. (Mathlib.)", "gcongr"),
        T("field_simp", "Clears denominators in equations with division. (Mathlib.)", "field_simp"),
        K("theorem", "Starts a theorem: a name, a statement after the colon, and a proof after :=. Lean checks the proof.", "theorem two_eq : 1 + 1 = 2 := rfl"),
        K("lemma", "The same as theorem (Mathlib's name for smaller results).", "lemma two_eq : 1 + 1 = 2 := rfl"),
        K("example", "A theorem without a name: for trying things out.", "example : 1 + 1 = 2 := rfl"),
        K("def", "Defines a value or a function: a name, its inputs with their types, its result type, and its body.", "def double (n : Nat) : Nat := n + n"),
        K("by", "Starts a proof written as a sequence of tactics, one per line.", "theorem t : 1 = 1 := by\n  rfl"),
        K("fun", "A function without a name: fun x => x + 1 (also written λ x => x + 1).", "(fun x => x * 2) 21"),
        K("let", "Names a value inside an expression.", "let y := x + 1; y * y"),
        K("match", "Chooses what to do by the shape of a value: one arm per case.", "match n with\n  | 0 => \"zero\"\n  | _ => \"more\""),
        K("if", "if condition then a else b.", "if n = 0 then 1 else n"),
        K("structure", "Defines a record type with named fields.", "structure Point where\n  x : Nat\n  y : Nat"),
        K("inductive", "Defines a type by listing the ways to build its values.", "inductive Color where\n  | red | green | blue"),
        K("instance", "Tells Lean how a type supports an operation (how to add, print, compare… values of it).", "instance : Add Point := ⟨fun a b => ⟨a.x + b.x, a.y + b.y⟩⟩"),
        K("namespace", "Groups names: inside namespace Foo, def bar is really Foo.bar.", "namespace Foo … end Foo"),
        K("open", "Lets you write short names: after open Nat you can write succ for Nat.succ.", "open Nat"),
        K("import", "Uses another file or library. Must come first in the file.", "import Mathlib"),
        K("variable", "Declares names (like variable (n : Nat)) that every following theorem gets as an input.", "variable (n : Nat)"),
        K("where", "Gives the fields of a structure, or helper definitions, on the lines that follow.", "def f := g 1 where g (n : Nat) := n + 1"),
        K("axiom", "Assumes a statement without proof. Everything that uses it depends on it; Tenet will point that out.", "axiom my_assumption : 1 = 2"),
        K("Prop", "The type of statements (things that can be proved).", "#check (1 = 2 : Prop)"),
        K("Nat", "The natural numbers 0, 1, 2, … (also written ℕ, type \\N).", "#eval (5 : Nat) - 7   -- 0: no negatives"),
        K("Int", "The integers …, −1, 0, 1, … (also written ℤ, type \\Z).", "#eval (5 : Int) - 7"),
    }.ToDictionary(e => e.Name, StringComparer.Ordinal);
}

/// <summary>
/// Lean's error messages, explained. Each pattern maps a message to what it means for a beginner and what to
/// try, shown under the message in hovers and in the tactic state.
/// </summary>
public static partial class ErrorGuide
{
    private static readonly (Regex Pattern, string Meaning)[] Rules =
    [
        (R(@"^unsolved goals"), "Your proof is not finished: Lean still has the goals listed to prove. Put the cursor at the end of the proof to see them, and add tactics."),
        (R(@"declaration uses .?sorry"), "This still has a sorry: a placeholder, not a proof. It is not proved until every sorry is replaced."),
        (R(@"^unknown (identifier|constant)"), "Lean does not know this name. Check the spelling and capitals; it may need its namespace (Nat.add_comm, not add_comm), an `open`, or an `import`."),
        (R(@"^type mismatch"), "The term has a different type from the one expected here. Compare the two types in the message: often an argument is missing, in the wrong order, or of the wrong kind (ℕ vs ℤ)."),
        (R(@"^application type mismatch"), "A function was given an argument of the wrong type. The message shows the argument, its type, and the type the function wanted."),
        (R(@"^failed to synthesize"), "Lean could not find how to do this operation for these types (add, compare, print…). Often a missing import, or mixing types such as ℕ and ℝ."),
        (R(@"^function expected"), "Something that is not a function was given arguments. Check the parentheses and spaces: in Lean, f x means \"f applied to x\"."),
        (R(@"(The rfl tactic failed|not definitionally equal|rfl.*failed)"), "rfl only works when both sides compute to exactly the same thing. Try simp, omega or rw with a theorem instead."),
        (R(@"decide failed|failed to reduce to true|evaluates to false"), "decide worked the statement out and it is false, or it could not compute it (it only handles statements about specific values)."),
        (R(@"omega could not prove"), "omega only handles +, −, numbers, ≤, < and = (plus / and % by constants). If x * y with two unknowns appears, or the statement is false, it cannot help."),
        (R(@"linarith failed"), "linarith could not combine your hypotheses linearly into the goal. Add the missing inequality with have, or check that the goal is true."),
        (R(@"simp made no progress"), "None of simp's rules apply here. Give it the facts it needs (simp [h, myDef]) or use another tactic."),
        (R(@"(did not find instance of the pattern|motive is not type correct|rewrite failed)"), "rw could not find the left-hand side of the equation in the goal, or rewriting would break the statement. Check the exact form of the goal; try rw [← h] or simp only [h]."),
        (R(@"^unexpected token|expected term|unexpected end of input"), "A syntax error: Lean could not read the line. Look for a missing :=, a missing bracket, or a keyword in the wrong place, at or just before the red mark."),
        (R(@"no goals to be proved|no goals"), "The proof was already complete before this step. Delete the extra tactics."),
        (R(@"(fail to show termination|failed to prove termination|structural recursion cannot be used)"), "Lean could not see that this recursive function always stops. Recurse on something smaller (n from n + 1, xs from x :: xs), or add termination_by."),
        (R(@"maximum recursion depth"), "Lean went too deep, often simp looping or a huge number. Use simp only with specific rules, or smaller values."),
        (R(@"(deterministic\) timeout|heartbeats)"), "Lean gave up because this took too long. Split the proof into smaller steps, or use more specific tactics than simp or decide."),
        (R(@"^invalid field"), "This value does not have that field or function. Hover the value to see its type, and type a dot to see what it offers."),
        (R(@"unused variable"), "A name is introduced but never used. Harmless; rename it _ or remove it to silence the warning."),
        (R(@"^cannot evaluate|could not synthesize a 'ToExpr', 'Repr', or 'ToString' instance|aborting evaluation"), "#eval could not run this: it may depend on sorry, or Lean does not know how to display the result's type."),
        (R(@"^Try this"), "Lean found something that works. Click the suggestion in the Tactic State panel (or press ⌘. / Ctrl+.) to put it in your proof."),
        (R(@"(is not a proposition|expected Prop)"), "A statement was expected here, but this is a value (like a number). A theorem's type must be something that can be true or false."),
        (R(@"^ambiguous"), "More than one name matches. Write the full name, with its namespace."),
    ];

    private static Regex R(string pattern) => new(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));

    /// <summary>
    /// A beginner's explanation of an error or warning, or null when there is none for it. The first rule whose
    /// pattern matches the message (ignoring case) wins.
    /// </summary>
    public static string? Explain(string message)
    {
        string first = message.TrimStart();
        foreach ((Regex p, string meaning) in Rules)
        {
            try
            {
                if (p.IsMatch(first))
                {
                    return meaning;
                }
            }
            catch (RegexMatchTimeoutException)
            {
            }
        }
        return null;
    }
}

/// <summary>
/// Reads a Lean statement aloud in English: "∀ (p q : Prop), p ∧ q → q ∧ p" becomes "for all propositions p
/// and q: if p and q, then q and p". A reading aid for newcomers, not a translation: it handles the logical
/// structure and the common symbols, and leaves everything else as Lean wrote it.
/// </summary>
public static partial class PlainEnglish
{
    /// <summary>
    /// The statement read in English, starting with a capital letter. Whitespace is collapsed first; parts it does
    /// not recognize are left as Lean wrote them.
    /// </summary>
    public static string Read(string statement)
    {
        string s = Regex.Replace(statement.Trim(), @"\s+", " ");
        return Capitalize(ReadProp(s));
    }

    private static string ReadProp(string s)
    {
        s = s.Trim();
        s = StripParens(s);

        // Binders: ∀ x y : T, body / ∀ (x : T) (y : U), body / ∃ x, body
        Match q = Quantifier().Match(s);
        if (q.Success)
        {
            int comma = TopLevelIndex(s, ',', q.Length);
            if (comma > 0)
            {
                string binders = s[q.Length..comma].Trim();
                string body = s[(comma + 1)..];
                bool forall = q.Groups[1].Value is "∀" or "forall";
                string who = ReadBinders(binders);
                return forall ? $"for all {who}: {ReadProp(body)}" : $"there is {Article(who)} such that {ReadProp(body)}";
            }
        }

        // Implications, right-associative: A → B → C reads "if A and B, then C".
        List<string> parts = SplitTop(s, " → ");
        if (parts.Count > 1)
        {
            string conclusion = ReadProp(parts[^1]);
            string premises = string.Join(" and ", parts.Take(parts.Count - 1).Select(ReadProp));
            return $"if {premises}, then {conclusion}";
        }
        List<string> iff = SplitTop(s, " ↔ ");
        if (iff.Count == 2)
        {
            return $"{ReadProp(iff[0])} exactly when {ReadProp(iff[1])}";
        }
        List<string> ors = SplitTop(s, " ∨ ");
        if (ors.Count > 1)
        {
            return string.Join(" or ", ors.Select(ReadProp));
        }
        List<string> ands = SplitTop(s, " ∧ ");
        if (ands.Count > 1)
        {
            return string.Join(" and ", ands.Select(ReadProp));
        }
        if (s.StartsWith('¬'))
        {
            return "it is not the case that " + ReadProp(s[1..]);
        }
        return ReadAtom(s);
    }

    private static string ReadAtom(string s)
    {
        if (s == "False")
        {
            return "a contradiction";
        }
        if (s == "True")
        {
            return "true";
        }
        foreach ((string op, string words) in Relations)
        {
            List<string> sides = SplitTop(s, op);
            if (sides.Count == 2)
            {
                return $"{sides[0].Trim()} {words} {sides[1].Trim()}";
            }
        }
        return s;
    }

    private static readonly (string Op, string Words)[] Relations =
    [
        (" ≠ ", "is not equal to"), (" ≤ ", "is at most"), (" ≥ ", "is at least"), (" < ", "is less than"),
        (" > ", "is greater than"), (" = ", "equals"), (" ∈ ", "is in"), (" ∉ ", "is not in"),
        (" ⊆ ", "is a subset of"), (" ∣ ", "divides"),
    ];

    private static string ReadBinders(string binders)
    {
        // (x y : T) (z : U)  or  x y : T  or  x
        var groups = new List<string>();
        foreach (Match m in BinderGroup().Matches(binders))
        {
            string names = m.Groups["names"].Value.Trim();
            string type = m.Groups["type"].Success ? m.Groups["type"].Value.Trim() : "";
            string[] ns = names.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            string list = ns.Length <= 1 ? names : string.Join(", ", ns[..^1]) + " and " + ns[^1];
            groups.Add(type.Length == 0 ? list : $"{TypeWord(type, ns.Length > 1)} {list}");
        }
        return groups.Count == 0 ? binders : string.Join(", and ", groups);
    }

    private static string TypeWord(string type, bool plural) => type switch
    {
        "Prop" => plural ? "propositions" : "proposition",
        "ℕ" or "Nat" => plural ? "natural numbers" : "natural number",
        "ℤ" or "Int" => plural ? "integers" : "integer",
        "ℚ" or "Rat" => plural ? "rationals" : "rational",
        "ℝ" or "Real" => plural ? "real numbers" : "real number",
        "ℂ" or "Complex" => plural ? "complex numbers" : "complex number",
        "Type" or "Type u" => plural ? "types" : "type",
        "Bool" => plural ? "booleans" : "boolean",
        "String" => plural ? "strings" : "string",
        _ => type + (plural ? "s" : ""),
    };

    /// <summary>"an" before a vowel sound: a vowel, or a single letter said with one (an n, an x, a y).</summary>
    private static string Article(string who)
    {
        if (who.Length == 0)
        {
            return who;
        }
        char c = char.ToLowerInvariant(who[0]);
        bool letterAlone = who.Length == 1 || !char.IsLetter(who[1]);
        bool vowelSound = letterAlone ? "aefhilmnorsx".Contains(c, StringComparison.Ordinal) : "aeiou".Contains(c, StringComparison.Ordinal);
        return (vowelSound ? "an " : "a ") + who;
    }

    /// <summary>Capitalize a sentence that starts with our own words; never a variable (p stays p).</summary>
    private static string Capitalize(string s) =>
        s.StartsWith("for all", StringComparison.Ordinal) || s.StartsWith("if ", StringComparison.Ordinal)
        || s.StartsWith("there is", StringComparison.Ordinal) || s.StartsWith("it is not", StringComparison.Ordinal)
        || s.StartsWith("a contradiction", StringComparison.Ordinal) || s.StartsWith("true", StringComparison.Ordinal)
            ? char.ToUpperInvariant(s[0]) + s[1..]
            : s;

    [GeneratedRegex(@"^(∀|∃|forall|exists)\s*")]
    private static partial Regex Quantifier();

    [GeneratedRegex(@"\((?<names>[^():]+)(:(?<type>[^()]+))?\)|\{(?<names>[^{}:]+)(:(?<type>[^{}]+))?\}|(?<names>[^():{}]+?)(\s*:\s*(?<type>.+))?$")]
    private static partial Regex BinderGroup();

    /// <summary>Remove parentheses that wrap the whole expression.</summary>
    private static string StripParens(string s)
    {
        while (s.StartsWith('(') && s.EndsWith(')') && MatchingClose(s, 0) == s.Length - 1)
        {
            s = s[1..^1].Trim();
        }
        return s;
    }

    private static int MatchingClose(string s, int open)
    {
        int depth = 0;
        for (int i = open; i < s.Length; i++)
        {
            if (s[i] is '(' or '[' or '{' or '⟨')
            {
                depth++;
            }
            else if (s[i] is ')' or ']' or '}' or '⟩' && --depth == 0)
            {
                return i;
            }
        }
        return -1;
    }

    /// <summary>Split on a separator only where it is outside every bracket.</summary>
    private static List<string> SplitTop(string s, string sep)
    {
        var parts = new List<string>();
        int depth = 0, start = 0;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c is '(' or '[' or '{' or '⟨')
            {
                depth++;
            }
            else if (c is ')' or ']' or '}' or '⟩')
            {
                depth--;
            }
            else if (depth == 0 && string.CompareOrdinal(s, i, sep, 0, sep.Length) == 0)
            {
                // A quantifier's body runs to the end: "A → ∀ x, B → C" splits only before the ∀.
                parts.Add(s[start..i]);
                start = i + sep.Length;
                if (s[start..].TrimStart().StartsWith('∀') || s[start..].TrimStart().StartsWith('∃'))
                {
                    break;
                }
                i = start - 1;
            }
        }
        parts.Add(s[start..]);
        return parts;
    }

    private static int TopLevelIndex(string s, char c, int from)
    {
        int depth = 0;
        for (int i = from; i < s.Length; i++)
        {
            if (s[i] is '(' or '[' or '{' or '⟨')
            {
                depth++;
            }
            else if (s[i] is ')' or ']' or '}' or '⟩')
            {
                depth--;
            }
            else if (depth == 0 && s[i] == c)
            {
                return i;
            }
        }
        return -1;
    }
}

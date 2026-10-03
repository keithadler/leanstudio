using LeanStudio.Core.Editing;
using LeanStudio.Core.Learn;

namespace LeanStudio.Tests;

/// <summary>The beginner's onramp: hints, Lean 3, surprises, plain-English declarations, a cheat sheet, symbol practice, badges, puzzles, other languages, tips.</summary>
public sealed class OnrampTests
{
    // ---- hints ----

    [Theory]
    [InlineData("∀ n : Nat, n + 0 = n", "intro x")]
    [InlineData("p ∧ q → q ∧ p", "intro h")]
    [InlineData("p ∧ q", "constructor")]
    [InlineData("p ∨ q", "left")]
    [InlineData("p ↔ q", "constructor")]
    [InlineData("∃ n : Nat, n * n = 49", "exact ⟨the_thing, proof⟩")]
    [InlineData("a ≤ b + 1", "omega")]
    [InlineData("2 + 2 = 4", "rfl")]
    [InlineData("¬ p", "intro h")]
    [InlineData("True", "trivial")]
    [InlineData("False", "contradiction")]
    [InlineData("(p ∧ q)", "constructor")]
    [InlineData("f x", "exact?")]
    public void HintsNameTheTacticForTheShapeOfTheGoal(string goal, string tactic)
    {
        IReadOnlyList<string> hints = GoalHints.For(goal);
        Assert.Equal(3, hints.Count);
        Assert.StartsWith("In words: ", hints[0], StringComparison.Ordinal);
        Assert.StartsWith("Try: ", hints[2], StringComparison.Ordinal);
        Assert.Contains(tactic, hints[2], StringComparison.Ordinal);
    }

    [Fact]
    public void HintsGoFromGentleToSpecific()
    {
        IReadOnlyList<string> hints = GoalHints.For("p ∧ q → q ∧ p");
        Assert.Equal("In words: If p and q, then q and p", hints[0]);
        Assert.Contains("\"if … then …\" goal", hints[1], StringComparison.Ordinal); // the connective that binds loosest wins, so → before ∧
    }

    // ---- Lean 3 ----

    [Fact]
    public void FindsTheLean3HabitsAndSaysWhatTheyAreNow()
    {
        const string text = "import data.nat.basic\nopen_locale classical\nvariables (a b : ℕ)\n\nlemma foo (p q : Prop) : p ∧ q → q ∧ p :=\nbegin\n  assume h,\n  cases h with hp hq,\n  exact ⟨hq, hp⟩,\nend\n\ndef f := λ x, x + 1\n";
        IReadOnlyList<Lean3Finding> found = Lean3.Find(text);
        Assert.Equal(["an import in lower case", "open_locale", "variables", "begin", "assume", "a comma after a tactic", "cases h with x hx", "a comma after a tactic", "a comma after a tactic", "end (closing a begin)", "λ x,"],
            found.Select(f => f.Found));
        Assert.Equal([0, 1, 2, 5, 6, 6, 7, 7, 8, 9, 11], found.Select(f => f.Line).Order()); // each on its own line, in order
        Assert.Contains("`fun x => body`", found[^1].Lean4, StringComparison.Ordinal);
        Assert.Contains("obtain ⟨x, hx⟩ := h", found.Single(f => f.Found == "cases h with x hx").Lean4, StringComparison.Ordinal);
    }

    [Fact]
    public void Lean4CommentsAndNamespaceEndsAreNotLean3()
    {
        const string text = "namespace Foo\n-- begin\n/- λ x, y -/\ntheorem t : 1 = 1 := by\n  rfl\nend Foo\nsection\nend\n";
        Assert.Empty(Lean3.Find(text));
    }

    // ---- surprises ----

    [Fact]
    public void WarnsAboutArithmeticSurprises()
    {
        const string text = "#eval 3 - 5\n#eval 7 / 2\n#eval 7 / 0\n#eval 8 / 2\n#eval 5 - 3\n#eval 0.1 + 0.2\n#eval 2 ^ 100\n#eval 2 ^ 10\ntheorem t (a b : Nat) : a == b := sorry\n-- #eval 1 - 2\n";
        IReadOnlyList<Gotcha> found = Gotchas.Find(text);
        Assert.Equal([0, 1, 2, 5, 6, 8], found.Select(g => g.Line));
        Assert.Equal("3 - 5 is 0, not -2", found[0].Title);
        Assert.Equal("7 / 2 is 3, not 3.5", found[1].Title);
        Assert.Equal("7 / 0 is 0", found[2].Title);
        Assert.Contains("Float", found[1].Explanation, StringComparison.Ordinal);
        Assert.Equal("natural numbers never overflow", found[4].Title);
        Assert.Equal("== in a statement", found[5].Title);
    }

    // ---- reading a declaration ----

    [Theory]
    [InlineData("def double (n : Nat) : Nat := n + n", "`double` is a function. Given n (a natural number), it gives back a natural number.")]
    [InlineData("def answer : Nat := 42", "`answer` is a value: a natural number.")]
    [InlineData("def greet (name : String) : String := \"hi \" ++ name", "`greet` is a function. Given name (a piece of text), it gives back a piece of text.")]
    [InlineData("def lengths (xs : List String) : List Nat := xs.map String.length", "`lengths` is a function. Given xs (a list of pieces of text), it gives back a list of natural numbers.")]
    [InlineData("theorem add_comm (a b : Nat) : a + b = b + a := by omega", "`add_comm` is a theorem. It says that for any a and b (each a natural number), a + b equals b + a.")]
    [InlineData("theorem foo (p q : Prop) (hp : p) (hq : q) : p ∧ q := ⟨hp, hq⟩", "`foo` is a theorem. It says that for any p and q (each a statement), if p and q, then p and q.")]
    [InlineData("example : 2 + 2 = 4 := rfl", "This example states that 2 + 2 equals 4.")]
    [InlineData("structure Point where\n  x : Nat\n  y : Nat", "`Point` is a structure: a record that bundles together x (a natural number) and y (a natural number).")]
    [InlineData("inductive Color where\n  | red\n  | green\n  | blue", "`Color` is a type with 3 ways to build a value: `red`, `green`, `blue`.")]
    public void ReadsADeclarationInPlainEnglish(string declaration, string expected) => Assert.Equal(expected, DeclExplain.Read(declaration));

    [Fact]
    public void FindsTheDeclarationAroundALine()
    {
        const string text = "def x := 1\n\n/-- doc -/\ndef double (n : Nat) : Nat :=\n  n + n\n\n-- a comment\n";
        Assert.Equal("`double` is a function. Given n (a natural number), it gives back a natural number.", DeclExplain.ReadAt(text, 4)); // inside its body
        Assert.Equal("`double` is a function. Given n (a natural number), it gives back a natural number.", DeclExplain.ReadAt(text, 3));
        Assert.Null(DeclExplain.ReadAt(text, 5)); // a blank line belongs to nothing
        Assert.Null(DeclExplain.ReadAt(text, 6)); // a comment is not a declaration
        Assert.Null(DeclExplain.Read("#eval 1"));
    }

    // ---- cheat sheet ----

    [Fact]
    public void TheCheatSheetListsWhatWasUsedInOrderAndSkipsComments()
    {
        const string text = "-- exact is only in this comment\ntheorem t (p q : Prop) : p ∧ q → q ∧ p := by\n  intro h\n  simp\n  intro h\n  rfl\n";
        Assert.Equal(["theorem", "intro", "simp", "rfl"], CheatSheet.Used(text).Select(e => e.Name));
        string md = CheatSheet.ToMarkdown(text);
        Assert.StartsWith("# My Lean cheat sheet\n\nEverything below is something you used", md, StringComparison.Ordinal);
        Assert.True(md.IndexOf("## Tactics", StringComparison.Ordinal) < md.IndexOf("## Keywords", StringComparison.Ordinal));
        Assert.Contains("### `intro`", md, StringComparison.Ordinal);
        Assert.DoesNotContain("### `exact`", md, StringComparison.Ordinal);
        Assert.Contains("Nothing here yet", CheatSheet.ToMarkdown("#eval 1\n"), StringComparison.Ordinal);
    }

    // ---- symbol practice ----

    [Fact]
    public void EverySymbolAbbreviationIsInTheRealTable()
    {
        Assert.True(SymbolTrainer.Symbols.Count >= 15);
        Assert.All(SymbolTrainer.Symbols, s => Assert.NotEqual("?", s.Symbol));
        Assert.Equal("∀", SymbolTrainer.Symbols.Single(s => s.Abbreviation == "forall").Symbol);
        Assert.Equal(SymbolTrainer.Symbols.Count, SymbolTrainer.Symbols.Select(s => s.Abbreviation).Distinct().Count());
    }

    [Fact]
    public void AWorksheetIsMarkedLineByLine()
    {
        string sheet = SymbolTrainer.Worksheet(4, 0);
        Assert.Equal(4, sheet.Split('\n').Count(l => l.Contains("(type \\", StringComparison.Ordinal)));
        (int none, int noneAnswered, IReadOnlyList<string> noneWrong) = SymbolTrainer.Grade(sheet); // nothing typed yet
        Assert.Equal((0, 0), (none, noneAnswered));
        Assert.Empty(noneWrong);
        string done = sheet.Replace("(type \\forall)  →  ", "(type \\forall)  →  ∀").Replace("(type \\exists)  →  ", "(type \\exists)  →  ∀");
        (int right, int answered, IReadOnlyList<string> wrong) = SymbolTrainer.Grade(done);
        Assert.Equal((1, 2), (right, answered));
        Assert.Equal("there exists: you typed ∀; it is ∃ (type \\exists and a space)", Assert.Single(wrong));
        Assert.NotEqual(SymbolTrainer.Worksheet(4, 0), SymbolTrainer.Worksheet(4, 3)); // another seed practises other symbols
        Assert.Equal(SymbolTrainer.Symbols.Count, SymbolTrainer.Worksheet(500, 1).Split('\n').Count(l => l.Contains("(type \\", StringComparison.Ordinal)));
    }

    // ---- badges ----

    [Fact]
    public void BadgesAreEarnedFromTheFilesAndTheFirstMissingOneIsNext()
    {
        IReadOnlyList<(Badge Badge, bool Earned)> none = Achievements.Earned([]);
        Assert.All(none, e => Assert.False(e.Earned));
        Assert.Equal("Hello, Lean", Achievements.Next(none)!.Title);

        IReadOnlyList<(Badge Badge, bool Earned)> some = Achievements.Earned(["#eval 1\n#check 1\ndef f := 1\nif true then 1 else 2\n", "theorem t : 1 = 1 := by\n  rfl\n"]);
        Assert.Equal(["hello", "check", "def", "if", "theorem", "proved"], some.Where(e => e.Earned).Select(e => e.Badge.Id));
        Assert.Equal("Pattern matcher", Achievements.Next(some)!.Title);
    }

    [Fact]
    public void AProofCountsOnlyInAFileWithoutSorryAndCommentsEarnNothing()
    {
        Assert.DoesNotContain(Achievements.Earned(["theorem t : 1 = 1 := by\n  sorry\n"]), e => e.Badge.Id == "proved" && e.Earned);
        Assert.DoesNotContain(Achievements.Earned(["-- #eval 1\n/- induction n -/\n"]), e => e.Earned);
        IReadOnlyList<(Badge Badge, bool Earned)> all = Achievements.Earned(["#eval 1", "#check 1", "def f := 1", "if a then b", "match n with", "structure P where", "∀ x", "theorem t : 1 = 1 := by\n  intro h\n  simp\n  rw [a]\n  constructor\n  cases h\n  omega\n  induction n\n  calc a = b := c"]);
        Assert.All(all, e => Assert.True(e.Earned, e.Badge.Id));
        Assert.Null(Achievements.Next(all));
        Assert.StartsWith("17 of 17 badges", Achievements.ToText(all), StringComparison.Ordinal);
        Assert.Equal(17, Achievements.All.Count);
    }

    // ---- puzzles ----

    [Fact]
    public void EveryPuzzleHasOneSorryThreeHintsAndASolutionWithout()
    {
        Assert.True(Puzzles.All.Count >= 12);
        foreach (Puzzle p in Puzzles.All)
        {
            Assert.Equal(1, p.Statement.Split("sorry").Length - 1);
            Assert.Equal(3, p.Hints.Count);
            Assert.DoesNotContain("sorry", p.Solved, StringComparison.Ordinal);
            Assert.InRange(p.Level, 1, 3);
            Assert.False(string.IsNullOrWhiteSpace(p.Title));
        }
        Assert.Equal(Puzzles.All.Count, Puzzles.All.Select(p => p.Title).Distinct().Count());
        Assert.Equal(Puzzles.All.OrderBy(p => p.Level).Select(p => p.Title), Puzzles.All.Select(p => p.Title)); // easiest first
    }

    [Fact]
    public void APuzzleFileRemembersWhichPuzzleItIs()
    {
        string file = Puzzles.File(2);
        Assert.StartsWith("-- Puzzle 3 of 12: Both at once", file, StringComparison.Ordinal);
        Assert.Equal(2, Puzzles.IndexOf(file));
        Assert.Equal(2, Puzzles.IndexOf(file.Replace("sorry", "exact ⟨hp, hq⟩", StringComparison.Ordinal)));
        Assert.Null(Puzzles.IndexOf("example : 1 = 1 := rfl"));
        Assert.Null(Puzzles.IndexOf("-- Puzzle 99 of 12: nope"));
        Assert.Equal(Puzzles.Daily(new DateOnly(2026, 10, 3)), Puzzles.Daily(new DateOnly(2026, 10, 3)));
        Assert.NotEqual(Puzzles.Daily(new DateOnly(2026, 10, 3)), Puzzles.Daily(new DateOnly(2026, 10, 4)));
    }

    [Fact]
    public void EveryPuzzleStartsWithOnlyItsSorryAndIsSolvable()
    {
        Lean.RequireLean();
        string dir = Directory.CreateTempSubdirectory("leanstudio-puzzles").FullName;
        try
        {
            File.WriteAllText(Path.Combine(dir, "lean-toolchain"), Lean.Toolchain + "\n");
            for (int i = 0; i < Puzzles.All.Count; i++)
            {
                File.WriteAllText(Path.Combine(dir, "Start.lean"), Puzzles.File(i));
                string start = Lean.RunLean(dir, "Start.lean");
                Assert.DoesNotContain("error", start, StringComparison.Ordinal);
                Assert.Contains("declaration uses 'sorry'", start, StringComparison.Ordinal);

                File.WriteAllText(Path.Combine(dir, "Solved.lean"), Puzzles.All[i].Solved);
                string solved = Lean.RunLean(dir, "Solved.lean");
                Assert.True(solved.Trim().Length == 0, $"puzzle {i + 1} ({Puzzles.All[i].Title}): {solved}");
            }
        }
        finally
        {
            Lean.DeleteTree(dir);
        }
    }

    // ---- other languages ----

    [Fact]
    public void EveryLanguageHasTheSameIdeasWrittenOutInLean()
    {
        Assert.Equal(["python", "haskell", "javascript", "rust"], Rosetta.Languages);
        foreach (string language in Rosetta.Languages)
        {
            IReadOnlyList<RosettaRow> rows = Rosetta.For(language)!;
            Assert.True(rows.Count >= 8, language);
            Assert.All(rows, r => Assert.False(string.IsNullOrWhiteSpace(r.Theirs) || string.IsNullOrWhiteSpace(r.Lean) || string.IsNullOrWhiteSpace(r.Concept)));
            Assert.Contains(rows, r => r.Lean.Contains("IO.println", StringComparison.Ordinal));
            Assert.Contains(rows, r => r.Concept.Contains("function", StringComparison.Ordinal));
        }
        Assert.Same(Rosetta.For("python"), Rosetta.For("  Python "));
        Assert.Null(Rosetta.For("cobol"));
        Assert.Null(Rosetta.ToMarkdown("cobol"));
        string md = Rosetta.ToMarkdown("Haskell")!;
        Assert.StartsWith("## Coming from Haskell\n\n**a function**", md, StringComparison.Ordinal);
        Assert.Contains("```lean\ndef add (a b : Int) : Int := a + b\n```", md, StringComparison.Ordinal);
    }

    // ---- tips ----

    [Fact]
    public void ThereIsAGoodTipForEveryDayOfAMonth()
    {
        Assert.True(Tips.All.Count >= 25);
        Assert.Equal(Tips.All.Count, Tips.All.Distinct().Count());
        Assert.All(Tips.All, t => Assert.InRange(t.Length, 20, 220));
        var days = Enumerable.Range(0, Tips.All.Count).Select(i => Tips.ForDate(new DateOnly(2026, 10, 1).AddDays(i))).ToList();
        Assert.Equal(Tips.All.Count, days.Distinct().Count()); // a full cycle shows every tip once
        Assert.Equal(Tips.ForDate(new DateOnly(2026, 10, 3)), Tips.ForDate(new DateOnly(2026, 10, 3)));
    }

    [Fact]
    public void EveryTypedAbbreviationInTheTipsIsReal()
    {
        foreach (string word in new[] { "forall", "exists", "to", "and", "or", "not", "le", "ne" })
        {
            Assert.NotNull(Abbreviations.Lookup(word));
        }
    }
}

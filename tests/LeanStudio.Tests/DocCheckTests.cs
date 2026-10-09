using LeanStudio.Core.Workflow;

namespace LeanStudio.Tests;

public sealed class DocCheckTests
{
    private static string[] Rules(string text, bool strict = false) => [.. DocCheck.Find(text, null, strict).Select(p => $"{p.Line}:{p.Rule}")];

    [Fact]
    public void FlagsAnEquivalenceClaimNoTheoremBacksUp()
    {
        // The shape from the thread: prose says it is the standard notion, and nothing proves it.
        const string text = "/-- An element is irreducible: equivalent to `Irreducible` in Mathlib. -/\ndef MyIrred {R : Type} [CommRing R] (x : R) : Prop := True\n";
        Assert.Equal(["1:doc-claim-unproved"], Rules(text));
    }

    [Fact]
    public void LeavesAnEquivalenceClaimWithATheoremAlone()
    {
        const string text = "/-- An element is irreducible: equivalent to `Irreducible` in Mathlib. -/\ndef MyIrred {R : Type} [CommRing R] (x : R) : Prop := True\n\ntheorem myIrred_iff {R : Type} [CommRing R] (x : R) : MyIrred x ↔ Irreducible x := by sorry\n";
        Assert.Empty(Rules(text));
    }

    [Fact]
    public void FlagsGeneralityTheDefinitionLacks()
    {
        const string text = "/-- The valuation of an element, for any commutative ring. -/\ndef val {R : Type} [CommRing R] [IsDomain R] (x : R) : ℕ := 0\n";
        Assert.Equal(["1:doc-overclaims-generality"], Rules(text));
    }

    [Fact]
    public void LeavesAnAdmittedHypothesisAlone()
    {
        const string text = "/-- The valuation of an element of any integral domain. -/\ndef val {R : Type} [CommRing R] [IsDomain R] (x : R) : ℕ := 0\n";
        Assert.Empty(Rules(text));
    }

    [Fact]
    public void IgnoresDocstringsThatClaimNothing()
    {
        Assert.Empty(Rules("/-- The number of divisors. -/\ndef nd (n : ℕ) : ℕ := 0\n"));
        Assert.Empty(Rules("def undocumented (n : ℕ) : ℕ := n\n"));
        Assert.Empty(Rules(""));
    }

    [Fact]
    public void StandardWithoutAReferenceIsStrictOnly()
    {
        const string text = "/-- The standard definition of a gadget. -/\ndef Gadget (n : ℕ) : Prop := True\n";
        Assert.Empty(Rules(text));
        Assert.Equal(["1:doc-claim-unanchored"], Rules(text, strict: true));
    }

    [Fact]
    public void ReadsTheTheoremsOfAWholeScope()
    {
        const string defs = "/-- Same as `Foo.bar`. -/\ndef Mine (n : ℕ) : Prop := True\n";
        var scope = new DocCheck.Scope();
        scope.Add(defs);
        Assert.Equal(["1:doc-claim-unproved"], DocCheck.Find(defs, scope).Select(p => $"{p.Line}:{p.Rule}"));
        scope.Add("theorem mine_iff (n : ℕ) : Mine n ↔ Foo.bar n := by sorry\n");
        Assert.Empty(DocCheck.Find(defs, scope));
    }

    [Fact]
    public void SkipsAttributeLinesBetweenDocAndDefinition()
    {
        const string text = "/-- Equals `Irreducible`. -/\n@[simp]\ndef Q (n : ℕ) : Prop := True\n";
        Assert.Equal(["2:doc-claim-unproved"], Rules(text));
    }
}

public sealed class DocCheckThreadTests
{
    // The definitions from the thread on frontier models smuggling in an incorrect definition under a correct docstring.
    private const string Before = "/-- A nonzero vector is primitive when its coordinates have no common factor,\nequivalently when `L / Zv` is torsion free. -/\ndef IsPrimitive (v : Fin 12 → Z) : Prop :=\n  v ≠ 0 ∧ ∀ (d : Z) (w : Fin 12 → Z), v = d • w → IsUnit d\n";

    private const string After = "/-- A sublattice is primitive (saturated) when the quotient by it is torsion free. -/\ndef IsPrimitiveSubmodule (N : Submodule Z (Fin 12 → Z)) : Prop :=\n  ∀ (d : Z) (v : Fin 12 → Z), d ≠ 0 → d • v ∈ N → v ∈ N\n\n/-- A nonzero vector is primitive when the line it spans is a primitive sublattice. -/\ndef IsPrimitive (v : Fin 12 → Z) : Prop :=\n  v ≠ 0 ∧ IsPrimitiveSubmodule (Submodule.span Z {v})\n";

    [Fact]
    public void FlagsTheEquivalentlyClaimNothingProves()
    {
        Assert.Equal(["2:doc-claim-unproved"], DocCheck.Find(Before).Select(p => $"{p.Line}:{p.Rule}"));
    }

    [Fact]
    public void AProvedEquivalenceSilencesIt()
    {
        string proved = Before + "\ntheorem isPrimitive_iff_torsionFree (v : Fin 12 → Z) : IsPrimitive v ↔ TorsionFree v := by sorry\n";
        Assert.Empty(DocCheck.Find(proved));
    }

    [Fact]
    public void SeesTheDefinitionChangeThatNoDocstringCheckCan()
    {
        const string theorems = "theorem primitive_scale (v : Fin 12 → Z) (h : IsPrimitive v) : v ≠ 0 := by sorry\n";
        IReadOnlyList<DefinitionBody> before = DefinitionDrift.Definitions("A.lean", Before + theorems);
        IReadOnlyList<DefinitionBody> after = DefinitionDrift.Definitions("A.lean", After + theorems);
        IReadOnlyList<TheoremStatement> statements = DuplicateStatements.Statements("A.lean", After + theorems);
        ChangedDefinition changed = Assert.Single(DefinitionDrift.Compare(before, after, statements));
        Assert.Equal("IsPrimitive", changed.Name);
        Assert.True(changed.DocChanged);
        Assert.Equal(["primitive_scale"], changed.Theorems);
    }

    [Fact]
    public void ARewordedDefinitionIsNotDrift()
    {
        const string a = "def f (n : ℕ) : ℕ :=\n  n + 1\n";
        const string b = "def f (k : ℕ) : ℕ := k   +   1 -- same thing\n";
        Assert.Empty(DefinitionDrift.Compare(DefinitionDrift.Definitions("A.lean", a), DefinitionDrift.Definitions("A.lean", b), []));
    }
}

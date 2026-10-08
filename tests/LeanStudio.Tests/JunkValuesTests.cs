using LeanStudio.Core.Workflow;

namespace LeanStudio.Tests;

public sealed class JunkValuesTests
{
    private static string[] Rules(string text, bool divisions = true) => [.. JunkValues.Find(text, divisions).Select(p => $"{p.Line}:{p.Rule}")];

    [Fact]
    public void FlagsSInfInADefinition()
    {
        // The case from "Navier-Stokes lost in translation", Example 4.2: sInf of an empty set is 0, so r_e is 1.
        const string text = "def B_e (n : Nat) : Prop := ∀ x1 x2 : Nat, x1 + x2 ≠ n\n\nnoncomputable def n_e : Nat := sInf {n : Nat | B_e n}\n\nnoncomputable def r_e : Rat := 1 / (n_e + 1)\n";
        Assert.Equal(["2:junk-infsup"], Rules(text));
        Assert.Equal(["2:junk-infsup"], Rules(text, divisions: false));
    }

    [Fact]
    public void DivisionIsOffByDefault()
    {
        const string text = "theorem t (x y : ℝ) : x / y * y = x := by sorry\n";
        Assert.Empty(JunkValues.Find(text));
        Assert.Single(JunkValues.Find(text, divisions: true));
    }

    [Fact]
    public void IgnoresSInfInAProofAndInComments()
    {
        const string text = "theorem t (s : Set ℕ) (h : s.Nonempty) : sInf s ∈ s := by\n  exact Nat.sInf_mem h\n\n-- sInf is junk on ∅\n/-- Uses sInf. -/\ndef ok : ℕ := 1\n";
        // The statement of `t` mentions sInf, so it is flagged once; the proof, the comment and the doc comment are not.
        Assert.Equal(["0:junk-infsup"], Rules(text));
    }

    [Fact]
    public void FlagsDivisionByAnUnguardedVariable()
    {
        Assert.Equal(["0:junk-division"], Rules("theorem t (x y : ℝ) : x / y * y = x := by sorry\n"));
    }

    [Fact]
    public void LeavesGuardedDivisionAlone()
    {
        Assert.Empty(Rules("theorem t (x y : ℝ) (hy : y ≠ 0) : x / y * y = x := by sorry\n"));
        Assert.Empty(Rules("theorem t (x y : ℝ) (hy : 0 < y) : x / y * y = x := by sorry\n"));
        Assert.Empty(Rules("theorem t (x y : ℝ) [NeZero y] : x / y * y = x := by sorry\n"));
    }

    [Fact]
    public void LeavesDivisionByALiteralAndCommentsAlone()
    {
        Assert.Empty(Rules("theorem t (x : ℝ) : x / 2 + x / 2 = x := by sorry\n"));
        Assert.Empty(Rules("/- x / y is junk at y = 0 -/\ndef a := 1\n"));
        Assert.Empty(Rules("def a := 1 -- b / c\n"));
    }

    [Fact]
    public void FlagsDefaultReturningAccess()
    {
        Assert.Equal(["0:junk-default-access"], Rules("def first (xs : List ℕ) : ℕ := xs.head!\n"));
        Assert.Equal(["0:junk-default-access"], Rules("def nth (xs : Array ℕ) (i : ℕ) : ℕ := xs[i]!\n"));
    }

    [Fact]
    public void ReadsOnlyTheStatementOfATheorem()
    {
        Assert.Empty(Rules("theorem t (x y : ℝ) (hy : y ≠ 0) : x = x := by\n  have := x / y\n  rfl\n"));
        Assert.Empty(Rules("theorem t : 1 = 1 :=\n  rfl\n"));
    }

    [Fact]
    public void HandlesEmptyAndOddInput()
    {
        Assert.Empty(Rules(""));
        Assert.Empty(Rules("\n\n"));
        Assert.Empty(Rules("def a := 1\r\ndef b := 2\r\n"));
    }
}

using LeanStudio.Core.Workflow;

namespace LeanStudio.Tests;

/// <summary>Proofs that are the same up to the names of what they introduce.</summary>
public sealed class SimilarProofsTests
{
    private static string Theorem(string name, string x, string y) =>
        $"theorem {name} ({x} {y} : \u2115) : {x} \u2264 {y} \u2192 {x} \u2264 {y} + 1 := by\n" +
        $"  intro h\n  rcases h with \u27e8{x}1, {y}1\u27e9\n  exact Nat.le_trans (Nat.add_le_add {x}1 {y}1) (Nat.le_succ _)\n\n";

    [Fact]
    public void ReadsTheProofOfEachTheorem()
    {
        IReadOnlyList<TheoremProof> found = SimilarProofs.Proofs("F.lean", "-- theorem hidden : 1 = 1 := rfl\n" + Theorem("t", "m", "n") + "def d := 1\n");
        TheoremProof t = Assert.Single(found);
        Assert.Equal(("t", 1), (t.Name, t.Line));
        Assert.Contains("Nat.le_trans", t.Tokens);
        Assert.DoesNotContain("def", t.Tokens);
    }

    [Fact]
    public void NumbersTheNamesAProofIntroduces()
    {
        IReadOnlyList<string> a = SimilarProofs.Tokens("(m n : \u2115) : m \u2264 n", "by\n  intro h\n  exact foo m n h\n");
        IReadOnlyList<string> b = SimilarProofs.Tokens("(x y : \u2115) : x \u2264 y", "by\n  intro k\n  exact foo x y k\n");
        Assert.Equal(a, b);
        Assert.Contains("foo", a); // a constant keeps its name
    }

    [Fact]
    public void FindsCopiesAndLeavesDifferentProofsAlone()
    {
        string text = Theorem("one", "a", "b") + Theorem("two", "p", "q") +
            "theorem other (n : \u2115) : n + 0 = n := by\n  induction n with\n  | zero => rfl\n  | succ k ih => simp [Nat.add_zero, Nat.succ_eq_add_one, ih, Nat.zero_add, Nat.add_assoc]\n";
        IReadOnlyList<ProofCluster> groups = SimilarProofs.Find(SimilarProofs.Proofs("F.lean", text), minTokens: 10);
        ProofCluster g = Assert.Single(groups);
        Assert.Equal(["one", "two"], g.Members.Select(m => m.Name));
        Assert.Equal(1.0, g.Similarity);
        Assert.True(g.SameStatement);
    }

    [Fact]
    public void SkipsProofsTooShortToSay()
    {
        string text = "theorem a (n : \u2115) : n = n := rfl\ntheorem b (m : \u2115) : m = m := rfl\n";
        Assert.Empty(SimilarProofs.Find(SimilarProofs.Proofs("F.lean", text)));
    }
}

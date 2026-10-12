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

    [Fact]
    public void RecognisesAFamilyWrittenOnPurpose()
    {
        Assert.True(SimilarProofs.IsVariantFamily(["Array.sum_push", "Array.prod_push"]));
        Assert.True(SimilarProofs.IsVariantFamily(["max_add_left", "max_add_right", "max_add_mid"]));
        Assert.False(SimilarProofs.IsVariantFamily(["max_add_left", "min_mul_right"]));
        Assert.False(SimilarProofs.IsVariantFamily(["foo_bar", "foo_bar_baz"]));
    }

    [Fact]
    public void SaysHowTheStatementsDiffer()
    {
        var a = new TheoremProof("F.lean", 0, "Ioo_subset", "Ioo a b \u2286 s", []);
        var b = new TheoremProof("F.lean", 5, "Ico_subset", "Ico a b \u2286 s", []);
        Assert.Equal("Ico_subset: Ioo \u2192 Ico", SimilarProofs.Differences([a, b]));
        Assert.Equal("", SimilarProofs.Differences([a, a with { Name = "again" }]));
    }

    [Fact]
    public void ListsTheGroupThatWouldSaveMostFirst()
    {
        string text = Theorem("one_x", "a", "b") + Theorem("one_y", "p", "q") + Theorem("big_one", "m", "n") + Theorem("big_two", "u", "v") +
            "theorem solo (n : \u2115) : n = n := by\n  intro\n";
        IReadOnlyList<ProofCluster> groups = SimilarProofs.Find(SimilarProofs.Proofs("F.lean", text), minTokens: 10);
        Assert.All(groups, g => Assert.True(g.Savings > 0));
        Assert.Equal(groups.OrderByDescending(g => g.Savings).ThenBy(g => 0).Select(g => g.Savings), groups.Select(g => g.Savings));
    }
}

using LeanStudio.Core.Workflow;

namespace LeanStudio.Tests;

/// <summary>A leanblueprint blueprint checked against what Lean built.</summary>
public sealed class BlueprintTests
{
    private const string Tex = """
        \chapter{Basics}
        \begin{definition}[Doubling]\label{def:double}\lean{double}\leanok
        Twice a number. % \lean{Commented.Out}
        \end{definition}

        \begin{lemma}\label{lem:double-even}
          \lean{double_even}\leanok
          \uses{def:double}
          $2n$ is even.
        \end{lemma}
        \begin{proof}\leanok
          Obvious.
        \end{proof}

        \begin{theorem}[Main]\label{thm:main}\lean{main, main'}\leanok
        The main theorem.
        \end{theorem}
        \begin{proof}
          Later.
        \end{proof}

        \begin{lemma}\label{lem:lied}\lean{lied}\leanok
        Says done.
        \end{lemma}
        \begin{proof}\leanok
        \end{proof}

        \begin{lemma}\label{lem:future}
        Nobody has started this.
        \end{lemma}

        \begin{lemma}\label{lem:gone}\lean{renamed_away}\leanok
        \end{lemma}
        """;

    [Fact]
    public void ReadsTheBlueprintsNodes()
    {
        IReadOnlyList<BlueprintNode> nodes = Blueprint.Parse(Tex, "content.tex");
        Assert.Equal(["def:double", "lem:double-even", "thm:main", "lem:lied", "lem:future", "lem:gone"], nodes.Select(n => n.Label));
        BlueprintNode d = nodes[0];
        Assert.Equal(("definition", "Doubling", true, 1), (d.Kind, d.Title, d.StatementOk, d.Line));
        Assert.Equal(["double"], d.LeanNames); // the commented-out \lean doesn't count
        Assert.True(nodes[1].ProofOk);
        Assert.Equal(["main", "main'"], nodes[2].LeanNames);
        Assert.False(nodes[2].ProofOk);
    }

    [Fact]
    public void ChecksItAgainstLean()
    {
        var lean = new Dictionary<string, LeanStatus>
        {
            ["double"] = LeanStatus.Proved, ["double_even"] = LeanStatus.Proved, ["main"] = LeanStatus.Proved,
            ["main'"] = LeanStatus.Proved, ["lied"] = LeanStatus.Sorry,
        };
        IReadOnlyList<BlueprintCheck> checks = Blueprint.Check(Blueprint.Parse(Tex, "content.tex"), n => lean.GetValueOrDefault(n, LeanStatus.Missing));
        Assert.Equal(
            [
                ("def:double", "done", false),
                ("lem:double-even", "done", false),
                ("thm:main", "proved in Lean (add \\leanok to the proof)", false),
                ("lem:lied", "proof marked \\leanok, but lied rests on sorry", true),
                ("lem:future", "not started", false),
                ("lem:gone", "marked \\leanok, but Lean has no renamed_away", true),
            ],
            checks.Select(c => (c.Node.Label, c.Verdict, c.Disagrees)));
    }
}

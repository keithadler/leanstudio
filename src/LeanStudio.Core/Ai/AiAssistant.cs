using System.Text;
using LeanStudio.Lsp;

namespace LeanStudio.Core.Ai;

/// <summary>What the editor shows around the cursor, which a question to the AI is about.</summary>
/// <param name="Path">The file.</param>
/// <param name="Text">The file's text.</param>
/// <param name="Line">The cursor's 0-based line.</param>
/// <param name="Column">The cursor's 0-based column.</param>
/// <param name="Goal">The tactic state at the cursor, if there is one.</param>
/// <param name="Messages">Lean's messages on or near the cursor's line.</param>
/// <param name="Selection">The selected text, if any.</param>
public sealed record EditorContext(string Path, string Text, int Line, int Column, string? Goal, IReadOnlyList<Diagnostic> Messages, string? Selection = null);

/// <summary>
/// Questions about Lean to a model: explain a message, explain a goal, or chat about the code at the cursor. The
/// prompts say what the editor shows, cut to fit the model, and ask for code in <c>```lean</c> blocks so the editor
/// can offer to insert it.
/// </summary>
public static class AiAssistant
{
    /// <summary>The instructions for every conversation.</summary>
    public const string SystemPrompt =
        """
        You are an expert in Lean 4 and Mathlib working inside Lean Studio, an IDE for Lean. Be concise and concrete.
        Put Lean code in ```lean blocks. Lean 4 syntax only (never Lean 3). Only use lemma names you are sure exist; if unsure, say so and suggest how to search (exact?, apply?, Loogle).
        Lean checks everything: never claim something is proved; the person will see Lean's verdict.
        """;

    /// <summary>
    /// A user message that shows the model the code around the cursor, the goal, and Lean's messages there, then asks
    /// <paramref name="question"/>. It is cut to leave room for the answer.
    /// </summary>
    /// <param name="c">What the editor shows.</param>
    /// <param name="question">The person's question.</param>
    /// <param name="tokens">How many tokens the message may take.</param>
    public static string WithContext(EditorContext c, string question, int tokens)
    {
        int budget = Math.Max(200, tokens - AiText.EstimateTokens(question) - 60);
        string goal = c.Goal is { Length: > 0 } g ? AiText.Fit(g, budget / 3) : "";
        string messages = string.Join("\n", c.Messages.Take(6).Select(d =>
            $"line {d.Range.Start.Line + 1}: {Severity(d.Severity)}: {AiText.Fit(d.Message.Trim(), 300)}"));
        messages = AiText.Fit(messages, budget / 4);
        string selection = c.Selection is { Length: > 0 } s ? AiText.Fit(s, budget / 4) : "";
        int codeBudget = budget - AiText.EstimateTokens(goal) - AiText.EstimateTokens(messages) - AiText.EstimateTokens(selection);
        string code = Around(c.Text, c.Line, codeBudget);

        var sb = new StringBuilder();
        sb.Append("File: ").Append(System.IO.Path.GetFileName(c.Path)).Append(", cursor on line ").Append(c.Line + 1).Append(".\n\n");
        sb.Append("```lean\n").Append(code).Append("\n```\n");
        if (selection.Length > 0)
        {
            sb.Append("\nSelected:\n```lean\n").Append(selection).Append("\n```\n");
        }
        if (goal.Length > 0)
        {
            sb.Append("\nGoal at the cursor:\n```lean\n").Append(goal).Append("\n```\n");
        }
        if (messages.Length > 0)
        {
            sb.Append("\nLean's messages:\n").Append(messages).Append('\n');
        }
        sb.Append('\n').Append(question);
        return sb.ToString();
    }

    /// <summary>
    /// The lines around <paramref name="line"/>, marked with <c>-- ← cursor</c>, as many as fit in
    /// <paramref name="tokens"/>, twice as many above the cursor as below (the code before it is what it builds on).
    /// </summary>
    public static string Around(string text, int line, int tokens)
    {
        string[] lines = text.Replace("\r", "", StringComparison.Ordinal).Split('\n');
        line = Math.Clamp(line, 0, Math.Max(0, lines.Length - 1));
        if (lines.Length == 0)
        {
            return "";
        }
        int lo = line, hi = line, used = AiText.EstimateTokens(lines[line]) + 4;
        while (true)
        {
            bool grew = false;
            for (int k = 0; k < 2 && lo > 0; k++)
            {
                int t = AiText.EstimateTokens(lines[lo - 1]) + 1;
                if (used + t > tokens)
                {
                    break;
                }
                used += t;
                lo--;
                grew = true;
            }
            if (hi + 1 < lines.Length)
            {
                int t = AiText.EstimateTokens(lines[hi + 1]) + 1;
                if (used + t <= tokens)
                {
                    used += t;
                    hi++;
                    grew = true;
                }
            }
            if (!grew)
            {
                break;
            }
        }
        var sb = new StringBuilder();
        if (lo > 0)
        {
            sb.Append("-- … (").Append(lo).Append(" lines above)\n");
        }
        for (int i = lo; i <= hi; i++)
        {
            sb.Append(lines[i]);
            if (i == line)
            {
                sb.Append("  -- ← cursor");
            }
            sb.Append('\n');
        }
        if (hi + 1 < lines.Length)
        {
            sb.Append("-- … (").Append(lines.Length - hi - 1).Append(" lines below)\n");
        }
        return sb.ToString().TrimEnd();
    }

    private static string Severity(DiagnosticSeverity s) => s switch
    {
        DiagnosticSeverity.Error => "error",
        DiagnosticSeverity.Warning => "warning",
        _ => "info",
    };

    /// <summary>The question for AI ▸ Explain: the message at the cursor if there is one, else the goal.</summary>
    public static string ExplainQuestion(EditorContext c) =>
        c.Messages.Any(m => m.Severity == DiagnosticSeverity.Error)
            ? "Explain Lean's error at the cursor in plain words: what it means here, why it happened, and how to fix it. Show the fixed code."
            : c.Goal is { Length: > 0 }
                ? "Explain the goal at the cursor in plain words: what each hypothesis says, what is left to prove, and a good next tactic."
                : "Explain what the code at the cursor does, in plain words.";

    /// <summary>A whole first conversation: the instructions, then the question with the editor's context.</summary>
    public static IReadOnlyList<ChatMessage> Start(IChatModel model, EditorContext c, string question) =>
    [
        ChatMessage.System(SystemPrompt),
        ChatMessage.User(WithContext(c, question, model.ContextTokens - Math.Clamp(model.ContextTokens / 4, 500, 2000) - AiText.EstimateTokens(SystemPrompt))),
    ];

    /// <summary>
    /// A conversation cut to fit <paramref name="model"/>: the instructions, the first question (with the code), and
    /// as many of the latest turns as fit.
    /// </summary>
    public static IReadOnlyList<ChatMessage> Fit(IChatModel model, IReadOnlyList<ChatMessage> conversation)
    {
        int budget = model.ContextTokens - Math.Clamp(model.ContextTokens / 4, 500, 2000);
        int total = conversation.Sum(m => AiText.EstimateTokens(m.Text) + 4);
        if (total <= budget || conversation.Count <= 3)
        {
            return conversation;
        }
        var head = conversation.Take(2).ToList();
        int used = head.Sum(m => AiText.EstimateTokens(m.Text) + 4);
        var tail = new List<ChatMessage>();
        for (int i = conversation.Count - 1; i >= 2; i--)
        {
            int t = AiText.EstimateTokens(conversation[i].Text) + 4;
            if (used + t > budget && tail.Count > 0)
            {
                break;
            }
            used += t;
            tail.Insert(0, conversation[i]);
        }
        // A model expects turns to alternate, and the first question ends the head: the tail must start with an answer.
        if (tail.Count > 1 && tail[0].Role == ChatRole.User)
        {
            tail.RemoveAt(0);
        }
        else if (tail.Count == 1 && tail[0].Role == ChatRole.User)
        {
            // Only the latest question fits beside the first: keep the instructions and the latest question.
            return [head[0], tail[0]];
        }
        return [.. head, .. tail];
    }
}

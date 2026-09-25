using System.Runtime.CompilerServices;
using System.Text;

namespace LeanStudio.Core.Ai;

/// <summary>Who said a message in a conversation with a model.</summary>
public enum ChatRole
{
    /// <summary>Instructions for the model: who it is and how to answer.</summary>
    System,
    /// <summary>The person (or Lean Studio on their behalf).</summary>
    User,
    /// <summary>The model.</summary>
    Assistant,
}

/// <summary>One message in a conversation with a model.</summary>
/// <param name="Role">Who said it.</param>
/// <param name="Text">What was said.</param>
public sealed record ChatMessage(ChatRole Role, string Text)
{
    /// <summary>A system message.</summary>
    public static ChatMessage System(string text) => new(ChatRole.System, text);

    /// <summary>A message from the person.</summary>
    public static ChatMessage User(string text) => new(ChatRole.User, text);

    /// <summary>A message from the model.</summary>
    public static ChatMessage Assistant(string text) => new(ChatRole.Assistant, text);
}

/// <summary>How to ask a model for one answer.</summary>
/// <param name="MaxTokens">The longest answer wanted, in tokens.</param>
/// <param name="Temperature">How adventurous the answer should be: 0 for the most likely, higher for more varied.</param>
public sealed record ChatOptions(int MaxTokens = 1024, double Temperature = 0.2);

/// <summary>Where a model runs, which decides whether the person's code leaves their computer.</summary>
public enum AiLocation
{
    /// <summary>On this computer: nothing is sent anywhere.</summary>
    OnDevice,
    /// <summary>On a server on this computer or its network (Ollama, LM Studio, llama.cpp, MLX).</summary>
    Local,
    /// <summary>A cloud service: the prompt, with the code in it, is sent over the internet.</summary>
    Cloud,
}

/// <summary>A model that answers a conversation, streaming its answer as it writes it.</summary>
public interface IChatModel
{
    /// <summary>A short name for people, such as <c>Apple on-device model</c> or <c>Ollama · qwen3:8b</c>.</summary>
    string DisplayName { get; }

    /// <summary>Where it runs.</summary>
    AiLocation Location { get; }

    /// <summary>
    /// How many tokens it can see at once, prompt and answer together. Prompts are trimmed to fit, so a small
    /// model (Apple's on-device model has 4096) gets the goal and the nearby code rather than the whole file.
    /// </summary>
    int ContextTokens { get; }

    /// <summary>The answer to <paramref name="messages"/>, a piece at a time as the model writes it.</summary>
    /// <param name="messages">The conversation so far; the last is usually the person's.</param>
    /// <param name="options">How long and how varied the answer may be.</param>
    /// <param name="ct">Stops the answer.</param>
    /// <exception cref="AiException">The model could not be reached or refused the request.</exception>
    IAsyncEnumerable<string> StreamAsync(IReadOnlyList<ChatMessage> messages, ChatOptions options, CancellationToken ct = default);
}

/// <summary>A model could not be reached, or answered with an error; the message says which, in plain words.</summary>
public sealed class AiException : Exception
{
    /// <summary>An error with a message for people.</summary>
    public AiException(string message) : base(message)
    {
    }

    /// <summary>An error with a message for people and the exception behind it.</summary>
    public AiException(string message, Exception inner) : base(message, inner)
    {
    }

    /// <summary>An error with no message.</summary>
    public AiException()
    {
    }
}

/// <summary>Helpers for any <see cref="IChatModel"/>.</summary>
public static class ChatModelExtensions
{
    /// <summary>The whole answer at once, with any <c>&lt;think&gt;</c> section a reasoning model writes first removed.</summary>
    public static async Task<string> CompleteAsync(this IChatModel model, IReadOnlyList<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default)
    {
        var sb = new StringBuilder();
        await foreach (string piece in model.StreamAsync(messages, options ?? new ChatOptions(), ct).ConfigureAwait(false))
        {
            sb.Append(piece);
        }
        return AiText.WithoutThinking(sb.ToString());
    }

    /// <summary>
    /// The answer as it streams, with a reasoning model's <c>&lt;think&gt;…&lt;/think&gt;</c> section held back, so
    /// people see the answer, not the deliberation.
    /// </summary>
    public static async IAsyncEnumerable<string> StreamAnswerAsync(this IChatModel model, IReadOnlyList<ChatMessage> messages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var pending = new StringBuilder();
        bool decided = false, thinking = false;
        await foreach (string piece in model.StreamAsync(messages, options ?? new ChatOptions(), ct).ConfigureAwait(false))
        {
            if (decided && !thinking)
            {
                yield return piece;
                continue;
            }
            pending.Append(piece);
            string s = pending.ToString();
            if (!decided)
            {
                string t = s.TrimStart();
                if (t.Length < 7 && "<think>".StartsWith(t, StringComparison.Ordinal))
                {
                    continue;
                }
                decided = true;
                thinking = t.StartsWith("<think>", StringComparison.Ordinal);
                if (!thinking)
                {
                    pending.Clear();
                    yield return s;
                    continue;
                }
            }
            int end = s.IndexOf("</think>", StringComparison.Ordinal);
            if (end >= 0)
            {
                thinking = false;
                pending.Clear();
                string rest = s[(end + "</think>".Length)..].TrimStart();
                if (rest.Length > 0)
                {
                    yield return rest;
                }
            }
        }
        if (!decided && pending.Length > 0)
        {
            yield return pending.ToString();
        }
    }
}

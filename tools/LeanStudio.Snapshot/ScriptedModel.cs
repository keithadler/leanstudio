using System.Runtime.CompilerServices;
using LeanStudio.Core.Ai;

namespace LeanStudio.Snapshot;

/// <summary>A stand-in AI model for the snapshot run: it always answers the same, and counts the questions.</summary>
internal sealed class ScriptedModel(string answer) : IChatModel
{
    public string DisplayName => "Scripted";

    public AiLocation Location => AiLocation.OnDevice;

    public int ContextTokens => 4096;

    public int Asked { get; private set; }

    public async IAsyncEnumerable<string> StreamAsync(IReadOnlyList<ChatMessage> messages, ChatOptions options, [EnumeratorCancellation] CancellationToken ct = default)
    {
        Asked++;
        await Task.Yield();
        yield return answer;
    }
}

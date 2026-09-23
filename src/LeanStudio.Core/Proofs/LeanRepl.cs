using System.Text.RegularExpressions;
using LeanStudio.Lsp;

namespace LeanStudio.Core.Proofs;

/// <summary>One input to the REPL and what Lean said about it.</summary>
public sealed record ReplResult(string Input, string Command, IReadOnlyList<Diagnostic> Messages)
{
    public bool IsError => Messages.Any(m => m.Severity == DiagnosticSeverity.Error);

    /// <summary>Lean's messages, the result of an #eval first; "✓" for a command Lean accepted silently.</summary>
    public string Output
    {
        get
        {
            if (Messages.Count == 0)
            {
                return Command.StartsWith("#eval", StringComparison.Ordinal) ? "(no output)" : "✓ accepted";
            }
            return string.Join("\n", Messages
                .OrderBy(m => m.Severity == DiagnosticSeverity.Information ? 0 : 1)
                .Select(m => (m.Severity switch
                {
                    DiagnosticSeverity.Error => "error: ",
                    DiagnosticSeverity.Warning => "warning: ",
                    _ => "",
                }) + m.Message.Trim()));
        }
    }
}

/// <summary>
/// A REPL in the context of a file: each input is checked after the file's text up to the declaration at the
/// cursor, so everything defined there (and the file's imports, namespaces and variables) is in scope. A bare
/// expression is evaluated with <c>#eval</c>; commands (<c>#check</c>, <c>example</c>, <c>def</c>…) run as they are.
///
/// The context is one document kept open in Lean, and each input replaces only its last lines, so Lean reuses
/// what it already elaborated and only checks the new input.
/// </summary>
public sealed partial class LeanRepl
{
    private string? _uri;

    [GeneratedRegex(@"^\s*(#\w+|example|theorem|lemma|def|abbrev|instance|structure|inductive|class|open|namespace|section|end|variable|universe|set_option|attribute|@\[|noncomputable|private|protected|macro|syntax|notation|infix|infixl|infixr|prefix|postfix|elab|deriving)\b")]
    private static partial Regex CommandStart();

    /// <summary>The file up to the end of the top-level command containing <paramref name="caretLine"/>.</summary>
    public static string Context(string text, int caretLine)
    {
        string[] lines = text.Split('\n');
        int end = lines.Length;
        for (int i = Math.Max(0, caretLine) + 1; i < lines.Length; i++)
        {
            string l = lines[i];
            if (l.Length > 0 && !char.IsWhiteSpace(l[0]) && !l.StartsWith("--", StringComparison.Ordinal))
            {
                end = i;
                break;
            }
        }
        return string.Join('\n', lines.Take(end)).TrimEnd() + "\n";
    }

    /// <summary>The command to run for an input: itself if it is a command, else <c>#eval</c> of it.</summary>
    public static string AsCommand(string input)
    {
        string t = input.Trim();
        return CommandStart().IsMatch(t) ? t : "#eval " + t;
    }

    /// <summary>Check <paramref name="input"/> after <paramref name="context"/>, in a scratch document beside the file.</summary>
    public async Task<ReplResult> EvalAsync(LeanServer server, string sourcePath, string context, string input, CancellationToken ct = default)
    {
        string command = AsCommand(input);
        string text = context + "\n" + command + "\n";
        int firstLine = context.Count(c => c == '\n') + 1;
        string uri = Scratch.UriFor(sourcePath, "Repl");
        if (_uri != uri || !server.IsOpen(uri))
        {
            if (_uri is not null && _uri != uri && server.IsOpen(_uri))
            {
                await server.CloseAsync(_uri).ConfigureAwait(false);
            }
            await server.OpenAsync(uri, text).ConfigureAwait(false);
            _uri = uri;
        }
        else
        {
            await server.ChangeAsync(uri, text).ConfigureAwait(false);
        }
        await server.WaitForElaborationAsync(uri, ct).ConfigureAwait(false);
        var messages = server.DiagnosticsOf(uri).Where(d => d.Range.Start.Line >= firstLine).ToList();
        return new ReplResult(input.Trim(), command, messages);
    }

    /// <summary>Close the scratch document (when the REPL is cleared or the file changes project).</summary>
    public async Task CloseAsync(LeanServer server)
    {
        if (_uri is not null && server.State == LeanServerState.Running && server.IsOpen(_uri))
        {
            await server.CloseAsync(_uri).ConfigureAwait(false);
        }
        _uri = null;
    }
}

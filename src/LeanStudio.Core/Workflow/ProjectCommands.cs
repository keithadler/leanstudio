using System.Text.Json;
using System.Text.RegularExpressions;

namespace LeanStudio.Core.Workflow;

/// <summary>A command a project defines in <c>.leanstudio/commands.json</c>.</summary>
/// <param name="Title">What the command palette shows.</param>
/// <param name="Program">The program to run.</param>
/// <param name="Arguments">Its arguments, which may use <c>${file}</c>, <c>${module}</c> and the other variables.</param>
/// <param name="Detail">A line saying what it does, or empty.</param>
/// <param name="Save">Whether to save everything first (the default).</param>
public sealed record ProjectCommand(string Title, string Program, IReadOnlyList<string> Arguments, string Detail, bool Save);

/// <summary>What a project command's variables stand for when it runs.</summary>
/// <param name="Root">The project folder (<c>${root}</c>).</param>
/// <param name="File">The active file (<c>${file}</c>), or empty.</param>
/// <param name="Module">Its module name (<c>${module}</c>), or empty.</param>
/// <param name="Line">The caret's 1-based line (<c>${line}</c>).</param>
/// <param name="Word">The word at the caret (<c>${word}</c>), or empty.</param>
/// <param name="Selection">The selected text (<c>${selection}</c>), or empty.</param>
public sealed record CommandContext(string Root, string File, string Module, int Line, string Word, string Selection);

/// <summary>
/// Commands of a project's own, kept with it in <c>.leanstudio/commands.json</c>: a program and its arguments, with
/// variables for where you are (<c>${file}</c>, <c>${module}</c>, <c>${line}</c>, <c>${word}</c>,
/// <c>${selection}</c>, <c>${root}</c>). They appear in the command palette, as <c>Project: …</c>, and can be bound
/// to keys in keybindings.json. Output goes to the Output panel.
/// <code>
/// [
///   { "title": "Build this module", "program": "lake", "args": ["build", "${module}"] },
///   { "title": "Run the linter script", "program": "lake", "args": ["exe", "lint-style", "${file}"] }
/// ]
/// </code>
/// </summary>
public static partial class ProjectCommands
{
    /// <summary>Where a project's commands are kept, relative to its root.</summary>
    public static readonly string RelativePath = Path.Combine(".leanstudio", "commands.json");

    /// <summary>Read commands.json: the commands, and a line for each entry that couldn't be read.</summary>
    public static (IReadOnlyList<ProjectCommand> Commands, IReadOnlyList<string> Problems) Parse(string json)
    {
        var commands = new List<ProjectCommand>();
        var problems = new List<string>();
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        }
        catch (JsonException e)
        {
            return ([], ["commands.json isn't valid JSON: " + e.Message]);
        }
        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return ([], ["commands.json should be a list of { \"title\", \"program\", \"args\" }"]);
            }
            int i = 0;
            foreach (JsonElement e in doc.RootElement.EnumerateArray())
            {
                i++;
                string? title = Str(e, "title"), program = Str(e, "program");
                if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(program))
                {
                    problems.Add($"command {i} needs a \"title\" and a \"program\"");
                    continue;
                }
                // Hand-written: a number or true/false is taken as its text; anything else is reported, not crashed on.
                var args = new List<string>();
                if (e.TryGetProperty("args", out JsonElement a) && a.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement x in a.EnumerateArray())
                    {
                        if (x.ValueKind == JsonValueKind.String)
                        {
                            args.Add(x.GetString()!);
                        }
                        else if (x.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)
                        {
                            args.Add(x.GetRawText());
                        }
                        else
                        {
                            problems.Add($"command {i} ({title.Trim()}): an argument isn't text ({x.GetRawText()}); it is left out");
                        }
                    }
                }
                bool save = !e.TryGetProperty("save", out JsonElement s) || s.ValueKind != JsonValueKind.False;
                commands.Add(new ProjectCommand(title.Trim(), program.Trim(), args, Str(e, "detail") ?? "", save));
            }
        }
        return (commands, problems);
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    [GeneratedRegex(@"\$\{(?<name>\w+)\}")]
    private static partial Regex Variable();

    /// <summary>
    /// The arguments with their variables filled in. Each argument stays one argument (a file name with spaces is
    /// never split), and an unknown variable is left as written.
    /// </summary>
    public static IReadOnlyList<string> Expand(IReadOnlyList<string> arguments, CommandContext c) =>
        arguments.Select(a => Variable().Replace(a, m => m.Groups["name"].Value switch
        {
            "root" => c.Root,
            "file" => c.File,
            "fileName" => Path.GetFileName(c.File),
            "relativeFile" => c.File.Length == 0 ? "" : Path.GetRelativePath(c.Root, c.File),
            "module" => c.Module,
            "line" => c.Line.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "word" => c.Word,
            "selection" => c.Selection,
            _ => m.Value,
        })).ToList();

    /// <summary>A commands.json to start from, with examples.</summary>
    public const string Template = """
        // Commands of this project's own. Each shows in the command palette as "Project: <title>", runs in the project
        // folder, and prints to Output. Variables: ${file} ${relativeFile} ${fileName} ${module} ${line} ${word}
        // ${selection} ${root}. Bind one to a key in keybindings.json with its palette name. Save to apply.
        [
          { "title": "Build this module", "program": "lake", "args": ["build", "${module}"], "detail": "lake build for the active file only" },
          // { "title": "Check style", "program": "lake", "args": ["exe", "lint-style", "${relativeFile}"] },
        ]
        """;
}

using System.Text;
using System.Text.Json;

namespace LeanStudio.Core.Editing;

/// <summary>
/// A key with its modifiers, as written in keybindings.json: <c>Cmd+Alt+P</c>, <c>Ctrl+Shift+L</c>, <c>F5</c>.
/// <c>Cmd</c> means ⌘ on a Mac and Ctrl elsewhere, so one file works on every system.
/// </summary>
/// <param name="Ctrl">The Control key.</param>
/// <param name="Alt">Alt (⌥ on a Mac).</param>
/// <param name="Shift">Shift.</param>
/// <param name="Meta">⌘ on a Mac, the Windows key elsewhere.</param>
/// <param name="Key">The key, by its Avalonia name (<c>P</c>, <c>F5</c>, <c>OemPeriod</c>…).</param>
public sealed record KeyChord(bool Ctrl, bool Alt, bool Shift, bool Meta, string Key)
{
    private static readonly Dictionary<string, string> Punctuation = new(StringComparer.Ordinal)
    {
        ["."] = "OemPeriod", ["+"] = "OemPlus", [","] = "OemComma", ["-"] = "OemMinus", ["="] = "OemPlus", ["\\"] = "OemPipe", ["/"] = "OemQuestion",
        ["["] = "OemOpenBrackets", ["]"] = "OemCloseBrackets", [";"] = "OemSemicolon", ["'"] = "OemQuotes", ["`"] = "OemTilde",
        ["Enter"] = "Return", ["Esc"] = "Escape", ["Del"] = "Delete", ["Backspace"] = "Back", ["Space"] = "Space",
    };

    /// <summary>Read a chord such as <c>Cmd+Shift+P</c>; null if it isn't one.</summary>
    /// <param name="text">The chord.</param>
    /// <param name="mac">Whether <c>Cmd</c> means ⌘ (on a Mac) rather than Ctrl.</param>
    public static KeyChord? Parse(string text, bool mac)
    {
        string t = text.Trim();
        string key;
        string[] modifiers;
        if (t.EndsWith("++", StringComparison.Ordinal))
        {
            // Cmd++: the key is + itself.
            key = "+";
            modifiers = t[..^2].Split('+');
        }
        else
        {
            string[] parts = t.Split('+');
            if (parts.Any(p => p.Trim().Length == 0))
            {
                return null;
            }
            key = parts[^1].Trim();
            modifiers = parts[..^1];
        }
        bool ctrl = false, alt = false, shift = false, meta = false;
        foreach (string m in modifiers)
        {
            switch (m.Trim().ToLowerInvariant())
            {
                case "cmd" or "mod":
                    if (mac) { meta = true; } else { ctrl = true; }
                    break;
                case "ctrl" or "control":
                    ctrl = true;
                    break;
                case "alt" or "option" or "opt":
                    alt = true;
                    break;
                case "shift":
                    shift = true;
                    break;
                case "meta" or "win" or "super":
                    meta = true;
                    break;
                default:
                    return null;
            }
        }
        if (Punctuation.TryGetValue(key, out string? named))
        {
            key = named;
        }
        else if (key.Length == 1 && char.IsLetter(key[0]))
        {
            key = key.ToUpperInvariant();
        }
        else if (key.Length == 1 && char.IsDigit(key[0]))
        {
            key = "D" + key;
        }
        return new KeyChord(ctrl, alt, shift, meta, key);
    }
}

/// <summary>One line of keybindings.json: this key runs this command (a command palette entry, by its title).</summary>
/// <param name="Key">The chord, as written.</param>
/// <param name="Command">The command's title, as the command palette shows it (<c>Lean: Prove It</c>).</param>
public sealed record KeyBinding(string Key, string Command);

/// <summary>
/// The person's own keyboard shortcuts: keybindings.json in the settings folder, a list of
/// <c>{ "key": "Cmd+Alt+P", "command": "Lean: Prove It" }</c>, each command named as the command palette names
/// it. They come before the built-in shortcuts. <c>//</c> comments and trailing commas are allowed.
/// </summary>
public static class KeyBindingsFile
{
    /// <summary>The file's name in the settings folder.</summary>
    public const string FileName = "keybindings.json";

    /// <summary>Read the file's text: the bindings, and a line for each entry that couldn't be read.</summary>
    public static (IReadOnlyList<KeyBinding> Bindings, IReadOnlyList<string> Problems) Parse(string json)
    {
        var bindings = new List<KeyBinding>();
        var problems = new List<string>();
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        }
        catch (JsonException e)
        {
            return ([], [$"{FileName} isn't valid JSON: {e.Message}"]);
        }
        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return ([], [$"{FileName} should be a list: [ {{ \"key\": \"Cmd+Alt+P\", \"command\": \"Lean: Prove It\" }} ]"]);
            }
            int i = 0;
            foreach (JsonElement e in doc.RootElement.EnumerateArray())
            {
                i++;
                // Hand-written: a key or command that isn't text is reported below, not crashed on.
                string? key = e.ValueKind == JsonValueKind.Object && e.TryGetProperty("key", out JsonElement k) && k.ValueKind == JsonValueKind.String ? k.GetString() : null;
                string? command = e.ValueKind == JsonValueKind.Object && e.TryGetProperty("command", out JsonElement c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
                if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(command))
                {
                    problems.Add($"entry {i} needs a \"key\" and a \"command\"");
                    continue;
                }
                if (KeyChord.Parse(key, mac: false) is null)
                {
                    problems.Add($"entry {i}: \"{key}\" isn't a key (write it like Cmd+Shift+P, Alt+F5 or Ctrl+.)");
                    continue;
                }
                bindings.Add(new KeyBinding(key.Trim(), command.Trim()));
            }
        }
        return (bindings, problems);
    }

    /// <summary>
    /// A keybindings.json to start from: an empty list, with every command and its built-in shortcut listed in a
    /// comment to copy from.
    /// </summary>
    public static string Template(IEnumerable<(string Command, string Keys)> commands)
    {
        var sb = new StringBuilder();
        sb.Append("// Your keyboard shortcuts. Each runs a command, named as the command palette (⌘⇧P / Ctrl+Shift+P) names it,\n");
        sb.Append("// and comes before the built-in shortcuts. Cmd is ⌘ on a Mac and Ctrl elsewhere. Save to apply.\n");
        sb.Append("//\n// Example:\n//   { \"key\": \"Cmd+Alt+L\", \"command\": \"Lean: Lint File (the linters CI runs)\" },\n//\n");
        sb.Append("// Every command, with its built-in shortcut:\n");
        foreach ((string command, string keys) in commands)
        {
            sb.Append("//   ").Append(command).Append(keys.Length > 0 ? "    (" + keys + ")" : "").Append('\n');
        }
        sb.Append("[\n]\n");
        return sb.ToString();
    }
}

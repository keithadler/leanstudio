using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LeanStudio.Core.Processes;

namespace LeanStudio.Core.Agents;

/// <summary>An AI assistant Lean Studio knows how to connect to, and what connecting takes.</summary>
public sealed record AgentClient(string Name, string Description, string Snippet, string SnippetLanguage, bool CanInstall);

/// <summary>
/// Connecting AI coding assistants to Lean Studio's MCP server. Every assistant here speaks MCP over stdio; they
/// differ only in where they keep the list of servers. Each gets the exact snippet to paste, and the ones whose
/// configuration is a plain file or a CLI command can be set up in one click.
/// </summary>
public sealed class AgentSetup
{
    public const string ServerName = "leanstudio";

    public AgentSetup(string command, IReadOnlyList<string> arguments)
    {
        Command = command;
        Arguments = arguments;
    }

    /// <summary>The program an assistant runs to start the server, and its arguments (ending in --mcp).</summary>
    public string Command { get; }
    public IReadOnlyList<string> Arguments { get; }

    /// <summary>How this process was started, turned into the command that starts it as an MCP server.</summary>
    public static AgentSetup ForCurrentProcess()
    {
        string exe = Environment.ProcessPath ?? "LeanStudio";
        string entry = System.Reflection.Assembly.GetEntryAssembly()?.Location ?? "";
        // Run as `dotnet LeanStudio.dll` (from source): the assistant must do the same.
        bool viaDotnet = Path.GetFileNameWithoutExtension(exe).Equals("dotnet", StringComparison.OrdinalIgnoreCase) && entry.Length > 0;
        return viaDotnet ? new AgentSetup(exe, [entry, "--mcp"]) : new AgentSetup(exe, ["--mcp"]);
    }

    private static string Quote(string s) => s.Contains(' ', StringComparison.Ordinal) || s.Contains('\\', StringComparison.Ordinal) ? "\"" + s + "\"" : s;

    public string CommandLine => string.Join(' ', new[] { Command }.Concat(Arguments).Select(Quote));

    public string ClaudeCommand => $"claude mcp add --scope user {ServerName} -- {CommandLine}";

    public string McpServersJson()
    {
        var o = new JsonObject
        {
            ["mcpServers"] = new JsonObject
            {
                [ServerName] = new JsonObject
                {
                    ["command"] = Command,
                    ["args"] = new JsonArray(Arguments.Select(a => (JsonNode)a).ToArray()),
                },
            },
        };
        return o.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    public string CodexToml()
    {
        static string T(string s) => "\"" + s.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
        return $"[mcp_servers.{ServerName}]\ncommand = {T(Command)}\nargs = [{string.Join(", ", Arguments.Select(T))}]\n";
    }

    public IReadOnlyList<AgentClient> Clients() =>
    [
        new("Claude Code", "Adds Lean Studio for every project with `claude mcp add --scope user`.", ClaudeCommand, "shell", true),
        new("Gemini CLI", $"Adds this under \"mcpServers\" in {GeminiSettingsPath}.", McpServersJson(), "json", true),
        new("Codex CLI", $"Adds this to {CodexConfigPath}.", CodexToml(), "toml", true),
        new("Grok CLI, Cursor, Windsurf, Zed and other MCP clients",
            "Any assistant that speaks MCP over stdio: add this server to its MCP configuration (most take exactly this JSON).",
            McpServersJson(), "json", false),
    ];

    public static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    public static string GeminiSettingsPath => Path.Combine(Home, ".gemini", "settings.json");
    public static string CodexConfigPath => Path.Combine(Home, ".codex", "config.toml");

    /// <summary>Set the assistant up; returns what happened, in a sentence.</summary>
    public async Task<string> InstallAsync(string client, CancellationToken ct = default) => client switch
    {
        "Claude Code" => await InstallClaudeAsync(ct).ConfigureAwait(false),
        "Gemini CLI" => InstallGemini(GeminiSettingsPath),
        "Codex CLI" => InstallCodex(CodexConfigPath),
        _ => "Copy the snippet into that assistant's MCP configuration.",
    };

    private async Task<string> InstallClaudeAsync(CancellationToken ct)
    {
        string? claude = FindOnPath("claude") ?? new[] { Path.Combine(Home, ".local", "bin", "claude"), Path.Combine(Home, ".claude", "local", "claude") }.FirstOrDefault(File.Exists);
        if (claude is null)
        {
            return "Claude Code's `claude` command was not found. Install Claude Code, or run the command shown in a terminal.";
        }
        // Replace an older registration (e.g. a different install path) rather than failing on the duplicate.
        await ProcessRunner.RunAsync(claude, ["mcp", "remove", "--scope", "user", ServerName], ct: ct).ConfigureAwait(false);
        ProcessResult r = await ProcessRunner.RunAsync(claude, ["mcp", "add", "--scope", "user", ServerName, "--", Command, .. Arguments], ct: ct).ConfigureAwait(false);
        return r.Success
            ? "Added to Claude Code. Start `claude` in a Lean project and ask it to work on a proof."
            : "claude mcp add failed: " + r.Output.Trim();
    }

    /// <summary>Merge the server into a Gemini CLI settings file, keeping everything else in it.</summary>
    public string InstallGemini(string settingsPath)
    {
        JsonObject root = new();
        if (File.Exists(settingsPath))
        {
            string existing = File.ReadAllText(settingsPath);
            try
            {
                root = JsonNode.Parse(existing, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject ?? new JsonObject();
            }
            catch (JsonException e)
            {
                return $"{settingsPath} is not valid JSON ({e.Message}); add the snippet by hand.";
            }
            File.Copy(settingsPath, settingsPath + ".bak", overwrite: true);
        }
        if (root["mcpServers"] is not JsonObject servers)
        {
            root["mcpServers"] = servers = new JsonObject();
        }
        servers[ServerName] = new JsonObject
        {
            ["command"] = Command,
            ["args"] = new JsonArray(Arguments.Select(a => (JsonNode)a).ToArray()),
        };
        Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
        File.WriteAllText(settingsPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return $"Added to Gemini CLI ({settingsPath}). Start `gemini` in a Lean project.";
    }

    /// <summary>Add (or replace) the server's table in a Codex config.toml, keeping everything else.</summary>
    public string InstallCodex(string configPath)
    {
        string text = File.Exists(configPath) ? File.ReadAllText(configPath) : "";
        if (text.Length > 0)
        {
            File.Copy(configPath, configPath + ".bak", overwrite: true);
        }
        // Drop an existing [mcp_servers.leanstudio] table: everything from its header to the next table header.
        var kept = new StringBuilder();
        bool skipping = false;
        foreach (string line in text.Split('\n'))
        {
            string t = line.Trim();
            if (t.StartsWith('['))
            {
                skipping = t == $"[mcp_servers.{ServerName}]";
            }
            if (!skipping)
            {
                kept.Append(line).Append('\n');
            }
        }
        string result = kept.ToString().TrimEnd() + (kept.ToString().Trim().Length > 0 ? "\n\n" : "") + CodexToml();
        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
        File.WriteAllText(configPath, result);
        return $"Added to Codex ({configPath}). Start `codex` in a Lean project.";
    }

    private static string? FindOnPath(string name)
    {
        string file = OperatingSystem.IsWindows() ? name + ".exe" : name;
        foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (string candidate in OperatingSystem.IsWindows() ? new[] { file, name + ".cmd" } : [file])
            {
                string p = Path.Combine(dir, candidate);
                if (File.Exists(p))
                {
                    return p;
                }
            }
        }
        return null;
    }
}

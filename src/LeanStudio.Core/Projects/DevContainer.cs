using System.Text.Json;
using LeanStudio.Core.Processes;
using LeanStudio.Core.Toolchains;
using LeanStudio.Lsp;

namespace LeanStudio.Core.Projects;

/// <summary>
/// A project's dev container (<c>.devcontainer/devcontainer.json</c>, the format VS Code and GitHub Codespaces use):
/// its Lean can run inside the container while the files are edited here, where the container mounts them. The
/// container is found by the label the dev container tools put on it (<c>devcontainer.local_folder</c>), and
/// started with the <c>devcontainer</c> command line when it is not running.
/// </summary>
/// <param name="ProjectRoot">The project's folder here.</param>
/// <param name="ConfigFile">Its <c>devcontainer.json</c>.</param>
/// <param name="WorkspaceFolder">Where the project is inside the container (<c>/workspaces/Name</c> unless it says).</param>
/// <param name="Name">The container's name in the file, or empty.</param>
public sealed record DevContainer(string ProjectRoot, string ConfigFile, string WorkspaceFolder, string Name)
{
    private static readonly JsonDocumentOptions Relaxed = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    /// <summary>The project's dev container configuration, or null when it has none (or it cannot be read).</summary>
    public static DevContainer? Find(string projectRoot)
    {
        foreach (string candidate in new[] { Path.Combine(".devcontainer", "devcontainer.json"), ".devcontainer.json" })
        {
            string file = Path.Combine(projectRoot, candidate);
            if (!File.Exists(file))
            {
                continue;
            }
            try
            {
                using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(file), Relaxed);
                JsonElement root = doc.RootElement;
                string folder = Path.GetFileName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(projectRoot)));
                string workspace = root.TryGetProperty("workspaceFolder", out JsonElement w) && w.ValueKind == JsonValueKind.String
                    ? w.GetString()!.Replace("${localWorkspaceFolderBasename}", folder, StringComparison.Ordinal)
                    : "/workspaces/" + folder;
                string name = root.TryGetProperty("name", out JsonElement n) && n.ValueKind == JsonValueKind.String ? n.GetString()! : "";
                return new DevContainer(Path.GetFullPath(projectRoot), file, workspace, name);
            }
            catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }
        return null;
    }

    /// <summary>The id of the running container made for this project, or null when none is running (or docker is missing).</summary>
    public async Task<string?> RunningContainerAsync(CancellationToken ct = default)
    {
        string? docker = Elan.FindExecutable("docker");
        if (docker is null)
        {
            return null;
        }
        ProcessResult r = await ProcessRunner.RunAsync(docker,
            ["ps", "--filter", "label=devcontainer.local_folder=" + ProjectRoot, "--format", "{{.ID}}"], ProjectRoot, ct: ct).ConfigureAwait(false);
        string? id = r.Success ? r.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() : null;
        return id is { Length: > 0 } ? id : null;
    }

    /// <summary>
    /// Start the container with <c>devcontainer up</c> (building it first if need be) and return its id, or null and
    /// why not: the <c>devcontainer</c> command line is not installed, or it failed.
    /// </summary>
    public async Task<(string? Id, string? Error)> StartAsync(Action<string>? onLine = null, CancellationToken ct = default)
    {
        string? cli = Elan.FindExecutable("devcontainer");
        if (cli is null)
        {
            return (null, "the devcontainer command line is not installed (npm install -g @devcontainers/cli), and no container of this project is running: start it from VS Code (Reopen in Container) or install the command line");
        }
        ProcessResult r = await ProcessRunner.RunAsync(cli, ["up", "--workspace-folder", ProjectRoot], ProjectRoot, onLine, ct: ct).ConfigureAwait(false);
        // It ends with one line of JSON: {"outcome":"success","containerId":"…",…}.
        foreach (string line in r.Output.Split('\n').Reverse())
        {
            if (!line.TrimStart().StartsWith('{'))
            {
                continue;
            }
            try
            {
                using JsonDocument doc = JsonDocument.Parse(line);
                if (doc.RootElement.TryGetProperty("containerId", out JsonElement id) && id.GetString() is { Length: > 0 } s)
                {
                    return (s, null);
                }
            }
            catch (JsonException)
            {
            }
        }
        return (null, "devcontainer up did not start it: " + string.Join(" ", r.Output.Split('\n').TakeLast(3)).Trim());
    }

    /// <summary>The target that runs Lean's tools in container <paramref name="containerId"/>, for this project.</summary>
    public RemoteTarget Target(string containerId) => new(containerId, WorkspaceFolder, ProjectRoot) { Kind = RemoteKind.Container };
}

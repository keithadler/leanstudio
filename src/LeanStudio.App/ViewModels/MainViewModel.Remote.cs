using CommunityToolkit.Mvvm.Input;
using LeanStudio.App.Services;
using LeanStudio.Core.Processes;
using LeanStudio.Core.Projects;
using LeanStudio.Lsp;

namespace LeanStudio.App.ViewModels;

public sealed partial class MainViewModel
{
    // ---- projects on other machines: files through a mount, Lean over SSH ----

    /// <summary>Use every remote project the settings know, so opening its local folder runs Lean on its machine.</summary>
    public void RegisterRemotes()
    {
        foreach (RemoteProject r in Settings.RemoteProjects.Where(r => r.Kind == "ssh" && r.Host.Length > 0 && r.LocalRoot.Length > 0))
        {
            RemoteTargets.Register(new RemoteTarget(r.Host, r.RemoteRoot, r.LocalRoot));
        }
    }

    /// <summary>The remote machine the open project's Lean runs on, or null when it runs here.</summary>
    public RemoteTarget? ProjectRemote => Project is null ? null : RemoteTargets.For(Project.Root);

    /// <summary>
    /// Open a project on another machine: <paramref name="destination"/> (<c>user@host:/path/to/project</c>) is
    /// where it is, and <paramref name="localRoot"/> is where that folder is mounted here (sshfs, a network drive).
    /// Lake is run there first, to check SSH works without a password and elan is installed; then the project opens,
    /// and is remembered.
    /// </summary>
    /// <param name="destination">Where the project is: <c>user@host:/path/to/project</c>.</param>
    /// <param name="localRoot">Where that folder is mounted here.</param>
    /// <param name="template">ssh settings to start from (another ssh program, other options), or null for the defaults.</param>
    /// <returns>Whether it opened.</returns>
    public async Task<bool> OpenRemoteProjectAsync(string destination, string localRoot, RemoteTarget? template = null)
    {
        if (RemoteTarget.ParseDestination(destination) is not (string host, string remoteRoot))
        {
            Log($"\"{destination}\" isn't a remote folder: write it as user@host:/path/to/project.");
            return false;
        }
        if (!Directory.Exists(localRoot))
        {
            Log($"{localRoot} doesn't exist: mount the remote folder there first (sshfs, or a network drive).");
            return false;
        }
        localRoot = Path.GetFullPath(localRoot);
        var target = (template ?? new RemoteTarget(host, remoteRoot, localRoot)) with { Host = host, RemoteRoot = remoteRoot, LocalRoot = localRoot };
        RemoteTargets.Register(target);
        BottomTab = OutputPanel;
        Log($"▶ Checking Lean on {host} (in {remoteRoot})");
        ProcessResult r = await ProcessRunner.RunAsync("lake", ["--version"], localRoot);
        if (!r.Success)
        {
            RemoteTargets.Unregister(localRoot);
            Log($"■ Couldn't run Lake on {host}: {r.Output.Trim()}");
            Log($"  Check that `ssh {host}` logs in without asking for a password (an SSH key or agent), and that elan is installed there.");
            return false;
        }
        Log("■ " + r.Output.Trim().Split('\n')[0] + " on " + host);
        Settings.RemoteProjects.RemoveAll(p => string.Equals(Path.GetFullPath(p.LocalRoot), localRoot, StringComparison.Ordinal));
        Settings.RemoteProjects.Add(new RemoteProject { Host = host, RemoteRoot = remoteRoot, LocalRoot = localRoot });
        Settings.Save();
        await OpenProjectAsync(localRoot);
        return true;
    }

    /// <summary>
    /// Before Lean starts for <paramref name="root"/>: a folder in WSL (<c>\\wsl.localhost\…</c>) runs its Lean
    /// inside WSL, and a project set to use its dev container runs it in the container, found running again now.
    /// Suggests the dev container for a project that has one and does not use it yet.
    /// </summary>
    private async Task PrepareRemoteAsync(string root)
    {
        if (RemoteTargets.For(root) is not null)
        {
            return;
        }
        if (OperatingSystem.IsWindows() && RemoteTarget.ForWslPath(root) is RemoteTarget wsl)
        {
            RemoteTargets.Register(wsl);
            Log($"This folder is in WSL ({wsl.Host}): Lean runs there, at {wsl.RemoteRoot}.");
            return;
        }
        if (DevContainer.Find(root) is not DevContainer dc)
        {
            return;
        }
        bool chosen = Settings.RemoteProjects.Any(p => p.Kind == "container" && SamePath(p.LocalRoot, root));
        if (!chosen)
        {
            Log("This project has a dev container" + (dc.Name.Length > 0 ? $" ({dc.Name})" : "") + ": Remote ▸ Use the Dev Container runs its Lean inside it.");
            return;
        }
        if (await dc.RunningContainerAsync() is string id)
        {
            RemoteTargets.Register(dc.Target(id));
            Log($"Lean runs in the dev container {id[..Math.Min(12, id.Length)]}, at {dc.WorkspaceFolder}.");
        }
        else
        {
            Log("This project's dev container isn't running: Remote ▸ Use the Dev Container starts it (or start it from VS Code). Lean runs here until then.");
        }
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar), Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    /// <summary>
    /// Run the open project's Lean in its dev container: the running one, or one started with <c>devcontainer up</c>.
    /// Lake is run in it first, to check elan is there; then Lean restarts in it, and the choice is remembered.
    /// </summary>
    [RelayCommand]
    public async Task UseDevContainerAsync()
    {
        if (Project is null || DevContainer.Find(Project.Root) is not DevContainer dc)
        {
            Log("This project has no dev container (.devcontainer/devcontainer.json).");
            return;
        }
        BottomTab = OutputPanel;
        string? id = await dc.RunningContainerAsync();
        if (id is null)
        {
            Log("▶ Starting the dev container (devcontainer up)…");
            (id, string? error) = await dc.StartAsync(line => Avalonia.Threading.Dispatcher.UIThread.Post(() => Log("  " + line)));
            if (id is null)
            {
                Log("■ Couldn't start it: " + error);
                return;
            }
        }
        RemoteTarget target = dc.Target(id);
        RemoteTargets.Register(target);
        ProcessResult r = await ProcessRunner.RunAsync("lake", ["--version"], Project.Root);
        if (!r.Success)
        {
            RemoteTargets.Unregister(Project.Root);
            Log($"■ Couldn't run Lake in {target.Describe()}: {r.Output.Trim()}");
            Log("  Install elan in the container (the dev container's Dockerfile or its postCreateCommand) and try again.");
            return;
        }
        Log($"■ {r.Output.Trim().Split('\n')[0]} in {target.Describe()}, at {dc.WorkspaceFolder}");
        Settings.RemoteProjects.RemoveAll(p => SamePath(p.LocalRoot, Project.Root));
        Settings.RemoteProjects.Add(new RemoteProject { Kind = "container", LocalRoot = Project.Root, RemoteRoot = dc.WorkspaceFolder });
        Settings.Save();
        await StartServerAsync();
    }

    /// <summary>Run the open project's Lean here again, instead of on its remote machine, and forget the remote.</summary>
    [RelayCommand]
    private async Task ForgetRemoteAsync()
    {
        if (ProjectRemote is not RemoteTarget remote)
        {
            Log("This project's Lean already runs here.");
            return;
        }
        RemoteTargets.Unregister(remote.LocalRoot);
        Settings.RemoteProjects.RemoveAll(p => string.Equals(Path.GetFullPath(p.LocalRoot), Path.GetFullPath(remote.LocalRoot), StringComparison.Ordinal));
        Settings.Save();
        Log($"Lean for {Project!.Root} runs here now, not in {remote.Describe()}.");
        await StartServerAsync();
    }
}

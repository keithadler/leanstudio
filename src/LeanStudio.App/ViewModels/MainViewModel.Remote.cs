using CommunityToolkit.Mvvm.Input;
using LeanStudio.App.Services;
using LeanStudio.Core.Processes;
using LeanStudio.Lsp;

namespace LeanStudio.App.ViewModels;

public sealed partial class MainViewModel
{
    // ---- projects on other machines: files through a mount, Lean over SSH ----

    /// <summary>Use every remote project the settings know, so opening its local folder runs Lean on its machine.</summary>
    public void RegisterRemotes()
    {
        foreach (RemoteProject r in Settings.RemoteProjects.Where(r => r.Host.Length > 0 && r.LocalRoot.Length > 0))
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
        Log($"Lean for {Project!.Root} runs here now, not on {remote.Host}.");
        await StartServerAsync();
    }
}

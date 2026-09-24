using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LeanStudio.Core.Projects;
using LeanStudio.Core.Toolchains;

namespace LeanStudio.App.ViewModels;

/// <summary>The toolchains elan has installed, the one the open project pins, and installing or switching between them.</summary>
/// <remarks>
/// Backs the Toolchains panel. Everything is read from and done with the <c>elan</c> command line; the open project
/// and restarting Lean come from <see cref="MainViewModel"/> through the constructor. Output goes to the Output panel.
/// </remarks>
public sealed partial class ToolchainsViewModel : ObservableObject
{
    private readonly Func<LeanProject?> _project;
    private readonly Action<string> _log;
    private readonly Func<Task> _restartServer;

    /// <summary>Create the panel's state. Nothing is listed until <see cref="RefreshAsync"/>.</summary>
    /// <param name="project">The open project, or null; read each time it is needed.</param>
    /// <param name="log">Writes a line to the Output panel.</param>
    /// <param name="restartServer">Restarts Lean, after the project's toolchain changes.</param>
    public ToolchainsViewModel(Func<LeanProject?> project, Action<string> log, Func<Task> restartServer)
    {
        _project = project;
        _log = log;
        _restartServer = restartServer;
    }

    /// <summary>The toolchains elan has installed.</summary>
    public ObservableList<Toolchain> Installed { get; } = new();

    /// <summary>The toolchain selected in the list, which Uninstall, Set Default and Use for Project act on.</summary>
    [ObservableProperty]
    private Toolchain? _selected;

    /// <summary>
    /// The toolchain to install, such as <c>leanprover/lean4:stable</c> or <c>leanprover/lean4:v4.20.0</c>.
    /// </summary>
    [ObservableProperty]
    private string _toInstall = "leanprover/lean4:stable";

    /// <summary>The toolchain the open project pins, or why there is none.</summary>
    [ObservableProperty]
    private string _projectToolchain = "";

    /// <summary>The panel's status line: how many are installed, what is running, or whether it worked.</summary>
    [ObservableProperty]
    private string _status = "";

    /// <summary>An elan command is running; the others are disabled until it finishes.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(InstallCommand), nameof(UninstallCommand), nameof(UseForProjectCommand), nameof(SetDefaultCommand))]
    private bool _isBusy;

    /// <summary>elan was found. Updated by <see cref="RefreshAsync"/>.</summary>
    public bool ElanInstalled => Elan.IsInstalled;

    /// <summary>Where elan's installation instructions are.</summary>
    public string ElanInstallUrl => Elan.InstallUrl;

    /// <summary>List the installed toolchains again (with <c>elan</c>) and the one the open project pins.</summary>
    [RelayCommand]
    public async Task RefreshAsync()
    {
        IReadOnlyList<Toolchain> list = await Elan.ListAsync();
        Installed.Reset(list);
        LeanProject? p = _project();
        ProjectToolchain = p is null ? "no project open" : p.Toolchain ?? "not pinned (no lean-toolchain file)";
        Status = !Elan.IsInstalled ? "elan is not installed. Lean Studio needs it to find Lean."
               : list.Count == 0 ? "No toolchains installed yet."
               : $"{list.Count} toolchain{(list.Count == 1 ? "" : "s")} installed";
        OnPropertyChanged(nameof(ElanInstalled));
    }

    private bool NotBusy => !IsBusy;

    /// <summary>
    /// Install <see cref="ToInstall"/> with elan; this can take a while, with progress in the Output panel.
    /// </summary>
    [RelayCommand(CanExecute = nameof(NotBusy))]
    private async Task InstallAsync()
    {
        string name = ToInstall.Trim();
        if (name.Length == 0)
        {
            return;
        }
        await RunAsync($"Installing {name}…", ct => Elan.InstallAsync(name, _log, ct));
    }

    /// <summary>Remove the selected toolchain with elan.</summary>
    [RelayCommand(CanExecute = nameof(NotBusy))]
    private async Task UninstallAsync()
    {
        if (Selected is Toolchain t)
        {
            await RunAsync($"Removing {t.Name}…", ct => Elan.UninstallAsync(t.Name, _log, ct));
        }
    }

    /// <summary>Make the selected toolchain elan's default, for folders that pin none.</summary>
    [RelayCommand(CanExecute = nameof(NotBusy))]
    private async Task SetDefaultAsync()
    {
        if (Selected is Toolchain t)
        {
            await RunAsync($"Making {t.Name} the default…", ct => Elan.SetDefaultAsync(t.Name, _log, ct));
        }
    }

    /// <summary>Pin the selected toolchain in the project's lean-toolchain file and restart Lean on it.</summary>
    [RelayCommand(CanExecute = nameof(NotBusy))]
    private async Task UseForProjectAsync()
    {
        if (Selected is not Toolchain t || _project() is not LeanProject p)
        {
            return;
        }
        p.SetToolchain(t.Name);
        _log($"{p.ToolchainPath} now pins {t.Name}");
        await RefreshAsync();
        await _restartServer();
    }

    private async Task RunAsync(string what, Func<CancellationToken, Task<Core.Processes.ProcessResult>> action)
    {
        IsBusy = true;
        Status = what;
        _log(what);
        try
        {
            var r = await action(CancellationToken.None);
            Status = r.Success ? "Done" : $"Failed (exit {r.ExitCode}); see Output";
        }
        finally
        {
            IsBusy = false;
            await RefreshAsync();
        }
    }
}

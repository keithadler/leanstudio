using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LeanStudio.Core.Projects;
using LeanStudio.Core.Toolchains;

namespace LeanStudio.App.ViewModels;

/// <summary>The toolchains elan has installed, the one the open project pins, and installing or switching between them.</summary>
public sealed partial class ToolchainsViewModel : ObservableObject
{
    private readonly Func<LeanProject?> _project;
    private readonly Action<string> _log;
    private readonly Func<Task> _restartServer;

    public ToolchainsViewModel(Func<LeanProject?> project, Action<string> log, Func<Task> restartServer)
    {
        _project = project;
        _log = log;
        _restartServer = restartServer;
    }

    public ObservableList<Toolchain> Installed { get; } = new();

    [ObservableProperty]
    private Toolchain? _selected;

    [ObservableProperty]
    private string _toInstall = "leanprover/lean4:stable";

    [ObservableProperty]
    private string _projectToolchain = "";

    [ObservableProperty]
    private string _status = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(InstallCommand), nameof(UninstallCommand), nameof(UseForProjectCommand), nameof(SetDefaultCommand))]
    private bool _isBusy;

    public bool ElanInstalled => Elan.IsInstalled;

    public string ElanInstallUrl => Elan.InstallUrl;

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

    [RelayCommand(CanExecute = nameof(NotBusy))]
    private async Task UninstallAsync()
    {
        if (Selected is Toolchain t)
        {
            await RunAsync($"Removing {t.Name}…", ct => Elan.UninstallAsync(t.Name, _log, ct));
        }
    }

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

using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LeanStudio.Core.Learn;
using LeanStudio.Core.Processes;
using LeanStudio.Core.Updates;

namespace LeanStudio.App.ViewModels;

/// <summary>For newcomers and programmers: the Learn tab, running programs, snippets; and updates.</summary>
public sealed partial class MainViewModel
{
    public const int LearnTab = 5;

    public LearnViewModel Learn { get; private set; } = null!;

    /// <summary>Raised to insert text at the caret (a symbol from the palette, a snippet).</summary>
    public event Action<string>? InsertRequested;

    public void RequestInsert(string text) => InsertRequested?.Invoke(text);

    private void InitLearn()
    {
        Learn = new LearnViewModel(this);
        Info.ShowEnglish = Settings.ShowGoalsInEnglish;
        Info.ExplainErrors = Settings.ExplainErrors;
        Info.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(InfoViewModel.ShowEnglish))
            {
                Settings.ShowGoalsInEnglish = Info.ShowEnglish;
                Settings.Save();
            }
        };
    }

    // ---- running programs ----

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunProgramCommand))]
    private bool _canRun;

    /// <summary>Recompute whether the active file is a program (has a main) the Run button can start.</summary>
    public void UpdateCanRun() => CanRun = ActiveDocument is { IsLean: true } d && ProgramRunner.HasMain(d.Document.Text);

    private CancellationTokenSource? _runCts;

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task RunProgramAsync()
    {
        if (ActiveDocument is not { IsLean: true } d || Project is null)
        {
            return;
        }
        await SaveDocumentAsync(d);
        _runCts?.Cancel();
        var cts = new CancellationTokenSource();
        _runCts = cts;
        BottomTab = OutputPanel;
        Log($"▶ Running {Path.GetFileName(d.Path)}");
        try
        {
            ProcessResult r = await ProgramRunner.RunAsync(Project, d.Path, line => Log("  " + line), cts.Token);
            Log(r.Success ? "■ Finished." : $"■ Exited with code {r.ExitCode}.");
        }
        catch (OperationCanceledException)
        {
            Log("■ Stopped.");
        }
    }

    [RelayCommand]
    private void StopProgram() => _runCts?.Cancel();

    // ---- updates ----

    [ObservableProperty]
    private UpdateInfo? _update;

    [ObservableProperty]
    private bool _hasUpdate;

    [ObservableProperty]
    private string _updateText = "";

    [ObservableProperty]
    private bool _isDownloadingUpdate;

    [ObservableProperty]
    private double _updateProgress;

    public string CurrentVersionText => "Lean Studio " + UpdateChecker.CurrentVersion.ToString(3);

    /// <summary>Check GitHub for a newer release: quietly once a day at startup, or on request (with a reply either way).</summary>
    public async Task CheckForUpdatesAsync(bool manual)
    {
        if (!manual && (!Settings.CheckForUpdates || Settings.LastUpdateCheck is DateTime last && DateTime.UtcNow - last < TimeSpan.FromHours(20)))
        {
            return;
        }
        try
        {
            UpdateInfo? u = await new UpdateChecker().CheckAsync();
            Settings.LastUpdateCheck = DateTime.UtcNow;
            Settings.Save();
            if (u is null || (!manual && u.Tag == Settings.SkippedVersion))
            {
                if (manual)
                {
                    await _dialogs.ConfirmAsync("Up to date", $"{CurrentVersionText} is the newest release.");
                }
                return;
            }
            Update = u;
            HasUpdate = true;
            UpdateText = $"Lean Studio {u.Version.ToString(3)} is available (you have {UpdateChecker.CurrentVersion.ToString(3)}).";
            Log(UpdateText + " " + u.PageUrl);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or InvalidOperationException)
        {
            Log("Could not check for updates: " + e.Message);
            if (manual)
            {
                await _dialogs.ConfirmAsync("Check for updates", "Could not reach GitHub: " + e.Message);
            }
        }
    }

    [RelayCommand]
    private async Task DownloadUpdateAsync()
    {
        if (Update is not UpdateInfo u)
        {
            return;
        }
        if (!u.HasDownload)
        {
            await _dialogs.LaunchAsync(new Uri(u.PageUrl));
            return;
        }
        IsDownloadingUpdate = true;
        try
        {
            var progress = new Progress<double>(p => UpdateProgress = p * 100);
            string file = await new UpdateChecker().DownloadAsync(u, progress);
            UpdateText = $"Downloaded {Path.GetFileName(file)} to {Path.GetDirectoryName(file)}. Quit Lean Studio and replace it with the new version.";
            Log(UpdateText);
            await _dialogs.RevealAsync(file);
        }
        catch (Exception e) when (e is HttpRequestException or IOException or TaskCanceledException or InvalidOperationException)
        {
            UpdateText = "The download failed: " + e.Message;
        }
        finally
        {
            IsDownloadingUpdate = false;
        }
    }

    [RelayCommand]
    private async Task OpenReleaseNotesAsync()
    {
        if (Update is UpdateInfo u)
        {
            await _dialogs.LaunchAsync(new Uri(u.PageUrl));
        }
    }

    [RelayCommand]
    private void SkipUpdate()
    {
        if (Update is UpdateInfo u)
        {
            Settings.SkippedVersion = u.Tag;
            Settings.Save();
        }
        HasUpdate = false;
    }

    [RelayCommand]
    private void DismissUpdate() => HasUpdate = false;

    [RelayCommand]
    private Task CheckForUpdatesNowAsync() => CheckForUpdatesAsync(manual: true);

    /// <summary>Schedule the quiet daily check a few seconds after startup, so it never slows opening.</summary>
    public void ScheduleUpdateCheck() =>
        DispatcherTimer.RunOnce(() => _ = CheckForUpdatesAsync(manual: false), TimeSpan.FromSeconds(5));
}

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
    /// <summary>The Learn panel's index in <see cref="SidebarTab"/>.</summary>
    public const int LearnTab = 5;

    /// <summary>The Learn panel.</summary>
    public LearnViewModel Learn { get; private set; } = null!;

    /// <summary>Raised to insert text at the caret (a symbol from the palette, a snippet).</summary>
    public event Action<string>? InsertRequested;

    /// <summary>Ask the editor to insert text at the caret, by raising <see cref="InsertRequested"/>.</summary>
    /// <param name="text">The text to insert.</param>
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

    /// <summary>
    /// The active file is a Lean program with a <c>main</c>, so Run can start it. Kept up to date by
    /// <see cref="UpdateCanRun"/>.
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunProgramCommand))]
    private bool _canRun;

    /// <summary>Recompute whether the active file is a program (has a main) the Run button can start.</summary>
    public void UpdateCanRun() => CanRun = ActiveDocument is { IsLean: true } d && ProgramRunner.HasMain(d.Document.Text);

    private CancellationTokenSource? _runCts;

    /// <summary>
    /// Save the active file and run its <c>main</c>, with its output in the Output panel. A program already running is
    /// stopped first.
    /// </summary>
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

    /// <summary>Stop the running program.</summary>
    [RelayCommand]
    private void StopProgram() => _runCts?.Cancel();

    // ---- updates ----

    /// <summary>The newer release found by the last check, or null.</summary>
    [ObservableProperty]
    private UpdateInfo? _update;

    /// <summary>Show the update banner.</summary>
    [ObservableProperty]
    private bool _hasUpdate;

    /// <summary>
    /// The update banner's text: what is available, where it was downloaded, or why the download failed.
    /// </summary>
    [ObservableProperty]
    private string _updateText = "";

    /// <summary>The update is downloading.</summary>
    [ObservableProperty]
    private bool _isDownloadingUpdate;

    /// <summary>How much of the update has downloaded, from 0 to 100.</summary>
    [ObservableProperty]
    private double _updateProgress;

    /// <summary>This version, as <c>Lean Studio 1.2.3</c>.</summary>
    public string CurrentVersionText => "Lean Studio " + UpdateChecker.CurrentVersion.ToString(3);

    /// <summary>Check GitHub for a newer release: quietly once a day at startup, or on request (with a reply either way).</summary>
    /// <remarks>
    /// A quiet check is skipped when checks are turned off or one ran in the last 20 hours, ignores a version the person
    /// skipped, and only logs a failure. A newer release shows the update banner.
    /// </remarks>
    /// <param name="manual">
    /// The person asked: always check, and say what was found (or why the check failed) in a dialog.
    /// </param>
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

    /// <summary>
    /// Download the update for this platform and show it in the file manager; the person replaces the app themselves.
    /// With no download for this platform, opens the release page instead.
    /// </summary>
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

    /// <summary>Open the update's release page in the browser.</summary>
    [RelayCommand]
    private async Task OpenReleaseNotesAsync()
    {
        if (Update is UpdateInfo u)
        {
            await _dialogs.LaunchAsync(new Uri(u.PageUrl));
        }
    }

    /// <summary>Hide the banner and do not mention this version again in the quiet daily check.</summary>
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

    /// <summary>Hide the update banner for now.</summary>
    [RelayCommand]
    private void DismissUpdate() => HasUpdate = false;

    /// <summary>Check for updates now, and say what was found.</summary>
    [RelayCommand]
    private Task CheckForUpdatesNowAsync() => CheckForUpdatesAsync(manual: true);

    /// <summary>Schedule the quiet daily check a few seconds after startup, so it never slows opening.</summary>
    public void ScheduleUpdateCheck() =>
        DispatcherTimer.RunOnce(() => _ = CheckForUpdatesAsync(manual: false), TimeSpan.FromSeconds(5));
}

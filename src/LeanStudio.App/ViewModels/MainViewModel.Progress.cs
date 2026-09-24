using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LeanStudio.Core.Workflow;

namespace LeanStudio.App.ViewModels;

/// <summary>
/// How far a long task has got, shown properly: a real bar in the status bar and a banner over the editor, fed by
/// what the task prints (Lake's <c>[done/total]</c>, the Mathlib cache's counts, elan's downloads) and by Tenet's
/// own progress; the time left; the slowest modules; warnings as they arrive; and a note when a long task ends.
/// </summary>
public sealed partial class MainViewModel
{
    private ProgressReader? _progressReader;
    private DateTimeOffset _progressShown;
    private DateTimeOffset _busySince;
    private DispatcherTimer? _noticeTimer;
    private DispatcherTimer? _trailingShow;

    /// <summary>The fraction of the running task done, 0 to 1, when it is known.</summary>
    [ObservableProperty]
    private double _busyFraction;

    /// <summary>Whether <see cref="BusyFraction"/> is known (else the bar just moves).</summary>
    [ObservableProperty]
    private bool _hasBusyFraction;

    /// <summary>The running task's detail: counts, what came from the cache, the time left, the last module.</summary>
    [ObservableProperty]
    private string _busyDetail = "";

    /// <summary>
    /// The fraction as a percentage, rounded down: 8,705 of 8,712 is 99%, not 100%, because the last jobs of a
    /// build are often the slow ones.
    /// </summary>
    [ObservableProperty]
    private string _busyPercent = "";

    /// <summary>The short form for the status bar: counts and the time left.</summary>
    [ObservableProperty]
    private string _busyShort = "";

    /// <summary>The slowest modules so far, for the progress banner.</summary>
    [ObservableProperty]
    private string _busySlowest = "";

    /// <summary>
    /// The stage of the long task, for the dashboard: <c>Toolchain</c>, <c>Cache</c>, <c>Build</c> or <c>Verify</c>,
    /// or empty for another task.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsStageToolchain), nameof(IsStageCache), nameof(IsStageBuild), nameof(IsStageVerify))]
    private string _busyStage = "";

    /// <summary>The stage is installing Lean.</summary>
    public bool IsStageToolchain => BusyStage == "Toolchain";

    /// <summary>The stage is fetching Mathlib's cache.</summary>
    public bool IsStageCache => BusyStage == "Cache";

    /// <summary>The stage is building.</summary>
    public bool IsStageBuild => BusyStage == "Build";

    /// <summary>The stage is Tenet's verification.</summary>
    public bool IsStageVerify => BusyStage == "Verify";

    partial void OnBusyTextChanged(string value) => BusyStage =
        value.Contains("Tenet", StringComparison.Ordinal) || value.StartsWith("Verifying", StringComparison.Ordinal) ? "Verify"
        : value.Contains("cache", StringComparison.OrdinalIgnoreCase) ? "Cache"
        : value.StartsWith("Downloading Lean", StringComparison.Ordinal) || value.StartsWith("Installing Lean", StringComparison.Ordinal) ? "Toolchain"
        : value.StartsWith("Build", StringComparison.Ordinal) ? "Build"
        : value.Length == 0 ? "" : BusyStage;

    /// <summary>The dashboard is open over the editor during a long task (it opens by itself when no file is).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowDashboard), nameof(ShowProgressBanner))]
    private bool _dashboardPinned;

    /// <summary>The dashboard shows: a long task with a known fraction, and no file open or the dashboard asked for.</summary>
    public bool ShowDashboard => HasBusyFraction && (DashboardPinned || !HasDocument);

    /// <summary>The slim banner over the editor shows instead of the dashboard.</summary>
    public bool ShowProgressBanner => HasBusyFraction && !ShowDashboard;

    partial void OnHasBusyFractionChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowDashboard));
        OnPropertyChanged(nameof(ShowProgressBanner));
    }

    /// <summary>Open or close the dashboard over the editor.</summary>
    [RelayCommand]
    private void ToggleDashboard() => DashboardPinned = !DashboardPinned;

    /// <summary>How long the long task has run, for the dashboard.</summary>
    [ObservableProperty]
    private string _busyElapsed = "";

    /// <summary>The modules being compiled now, for the dashboard.</summary>
    public ObservableList<DashboardLine> CompilingNow { get; } = new();

    /// <summary>The slowest modules so far, for the dashboard.</summary>
    public ObservableList<DashboardLine> SlowestSoFar { get; } = new();

    /// <summary>The latest warnings and errors, for the dashboard.</summary>
    public ObservableList<string> RecentMessages { get; } = new();

    /// <summary>How many warnings and errors so far, for the dashboard.</summary>
    [ObservableProperty]
    private string _messageCounts = "";

    /// <summary>A short note after a long task ends (how long it took, and what it found); empty otherwise.</summary>
    [ObservableProperty]
    private string _doneNotice = "";

    /// <summary>Start reading the output of a long task for its progress.</summary>
    private void BeginProgress()
    {
        _progressReader = new ProgressReader();
        _busySince = DateTimeOffset.Now;
        BusyFraction = 0;
        HasBusyFraction = false;
        BusyDetail = BusySlowest = BusyPercent = BusyShort = BusyElapsed = MessageCounts = "";
        DoneNotice = "";
        CompilingNow.Clear();
        SlowestSoFar.Clear();
        RecentMessages.Clear();
    }

    /// <summary>A line the running task printed.</summary>
    private void FeedProgress(string line)
    {
        if (_progressReader is not ProgressReader r || !r.Feed(line, DateTimeOffset.Now))
        {
            return;
        }
        // At most a few times a second: a build prints thousands of lines.
        DateTimeOffset now = DateTimeOffset.Now;
        if (now - _progressShown < TimeSpan.FromMilliseconds(200) && r.Progress.Done < r.Progress.Total)
        {
            // Not now, but soon: the last lines of a burst (a warning after a module) must still show.
            if (_trailingShow is null)
            {
                _trailingShow = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
                _trailingShow.Tick += (_, _) =>
                {
                    _trailingShow?.Stop();
                    _trailingShow = null;
                    if (_progressReader is ProgressReader latest)
                    {
                        _progressShown = DateTimeOffset.Now;
                        ShowProgress(latest.Progress);
                    }
                };
                _trailingShow.Start();
            }
            return;
        }
        _progressShown = now;
        ShowProgress(r.Progress);
    }

    private void ShowProgress(TaskProgress p)
    {
        if (p.Stage.Length > 0)
        {
            BusyText = p.Stage + "…";
        }
        HasBusyFraction = p.Fraction is not null;
        BusyFraction = p.Fraction ?? 0;
        BusyPercent = p.Fraction is double f ? $"{Math.Floor(f * 100):0}%" : "";
        BusyShort = p.Total == 0 ? "" : $"{p.Done:N0} / {p.Total:N0}" + (p.Remaining is TimeSpan left ? $" · about {TaskProgress.Format(left)} left" : "");
        BusyDetail = p.Detail + (p.Warnings > 0 ? $" · {p.Warnings} warning{(p.Warnings == 1 ? "" : "s")}" : "");
        BusySlowest = p.Slowest.Count == 0 ? "" : "Slowest: " + string.Join(", ", p.Slowest.Select(s => $"{s.Name} ({TaskProgress.Format(s.Took)})"));
        BusyElapsed = "Running for " + TaskProgress.Format(DateTimeOffset.Now - _busySince);
        if (SlowestSoFar.Count != p.Slowest.Count || SlowestSoFar.Select(s => s.Name).Zip(p.Slowest).Any(x => x.First != x.Second.Name))
        {
            SlowestSoFar.Reset(p.Slowest.Select(s => new DashboardLine(s.Name, TaskProgress.Format(s.Took))));
        }
        if (RecentMessages.Count != p.RecentMessages.Count || !RecentMessages.SequenceEqual(p.RecentMessages))
        {
            RecentMessages.Reset(p.RecentMessages);
        }
        MessageCounts = p.Warnings == 0 ? "No warnings or errors so far"
            : $"{p.Errors} error{(p.Errors == 1 ? "" : "s")}, {p.Warnings - p.Errors} warning{(p.Warnings - p.Errors == 1 ? "" : "s")} so far";
    }

    /// <summary>The long task ended: say how long it took and what it found, for a minute.</summary>
    private void EndProgress(string what, bool cancelled)
    {
        TaskProgress? p = _progressReader?.Progress;
        _progressReader = null;
        HasBusyFraction = false;
        TimeSpan took = DateTimeOffset.Now - _busySince;
        if (cancelled || took < TimeSpan.FromSeconds(30))
        {
            return;
        }
        string found = p is null ? "" : p.Errors > 0 ? $": {p.Errors} error{(p.Errors == 1 ? "" : "s")}" : p.Warnings > 0 ? $": {p.Warnings} warning{(p.Warnings == 1 ? "" : "s")}" : "";
        DoneNotice = $"✓ {what.TrimEnd('…', '.')} took {TaskProgress.Format(took)}{found}";
        Log(DoneNotice + (p?.Slowest.Count > 0 ? ". Slowest: " + string.Join(", ", p.Slowest.Select(s => $"{s.Name} ({TaskProgress.Format(s.Took)})")) : ""));
        _noticeTimer?.Stop();
        _noticeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
        _noticeTimer.Tick += (_, _) =>
        {
            DoneNotice = "";
            _noticeTimer?.Stop();
        };
        _noticeTimer.Start();
    }

    /// <summary>Put away the note about the last long task.</summary>
    [RelayCommand]
    private void DismissDoneNotice() => DoneNotice = "";

    /// <summary>What the Lean server's start says about downloading or installing its toolchain, for the status bar.</summary>
    private void ServerLogLine(string line)
    {
        var r = new ProgressReader();
        if (r.Feed(line, DateTimeOffset.Now)
            && (r.Progress.Stage.StartsWith("Downloading Lean", StringComparison.Ordinal) || r.Progress.Stage.StartsWith("Installing Lean", StringComparison.Ordinal)))
        {
            Dispatcher.UIThread.Post(() => ServerStatus = "Lean: " + char.ToLowerInvariant(r.Progress.Stage[0]) + r.Progress.Stage[1..] + "…");
        }
    }
}

/// <summary>A line of the build dashboard: a module, and a time.</summary>
/// <param name="Name">The module.</param>
/// <param name="Time">How long, as a person reads it.</param>
public sealed record DashboardLine(string Name, string Time);

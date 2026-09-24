using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LeanStudio.Core.Projects;
using LeanStudio.Core.Proofs;
using LeanStudio.Core.Verification;
using LeanStudio.Core.Workflow;
using LeanStudio.Lsp;

namespace LeanStudio.App.ViewModels;

/// <summary>A declaration's time in the Timing panel.</summary>
/// <param name="Timing">The declaration's time, from Lean's profiler.</param>
/// <param name="Slowest">
/// The slowest declaration's time in the same profile, in seconds, which gets the full-width bar.
/// </param>
/// <param name="Document">The file profiled, to go to the declaration in.</param>
public sealed record TimingItem(DeclarationTiming Timing, double Slowest, DocumentViewModel Document)
{
    /// <summary>The declaration's 1-based line, as <c>line N</c>.</summary>
    public string Where => $"line {Timing.Line + 1}";
    /// <summary>How long Lean took to check it, formatted.</summary>
    public string Time => Timing.Time;
    /// <summary>The declaration's name.</summary>
    public string Declaration => Timing.Declaration;
    /// <summary>
    /// The step inside the declaration that took longest, and its time; empty when the profile names none.
    /// </summary>
    public string HotSpot => Timing.HotSpot is string h ? $"slowest part: {h} ({DeclarationTiming.Format(Timing.HotSpotSeconds)})" : "";
    /// <summary>It took a second or more.</summary>
    public bool IsHot => Timing.Heat == 2;
    /// <summary>It took from a tenth of a second up to a second.</summary>
    public bool IsWarm => Timing.Heat == 1;
    /// <summary>The bar's width in pixels: up to 120, in proportion to the slowest, and at least 2.</summary>
    public double BarWidth => Math.Max(2, 120 * Timing.Seconds / Math.Max(Slowest, 1e-9));
}

/// <summary>One REPL input and its result.</summary>
/// <param name="Input">What was typed.</param>
/// <param name="Output">Lean's answer, or why there was none.</param>
/// <param name="IsError">The answer is an error.</param>
/// <param name="Where">
/// The file and 1-based line whose context it ran in, as <c>File.lean:12</c>; empty if it did not run.
/// </param>
public sealed record ReplEntry(string Input, string Output, bool IsError, string Where);

/// <summary>
/// Five things no other Lean editor does out of the box: Prove It (a portfolio of tactics raced against every
/// sorry), why a theorem is not fully proved (the chain down to the sorry, from Tenet), a performance heat map,
/// proof walkthroughs anyone can read in a browser (and share links to the Lean 4 web editor), and search of
/// Mathlib in plain English (in the Library panel).
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>The Timing and REPL panels' indices in <see cref="BottomTab"/>.</summary>
    public const int TimingPanel = 6, ReplPanel = 7;

    private CancellationTokenSource? _proveCts;
    private DocumentViewModel? _proveDoc;

    private void InitAssist()
    {
        Info.Search.Run = ProveAsync;
        Info.Search.ApplyTrial = ApplyTrial;
        Info.Search.Cancel = () => _proveCts?.Cancel();
        Info.Search.Extract = r => _ = ExtractFromSearchAsync(r);
        Info.ShowWidgets = ShowInfoview;
        Verification.Explain = WhyNotProvedAsync;
        Verification.OpenRequested += (file, line) => _ = OpenFileAsync(file, line - 1, 0);
    }

    // ---- Prove It ----

    /// <summary>Run Prove It on the sorry at the caret.</summary>
    [RelayCommand]
    private Task ProveItAsync() => ProveAsync(false);

    /// <summary>Run Prove It on every sorry in the active file.</summary>
    [RelayCommand]
    private Task ProveAllSorriesAsync() => ProveAsync(true);

    /// <summary>Try the portfolio on the sorry at the caret, or on every sorry in the file.</summary>
    /// <remarks>
    /// Shows the Prove It card and reports there, including when there is no Lean file, no server or no sorry. The
    /// search runs in Lean on a copy of the file, so the file itself is not changed; a newer search cancels an older
    /// one, and a search stops after five minutes.
    /// </remarks>
    /// <param name="all">True for every sorry in the file; false for the one at the caret.</param>
    public async Task ProveAsync(bool all)
    {
        ProofSearchViewModel ps = Info.Search;
        RightTab = GoalsTab;
        ps.IsVisible = true;
        if (ActiveDocument is not { IsLean: true } d)
        {
            ps.Status = "Open a Lean file, put the cursor on a sorry, and Prove It tries a portfolio of tactics on that goal.";
            return;
        }
        if (_server is not { State: LeanServerState.Running } server)
        {
            ps.Status = "Lean is not running yet.";
            return;
        }
        string text = d.Document.Text;
        IReadOnlyList<SorrySite> sites = ProofSearch.Sites(text);
        if (!all)
        {
            sites = ProofSearch.At(sites, d.CaretLine, d.CaretColumn) is SorrySite one ? [one] : [];
        }
        if (sites.Count == 0)
        {
            ps.Results.Reset([]);
            ps.Status = "There is no sorry in this file. Write sorry where a proof should go, and Prove It tries a portfolio of tactics on that goal.";
            return;
        }
        _proveCts?.Cancel();
        var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        _proveCts = cts;
        _proveDoc = d;
        ps.IsRunning = true;
        ps.Results.Reset([]);
        ps.Status = sites.Count == 1
            ? $"Trying {ProofSearch.Portfolio.Count} tactics on the sorry at {sites[0].Where}…"
            : $"Trying {ProofSearch.Portfolio.Count} tactics on each of the {sites.Count} sorries in this file…";
        try
        {
            IReadOnlyList<SearchResult> results = await ProofSearch.RunAsync(server, d.Path, text, sites, cts.Token);
            if (_proveCts != cts)
            {
                return;
            }
            ps.Show(results);
            if (d.Document.Text != text)
            {
                ps.Status += " (The file changed while searching; each fill checks its sorry is still there.)";
            }
            Log($"Prove It: {results.Count(r => r.Best is not null)} of {results.Count} sorr{(results.Count == 1 ? "y" : "ies")} in {Path.GetFileName(d.Path)} can be closed by a tactic.");
        }
        catch (OperationCanceledException)
        {
            if (_proveCts == cts)
            {
                ps.Status = cts.IsCancellationRequested ? "Stopped: the search took more than five minutes." : "Stopped.";
            }
        }
        catch (Exception e) when (e is JsonRpcException or IOException or InvalidOperationException)
        {
            ps.Status = "Lean could not run the search: " + e.Message;
        }
        finally
        {
            if (_proveCts == cts)
            {
                ps.IsRunning = false;
            }
        }
    }

    private async Task ExtractFromSearchAsync(SearchResultView r)
    {
        if (_proveDoc is not DocumentViewModel d || !Documents.Contains(d))
        {
            return;
        }
        ActiveDocument = d;
        SorrySite now = r.Result.Site with { Offset = r.Offset };
        if (await ExtractLemmaAtAsync(null, now) is null)
        {
            // The lemma moved everything below it; the other results' places are stale.
            Info.Search.Results.Reset([]);
            Info.Search.Status = "Extracted the goal as a lemma above the declaration. Run Prove It again for the other sorries.";
        }
    }

    /// <summary>Put a tactic that works in place of its sorry, as an ordinary (undoable) edit.</summary>
    private void ApplyTrial(SearchResultView r, TacticTrial t)
    {
        ProofSearchViewModel ps = Info.Search;
        if (_proveDoc is not DocumentViewModel d || !Documents.Contains(d))
        {
            ps.Status = "That file is no longer open.";
            return;
        }
        if (ActiveDocument != d)
        {
            ActiveDocument = d;
        }
        AvaloniaEdit.Document.TextDocument doc = d.Document;
        int off = r.Offset, len = r.Result.Site.Length;
        if (off < 0 || off + len > doc.TextLength || doc.GetText(off, len) is not ("sorry" or "admit"))
        {
            ps.Status = $"The file has changed where the sorry at {r.Result.Site.Where} was. Run Prove It again.";
            return;
        }
        string fill = r.Result.Fill(t);
        doc.Replace(off, len, fill);
        int delta = fill.Length - len;
        foreach (SearchResultView other in ps.Results)
        {
            if (other != r && other.Offset > off)
            {
                other.Offset += delta;
            }
        }
        r.IsApplied = true;
        ps.UpdateCanFillAll();
        Log($"Prove It: {r.Result.Site.Where} of {Path.GetFileName(d.Path)} is now proved by {fill}");
    }

    // ---- the REPL: Lean in the context of the file at the cursor ----

    private readonly LeanRepl _repl = new();
    private readonly List<string> _replHistory = [];
    private int _replHistoryAt;

    /// <summary>The REPL panel's inputs and results, oldest first.</summary>
    public ObservableList<ReplEntry> ReplEntries { get; } = new();

    /// <summary>The REPL's input box.</summary>
    [ObservableProperty]
    private string _replInput = "";

    /// <summary>Lean is evaluating a REPL input; another is not accepted until it answers.</summary>
    [ObservableProperty]
    private bool _replBusy;

    /// <summary>
    /// Evaluate <see cref="ReplInput"/> in the context of the active Lean file at the caret (its text up to the end
    /// of the declaration there, so its imports and definitions are in scope), add the result to
    /// <see cref="ReplEntries"/>, and remember the input for <see cref="ReplHistory"/>. Gives up after two minutes.
    /// The file is not changed.
    /// </summary>
    [RelayCommand]
    public async Task RunReplAsync()
    {
        string input = ReplInput.Trim();
        if (input.Length == 0 || ReplBusy)
        {
            return;
        }
        if (ActiveDocument is not { IsLean: true } d || _server is not { State: LeanServerState.Running } server)
        {
            ReplEntries.Add(new ReplEntry(input, "Open a Lean file: the REPL runs in its context, at the cursor.", true, ""));
            return;
        }
        _replHistory.Remove(input);
        _replHistory.Add(input);
        _replHistoryAt = _replHistory.Count;
        ReplInput = "";
        ReplBusy = true;
        string where = $"{Path.GetFileName(d.Path)}:{d.CaretLine + 1}";
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            ReplResult r = await _repl.EvalAsync(server, d.Path, LeanRepl.Context(d.Document.Text, d.CaretLine), input, timeout.Token);
            ReplEntries.Add(new ReplEntry(input, r.Output, r.IsError, where));
        }
        catch (Exception e) when (e is OperationCanceledException or JsonRpcException or IOException)
        {
            ReplEntries.Add(new ReplEntry(input, "Lean did not answer: " + e.Message, true, where));
        }
        finally
        {
            ReplBusy = false;
        }
    }

    /// <summary>Step through earlier inputs (-1 older, +1 newer), as a shell does with the arrow keys.</summary>
    /// <param name="delta">-1 for the previous input, +1 for the next; past the newest the box is emptied.</param>
    public void ReplHistory(int delta)
    {
        if (_replHistory.Count == 0)
        {
            return;
        }
        _replHistoryAt = Math.Clamp(_replHistoryAt + delta, 0, _replHistory.Count);
        ReplInput = _replHistoryAt < _replHistory.Count ? _replHistory[_replHistoryAt] : "";
    }

    /// <summary>Clear the REPL panel.</summary>
    [RelayCommand]
    private void ClearRepl() => ReplEntries.Reset([]);

    // ---- extract a goal as a lemma ----

    /// <summary>Extract the goal at the sorry at the caret as a lemma, asking for its name.</summary>
    [RelayCommand]
    private async Task ExtractLemmaAsync() => await ExtractLemmaAtAsync(null);

    /// <summary>
    /// Turn the goal at the sorry nearest the caret into a lemma above the declaration, and use it there. Asks
    /// for the lemma's name unless one is given. Returns what went wrong, or null.
    /// </summary>
    /// <remarks>
    /// Lean works out the lemma's statement from the goal (for up to three minutes); the file is then edited in one
    /// undoable step and left unsaved. If the file changes while Lean works, nothing is edited.
    /// </remarks>
    /// <param name="name">The lemma's name, or null to ask for one.</param>
    /// <param name="at">The sorry to use, or null for the one at the caret.</param>
    public async Task<string?> ExtractLemmaAtAsync(string? name, SorrySite? at = null)
    {
        if (ActiveDocument is not { IsLean: true } d || _server is not { State: LeanServerState.Running } server)
        {
            return "Open a Lean file first.";
        }
        string text = d.Document.Text;
        SorrySite? site = at ?? ProofSearch.At(ProofSearch.Sites(text), d.CaretLine, d.CaretColumn);
        if (site is null)
        {
            Log("Extract as lemma: put the cursor on a sorry. Its goal, with the hypotheses it needs, becomes a lemma of its own.");
            return "There is no sorry here.";
        }
        string suggested = (site.Declaration is { Length: > 0 } decl && decl != "example" ? decl.Split('.')[^1] : "step") + "_aux";
        name ??= await _dialogs.PromptAsync("Extract as lemma",
            $"The goal at {site.Where}, with the hypotheses it needs, becomes a lemma above the declaration, and the sorry becomes a use of it. Name:",
            suggested);
        if (name is null)
        {
            return "Cancelled.";
        }
        name = name.Trim();
        if (!ExtractLemma.IsValidName(name))
        {
            Log($"Extract as lemma: {name} is not a name Lean accepts.");
            return "Not a valid name.";
        }
        IsBusy = true;
        BusyText = "Asking Lean for the goal's lemma…";
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            ExtractedLemma? lemma = await ExtractLemma.RunAsync(server, d.Path, text, site, name, timeout.Token);
            if (lemma is null)
            {
                Log($"Extract as lemma: Lean did not reach the sorry at {site.Where} (fix the errors before it first).");
                return "Lean did not reach the sorry.";
            }
            if (d.Document.Text != text)
            {
                Log("Extract as lemma: the file changed meanwhile; try again.");
                return "The file changed.";
            }
            // One edit, so one undo takes it all back: the use first (it is below), then the lemma.
            AvaloniaEdit.Document.TextDocument doc = d.Document;
            doc.BeginUpdate();
            try
            {
                doc.Replace(site.Offset, site.Length, lemma.Call);
                int insertAt = doc.GetLineByNumber(Math.Clamp(lemma.InsertLine + 1, 1, doc.LineCount)).Offset;
                doc.Insert(insertAt, lemma.Text);
            }
            finally
            {
                doc.EndUpdate();
            }
            d.Reveal(lemma.InsertLine, 8);
            Log($"Extracted {lemma.Name} above {site.Declaration ?? "the declaration"}; {site.Where} now uses it. Prove it there (⌘⌥P / Ctrl+Alt+P tries tactics).");
            return null;
        }
        catch (Exception e) when (e is OperationCanceledException or JsonRpcException or IOException)
        {
            Log("Extract as lemma: " + e.Message);
            return e.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    // ---- why a theorem is not fully proved ----

    /// <summary>
    /// Explain why the declaration at the caret is not fully proved (see <see cref="WhyNotProvedAsync"/>).
    /// </summary>
    [RelayCommand]
    private async Task WhyNotProvedAtCaretAsync()
    {
        if (ActiveDocument is not { IsLean: true } d || EmittedC.DeclarationAt(d.Lines(), d.CaretLine) is not { Name.Length: > 0 } decl)
        {
            Verification.TrailTitle = "Put the cursor in a theorem or definition, then ask why it is not fully proved.";
            Verification.Trails.Reset([]);
            Verification.HasTrail = true;
            BottomTab = TenetPanel;
            return;
        }
        await WhyNotProvedAsync(decl.Name);
    }

    /// <summary>Show, in the Tenet panel, the chains from a declaration down to each sorry or axiom it rests on.</summary>
    /// <remarks>
    /// Reads the last build (opening it with Tenet if needed); says so in the panel if there is none, or the
    /// declaration is not in it.
    /// </remarks>
    /// <param name="name">The declaration's full name.</param>
    public async Task WhyNotProvedAsync(string name)
    {
        BottomTab = TenetPanel;
        Verification.Trails.Reset([]);
        Verification.HasTrail = true;
        if (_tenet is null || _tenet.OwnModules.Count == 0)
        {
            await ReopenTenetAsync();
        }
        if (_tenet is not TenetWorkspace ws || ws.OwnModules.Count == 0)
        {
            Verification.TrailTitle = "Build the project first (Lean ▸ Build Project): Tenet reads what Lean built.";
            return;
        }
        if (ws.Details(name) is null)
        {
            Verification.TrailTitle = $"{name} is not in the last build. Build the project (Lean ▸ Build Project), then ask again.";
            return;
        }
        Verification.TrailTitle = $"Tracing what {name} rests on…";
        try
        {
            IReadOnlyList<AssumptionTrail> trails = await OnLargeStack(() => ws.WhyNotProved(name));
            Verification.ShowTrails(name, trails);
        }
        catch (Exception e) when (e is Tenet.Kernel.KernelException or IOException or InvalidOperationException)
        {
            Verification.TrailTitle = "Tenet could not trace it: " + e.Message;
        }
    }

    // ---- the project map ----

    /// <summary>Raised with a computed map, for the window to show.</summary>
    /// <remarks>Raised on the UI thread, by <see cref="ShowProjectMapAsync"/>.</remarks>
    public event Action<ProjectMap>? ProjectMapReady;

    /// <summary>
    /// Map the project's declarations and which uses which, from the last build read by Tenet, and raise
    /// <see cref="ProjectMapReady"/> with it. Logs why instead when nothing is built yet or Tenet fails.
    /// </summary>
    /// <returns>The map, or null if there is none.</returns>
    [RelayCommand]
    public async Task<ProjectMap?> ShowProjectMapAsync()
    {
        if (_tenet is null || _tenet.OwnModules.Count == 0)
        {
            await ReopenTenetAsync();
        }
        if (_tenet is not TenetWorkspace ws || ws.OwnModules.Count == 0)
        {
            Log("Project map: build the project first (Lean ▸ Build Project). The map is read from what Lean built.");
            return null;
        }
        IsBusy = true;
        BusyText = "Mapping the project…";
        try
        {
            ProjectMap map = await OnLargeStack(() => ws.Map());
            Log($"Project map: {map.Nodes.Count} declarations, {map.Edges.Count} uses; {map.Nodes.Count(n => n.Status == MapStatus.RestsOnSorry)} rest on sorry.");
            ProjectMapReady?.Invoke(map);
            return map;
        }
        catch (Exception e) when (e is Tenet.Kernel.KernelException or IOException or InvalidOperationException)
        {
            Log("Project map: " + e.Message);
            return null;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Open a declaration from the map in the editor.</summary>
    /// <param name="n">The declaration; one without a source file is ignored.</param>
    public void OpenMapNode(MapNode n)
    {
        if (n.SourceFile is string f)
        {
            _ = OpenFileAsync(f, (n.Line ?? 1) - 1, 0);
        }
    }

    /// <summary>Tenet walks terms recursively; deep ones need more stack than a thread-pool thread has.</summary>
    private static Task<T> OnLargeStack<T>(Func<T> work)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                tcs.SetResult(work());
            }
            catch (Exception e)
            {
                tcs.SetException(e);
            }
        }, 256 * 1024 * 1024)
        {
            IsBackground = true,
            Name = "Tenet trail",
        };
        thread.Start();
        return tcs.Task;
    }

    // ---- performance heat map ----

    /// <summary>The Timing panel: the last profiled file's declarations, slowest first.</summary>
    public ObservableList<TimingItem> TimingItems { get; } = new();

    /// <summary>What the Timing panel says above its list: how to profile, progress, or the total.</summary>
    [ObservableProperty]
    private string _timingStatus = "Lean ▸ Profile File runs Lean's profiler over the file and shows how long each declaration takes to check, slowest first, with the step inside it that costs the most.";

    /// <summary>A profile is running; another is not started until it finishes.</summary>
    [ObservableProperty]
    private bool _isProfiling;

    /// <summary>
    /// Run the active Lean file's current text through Lean's profiler (a separate <c>lean</c> process, not the server)
    /// and show each declaration's time in the Timing panel. The times are also shown in the editor until the next edit.
    /// Works without a project too, using the file's folder.
    /// </summary>
    [RelayCommand]
    public async Task ProfileFileAsync()
    {
        if (ActiveDocument is not { IsLean: true } d || IsProfiling)
        {
            return;
        }
        LeanProject project = Project ?? new LeanProject(Path.GetDirectoryName(d.Path)!);
        BottomTab = TimingPanel;
        IsProfiling = true;
        TimingStatus = $"Profiling {Path.GetFileName(d.Path)}: Lean is checking it with its profiler on…";
        string text = d.Document.Text;
        try
        {
            var (timings, error) = await Profiler.RunAsync(project, d.Path, text);
            if (error is not null)
            {
                TimingItems.Reset([]);
                TimingStatus = "Lean could not profile this file. " + error.Split('\n')[0];
                Log("Profile: " + error);
                return;
            }
            if (d.Document.Text == text)
            {
                d.Timings = timings;
            }
            double slowest = timings.Count == 0 ? 1 : timings.Max(t => t.Seconds);
            TimingItems.Reset(timings.Select(t => new TimingItem(t, slowest, d)));
            TimingStatus = timings.Count == 0
                ? "Nothing in this file takes Lean more than a few milliseconds to check."
                : $"{Path.GetFileName(d.Path)}: {DeclarationTiming.Format(timings.Sum(t => t.Seconds))} across {timings.Count} declaration{(timings.Count == 1 ? "" : "s")}, slowest first. "
                  + "Click one to go to it; the times are in the editor too, until the next edit.";
        }
        catch (Exception e) when (e is IOException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            TimingStatus = "Could not run Lean: " + e.Message;
        }
        finally
        {
            IsProfiling = false;
        }
    }

    /// <summary>Go to a profiled declaration, if its file is still open.</summary>
    /// <param name="t">The declaration; null does nothing.</param>
    [RelayCommand]
    private void OpenTiming(TimingItem? t)
    {
        if (t is null || !Documents.Contains(t.Document))
        {
            return;
        }
        ActiveDocument = t.Document;
        t.Document.Reveal(t.Timing.Line, 0);
    }

    // ---- walkthroughs and share links ----

    /// <summary>Ask where to save the active file's proof walkthrough, write it, and open it in the browser.</summary>
    [RelayCommand]
    private async Task ExportWalkthroughAsync()
    {
        if (ActiveDocument is not { IsLean: true } d)
        {
            return;
        }
        string? path = await _dialogs.SaveWebPageAsync("Export proof walkthrough", Path.GetFileNameWithoutExtension(d.Path) + "-walkthrough.html", Path.GetDirectoryName(d.Path));
        if (path is not null && await WriteWalkthroughAsync(path))
        {
            await _dialogs.LaunchAsync(new Uri(path));
        }
    }

    /// <summary>Write the walkthrough of the active file's proofs to <paramref name="path"/>.</summary>
    /// <remarks>
    /// Waits for Lean to finish checking the file (up to five minutes), then builds the page from what Lean says
    /// about each tactic proof. Problems are logged, not thrown.
    /// </remarks>
    /// <param name="path">The HTML file to write; replaced if it exists.</param>
    /// <returns>
    /// True if the page was written; false with no Lean file or server, no tactic proofs, or an error.
    /// </returns>
    public async Task<bool> WriteWalkthroughAsync(string path)
    {
        if (ActiveDocument is not { IsLean: true } d || _server is not { State: LeanServerState.Running } server)
        {
            return false;
        }
        IsBusy = true;
        BusyText = "Writing the walkthrough…";
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            await server.WaitForElaborationAsync(d.Uri, timeout.Token);
            IReadOnlyList<WalkProof> proofs = await Walkthrough.BuildAsync(server, d.Uri, d.Lines(), timeout.Token);
            if (proofs.Count == 0)
            {
                Log("There are no tactic proofs (… := by …) in this file to walk through.");
                return false;
            }
            await File.WriteAllTextAsync(path, Walkthrough.Html(Path.GetFileName(d.Path), proofs, d.Document.Text));
            Log($"Wrote a walkthrough of {proofs.Count} proof{(proofs.Count == 1 ? "" : "s")} to {path}");
            return true;
        }
        catch (Exception e) when (e is OperationCanceledException or JsonRpcException or IOException or UnauthorizedAccessException)
        {
            Log("Could not write the walkthrough: " + e.Message);
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Open the active file in the Lean 4 web editor, in the browser.</summary>
    [RelayCommand]
    private async Task OpenInWebEditorAsync()
    {
        if (await ShareUrlAsync() is string url)
        {
            await _dialogs.LaunchAsync(new Uri(url));
        }
    }

    /// <summary>Copy a link that opens the active file in the Lean 4 web editor.</summary>
    [RelayCommand]
    private async Task CopyShareLinkAsync()
    {
        if (await ShareUrlAsync() is string url)
        {
            await _dialogs.CopyTextAsync(url);
            Log("Copied a link that opens this file in the Lean 4 web editor. Anyone with it can run the code in their browser.");
        }
    }

    private async Task<string?> ShareUrlAsync()
    {
        if (ActiveDocument is not { IsLean: true } d)
        {
            return null;
        }
        string text = d.Document.Text;
        IReadOnlyList<string> missing = Walkthrough.MissingOnWeb(text);
        if (missing.Count > 0 && !await _dialogs.ConfirmAsync("Share this file?",
                $"It imports {string.Join(", ", missing.Take(3))}{(missing.Count > 3 ? " and more" : "")}, which the web editor does not have "
                + "(it has Lean, Std, Batteries and Mathlib), so those imports will fail there. Share it anyway?"))
        {
            return null;
        }
        return Walkthrough.ShareUrl(text);
    }
}

using CommunityToolkit.Mvvm.Input;
using LeanStudio.Core.Proofs;
using LeanStudio.Lsp;

namespace LeanStudio.App.ViewModels;

/// <summary>What a proof-state map was made from: the steps, and which files they came from.</summary>
/// <param name="Scope">What was mapped, for the window's title (a file name, or the project's).</param>
/// <param name="Root">The folder the steps' file names are relative to.</param>
/// <param name="Steps">Every tactic step, with the goals before and after it.</param>
/// <param name="Files">How many files were read.</param>
public sealed record ProofStatesResult(string Scope, string Root, IReadOnlyList<StateStep> Steps, int Files);

public partial class MainViewModel
{
    /// <summary>Raised with collected proof states, for the window to show.</summary>
    /// <remarks>Raised on the UI thread, by <see cref="CollectProofStatesAsync"/>.</remarks>
    public event Action<ProofStatesResult>? ProofStatesReady;

    /// <summary>Map the proof states of the file in the editor.</summary>
    [RelayCommand]
    private async Task ShowProofStatesForFileAsync() => await CollectProofStatesAsync(wholeProject: false);

    /// <summary>Map the proof states of every Lean file in the project.</summary>
    [RelayCommand]
    private async Task ShowProofStatesForProjectAsync() => await CollectProofStatesAsync(wholeProject: true);

    /// <summary>
    /// Ask Lean for the goals before and after every tactic, in the active file or in every source file of the
    /// project, and raise <see cref="ProofStatesReady"/> with them. A file that is not open is opened in Lean for
    /// the purpose and closed again. Problems are logged, not thrown.
    /// </summary>
    /// <param name="wholeProject">Every source file of the project, instead of the active file.</param>
    /// <returns>The result, or null with nothing to map or no Lean server.</returns>
    public async Task<ProofStatesResult?> CollectProofStatesAsync(bool wholeProject)
    {
        if (_server is not { State: LeanServerState.Running } server)
        {
            Log("Proof-state map: Lean isn't running yet.");
            return null;
        }
        string root = Project?.Root ?? (ActiveDocument is { } a ? Path.GetDirectoryName(a.Path) ?? "." : ".");
        List<string> files;
        if (wholeProject)
        {
            if (Project is null)
            {
                Log("Proof-state map: open a project first.");
                return null;
            }
            files = Project.SourceFiles().Where(f => !Path.GetFileName(f).StartsWith("LeanStudio", StringComparison.Ordinal)).OrderBy(f => f, StringComparer.Ordinal).ToList();
        }
        else if (ActiveDocument is { IsLean: true } d)
        {
            files = [d.Path];
        }
        else
        {
            Log("Proof-state map: open a Lean file first.");
            return null;
        }

        IsBusy = true;
        var steps = new List<StateStep>();
        try
        {
            for (int i = 0; i < files.Count; i++)
            {
                string path = files[i];
                string rel = Path.GetRelativePath(root, path).Replace('\\', '/');
                BusyText = files.Count == 1 ? "Reading the proof states…" : $"Reading proof states: {rel} ({i + 1} of {files.Count})…";
                DocumentViewModel? open = Documents.FirstOrDefault(x => string.Equals(x.Path, path, StringComparison.Ordinal));
                string uri = open?.Uri ?? LeanServer.UriOf(path);
                string text = open?.Document.Text ?? await File.ReadAllTextAsync(path);
                bool opened = false;
                using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
                try
                {
                    if (!server.IsOpen(uri))
                    {
                        await server.OpenAsync(uri, text);
                        opened = true;
                    }
                    await server.WaitForElaborationAsync(uri, timeout.Token);
                    steps.AddRange(await ProofStates.CollectAsync(server, uri, rel, text.Replace("\r", "", StringComparison.Ordinal).Split('\n'), timeout.Token));
                }
                catch (Exception e) when (e is OperationCanceledException or JsonRpcException or IOException)
                {
                    Log($"Proof-state map: skipped {rel}: {e.Message}");
                }
                finally
                {
                    if (opened && server.State == LeanServerState.Running && server.IsOpen(uri))
                    {
                        await server.CloseAsync(uri);
                    }
                }
            }
        }
        finally
        {
            IsBusy = false;
        }
        string scope = wholeProject ? ProjectName : Path.GetFileName(files[0]);
        var result = new ProofStatesResult(scope, root, steps, files.Count);
        int shared = ProofStateMap.Build(steps, StateMatch.Exact).Shared.Count;
        Log($"Proof-state map of {scope}: {steps.Count} tactic steps in {files.Count} file{(files.Count == 1 ? "" : "s")}; {shared} state{(shared == 1 ? "" : "s")} reached by more than one proof.");
        ProofStatesReady?.Invoke(result);
        return result;
    }

    /// <summary>Open the file at a place a proof passes through a state.</summary>
    public void OpenStateVisit(string root, StateVisit v) => _ = OpenFileAsync(Path.Combine(root, v.File), v.Line, v.Column);

    /// <summary>
    /// Extract a shared state as a lemma: Lean works out the lemma's statement from the goal where the first proof
    /// reaches it, and the lemma goes above that proof with a <c>sorry</c> to fill in. The proofs themselves are
    /// left alone: using the lemma in each of them is a choice for the person, so the log says where they are.
    /// Returns what went wrong, or null.
    /// </summary>
    /// <param name="root">The folder the visits' file names are relative to.</param>
    /// <param name="node">The shared state.</param>
    public async Task<string?> ExtractSharedStateAsync(string root, StateNode node)
    {
        if (_server is not { State: LeanServerState.Running } server)
        {
            return "Lean isn't running.";
        }
        StateVisit first = node.Visits[0];
        DocumentViewModel? d = await OpenFileAsync(Path.Combine(root, first.File), first.Line, first.Column);
        if (d is null)
        {
            return "Could not open " + first.File + ".";
        }
        string suggested = first.Declaration.Split('.')[^1] + "_step";
        string? name = await _dialogs.PromptAsync("Extract shared state as lemma",
            $"{node.Declarations.Count} proofs reach this state. It becomes a lemma above {first.Declaration}, to prove once and use in each of them. Name:",
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
        string text = d.Document.Text;
        // Ask for the goal right where the proof reaches the state, in a scratch copy (the file is not touched).
        (string probe, SorrySite site) = ProofStates.ProbeAt(text, first);
        IsBusy = true;
        BusyText = "Asking Lean for the state's lemma…";
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            ExtractedLemma? lemma = await ExtractLemma.RunAsync(server, d.Path, probe, site, name, timeout.Token);
            if (lemma is null)
            {
                Log($"Extract as lemma: Lean did not reach line {first.Line + 1} of {first.File} (fix the errors before it first).");
                return "Lean did not reach the state.";
            }
            if (d.Document.Text != text)
            {
                Log("Extract as lemma: the file changed meanwhile; try again.");
                return "The file changed.";
            }
            int insertAt = d.Document.GetLineByNumber(Math.Clamp(lemma.InsertLine + 1, 1, d.Document.LineCount)).Offset;
            d.Document.Insert(insertAt, lemma.Text);
            d.Reveal(lemma.InsertLine, 8);
            string where = string.Join(", ", node.Visits.Select(v => $"{v.File}:{v.Line + 1 + (v.File == first.File && v.Line >= lemma.InsertLine ? lemma.Text.Count(c => c == '\n') : 0)} ({v.Declaration})").Distinct().Take(12));
            Log($"Extracted {lemma.Name} above {first.Declaration}. Prove it once (⌘⌥P / Ctrl+Alt+P tries tactics), then `{lemma.Call}` can close this state in: {where}.");
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
}

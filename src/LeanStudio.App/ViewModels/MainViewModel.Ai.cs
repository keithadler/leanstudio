using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LeanStudio.Core.Ai;
using LeanStudio.Core.Proofs;
using LeanStudio.Lsp;

namespace LeanStudio.App.ViewModels;

/// <summary>
/// The AI built into the editor. It prefers a model on this computer: Apple's on-device model on macOS 27, or
/// Ollama, LM Studio, llama.cpp or MLX when one is running; a cloud model (Claude, or any OpenAI-compatible service)
/// only when the person has chosen one or allowed it. Whatever the model suggests as a proof is checked by Lean
/// before it is offered, through the same trials Prove It runs.
/// </summary>
public sealed partial class MainViewModel
{
    private AiDiscovery? _aiDiscovery;
    private SecretStore? _secrets;
    private IChatModel? _aiModel;
    private AiConfig? _aiModelConfig;
    private readonly SemaphoreSlim _aiGate = new(1, 1);
    private CancellationTokenSource? _aiProveCts;

    /// <summary>Where API keys for cloud models are kept (the Keychain on macOS).</summary>
    public SecretStore Secrets => _secrets ??= new SecretStore(Services.Settings.Directory);

    /// <summary>Finds the models this computer can use.</summary>
    public AiDiscovery AiDiscovery => _aiDiscovery ??= new AiDiscovery(Secrets);

    /// <summary>The model in use, for the status line and the AI window: its name and where it runs.</summary>
    [ObservableProperty]
    private string _aiModelName = "";

    /// <summary>Raised to open the AI window with a question already asked (or none: an empty chat).</summary>
    /// <remarks>The window builds the question's context with <see cref="AiContext"/>.</remarks>
    public event Action<string?>? AiChatRequested;

    private void InitAi()
    {
        Info.Search.AskAi = AskAiAboutResultAsync;
    }

    /// <summary>
    /// The model to use now: the one chosen in AI ▸ Choose a Model, or the best one running. Looked up once and kept
    /// until the settings change (<see cref="ForgetAiModel"/>) or it fails.
    /// </summary>
    /// <returns>The model, or null and why there is none, in words that say what to do.</returns>
    public async Task<(IChatModel? Model, string Why)> AiModelAsync(CancellationToken ct = default)
    {
        AiConfig config = Settings.ToAiConfig();
        await _aiGate.WaitAsync(ct);
        try
        {
            if (_aiModel is not null && config == _aiModelConfig)
            {
                return (_aiModel, "");
            }
            (IChatModel? m, string why) = await AiDiscovery.ChooseAsync(config, ct);
            _aiModel = m;
            _aiModelConfig = m is null ? null : config;
            AiModelName = m is null ? "" : m.DisplayName + (m.Location == AiLocation.Cloud ? " (cloud)" : m.Location == AiLocation.OnDevice ? " (on this Mac)" : " (local)");
            return (m, why);
        }
        finally
        {
            _aiGate.Release();
        }
    }

    /// <summary>Look for the model again next time: the settings changed, or the model stopped answering.</summary>
    public void ForgetAiModel()
    {
        _aiModel = null;
        _aiModelConfig = null;
        AiModelName = "";
    }

    /// <summary>
    /// What the editor shows around the caret, for a question to the AI: the file, the goal, Lean's messages within a
    /// few lines, and the selection. Null when no file is open.
    /// </summary>
    public EditorContext? AiContext()
    {
        if (ActiveDocument is not DocumentViewModel d)
        {
            return null;
        }
        int line = d.CaretLine;
        var near = d.Diagnostics
            .Where(x => x.Range.Start.Line <= line + 3 && x.Range.End.Line >= line - 3)
            .OrderBy(x => x.Severity)
            .ThenBy(x => Math.Abs(x.Range.Start.Line - line))
            .ToList();
        string? selection = SelectionProvider?.Invoke();
        return new EditorContext(d.Path, d.Document.Text, line, d.CaretColumn, Info.PlainGoals.Length > 0 ? Info.PlainGoals : null, near,
            string.IsNullOrEmpty(selection) ? null : selection);
    }

    /// <summary>Open the AI window, empty, to ask about the code at the caret.</summary>
    [RelayCommand]
    private void AskAi() => AiChatRequested?.Invoke(null);

    /// <summary>
    /// Ask the AI to explain what is at the caret: Lean's error there, or else the goal, or else the code, in the AI
    /// window.
    /// </summary>
    [RelayCommand]
    private void ExplainWithAi()
    {
        EditorContext? c = AiContext();
        AiChatRequested?.Invoke(c is null ? "Explain the basics of writing a proof in Lean 4." : AiAssistant.ExplainQuestion(c));
    }

    /// <summary>
    /// Ask the AI for proofs of the goal at the sorry at the caret, check them all with Lean, and show the ones that
    /// work in the Prove It card, ready to use. The portfolio is not run first; Prove It does that.
    /// </summary>
    [RelayCommand]
    private async Task AskAiToProveAsync()
    {
        ProofSearchViewModel ps = Info.Search;
        RightTab = GoalsTab;
        ps.IsVisible = true;
        if (ActiveDocument is not { IsLean: true } d)
        {
            ps.Status = "Open a Lean file and put the cursor on a sorry: the AI suggests proofs, and Lean checks them.";
            return;
        }
        SorrySite? site = ProofSearch.At(ProofSearch.Sites(d.Document.Text), d.CaretLine, d.CaretColumn);
        if (site is null)
        {
            ps.Results.Reset([]);
            ps.Status = "There is no sorry in this file. Write sorry where a proof should go, and the AI suggests proofs for it.";
            return;
        }
        _proveCts?.Cancel();
        _proveDoc = d;
        var view = new SearchResultView(new SearchResult(site, false, []));
        ps.Results.Reset([view]);
        await AskAiAboutResultAsync(view);
    }

    /// <summary>
    /// Ask the AI about one of the Prove It card's sorries, check its suggestions with Lean, and add them to the card.
    /// </summary>
    private async Task AskAiAboutResultAsync(SearchResultView r)
    {
        ProofSearchViewModel ps = Info.Search;
        if (_proveDoc is not DocumentViewModel d || !Documents.Contains(d))
        {
            ps.Status = "That file is no longer open.";
            return;
        }
        if (_server is not { State: LeanServerState.Running } server)
        {
            ps.Status = "Lean is not running yet.";
            return;
        }
        string text = d.Document.Text;
        // The sorry as it is now: fills above it may have moved it.
        SorrySite? site = ProofSearch.Sites(text).FirstOrDefault(s => s.Offset == r.Offset);
        if (site is null)
        {
            ps.Status = $"The file has changed where the sorry at {r.Result.Site.Where} was. Run Prove It again.";
            return;
        }
        _aiProveCts?.Cancel();
        var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        _aiProveCts = cts;
        r.IsAskingAi = true;
        ps.IsRunning = true;
        try
        {
            (IChatModel? model, string why) = await AiModelAsync(cts.Token);
            if (model is null)
            {
                ps.Status = why;
                return;
            }
            await FlushChangesAsync(d);
            // Progress comes from Lean's and the model's threads: show it on the UI thread.
            AiProofResult result = await AiProver.RunAsync(server, model, d.Path, text, site,
                s => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    if (_aiProveCts == cts)
                    {
                        ps.Status = s;
                    }
                }), ct: cts.Token);
            if (_aiProveCts != cts)
            {
                return;
            }
            if (result.Rounds == 0)
            {
                ps.Status = "Lean has no goal at that sorry yet: an error earlier in the file may stop it, or it is still checking the file.";
                return;
            }
            r.AddAi(result, ps.ShowFailures);
            ps.UpdateCanFillAll();
            ps.Status = result.Result.Best is not null
                ? "Found a proof Lean accepts. Click it to use it."
                : result.Suggested == 0 ? $"{model.DisplayName} did not suggest a proof Lean could run. Try again, or ask in AI ▸ Ask AI."
                : "Lean rejected every suggestion. Tick \"show the tactics that failed\" to see them; one may be close.";
            Log($"AI: {result.Summary} ({Path.GetFileName(d.Path)}, {site.Where})");
        }
        catch (AiException e)
        {
            ForgetAiModel();
            ps.Status = e.Message;
        }
        catch (OperationCanceledException)
        {
            if (_aiProveCts == cts)
            {
                ps.Status = cts.IsCancellationRequested ? "Stopped: the AI took more than five minutes." : "Stopped.";
            }
        }
        catch (Exception e) when (e is JsonRpcException or IOException or InvalidOperationException)
        {
            ps.Status = "Lean could not check the suggestions: " + e.Message;
        }
        finally
        {
            r.IsAskingAi = false;
            if (_aiProveCts == cts)
            {
                ps.IsRunning = false;
            }
        }
    }

    /// <summary>
    /// After Prove It on one sorry finds nothing: ask the AI too, when that is turned on and a model is at hand.
    /// Does nothing (and says nothing) when no model is available, so Prove It works as before without one.
    /// </summary>
    private async Task AskAiWhenStuckAsync(IReadOnlyList<SearchResult> results)
    {
        if (!Settings.AiAskWhenStuck || results.Count != 1 || Info.Search.Results.FirstOrDefault() is not { CanAskAi: true } view)
        {
            return;
        }
        (IChatModel? model, _) = await AiModelAsync();
        if (model is null)
        {
            Info.Search.Status += " (AI ▸ Choose a Model sets up a local model to suggest proofs.)";
            return;
        }
        await AskAiAboutResultAsync(view);
    }
}

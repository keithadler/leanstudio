using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LeanStudio.Core.Workflow;
using LeanStudio.Lsp;

namespace LeanStudio.App.ViewModels;

/// <summary>For people who live in it: the compiled C beside the source, fixes applied for you, and project-wide
/// search and replace and module renames.</summary>
public sealed partial class MainViewModel
{
    /// <summary>
    /// The right-hand panels' indices in <see cref="RightTab"/>: the Tactic State, the C that Lean emits, and Lean's
    /// own infoview (with widgets).
    /// </summary>
    public const int GoalsTab = 0, CodeTab = 1, InfoviewTab = 2;

    // ---- the C that Lean emits, beside the definition at the cursor ----

    /// <summary>
    /// The right-hand panel shown: <see cref="GoalsTab"/>, <see cref="CodeTab"/> or <see cref="InfoviewTab"/>.
    /// Switching to the C view compiles the file to C if needed.
    /// </summary>
    [ObservableProperty]
    private int _rightTab;

    /// <summary>The C view's code: the C functions for the definition at the caret, or the compiler's error.</summary>
    [ObservableProperty]
    private string _cCode = "";

    /// <summary>
    /// The C view's status line: which definition and function are shown, progress, or why there is no code.
    /// </summary>
    [ObservableProperty]
    private string _cStatus = "Put the cursor on a definition to see the C that Lean compiles it to.";

    private (string Path, int Hash, string C)? _cCache;
    private CancellationTokenSource? _cCts;

    partial void OnRightTabChanged(int value)
    {
        if (value == CodeTab && ActiveDocument is DocumentViewModel d)
        {
            ScheduleC(d);
        }
    }

    private void ScheduleC(DocumentViewModel d)
    {
        if (RightTab != CodeTab || !d.IsLean)
        {
            return;
        }
        _cCts?.Cancel();
        var cts = new CancellationTokenSource();
        _cCts = cts;
        _ = ShowCAsync(d, cts.Token);
    }

    private async Task ShowCAsync(DocumentViewModel d, CancellationToken ct)
    {
        try
        {
            await Task.Delay(250, ct);
            string text = d.Document.Text;
            (string Kind, string Name)? decl = EmittedC.DeclarationAt(d.Lines(), d.CaretLine);
            if (decl is not { Name.Length: > 0 } dn)
            {
                CStatus = "Put the cursor on a definition to see the C that Lean compiles it to.";
                CCode = "";
                return;
            }
            if (dn.Kind is "theorem" or "lemma" or "example")
            {
                CStatus = $"{dn.Kind} {dn.Name}: proofs are erased when Lean compiles, so there is no code.";
                CCode = "";
                return;
            }
            if (_cCache is not { } cached || cached.Path != d.Path || cached.Hash != text.GetHashCode(StringComparison.Ordinal))
            {
                if (Project is null)
                {
                    return;
                }
                CStatus = $"Compiling {Path.GetFileName(d.Path)} to C…";
                var (c, error) = await EmittedC.EmitAsync(Project, d.Path, text, ct);
                if (c is null)
                {
                    CStatus = "Lean could not compile this file to C (fix its errors, and build its imports first).";
                    CCode = error;
                    return;
                }
                _cCache = (d.Path, text.GetHashCode(StringComparison.Ordinal), c);
            }
            IReadOnlyList<CFunction> fns = EmittedC.For(_cCache.Value.C, dn.Name);
            if (fns.Count == 0)
            {
                CStatus = $"{dn.Kind} {dn.Name} has no C function of its own (it may be inlined, noncomputable, or a type).";
                CCode = "";
                return;
            }
            CStatus = $"{dn.Kind} {dn.Name}  →  {fns[0].Name}" + (fns.Count > 1 ? $"  (+{fns.Count - 1} related)" : "");
            CCode = string.Join("\n\n", fns.Select(f => f.Code));
        }
        catch (OperationCanceledException)
        {
        }
    }

    // ---- fixes: every suggestion in the file at once, or automatically ----

    private static bool Actionable(Diagnostic d) =>
        d.Message.Contains("Try this", StringComparison.Ordinal) || d.Message.Contains("[apply]", StringComparison.Ordinal);

    /// <summary>The code actions Lean offers on one line.</summary>
    /// <remarks>
    /// For each message on the line, the actions for it, without duplicate titles. Empty without an active Lean file
    /// and a running server.
    /// </remarks>
    /// <param name="line">The 0-based line in the active file.</param>
    public async Task<IReadOnlyList<CodeAction>> CodeActionsAtLineAsync(int line)
    {
        if (ActiveDocument is not { IsLean: true } d || _server is not { State: LeanServerState.Running } s)
        {
            return [];
        }
        var list = new List<CodeAction>();
        foreach (Diagnostic diag in d.Diagnostics.Where(x => x.Extent.Start.Line <= line && line <= x.Extent.End.Line))
        {
            try
            {
                list.AddRange(await s.CodeActionsAsync(d.Uri, diag.Range, [diag]));
            }
            catch (Exception e) when (e is JsonRpcException or IOException)
            {
            }
        }
        return list.GroupBy(a => a.Title).Select(g => g.First()).ToList();
    }

    /// <summary>
    /// Apply one suggestion for every message in the file that has one: each "Try this" and each [apply] hint.
    /// Fixes are applied from the bottom of the file up, so each one's position is still right when it is used.
    /// </summary>
    /// <remarks>
    /// A fix that overlaps one already chosen is skipped. All are applied as one undoable edit, and the file is left
    /// unsaved.
    /// </remarks>
    [RelayCommand]
    public async Task FixAllInFileAsync()
    {
        if (ActiveDocument is not { IsLean: true } d || _server is not { State: LeanServerState.Running } s)
        {
            return;
        }
        var targets = d.Diagnostics.Where(Actionable).OrderByDescending(x => x.Range.Start).ToList();
        int applied = 0;
        var edits = new List<TextEdit>();
        foreach (Diagnostic diag in targets)
        {
            try
            {
                IReadOnlyList<CodeAction> actions = await s.CodeActionsAsync(d.Uri, diag.Range, [diag]);
                if ((actions.FirstOrDefault(a => a.IsPreferred) ?? actions.FirstOrDefault()) is CodeAction a0)
                {
                    CodeAction a = await s.ResolveAsync(a0);
                    if (a.Edit?.Changes.TryGetValue(d.Uri, out IReadOnlyList<TextEdit>? es) == true)
                    {
                        // Skip a fix that overlaps one already taken: two edits to one place cannot both apply.
                        var fresh = es.Where(e => !edits.Any(x => x.Range.Start <= e.Range.End && e.Range.Start <= x.Range.End)).ToList();
                        if (fresh.Count == es.Count)
                        {
                            edits.AddRange(es);
                            applied++;
                        }
                    }
                }
            }
            catch (Exception e) when (e is JsonRpcException or IOException)
            {
            }
        }
        if (edits.Count == 0)
        {
            Log("No fixes to apply in this file.");
            return;
        }
        await ApplyWorkspaceEditAsync(new WorkspaceEdit(new Dictionary<string, IReadOnlyList<TextEdit>> { [d.Uri] = edits }));
        Log($"Applied {applied} fix{(applied == 1 ? "" : "es")} in {Path.GetFileName(d.Path)}. Undo with ⌘Z / Ctrl+Z.");
    }

    private readonly HashSet<string> _autoFixed = new(StringComparer.Ordinal);

    /// <summary>
    /// With "apply fixes automatically" on: when a search tactic (exact?, apply?, simp?, rw?, aesop?…) you just
    /// wrote finds exactly one answer, put it in. Each message is fixed at most once.
    /// </summary>
    private async Task AutoFixAsync(DocumentViewModel d)
    {
        if (!Settings.AutoApplyFixes || d != ActiveDocument || d.IsProcessing || _server is not { State: LeanServerState.Running } s)
        {
            return;
        }
        string[] lines = d.Lines();
        foreach (Diagnostic diag in d.Diagnostics.Where(x => x.Message.StartsWith("Try this", StringComparison.Ordinal)).OrderByDescending(x => x.Range.Start))
        {
            int line = diag.Range.Start.Line;
            string key = d.Path + "|" + line + "|" + diag.Message;
            if (line >= lines.Length || !System.Text.RegularExpressions.Regex.IsMatch(lines[line], @"\b\w+\?") || !_autoFixed.Add(key))
            {
                continue;
            }
            try
            {
                IReadOnlyList<CodeAction> actions = await s.CodeActionsAsync(d.Uri, diag.Range, [diag]);
                var tries = actions.Where(a => a.Title.StartsWith("Try this", StringComparison.Ordinal)).ToList();
                if (tries.Count == 1)
                {
                    await ApplyCodeActionAsync(tries[0]);
                    Log($"Applied automatically: {tries[0].Title} (line {line + 1}). Undo with ⌘Z / Ctrl+Z.");
                }
            }
            catch (Exception e) when (e is JsonRpcException or IOException)
            {
            }
        }
    }

    // ---- replace across the project ----

    /// <summary>
    /// The replacement for Replace in Files; with a regular expression it may use <c>$1</c> and the like.
    /// </summary>
    [ObservableProperty]
    private string _replaceWith = "";

    /// <summary>
    /// Replace every match of the search in the project. Open files are edited in the editor (undoable, unsaved,
    /// so you can review); other files are written, after their current text is kept in local history.
    /// </summary>
    /// <remarks>Uses the Search panel's query and options. Afterwards the search is run again.</remarks>
    /// <param name="confirm">
    /// Asked with the number of matches and of files before anything changes; false cancels.
    /// </param>
    /// <returns>
    /// How many matches were replaced, in how many files; zeros if nothing was (no project, no matches, cancelled, or
    /// an invalid expression).
    /// </returns>
    public async Task<(int Matches, int Files)> ReplaceInFilesAsync(Func<int, int, Task<bool>> confirm)
    {
        if (Project is null || SearchQuery.Length == 0)
        {
            return (0, 0);
        }
        string root = Project.Root, query = SearchQuery, replacement = ReplaceWith;
        bool cs = SearchCaseSensitive, rx = SearchRegex;
        var open = Documents.Where(d => !d.IsVirtual).ToDictionary(d => d.Path, d => d.Document.Text, StringComparer.Ordinal);
        IReadOnlyList<FileReplacement> plan;
        try
        {
            plan = await Task.Run(() => Refactor.Plan(root, query, replacement, cs, rx, p => open.GetValueOrDefault(p)));
        }
        catch (ArgumentException e)
        {
            SearchStatus = "Invalid regular expression: " + e.Message;
            return (0, 0);
        }
        int matches = plan.Sum(p => p.Count);
        if (matches == 0 || !await confirm(matches, plan.Count))
        {
            return (0, 0);
        }
        foreach (FileReplacement f in plan)
        {
            DocumentViewModel? d = Documents.FirstOrDefault(x => x.Path == f.Path);
            if (d is not null)
            {
                d.ReplaceAll(f.NewText);
            }
            else
            {
                _history.Record(f.Path, await File.ReadAllTextAsync(f.Path));
                await File.WriteAllTextAsync(f.Path, f.NewText);
                _history.Record(f.Path, f.NewText);
            }
        }
        Log($"Replaced {matches} match{(matches == 1 ? "" : "es")} in {plan.Count} file{(plan.Count == 1 ? "" : "s")}. Open files are edited but not saved; File ▸ Local History has the others' previous versions.");
        await SearchInFilesAsync();
        return (matches, plan.Count);
    }

    // ---- rename a module ----

    /// <summary>Rename the active file's module, moving the file and rewriting every import of it.</summary>
    /// <remarks>Saves every file first. The file reopens at the same caret position under its new name.</remarks>
    /// <param name="newModule">
    /// The new module name, such as <c>MyProject.Algebra.Groups</c>; its file goes under the project's root.
    /// </param>
    /// <returns>What went wrong, or null.</returns>
    public async Task<string?> RenameModuleAsync(string newModule)
    {
        if (Project is null || ActiveDocument is not { IsLean: true } d)
        {
            return "Open the module to rename first.";
        }
        newModule = newModule.Trim();
        if (!System.Text.RegularExpressions.Regex.IsMatch(newModule, @"^[A-Za-z_][\w']*(\.[A-Za-z_][\w']*)*$"))
        {
            return "A module name is words separated by dots, like MyProject.Algebra.Groups.";
        }
        string newFile = Path.Combine([Project.Root, .. newModule.Split('.')]) + ".lean";
        if (File.Exists(newFile))
        {
            return $"{Path.GetRelativePath(Project.Root, newFile)} already exists.";
        }
        await SaveDocumentAsync(d);
        await SaveAllCommand.ExecuteAsync(null);
        string oldPath = d.Path;
        string oldModule = Refactor.ModuleOf(Project.Root, oldPath);
        int caretLine = d.CaretLine, caretColumn = d.CaretColumn;
        IReadOnlyList<string> changed = Refactor.RenameModule(Project.Root, oldPath, newFile);
        int index = Documents.IndexOf(d);
        Documents.Remove(d);
        SidesForget(d, index);
        if (_server is { State: LeanServerState.Running } s)
        {
            await s.CloseAsync(d.Uri);
        }
        await OpenFileAsync(newFile, caretLine, caretColumn);
        RefreshFiles();
        Log($"Renamed {oldModule} to {newModule}; updated imports in {changed.Count} file{(changed.Count == 1 ? "" : "s")}. Build to refresh anything that imports it.");
        return null;
    }
}

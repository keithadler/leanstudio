using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LeanStudio.Core.Projects;
using LeanStudio.Core.Workflow;
using LeanStudio.Lsp;

namespace LeanStudio.App.ViewModels;

/// <summary>
/// For people who write a lot of Lean, Mathlib contributors above all: imports tidied, the linters CI runs, renames
/// that keep the old name working, and a library root that imports every module.
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>Findings from tools other than Lean's server (imports, linters), by source, for the Problems panel.</summary>
    private readonly Dictionary<string, List<ProblemItem>> _toolProblems = new(StringComparer.Ordinal);

    /// <summary>What an import check or a lint is doing, or empty.</summary>
    [ObservableProperty]
    private string _proStatus = "";

    private IEnumerable<ProblemItem> ToolProblems() => _toolProblems.Values.SelectMany(x => x);

    private void SetToolProblems(string source, IEnumerable<ProblemItem> items)
    {
        _toolProblems[source] = items.ToList();
        UpdateProblems();
    }

    /// <summary>A file was edited: what the linters said about it describes the text as it was.</summary>
    private void ForgetToolProblems(string path)
    {
        bool changed = false;
        foreach (List<ProblemItem> list in _toolProblems.Values)
        {
            changed |= list.RemoveAll(p => p.Path == path) > 0;
        }
        if (changed)
        {
            UpdateProblems();
        }
    }

    private LeanProject ProjectFor(DocumentViewModel d) => Project ?? new LeanProject(Path.GetDirectoryName(d.Path)!);

    // ---- imports ----

    /// <summary>
    /// Check which imports the active file needs, and remove the others as one undoable edit: those nothing uses,
    /// and those another import already brings in. Each is explained in Output. Returns the check's report.
    /// </summary>
    [RelayCommand]
    public async Task<ImportReport?> RemoveUnusedImportsAsync()
    {
        if (ActiveDocument is not { IsLean: true } d)
        {
            return null;
        }
        string text = d.Document.Text;
        ProStatus = $"Checking the imports of {Path.GetFileName(d.Path)}…";
        Log(ProStatus);
        try
        {
            ImportReport report = await ImportCheck.RunAsync(ProjectFor(d), d.Path, text);
            if (report.Errors > 0)
            {
                Log($"Imports: Lean reports {report.Errors} error{(report.Errors == 1 ? "" : "s")} in this file, so what it uses can't be trusted. Fix them first; nothing was removed.");
                return report;
            }
            if (report.Removable.Count == 0)
            {
                Log($"Imports: all {report.Imports.Count} import{(report.Imports.Count == 1 ? " is" : "s are")} needed.");
                return report;
            }
            if (d.Document.Text != text)
            {
                Log("Imports: the file changed while Lean was checking it; run it again.");
                return report;
            }
            IReadOnlyDictionary<string, int> lines = ImportCheck.ImportLines(text);
            d.Document.BeginUpdate();
            try
            {
                // Bottom up, so the line numbers above stay right.
                foreach (int line in report.Removable.Where(r => lines.ContainsKey(r.Module)).Select(r => lines[r.Module]).OrderDescending())
                {
                    AvaloniaEdit.Document.DocumentLine l = d.Document.GetLineByNumber(line + 1);
                    d.Document.Remove(l.Offset, l.TotalLength);
                }
            }
            finally
            {
                d.Document.EndUpdate();
            }
            foreach (ImportVerdict r in report.Removable)
            {
                Log("  removed " + r.Module + ": " + r.Explanation);
            }
            Log($"Imports: removed {report.Removable.Count} of {report.Imports.Count}. Undo brings them back.");
            return report;
        }
        catch (Exception e) when (e is InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
        {
            Log("Imports: Lean couldn't check them. Build the file's imports first (Build). " + e.Message.Split('\n')[0]);
            return null;
        }
        finally
        {
            ProStatus = "";
        }
    }

    // ---- linters ----

    /// <summary>
    /// Run the linters CI runs on the active file (saving it first) and list what they find in Problems: Mathlib's
    /// standard set in a Mathlib project, Batteries' environment linters where Batteries is available, and every
    /// linter Lean has elsewhere. Returns the findings.
    /// </summary>
    [RelayCommand]
    public async Task<IReadOnlyList<LintFinding>> LintFileAsync()
    {
        if (ActiveDocument is not { IsLean: true } d || Project is not LeanProject p)
        {
            Log("Lint: open a file of a Lake project to lint it.");
            return [];
        }
        if (d.IsDirty)
        {
            await SaveDocumentAsync(d);
        }
        ProStatus = $"Linting {Path.GetFileName(d.Path)}…";
        Log(ProStatus + $" ({Lint.LintersFor(p)}{(p.DependsOnBatteries ? ", and Batteries' environment linters" : "")})");
        try
        {
            var (findings, error) = await Lint.RunAsync(p, d.Path);
            if (error is not null)
            {
                Log("Lint: " + error);
                return [];
            }
            SetToolProblems("lint", findings.Select(f => new ProblemItem(Documents.FirstOrDefault(x => x.Path == f.Path), f.Path, new Diagnostic(
                new Lsp.Range(new Position(f.Line, f.Column), new Position(f.Line, f.Column)),
                f.IsError ? DiagnosticSeverity.Error : DiagnosticSeverity.Warning,
                f.Message.Split('\n')[0] + (f.Linter is null ? "" : $"  ({f.Linter})"), "lint"))));
            Log(findings.Count == 0 ? "Lint: nothing found." : $"Lint: {findings.Count} finding{(findings.Count == 1 ? "" : "s")}, in Problems.");
            if (findings.Count > 0)
            {
                BottomTab = ProblemsPanel;
            }
            return findings;
        }
        catch (Exception e) when (e is IOException or System.ComponentModel.Win32Exception)
        {
            Log("Lint: " + e.Message);
            return [];
        }
        finally
        {
            ProStatus = "";
        }
    }

    // ---- renames that keep the old name working ----

    /// <summary>
    /// After a declaration was renamed from <paramref name="oldName"/> to <paramref name="newName"/>: offer to keep
    /// the old name as a deprecated alias after it, as Mathlib asks of every rename. The declaration is found where
    /// it was before the rename (<paramref name="definition"/>).
    /// </summary>
    private async Task OfferDeprecatedAliasAsync(Location definition, string oldName, string newName)
    {
        DocumentViewModel? doc = Documents.FirstOrDefault(x => x.Uri == definition.Uri) ?? await OpenFileAsync(LeanServer.PathOf(definition.Uri));
        if (doc is null || definition.Range.Start.Line >= doc.Document.LineCount)
        {
            return;
        }
        int line = definition.Range.Start.Line;
        string lineText = doc.Document.GetText(doc.Document.GetLineByNumber(line + 1));
        if (Deprecation.DeclarationKeyword(lineText, newName) is null)
        {
            return;
        }
        bool batteries = Project?.DependsOnBatteries == true;
        string alias = Deprecation.AliasLine(oldName, newName, Deprecation.DeclarationKeyword(lineText, newName)!, batteries, DateOnly.FromDateTime(DateTime.Now));
        if (!await _dialogs.ConfirmAsync("Keep the old name working?",
                $"Add a deprecated alias after the declaration, so code that uses {oldName} still compiles and is told to use {newName}:\n\n{alias}"))
        {
            return;
        }
        string text = doc.Document.Text;
        string updated = Deprecation.AddAlias(text, line, oldName, newName, batteries, DateOnly.FromDateTime(DateTime.Now));
        if (updated != text)
        {
            int end = Deprecation.EndOfDeclaration(text.Split('\n'), line);
            int offset = end >= doc.Document.LineCount ? doc.Document.TextLength : doc.Document.GetLineByNumber(end + 1).Offset;
            string insert = end >= doc.Document.LineCount ? "\n\n" + alias : "\n" + alias + "\n";
            doc.Document.Insert(offset, insert);
            Log($"Rename: {oldName} stays as a deprecated alias of {newName}.");
        }
    }

    // ---- the library root ----

    /// <summary>
    /// After a new module was created: if its library's root file imports every module (as Mathlib.lean does),
    /// import the new one there too, so the check that everything is imported keeps passing.
    /// </summary>
    private async Task AddToLibraryRootAsync(string file)
    {
        if (Project is not LeanProject p || !file.EndsWith(".lean", StringComparison.Ordinal) || p.ModuleNameOf(file) is not string module
            || LibraryRoot.RootFileOf(p, module) is not string root || root == file)
        {
            return;
        }
        string text = Documents.FirstOrDefault(x => x.Path == root)?.Document.Text ?? await File.ReadAllTextAsync(root);
        if (!LibraryRoot.ImportsEverything(text))
        {
            return;
        }
        await SetFileTextAsync(root, LibraryRoot.AddImport(text, module));
        Log($"Added import {module} to {Path.GetFileName(root)}, which imports every module of the library.");
    }

    /// <summary>
    /// Before <paramref name="path"/> (a file or a folder) is deleted: take its modules out of their library's root
    /// file, when that imports every module, so the library still builds.
    /// </summary>
    private async Task RemoveFromLibraryRootAsync(string path)
    {
        if (Project is not LeanProject p)
        {
            return;
        }
        IEnumerable<string> files = Directory.Exists(path) ? Directory.EnumerateFiles(path, "*.lean", SearchOption.AllDirectories) : [path];
        foreach (IGrouping<string, string> byRoot in files.Select(f => p.ModuleNameOf(f)).OfType<string>()
                     .GroupBy(m => LibraryRoot.RootFileOf(p, m) ?? "").Where(g => g.Key.Length > 0 && g.Key != path))
        {
            string text = Documents.FirstOrDefault(x => x.Path == byRoot.Key)?.Document.Text ?? await File.ReadAllTextAsync(byRoot.Key);
            if (!LibraryRoot.ImportsEverything(text))
            {
                continue;
            }
            string updated = byRoot.Aggregate(text, LibraryRoot.RemoveImport);
            if (updated != text)
            {
                await SetFileTextAsync(byRoot.Key, updated);
                Log($"Removed {string.Join(", ", byRoot)} from {Path.GetFileName(byRoot.Key)}.");
            }
        }
    }

    /// <summary>
    /// Import every module of the library that its root file doesn't, when the root imports everything (what
    /// Mathlib's <c>lake exe mk_all</c> does). Returns the modules added.
    /// </summary>
    [RelayCommand]
    public async Task<IReadOnlyList<string>> ImportAllModulesAsync()
    {
        if (Project is not LeanProject p)
        {
            return [];
        }
        var added = new List<string>();
        foreach (string root in Directory.EnumerateFiles(p.Root, "*.lean"))
        {
            string text = Documents.FirstOrDefault(x => x.Path == root)?.Document.Text ?? await File.ReadAllTextAsync(root);
            if (!LibraryRoot.ImportsEverything(text))
            {
                continue;
            }
            IReadOnlyList<string> missing = LibraryRoot.Missing(p, root);
            if (missing.Count == 0)
            {
                continue;
            }
            await SetFileTextAsync(root, missing.Aggregate(text, LibraryRoot.AddImport));
            added.AddRange(missing);
            Log($"{Path.GetFileName(root)}: added {string.Join(", ", missing)}.");
        }
        if (added.Count == 0)
        {
            Log("Every module is imported by its library's root file.");
        }
        return added;
    }

    /// <summary>Change a file's text: in the editor (one undoable edit, left unsaved) if it is open, else on disk.</summary>
    private async Task SetFileTextAsync(string path, string text)
    {
        if (Documents.FirstOrDefault(x => x.Path == path) is DocumentViewModel open)
        {
            open.Document.Replace(0, open.Document.TextLength, text);
        }
        else
        {
            await File.WriteAllTextAsync(path, text);
        }
    }

    // ---- Mathlib's cache, for the files at hand ----

    /// <summary>
    /// In the Mathlib repository itself: fetch the cache only for the open files and what they import
    /// (<c>lake exe cache get</c> with their paths), rather than all of Mathlib. Elsewhere, the whole cache.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRunProjectTask))]
    private Task GetCacheForOpenFilesAsync() => RunBusyAsync("Fetching Mathlib's cache for the open files…", async ct =>
    {
        BottomTab = OutputPanel;
        if (Project!.IsMathlib)
        {
            var files = Documents.Where(x => x.IsLean && x.Path.StartsWith(Project.Root, StringComparison.Ordinal)).Select(x => x.Path).ToList();
            await Lake.GetCacheForAsync(Project, files, Log, ct);
        }
        else
        {
            await Lake.GetCacheAsync(Project, Log, ct);
        }
        await RestartServerAsync();
    });
}

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

    // ---- the goals, into the file or onto the clipboard ----

    /// <summary>
    /// Put the goals the Tactic State shows into the active file, as a comment above the cursor's line, indented
    /// like it (VS Code's Copy Contents to Comment): to keep a state in view while changing the proof above it.
    /// </summary>
    [RelayCommand]
    public void GoalsToComment()
    {
        if (ActiveDocument is not { IsLean: true } d || Info.PlainGoals.Length == 0)
        {
            return;
        }
        AvaloniaEdit.Document.DocumentLine line = d.Document.GetLineByNumber(Math.Clamp(d.CaretLine + 1, 1, d.Document.LineCount));
        string lineText = d.Document.GetText(line);
        string indent = lineText[..(lineText.Length - lineText.TrimStart().Length)];
        string body = Info.PlainGoals.Replace("-/", "- /", StringComparison.Ordinal).Replace("\n", "\n" + indent + "   ", StringComparison.Ordinal);
        d.Document.Insert(line.Offset, indent + "/- " + body + " -/\n");
    }

    /// <summary>Copy the goals the Tactic State shows, as Lean prints them.</summary>
    [RelayCommand]
    public Task CopyGoalsAsync() => Info.PlainGoals.Length == 0 ? Task.CompletedTask : _dialogs.CopyTextAsync(Info.PlainGoals);

    // ---- updating a dependency, and what it broke ----

    /// <summary>The report of the last dependency update, for its deprecation renames.</summary>
    public BumpReport? LastBump { get; private set; }

    private string BackupFolder => Path.Combine(Project!.Root, ".lake", "leanstudio-update-backup");

    /// <summary>
    /// Update a dependency (Mathlib unless another is named) and find out what that did: its commits before and
    /// after, the toolchain it moved to (the project follows Mathlib's), the build's errors by file, and every use
    /// of a name it deprecated, which can then be renamed in one go. lake-manifest.json and lean-toolchain are
    /// backed up first; <see cref="UndoDependencyUpdateAsync"/> puts them back.
    /// </summary>
    public async Task<BumpReport?> UpdateDependencyAsync(string package = "mathlib")
    {
        if (Project is not LeanProject p || !p.IsLakeProject)
        {
            return null;
        }
        BumpReport? report = null;
        await RunBusyAsync($"Updating {package}…", async ct =>
        {
            BottomTab = OutputPanel;
            Directory.CreateDirectory(BackupFolder);
            foreach (string f in new[] { p.ManifestPath, p.ToolchainPath }.Where(File.Exists))
            {
                File.Copy(f, Path.Combine(BackupFolder, Path.GetFileName(f)), overwrite: true);
            }
            string? oldRev = DependencyBump.ManifestRev(p, package), oldToolchain = p.Toolchain;
            await Lake.UpdateAsync(p, Log, ct, package);
            // Mathlib builds with one toolchain; the project has to use the same one.
            string depToolchain = Path.Combine(p.PackagesDirectory, package, LeanProject.ToolchainFile);
            if (File.Exists(depToolchain) && (await File.ReadAllTextAsync(depToolchain, ct)).Trim() is { Length: > 0 } tc && tc != oldToolchain)
            {
                p.SetToolchain(tc);
                Log($"The project now uses {tc}, as {package} does.");
            }
            if (p.DependsOnMathlib)
            {
                await Lake.GetCacheAsync(p, Log, ct);
            }
            var build = await Lake.BuildAsync(p, onLine: Log, ct: ct);
            TakeBuildOutput(build.Output);
            IReadOnlyList<BuildMessage> messages = LakeOutput.Parse(build.Output, p.Root);
            report = new BumpReport(package, oldRev, DependencyBump.ManifestRev(p, package), oldToolchain, p.Toolchain,
                messages.Where(m => m.IsError).GroupBy(m => m.Path).ToDictionary(g => g.Key, g => (IReadOnlyList<BuildMessage>)g.ToList()),
                DependencyBump.DeprecatedUses(messages));
            Log(report.Summary);
            foreach ((string file, IReadOnlyList<BuildMessage> errs) in report.Errors.OrderByDescending(e => e.Value.Count).Take(10))
            {
                Log($"  {Path.GetRelativePath(p.Root, file)}: {errs.Count} error{(errs.Count == 1 ? "" : "s")}, first: {errs[0].Message.Split('\n')[0]}");
            }
        });
        await RestartServerAsync();
        LastBump = report;
        if (report is { Deprecated.Count: > 0 }
            && await _dialogs.ConfirmAsync("Rename the deprecated names?",
                $"{report.Deprecated.Count} place{(report.Deprecated.Count == 1 ? "" : "s")} use names the update deprecated, and Lean says what to use instead:\n\n"
                + string.Join("\n", report.Deprecated.Select(d => $"{d.Old} → {d.New}").Distinct().Take(12))
                + "\n\nRename them all? Open files are edited in the editor (undo works); others are written to disk."))
        {
            await ApplyDeprecationRenamesAsync(report.Deprecated);
        }
        return report;
    }

    /// <summary>Rename every use of a deprecated name to what Lean says to use instead, file by file.</summary>
    public async Task ApplyDeprecationRenamesAsync(IReadOnlyList<DeprecatedUse> uses)
    {
        int files = 0;
        foreach (IGrouping<string, DeprecatedUse> inFile in uses.GroupBy(u => u.File))
        {
            // Lake names files by their real path (/private/var on a Mac for /var): find the open one by that too.
            string real = Lint.RealPath(inFile.Key);
            string path = Documents.FirstOrDefault(x => x.Path == inFile.Key || Lint.RealPath(x.Path) == real)?.Path ?? inFile.Key;
            string text = Documents.FirstOrDefault(x => x.Path == path)?.Document.Text ?? await File.ReadAllTextAsync(path);
            string renamed = DependencyBump.Rename(text, inFile);
            if (renamed != text)
            {
                await SetFileTextAsync(path, renamed);
                files++;
            }
        }
        Log($"Renamed deprecated names in {files} file{(files == 1 ? "" : "s")}. Build to check (open files are left unsaved).");
    }

    /// <summary>Put back lake-manifest.json and lean-toolchain as they were before the last update, and fetch and build again.</summary>
    [RelayCommand]
    public async Task UndoDependencyUpdateAsync()
    {
        if (Project is not LeanProject p || !Directory.Exists(BackupFolder))
        {
            Log("There is no dependency update to undo.");
            return;
        }
        foreach (string f in Directory.GetFiles(BackupFolder))
        {
            File.Copy(f, Path.Combine(p.Root, Path.GetFileName(f)), overwrite: true);
        }
        Directory.Delete(BackupFolder, true);
        Log("Put back lake-manifest.json and lean-toolchain from before the update.");
        await RunBusyAsync("Going back to the previous versions…", async ct =>
        {
            if (p.DependsOnMathlib)
            {
                await Lake.GetCacheAsync(p, Log, ct);
            }
            var build = await Lake.BuildAsync(p, onLine: Log, ct: ct);
            TakeBuildOutput(build.Output);
        });
        await RestartServerAsync();
    }

    // ---- the project's own commands (.leanstudio/commands.json) ----

    /// <summary>The project's own commands, read fresh (so an edit to commands.json applies at once); empty without a project.</summary>
    public IReadOnlyList<ProjectCommand> ProjectCommandList()
    {
        if (Project is not LeanProject p || !File.Exists(Path.Combine(p.Root, ProjectCommands.RelativePath)))
        {
            return [];
        }
        try
        {
            var (commands, problems) = ProjectCommands.Parse(File.ReadAllText(Path.Combine(p.Root, ProjectCommands.RelativePath)));
            foreach (string problem in problems)
            {
                Log(".leanstudio/commands.json: " + problem);
            }
            return commands;
        }
        catch (IOException)
        {
            return [];
        }
    }

    /// <summary>Run one of the project's own commands, with its variables filled in from where you are.</summary>
    public Task RunProjectCommandAsync(ProjectCommand c)
    {
        if (Project is not LeanProject p)
        {
            return Task.CompletedTask;
        }
        DocumentViewModel? d = ActiveDocument;
        string word = d is null ? "" : Core.Editing.MultiCursor.WordAt(d.Document.Text,
            Math.Min(d.Document.TextLength, d.Document.GetOffset(Math.Clamp(d.CaretLine + 1, 1, d.Document.LineCount), d.CaretColumn + 1))) is { } w
            ? d.Document.Text[w.Start..w.End] : "";
        var ctx = new CommandContext(p.Root, d?.Path ?? "", d is null ? "" : p.ModuleNameOf(d.Path) ?? "", (d?.CaretLine ?? 0) + 1, word, SelectionProvider?.Invoke() ?? "");
        return RunTaskAsync(new ProjectTask(c.Title, c.Detail, c.Program, ProjectCommands.Expand(c.Arguments, ctx)), c.Save);
    }

    /// <summary>Open the project's commands.json, starting it from a template with examples.</summary>
    [RelayCommand]
    public async Task EditProjectCommandsAsync()
    {
        if (Project is not LeanProject p)
        {
            return;
        }
        string path = Path.Combine(p.Root, ProjectCommands.RelativePath);
        if (!File.Exists(path))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, ProjectCommands.Template);
        }
        await OpenFileAsync(path);
    }

    // ---- the blueprint ----

    /// <summary>
    /// Check the project's blueprint (leanblueprint's <c>blueprint/src/*.tex</c>) against the last build and list
    /// every node in the References panel, disagreements first: a <c>\leanok</c> over a sorry or a missing
    /// declaration, then what is proved but not marked, what is ready to prove, and what isn't started. Click one
    /// to go to it in the .tex file. Returns the checks.
    /// </summary>
    [RelayCommand]
    public async Task<IReadOnlyList<BlueprintCheck>> CheckBlueprintAsync()
    {
        if (Project is not LeanProject p || Blueprint.SourceFolder(p.Root) is not string folder)
        {
            Log("Blueprint: this project has no blueprint folder (blueprint/src, as leanblueprint makes it).");
            return [];
        }
        if (!await TenetIsCurrentAsync())
        {
            await ReopenTenetAsync();
        }
        if (_tenet is not { } ws || ws.OwnModules.Count == 0)
        {
            Log("Blueprint: build the project first (Lean ▸ Build Project): the blueprint is checked against what Lean built.");
            return [];
        }
        IReadOnlyList<BlueprintCheck> checks = await Task.Run(() => Blueprint.Check(Blueprint.Read(folder), ws.BlueprintStatus));
        static string Mark(BlueprintCheck c) => c.Disagrees ? "✗" : c.Verdict == "done" ? "✓" : c.Verdict.StartsWith("proved", StringComparison.Ordinal) || c.Verdict.StartsWith("done in", StringComparison.Ordinal) ? "✓?" : c.Verdict == "not started" ? "·" : "◐";
        References.Reset(checks.OrderBy(c => c.Disagrees ? 0 : c.Verdict == "done" ? 3 : 1).ThenBy(c => c.Node.File, StringComparer.Ordinal).ThenBy(c => c.Node.Line)
            .Select(c => new LocationItem(c.Node.File, c.Node.Line, 0,
                $"{Mark(c)} {(c.Node.Label.Length > 0 ? c.Node.Label : c.Node.Kind)}{(c.Node.Title.Length > 0 ? " (" + c.Node.Title + ")" : "")}: {c.Verdict}")));
        int done = checks.Count(c => c.Verdict == "done"), disagree = checks.Count(c => c.Disagrees);
        ReferencesTitle = $"Blueprint: {done} of {checks.Count} done · {disagree} disagree{(disagree == 1 ? "s" : "")} with Lean";
        BottomTab = ReferencesPanel;
        Log($"Blueprint: {checks.Count} nodes, {done} done, {checks.Count(c => c.Verdict == "ready to prove")} ready to prove, "
            + $"{checks.Count(c => c.Verdict == "not started")} not started, {disagree} where the blueprint says more than Lean does.");
        return checks;
    }

    // ---- heartbeats ----

    /// <summary>
    /// Count the heartbeats of each top-level declaration of the active file (what <c>maxHeartbeats</c> limits) and
    /// list them in the References panel, heaviest first, with their share of Lean's default limit. Returns them.
    /// </summary>
    [RelayCommand]
    public async Task<IReadOnlyList<Core.Proofs.DeclarationHeartbeats>> CountHeartbeatsAsync()
    {
        if (ActiveDocument is not { IsLean: true } d)
        {
            return [];
        }
        ProStatus = $"Counting heartbeats in {Path.GetFileName(d.Path)}…";
        Log(ProStatus);
        try
        {
            var (counts, error) = await Core.Proofs.Heartbeats.RunAsync(ProjectFor(d), d.Path, d.Document.Text);
            if (error is not null)
            {
                Log("Heartbeats: " + error);
                return [];
            }
            References.Reset(counts.Select(c => new LocationItem(d.Path, c.Line, 0,
                $"{c.Heartbeats:N0} heartbeats ({c.OfLimit:P0} of the default limit) · {c.Declaration.Trim()}")));
            ReferencesTitle = $"Heartbeats in {Path.GetFileName(d.Path)}";
            BottomTab = ReferencesPanel;
            if (Core.Proofs.Heartbeats.Warning(counts) is string warning)
            {
                Log("Heartbeats: " + warning);
            }
            return counts;
        }
        catch (Exception e) when (e is IOException or System.ComponentModel.Win32Exception)
        {
            Log("Heartbeats: " + e.Message);
            return [];
        }
        finally
        {
            ProStatus = "";
        }
    }

    // ---- the import graph ----

    /// <summary>
    /// List, in the References panel, what the active module imports and which of the project's modules import it,
    /// and say how many modules Lake rebuilds when it changes. Click one to go to its import.
    /// </summary>
    [RelayCommand]
    public async Task ShowImportGraphAsync()
    {
        if (ActiveDocument is not { IsLean: true } d || Project is not LeanProject p || p.ModuleNameOf(d.Path) is not string module)
        {
            Log("Imports: open a file of the project.");
            return;
        }
        ImportGraph g = await Task.Run(() => ImportGraph.Build(p));
        IReadOnlyList<ImportEdge> imports = g.ImportsOf(module), importedBy = g.ImportedBy(module);
        IReadOnlyList<string> dependents = g.Dependents(module);
        var items = imports.Select(e => new LocationItem(e.File, e.Line, 0, "imports " + e.Imported + (g.Files.ContainsKey(e.Imported) ? "" : "  (outside the project)")))
            .Concat(importedBy.Select(e => new LocationItem(e.File, e.Line, 0, $"{e.Module} imports {module}")))
            .ToList();
        References.Reset(items);
        ReferencesTitle = $"{module}: imports {imports.Count} · imported by {importedBy.Count}";
        BottomTab = ReferencesPanel;
        Log($"{module} imports {imports.Count} module{(imports.Count == 1 ? "" : "s")} and is imported by {importedBy.Count}; "
            + $"changing it rebuilds {dependents.Count} module{(dependents.Count == 1 ? "" : "s")} of the project.");
    }

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

    /// <summary>
    /// Put the active file's imports in order, as Mathlib's style asks: each run in the header sorted by module name,
    /// repeats dropped. Only the changed lines are replaced, as one undoable edit, so the cursor and folds stay put.
    /// </summary>
    [RelayCommand]
    public void SortImports()
    {
        if (ActiveDocument is not { IsLean: true } d)
        {
            return;
        }
        string text = d.Document.Text;
        string sorted = ImportOrder.Sort(text);
        if (sorted == text)
        {
            Log("Imports: already in order.");
            return;
        }
        int head = 0;
        while (head < text.Length && head < sorted.Length && text[head] == sorted[head])
        {
            head++;
        }
        int tail = 0;
        while (tail < text.Length - head && tail < sorted.Length - head && text[text.Length - 1 - tail] == sorted[sorted.Length - 1 - tail])
        {
            tail++;
        }
        d.Document.Replace(head, text.Length - head - tail, sorted.Substring(head, sorted.Length - head - tail));
        Log("Imports: sorted. Undo brings the old order back.");
    }

    /// <summary>
    /// Check the active file against Mathlib's text rules (trailing whitespace, lines over 100 characters, tabs, line
    /// endings, the final newline), listing what is wrong in Output, and fix what has one obvious fix as one undoable edit.
    /// </summary>
    [RelayCommand]
    public void TidyWhitespace()
    {
        if (ActiveDocument is not { IsLean: true } d)
        {
            return;
        }
        string text = d.Document.Text;
        IReadOnlyList<StyleProblem> problems = StyleCheck.Find(text);
        if (problems.Count == 0)
        {
            Log("Style: no problems.");
            return;
        }
        string fixedText = StyleCheck.Fix(text);
        if (fixedText != text)
        {
            d.Document.Replace(0, text.Length, fixedText);
        }
        foreach (StyleProblem p in problems.Where(p => p.Rule == "long-line"))
        {
            Log($"  line {p.Line + 1}: {p.Message}");
        }
        int left = problems.Count(p => p.Rule == "long-line");
        Log($"Style: fixed {problems.Count - left} problem{(problems.Count - left == 1 ? "" : "s")}"
            + (left > 0 ? $"; {left} long line{(left == 1 ? "" : "s")} left for you." : ".") + " Undo brings the old text back.");
    }

    /// <summary>
    /// Put the whole file in Mathlib's shape in one undoable edit: the imports in order, then the text rules
    /// (whitespace, tabs, line endings, the final newline). Long lines and the file conventions are listed in Output.
    /// </summary>
    [RelayCommand]
    public void TidyFile()
    {
        if (ActiveDocument is not { IsLean: true } d)
        {
            return;
        }
        string text = d.Document.Text;
        string tidy = StyleCheck.Fix(ImportOrder.Sort(text));
        if (tidy != text)
        {
            d.Document.Replace(0, text.Length, tidy);
        }
        List<StyleProblem> left = [.. StyleCheck.Find(tidy)];
        if (ProjectFor(d).DependsOnMathlib)
        {
            left.AddRange(MathlibConventions.Find(tidy));
            left.AddRange(DocCoverage.Find(tidy));
        }
        foreach (StyleProblem p in left)
        {
            Log($"  line {p.Line + 1}: {p.Message}");
        }
        Log((tidy == text ? "Tidy: already tidy" : "Tidy: imports sorted and whitespace fixed")
            + (left.Count > 0 ? $"; {left.Count} thing{(left.Count == 1 ? "" : "s")} left for you (above)." : ".")
            + (tidy == text ? "" : " Undo brings the old text back."));
    }

    /// <summary>
    /// Put Mathlib's copyright header at the top of the active file, with this year and the name git is set up with
    /// (the Git user name, else the system user). A file that already starts with a comment is left alone.
    /// </summary>
    [RelayCommand]
    public async Task AddMathlibHeaderAsync()
    {
        if (ActiveDocument is not { IsLean: true } d)
        {
            return;
        }
        string author = Environment.UserName;
        if (Core.Git.GitRepository.Find(d.Path) is Core.Git.GitRepository git)
        {
            Core.Processes.ProcessResult r = await git.RunAsync(["config", "user.name"]);
            if (r.Success && r.Output.Trim().Length > 0)
            {
                author = r.Output.Trim();
            }
        }
        string text = d.Document.Text;
        string withHeader = MathlibConventions.AddHeader(text, DateTime.Now.Year, author);
        if (withHeader == text)
        {
            Log("Header: the file already starts with a comment, so it was left alone.");
            return;
        }
        d.Document.Insert(0, withHeader[..^text.Length]);
        Log($"Header: added Mathlib's copyright header for {author}. Check the name, then undo if it is wrong.");
    }

    /// <summary>
    /// Delete the deprecated aliases and declarations of the active file that are more than six months old (by their
    /// <c>(since := "…")</c>), with their doc comments, as one undoable edit. Each is named in Output.
    /// </summary>
    [RelayCommand]
    public void RemoveStaleDeprecations()
    {
        if (ActiveDocument is not { IsLean: true } d)
        {
            return;
        }
        string text = d.Document.Text;
        IReadOnlyList<StaleDeprecation> stale = StaleDeprecations.Find(text, DateOnly.FromDateTime(DateTime.Today));
        if (stale.Count == 0)
        {
            Log("Deprecations: none is more than six months old.");
            return;
        }
        d.Document.Replace(0, text.Length, StaleDeprecations.Remove(text, stale));
        foreach (StaleDeprecation s in stale)
        {
            Log($"  removed {s.Name}, deprecated {s.Since:yyyy-MM-dd} ({s.AgeMonths} months ago)");
        }
        Log($"Deprecations: removed {stale.Count}. Undo brings them back.");
    }

    /// <summary>
    /// Break the comment and docstring lines of the active file that are over 100 characters at a space, as one undoable
    /// edit. Code is never touched; what still is too long is listed in Output.
    /// </summary>
    [RelayCommand]
    public void WrapLongComments()
    {
        if (ActiveDocument is not { IsLean: true } d)
        {
            return;
        }
        string text = d.Document.Text;
        string wrapped = StyleCheck.WrapComments(text);
        if (wrapped != text)
        {
            d.Document.Replace(0, text.Length, wrapped);
        }
        IReadOnlyList<StyleProblem> left = [.. StyleCheck.Find(wrapped).Where(p => p.Rule == "long-line")];
        foreach (StyleProblem p in left)
        {
            Log($"  line {p.Line + 1}: {p.Message}");
        }
        Log((wrapped == text ? "Wrap: no comment line to break" : "Wrap: broke the long comment lines")
            + (left.Count > 0 ? $"; {left.Count} long line{(left.Count == 1 ? "" : "s")} of code left for you (above)." : ".")
            + (wrapped == text ? "" : " Undo brings the old text back."));
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

using System.Collections.ObjectModel;
using AvaloniaEdit.Document;
using CommunityToolkit.Mvvm.ComponentModel;
using LeanStudio.Core.Verification;
using LeanStudio.Lsp;

namespace LeanStudio.App.ViewModels;

/// <summary>One open file: its text, whether it is saved, and what Lean and Tenet have said about it.</summary>
/// <remarks>
/// Backs one editor tab. <see cref="MainViewModel"/> creates it from the file's text on disk, forwards its edits to
/// Lean, and fills in <see cref="Diagnostics"/>, <see cref="Processing"/>, <see cref="Verdicts"/>,
/// <see cref="Timings"/> and <see cref="LineChanges"/> as Lean, Tenet, the profiler and Git report on it. Used on
/// the UI thread.
/// </remarks>
public sealed partial class DocumentViewModel : ObservableObject
{
    /// <summary>Wrap a file's text. Nothing is read or written here: the caller reads the file.</summary>
    /// <param name="path">The file; made absolute. For a virtual document, any name.</param>
    /// <param name="text">The text, which is also taken as what is saved on disk.</param>
    public DocumentViewModel(string path, string text)
    {
        Path = System.IO.Path.GetFullPath(path);
        Uri = LeanServer.UriOf(Path);
        Document = new TextDocument(text) { FileName = Path };
        SavedText = text;
        // Unsaved means "differs from what is on disk". Comparing lengths first makes that nearly free while
        // typing; the text is compared only when the lengths match. (The undo stack's own notion of this is lost
        // whenever a document's text is replaced wholesale, so it cannot be relied on.)
        Document.TextChanged += (_, _) =>
        {
            IsDirty = Document.TextLength != SavedText.Length || Document.Text != SavedText;
            TextChanged?.Invoke(this);
        };
    }

    /// <summary>The file's full path. Changes with Save As.</summary>
    public string Path { get; private set; }
    /// <summary>The file's URI, which identifies it to Lean. Changes with Save As.</summary>
    public string Uri { get; private set; }
    /// <summary>The text, as the editor shows and edits it.</summary>
    public TextDocument Document { get; }
    /// <summary>The text as last read from or written to disk, to tell whether there are unsaved changes.</summary>
    public string SavedText { get; private set; }

    /// <summary>
    /// The tab's title: the file name, with • when there are unsaved changes, or <c>(changes)</c> for a diff.
    /// </summary>
    public string Title => IsVirtual
        ? System.IO.Path.GetFileNameWithoutExtension(Path) + " (changes)"
        : System.IO.Path.GetFileName(Path) + (IsDirty ? " •" : "");

    /// <summary>A <c>.lean</c> file, which Lean checks; false for a virtual document.</summary>
    public bool IsLean => !IsVirtual && Path.EndsWith(".lean", StringComparison.OrdinalIgnoreCase);

    /// <summary>C or C++ (a project's FFI code), which clangd serves when it is installed.</summary>
    public bool IsC => !IsVirtual && Core.Workflow.Ffi.IsCFile(Path);

    /// <summary>A view with no file behind it (a diff): read-only, never saved, never sent to Lean.</summary>
    public bool IsVirtual { get; init; }

    /// <summary>Lines that differ from the last commit, for the gutter.</summary>
    [ObservableProperty]
    private IReadOnlyList<Core.Git.LineChange> _lineChanges = [];

    /// <summary>The text differs from <see cref="SavedText"/>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title))]
    private bool _isDirty;

    /// <summary>Lean's diagnostics for this file, newest set.</summary>
    [ObservableProperty]
    private IReadOnlyList<Diagnostic> _diagnostics = [];

    /// <summary>Where each proof ends, finished or with goals left, for the marks at the end of those lines.</summary>
    [ObservableProperty]
    private IReadOnlyList<ProofMark> _proofMarks = [];

    /// <summary>The ranges Lean is still elaborating; empty when the file is done.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsProcessing))]
    private IReadOnlyList<LeanFileProgressRange> _processing = [];

    /// <summary>Lean is still checking part of the file.</summary>
    public bool IsProcessing => Processing.Count > 0;

    /// <summary>Tenet's verdict for each declaration in this file, by 1-based line, from the last verification.</summary>
    [ObservableProperty]
    private IReadOnlyDictionary<int, DeclarationVerdict> _verdicts = new Dictionary<int, DeclarationVerdict>();

    /// <summary>How long each declaration took Lean, from the last profile of this text (empty after an edit).</summary>
    [ObservableProperty]
    private IReadOnlyList<Core.Proofs.DeclarationTiming> _timings = [];

    /// <summary>Raised on every edit, for the session to forward to Lean.</summary>
    public event Action<DocumentViewModel>? TextChanged;

    /// <summary>Raised when something asks the editor to move the caret (go to definition, a problem, a declaration).</summary>
    public event Action<int, int>? RevealRequested;

    /// <summary>
    /// Move the caret. Recorded as well as announced, so a document the editor has not switched to yet still opens
    /// at the right place.
    /// </summary>
    /// <param name="line">The 0-based line.</param>
    /// <param name="column">The 0-based column.</param>
    public void Reveal(int line, int column)
    {
        CaretLine = line;
        CaretColumn = column;
        RevealRequested?.Invoke(line, column);
    }

    /// <summary>
    /// The caret's 0-based line, kept while the document is not shown. Use <see cref="Reveal"/> to move the editor's
    /// caret.
    /// </summary>
    public int CaretLine { get; set; }
    /// <summary>The caret's 0-based column; see <see cref="CaretLine"/>.</summary>
    public int CaretColumn { get; set; }

    /// <summary>
    /// Write the text to disk. Does not tell Lean; <see cref="MainViewModel"/> does that. Throws if the file cannot be
    /// written.
    /// </summary>
    /// <param name="newPath">A new path to save under (Save As), or null to save in place.</param>
    public async Task SaveAsync(string? newPath = null)
    {
        if (newPath is not null)
        {
            Path = System.IO.Path.GetFullPath(newPath);
            Uri = LeanServer.UriOf(Path);
            Document.FileName = Path;
            OnPropertyChanged(nameof(Path));
        }
        string text = Document.Text;
        await File.WriteAllTextAsync(Path, text);
        SavedText = text;
        IsDirty = false;
        OnPropertyChanged(nameof(Title));
    }

    /// <summary>Take text that changed on disk as the new saved version, as one undoable edit.</summary>
    public void ReloadFrom(string text)
    {
        SavedText = text;
        ReplaceAll(text);
        IsDirty = false;
        OnPropertyChanged(nameof(Title));
    }

    /// <summary>Replace the whole text as a single edit, so undo brings the old text back.</summary>
    public void ReplaceAll(string text) => Document.Replace(0, Document.TextLength, text);

    /// <summary>The text split into lines, without line endings. A new array each call.</summary>
    public string[] Lines() => Document.Text.Split('\n').Select(l => l.TrimEnd('\r')).ToArray();
}

/// <summary>A problem in the list: from Lean's live diagnostics for an open file, or from the last build for any file.</summary>
/// <param name="Document">The open file it is in, or null for a build message about a file that is not open.</param>
/// <param name="Path">The file.</param>
/// <param name="Diagnostic">The message, with its range and severity.</param>
public sealed record ProblemItem(DocumentViewModel? Document, string Path, Diagnostic Diagnostic)
{
    /// <summary>A problem in an open file.</summary>
    /// <param name="document">The file.</param>
    /// <param name="diagnostic">Lean's message.</param>
    public ProblemItem(DocumentViewModel document, Diagnostic diagnostic)
        : this(document, document.Path, diagnostic)
    {
    }

    /// <summary>The file's name, without its folder.</summary>
    public string File => System.IO.Path.GetFileName(Path);
    /// <summary>The 1-based line and column, as <c>line:column</c>.</summary>
    public string Location => $"{Diagnostic.Range.Start.Line + 1}:{Diagnostic.Range.Start.Character + 1}";
    /// <summary>The message's first line.</summary>
    public string Message => Diagnostic.Message.Split('\n')[0];
    /// <summary>The whole message.</summary>
    public string FullMessage => Diagnostic.Message;
    /// <summary>✕ for an error, ▲ for a warning, ● otherwise.</summary>
    public string Icon => Diagnostic.Severity switch
    {
        DiagnosticSeverity.Error => "✕",
        DiagnosticSeverity.Warning => "▲",
        _ => "●",
    };
    /// <summary>The message's severity.</summary>
    public DiagnosticSeverity Severity => Diagnostic.Severity;
    /// <summary>It is an error.</summary>
    public bool IsError => Diagnostic.Severity == DiagnosticSeverity.Error;
    /// <summary>It is a warning.</summary>
    public bool IsWarning => Diagnostic.Severity == DiagnosticSeverity.Warning;
}

/// <summary>
/// A row of the Problems panel: one problem, or a group of problems with the same message (a deprecation used 205
/// times), which opens to list them.
/// </summary>
/// <param name="Item">The problem, for a problem's row; for a group's row, the first of them.</param>
/// <param name="Count">How many problems the group holds; 0 for a problem's own row.</param>
/// <param name="Files">How many files the group's problems are in.</param>
/// <param name="IsExpanded">The group is open.</param>
/// <param name="InGroup">A problem listed under its open group.</param>
public sealed record ProblemRow(ProblemItem Item, int Count, int Files, bool IsExpanded, bool InGroup)
{
    /// <summary>It is a group's row.</summary>
    public bool IsGroup => Count > 0;

    /// <summary>What the row says first: the file, or the group's count.</summary>
    public string Lead => IsGroup ? $"{(IsExpanded ? "▾" : "▸")} {Count} ×" : Item.File;

    /// <summary>Where: the line and column, or how many files a group spans.</summary>
    public string Where => IsGroup ? (Files == 1 ? "in " + Item.File : $"in {Files} files") : Item.Location;

    /// <summary>The message's first line.</summary>
    public string Message => Item.Message;

    /// <summary>The whole message, for the tooltip.</summary>
    public string FullMessage => IsGroup ? $"{Count} problems say this. Click to {(IsExpanded ? "fold them" : "list them")}.\n\n{Item.FullMessage}" : Item.FullMessage;

    /// <summary>The problem's icon.</summary>
    public string Icon => Item.Icon;

    /// <summary>It is an error.</summary>
    public bool IsError => Item.IsError;

    /// <summary>It is a warning.</summary>
    public bool IsWarning => Item.IsWarning;

    /// <summary>The indent of a problem inside an open group.</summary>
    public Avalonia.Thickness Indent => InGroup ? new Avalonia.Thickness(26, 0, 0, 0) : default;

    /// <summary>
    /// The rows for <paramref name="items"/> (in the order they are listed): problems whose first line and severity
    /// are shared by at least <paramref name="minimum"/> of them become one group, at the place of the first, open
    /// when its key is in <paramref name="expanded"/>.
    /// </summary>
    public static IReadOnlyList<ProblemRow> Group(IReadOnlyList<ProblemItem> items, ISet<string> expanded, int minimum = 3)
    {
        var groups = items.GroupBy(KeyOf).Where(g => g.Count() >= minimum).ToDictionary(g => g.Key, g => g.ToList());
        var rows = new List<ProblemRow>(items.Count);
        var done = new HashSet<string>();
        foreach (ProblemItem p in items)
        {
            string key = KeyOf(p);
            if (!groups.TryGetValue(key, out List<ProblemItem>? members))
            {
                rows.Add(new ProblemRow(p, 0, 0, false, false));
                continue;
            }
            if (!done.Add(key))
            {
                continue;
            }
            bool open = expanded.Contains(key);
            rows.Add(new ProblemRow(p, members.Count, members.Select(m => m.Path).Distinct().Count(), open, false));
            if (open)
            {
                rows.AddRange(members.Select(m => new ProblemRow(m, 0, 0, false, true)));
            }
        }
        return rows;
    }

    /// <summary>What groups problems: their severity and first line.</summary>
    public static string KeyOf(ProblemItem p) => $"{(int)p.Severity}|{p.Message}";
}

/// <summary>
/// A collection bound to a list in the UI, which can have all its items replaced with one change notification.
/// </summary>
/// <typeparam name="T">The items' type.</typeparam>
public sealed class ObservableList<T> : ObservableCollection<T>
{
    /// <summary>
    /// Replace every item and raise a single <c>Reset</c> notification, so a panel redraws once rather than once per
    /// item. On the UI thread only, like any change to a bound collection.
    /// </summary>
    /// <param name="items">The new items; enumerated once.</param>
    public void Reset(IEnumerable<T> items)
    {
        Items.Clear();
        foreach (T i in items)
        {
            Items.Add(i);
        }
        OnCollectionChanged(new System.Collections.Specialized.NotifyCollectionChangedEventArgs(System.Collections.Specialized.NotifyCollectionChangedAction.Reset));
        OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs("Item[]"));
    }
}

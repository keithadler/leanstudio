using System.Collections.ObjectModel;
using AvaloniaEdit.Document;
using CommunityToolkit.Mvvm.ComponentModel;
using LeanStudio.Core.Verification;
using LeanStudio.Lsp;

namespace LeanStudio.App.ViewModels;

/// <summary>One open file: its text, whether it is saved, and what Lean and Tenet have said about it.</summary>
public sealed partial class DocumentViewModel : ObservableObject
{
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

    public string Path { get; private set; }
    public string Uri { get; private set; }
    public TextDocument Document { get; }
    public string SavedText { get; private set; }

    public string Title => IsVirtual
        ? System.IO.Path.GetFileNameWithoutExtension(Path) + " (changes)"
        : System.IO.Path.GetFileName(Path) + (IsDirty ? " •" : "");

    public bool IsLean => !IsVirtual && Path.EndsWith(".lean", StringComparison.OrdinalIgnoreCase);

    /// <summary>C or C++ (a project's FFI code), which clangd serves when it is installed.</summary>
    public bool IsC => !IsVirtual && Core.Workflow.Ffi.IsCFile(Path);

    /// <summary>A view with no file behind it (a diff): read-only, never saved, never sent to Lean.</summary>
    public bool IsVirtual { get; init; }

    /// <summary>Lines that differ from the last commit, for the gutter.</summary>
    [ObservableProperty]
    private IReadOnlyList<Core.Git.LineChange> _lineChanges = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title))]
    private bool _isDirty;

    /// <summary>Lean's diagnostics for this file, newest set.</summary>
    [ObservableProperty]
    private IReadOnlyList<Diagnostic> _diagnostics = [];

    /// <summary>The ranges Lean is still elaborating; empty when the file is done.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsProcessing))]
    private IReadOnlyList<LeanFileProgressRange> _processing = [];

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
    public void Reveal(int line, int column)
    {
        CaretLine = line;
        CaretColumn = column;
        RevealRequested?.Invoke(line, column);
    }

    public int CaretLine { get; set; }
    public int CaretColumn { get; set; }

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

    public string[] Lines() => Document.Text.Split('\n').Select(l => l.TrimEnd('\r')).ToArray();
}

/// <summary>A problem in the list: from Lean's live diagnostics for an open file, or from the last build for any file.</summary>
public sealed record ProblemItem(DocumentViewModel? Document, string Path, Diagnostic Diagnostic)
{
    public ProblemItem(DocumentViewModel document, Diagnostic diagnostic)
        : this(document, document.Path, diagnostic)
    {
    }

    public string File => System.IO.Path.GetFileName(Path);
    public string Location => $"{Diagnostic.Range.Start.Line + 1}:{Diagnostic.Range.Start.Character + 1}";
    public string Message => Diagnostic.Message.Split('\n')[0];
    public string FullMessage => Diagnostic.Message;
    public string Icon => Diagnostic.Severity switch
    {
        DiagnosticSeverity.Error => "✕",
        DiagnosticSeverity.Warning => "▲",
        _ => "●",
    };
    public DiagnosticSeverity Severity => Diagnostic.Severity;
    public bool IsError => Diagnostic.Severity == DiagnosticSeverity.Error;
    public bool IsWarning => Diagnostic.Severity == DiagnosticSeverity.Warning;
}

public sealed class ObservableList<T> : ObservableCollection<T>
{
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

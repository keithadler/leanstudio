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
        Document.TextChanged += (_, _) =>
        {
            IsDirty = Document.Text != SavedText;
            TextChanged?.Invoke(this);
        };
    }

    public string Path { get; private set; }
    public string Uri { get; private set; }
    public TextDocument Document { get; }
    public string SavedText { get; private set; }

    public string Title => System.IO.Path.GetFileName(Path) + (IsDirty ? " •" : "");

    public bool IsLean => Path.EndsWith(".lean", StringComparison.OrdinalIgnoreCase);

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

    /// <summary>Raised on every edit, for the session to forward to Lean.</summary>
    public event Action<DocumentViewModel>? TextChanged;

    /// <summary>Raised when something asks the editor to move the caret (go to definition, a problem, a declaration).</summary>
    public event Action<int, int>? RevealRequested;

    public void Reveal(int line, int column) => RevealRequested?.Invoke(line, column);

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

    public string[] Lines() => Document.Text.Split('\n').Select(l => l.TrimEnd('\r')).ToArray();
}

public sealed record ProblemItem(DocumentViewModel Document, Diagnostic Diagnostic)
{
    public string File => System.IO.Path.GetFileName(Document.Path);
    public string Location => $"{Diagnostic.Range.Start.Line + 1}:{Diagnostic.Range.Start.Character + 1}";
    public string Message => Diagnostic.Message.Split('\n')[0];
    public string FullMessage => Diagnostic.Message;
    public string Icon => Diagnostic.Severity switch
    {
        DiagnosticSeverity.Error => "⛔",
        DiagnosticSeverity.Warning => "⚠",
        _ => "ℹ",
    };
    public DiagnosticSeverity Severity => Diagnostic.Severity;
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

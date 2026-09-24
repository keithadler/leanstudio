using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace LeanStudio.App.ViewModels;

/// <summary>
/// The editor split in two, side by side (View ▸ Split Editor): a lemma beside the proof that uses it, or two places
/// in one file. Each side shows its own document; <see cref="ActiveDocument"/> is the one in the side that has the
/// focus, so every command, the Tactic State and the status bar follow where you are typing.
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>The document in the main (left) editor, the one the tabs show.</summary>
    [ObservableProperty]
    private DocumentViewModel? _primaryDocument;

    /// <summary>The document in the split (right) editor; null when the editor isn't split.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSplit))]
    private DocumentViewModel? _splitDocument;

    private bool _splitFocused;

    /// <summary>Whether the editor is split.</summary>
    public bool IsSplit => SplitDocument is not null;

    /// <summary>Whether the split (right) side has the focus, so it is the one <see cref="ActiveDocument"/> shows.</summary>
    public bool SplitFocused => _splitFocused && SplitDocument is not null;

    partial void OnPrimaryDocumentChanged(DocumentViewModel? value)
    {
        if (!SplitFocused && value is not null && ActiveDocument != value)
        {
            ActiveDocument = value;
        }
    }

    /// <summary>The active document changed: it goes in the focused side (the left one, unless the right has the focus and shows it).</summary>
    private void FollowActiveDocument(DocumentViewModel? value)
    {
        if (SplitFocused && value == SplitDocument)
        {
            return;
        }
        _splitFocused = false;
        if (value is not null && PrimaryDocument != value)
        {
            PrimaryDocument = value;
        }
    }

    /// <summary>An editor side got the focus: its document becomes the active one.</summary>
    /// <param name="split">Whether it is the split (right) side.</param>
    public void EditorFocused(bool split)
    {
        if (split && SplitDocument is null)
        {
            return;
        }
        _splitFocused = split;
        DocumentViewModel? doc = split ? SplitDocument : PrimaryDocument;
        if (doc is not null && ActiveDocument != doc)
        {
            ActiveDocument = doc;
        }
    }

    /// <summary>Split the editor: the active document on the right too, to scroll to another place in it or switch it to another file.</summary>
    [RelayCommand]
    public void SplitEditor()
    {
        if (ActiveDocument is DocumentViewModel d)
        {
            SplitDocument = d;
        }
    }

    /// <summary>Close the split (right) side; the left one keeps its document.</summary>
    [RelayCommand]
    public void CloseSplit()
    {
        bool wasFocused = SplitFocused;
        _splitFocused = false;
        SplitDocument = null;
        if (wasFocused && PrimaryDocument is not null)
        {
            ActiveDocument = PrimaryDocument;
        }
    }

    /// <summary>
    /// A document was closed (it was at <paramref name="index"/> in the tabs): the split closes if it showed it, and
    /// the left side shows the neighbouring tab if it did.
    /// </summary>
    private void SidesForget(DocumentViewModel d, int index)
    {
        if (SplitDocument == d)
        {
            CloseSplit();
        }
        if (PrimaryDocument == d)
        {
            PrimaryDocument = Documents.Count == 0 ? null : Documents[Math.Clamp(index, 0, Documents.Count - 1)];
        }
    }
}

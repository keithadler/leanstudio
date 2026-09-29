using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LeanStudio.Core.Editing;

namespace LeanStudio.App.ViewModels;

/// <summary>
/// The Markdown preview in the right column: the active Markdown file (a README, blueprint notes) drawn as it will
/// read, kept up to date as it is edited, its links opened in the browser or, for files of the project, in the editor.
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>The right-hand panel with the Markdown preview.</summary>
    public const int PreviewTab = 3;

    private DocumentViewModel? _previewDoc;
    private CancellationTokenSource? _previewCts;

    /// <summary>The previewed file, read into blocks.</summary>
    [ObservableProperty]
    private IReadOnlyList<MdBlock> _markdownPreview = [];

    /// <summary>The previewed file's folder, for its relative images and links.</summary>
    [ObservableProperty]
    private string? _markdownFolder;

    /// <summary>What the preview says above itself: the file, or how to open one.</summary>
    [ObservableProperty]
    private string _markdownTitle = "Open a Markdown file and choose View ▸ Markdown Preview.";

    /// <summary>A file is Markdown (by its extension).</summary>
    public static bool IsMarkdown(string path) => Path.GetExtension(path).ToLowerInvariant() is ".md" or ".markdown";

    /// <summary>Preview the active file, which must be Markdown, in the right column.</summary>
    [RelayCommand]
    public void OpenMarkdownPreview()
    {
        if (ActiveDocument is not { } d || !IsMarkdown(d.Path))
        {
            MarkdownTitle = "The Markdown preview shows .md files: open one first.";
            RightTab = PreviewTab;
            return;
        }
        ShowPreviewOf(d);
        RightTab = PreviewTab;
    }

    private void ShowPreviewOf(DocumentViewModel d)
    {
        _previewDoc = d;
        MarkdownFolder = Path.GetDirectoryName(d.Path);
        MarkdownTitle = Path.GetFileName(d.Path);
        MarkdownPreview = MarkdownModel.Parse(d.Document.Text);
    }

    /// <summary>The previewed file was edited: draw it again after a moment, so typing is not slowed.</summary>
    private void MarkdownEdited(DocumentViewModel d)
    {
        if (d != _previewDoc)
        {
            return;
        }
        _previewCts?.Cancel();
        var cts = _previewCts = new CancellationTokenSource();
        _ = RefreshPreviewAsync(d, cts.Token);
    }

    private async Task RefreshPreviewAsync(DocumentViewModel d, CancellationToken ct)
    {
        try
        {
            await Task.Delay(250, ct);
            if (d == _previewDoc)
            {
                MarkdownPreview = MarkdownModel.Parse(d.Document.Text);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>Another file became active: while the preview is showing, it follows to another Markdown file.</summary>
    private void PreviewFollow(DocumentViewModel? d)
    {
        if (RightTab == PreviewTab && d is not null && IsMarkdown(d.Path) && d != _previewDoc)
        {
            ShowPreviewOf(d);
        }
    }

    /// <summary>
    /// Open a link from the preview: a web or mail address in the browser, a file (relative to the previewed one)
    /// in the editor.
    /// </summary>
    public async Task OpenMarkdownLinkAsync(string url)
    {
        if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || url.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
        {
            if (Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
            {
                await _dialogs.LaunchAsync(uri);
            }
            return;
        }
        if (MarkdownFolder is not string folder || url.StartsWith('#'))
        {
            return;
        }
        string path = Path.GetFullPath(Path.Combine(folder, Uri.UnescapeDataString(url.Split('#', '?')[0])));
        if (File.Exists(path))
        {
            await OpenFileAsync(path);
        }
        else if (Directory.Exists(path))
        {
            Log($"{url} is a folder.");
        }
        else
        {
            Log($"No file {path}.");
        }
    }
}

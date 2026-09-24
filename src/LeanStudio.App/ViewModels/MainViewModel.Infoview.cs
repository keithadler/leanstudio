using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using LeanStudio.App.Services;
using LeanStudio.Core.Agents;
using LeanStudio.Lsp;

namespace LeanStudio.App.ViewModels;

/// <summary>
/// Lean's own infoview, the one VS Code uses, connected to this window: in the Infoview tab beside the Tactic State,
/// or in a browser tab. It renders ProofWidgets and every other user widget, and follows the cursor here. The Tactic
/// State panel stays the everyday view; this is for widgets and for anyone who wants the infoview they know.
/// </summary>
public sealed partial class MainViewModel : IInfoviewEditor
{
    private InfoviewBridge? _infoviewBridge;

    /// <summary>Vim's mode and pending keys for the status bar, empty when Vim mode is off.</summary>
    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty]
    private string _vimStatus = "";

    /// <summary>The infoview bridge, once the infoview has been opened.</summary>
    public InfoviewBridge? InfoviewBridge => _infoviewBridge;

    /// <summary>Start the bridge if needed and open Lean's infoview in the browser. Returns its address.</summary>
    [RelayCommand]
    public async Task<Uri?> OpenInfoviewAsync()
    {
        Uri url = StartInfoview();
        await _dialogs.LaunchAsync(url);
        Log("Lean's own infoview is open in your browser, with widgets. It follows the cursor here.");
        return url;
    }

    /// <summary>Show Lean's infoview in the Infoview tab, beside the Tactic State.</summary>
    [RelayCommand]
    public void ShowInfoview() => RightTab = InfoviewTab;

    /// <summary>Start the bridge if needed and return the address of the page for the Infoview tab.</summary>
    public Uri StartEmbeddedInfoview() => new(StartInfoview() + "&embedded=1");

    /// <summary>
    /// Open a link the infoview followed (documentation, say) in the browser. Only web links: a widget's page is
    /// not trusted to open files or other apps.
    /// </summary>
    public Task OpenLinkAsync(Uri link) =>
        link.Scheme is "http" or "https" ? _dialogs.LaunchAsync(link) : Task.CompletedTask;

    /// <summary>The theme changed: the infoview pages follow.</summary>
    public void InfoviewThemeChanged() => _infoviewBridge?.SetTheme(Settings.Theme == "Light" ? "light" : "dark");

    /// <summary>Start the bridge without opening a browser (for tests and scripts). Returns the page's address.</summary>
    public Uri StartInfoview()
    {
        if (_infoviewBridge is null)
        {
            _infoviewBridge = new InfoviewBridge(() => _server, this, InfoviewAssets.Get);
            _infoviewBridge.Log += line => Dispatcher.UIThread.Post(() => Log(line));
        }
        Uri url = _infoviewBridge.Start(Settings.Theme == "Light" ? "light" : "dark");
        if (ActiveDocument is { IsLean: true } d)
        {
            _infoviewBridge.CursorMoved(d.Uri, new Position(d.CaretLine, d.CaretColumn));
        }
        return url;
    }

    private void InfoviewCursor(DocumentViewModel doc, int line, int column)
    {
        if (doc.IsLean)
        {
            _infoviewBridge?.CursorMoved(doc.Uri, new Position(line, column));
        }
    }

    private async Task StopInfoviewAsync()
    {
        if (_infoviewBridge is not null)
        {
            await _infoviewBridge.DisposeAsync();
            _infoviewBridge = null;
        }
    }

    Task IInfoviewEditor.CopyAsync(string text) => Dispatcher.UIThread.InvokeAsync(() => _dialogs.CopyTextAsync(text));

    Task IInfoviewEditor.InsertTextAsync(string text, string kind, string? uri, Position? position) => Dispatcher.UIThread.InvokeAsync(async () =>
    {
        DocumentViewModel? d = uri is null ? ActiveDocument : Documents.FirstOrDefault(x => x.Uri == uri) ?? await OpenFileAsync(LeanServer.PathOf(uri));
        if (d is null)
        {
            return;
        }
        ActiveDocument = d;
        int line = position?.Line ?? d.CaretLine, column = position?.Character ?? d.CaretColumn;
        var doc = d.Document;
        AvaloniaEdit.Document.DocumentLine l = doc.GetLineByNumber(Math.Clamp(line + 1, 1, doc.LineCount));
        if (kind == "above")
        {
            // On a new line above, indented like the line it goes above.
            string current = doc.GetText(l);
            string indent = current[..(current.Length - current.TrimStart().Length)];
            doc.Insert(l.Offset, indent + text + "\n");
        }
        else
        {
            doc.Insert(Math.Min(l.Offset + column, l.EndOffset), text);
        }
    });

    Task IInfoviewEditor.ApplyEditAsync(WorkspaceEdit edit) => Dispatcher.UIThread.InvokeAsync(() => ApplyWorkspaceEditAsync(edit));

    Task IInfoviewEditor.ShowDocumentAsync(string uri, Lsp.Range? selection) => Dispatcher.UIThread.InvokeAsync(async () =>
    {
        await OpenFileAsync(LeanServer.PathOf(uri), selection?.Start.Line, selection?.Start.Character);
    });

    Task IInfoviewEditor.RestartFileAsync(string uri) => Dispatcher.UIThread.InvokeAsync(async () =>
    {
        if (Documents.FirstOrDefault(x => x.Uri == uri) is DocumentViewModel d)
        {
            ActiveDocument = d;
            await RestartFileAsync();
        }
    });
}

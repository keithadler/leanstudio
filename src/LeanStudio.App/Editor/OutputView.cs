using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using AvaloniaEdit;
using LeanStudio.App.ViewModels;

namespace LeanStudio.App.Editor;

/// <summary>Build logs, Lean server messages, and anything else Lean Studio ran, in a read-only editor.</summary>
public sealed class OutputView : UserControl
{
    private readonly TextEditor _editor = new()
    {
        IsReadOnly = true,
        ShowLineNumbers = false,
        FontFamily = new FontFamily("JuliaMono, Cascadia Code, SF Mono, Menlo, Consolas, DejaVu Sans Mono, monospace"),
        FontSize = 12,
        WordWrap = true,
        Padding = new Thickness(6, 4),
    };
    private MainViewModel? _vm;

    public OutputView()
    {
        // Follow the app's theme: the editor's own default is black text, unreadable on the dark theme.
        _editor.Bind(TextEditor.ForegroundProperty, _editor.GetResourceObservable("SystemControlForegroundBaseHighBrush"));
        _editor.Background = Brushes.Transparent;
        _editor.Options.AllowScrollBelowDocument = false;
        Content = _editor;
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null)
        {
            _vm.OutputAppended -= ScrollToEnd;
        }
        _vm = DataContext as MainViewModel;
        if (_vm is not null)
        {
            _editor.Document = _vm.Output;
            _vm.OutputAppended += ScrollToEnd;
        }
    }

    /// <summary>Keep the newest output in view: the last line at the bottom, after layout has caught up.</summary>
    private void ScrollToEnd() =>
        Avalonia.Threading.Dispatcher.UIThread.Post(() => _editor.ScrollToLine(Math.Max(1, _editor.Document.LineCount - 1)), Avalonia.Threading.DispatcherPriority.Background);
}

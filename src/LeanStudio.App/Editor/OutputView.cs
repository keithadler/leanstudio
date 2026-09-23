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

    public OutputView() => Content = _editor;

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

    private void ScrollToEnd() => _editor.ScrollToEnd();
}

using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using AvaloniaEdit;
using AvaloniaEdit.TextMate;
using LeanStudio.App.Services;
using LeanStudio.App.ViewModels;
using TextMateSharp.Grammars;

namespace LeanStudio.App.Editor;

/// <summary>The compiled C for the definition at the cursor: read-only, highlighted as C.</summary>
public sealed class CodeView : UserControl
{
    private readonly TextEditor _editor = new()
    {
        IsReadOnly = true,
        ShowLineNumbers = true,
        FontFamily = new FontFamily("JuliaMono, Cascadia Code, SF Mono, Menlo, Consolas, DejaVu Sans Mono, monospace"),
        FontSize = 12,
        WordWrap = false,
        Padding = new Thickness(4),
    };
    private MainViewModel? _vm;

    /// <summary>Create the view; it shows <see cref="MainViewModel.CCode"/> of the view model it is given as its data context.</summary>
    public CodeView()
    {
        TextMate.Installation tm = _editor.InstallTextMate(new LeanRegistryOptions(ThemeName.DarkPlus));
        tm.SetGrammar("source.c");
        if (tm.TryGetThemeColor("editor.background", out string? bg) && Color.TryParse(bg, out Color b))
        {
            _editor.Background = new SolidColorBrush(b);
        }
        if (tm.TryGetThemeColor("editor.foreground", out string? fg) && Color.TryParse(fg, out Color f))
        {
            _editor.Foreground = new SolidColorBrush(f);
        }
        Content = _editor;
    }

    /// <inheritdoc/>
    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null)
        {
            _vm.PropertyChanged -= OnVmChanged;
        }
        _vm = DataContext as MainViewModel;
        if (_vm is not null)
        {
            _vm.PropertyChanged += OnVmChanged;
            _editor.Text = _vm.CCode;
        }
    }

    private void OnVmChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.CCode) && _vm is not null)
        {
            _editor.Text = _vm.CCode;
            _editor.ScrollToHome();
        }
    }
}

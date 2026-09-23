using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using AvaloniaEdit;
using AvaloniaEdit.CodeCompletion;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Rendering;
using AvaloniaEdit.TextMate;
using LeanStudio.App.Services;
using LeanStudio.App.ViewModels;
using LeanStudio.Core.Editing;
using LeanStudio.Core.Verification;
using LeanStudio.Lsp;
using TextMateSharp.Grammars;

namespace LeanStudio.App.Editor;

/// <summary>
/// The code editor: one AvaloniaEdit text editor that shows whichever document is active, with Lean highlighting,
/// Lean's diagnostics underlined, elaboration progress and Tenet verdicts in the gutter, Unicode abbreviations,
/// hovers, completion and go-to-definition.
/// </summary>
public sealed class LeanEditor : UserControl
{
    public static readonly StyledProperty<DocumentViewModel?> DocumentProperty =
        AvaloniaProperty.Register<LeanEditor, DocumentViewModel?>(nameof(Document));

    public static readonly StyledProperty<MainViewModel?> MainProperty =
        AvaloniaProperty.Register<LeanEditor, MainViewModel?>(nameof(Main));

    private readonly TextEditor _editor;
    private readonly DiagnosticRenderer _diagnostics = new();
    private readonly StatusMargin _margin = new();
    private readonly TextMate.Installation _textMate;
    private readonly Dictionary<DocumentViewModel, Vector> _scroll = new();
    private DocumentViewModel? _current;
    private int _abbrevStart = -1;
    private CompletionWindow? _completion;
    private CancellationTokenSource? _hoverCts;
    private bool _dark = true;

    public LeanEditor()
    {
        _editor = new TextEditor
        {
            ShowLineNumbers = true,
            FontFamily = new FontFamily("JuliaMono, Cascadia Code, SF Mono, Menlo, Consolas, DejaVu Sans Mono, Noto Sans Mono, monospace"),
            FontSize = 14,
            WordWrap = false,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            IsEnabled = false,
        };
        _editor.Options.ConvertTabsToSpaces = true;
        _editor.Options.IndentationSize = 2;
        _editor.Options.HighlightCurrentLine = true;
        _editor.Options.EnableHyperlinks = false;
        _editor.Options.EnableEmailHyperlinks = false;
        _editor.Options.AllowScrollBelowDocument = true;
        _editor.TextArea.TextView.BackgroundRenderers.Add(_diagnostics);
        _editor.TextArea.LeftMargins.Insert(0, _margin);
        _textMate = _editor.InstallTextMate(new LeanRegistryOptions(ThemeName.DarkPlus));
        _textMate.SetGrammar(LeanRegistryOptions.LeanScope);

        _editor.TextArea.Caret.PositionChanged += (_, _) => OnCaretMoved();
        _editor.TextArea.TextEntering += OnTextEntering;
        _editor.TextArea.TextEntered += OnTextEntered;
        _editor.TextArea.TextView.PointerHover += OnPointerHover;
        _editor.TextArea.TextView.PointerHoverStopped += (_, _) => CloseHover();
        _editor.TextArea.TextView.PointerPressed += OnPointerPressed;
        _editor.AddHandler(KeyDownEvent, OnKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        _margin.PointerPressed += OnMarginPressed;

        Content = _editor;
    }

    public DocumentViewModel? Document
    {
        get => GetValue(DocumentProperty);
        set => SetValue(DocumentProperty, value);
    }

    public MainViewModel? Main
    {
        get => GetValue(MainProperty);
        set => SetValue(MainProperty, value);
    }

    public TextEditor TextEditor => _editor;

    public void ApplySettings(Settings s)
    {
        _editor.FontSize = s.EditorFontSize;
        _editor.FontFamily = new FontFamily(s.EditorFontFamily);
        _editor.ShowLineNumbers = s.ShowLineNumbers;
        bool dark = s.Theme != "Light";
        if (dark != _dark)
        {
            _dark = dark;
            var options = (LeanRegistryOptions)_textMate.RegistryOptions;
            _textMate.SetTheme(options.LoadTheme(dark ? ThemeName.DarkPlus : ThemeName.LightPlus));
        }
        ApplyThemeColors();
    }

    /// <summary>Take the editor's background and foreground from the TextMate theme so the two agree.</summary>
    private void ApplyThemeColors()
    {
        if (_textMate.TryGetThemeColor("editor.background", out string? bg) && Color.TryParse(bg, out Color b))
        {
            _editor.Background = new SolidColorBrush(b);
        }
        if (_textMate.TryGetThemeColor("editor.foreground", out string? fg) && Color.TryParse(fg, out Color f))
        {
            _editor.Foreground = new SolidColorBrush(f);
        }
        if (_textMate.TryGetThemeColor("editor.selectionBackground", out string? sel) && Color.TryParse(sel, out Color s))
        {
            _editor.TextArea.SelectionBrush = new SolidColorBrush(s);
        }
        if (_textMate.TryGetThemeColor("editor.lineHighlightBackground", out string? lh) && Color.TryParse(lh, out Color l))
        {
            _editor.TextArea.TextView.CurrentLineBackground = new SolidColorBrush(l);
            _editor.TextArea.TextView.CurrentLineBorder = new Pen(new SolidColorBrush(l));
        }
        if (_textMate.TryGetThemeColor("editorLineNumber.foreground", out string? ln) && Color.TryParse(ln, out Color n))
        {
            _editor.LineNumbersForeground = new SolidColorBrush(n);
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        ApplyThemeColors();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == DocumentProperty)
        {
            Switch(change.GetNewValue<DocumentViewModel?>());
        }
    }

    private void Switch(DocumentViewModel? doc)
    {
        if (_current is not null)
        {
            _scroll[_current] = new Vector(_editor.HorizontalOffset, _editor.VerticalOffset);
            _current.PropertyChanged -= OnDocumentPropertyChanged;
            _current.RevealRequested -= Reveal;
        }
        _current = doc;
        _abbrevStart = -1;
        _completion?.Close();
        if (doc is null)
        {
            _editor.Document = new TextDocument();
            _editor.IsEnabled = false;
            _diagnostics.Update([]);
            _margin.Update([], new Dictionary<int, DeclarationVerdict>());
            return;
        }
        _editor.IsEnabled = true;
        _editor.Document = doc.Document;
        _textMate.SetGrammar(doc.IsLean
            ? LeanRegistryOptions.LeanScope
            : ((LeanRegistryOptions)_textMate.RegistryOptions).ScopeForExtension(System.IO.Path.GetExtension(doc.Path)));
        doc.PropertyChanged += OnDocumentPropertyChanged;
        doc.RevealRequested += Reveal;
        _diagnostics.Update(doc.Diagnostics);
        _margin.Update(doc.Processing, doc.Verdicts);
        int offset = Math.Min(doc.Document.TextLength, SafeOffset(doc.Document, doc.CaretLine, doc.CaretColumn));
        _editor.TextArea.Caret.Offset = offset;
        if (_scroll.TryGetValue(doc, out Vector v))
        {
            Dispatcher.UIThread.Post(() => _editor.ScrollToHorizontalOffset(v.X), DispatcherPriority.Background);
            Dispatcher.UIThread.Post(() => _editor.ScrollToVerticalOffset(v.Y), DispatcherPriority.Background);
        }
        _editor.TextArea.TextView.Redraw();
        Dispatcher.UIThread.Post(() => _editor.TextArea.Focus(), DispatcherPriority.Background);
    }

    private void OnDocumentPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (_current is null)
        {
            return;
        }
        switch (e.PropertyName)
        {
            case nameof(DocumentViewModel.Diagnostics):
                _diagnostics.Update(_current.Diagnostics);
                _editor.TextArea.TextView.InvalidateLayer(_diagnostics.Layer);
                break;
            case nameof(DocumentViewModel.Processing):
            case nameof(DocumentViewModel.Verdicts):
                _margin.Update(_current.Processing, _current.Verdicts);
                break;
        }
    }

    private static int SafeOffset(TextDocument doc, int line, int column)
    {
        if (doc.LineCount == 0)
        {
            return 0;
        }
        DocumentLine l = doc.GetLineByNumber(Math.Clamp(line + 1, 1, doc.LineCount));
        return l.Offset + Math.Clamp(column, 0, l.Length);
    }

    public void Reveal(int line, int column)
    {
        if (_current is null)
        {
            return;
        }
        int offset = SafeOffset(_current.Document, line, column);
        _editor.TextArea.Caret.Offset = offset;
        _editor.TextArea.Caret.BringCaretToView(80);
        _editor.ScrollTo(Math.Clamp(line + 1, 1, Math.Max(1, _current.Document.LineCount)), column + 1);
        _editor.TextArea.Focus();
    }

    private void OnCaretMoved()
    {
        if (_current is null || Main is null)
        {
            return;
        }
        TextViewPosition p = _editor.TextArea.Caret.Position;
        Main.CaretMoved(_current, p.Line - 1, p.Column - 1);
        if (_abbrevStart >= 0)
        {
            int caret = _editor.CaretOffset;
            if (caret <= _abbrevStart || caret > _abbrevStart + 1 + 24 || !IsBackslashAt(_abbrevStart))
            {
                EndAbbreviation();
            }
        }
    }

    // ---- Unicode abbreviations ----

    private bool UnicodeInput => Main?.Settings.UnicodeInput ?? true;

    private bool IsBackslashAt(int offset) =>
        _current is not null && offset < _current.Document.TextLength && _current.Document.GetCharAt(offset) == Abbreviations.Leader;

    private string PendingAbbreviation()
    {
        if (_abbrevStart < 0 || _current is null)
        {
            return "";
        }
        int caret = _editor.CaretOffset;
        return caret > _abbrevStart + 1 ? _current.Document.GetText(_abbrevStart + 1, caret - _abbrevStart - 1) : "";
    }

    private void OnTextEntering(object? sender, TextInputEventArgs e)
    {
        if (!UnicodeInput || _abbrevStart < 0 || string.IsNullOrEmpty(e.Text) || _current is null)
        {
            return;
        }
        string pending = PendingAbbreviation();
        char c = e.Text[0];
        string? replacement = Abbreviations.OnType(pending, c);
        if (replacement is not null)
        {
            ReplacePending(replacement);
            if (c == '\t')
            {
                e.Handled = true;
            }
        }
        else if (!Abbreviations.IsPrefix(pending + c))
        {
            EndAbbreviation();
        }
    }

    private void OnTextEntered(object? sender, TextInputEventArgs e)
    {
        if (_current is null || string.IsNullOrEmpty(e.Text))
        {
            return;
        }
        if (UnicodeInput && e.Text == "\\")
        {
            _abbrevStart = _editor.CaretOffset - 1;
            ShowAbbreviationCompletion();
            return;
        }
        if (UnicodeInput && _abbrevStart >= 0)
        {
            string pending = PendingAbbreviation();
            if (Abbreviations.Lookup(pending) is string symbol && !Abbreviations.HasLongerMatch(pending))
            {
                ReplacePending(symbol);
            }
            else if (!Abbreviations.IsPrefix(pending))
            {
                EndAbbreviation();
            }
            return;
        }
        if (e.Text == "." && _current.IsLean)
        {
            _ = ShowCompletionAsync();
        }
    }

    private void ReplacePending(string symbol)
    {
        if (_current is null || _abbrevStart < 0)
        {
            return;
        }
        int start = _abbrevStart;
        int length = _editor.CaretOffset - start;
        _abbrevStart = -1;
        _completion?.Close();
        _current.Document.Replace(start, length, symbol);
        _editor.CaretOffset = start + symbol.Length;
    }

    private void EndAbbreviation()
    {
        _abbrevStart = -1;
        if (_completion?.Tag as string == "abbrev")
        {
            _completion.Close();
        }
    }

    private void ShowAbbreviationCompletion()
    {
        _completion?.Close();
        var window = new CompletionWindow(_editor.TextArea)
        {
            Tag = "abbrev",
            StartOffset = _editor.CaretOffset,
            CloseWhenCaretAtBeginning = true,
            MinWidth = 260,
        };
        int priority = 100000;
        foreach (var kv in Abbreviations.Table.OrderBy(kv => kv.Key.Length).ThenBy(kv => kv.Key, StringComparer.Ordinal))
        {
            window.CompletionList.CompletionData.Add(new AbbreviationCompletionData(kv.Key, kv.Value, priority--));
        }
        window.Closed += (_, _) =>
        {
            if (_completion == window)
            {
                _completion = null;
            }
        };
        _completion = window;
        window.Show();
    }

    // ---- completion ----

    private async Task ShowCompletionAsync()
    {
        if (_current is not { IsLean: true } doc || Main?.Server is not { State: LeanServerState.Running } server)
        {
            return;
        }
        TextViewPosition p = _editor.TextArea.Caret.Position;
        int caret = _editor.CaretOffset;
        // Completion answers about the text Lean has; give the pending edit a moment to arrive.
        await Task.Delay(200);
        IReadOnlyList<CompletionItem> items;
        try
        {
            items = await server.CompletionAsync(doc.Uri, new Position(p.Line - 1, p.Column - 1));
        }
        catch (Exception e) when (e is JsonRpcException or IOException)
        {
            return;
        }
        if (items.Count == 0 || _current != doc || Math.Abs(_editor.CaretOffset - caret) > 1)
        {
            return;
        }
        _completion?.Close();
        int start = _editor.CaretOffset;
        while (start > 0 && IsIdentChar(doc.Document.GetCharAt(start - 1)))
        {
            start--;
        }
        var window = new CompletionWindow(_editor.TextArea) { StartOffset = start, MinWidth = 380 };
        double priority = items.Count;
        foreach (CompletionItem item in items.OrderBy(i => i.SortText ?? i.Label, StringComparer.Ordinal).Take(2000))
        {
            window.CompletionList.CompletionData.Add(new LeanCompletionData(item, priority--));
        }
        window.Closed += (_, _) =>
        {
            if (_completion == window)
            {
                _completion = null;
            }
        };
        _completion = window;
        window.Show();
    }

    private static bool IsIdentChar(char c) => char.IsLetterOrDigit(c) || c is '_' or '\'' or '!' or '?' || (c > 127 && char.IsLetter(c));

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        bool cmd = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        if (e.Key == Key.Space && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            e.Handled = true;
            _ = ShowCompletionAsync();
        }
        else if (e.Key == Key.F12)
        {
            e.Handled = true;
            Main?.GoToDefinitionCommand.Execute(null);
        }
        else if (e.Key == Key.Tab && _abbrevStart >= 0 && UnicodeInput)
        {
            string pending = PendingAbbreviation();
            if (Abbreviations.Lookup(pending) is string symbol)
            {
                e.Handled = true;
                ReplacePending(symbol);
            }
        }
        else if (e.Key == Key.OemQuestion && cmd)
        {
            e.Handled = true;
            ToggleLineComment();
        }
    }

    /// <summary>Comment or uncomment the selected lines with <c>--</c>.</summary>
    private void ToggleLineComment()
    {
        if (_current is null)
        {
            return;
        }
        TextDocument doc = _current.Document;
        TextArea area = _editor.TextArea;
        int startLine = doc.GetLineByOffset(area.Selection.IsEmpty ? _editor.CaretOffset : area.Selection.SurroundingSegment.Offset).LineNumber;
        int endLine = doc.GetLineByOffset(area.Selection.IsEmpty ? _editor.CaretOffset : area.Selection.SurroundingSegment.EndOffset).LineNumber;
        var lines = Enumerable.Range(startLine, endLine - startLine + 1).Select(doc.GetLineByNumber).ToList();
        bool allCommented = lines.Where(l => doc.GetText(l).Trim().Length > 0).All(l => doc.GetText(l).TrimStart().StartsWith("--", StringComparison.Ordinal));
        using (doc.RunUpdate())
        {
            foreach (DocumentLine l in lines.AsEnumerable().Reverse())
            {
                string text = doc.GetText(l);
                int indent = text.Length - text.TrimStart().Length;
                if (allCommented)
                {
                    int at = text.IndexOf("--", StringComparison.Ordinal);
                    if (at >= 0)
                    {
                        int len = at + 2 < text.Length && text[at + 2] == ' ' ? 3 : 2;
                        doc.Remove(l.Offset + at, len);
                    }
                }
                else if (text.Trim().Length > 0)
                {
                    doc.Insert(l.Offset + indent, "-- ");
                }
            }
        }
    }

    // ---- hover and go to definition ----

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        bool cmd = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        if (!cmd || _current is null)
        {
            return;
        }
        TextViewPosition? pos = _editor.TextArea.TextView.GetPosition(e.GetPosition(_editor.TextArea.TextView) + _editor.TextArea.TextView.ScrollOffset);
        if (pos is TextViewPosition p)
        {
            _editor.TextArea.Caret.Position = p;
            e.Handled = true;
            Main?.GoToDefinitionCommand.Execute(null);
        }
    }

    private void OnMarginPressed(object? sender, PointerPressedEventArgs e)
    {
        TextViewPosition? pos = _editor.TextArea.TextView.GetPositionFloor(new Point(0, e.GetPosition(_editor.TextArea.TextView).Y) + _editor.TextArea.TextView.ScrollOffset);
        if (pos is TextViewPosition p && _margin.VerdictAtLine(p.Line) is DeclarationVerdict v && Main is not null)
        {
            Main.BottomTab = 2;
            _ = Main.Navigator.ShowAsync(v.Name);
            Main.SidebarTab = MainViewModel.LibraryTab;
        }
    }

    private async void OnPointerHover(object? sender, PointerEventArgs e)
    {
        if (_current is not { IsLean: true } doc)
        {
            return;
        }
        TextView view = _editor.TextArea.TextView;
        TextViewPosition? pos = view.GetPosition(e.GetPosition(view) + view.ScrollOffset);
        if (pos is not TextViewPosition p)
        {
            return;
        }
        var lsp = new Position(p.Line - 1, p.Column - 1);
        _hoverCts?.Cancel();
        var cts = new CancellationTokenSource();
        _hoverCts = cts;

        var panel = new StackPanel { Spacing = 6, MaxWidth = 720 };
        foreach (Diagnostic d in doc.Diagnostics.Where(d => d.Range.Contains(lsp) || (d.Range.Start == d.Range.End && d.Range.Start.Line == lsp.Line)))
        {
            panel.Children.Add(new TextBlock
            {
                Text = d.Message,
                TextWrapping = TextWrapping.Wrap,
                FontFamily = _editor.FontFamily,
                FontSize = _editor.FontSize - 1,
                Foreground = d.Severity switch
                {
                    DiagnosticSeverity.Error => DiagnosticRenderer.ErrorBrush,
                    DiagnosticSeverity.Warning => DiagnosticRenderer.WarningBrush,
                    _ => DiagnosticRenderer.InfoBrush,
                },
            });
        }
        if (Main?.Server is { State: LeanServerState.Running } server)
        {
            try
            {
                Hover? h = await server.HoverAsync(doc.Uri, lsp, cts.Token);
                if (cts.IsCancellationRequested)
                {
                    return;
                }
                if (h is not null && h.Contents.Trim().Length > 0)
                {
                    foreach (Control c in RenderMarkdown(h.Contents))
                    {
                        panel.Children.Add(c);
                    }
                }
            }
            catch (Exception ex) when (ex is JsonRpcException or IOException or OperationCanceledException)
            {
            }
        }
        if (panel.Children.Count == 0 || cts.IsCancellationRequested)
        {
            return;
        }
        ToolTip.SetTip(view, panel);
        ToolTip.SetPlacement(view, PlacementMode.Pointer);
        ToolTip.SetIsOpen(view, true);
    }

    private void CloseHover()
    {
        _hoverCts?.Cancel();
        ToolTip.SetIsOpen(_editor.TextArea.TextView, false);
    }

    /// <summary>
    /// Lean's hovers are Markdown: fenced Lean code for the signature, then the docstring. Code is shown in the
    /// editor's font; prose as wrapped text. That covers what Lean sends without a Markdown engine.
    /// </summary>
    private List<Control> RenderMarkdown(string md)
    {
        var controls = new List<Control>();
        var prose = new System.Text.StringBuilder();
        var code = new System.Text.StringBuilder();
        bool inCode = false;
        void FlushProse()
        {
            string t = prose.ToString().Trim();
            if (t.Length > 0 && t != "***" && t != "---")
            {
                controls.Add(new TextBlock { Text = t.Replace("`", "", StringComparison.Ordinal), TextWrapping = TextWrapping.Wrap });
            }
            prose.Clear();
        }
        foreach (string raw in md.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                if (inCode)
                {
                    controls.Add(new Border
                    {
                        Padding = new Thickness(6, 4),
                        CornerRadius = new CornerRadius(4),
                        Background = new SolidColorBrush(Color.FromArgb(0x30, 0x80, 0x80, 0x80)),
                        Child = new SelectableTextBlock { Text = code.ToString().TrimEnd(), FontFamily = _editor.FontFamily, FontSize = _editor.FontSize - 1, TextWrapping = TextWrapping.Wrap },
                    });
                    code.Clear();
                    inCode = false;
                }
                else
                {
                    FlushProse();
                    inCode = true;
                }
                continue;
            }
            if (inCode)
            {
                code.AppendLine(line);
            }
            else if (line.Trim() is "***" or "---")
            {
                FlushProse();
                controls.Add(new Separator { Margin = new Thickness(0, 2) });
            }
            else
            {
                prose.AppendLine(line);
            }
        }
        FlushProse();
        return controls;
    }
}

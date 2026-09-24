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
    /// <summary>The proof-end marks drawn (for checks).</summary>
    public ProofMarksRenderer ProofMarks => _proofMarks;

    /// <summary>The cursors beyond the editor's own (⌘D, ⌥-click…).</summary>
    public MultiCursorLayer MultiCursor => _multi;

    /// <summary>Defines the <see cref="Document"/> property.</summary>
    public static readonly StyledProperty<DocumentViewModel?> DocumentProperty =
        AvaloniaProperty.Register<LeanEditor, DocumentViewModel?>(nameof(Document));

    /// <summary>Defines the <see cref="Main"/> property.</summary>
    public static readonly StyledProperty<MainViewModel?> MainProperty =
        AvaloniaProperty.Register<LeanEditor, MainViewModel?>(nameof(Main));

    private readonly TextEditor _editor;
    private readonly DiagnosticRenderer _diagnostics = new();
    private readonly StatusMargin _margin = new();
    private readonly BracketHighlighter _brackets = new();
    private readonly InlineResults _inline = new();
    private readonly ProofMarksRenderer _proofMarks = new();
    private readonly TimingRenderer _timings = new(labels: false), _timingLabels = new(labels: true);
    private readonly SemanticColorizer _semantic = new();
    private readonly OccurrenceHighlighter _occurrences = new();
    private readonly InlayHintGenerator _hints = new();
    private CancellationTokenSource? _semanticCts, _occurrenceCts;
    private readonly VimEngine _vim;
    private readonly VimBlockCaret _vimCaret;
    private readonly MultiCursorLayer _multi;
    private readonly EmacsEngine _emacs;
    private bool _swallowText;
    private int _edits;
    private AvaloniaEdit.Folding.FoldingManager? _folding;
    private CancellationTokenSource? _foldCts;
    private readonly TextMate.Installation _textMate;
    private readonly Dictionary<DocumentViewModel, Vector> _scroll = new();
    private DocumentViewModel? _current;
    private bool _switching;
    private int _abbrevStart = -1;
    private CompletionWindow? _completion;
    private CancellationTokenSource? _hoverCts;
    private bool _dark = true;

    /// <summary>Create the editor, disabled until a <see cref="Document"/> is set.</summary>
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
        _multi = new MultiCursorLayer(_editor);
        _editor.Options.EnableRectangularSelection = true; // ⌥-drag (Alt+drag) selects a column
        _editor.TextArea.TextView.BackgroundRenderers.Add(_diagnostics);
        _editor.TextArea.TextView.BackgroundRenderers.Add(_brackets);
        _editor.TextArea.TextView.BackgroundRenderers.Add(_inline);
        _editor.TextArea.TextView.BackgroundRenderers.Add(_proofMarks);
        _editor.TextArea.TextView.BackgroundRenderers.Add(_timings);
        _editor.TextArea.TextView.BackgroundRenderers.Add(_timingLabels);
        _editor.TextArea.TextView.BackgroundRenderers.Add(_occurrences);
        _editor.TextArea.TextView.ElementGenerators.Add(_hints);
        _vimCaret = new VimBlockCaret(_editor);
        _editor.TextArea.TextView.BackgroundRenderers.Add(_vimCaret);
        _vim = new VimEngine(new EditorVimHost(_editor, VimEx));
        _emacs = new EmacsEngine(new EditorVimHost(_editor, EmacsEx));
        _emacs.Changed += OnEmacsChanged;
        _emacs.KilledText += text =>
        {
            if (TopLevel.GetTopLevel(this)?.Clipboard is { } cb)
            {
                _ = Avalonia.Input.Platform.ClipboardExtensions.SetValueAsync(cb, Avalonia.Input.DataFormat.Text, text);
            }
        };
        _vim.Changed += OnVimChanged;
        _editor.TextArea.IndentationStrategy = new LeanIndentationStrategy();
        _editor.TextArea.LeftMargins.Insert(0, _margin);
        _textMate = _editor.InstallTextMate(new LeanRegistryOptions(ThemeName.DarkPlus));
        _textMate.SetGrammar(LeanRegistryOptions.LeanScope);
        _editor.TextArea.TextView.LineTransformers.Add(_semantic);

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

    /// <summary>
    /// The document shown. Setting it swaps the editor's text document, restoring that document's caret and scroll
    /// position and choosing its highlighting; null shows an empty, disabled editor.
    /// </summary>
    public DocumentViewModel? Document
    {
        get => GetValue(DocumentProperty);
        set => SetValue(DocumentProperty, value);
    }

    /// <summary>The main view model, told when the caret moves and asked for hovers, completion and definitions.</summary>
    public MainViewModel? Main
    {
        get => GetValue(MainProperty);
        set => SetValue(MainProperty, value);
    }

    /// <summary>The AvaloniaEdit editor inside, for commands that act on it directly (find, undo, selection).</summary>
    public TextEditor TextEditor => _editor;

    /// <summary>The lightbulb was clicked: show Lean's fixes for this (0-based) line.</summary>
    public event Action<int>? QuickFixAtLineRequested;

    /// <summary>Lines with a message Lean can fix: a "Try this", or a hint marked [apply].</summary>
    private void UpdateBulbs() =>
        _margin.SetBulbs(_current is { IsLean: true } d
            ? d.Diagnostics.Where(x => x.Message.Contains("Try this", StringComparison.Ordinal) || x.Message.Contains("[apply]", StringComparison.Ordinal)
                                       || Core.Workflow.ImportFinder.MissingName(x.Message) is not null)
                .Select(x => x.Range.Start.Line + 1)
            : []);

    /// <summary>Apply the editor settings (font, line numbers, word wrap, inline results, theme) from <paramref name="s"/>.</summary>
    public void ApplySettings(Settings s)
    {
        _editor.FontSize = s.EditorFontSize;
        _editor.FontFamily = new FontFamily(s.EditorFontFamily);
        _editor.ShowLineNumbers = s.ShowLineNumbers;
        _inline.Enabled = s.InlineResults;
        _proofMarks.Enabled = s.ShowProofMarks;
        _editor.WordWrap = s.WordWrap;
        _editor.TextArea.TextView.InvalidateLayer(_inline.Layer);
        OnVimChanged();
        _semantic.Enabled = s.SemanticHighlighting;
        _hints.Enabled = s.InlayHints;
        _hints.FontFamily = _editor.FontFamily;
        _hints.FontSize = _editor.FontSize;
        _semantic.SetDark(s.Theme != "Light");
        _editor.TextArea.TextView.Redraw();
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

    /// <inheritdoc/>
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        ApplyThemeColors();
    }

    /// <inheritdoc/>
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
            _current.Document.Changed -= OnTextChangedForLayers;
            _current.RevealRequested -= Reveal;
        }
        _current = doc;
        _abbrevStart = -1;
        _completion?.Close();
        // A folding manager belongs to the document it was installed on: remove it before the document changes.
        if (_folding is not null)
        {
            AvaloniaEdit.Folding.FoldingManager.Uninstall(_folding);
            _folding = null;
        }
        if (doc is null)
        {
            _editor.Document = new TextDocument();
            _editor.IsEnabled = false;
            _diagnostics.Update([]);
            _inline.Update([]);
            _proofMarks.Update([]);
            _timings.Update([]);
            _timingLabels.Update([]);
            _margin.Update([], new Dictionary<int, DeclarationVerdict>(), []);
            return;
        }
        _editor.IsEnabled = true;
        // Attaching a document moves the caret to its start; that is not the person moving it, and must not
        // overwrite the position the document remembers, which is read back just below.
        (int caretLine, int caretColumn) = (doc.CaretLine, doc.CaretColumn);
        _switching = true;
        try
        {
            _editor.Document = doc.Document;
        }
        finally
        {
            _switching = false;
        }
        (doc.CaretLine, doc.CaretColumn) = (caretLine, caretColumn);
        _folding = AvaloniaEdit.Folding.FoldingManager.Install(_editor.TextArea);
        try
        {
            _textMate.SetGrammar(doc.IsLean
                ? LeanRegistryOptions.LeanScope
                : ((LeanRegistryOptions)_textMate.RegistryOptions).ScopeForExtension(System.IO.Path.GetExtension(doc.Path)));
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // Highlighting is a nicety; a grammar problem must never leave the editor without its document.
            Main?.Log($"No highlighting for {System.IO.Path.GetFileName(doc.Path)}: {e.Message}");
        }
        doc.PropertyChanged += OnDocumentPropertyChanged;
        doc.Document.Changed += OnTextChangedForLayers;
        _semantic.Clear();
        _hints.Clear();
        _occurrences.Clear();
        ScheduleSemantic(doc, 0);
        doc.RevealRequested += Reveal;
        _diagnostics.Update(doc.Diagnostics);
        _inline.Update(doc.Diagnostics);
        _proofMarks.Update(doc.ProofMarks);
        _timings.Update(doc.Timings);
        _timingLabels.Update(doc.Timings);
        _margin.Update(doc.Processing, doc.Verdicts, doc.LineChanges);
        UpdateBulbs();
        _editor.IsReadOnly = doc.IsVirtual;
        ScheduleFolds();
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
                _inline.Update(_current.Diagnostics);
                UpdateBulbs();
                _editor.TextArea.TextView.InvalidateLayer(_diagnostics.Layer);
                break;
            case nameof(DocumentViewModel.ProofMarks):
                _proofMarks.Update(_current.ProofMarks);
                _editor.TextArea.TextView.InvalidateLayer(_proofMarks.Layer);
                break;
            case nameof(DocumentViewModel.Timings):
                _timings.Update(_current.Timings);
                _timingLabels.Update(_current.Timings);
                _editor.TextArea.TextView.InvalidateLayer(_timings.Layer);
                _editor.TextArea.TextView.InvalidateLayer(_timingLabels.Layer);
                break;
            case nameof(DocumentViewModel.Processing):
            case nameof(DocumentViewModel.Verdicts):
            case nameof(DocumentViewModel.LineChanges):
                _margin.Update(_current.Processing, _current.Verdicts, _current.LineChanges);
                if (e.PropertyName == nameof(DocumentViewModel.Processing) && !_current.IsProcessing)
                {
                    ScheduleFolds();
                    ScheduleSemantic(_current, 300);
                }
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

    /// <summary>Move the caret to a 0-based line and column (clamped to the document), scroll it into view and focus the editor.</summary>
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
        if (_current is null || Main is null || _switching)
        {
            return;
        }
        TextViewPosition p = _editor.TextArea.Caret.Position;
        Main.CaretMoved(_current, p.Line - 1, p.Column - 1);
        UpdateBracketMatch();
        if (_vimCaret.Visible)
        {
            _editor.TextArea.TextView.InvalidateLayer(_vimCaret.Layer);
        }
        ScheduleOccurrences();
        if (_abbrevStart >= 0)
        {
            int caret = _editor.CaretOffset;
            if (caret <= _abbrevStart || caret > _abbrevStart + 1 + 24 || !IsBackslashAt(_abbrevStart))
            {
                EndAbbreviation();
            }
        }
    }

    // ---- Vim ----

    private bool VimOn => Main?.Settings.VimMode == true && _current is not null;

    /// <summary>The Vim engine (for tests and scripts).</summary>
    public VimEngine Vim => _vim;

    /// <summary>
    /// Keys that are not text: Escape, Enter, Backspace, the arrows, and Ctrl-R/D/U. In insert mode only Escape is
    /// Vim's; ⌘ and other Ctrl shortcuts always stay the app's.
    /// </summary>
    private bool VimKeyDown(KeyEventArgs e)
    {
        if (_vim.Mode == VimMode.Insert)
        {
            return e.Key == Key.Escape && e.KeyModifiers == KeyModifiers.None && _vim.Key("<Esc>");
        }
        string? key;
        if (e.KeyModifiers == KeyModifiers.Control)
        {
            key = e.Key switch { Key.R => "<C-r>", Key.D => "<C-d>", Key.U => "<C-u>", Key.OemOpenBrackets => "<Esc>", _ => null };
        }
        else if (e.KeyModifiers is KeyModifiers.None or KeyModifiers.Shift)
        {
            key = e.Key switch
            {
                Key.Escape => "<Esc>",
                Key.Enter => "<CR>",
                Key.Back => "<BS>",
                Key.Left => "h",
                Key.Right => "l",
                Key.Up => "k",
                Key.Down => "j",
                _ => null,
            };
        }
        else
        {
            return false;
        }
        return key is not null && _vim.Key(key);
    }

    private bool VimEx(string command)
    {
        switch (command)
        {
            case "w":
                Main?.SaveCommand.Execute(null);
                return true;
            case "q" or "q!":
                Main?.CloseDocumentCommand.Execute(_current);
                return true;
            case "wq" or "x":
                if (Main is not null)
                {
                    Main.SaveCommand.Execute(null);
                    Main.CloseDocumentCommand.Execute(_current);
                }
                return true;
            case "noh" or "nohlsearch":
                return true;
            default:
                Main?.Log($"Vim: :{command} is not supported here.");
                return false;
        }
    }

    private void OnVimChanged()
    {
        bool on = VimOn;
        _vimCaret.Visible = on && _vim.Mode != VimMode.Insert;
        _editor.TextArea.TextView.InvalidateLayer(_vimCaret.Layer);
        if (Main is not null)
        {
            Main.VimStatus = !on ? "" : _vim.Status.Length > 0 ? _vim.Status : "-- NORMAL --";
        }
    }

    // ---- screen readers ----

    /// <inheritdoc />
    protected override Avalonia.Automation.Peers.AutomationPeer OnCreateAutomationPeer() => new EditorPeer(this);

    /// <summary>
    /// What a screen reader is told about the editor: an edit field named after its file, whose value is the text.
    /// </summary>
    private sealed class EditorPeer(LeanEditor owner) : Avalonia.Automation.Peers.ControlAutomationPeer(owner), Avalonia.Automation.Provider.IValueProvider
    {
        protected override Avalonia.Automation.Peers.AutomationControlType GetAutomationControlTypeCore() => Avalonia.Automation.Peers.AutomationControlType.Edit;

        protected override string GetNameCore() => owner._current is DocumentViewModel d
            ? $"Editor, {System.IO.Path.GetFileName(d.Path)}, line {d.CaretLine + 1}"
            : "Editor, no file open";

        public bool IsReadOnly => owner._editor.IsReadOnly || owner._current is null;

        public string? Value => owner._current?.Document.Text;

        public void SetValue(string? value)
        {
            if (!IsReadOnly && value is not null)
            {
                owner._current!.ReplaceAll(value);
            }
        }
    }

    // ---- Emacs ----

    private bool EmacsOn => Main?.Settings.EmacsMode == true && Main.Settings.VimMode != true && _current is not null;

    /// <summary>The Emacs engine (for tests and scripts).</summary>
    public EmacsEngine Emacs => _emacs;

    /// <summary>A key in Emacs's terms (<c>C-f</c>, <c>M-f</c>…), or null for one Emacs leaves to the editor.</summary>
    private string? EmacsKey(KeyEventArgs e)
    {
        KeyModifiers m = e.KeyModifiers;
        string? letter = e.Key is >= Key.A and <= Key.Z ? e.Key.ToString().ToLowerInvariant() : null;
        if (m == KeyModifiers.Control)
        {
            return e.Key switch
            {
                Key.Space => "C-SPC",
                Key.OemQuestion => "C-/",
                _ => letter is null ? null : "C-" + letter,
            };
        }
        if (m == (KeyModifiers.Control | KeyModifiers.Shift) && e.Key == Key.OemMinus)
        {
            return "C-_";
        }
        if (m == KeyModifiers.Alt)
        {
            return e.Key == Key.Back ? "M-<backspace>" : letter is null ? null : "M-" + letter;
        }
        if (m == (KeyModifiers.Alt | KeyModifiers.Shift))
        {
            return e.Key switch { Key.OemComma => "M-<", Key.OemPeriod => "M->", _ => null };
        }
        // After C-x, a plain letter finishes the sequence (C-x u, C-x h, C-x k).
        return m == KeyModifiers.None && letter is not null && _emacs.Status.StartsWith("C-x", StringComparison.Ordinal) ? letter : null;
    }

    private bool EmacsKeyDown(KeyEventArgs e)
    {
        if (EmacsKey(e) is not string key || !_emacs.Key(key))
        {
            return false;
        }
        _swallowText = key.StartsWith("M-", StringComparison.Ordinal) || key.Length == 1; // ⌥-letters and C-x's letter also type a character
        return true;
    }

    private bool EmacsEx(string command)
    {
        switch (command)
        {
            case "w":
                Main?.SaveCommand.Execute(null);
                return true;
            case "open":
                Main?.OpenFileDialogCommand.Execute(null);
                return true;
            case "find" or "find-backward":
                _editor.SearchPanel.Open();
                return true;
            case "close":
                Main?.CloseDocumentCommand.Execute(_current);
                return true;
            default:
                return false;
        }
    }

    private void OnEmacsChanged()
    {
        if (Main is not null && EmacsOn)
        {
            Main.VimStatus = _emacs.Status;
        }
    }

    // ---- Lean's semantic tokens, inlay hints and occurrences ----

    private void OnTextChangedForLayers(object? sender, DocumentChangeEventArgs e)
    {
        _multi.DocumentChanged();
        _edits++;
        _semantic.Shift(e);
        _hints.Shift(e);
        if (_occurrences.HasAny)
        {
            _occurrences.Clear();
            _editor.TextArea.TextView.InvalidateLayer(_occurrences.Layer);
        }
    }

    /// <summary>Ask Lean for the file's semantic tokens and inlay hints, once it has checked the text.</summary>
    private void ScheduleSemantic(DocumentViewModel doc, int delayMs)
    {
        _semanticCts?.Cancel();
        if (!doc.IsLean)
        {
            return;
        }
        var cts = new CancellationTokenSource();
        _semanticCts = cts;
        _ = RefreshSemanticAsync(doc, delayMs, cts.Token);
    }

    private async Task RefreshSemanticAsync(DocumentViewModel doc, int delayMs, CancellationToken ct)
    {
        try
        {
            await Task.Delay(delayMs, ct);
            if (Main?.Server is not { State: LeanServerState.Running } server || doc.IsProcessing)
            {
                return;
            }
            int edits = _edits;
            IReadOnlyList<SemanticToken> tokens = await server.SemanticTokensAsync(doc.Uri, ct);
            IReadOnlyList<InlayHint> hints = await server.InlayHintsAsync(doc.Uri, new Lsp.Range(new Position(0, 0), new Position(doc.Document.LineCount, 0)), ct);
            if (_current != doc || edits != _edits)
            {
                return; // the text moved on; the next check brings fresh ones
            }
            _semantic.Update(doc.Document, tokens);
            _hints.Update(doc.Document, hints);
            _editor.TextArea.TextView.Redraw();
        }
        catch (Exception e) when (e is OperationCanceledException or JsonRpcException or IOException or InvalidOperationException)
        {
        }
    }

    private void ScheduleOccurrences()
    {
        _occurrenceCts?.Cancel();
        if (_current is not { IsLean: true } doc || Main?.Server is not { State: LeanServerState.Running } server)
        {
            return;
        }
        var cts = new CancellationTokenSource();
        _occurrenceCts = cts;
        TextViewPosition p = _editor.TextArea.Caret.Position;
        var pos = new Position(p.Line - 1, p.Column - 1);
        int edits = _edits;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(250, cts.Token);
                IReadOnlyList<Lsp.Range> ranges = await server.DocumentHighlightsAsync(doc.Uri, pos, cts.Token);
                Dispatcher.UIThread.Post(() =>
                {
                    if (cts.IsCancellationRequested || _current != doc || edits != _edits)
                    {
                        return;
                    }
                    _occurrences.Update(doc.Document, ranges);
                    _editor.TextArea.TextView.InvalidateLayer(_occurrences.Layer);
                });
            }
            catch (Exception e) when (e is OperationCanceledException or JsonRpcException or IOException or InvalidOperationException)
            {
            }
        }, cts.Token);
    }

    /// <summary>Whether occurrences of the symbol at the cursor are highlighted.</summary>
    public bool HasOccurrences => _occurrences.HasAny;

    /// <summary>How many tokens Lean's semantic highlighting colours.</summary>
    public int SemanticTokenCount => _semantic.Count;

    /// <summary>The inlay hints shown.</summary>
    public IReadOnlyList<string> InlayHintLabels => _hints.Labels;

    // ---- brackets and folding ----

    private void UpdateBracketMatch()
    {
        if (_current is null)
        {
            return;
        }
        string text = _current.Document.Text;
        int caret = _editor.CaretOffset;
        int at = caret < text.Length && (LeanText.IsOpener(text[caret]) || LeanText.IsCloser(text[caret])) ? caret
               : caret > 0 && (LeanText.IsOpener(text[caret - 1]) || LeanText.IsCloser(text[caret - 1])) ? caret - 1 : -1;
        int match = at < 0 || text.Length > 400_000 ? -1 : LeanText.MatchingBracket(text, at);
        _brackets.Update(match < 0 ? -1 : at, match);
        _editor.TextArea.TextView.InvalidateLayer(_brackets.Layer);
    }

    /// <summary>Ask Lean where the file's blocks are (declarations, namespaces, comments) and offer them for folding.</summary>
    private void ScheduleFolds()
    {
        _foldCts?.Cancel();
        var cts = new CancellationTokenSource();
        _foldCts = cts;
        _ = UpdateFoldsAsync(cts.Token);
    }

    private async Task UpdateFoldsAsync(CancellationToken ct)
    {
        if (_current is not { IsLean: true } doc || Main?.Server is not { State: LeanServerState.Running } server)
        {
            return;
        }
        try
        {
            await Task.Delay(400, ct);
            IReadOnlyList<FoldingRange> ranges = await server.FoldingRangesAsync(doc.Uri, ct);
            if (ct.IsCancellationRequested || _current != doc)
            {
                return;
            }
            TextDocument d = doc.Document;
            var folds = ranges
                .Where(r => r.EndLine > r.StartLine && r.EndLine < d.LineCount)
                .Select(r => new AvaloniaEdit.Folding.NewFolding(d.GetLineByNumber(r.StartLine + 1).EndOffset, d.GetLineByNumber(r.EndLine + 1).EndOffset) { Name = " … " })
                .OrderBy(f => f.StartOffset)
                .ToList();
            _folding?.UpdateFoldings(folds, -1);
        }
        catch (Exception e) when (e is OperationCanceledException or JsonRpcException or IOException)
        {
        }
    }

    /// <summary>Type an opener and get its closer too, when nothing that could go inside follows the caret.</summary>
    private void AutoClose(char opener)
    {
        if (_current is null || !LeanText.Pairs.TryGetValue(opener, out char closer))
        {
            return;
        }
        int caret = _editor.CaretOffset;
        char? next = caret < _current.Document.TextLength ? _current.Document.GetCharAt(caret) : null;
        if (LeanText.ShouldAutoClose(next))
        {
            _current.Document.Insert(caret, closer.ToString());
            _editor.CaretOffset = caret;
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
        // In Vim's normal, visual and command-line modes, typed characters are commands, not text.
        if (VimOn && _vim.Mode != VimMode.Insert && e.Text is { Length: > 0 } typed)
        {
            e.Handled = true;
            if (char.IsSurrogatePair(typed, 0))
            {
                _vim.Key(typed);
                return;
            }
            for (int k = 0; k < typed.Length; k++)
            {
                if (_vim.Mode == VimMode.Insert)
                {
                    // A command switched to insert mode partway through (pasted or composed input): the rest is text.
                    _editor.Document.Insert(_editor.CaretOffset, typed[k..]);
                    return;
                }
                _vim.Key(typed[k] == '<' ? "<lt>" : typed[k].ToString());
            }
            return;
        }
        if (_swallowText)
        {
            _swallowText = false;
            e.Handled = true;
            return;
        }
        if (e.Text is { Length: > 0 } text && _multi.TextEntering(text))
        {
            e.Handled = true;
            return;
        }
        // Typing a closer right before the same closer steps over it instead of doubling it.
        if (_abbrevStart < 0 && _current is not null && e.Text is { Length: 1 } t && LeanText.IsCloser(t[0])
            && _editor.CaretOffset < _current.Document.TextLength && _current.Document.GetCharAt(_editor.CaretOffset) == t[0])
        {
            _editor.CaretOffset++;
            e.Handled = true;
            return;
        }
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
            // Tab only completes. So does a space after a bracket: `\<` then space gives ⟨|⟩, ready to fill in.
            if (c == '\t' || (c == ' ' && replacement.Length == 1 && LeanText.IsOpener(replacement[0])))
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
        else if (e.Text.Length == 1 && LeanText.IsOpener(e.Text[0]))
        {
            AutoClose(e.Text[0]);
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
        if (symbol.Length == 1 && LeanText.IsOpener(symbol[0]))
        {
            AutoClose(symbol[0]);
        }
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
        if (_current is not { } doc)
        {
            return;
        }
        Func<string, Position, Task<IReadOnlyList<CompletionItem>>>? complete =
            doc.IsLean && Main?.Server is { State: LeanServerState.Running } server ? (u, pos) => server.CompletionAsync(u, pos)
            : doc.IsC && Main?.CServer is { IsRunning: true } clangd ? (u, pos) => clangd.CompletionAsync(u, pos)
            : null;
        if (complete is null)
        {
            return;
        }
        TextViewPosition p = _editor.TextArea.Caret.Position;
        int caret = _editor.CaretOffset;
        // Completion answers about the text the server has; give the pending edit a moment to arrive.
        await Task.Delay(200);
        IReadOnlyList<CompletionItem> items;
        try
        {
            items = await complete(doc.Uri, new Position(p.Line - 1, p.Column - 1));
        }
        catch (Exception e) when (e is JsonRpcException or IOException or InvalidOperationException)
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
        if (VimOn && VimKeyDown(e))
        {
            e.Handled = true;
            return;
        }
        if (EmacsOn && EmacsKeyDown(e))
        {
            e.Handled = true;
            return;
        }
        if (!VimOn && _multi.KeyDown(e))
        {
            e.Handled = true;
            return;
        }
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
        else if (e.Key == Key.Back && _current is not null && _editor.TextArea.Selection.IsEmpty)
        {
            int caret = _editor.CaretOffset;
            TextDocument d = _current.Document;
            if (caret > 0 && caret < d.TextLength && LeanText.Pairs.TryGetValue(d.GetCharAt(caret - 1), out char closer) && d.GetCharAt(caret) == closer)
            {
                e.Handled = true;
                d.Remove(caret - 1, 2);
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
        if (pos is TextViewPosition bp && _margin.HasBulb(bp.Line))
        {
            QuickFixAtLineRequested?.Invoke(bp.Line - 1);
            return;
        }
        if (pos is TextViewPosition p && _margin.VerdictAtLine(p.Line) is DeclarationVerdict v && Main is not null)
        {
            Main.BottomTab = 2;
            _ = Main.Navigator.ShowAsync(v.Name);
            Main.SidebarTab = MainViewModel.LibraryTab;
            if (v.Status == VerificationStatus.RestsOnAssumption && !v.Assumptions.Contains(v.Name))
            {
                _ = Main.WhyNotProvedAsync(v.Name);
            }
        }
    }

    private async void OnPointerHover(object? sender, PointerEventArgs e)
    {
        if (_current is not DocumentViewModel doc || !(doc.IsLean || doc.IsC))
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
            if (Main?.Settings.ExplainErrors != false && Core.Learn.ErrorGuide.Explain(d.Message) is string meaning)
            {
                panel.Children.Add(new TextBlock { Text = "What this means: " + meaning, TextWrapping = TextWrapping.Wrap, FontSize = _editor.FontSize - 2, Opacity = 0.85 });
            }
        }
        // A tactic or keyword under the pointer: say what it does, for people new to Lean.
        if (doc.IsLean && WordAt(doc.Document, doc.Document.GetOffset(p.Location)) is string word && Core.Learn.TacticGuide.Explain(word) is { } guide)
        {
            panel.Children.Add(new StackPanel
            {
                Spacing = 3,
                Children =
                {
                    new TextBlock { Text = $"{guide.Name}  ({guide.Kind})", FontWeight = FontWeight.SemiBold, FontSize = _editor.FontSize - 1 },
                    new TextBlock { Text = guide.Explanation, TextWrapping = TextWrapping.Wrap, FontSize = _editor.FontSize - 2 },
                    new TextBlock { Text = guide.Example, FontFamily = _editor.FontFamily, FontSize = _editor.FontSize - 2, Opacity = 0.75 },
                },
            });
        }
        // How to type the symbol under the pointer, which Lean users constantly need to know.
        int off = doc.Document.GetOffset(p.Location);
        if (doc.IsLean && off < doc.Document.TextLength)
        {
            string ch = char.IsHighSurrogate(doc.Document.GetCharAt(off)) && off + 1 < doc.Document.TextLength
                ? doc.Document.GetText(off, 2)
                : doc.Document.GetCharAt(off).ToString();
            IReadOnlyList<string> names = ch[0] > 127 ? Abbreviations.NamesFor(ch) : [];
            if (names.Count > 0)
            {
                panel.Children.Add(new TextBlock
                {
                    Text = $"Type {ch} with " + string.Join(" or ", names.Take(3).Select(n => "\\" + n)),
                    Opacity = 0.75,
                    FontSize = _editor.FontSize - 2,
                });
            }
        }
        if (doc.IsC && Main?.CServer is { IsRunning: true } clangd)
        {
            try
            {
                Hover? h = await clangd.HoverAsync(doc.Uri, lsp, cts.Token);
                if (h is not null && h.Contents.Trim().Length > 0 && !cts.IsCancellationRequested)
                {
                    foreach (Control c in RenderMarkdown(h.Contents))
                    {
                        panel.Children.Add(c);
                    }
                }
            }
            catch (Exception ex) when (ex is JsonRpcException or IOException or OperationCanceledException or InvalidOperationException)
            {
            }
        }
        else if (Main?.Server is { State: LeanServerState.Running } server)
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

    /// <summary>The identifier (tactic name, keyword) touching an offset, including a trailing ? as in exact?.</summary>
    private static string? WordAt(TextDocument doc, int offset)
    {
        if (offset < 0 || offset >= doc.TextLength)
        {
            return null;
        }
        static bool Part(char c) => char.IsLetterOrDigit(c) || c is '_' or '\'';
        int s = offset, e = offset;
        while (s > 0 && Part(doc.GetCharAt(s - 1)))
        {
            s--;
        }
        while (e < doc.TextLength && Part(doc.GetCharAt(e)))
        {
            e++;
        }
        if (e < doc.TextLength && doc.GetCharAt(e) == '?')
        {
            e++;
        }
        if (s > 0 && doc.GetCharAt(s - 1) == '.')
        {
            return null; // Nat.succ is a name, not the keyword
        }
        return e > s ? doc.GetText(s, e - s) : null;
    }

    /// <summary>
    /// Insert text at the caret (a symbol from the palette), leaving the caret after it; an opening bracket also gets
    /// its closer. Does nothing for a read-only document.
    /// </summary>
    public void InsertAtCaret(string text)
    {
        if (_current is null || _current.IsVirtual)
        {
            return;
        }
        int caret = _editor.CaretOffset;
        _current.Document.Insert(caret, text);
        _editor.CaretOffset = caret + text.Length;
        if (text.Length == 1 && LeanText.IsOpener(text[0]))
        {
            AutoClose(text[0]);
        }
        _editor.TextArea.Focus();
    }

    /// <summary>
    /// Insert a snippet at the caret, indented like the current line, with the caret at its $0. Does nothing for a
    /// read-only document.
    /// </summary>
    public void InsertSnippet(Core.Learn.Snippet snippet)
    {
        if (_current is null || _current.IsVirtual)
        {
            return;
        }
        TextDocument doc = _current.Document;
        DocumentLine line = doc.GetLineByOffset(_editor.CaretOffset);
        string lineText = doc.GetText(line);
        string indent = lineText[..(lineText.Length - lineText.TrimStart().Length)];
        (string text, int cursor) = snippet.Expand(indent);
        int at = _editor.CaretOffset;
        doc.Insert(at, text);
        _editor.CaretOffset = at + cursor;
        _editor.TextArea.Focus();
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

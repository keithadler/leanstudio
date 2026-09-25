using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using LeanStudio.App.ViewModels;
using LeanStudio.Core.Ai;

namespace LeanStudio.App.Views;

/// <summary>
/// The AI window: a conversation about the code at the cursor. The first question carries the file around the
/// cursor, the goal and Lean's messages there; answers stream in as the model writes them, and each Lean code block
/// in an answer can be copied or inserted at the cursor, where Lean checks it like anything typed.
/// </summary>
public sealed class AiChatWindow : Window
{
    private const string Mono = "JuliaMono, Cascadia Code, SF Mono, Menlo, Consolas, DejaVu Sans Mono, monospace";

    private readonly MainViewModel _vm;
    private readonly Func<Task> _chooseModel;
    private readonly List<ChatMessage> _conversation = [];
    private readonly StackPanel _messages = new() { Spacing = 12, Margin = new Thickness(16, 12) };
    private readonly ScrollViewer _scroll;
    private readonly TextBox _input;
    private readonly Button _send;
    private readonly Button _stop;
    private readonly TextBlock _model = new() { FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly CheckBox _withCode = new() { Content = "Include the code at the cursor", IsChecked = true, FontSize = 12 };
    private CancellationTokenSource? _cts;

    /// <summary>A new, empty conversation for the window of <paramref name="vm"/>.</summary>
    /// <param name="vm">The main window's view model: the model, the editor's context, and inserting code.</param>
    /// <param name="chooseModel">Opens AI ▸ Choose a Model.</param>
    public AiChatWindow(MainViewModel vm, Func<Task> chooseModel)
    {
        _vm = vm;
        _chooseModel = chooseModel;
        Title = "Ask AI — Lean Studio";
        Width = 620;
        Height = 720;
        MinWidth = 420;
        MinHeight = 360;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Bind(BackgroundProperty, this.GetResourceObservable("PanelBackground"));

        var change = new Button { Content = "Change…", Classes = { "chip" } };
        ToolTip.SetTip(change, "Choose the model: Apple's on-device model, Ollama, LM Studio, llama.cpp, MLX, or a cloud model");
        change.Click += async (_, _) =>
        {
            await _chooseModel();
            await ShowModelAsync();
        };
        var clear = new Button { Content = "New chat", Classes = { "chip" } };
        clear.Click += (_, _) => NewChat();
        var header = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"),
            Margin = new Thickness(16, 10, 16, 6),
        };
        header.Children.Add(_model);
        Grid.SetColumn(change, 1);
        header.Children.Add(change);
        clear.Margin = new Thickness(6, 0, 0, 0);
        Grid.SetColumn(clear, 2);
        header.Children.Add(clear);

        _scroll = new ScrollViewer { Content = _messages };

        _input = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 64,
            MaxHeight = 200,
            PlaceholderText = "Ask about this proof, an error, a tactic, or Mathlib… (Enter sends, Shift+Enter for a new line)",
        };
        Avalonia.Automation.AutomationProperties.SetName(_input, "Question for the AI");
        _input.AddHandler(KeyDownEvent, OnInputKey, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        _send = new Button { Content = "Send", Classes = { "accent" }, VerticalAlignment = VerticalAlignment.Bottom };
        _send.Click += (_, _) => _ = SendAsync(_input.Text);
        _stop = new Button { Content = "Stop", IsVisible = false, VerticalAlignment = VerticalAlignment.Bottom };
        _stop.Click += (_, _) => _cts?.Cancel();
        var inputRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto") };
        inputRow.Children.Add(_input);
        _send.Margin = new Thickness(8, 0, 0, 0);
        _stop.Margin = new Thickness(8, 0, 0, 0);
        Grid.SetColumn(_send, 1);
        Grid.SetColumn(_stop, 2);
        inputRow.Children.Add(_send);
        inputRow.Children.Add(_stop);
        var footer = new StackPanel { Spacing = 6, Margin = new Thickness(16, 8, 16, 14), Children = { inputRow, _withCode } };

        var top = new Border { BorderThickness = new Thickness(0, 0, 0, 1), Child = header };
        top.Bind(Border.BorderBrushProperty, top.GetResourceObservable("Divider"));
        var bottom = new Border { BorderThickness = new Thickness(0, 1, 0, 0), Child = footer };
        bottom.Bind(Border.BorderBrushProperty, bottom.GetResourceObservable("Divider"));
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(bottom, Dock.Bottom);
        Content = new DockPanel { Children = { top, bottom, _scroll } };

        Opened += async (_, _) =>
        {
            DialogHooks.Raise(this);
            _input.Focus();
            await ShowModelAsync();
        };
        Closed += (_, _) => _cts?.Cancel();
        NewChat();
    }

    /// <summary>Ask <paramref name="question"/> now, as if typed and sent.</summary>
    public Task AskAsync(string question) => SendAsync(question);

    private void OnInputKey(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && !e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            e.Handled = true;
            _ = SendAsync(_input.Text);
        }
    }

    private async Task ShowModelAsync()
    {
        _model.Text = "Looking for a model…";
        (IChatModel? m, string why) = await _vm.AiModelAsync();
        _model.Text = m is null ? "No model: " + why : "Model: " + _vm.AiModelName;
        ToolTip.SetTip(_model, m is null ? why
            : m.Location == AiLocation.Cloud ? "A cloud model: your questions, and the code they include, are sent to its provider."
            : "Runs on this computer: nothing you ask leaves it.");
    }

    private void NewChat()
    {
        _cts?.Cancel();
        _conversation.Clear();
        _messages.Children.Clear();
        _messages.Children.Add(new TextBlock
        {
            Text = "Ask about the code at the cursor: why an error happens, what a goal means, which tactic or lemma to try. "
                 + "The question comes with the code around the cursor, the goal and Lean's messages there. "
                 + "Code in an answer can be inserted at the cursor, where Lean checks it; nothing is proved until Lean says so.",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.65,
            FontSize = 12,
        });
    }

    private async Task SendAsync(string? text)
    {
        string question = (text ?? "").Trim();
        if (question.Length == 0 || _stop.IsVisible)
        {
            return;
        }
        _input.Text = "";
        var cts = new CancellationTokenSource();
        _cts = cts;
        _send.IsVisible = false;
        _stop.IsVisible = true;
        AddBubble(question, fromPerson: true);
        var answerText = new SelectableTextBlock { Text = "…", TextWrapping = TextWrapping.Wrap, FontSize = 13 };
        var answerPanel = new StackPanel { Spacing = 6, Children = { answerText } };
        AddBubble(answerPanel);
        var answer = new StringBuilder();
        try
        {
            (IChatModel? model, string why) = await _vm.AiModelAsync(cts.Token);
            if (model is null)
            {
                answerText.Text = why;
                return;
            }
            EditorContext? context = _withCode.IsChecked == true ? _vm.AiContext() : null;
            if (_conversation.Count == 0)
            {
                IReadOnlyList<ChatMessage> start = context is null
                    ? new[] { ChatMessage.System(AiAssistant.SystemPrompt), ChatMessage.User(question) }
                    : AiAssistant.Start(model, context, question);
                _conversation.AddRange(start);
            }
            else
            {
                _conversation.Add(ChatMessage.User(question));
            }
            DateTime lastPaint = DateTime.MinValue;
            await foreach (string piece in model.StreamAnswerAsync(AiAssistant.Fit(model, _conversation), new ChatOptions(Math.Clamp(model.ContextTokens / 4, 500, 2000), 0.3), cts.Token))
            {
                answer.Append(piece);
                // Repaint a few times a second, not per token.
                if (DateTime.UtcNow - lastPaint > TimeSpan.FromMilliseconds(80))
                {
                    lastPaint = DateTime.UtcNow;
                    answerText.Text = answer.ToString();
                    _scroll.ScrollToEnd();
                }
            }
            string final = AiText.WithoutThinking(answer.ToString());
            _conversation.Add(ChatMessage.Assistant(final));
            Render(answerPanel, final);
        }
        catch (AiException e)
        {
            _vm.ForgetAiModel();
            answerText.Text = (answer.Length > 0 ? answer + "\n\n" : "") + "⚠ " + e.Message;
            RemoveUnanswered();
        }
        catch (OperationCanceledException)
        {
            string partial = AiText.WithoutThinking(answer.ToString());
            if (partial.Length > 0)
            {
                _conversation.Add(ChatMessage.Assistant(partial));
                Render(answerPanel, partial + "\n\n(stopped)");
            }
            else
            {
                answerText.Text = "(stopped)";
                RemoveUnanswered();
            }
        }
        finally
        {
            if (_cts == cts)
            {
                _send.IsVisible = true;
                _stop.IsVisible = false;
            }
            Dispatcher.UIThread.Post(() => _scroll.ScrollToEnd(), DispatcherPriority.Background);
        }
    }

    /// <summary>A question that got no answer is taken back, so the conversation still alternates.</summary>
    private void RemoveUnanswered()
    {
        if (_conversation.Count > 0 && _conversation[^1].Role == ChatRole.User)
        {
            _conversation.RemoveAt(_conversation.Count - 1);
            if (_conversation.Count == 1)
            {
                _conversation.Clear();
            }
        }
    }

    private void AddBubble(string text, bool fromPerson) =>
        AddBubble(new SelectableTextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 13 }, fromPerson);

    private void AddBubble(Control content, bool fromPerson = false)
    {
        var b = new Border
        {
            Padding = new Thickness(12, 8),
            CornerRadius = new CornerRadius(8),
            Child = content,
            HorizontalAlignment = fromPerson ? HorizontalAlignment.Right : HorizontalAlignment.Stretch,
            MaxWidth = fromPerson ? 480 : double.PositiveInfinity,
        };
        if (fromPerson)
        {
            b.Bind(Border.BackgroundProperty, b.GetResourceObservable("CardBackground"));
        }
        _messages.Children.Add(b);
        _scroll.ScrollToEnd();
    }

    /// <summary>Lay out a finished answer: prose as text, each code block in a box with Copy and Insert at Cursor.</summary>
    private void Render(StackPanel panel, string markdown)
    {
        panel.Children.Clear();
        foreach ((bool isCode, string part) in Split(markdown))
        {
            if (!isCode)
            {
                if (part.Trim().Length > 0)
                {
                    panel.Children.Add(new SelectableTextBlock { Text = part.Trim('\n'), TextWrapping = TextWrapping.Wrap, FontSize = 13 });
                }
                continue;
            }
            var code = new SelectableTextBlock { Text = part, FontFamily = new FontFamily(Mono), FontSize = 12, TextWrapping = TextWrapping.Wrap };
            var copy = new Button { Content = "Copy", Classes = { "chip" } };
            copy.Click += async (_, _) =>
            {
                if (Clipboard is { } cb)
                {
                    await Avalonia.Input.Platform.ClipboardExtensions.SetValueAsync(cb, Avalonia.Input.DataFormat.Text, part);
                }
            };
            var insert = new Button { Content = "Insert at Cursor", Classes = { "chip", "accent" } };
            ToolTip.SetTip(insert, "Put this code at the cursor in the editor, as an edit you can undo. Lean checks it at once.");
            insert.Click += (_, _) =>
            {
                _vm.RequestInsert(part);
                insert.Content = "Inserted ✓";
            };
            var box = new Border
            {
                Padding = new Thickness(10, 8),
                CornerRadius = new CornerRadius(6),
                Child = new StackPanel
                {
                    Spacing = 6,
                    Children = { code, new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { insert, copy } } },
                },
            };
            box.Bind(Border.BackgroundProperty, box.GetResourceObservable("CardBackground"));
            panel.Children.Add(box);
        }
    }

    /// <summary>A Markdown answer in order: prose, and the code of each fenced block.</summary>
    public static IEnumerable<(bool IsCode, string Text)> Split(string markdown)
    {
        string[] lines = markdown.Replace("\r", "", StringComparison.Ordinal).Split('\n');
        var sb = new StringBuilder();
        bool inCode = false;
        foreach (string line in lines)
        {
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                if (sb.Length > 0 || inCode)
                {
                    yield return (inCode, inCode ? sb.ToString().TrimEnd('\n') : sb.ToString());
                }
                sb.Clear();
                inCode = !inCode;
                continue;
            }
            sb.Append(line).Append('\n');
        }
        if (sb.Length > 0)
        {
            yield return (inCode, inCode ? sb.ToString().TrimEnd('\n') : sb.ToString());
        }
    }
}

using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using LeanStudio.App.ViewModels;
using LeanStudio.Core.Agents;
using LeanStudio.Core.Projects;

namespace LeanStudio.App.Views;

/// <summary>Lets a test harness see each dialog as it opens (to capture it, and close it).</summary>
public static class DialogHooks
{
    /// <summary>Raised on the UI thread when any of the <c>Dialogs</c> windows opens, with that window.</summary>
    public static event Action<Window>? Opened;

    internal static void Raise(Window w) => Opened?.Invoke(w);
}

/// <summary>Small modal dialogs, built in code: a confirmation, a one-line prompt, and the new-project form.</summary>
internal static class Dialogs
{
    private static Window Frame(string title, Control content, double width = 420)
    {
        var w = new Window
        {
            Title = title,
            Width = width,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
            Content = new Border { Padding = new Thickness(24, 20), Child = content },
        };
        w.Bind(Window.BackgroundProperty, w.GetResourceObservable("PanelBackground"));
        w.Opened += (_, _) => DialogHooks.Raise(w);
        return w;
    }

    /// <summary>A section heading, as the panels have them.</summary>
    private static TextBlock Heading(string text) => new() { Text = text.ToUpperInvariant(), Classes = { "panelTitle" }, Margin = new Thickness(0, 14, 0, 4) };

    /// <summary>A code block on the card colour.</summary>
    private static Border CodeBlock(Control child)
    {
        var b = new Border { Padding = new Thickness(10, 8), CornerRadius = new CornerRadius(6), Child = child };
        b.Bind(Border.BackgroundProperty, b.GetResourceObservable("CardBackground"));
        return b;
    }

    private static StackPanel Buttons(params Button[] buttons)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        foreach (Button b in buttons)
        {
            p.Children.Add(b);
        }
        return p;
    }

    /// <summary>Ask a yes-or-no question; true only if OK was pressed.</summary>
    public static async Task<bool> ConfirmAsync(Window owner, string title, string message)
    {
        bool result = false;
        var ok = new Button { Content = "OK", IsDefault = true, Classes = { "accent" } };
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        var panel = new StackPanel
        {
            Children = { new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap }, Buttons(cancel, ok) },
        };
        Window w = Frame(title, panel);
        ok.Click += (_, _) => { result = true; w.Close(); };
        cancel.Click += (_, _) => w.Close();
        await w.ShowDialog(owner);
        return result;
    }

    /// <summary>Show a message (selectable, so it can be copied) with an OK button, and wait until it is closed.</summary>
    public static async Task InfoAsync(Window owner, string title, string message, double width = 460)
    {
        var ok = new Button { Content = "OK", IsDefault = true, IsCancel = true, Classes = { "accent" } };
        var panel = new StackPanel
        {
            Children = { new SelectableTextBlock { Text = message, TextWrapping = TextWrapping.Wrap, LineHeight = 20 }, Buttons(ok) },
        };
        Window w = Frame(title, panel, width);
        ok.Click += (_, _) => w.Close();
        await w.ShowDialog(owner);
    }

    /// <summary>The About box: icon, version, what Lean Studio is, and who made it, with the links live.</summary>
    public static async Task AboutAsync(Window owner, string serverCommand)
    {
        static Button Link(string text, string url, Window w)
        {
            var b = new Button
            {
                Content = text,
                Classes = { "flat" },
                Foreground = new SolidColorBrush(Color.FromRgb(0x4D, 0x9F, 0xFF)),
                Padding = new Thickness(0),
                HorizontalAlignment = HorizontalAlignment.Left,
                Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
            };
            b.Click += async (_, _) => await w.Launcher.LaunchUriAsync(new Uri(url));
            return b;
        }
        var ok = new Button { Content = "OK", IsDefault = true, IsCancel = true, Classes = { "accent" } };
        var icon = new Image
        {
            Source = new Avalonia.Media.Imaging.Bitmap(Avalonia.Platform.AssetLoader.Open(new Uri("avares://LeanStudio/Assets/leanstudio-256.png"))),
            Width = 72,
            Height = 72,
            VerticalAlignment = VerticalAlignment.Top,
        };
        var text = new StackPanel { Spacing = 4 };
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        header.Children.Add(icon);
        Grid.SetColumn(text, 1);
        text.Margin = new Thickness(16, 0, 0, 0);
        header.Children.Add(text);
        Window w = Frame("About Lean Studio", new StackPanel { Children = { header, Buttons(ok) } }, 480);
        text.Children.Add(new TextBlock { Text = "Lean Studio", FontSize = 22, FontWeight = FontWeight.SemiBold });
        text.Children.Add(new TextBlock { Text = "Version " + Services.Credits.Version, Opacity = 0.7 });
        text.Children.Add(new TextBlock { Text = "An IDE for Lean 4 on macOS, Windows and Linux.", Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap });
        text.Children.Add(new TextBlock
        {
            Text = "Lean's own language server elaborates your files. Tenet, an independent implementation of the Lean kernel, re-checks every declaration Lean builds.",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.8,
            FontSize = 12,
        });
        text.Children.Add(new TextBlock { Text = "Created by " + Services.Credits.Author, Margin = new Thickness(0, 10, 0, 0), FontWeight = FontWeight.SemiBold });
        text.Children.Add(Link(Services.Credits.XHandle + " on X", Services.Credits.XUrl, w));
        text.Children.Add(Link("github.com/keithadler/leanstudio", Services.Credits.RepositoryUrl, w));
        text.Children.Add(Link("Tenet: github.com/keithadler/tenet", Services.Credits.TenetUrl, w));
        text.Children.Add(new TextBlock { Text = Services.Credits.Copyright, Opacity = 0.6, FontSize = 11, Margin = new Thickness(0, 8, 0, 0) });
        text.Children.Add(new SelectableTextBlock { Text = "Lean server: " + serverCommand, Opacity = 0.6, FontSize = 11, TextWrapping = TextWrapping.Wrap });
        ok.Click += (_, _) => w.Close();
        await w.ShowDialog(owner);
    }

    /// <summary>
    /// How to connect Claude Code, Gemini CLI, Codex, Grok or any MCP client to Lean Studio: the snippet for each,
    /// a Copy button, and one-click setup where the assistant's configuration can be written for the person.
    /// </summary>
    public static async Task ConnectAssistantAsync(Window owner, AgentSetup setup, Action<string> log)
    {
        var close = new Button { Content = "Close", IsDefault = true, IsCancel = true };
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 8, 0, 0) };
        var list = new StackPanel { Spacing = 14 };
        Window w = Frame("Connect an AI Assistant", new StackPanel(), 720);
        list.Children.Add(new TextBlock
        {
            Text = "Lean Studio is also an MCP server. Connect an AI coding assistant and it can check your Lean files, read goals "
                 + "and proof steps, run snippets, build, verify with Tenet, and see and open files in this window.",
            TextWrapping = TextWrapping.Wrap,
        });
        foreach (AgentClient c in setup.Clients())
        {
            var code = new SelectableTextBlock
            {
                Text = c.Snippet,
                FontFamily = new FontFamily("JuliaMono, Cascadia Code, SF Mono, Menlo, Consolas, DejaVu Sans Mono, monospace"),
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
            };
            var copy = new Button { Content = "Copy", Classes = { "chip" } };
            copy.Click += async (_, _) =>
            {
                if (w.Clipboard is { } cb)
                {
                    await Avalonia.Input.Platform.ClipboardExtensions.SetValueAsync(cb, Avalonia.Input.DataFormat.Text, c.Snippet);
                    status.Text = $"Copied the {c.Name} snippet.";
                }
            };
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { copy } };
            if (c.CanInstall)
            {
                var install = new Button { Content = "Set up " + c.Name, Classes = { "chip", "accent" } };
                install.Click += async (_, _) =>
                {
                    install.IsEnabled = false;
                    try
                    {
                        string result = await setup.InstallAsync(c.Name);
                        status.Text = result;
                        log(result);
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                    {
                        status.Text = $"Could not set up {c.Name}: {e.Message}";
                    }
                    finally
                    {
                        install.IsEnabled = true;
                    }
                };
                buttons.Children.Insert(0, install);
            }
            list.Children.Add(new StackPanel
            {
                Spacing = 4,
                Children =
                {
                    new TextBlock { Text = c.Name, FontWeight = FontWeight.SemiBold },
                    new TextBlock { Text = c.Description, TextWrapping = TextWrapping.Wrap, Opacity = 0.75, FontSize = 12 },
                    CodeBlock(code),
                    buttons,
                },
            });
        }
        list.Children.Add(new TextBlock
        {
            Text = "Then start the assistant in your Lean project and ask it to prove, fix or explain something. It works on the files "
                 + "on disk; open files here reload when it changes them, and it can open files in this window to show you its work.",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.75,
            FontSize = 12,
        });
        list.Children.Add(status);
        w.Content = new Border
        {
            Padding = new Thickness(24, 20),
            Child = new DockPanel
            {
                Children =
                {
                    Dock(Buttons(close), Avalonia.Controls.Dock.Bottom),
                    new ScrollViewer { Content = list, MaxHeight = 620 },
                },
            },
        };
        w.SizeToContent = SizeToContent.Height;
        close.Click += (_, _) => w.Close();
        await w.ShowDialog(owner);
    }

    private static Control Dock(Control c, Avalonia.Controls.Dock d)
    {
        DockPanel.SetDock(c, d);
        return c;
    }

    /// <summary>Every setting in one place, grouped, applied when OK is pressed.</summary>
    public static async Task PreferencesAsync(Window owner, Services.Settings s)
    {
        CheckBox Check(string text, bool value, string? tip = null)
        {
            var c = new CheckBox { Content = text, IsChecked = value };
            if (tip is not null)
            {
                ToolTip.SetTip(c, tip);
            }
            return c;
        }
        TextBlock Head(string text) => Heading(text);

        var theme = new ComboBox { ItemsSource = new[] { "Dark", "Light" }, SelectedItem = s.Theme == "Light" ? "Light" : "Dark", MinWidth = 140 };
        var font = new TextBox { Text = s.EditorFontFamily };
        var size = new NumericUpDown { Value = (decimal)s.EditorFontSize, Minimum = 8, Maximum = 32, Increment = 1, MinWidth = 140, FormatString = "0" };
        var lineNumbers = Check("Line numbers", s.ShowLineNumbers);
        var wrap = Check("Word wrap", s.WordWrap);
        var unicode = Check("Unicode input (\\alpha becomes α)", s.UnicodeInput);
        var autosave = Check("Auto save", s.AutoSave, "Save a moment after typing stops, and when the window loses focus");
        var inline = Check("Show #eval and #check results at the end of the line", s.InlineResults);
        var semantic = Check("Colour variables and fields as Lean sees them (semantic highlighting)", s.SemanticHighlighting);
        var hints = Check("Show inlay hints (what Lean fills in, such as implicit arguments)", s.InlayHints);
        var vim = Check("Vim mode (normal, insert and visual modes; motions, operators, :w, :q)", s.VimMode);
        var english = Check("Read goals aloud in English", s.ShowGoalsInEnglish);
        var explain = Check("Explain Lean's messages in plain words", s.ExplainErrors);
        var autofix = Check("Apply a lone Try this suggestion automatically", s.AutoApplyFixes);
        var verify = Check("Verify with Tenet after every build", s.VerifyAfterBuild);
        var blame = Check("Show who last changed the current line", s.ShowBlame);
        var updates = Check("Check for updates once a day", s.CheckForUpdates);
        var installed = (await Core.Toolchains.Elan.ListAsync()).Select(t => t.Name).ToList();
        var toolchains = new List<string> { "(the newest installed)" };
        toolchains.AddRange(installed);
        var fallback = new ComboBox
        {
            ItemsSource = toolchains,
            SelectedItem = s.FallbackToolchain is string f && installed.Contains(f) ? f : toolchains[0],
            MinWidth = 280,
        };
        ToolTip.SetTip(fallback, "The Lean version for files outside any project (a project's lean-toolchain file always wins)");

        Grid Row(string label, Control c)
        {
            var g = new Grid { ColumnDefinitions = new ColumnDefinitions("140,*"), Margin = new Thickness(0, 2) };
            g.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
            Grid.SetColumn(c, 1);
            g.Children.Add(c);
            return g;
        }

        var ok = new Button { Content = "OK", IsDefault = true, Classes = { "accent" } };
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        var panel = new StackPanel
        {
            Spacing = 2,
            Children =
            {
                Head("Appearance"), Row("Theme", theme), Row("Editor font", font), Row("Font size", size), lineNumbers, wrap, semantic, hints,
                Head("Editing"), unicode, autosave, inline, vim,
                Head("Lean"), english, explain, autofix, verify, Row("Loose files use", fallback),
                Head("Other"), blame, updates,
                new TextBlock { Text = "Settings are kept in " + Services.Settings.FilePath, FontSize = 11, Opacity = 0.6, Margin = new Thickness(0, 12, 0, 0), TextWrapping = TextWrapping.Wrap },
            },
        };
        // The buttons stay in view below the list, however long it gets.
        Window w = Frame("Preferences", new DockPanel
        {
            Children = { Dock(Buttons(cancel, ok), Avalonia.Controls.Dock.Bottom), new ScrollViewer { Content = panel, MaxHeight = 600 } },
        }, 540);
        ok.Click += (_, _) =>
        {
            s.Theme = theme.SelectedItem as string ?? "Dark";
            s.EditorFontFamily = string.IsNullOrWhiteSpace(font.Text) ? s.EditorFontFamily : font.Text.Trim();
            s.EditorFontSize = (double)(size.Value ?? 14);
            s.ShowLineNumbers = lineNumbers.IsChecked == true;
            s.WordWrap = wrap.IsChecked == true;
            s.UnicodeInput = unicode.IsChecked == true;
            s.AutoSave = autosave.IsChecked == true;
            s.InlineResults = inline.IsChecked == true;
            s.SemanticHighlighting = semantic.IsChecked == true;
            s.InlayHints = hints.IsChecked == true;
            s.VimMode = vim.IsChecked == true;
            s.ShowGoalsInEnglish = english.IsChecked == true;
            s.ExplainErrors = explain.IsChecked == true;
            s.AutoApplyFixes = autofix.IsChecked == true;
            s.VerifyAfterBuild = verify.IsChecked == true;
            s.ShowBlame = blame.IsChecked == true;
            s.CheckForUpdates = updates.IsChecked == true;
            s.FallbackToolchain = fallback.SelectedIndex > 0 ? fallback.SelectedItem as string : null;
            s.Save();
            w.Close();
        };
        cancel.Click += (_, _) => w.Close();
        await w.ShowDialog(owner);
    }

    /// <summary>Ask for one line of text, starting from <paramref name="initial"/>; null if cancelled.</summary>
    public static async Task<string?> PromptAsync(Window owner, string title, string message, string initial)
    {
        string? result = null;
        var box = new TextBox { Text = initial };
        var ok = new Button { Content = "OK", IsDefault = true, Classes = { "accent" } };
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        var panel = new StackPanel { Spacing = 10, Children = { new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, LineHeight = 19 }, box, Buttons(cancel, ok) } };
        Window w = Frame(title, panel, 460);
        ok.Click += (_, _) => { result = box.Text; w.Close(); };
        cancel.Click += (_, _) => w.Close();
        w.Opened += (_, _) => { box.Focus(); box.SelectAll(); };
        await w.ShowDialog(owner);
        return result;
    }

    /// <summary>
    /// The New Project form: name, location, template and toolchain, checked before it closes. Returns what to create,
    /// or null if cancelled; nothing is created here.
    /// </summary>
    public static async Task<NewProjectRequest?> NewProjectAsync(Window owner, IReadOnlyList<string> toolchains, string defaultParent, Func<Task<string?>> pickFolder)
    {
        NewProjectRequest? result = null;
        var name = new TextBox { PlaceholderText = "MyProject" };
        var parent = new TextBox { Text = defaultParent };
        var browse = new Button { Content = "…" };
        var template = new ComboBox
        {
            ItemsSource = new[]
            {
                "Library (lib)",
                "Library + executable (std)",
                "Library using Mathlib (math)",
                "Executable (exe)",
            },
            SelectedIndex = 0,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        var choices = toolchains.OrderDescending(StringComparer.Ordinal).ToList();
        if (!choices.Contains("leanprover/lean4:stable"))
        {
            choices.Add("leanprover/lean4:stable");
        }
        var toolchain = new ComboBox { ItemsSource = choices, SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch };
        var error = new TextBlock { Foreground = Brushes.IndianRed, TextWrapping = TextWrapping.Wrap };
        var note = new TextBlock
        {
            Text = "A Mathlib project downloads Mathlib and its prebuilt cache (several GB) and pins the toolchain Mathlib uses.",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.7,
            FontSize = 12,
            IsVisible = false,
        };
        template.SelectionChanged += (_, _) => note.IsVisible = template.SelectedIndex == 2;
        var ok = new Button { Content = "Create", IsDefault = true, Classes = { "accent" } };
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        var parentRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        parentRow.Children.Add(parent);
        Grid.SetColumn(browse, 1);
        browse.Margin = new Thickness(6, 0, 0, 0);
        parentRow.Children.Add(browse);
        var panel = new StackPanel
        {
            Spacing = 6,
            Children =
            {
                new TextBlock { Text = "Name" }, name,
                new TextBlock { Text = "Location", Margin = new Thickness(0, 6, 0, 0) }, parentRow,
                new TextBlock { Text = "Template", Margin = new Thickness(0, 6, 0, 0) }, template,
                new TextBlock { Text = "Lean toolchain", Margin = new Thickness(0, 6, 0, 0) }, toolchain,
                note, error,
                Buttons(cancel, ok),
            },
        };
        Window w = Frame("New Lean Project", panel, 480);
        browse.Click += async (_, _) =>
        {
            string? f = await pickFolder();
            if (f is not null)
            {
                parent.Text = f;
            }
        };
        ok.Click += (_, _) =>
        {
            string n = (name.Text ?? "").Trim();
            string p = (parent.Text ?? "").Trim();
            if (!Lake.IsValidName(n))
            {
                error.Text = "The name must start with a letter and contain only letters, digits, - and _.";
                return;
            }
            if (!Directory.Exists(p))
            {
                error.Text = "That location does not exist.";
                return;
            }
            if (Directory.Exists(Path.Combine(p, n)))
            {
                error.Text = $"{Path.Combine(p, n)} already exists.";
                return;
            }
            ProjectTemplate t = template.SelectedIndex switch
            {
                1 => ProjectTemplate.Standard,
                2 => ProjectTemplate.Math,
                3 => ProjectTemplate.Executable,
                _ => ProjectTemplate.Library,
            };
            result = new NewProjectRequest(p, n, t, toolchain.SelectedItem as string ?? "leanprover/lean4:stable");
            w.Close();
        };
        cancel.Click += (_, _) => w.Close();
        w.Opened += (_, _) => name.Focus();
        await w.ShowDialog(owner);
        return result;
    }
}

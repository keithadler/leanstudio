using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;
using LeanStudio.App.ViewModels;
using LeanStudio.Core.Proofs;

namespace LeanStudio.App.Views;

/// <summary>
/// The proof-state map in a window of its own. On the left, every state the proofs pass through, in 3D: a point per
/// state, a line per tactic step, and where proofs of different declarations reach the same state they share a
/// point. On the right, the states worth acting on, the ones two or more proofs reach, each with the proofs to jump
/// to and a way to extract it as a lemma. The two are linked: picking in one selects in the other.
/// </summary>
public sealed class ProofStatesWindow : Window
{
    private static readonly string[] LevelKeys = ["exact", "goal", "shape"];

    private static readonly Dictionary<StateMatch, (string Name, string Note)> Levels = new()
    {
        [StateMatch.Exact] = ("Exact", "Same hypotheses and same goals, up to renaming local names: the proofs are in the same place."),
        [StateMatch.Goal] = ("Same goal", "Same goals, whatever the hypotheses: proofs working toward the same statement from different contexts."),
        [StateMatch.Shape] = ("Same shape", "Same goals with local names and numbers blanked out: n + 2 ≤ n * 5 and k + 3 ≤ k * 7 agree."),
    };

    private readonly ProofStatesResult _result;
    private readonly Dictionary<StateMatch, ProofStateMap> _maps;
    private readonly Action<StateVisit> _open;
    private readonly Func<StateNode, Task> _extract;
    private readonly StackPanel _list = new() { Spacing = 8 };
    private readonly TextBlock _note = Dim("");
    private readonly StackPanel _levelButtons = new() { Orientation = Orientation.Horizontal, Spacing = 6 };
    private readonly Dictionary<int, Border> _items = [];
    private readonly ScrollViewer _scroll;
    private NativeWebView? _web;
    private StateMatch _level = StateMatch.Exact;
    private int? _selected;

    /// <summary>Build the window for collected proof states.</summary>
    /// <param name="result">The steps and where they came from.</param>
    /// <param name="light">Whether the app is in its light theme.</param>
    /// <param name="open">Called to open a place a proof passes through a state, in the editor.</param>
    /// <param name="extract">Called to extract a shared state as a lemma.</param>
    /// <param name="openInBrowser">Called with the page when there is no web view here, to show it in the browser.</param>
    public ProofStatesWindow(ProofStatesResult result, bool light, Action<StateVisit> open, Func<StateNode, Task> extract, Func<string, Task> openInBrowser)
    {
        _result = result;
        _open = open;
        _extract = extract;
        _maps = Enum.GetValues<StateMatch>().ToDictionary(m => m, m => ProofStateMap.Build(result.Steps, m));
        Title = $"Proof-State Map — {result.Scope}";
        Width = 1240;
        Height = 780;
        MinWidth = 680;
        MinHeight = 440;
        Bind(BackgroundProperty, this.GetResourceObservable("PanelBackground"));
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        string page = Page(result.Steps, embedded: true, light);
        Control left;
        if (InfoviewPane.NativeWebViewAllowed && InfoviewPane.Unavailable() is null)
        {
            _web = new NativeWebView();
            _web.Bind(NativeWebView.BackgroundProperty, this.GetResourceObservable("PanelBackground"));
            _web.EnvironmentRequested += (_, e) => e.EnableDevTools = false;
            _web.NavigationStarted += (_, e) =>
            {
                // The page has no links of its own; nothing it loads may take the view anywhere else.
                if (e.Request is Uri to && to.Scheme is "http" or "https")
                {
                    e.Cancel = true;
                }
            };
            _web.WebMessageReceived += (_, e) => OnPageMessage(e.Body);
            _web.AdapterCreated += (_, _) => _web.NavigateToString(page, new Uri("about:blank"));
            left = _web;
        }
        else
        {
            left = Fallback(InfoviewPane.Unavailable() ?? "There is no web view here.", () => openInBrowser(Page(result.Steps, embedded: false, light)));
        }

        var side = new StackPanel { Spacing = 8, Margin = new Thickness(16, 14) };
        side.Children.Add(new TextBlock { Text = "Proof-state map", FontSize = 18, FontWeight = FontWeight.SemiBold });
        int proofs = result.Steps.Select(s => s.File + " · " + s.Declaration).Distinct().Count();
        side.Children.Add(Dim($"{proofs} tactic proofs and {result.Steps.Count} steps in {(result.Files == 1 ? result.Scope : $"{result.Files} files of {result.Scope}")}, as Lean reports them. "
                              + "Each point is a proof state, each line a tactic. Where different proofs reach the same state, they share a point."));
        side.Children.Add(new TextBlock { Text = "WHAT COUNTS AS THE SAME STATE", Classes = { "panelTitle" }, Margin = new Thickness(0, 8, 0, 0) });
        side.Children.Add(_levelButtons);
        side.Children.Add(_note);
        side.Children.Add(new TextBlock { Text = "REACHED BY MORE THAN ONE PROOF", Classes = { "panelTitle" }, Margin = new Thickness(0, 10, 0, 0) });
        side.Children.Add(Dim("A state several proofs pass through is a lemma nobody has written yet. Trivial goals (False, a = a) are left out."));
        side.Children.Add(_list);
        side.Children.Add(Dim("Drag to turn, scroll to zoom, click a point to select it. Esc clears.").WithMargin(new Thickness(0, 10, 0, 0)));
        _scroll = new ScrollViewer { Content = side };

        var divider = new Border { Width = 1 };
        divider.Bind(Border.BackgroundProperty, divider.GetResourceObservable("Divider"));
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,360") };
        grid.Children.Add(left);
        Grid.SetColumn(divider, 1);
        grid.Children.Add(divider);
        Grid.SetColumn(_scroll, 2);
        grid.Children.Add(_scroll);
        Content = grid;
        ShowLevel(StateMatch.Exact);
    }

    /// <summary>The web view on the left, when this system has one (for checks that read the page).</summary>
    public NativeWebView? WebView => _web;

    /// <summary>The state selected in the list and the 3D view, if any.</summary>
    public int? Selected => _selected;

    /// <summary>The level shown now.</summary>
    public StateMatch Level => _level;

    /// <summary>The shared states listed now, most shared first.</summary>
    public IReadOnlyList<StateNode> Listed => _maps[_level].Shared;

    /// <summary>
    /// The 3D page for a set of steps: the maps of all three levels inlined as data, so the page needs nothing from
    /// outside. <paramref name="embedded"/> hides the page's own level buttons, which this window replaces.
    /// </summary>
    public static string Page(IReadOnlyList<StateStep> steps, bool embedded, bool light)
    {
        using Stream s = AssetLoader.Open(new Uri("avares://LeanStudio/Assets/proof-states.html"));
        using var reader = new StreamReader(s);
        string html = reader.ReadToEnd();
        // "</" inside a string would end the script element early: every goal is text from the user's file.
        string data = ProofStates.ViewJson(steps).Replace("</", "<\\/", StringComparison.Ordinal);
        return html.Replace("/*DATA*/null", data, StringComparison.Ordinal)
                   .Replace("/*EMBEDDED*/false", embedded ? "true" : "false", StringComparison.Ordinal)
                   .Replace("<html lang=\"en\">", light ? "<html lang=\"en\" data-theme=\"light\">" : "<html lang=\"en\">", StringComparison.Ordinal);
    }

    /// <summary>Follow the app's theme in the 3D view (the list follows it by itself).</summary>
    public void SetTheme(bool light) => Script($"setTheme('{(light ? "light" : "dark")}')");

    /// <summary>Show one level: its note, its list, and the same level in the 3D view.</summary>
    public void ShowLevel(StateMatch level)
    {
        _level = level;
        _selected = null;
        _levelButtons.Children.Clear();
        foreach (StateMatch m in Enum.GetValues<StateMatch>())
        {
            var b = new Button
            {
                Content = new StackPanel
                {
                    Children =
                    {
                        new TextBlock { Text = Levels[m].Name, FontWeight = FontWeight.SemiBold, FontSize = 12 },
                        new TextBlock { Text = $"{_maps[m].Shared.Count} shared", Classes = { "dim" }, FontSize = 11 },
                    },
                },
            };
            if (m == level)
            {
                b.Classes.Add("accent");
            }
            StateMatch target = m;
            b.Click += (_, _) => ShowLevel(target);
            _levelButtons.Children.Add(b);
        }
        _note.Text = Levels[level].Note;
        Script($"setLevel('{LevelKeys[(int)level]}')");
        BuildList();
    }

    private void BuildList()
    {
        _list.Children.Clear();
        _items.Clear();
        IReadOnlyList<StateNode> shared = _maps[_level].Shared;
        if (shared.Count == 0)
        {
            _list.Children.Add(Dim(_level == StateMatch.Shape
                ? "No two proofs pass through the same state, even with names and numbers ignored."
                : "No two proofs pass through the same state at this level. Try a looser one."));
            return;
        }
        foreach (StateNode n in shared.Take(80))
        {
            var body = new StackPanel { Spacing = 4 };
            body.Children.Add(new TextBlock { Text = $"{n.Declarations.Count} proofs reach this state", FontWeight = FontWeight.SemiBold, FontSize = 12 });
            body.Children.Add(new TextBlock
            {
                Text = Goals(n.Example),
                Classes = { "mono" },
                FontSize = 11.5,
                TextWrapping = TextWrapping.Wrap,
                MaxLines = 4,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            var places = new WrapPanel();
            foreach (StateVisit v in n.Visits.GroupBy(v => v.File + " · " + v.Declaration).Select(g => g.First()).Take(10))
            {
                var go = new Button { Content = new TextBlock { Text = v.Declaration, FontSize = 11, Classes = { "mono" } }, Classes = { "flat" }, Padding = new Thickness(4, 1), Margin = new Thickness(0, 0, 4, 2) };
                ToolTip.SetTip(go, $"{v.File}, line {v.Line + 1}: {v.Tactic}");
                StateVisit visit = v;
                go.Click += (_, _) => _open(visit);
                places.Children.Add(go);
            }
            body.Children.Add(places);
            var extract = new Button { Content = "Extract as Lemma…", FontSize = 12, HorizontalAlignment = HorizontalAlignment.Left };
            ToolTip.SetTip(extract, "Lean writes the lemma for this state above the first proof that reaches it, with a sorry to prove once.");
            StateNode node = n;
            extract.Click += async (_, _) => await _extract(node);
            body.Children.Add(extract);

            var item = new Border { Padding = new Thickness(10, 8), CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(1), Child = body, Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand) };
            item.Bind(Border.BorderBrushProperty, item.GetResourceObservable("Divider"));
            item.PointerPressed += (_, e) =>
            {
                if (e.Source is not Button && e.Source is not TextBlock { Parent: Button })
                {
                    Select(node.Id, fromPage: false);
                }
            };
            _items[n.Id] = item;
            _list.Children.Add(item);
        }
        if (shared.Count > 80)
        {
            _list.Children.Add(Dim($"…and {shared.Count - 80} more."));
        }
    }

    /// <summary>Select a state in the list, and in the 3D view unless that is where the click came from.</summary>
    public void Select(int id, bool fromPage)
    {
        if (_selected is int old && _items.TryGetValue(old, out Border? was))
        {
            was.Bind(Border.BorderBrushProperty, was.GetResourceObservable("Divider"));
            was.BorderThickness = new Thickness(1);
        }
        _selected = id;
        if (_items.TryGetValue(id, out Border? item))
        {
            item.BorderBrush = Brush.Parse("#E3A82B");
            item.BorderThickness = new Thickness(2);
            item.BringIntoView();
        }
        if (!fromPage)
        {
            Script($"selectState({id})");
        }
    }

    private void OnPageMessage(string? body)
    {
        if (string.IsNullOrEmpty(body))
        {
            return;
        }
        try
        {
            using JsonDocument outer = JsonDocument.Parse(body);
            // The bridge may hand the page's JSON over as a JSON string of its own: unwrap it.
            string json = outer.RootElement.ValueKind == JsonValueKind.String ? outer.RootElement.GetString() ?? "" : body;
            using JsonDocument doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return;
            }
            string? type = doc.RootElement.TryGetProperty("type", out JsonElement t) ? t.GetString() : null;
            if (type == "select" && doc.RootElement.TryGetProperty("id", out JsonElement id) && id.TryGetInt32(out int n))
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() => Select(n, fromPage: true));
            }
            else if (type == "ready")
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() => Script($"setLevel('{LevelKeys[(int)_level]}')"));
            }
        }
        catch (JsonException)
        {
            // not a message from the page
        }
    }

    private void Script(string js)
    {
        if (_web is not null)
        {
            _ = _web.InvokeScript(js);
        }
    }

    /// <summary>The goal lines of a state (hypotheses left out), for the list.</summary>
    private static string Goals(string example) =>
        string.Join("\n", example.Split('\n').Where(l => l.StartsWith("⊢", StringComparison.Ordinal)));

    private static TextBlock Dim(string text) => new() { Text = text, Classes = { "dim" }, FontSize = 12, TextWrapping = TextWrapping.Wrap };

    private static Control Fallback(string reason, Func<Task> openInBrowser)
    {
        var open = new Button { Content = "Open in Browser", HorizontalAlignment = HorizontalAlignment.Center };
        open.Classes.Add("accent");
        open.Click += async (_, _) => await openInBrowser();
        var panel = new StackPanel { Spacing = 10, Margin = new Thickness(24), VerticalAlignment = VerticalAlignment.Center, MaxWidth = 360 };
        panel.Children.Add(new TextBlock { Text = "The 3D view", FontWeight = FontWeight.SemiBold, FontSize = 15, HorizontalAlignment = HorizontalAlignment.Center });
        panel.Children.Add(new TextBlock
        {
            Text = "It can't be shown in this window here. " + reason + " The list on the right works without it.",
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            Opacity = 0.75,
        });
        panel.Children.Add(open);
        return panel;
    }
}

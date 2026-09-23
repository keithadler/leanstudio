using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using LeanStudio.Core.Verification;

namespace LeanStudio.App.Views;

/// <summary>
/// The project map in a window of its own: the graph on the left; on the right, what it means and the sorries
/// and axioms worth fixing first, the ones the most declarations rest on.
/// </summary>
public sealed class ProjectMapWindow : Window
{
    public ProjectMapWindow(ProjectMap map, string projectName, Action<MapNode> open)
    {
        Title = $"Project Map — {projectName}";
        Width = 1200;
        Height = 760;
        MinWidth = 640;
        MinHeight = 420;
        Bind(BackgroundProperty, this.GetResourceObservable("PanelBackground"));
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        View = new ProjectMapView { Map = map };
        View.NodeClicked += open;

        int proved = map.Nodes.Count(n => n.Status == MapStatus.Proved);
        int onSorry = map.Nodes.Count(n => n.Status == MapStatus.RestsOnSorry);
        int onAxiom = map.Nodes.Count(n => n.Status is MapStatus.RestsOnAxiom);
        var side = new StackPanel { Spacing = 6, Margin = new Thickness(16, 14) };
        side.Children.Add(new TextBlock { Text = "Project map", FontSize = 18, FontWeight = FontWeight.SemiBold });
        side.Children.Add(Dim($"{map.Nodes.Count} declarations and {map.Edges.Count} uses between them, from the last build. "
                              + "Each column builds on the ones to its left."));
        side.Children.Add(Legend("#3FB950", $"fully proved  ({proved})"));
        side.Children.Add(Legend("#E59E2C", $"rests on sorry  ({onSorry})"));
        side.Children.Add(Legend("#B48EAD", $"an axiom of the project, or rests on one  ({onAxiom + map.Nodes.Count(n => n.Status == MapStatus.Axiom)})"));
        side.Children.Add(Dim("A dot marks where the sorry or axiom itself is."));

        var blockers = map.Blockers.Where(n => n.Status != MapStatus.Proved).Take(12).ToList();
        side.Children.Add(new TextBlock { Text = "FIX THESE FIRST", Classes = { "panelTitle" }, Margin = new Thickness(0, 14, 0, 0) });
        if (blockers.Count == 0)
        {
            side.Children.Add(Dim("Nothing: every declaration is fully proved. ✓"));
        }
        foreach (MapNode n in blockers)
        {
            var button = new Button
            {
                Classes = { "flat" },
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Content = new StackPanel
                {
                    Children =
                    {
                        new TextBlock { Text = n.Name, Classes = { "mono" }, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis },
                        new TextBlock
                        {
                            Text = (n.Kind == "axiom" ? "axiom" : "uses sorry") + (n.UsedBy > 0 ? $" · {n.UsedBy} rest on it" : " · nothing else rests on it"),
                            Classes = { "dim" }, FontSize = 11,
                        },
                    },
                },
            };
            ToolTip.SetTip(button, "Show it on the map and open it in the editor");
            button.Click += (_, _) =>
            {
                View.Select(n.Name);
                open(n);
            };
            side.Children.Add(button);
        }
        side.Children.Add(Dim("Scroll to zoom, drag to move, hover for details, click to open.").WithMargin(new Thickness(0, 14, 0, 0)));

        var divider = new Border { Width = 1 };
        divider.Bind(Border.BackgroundProperty, divider.GetResourceObservable("Divider"));
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,300") };
        grid.Children.Add(View);
        Grid.SetColumn(divider, 1);
        grid.Children.Add(divider);
        var scroll = new ScrollViewer { Content = side };
        Grid.SetColumn(scroll, 2);
        grid.Children.Add(scroll);
        Content = grid;
    }

    public ProjectMapView View { get; }

    private static TextBlock Dim(string text) => new() { Text = text, Classes = { "dim" }, FontSize = 12, TextWrapping = TextWrapping.Wrap };

    private static Control Legend(string color, string text) => new StackPanel
    {
        Orientation = Orientation.Horizontal,
        Spacing = 8,
        Children =
        {
            new Border { Width = 14, Height = 10, CornerRadius = new CornerRadius(3), BorderThickness = new Thickness(1.5), BorderBrush = Brush.Parse(color),
                         Background = new SolidColorBrush(Color.Parse(color), 0.3), VerticalAlignment = VerticalAlignment.Center },
            new TextBlock { Text = text, FontSize = 12 },
        },
    };
}

internal static class ControlExtensions
{
    public static T WithMargin<T>(this T c, Thickness m) where T : Control
    {
        c.Margin = m;
        return c;
    }
}

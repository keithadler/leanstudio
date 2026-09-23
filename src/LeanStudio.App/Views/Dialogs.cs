using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using LeanStudio.App.ViewModels;
using LeanStudio.Core.Projects;

namespace LeanStudio.App.Views;

/// <summary>Small modal dialogs, built in code: a confirmation, a one-line prompt, and the new-project form.</summary>
internal static class Dialogs
{
    private static Window Frame(string title, Control content, double width = 420) => new()
    {
        Title = title,
        Width = width,
        SizeToContent = SizeToContent.Height,
        CanResize = false,
        WindowStartupLocation = WindowStartupLocation.CenterOwner,
        ShowInTaskbar = false,
        Content = new Border { Padding = new Thickness(20, 16), Child = content },
    };

    private static StackPanel Buttons(params Button[] buttons)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        foreach (Button b in buttons)
        {
            p.Children.Add(b);
        }
        return p;
    }

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

    public static async Task InfoAsync(Window owner, string title, string message, double width = 460)
    {
        var ok = new Button { Content = "OK", IsDefault = true, IsCancel = true };
        var panel = new StackPanel
        {
            Children = { new SelectableTextBlock { Text = message, TextWrapping = TextWrapping.Wrap }, Buttons(ok) },
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
        var ok = new Button { Content = "OK", IsDefault = true, IsCancel = true };
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

    public static async Task<string?> PromptAsync(Window owner, string title, string message, string initial)
    {
        string? result = null;
        var box = new TextBox { Text = initial };
        var ok = new Button { Content = "OK", IsDefault = true, Classes = { "accent" } };
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        var panel = new StackPanel { Spacing = 8, Children = { new TextBlock { Text = message }, box, Buttons(cancel, ok) } };
        Window w = Frame(title, panel, 360);
        ok.Click += (_, _) => { result = box.Text; w.Close(); };
        cancel.Click += (_, _) => w.Close();
        w.Opened += (_, _) => { box.Focus(); box.SelectAll(); };
        await w.ShowDialog(owner);
        return result;
    }

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

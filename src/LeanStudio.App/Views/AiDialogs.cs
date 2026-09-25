using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using LeanStudio.App.ViewModels;
using LeanStudio.Core.Ai;

namespace LeanStudio.App.Views;

/// <summary>AI ▸ Choose a Model: which model the AI features use, local first, and keys for cloud ones.</summary>
internal static class AiDialogs
{
    private sealed record Row(string Id, RadioButton Radio, ComboBox? Models);

    /// <summary>
    /// Show what this computer can use (Apple's on-device model, Ollama, LM Studio, llama.cpp or MLX, a custom server,
    /// Claude), let the person pick one and a model, and keep the choice. Keys go to the system's keychain, never to
    /// settings.json.
    /// </summary>
    public static async Task ChooseModelAsync(Window owner, MainViewModel vm)
    {
        Services.Settings s = vm.Settings;
        var w = new Window
        {
            Title = "Choose an AI Model",
            Width = 620,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
        };
        w.Bind(Window.BackgroundProperty, w.GetResourceObservable("PanelBackground"));

        var status = new TextBlock { Text = "Looking for models on this computer…", TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = 0.75 };
        var rows = new StackPanel { Spacing = 6 };
        var list = new List<Row>();
        var customUrl = new TextBox { Text = s.AiCustomUrl, PlaceholderText = "http://127.0.0.1:8000/v1, or https://api.openai.com/v1", MinWidth = 320 };
        var customKey = new TextBox { PasswordChar = '•', PlaceholderText = "API key, if it needs one", MinWidth = 320 };
        var anthropicKey = new TextBox { PasswordChar = '•', PlaceholderText = "sk-ant-…", MinWidth = 320 };
        Avalonia.Automation.AutomationProperties.SetName(customUrl, "Custom server address");
        Avalonia.Automation.AutomationProperties.SetName(customKey, "Custom server API key");
        Avalonia.Automation.AutomationProperties.SetName(anthropicKey, "Anthropic API key");
        var allowCloud = new CheckBox
        {
            Content = "When no local model is running, the automatic choice may use a cloud model (your code is sent to it)",
            IsChecked = s.AiAllowCloud,
        };
        var askWhenStuck = new CheckBox
        {
            Content = "When Prove It's tactics don't close a goal, ask the AI too (Lean checks every suggestion)",
            IsChecked = s.AiAskWhenStuck,
        };
        var refresh = new Button { Content = "Look Again", Classes = { "chip" } };
        var ok = new Button { Content = "Save", IsDefault = true, Classes = { "accent" } };
        var cancel = new Button { Content = "Cancel", IsCancel = true };

        async Task FillAsync()
        {
            status.Text = "Looking for models on this computer…";
            refresh.IsEnabled = false;
            rows.Children.Clear();
            list.Clear();
            IReadOnlyList<AiCandidate> found = await vm.AiDiscovery.FindAsync(s.ToAiConfig() with { CustomUrl = customUrl.Text?.Trim() ?? "" });
            var auto = new RadioButton
            {
                GroupName = "ai",
                Content = new TextBlock { Text = "Automatic: the best model running on this computer", FontWeight = FontWeight.SemiBold },
                IsChecked = s.AiProvider == AiProviders.Auto,
            };
            rows.Children.Add(auto);
            list.Add(new Row(AiProviders.Auto, auto, null));
            foreach (AiCandidate c in found)
            {
                if (c.Id == AiProviders.Apple && !OperatingSystem.IsMacOS())
                {
                    continue;
                }
                bool configurable = c.Id is AiProviders.Custom or AiProviders.Anthropic;
                var radio = new RadioButton
                {
                    GroupName = "ai",
                    IsEnabled = c.Available || configurable,
                    IsChecked = s.AiProvider == c.Id,
                    Content = new StackPanel
                    {
                        Children =
                        {
                            new TextBlock { Text = c.Name + (c.Location == AiLocation.Cloud ? "  ☁" : c.Location == AiLocation.OnDevice ? "  ●" : ""), FontWeight = FontWeight.SemiBold },
                            new TextBlock { Text = c.Detail, FontSize = 11, Opacity = 0.7, TextWrapping = TextWrapping.Wrap },
                        },
                    },
                };
                ComboBox? models = null;
                if (c.Models.Count > 1)
                {
                    models = new ComboBox
                    {
                        ItemsSource = c.Models,
                        SelectedItem = s.AiProvider == c.Id && c.Models.Contains(s.AiModel) ? s.AiModel : c.Models[0],
                        MinWidth = 220,
                        Margin = new Thickness(28, 0, 0, 4),
                        IsEnabled = c.Available,
                    };
                    Avalonia.Automation.AutomationProperties.SetName(models, c.Name + " model");
                }
                rows.Children.Add(radio);
                if (models is not null)
                {
                    rows.Children.Add(models);
                }
                if (c.Id == AiProviders.Custom)
                {
                    rows.Children.Add(Indented(new StackPanel { Spacing = 4, Children = { customUrl, customKey } }));
                }
                if (c.Id == AiProviders.Anthropic)
                {
                    rows.Children.Add(Indented(anthropicKey));
                    anthropicKey.PlaceholderText = c.Available ? "a key is saved; type a new one to replace it" : "sk-ant-…";
                }
                list.Add(new Row(c.Id, radio, models));
            }
            int local = found.Count(c => c.Available && c.Location != AiLocation.Cloud);
            status.Text = local > 0
                ? $"Found {local} local model source{(local == 1 ? "" : "s")}. Local models keep your code on this computer."
                : "No local model is running. " + AiDiscovery.Suggestion();
            refresh.IsEnabled = true;
        }

        refresh.Click += async (_, _) => await FillAsync();
        ok.Click += async (_, _) =>
        {
            ok.IsEnabled = false;
            Row? chosen = list.FirstOrDefault(r => r.Radio.IsChecked == true);
            s.AiProvider = chosen?.Id ?? AiProviders.Auto;
            s.AiModel = chosen?.Models?.SelectedItem as string ?? "";
            s.AiCustomUrl = customUrl.Text?.Trim() ?? "";
            s.AiAllowCloud = allowCloud.IsChecked == true;
            s.AiAskWhenStuck = askWhenStuck.IsChecked == true;
            try
            {
                if (!string.IsNullOrWhiteSpace(customKey.Text))
                {
                    await vm.Secrets.SetAsync("custom", customKey.Text);
                }
                if (!string.IsNullOrWhiteSpace(anthropicKey.Text))
                {
                    string where = await vm.Secrets.SetAsync("anthropic", anthropicKey.Text);
                    vm.Log("AI: saved the Anthropic API key in " + where + ".");
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                vm.Log("AI: could not save the API key: " + e.Message);
            }
            s.Save();
            vm.ForgetAiModel();
            vm.Log("AI: using " + (s.AiProvider == AiProviders.Auto ? "the best model running on this computer" : AiProviders.Name(s.AiProvider) + (s.AiModel.Length > 0 ? " · " + s.AiModel : "")) + ".");
            w.Close();
        };
        cancel.Click += (_, _) => w.Close();

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0), Children = { cancel, ok } };
        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        head.Children.Add(status);
        Grid.SetColumn(refresh, 1);
        refresh.VerticalAlignment = VerticalAlignment.Top;
        head.Children.Add(refresh);
        var panel = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                new TextBlock
                {
                    Text = "Lean Studio's AI suggests proofs, explains errors and goals, and answers questions about your code. "
                         + "Every proof it suggests is checked by Lean before it is offered.",
                    TextWrapping = TextWrapping.Wrap,
                },
                head,
                new ScrollViewer { Content = rows, MaxHeight = 420 },
                allowCloud,
                askWhenStuck,
                new TextBlock
                {
                    Text = "API keys are kept in " + (OperatingSystem.IsMacOS() ? "the Keychain" : OperatingSystem.IsLinux() ? "the keyring (or a private file)" : "a private file in your profile")
                         + ", never in settings.json. ANTHROPIC_API_KEY is used when no key is saved.",
                    FontSize = 11, Opacity = 0.6, TextWrapping = TextWrapping.Wrap,
                },
                buttons,
            },
        };
        w.Content = new Border { Padding = new Thickness(24, 20), Child = panel };
        w.Opened += async (_, _) =>
        {
            DialogHooks.Raise(w);
            await FillAsync();
        };
        await w.ShowDialog(owner);
    }

    private static Control Indented(Control c)
    {
        c.Margin = new Thickness(28, 0, 0, 4);
        return c;
    }
}

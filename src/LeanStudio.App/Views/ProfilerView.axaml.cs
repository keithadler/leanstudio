using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using LeanStudio.App.ViewModels;

namespace LeanStudio.App.Views;

/// <summary>
/// The Profiler panel: what to profile and how, each declaration's cost, and the details of the one picked (a flame
/// graph of its trace, where the cost goes bottom up, its tactic lines, Lean's own profiler, its counters), the files
/// of a project profile, and the last build's modules. Its data context is the <see cref="MainViewModel"/>.
/// </summary>
public sealed partial class ProfilerView : UserControl
{
    /// <summary>Create the view and load its XAML.</summary>
    public ProfilerView() => AvaloniaXamlLoader.Load(this);

    private MainViewModel? Vm => DataContext as MainViewModel;

    private void OnTimingDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is ListBox { SelectedItem: TimingItem t })
        {
            Vm?.OpenTimingCommand.Execute(t);
        }
    }

    private void OnTimingKey(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && sender is ListBox { SelectedItem: TimingItem t })
        {
            Vm?.OpenTimingCommand.Execute(t);
            e.Handled = true;
        }
    }

    private void OnLineCostTapped(object? sender, TappedEventArgs e)
    {
        if (sender is ListBox { SelectedItem: LineCostItem l })
        {
            Vm?.OpenLineCostCommand.Execute(l);
        }
    }

    private void OnProfiledFileTapped(object? sender, TappedEventArgs e)
    {
        if (sender is ListBox { SelectedItem: ProfiledFile f })
        {
            Vm?.ShowProfiledFileCommand.Execute(f);
        }
    }

    /// <summary>The Baseline menu, built as it opens: its list of saved profiles changes with every profile.</summary>
    private void OnBaselineMenuOpening(object? sender, EventArgs e)
    {
        if (sender is not MenuFlyout menu || Vm is not MainViewModel vm)
        {
            return;
        }
        var items = new List<Control>
        {
            new MenuItem { Header = "Set to This Profile", Command = vm.SetProfileBaselineCommand, IsEnabled = vm.Profile is not null },
            new MenuItem { Header = "Compare with the Last Commit (HEAD)", Command = vm.CompareWithHeadCommand },
            new MenuItem { Header = "Compare with a Branch or Commit…", Command = vm.CompareWithRevisionPromptCommand },
        };
        IReadOnlyList<Core.Proofs.SavedProfile> saved = vm.SavedProfiles();
        if (saved.Count > 0)
        {
            var sub = new MenuItem { Header = "Compare with a Saved Profile" };
            foreach (Core.Proofs.SavedProfile p in saved.Take(15))
            {
                var item = new MenuItem { Header = p.Describe() };
                item.Click += (_, _) => _ = vm.CompareWithSavedAsync(p);
                sub.Items.Add(item);
            }
            items.Add(sub);
        }
        items.Add(new Separator());
        items.Add(new MenuItem { Header = "Clear Baseline", Command = vm.ClearProfileBaselineCommand, IsEnabled = vm.HasBaseline });
        menu.Items.Clear();
        foreach (Control c in items)
        {
            menu.Items.Add(c);
        }
    }

    private void OnModuleTimingTapped(object? sender, TappedEventArgs e)
    {
        if (sender is ListBox { SelectedItem: ModuleTiming t })
        {
            Vm?.OpenModuleTimingCommand.Execute(t);
        }
    }
}

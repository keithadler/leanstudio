using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace LeanStudio.App.Views;

/// <summary>An entry in a picker: what it says, a hint to its right, and what choosing it does.</summary>
/// <param name="Title">The entry's text.</param>
/// <param name="Detail">A dimmed hint shown to its right (a path, a shortcut), or null.</param>
/// <param name="Run">What choosing it does; run on the UI thread after the picker has closed.</param>
public sealed record PickerItem(string Title, string? Detail, Func<Task> Run);

/// <summary>
/// The quick picker behind the command palette, quick open and go to symbol: a search box over a list that
/// filters as you type. Arrow keys move, Enter chooses, Escape closes. Items come from an async source, so a
/// picker can ask Lean as the query changes.
/// </summary>
internal static class Picker
{
    /// <summary>
    /// Show a picker over <paramref name="owner"/> and wait until it closes, then run the chosen item, if any.
    /// <paramref name="source"/> is called with the query each time it changes (and once on opening); a call still
    /// running when the query changes again is cancelled and its result ignored.
    /// </summary>
    public static async Task ShowAsync(Window owner, string placeholder, Func<string, CancellationToken, Task<IReadOnlyList<PickerItem>>> source, string initial = "")
    {
        var box = new TextBox { PlaceholderText = placeholder, Text = initial, FontSize = 14, Margin = new Thickness(8) };
        var list = new ListBox { MaxHeight = 420, Background = Brushes.Transparent, Margin = new Thickness(4, 0, 4, 6) };
        list.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<PickerItem>((item, _) =>
        {
            var g = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            g.Children.Add(new TextBlock { Text = item?.Title, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis });
            var detail = new TextBlock { Text = item?.Detail, FontSize = 11, Opacity = 0.6, Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(detail, 1);
            g.Children.Add(detail);
            return g;
        });
        var w = new Window
        {
            Title = placeholder,
            Width = 620,
            SizeToContent = SizeToContent.Height,
            WindowDecorations = WindowDecorations.BorderOnly,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
            CanResize = false,
            Content = new StackPanel { Children = { box, list } },
        };
        PickerItem? chosen = null;
        CancellationTokenSource? cts = null;
        async Task Refresh()
        {
            cts?.Cancel();
            var mine = new CancellationTokenSource();
            cts = mine;
            try
            {
                IReadOnlyList<PickerItem> items = await source(box.Text ?? "", mine.Token);
                if (!mine.IsCancellationRequested)
                {
                    list.ItemsSource = items;
                    list.SelectedIndex = items.Count > 0 ? 0 : -1;
                }
            }
            catch (OperationCanceledException)
            {
            }
        }
        box.TextChanged += (_, _) => _ = Refresh();
        void Choose()
        {
            chosen = list.SelectedItem as PickerItem;
            w.Close();
        }
        box.KeyDown += (_, e) =>
        {
            int count = list.ItemCount;
            switch (e.Key)
            {
                case Key.Down when count > 0:
                    list.SelectedIndex = Math.Min(count - 1, list.SelectedIndex + 1);
                    list.ScrollIntoView(list.SelectedIndex);
                    e.Handled = true;
                    break;
                case Key.Up when count > 0:
                    list.SelectedIndex = Math.Max(0, list.SelectedIndex - 1);
                    list.ScrollIntoView(list.SelectedIndex);
                    e.Handled = true;
                    break;
                case Key.Enter:
                    Choose();
                    e.Handled = true;
                    break;
                case Key.Escape:
                    w.Close();
                    e.Handled = true;
                    break;
            }
        };
        list.DoubleTapped += (_, _) => Choose();
        list.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                Choose();
            }
            else if (e.Key == Key.Escape)
            {
                w.Close();
            }
        };
        w.Deactivated += (_, _) => w.Close();
        w.Opened += (_, _) =>
        {
            DialogHooks.Raise(w);
            box.Focus();
            box.CaretIndex = box.Text?.Length ?? 0;
            _ = Refresh();
        };
        await w.ShowDialog(owner);
        if (chosen is not null)
        {
            // Let the picker close before the action opens anything of its own.
            await Dispatcher.UIThread.InvokeAsync(chosen.Run, DispatcherPriority.Background);
        }
    }
}

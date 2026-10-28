using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Zinwe.Agent.Tray.Widget;

namespace Zinwe.Agent.Tray.Views;

/// <summary>Tasks (task seam): four plain states, one-tap Done with Undo, filters that combine.</summary>
public partial class TasksView : UserControl
{
    public TasksView() => InitializeComponent();

    public TrayModel? Model { get; set; }

    public void Render()
    {
        if (Model is not { } m)
        {
            return;
        }
        var tasks = m.TaskList;
        var f = m.Filter;
        var now = m.Now;
        RenderChips(m, tasks, f, now);

        var shown = TaskBoard.Shown(tasks, f, now);
        Groups.Children.Clear();
        foreach (var g in TaskBoard.Groups(shown, f.Status, m.OwnerFirst))
        {
            var label = new TextBlock { Text = g.Label, Style = (Style)FindResource("Z.Lbl"), Margin = new Thickness(4, 4, 0, 6) };
            Groups.Children.Add(label);
            foreach (var t in g.Items)
            {
                var card = TaskCard.Build(t, m, m.OpenCard == t.Id);
                card.Margin = new Thickness(0, 0, 0, 6);
                Groups.Children.Add(card);
            }
        }
        Empty.Visibility = shown.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyText.Text = TaskBoard.EmptyText(f, m.OwnerFirst);
        ClearFilters.Visibility = f.IsFiltered ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RenderChips(TrayModel m, IReadOnlyList<MemberTask> tasks, TaskFilter f, DateTimeOffset now)
    {
        Chips.Children.Clear();
        foreach (var (key, label) in TaskBoard.StatusChips)
        {
            var count = TaskBoard.ChipCount(tasks, key, f, now);
            var chip = FilterChip(label, f.Status == key, count > 0 && key != "done" ? count : 0, key == "new",
                () => m.SetFilter(f with { Status = key }));
            Chips.Children.Add(chip);
        }
        var bar = new Border { Width = 1, Margin = new Thickness(2, 12, 8, 12) };
        bar.SetResourceReference(Border.BackgroundProperty, "Z.Line2");
        Chips.Children.Add(bar);

        var late = tasks.Count(t => TaskBoard.IsLate(t, now));
        Chips.Children.Add(Dropdown("Due date", Ui.Icon.Calendar, f.Due, TaskBoard.DueOptions, f.Due == "overdue",
            f.Due == "any" && late > 0, k => m.SetFilter(f with { Due = k }), tasks, (t, k) => TaskBoard.PassesDue(t, k, now), "Due: "));
        Chips.Children.Add(Dropdown("From", Ui.Icon.Person, f.From, TaskBoard.FromOptions(m.OwnerFirst), false, false,
            k => m.SetFilter(f with { From = k }), tasks, TaskBoard.PassesFrom, ""));

        var high = tasks.Count(t => !TaskBoard.Finished(t) && t.High);
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(Ui.Text("!", 13, FontWeights.ExtraBold, "Z.Ink2").Res(TextBlock.ForegroundProperty, f.High ? "Z.RedText" : "Z.Ink2"));
        content.Children.Add(Ui.Text(" High priority", 13, FontWeights.SemiBold).Res(TextBlock.ForegroundProperty, f.High ? "Z.RedText" : "Z.Ink2"));
        if (high > 0 && !f.High)
        {
            content.Children.Add(Count(high, "Z.Surface", "Z.RedText"));
        }
        var highChip = new ToggleButton { Content = content, IsChecked = f.High, Style = (Style)FindResource("Z.FilterChipRed") };
        AutomationProperties.SetName(highChip, "High priority");
        highChip.Checked += (_, _) => m.SetFilter(f with { High = true });
        highChip.Unchecked += (_, _) => m.SetFilter(f with { High = false });
        Chips.Children.Add(highChip);
        if (f.IsFiltered)
        {
            Chips.Children.Add(Ui.Button("Clear", "Z.LinkBtn", () => m.SetFilter(TaskFilter.None)));
        }
    }

    private ToggleButton FilterChip(string label, bool on, int count, bool orangeCount, Action pick)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
        if (count > 0)
        {
            content.Children.Add(orangeCount ? Count(count, "Z.Orange", "Z.OnOrange") : on ? Count(count, "Z.Surface", "Z.StDoing") : Count(count, "Z.Surface", "Z.Ink2"));
        }
        var chip = new ToggleButton { Content = content, IsChecked = on, Style = (Style)FindResource("Z.FilterChip") };
        AutomationProperties.SetName(chip, count > 0 ? $"{label}, {count}" : label);
        chip.Checked += (_, _) => pick();
        chip.Unchecked += (_, _) => pick();
        return chip;
    }

    private static Border Count(int n, string bg, string fg) =>
        new Border
        {
            MinWidth = 18, Height = 18, CornerRadius = new CornerRadius(9), Padding = new Thickness(5, 0, 5, 0), Margin = new Thickness(6, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = Centered(Ui.Text(n.ToString(), 11, FontWeights.ExtraBold, fg)),
        }.Res(Border.BackgroundProperty, bg);

    private static TextBlock Centered(TextBlock t)
    {
        t.HorizontalAlignment = HorizontalAlignment.Center;
        t.VerticalAlignment = VerticalAlignment.Center;
        return t;
    }

    private ToggleButton Dropdown(string idle, string icon, string current, (string Key, string Label)[] options, bool red, bool dot,
        Action<string> pick, IReadOnlyList<MemberTask> tasks, Func<MemberTask, string, bool> test, string prefix)
    {
        var on = current != "any";
        var label = on ? prefix + options.First(o => o.Key == current).Label : idle;
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(Ui.Glyph(icon, 14, on ? (red ? "Z.RedText" : "Z.StDoing") : "Z.Ink2"));
        content.Children.Add(new TextBlock { Text = label, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        if (dot)
        {
            var d = Ui.Dot(7, "Z.RedText");
            d.Margin = new Thickness(6, 0, 0, 0);
            content.Children.Add(d);
        }
        var chev = Ui.Glyph(Ui.Icon.Chevron, 12, on ? (red ? "Z.RedText" : "Z.StDoing") : "Z.Ink2", 2.6);
        chev.Margin = new Thickness(6, 0, 0, 0);
        content.Children.Add(chev);
        var chip = new ToggleButton { Content = content, IsChecked = on, Style = (Style)FindResource(red ? "Z.FilterChipRed" : "Z.FilterChip") };
        AutomationProperties.SetName(chip, idle + ": " + options.First(o => o.Key == current).Label);
        chip.Click += (_, _) =>
        {
            chip.IsChecked = on;
            var menu = new ContextMenu { PlacementTarget = chip, Placement = PlacementMode.Bottom };
            foreach (var (key, text) in options)
            {
                var n = key == "any" ? 0 : tasks.Count(t => !TaskBoard.Finished(t) && test(t, key));
                var item = new MenuItem { Header = n > 0 ? $"{text} ({n})" : text, IsCheckable = true, IsChecked = key == current };
                item.Click += (_, _) => pick(key);
                menu.Items.Add(item);
            }
            menu.IsOpen = true;
        };
        return chip;
    }

    private void OnChipWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is ScrollViewer sv)
        {
            sv.ScrollToHorizontalOffset(sv.HorizontalOffset - e.Delta / 3.0);
            e.Handled = true;
        }
    }

    private void OnClear(object sender, RoutedEventArgs e) => Model?.SetFilter(TaskFilter.None);

    private void OnAdd(object sender, RoutedEventArgs e) => Model?.OpenNewTask();
}

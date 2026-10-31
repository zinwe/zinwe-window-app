using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Zinwe.Agent.Tray.Widget;

namespace Zinwe.Agent.Tray.Views;

/// <summary>Tasks (task seam): four plain states, one-tap Done with Undo, a Todo button and dropdown filters that combine.</summary>
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
        RenderFilters(m, tasks, f, now);

        var shown = TaskBoard.Shown(tasks, f, now);
        Groups.Children.Clear();
        foreach (var g in TaskBoard.Groups(shown, f.Status, m.OwnerFirst))
        {
            var label = Ui.Text($"{g.Label}  {g.Items.Count}", 12, FontWeights.SemiBold, "Z.Muted");
            label.Margin = new Thickness(2, 6, 0, 6);
            Groups.Children.Add(label);
            foreach (var t in g.Items)
            {
                var card = TaskCard.Build(t, m, m.OpenCard == t.Id);
                card.Margin = new Thickness(0, 0, 0, 8);
                Groups.Children.Add(card);
            }
        }
        ResultText.Text = shown.Count == 1 ? "1 task" : shown.Count + " tasks";
        ClearLink.Visibility = f.IsFiltered ? Visibility.Visible : Visibility.Collapsed;
        Empty.Visibility = shown.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyText.Text = TaskBoard.EmptyText(f, m.OwnerFirst);
    }

    private void RenderFilters(TrayModel m, IReadOnlyList<MemberTask> tasks, TaskFilter f, DateTimeOffset now)
    {
        var todo = TaskBoard.TodoCount(tasks, f, now);
        TodoCount.Text = todo.ToString();
        if (TodoButton.IsChecked != f.Todo)
        {
            TodoButton.IsChecked = f.Todo;
        }
        AutomationProperties.SetName(TodoButton, $"Todo: overdue and today, {todo}");

        Filters.Children.Clear();
        var status = TaskBoard.StatusOptions(m.OwnerFirst)
            .Select(o => (o.Key, o.Label, Count: o.Key == "high" ? tasks.Count(t => !TaskBoard.Finished(t) && t.High) : TaskBoard.ChipCount(tasks, o.Key, f with { High = false }, now)))
            .ToArray();
        Add(0, Dropdown("Status", f.High ? "high" : f.Status, status, "all", key => m.SetFilter(TaskBoard.WithStatus(f, key))));
        Add(1, Dropdown("Date", f.Due, [.. TaskBoard.DueOptions.Select(o => (o.Key, o.Label, Open(t => TaskBoard.PassesDue(t, o.Key, now))))], "any",
            key => m.SetFilter(f with { Due = key, Todo = false })));
        Add(2, Dropdown("From", f.From, [.. TaskBoard.FromOptions(m.OwnerFirst).Select(o => (o.Key, o.Label, Open(t => TaskBoard.PassesFrom(t, o.Key))))], "any",
            key => m.SetFilter(f with { From = key })));
        return;

        int Open(Func<MemberTask, bool> test) => tasks.Count(t => !TaskBoard.Finished(t) && test(t));

        void Add(int column, UIElement filter)
        {
            Grid.SetColumn(filter, column);
            Filters.Children.Add(filter);
        }
    }

    private ToggleButton Dropdown(string name, string current, (string Key, string Label, int Count)[] options, string idle, Action<string> pick)
    {
        var on = current != idle;
        var selected = options.First(o => o.Key == current).Label;
        var content = new Grid();
        content.ColumnDefinitions.Add(new ColumnDefinition());
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        content.Children.Add(new TextBlock { Text = on ? selected : name, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
        var chevron = Ui.Glyph(Ui.Icon.Chevron, 11, on ? "Z.Ink" : "Z.Muted", 2.6);
        chevron.Margin = new Thickness(4, 0, 0, 0);
        Grid.SetColumn(chevron, 1);
        content.Children.Add(chevron);

        var button = new ToggleButton { Content = content, IsChecked = on, Style = (Style)FindResource("Z.FilterBtn"), Margin = new Thickness(0, 0, 6, 0) };
        AutomationProperties.SetName(button, $"{name}: {selected}");
        button.Click += (_, _) =>
        {
            button.IsChecked = on;
            var menu = new ContextMenu { PlacementTarget = button, Placement = PlacementMode.Bottom, VerticalOffset = 4, Style = (Style)FindResource("Z.Menu") };
            foreach (var (key, label, count) in options)
            {
                var item = new MenuItem
                {
                    Header = label, Tag = key == idle ? "" : count.ToString(), IsCheckable = true, IsChecked = key == current,
                    Style = (Style)FindResource("Z.MenuItem"),
                };
                item.Click += (_, _) => pick(key);
                menu.Items.Add(item);
            }
            menu.Closed += (_, _) => button.Focus();
            menu.IsOpen = true;
        };
        return button;
    }

    private void OnTodo(object sender, RoutedEventArgs e)
    {
        if (Model is { } m)
        {
            m.SetFilter(TaskBoard.ToggleTodo(m.Filter));
        }
    }

    private void OnClear(object sender, RoutedEventArgs e) => Model?.SetFilter(TaskFilter.None);

    private void OnAdd(object sender, RoutedEventArgs e) => Model?.OpenNewTask();
}

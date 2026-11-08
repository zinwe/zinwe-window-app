using System.Windows;
using System.Windows.Controls;

namespace Zinwe.Agent.Tray.Views;

/// <summary>
/// Tasks v1.4 (prototype WinApp board, engine_win.js renderVals): search + "+ Add", the chips All · Today · Upcoming ·
/// Done with counts, and the member's cards under "Needs you" (a button for you, your own open to-dos) and "Waiting" (on
/// a teammate or on the owner); Done lists what the owner gave the OK. The list is rebuilt only when what it shows
/// changed, so the 5 s status poll keeps its scroll and focus.
/// </summary>
public partial class TasksView : UserControl
{
    private string _shown = "";

    public TasksView()
    {
        InitializeComponent();
    }

    public TrayModel? Model { get; set; }

    /// <summary>Kept for the window's snack bar wiring (the + is no longer floating, so nothing moves).</summary>
    public void SetSnackLift(double snackHeight)
    {
        _ = snackHeight;
    }

    public void Render()
    {
        if (Model is not { } m)
        {
            return;
        }
        if (Search.Text != m.TaskQuery && !Search.IsKeyboardFocused)
        {
            Search.Text = m.TaskQuery;
        }
        var sections = m.TaskSections;
        var counts = new[] { TaskChip.All, TaskChip.Today, TaskChip.Upcoming, TaskChip.Done }.Select(c => (c, m.ChipCount(c))).ToList();
        var sig = string.Join("|", counts.Select(c => c.Item2)) + $"|{m.Chip}|{m.TaskQuery}|{m.Now:yyyyMMddHHmm}|{m.OwnerFirst}|{m.Status?.ChatVersion}"
            + string.Join("#", sections.Select(s => s.Name + ":" + string.Join(",", s.Items.Select(i =>
                System.Text.Json.JsonSerializer.Serialize(i.Task) + i.Ctx.GroupName + m.TaskChatSummary(i)))));
        if (sig == _shown)
        {
            return;
        }
        _shown = sig;

        Chips.Children.Clear();
        foreach (var (chip, n) in counts)
        {
            var label = chip switch { TaskChip.Today => "Today", TaskChip.Upcoming => "Upcoming", TaskChip.Done => "Done", _ => "All" };
            Chips.Children.Add(Tv.Chip(label, n, m.Chip == chip, () => m.SetChip(chip)));
        }

        Rows.Children.Clear();
        foreach (var (name, sub, items) in sections)
        {
            // The section line (prototype): "Needs you" 13 px, its hint and count 12 px, 12 px above, 4 below.
            var head = new WrapPanel { Margin = new Thickness(4, 12, 4, 4), MinHeight = 18 };
            var title = Ui.Text(name, 13, FontWeights.SemiBold);
            title.Margin = new Thickness(0, 0, 6, 0);
            head.Children.Add(title);
            var s = Ui.Text(sub, 12, fg: "Z.Muted");
            s.Margin = new Thickness(0, 1, 6, 0);
            head.Children.Add(s);
            var count = Ui.Text(items.Count.ToString(System.Globalization.CultureInfo.InvariantCulture), 12, fg: "Z.Faint");
            count.Margin = new Thickness(0, 1, 0, 0);
            head.Children.Add(count);
            Rows.Children.Add(head);
            foreach (var it in items)
            {
                var card = TaskCardV2.Build(it, m);
                card.Margin = new Thickness(0, 4, 0, 4); // 8 px between cards
                Rows.Children.Add(card);
            }
        }
        Empty.Visibility = sections.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        (EmptyTitle.Text, EmptyText.Text) = m.TaskQuery.Trim().Length > 0
            ? ("No tasks match “" + m.TaskQuery.Trim() + "”", "Try another word, or clear the search.")
            : m.Chip == TaskChip.Done
                ? ("Nothing approved yet", $"Tasks {m.OwnerFirst} approves show up here.")
                : ("All clear", "Nothing needs you right now.");
    }

    private void OnSearch(object sender, TextChangedEventArgs e) => Model?.SetTaskQuery(Search.Text);

    private void OnAdd(object sender, RoutedEventArgs e) => Model?.OpenNewTask();

    /// <summary>The wheel moves the chips sideways while they run wider than the window.</summary>
    private void OnChipWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        if (ChipScroll.ScrollableWidth > 0)
        {
            ChipScroll.ScrollToHorizontalOffset(ChipScroll.HorizontalOffset - e.Delta / 3.0);
            e.Handled = true;
        }
    }
}

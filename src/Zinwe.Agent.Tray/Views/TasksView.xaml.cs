using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Zinwe.Agent.Tray.Widget;

namespace Zinwe.Agent.Tray.Views;

/// <summary>
/// Tasks (F-16), built like the to-do apps people know: add at the top, four views, every task under a real date.
/// The due words come from the clock at each render (TaskBoard), so yesterday's "Tomorrow" reads Today after midnight.
/// A tick never undoes itself: the task stays in place, greyed with Undo, then moves to Done; an owner's task stays
/// ticked in orange as "Waiting for {owner}'s OK".
/// </summary>
public partial class TasksView : UserControl
{
    private static readonly (TaskView View, string Label)[] ViewNames =
        [(TaskView.Today, "Today"), (TaskView.Upcoming, "Upcoming"), (TaskView.All, "All"), (TaskView.Done, "Done")];

    private readonly Dictionary<TaskView, RadioButton> _views = [];
    // The section headings drawn this render, top to bottom, for the pinned copy at the top of the list.
    private readonly List<(FrameworkElement Heading, Func<FrameworkElement> Copy)> _headings = [];
    private bool _menuOpen;

    public TasksView()
    {
        InitializeComponent();
        foreach (var (view, label) in ViewNames)
        {
            var b = new RadioButton { GroupName = "taskview", Style = (Style)FindResource("Z.ViewSeg") };
            b.Click += (_, _) => Model?.SetView(view);
            _views[view] = b;
            Views.Children.Add(b);
        }
    }

    public TrayModel? Model { get; set; }

    public void Render()
    {
        if (Model is not { } m)
        {
            return;
        }
        var now = m.Now;
        var tasks = m.TaskList;
        var holding = m.Holding;
        Clock.Text = TaskBoard.DayLabel(TaskBoard.Today(now)) + " · " + TaskBoard.TimeLabel(now);
        RenderAdd(m, now);

        foreach (var (view, label) in ViewNames)
        {
            var count = TaskBoard.Count(tasks, view, now, holding);
            var content = new StackPanel { Orientation = Orientation.Horizontal };
            content.Children.Add(new TextBlock { Text = label });
            if (count > 0)
            {
                var n = Ui.Text(count.ToString(), 11, fg: "Z.Muted");
                n.Margin = new Thickness(4, 1, 0, 0);
                n.VerticalAlignment = VerticalAlignment.Center;
                content.Children.Add(n);
            }
            var b = _views[view];
            b.Content = content;
            b.IsChecked = m.View == view;
            AutomationProperties.SetName(b, view == TaskView.Done ? $"{label}, {count} today" : $"{label}, {count}");
        }

        Rows.Children.Clear();
        _headings.Clear();
        var sections = TaskBoard.Sections(tasks, m.View, now, holding);
        foreach (var s in sections)
        {
            var sub = s.Sub.Length > 0 ? $"{s.Sub} · {s.Items.Count}" : s.Items.Count.ToString();
            AddHeading(s.Name, sub, s.Late, null);
            foreach (var t in s.Items)
            {
                Rows.Children.Add(Row(t, m, holding));
            }
        }
        if (m.View == TaskView.Today && TaskBoard.DoneToday(tasks, now, holding) is { Count: > 0 } doneToday)
        {
            AddHeading("Done today", doneToday.Count.ToString(), false,
                () => Ui.Button(m.ShowDoneToday ? "Hide" : "Show", "Z.TextAction", m.ToggleDoneToday,
                    (m.ShowDoneToday ? "Hide" : "Show") + " what you did today"));
            if (m.ShowDoneToday)
            {
                foreach (var t in doneToday)
                {
                    Rows.Children.Add(Row(t, m, holding));
                }
            }
        }
        var empty = sections.Count == 0;
        Empty.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        (EmptyTitle.Text, EmptyText.Text) = TaskBoard.EmptyText(m.View);
        Dispatcher.BeginInvoke(UpdatePinned, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    // ---- Sections -------------------------------------------------------------------------------

    private void AddHeading(string name, string sub, bool late, Func<FrameworkElement>? action)
    {
        var heading = Heading();
        Rows.Children.Add(heading);
        _headings.Add((heading, Heading));
        return;

        FrameworkElement Heading()
        {
            var row = new DockPanel { LastChildFill = false, MinHeight = 36, Margin = new Thickness(4, 6, 0, 0) };
            var title = Ui.Text(name, 13, FontWeights.Bold, late ? "Z.WarnText" : "Z.Ink");
            title.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(title);
            var meta = Ui.Text(sub, 12, fg: "Z.Muted");
            meta.VerticalAlignment = VerticalAlignment.Center;
            meta.Margin = new Thickness(8, 1, 0, 0);
            row.Children.Add(meta);
            if (action?.Invoke() is { } a)
            {
                DockPanel.SetDock(a, Dock.Right);
                row.Children.Add(a);
            }
            AutomationProperties.SetName(row, $"{name}, {sub}");
            return new Border { Child = row }.Res(Border.BackgroundProperty, "Z.Surface");
        }
    }

    private void OnListScrolled(object sender, ScrollChangedEventArgs e) => UpdatePinned();

    /// <summary>The heading of the section under the top edge stays pinned there (the list's sticky heading).</summary>
    private void UpdatePinned()
    {
        (FrameworkElement Heading, Func<FrameworkElement> Copy)? top = null;
        foreach (var h in _headings)
        {
            if (!h.Heading.IsVisible)
            {
                continue;
            }
            if (h.Heading.TranslatePoint(new Point(0, 0), ListScroller).Y > 0.5)
            {
                break;
            }
            top = h;
        }
        if (top is not { } pin || ListScroller.VerticalOffset < 1)
        {
            Pinned.Visibility = Visibility.Collapsed;
            Pinned.Child = null;
            return;
        }
        Pinned.Child = pin.Copy();
        Pinned.Visibility = Visibility.Visible;
    }

    // ---- One task ---------------------------------------------------------------------------------

    private static FrameworkElement Row(MemberTask t, TrayModel m, IReadOnlySet<int> holding)
    {
        var now = m.Now;
        var owner = m.OwnerFirst;
        var held = holding.Contains(t.Id);
        var waiting = t.Status == MemberTaskStatus.Review;
        var greyed = TaskBoard.Finished(t) || held && !waiting;
        var open = m.OpenCard == t.Id;

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        grid.Children.Add(Check(t, m, TaskBoard.Ticked(t), waiting));

        var title = new TextBlock
        {
            Text = t.Title, FontSize = 14, TextWrapping = TextWrapping.Wrap, LineHeight = 19,
            TextDecorations = greyed ? TextDecorations.Strikethrough : null,
        }.Res(TextBlock.ForegroundProperty, greyed ? "Z.Muted" : "Z.Ink");
        var titleButton = new Button
        {
            Content = title, Cursor = Cursors.Hand, HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(0, 3, 0, 3),
            Template = Plain(), FocusVisualStyle = (Style)Application.Current.FindResource("Z.FocusRing"),
        };
        titleButton.Click += (_, _) => m.ToggleCard(t.Id);
        AutomationProperties.SetName(titleButton, (open ? "Hide details: " : "Show details: ") + t.Title);
        Grid.SetColumn(titleButton, 1);
        grid.Children.Add(titleButton);

        var more = MoreButton(t, m);
        Grid.SetColumn(more, 2);
        grid.Children.Add(more);

        var meta = Meta(t, m, owner, now, held);
        if (meta.Children.Count > 0)
        {
            Grid.SetRow(meta, 1);
            Grid.SetColumn(meta, 1);
            Grid.SetColumnSpan(meta, 2);
            grid.Children.Add(meta);
        }

        if (open || m.OpenLog == t.Id)
        {
            var extra = new StackPanel { Margin = new Thickness(0, 0, 6, 4) };
            if (open)
            {
                extra.Children.Add(TaskCard.Details(t, m, m.OwnerFirst));
            }
            if (m.OpenLog == t.Id)
            {
                extra.Children.Add(TaskCard.LogPanel(t, m));
            }
            Grid.SetRow(extra, 2);
            Grid.SetColumn(extra, 1);
            Grid.SetColumnSpan(extra, 2);
            grid.Children.Add(extra);
        }

        return new Border
        {
            Child = grid, Padding = new Thickness(4, 6, 0, 6), BorderThickness = new Thickness(0, 0, 0, 1), SnapsToDevicePixels = true,
        }.Res(Border.BorderBrushProperty, "Z.Line");
    }

    /// <summary>The round tick: empty while open, ink when done, orange while it waits for the owner's OK.</summary>
    private static Button Check(MemberTask t, TrayModel m, bool ticked, bool waiting)
    {
        var circle = new Border
        {
            Width = 20, Height = 20, CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1.6),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        };
        var fill = waiting ? "Z.Orange" : ticked ? "Z.Inv" : "Z.Surface";
        circle.SetResourceReference(Border.BackgroundProperty, fill);
        circle.SetResourceReference(Border.BorderBrushProperty, ticked ? fill : "Z.Ink2");
        if (ticked)
        {
            var mark = new Path
            {
                Data = Geometry.Parse("M2,6.5 L4.5,9 L10,3.5"), StrokeThickness = 2, Width = 12, Height = 12,
                StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            };
            mark.SetResourceReference(Shape.StrokeProperty, waiting ? "Z.OnOrange" : "Z.OnInv");
            circle.Child = mark;
        }
        var closed = t.RemoteId is not null && t.Status is MemberTaskStatus.Approved or MemberTaskStatus.Cancelled;
        var b = new Button
        {
            Content = circle, Width = 40, Height = 40, Margin = new Thickness(-10, -7, -2, -9), VerticalAlignment = VerticalAlignment.Top,
            Cursor = closed ? Cursors.Arrow : Cursors.Hand, Template = Plain(), IsEnabled = !closed,
            FocusVisualStyle = (Style)Application.Current.FindResource("Z.FocusRing"),
        };
        if (!ticked)
        {
            b.MouseEnter += (_, _) => circle.SetResourceReference(Border.BorderBrushProperty, "Z.Orange");
            b.MouseLeave += (_, _) => circle.SetResourceReference(Border.BorderBrushProperty, "Z.Ink2");
        }
        b.Click += (_, _) => m.ToggleTick(t);
        AutomationProperties.SetName(b, (ticked ? "Mark not done: " : "Mark done: ") + t.Title);
        AutomationProperties.SetItemStatus(b, waiting ? $"Waiting for {m.OwnerFirst}’s OK" : ticked ? "done" : "open");
        return b;
    }

    /// <summary>The line under the title: due words, who it's from, steps, priority, where it stands, Undo.</summary>
    private static WrapPanel Meta(MemberTask t, TrayModel m, string owner, DateTimeOffset now, bool held)
    {
        var meta = new WrapPanel { Margin = new Thickness(0, 0, 0, 2) };
        void Add(FrameworkElement el)
        {
            el.Margin = new Thickness(0, 0, 10, 2);
            el.VerticalAlignment = VerticalAlignment.Center;
            meta.Children.Add(el);
        }

        if ((!TaskBoard.Ticked(t) || held) && TaskBoard.DueLabel(t, now) is { Length: > 0 } due)
        {
            var tone = TaskBoard.DueToneOf(t, now);
            Add(Ui.Text(due, 12, tone == DueTone.Late ? FontWeights.SemiBold : FontWeights.Normal, tone == DueTone.Normal ? "Z.Muted" : "Z.WarnText"));
        }
        if (t.IsNew && !TaskBoard.Ticked(t))
        {
            var tag = Ui.Pill("New", "Z.OrangeTint", "Z.OrangeInk", 11, FontWeights.SemiBold);
            tag.Padding = new Thickness(5, 0, 5, 0);
            Add(tag);
        }
        if (!t.Mine)
        {
            var from = new StackPanel { Orientation = Orientation.Horizontal };
            var initial = Ui.Text(owner.Length > 0 ? owner[..1].ToUpperInvariant() : "?", 9, FontWeights.Bold, "Z.OnInv");
            initial.HorizontalAlignment = HorizontalAlignment.Center;
            initial.VerticalAlignment = VerticalAlignment.Center;
            from.Children.Add(new Border { Width = 15, Height = 15, CornerRadius = new CornerRadius(7.5), Child = initial, Margin = new Thickness(0, 0, 4, 0) }
                .Res(Border.BackgroundProperty, "Z.Inv"));
            from.Children.Add(Ui.Text("From " + owner, 12, fg: "Z.Muted"));
            Add(from);
        }
        if (t.Status is MemberTaskStatus.Progress && !held)
        {
            Add(Ui.Text("Doing", 12, FontWeights.SemiBold, "Z.StDoing"));
        }
        else if (t.Status is MemberTaskStatus.Help or MemberTaskStatus.Cant)
        {
            Add(Ui.Text(TaskBoard.LaneLabel(t, m.OwnerFirst), 12, FontWeights.SemiBold, "Z.StWait"));
        }
        if (t.Steps.Count > 0)
        {
            Add(Ui.Text($"{t.Steps.Count(s => s.Done)}/{t.Steps.Count} steps", 12, fg: "Z.Muted"));
        }
        if (t.High && !TaskBoard.Ticked(t))
        {
            Add(Ui.Text("High", 12, FontWeights.Bold, "Z.Orange"));
        }
        if (t.Status == MemberTaskStatus.Review)
        {
            var pill = new Border
            {
                CornerRadius = new CornerRadius(9), BorderThickness = new Thickness(1), Padding = new Thickness(7, 0, 7, 1),
                Child = Ui.Text($"Waiting for {m.OwnerFirst}’s OK", 12, fg: "Z.WarnText"),
            }.Res(Border.BackgroundProperty, "Z.AccentSoft").Res(Border.BorderBrushProperty, "Z.AccentLine");
            Add(pill);
        }
        else if (t.Status is MemberTaskStatus.Approved or MemberTaskStatus.Cancelled)
        {
            Add(Ui.Text(t.Status == MemberTaskStatus.Approved ? $"Approved by {m.OwnerFirst}" : "Cancelled", 12, fg: "Z.Muted"));
        }
        if (TaskBoard.Finished(t) && t.DoneAt is { } doneAt && !held)
        {
            Add(Ui.Text("Done " + TaskCard.Sent(doneAt, now), 12, fg: "Z.Muted"));
        }
        if (TaskBoard.WaitsToSend(t) && m.Offline)
        {
            Add(Ui.Text("Saved on this PC", 12, FontWeights.SemiBold, "Z.WarnText"));
        }
        if (held)
        {
            var undo = Ui.Button("Undo", "Z.TextAction", () => m.ToggleTick(t), "Undo: " + t.Title);
            undo.Margin = new Thickness(0, -12, 0, -12);
            Add(undo);
        }
        return meta;
    }

    /// <summary>The ⋯ menu: what can be done to the task besides ticking it.</summary>
    private static Button MoreButton(MemberTask t, TrayModel m)
    {
        var dots = new GeometryGroup();
        foreach (var x in new[] { 3.5, 8, 12.5 })
        {
            dots.Children.Add(new EllipseGeometry(new Point(x, 8), 1.3, 1.3));
        }
        var glyph = new Path { Data = dots, Width = 16, Height = 16 };
        glyph.SetResourceReference(Shape.FillProperty, "Z.Muted");
        var b = new Button
        {
            Content = glyph, Width = 36, Height = 36, Margin = new Thickness(0, -6, 0, -6), VerticalAlignment = VerticalAlignment.Top,
            Style = (Style)Application.Current.FindResource("Z.IconBtn"),
        };
        AutomationProperties.SetName(b, "More for " + t.Title);
        b.Click += (_, _) =>
        {
            var menu = new ContextMenu { PlacementTarget = b, Placement = PlacementMode.Bottom, Style = (Style)Application.Current.FindResource("Z.Menu") };
            foreach (var (label, run) in Actions(t, m))
            {
                var item = new MenuItem { Header = label, Style = (Style)Application.Current.FindResource("Z.MenuItem") };
                item.Click += (_, _) => run();
                menu.Items.Add(item);
            }
            menu.Closed += (_, _) => b.Focus();
            menu.IsOpen = true;
        };
        return b;
    }

    private static IEnumerable<(string Label, Action Run)> Actions(MemberTask t, TrayModel m)
    {
        var open = !TaskBoard.Ticked(t);
        if (t.Mine)
        {
            yield return ("Edit", () => m.OpenNewTask(t));
            yield return ("Delete", () => m.DeleteOwn(t));
        }
        else
        {
            if (TaskBoard.LaneOf(t) == TaskLane.Todo && t.RemoteId is not null && TaskBoard.NeedsConfirm(t))
            {
                yield return ("Confirm", () => m.ConfirmTask(t));
            }
            if (TaskBoard.LaneOf(t) == TaskLane.Todo || t.Status is MemberTaskStatus.Help or MemberTaskStatus.Cant)
            {
                yield return (TaskBoard.LaneOf(t) == TaskLane.Todo ? "Start" : "Carry on",
                    () => (TaskBoard.LaneOf(t) == TaskLane.Todo ? (Action<MemberTask>)m.StartTask : m.CarryOn)(t));
            }
            if (open && t.Status is not (MemberTaskStatus.Help or MemberTaskStatus.Cant))
            {
                yield return ($"Ask {m.OwnerFirst}", () => m.AskAbout(t));
            }
            if (t.RemoteId is not null && t.Status != MemberTaskStatus.Cancelled)
            {
                yield return ("Add a note", () => m.OpenNote(t.Id));
            }
            yield return (m.OpenLog == t.Id ? "Hide log" : "Show log", () => m.ToggleLog(t.Id));
        }
        yield return (m.OpenCard == t.Id ? "Hide details" : "Show details", () => m.ToggleCard(t.Id));
    }

    /// <summary>A button that draws only its content (the focus ring still shows for the keyboard).</summary>
    private static ControlTemplate Plain()
    {
        var t = new ControlTemplate(typeof(Button));
        var grid = new FrameworkElementFactory(typeof(Grid));
        grid.SetValue(Panel.BackgroundProperty, Brushes.Transparent);
        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Stretch);
        content.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        content.SetValue(FrameworkElement.MarginProperty, new TemplateBindingExtension(Control.PaddingProperty));
        grid.AppendChild(content);
        t.VisualTree = grid;
        return t;
    }

    // ---- Quick add ----------------------------------------------------------------------------------

    // Made once and updated, so a pick keeps keyboard focus in the add box (and its options stay open).
    private static readonly (string Key, string Label)[] DueChoices = [("today", "Today"), ("tomorrow", "Tomorrow"), ("date", "Pick date")];
    private readonly Dictionary<string, ToggleButton> _chips = [];
    private ToggleButton? _time;

    private void RenderAdd(TrayModel m, DateTimeOffset now)
    {
        var q = m.Quick;
        var today = TaskBoard.Today(now);
        if (_chips.Count == 0)
        {
            foreach (var (key, label) in DueChoices)
            {
                var chip = new ToggleButton { Style = (Style)FindResource("Z.Chip"), Margin = new Thickness(0, 0, 4, 0) };
                chip.Click += (_, _) => PickDue(key);
                AutomationProperties.SetName(chip, "Due " + label);
                _chips[key] = chip;
                AddChips.Children.Add(chip);
            }
            _time = new ToggleButton { Style = (Style)FindResource("Z.FilterBtn") };
            _time.Click += (_, _) => OpenTimes();
            AddChips.Children.Add(_time);
        }
        foreach (var (key, label) in DueChoices)
        {
            _chips[key].Content = key == "date" && q is { Due: "date", Date: { } d } ? TaskBoard.DayLabel(d) : label;
            _chips[key].IsChecked = q.Due == key;
        }
        var timeLabel = TimeText(q.Time, today);
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(new TextBlock { Text = timeLabel, VerticalAlignment = VerticalAlignment.Center });
        var chevron = Ui.Glyph(Ui.Icon.Chevron, 11, "Z.Muted", 2.6);
        chevron.Margin = new Thickness(4, 0, 0, 0);
        content.Children.Add(chevron);
        _time!.Content = content;
        _time.IsChecked = q.Time is not null;
        AutomationProperties.SetName(_time, "Due time: " + timeLabel);

        AddDate.Visibility = q.Due == "date" ? Visibility.Visible : Visibility.Collapsed;
        AddDate.DisplayDateStart = today.ToDateTime(TimeOnly.MinValue);
        var selected = q.Date?.ToDateTime(TimeOnly.MinValue);
        if (AddDate.SelectedDate != selected)
        {
            AddDate.SelectedDate = selected;
        }
        AddHint.Text = TaskBoard.QuickHint(q, now);
        UpdateAdd();
    }

    private static string TimeText(TimeOnly? time, DateOnly day) =>
        time is { } t ? "By " + TaskBoard.TimeLabel(new DateTimeOffset(day.ToDateTime(t, DateTimeKind.Local))) : "No time";

    private void PickDue(string key)
    {
        if (Model is not { } m)
        {
            return;
        }
        var q = m.Quick;
        var today = TaskBoard.Today(m.Now);
        m.SetQuick(key == "date"
            ? q with { Due = "date", Date = q.Date is { } picked && picked >= today ? picked : today.AddDays(2) }
            : q with { Due = key });
    }

    /// <summary>"No time" / "By 4:00 PM": a menu of half hours through the working day.</summary>
    private void OpenTimes()
    {
        if (Model is not { } m || _time is not { } button)
        {
            return;
        }
        var q = m.Quick;
        var today = TaskBoard.Today(m.Now);
        button.IsChecked = q.Time is not null;
        var menu = new ContextMenu
        {
            PlacementTarget = button, Placement = PlacementMode.Bottom, Style = (Style)FindResource("Z.Menu"), MaxHeight = 320,
        };
        var times = new List<TimeOnly?> { null };
        for (var h = 9; h <= 20; h++)
        {
            times.Add(new TimeOnly(h, 0));
            times.Add(new TimeOnly(h, 30));
        }
        foreach (var t in times)
        {
            var item = new MenuItem { Header = TimeText(t, today), IsCheckable = true, IsChecked = t == q.Time, Style = (Style)FindResource("Z.MenuItem") };
            item.Click += (_, _) => m.SetQuick(m.Quick with { Time = t });
            menu.Items.Add(item);
        }
        _menuOpen = true;
        menu.Closed += (_, _) =>
        {
            _menuOpen = false;
            AddTitle.Focus();
        };
        menu.IsOpen = true;
    }

    private void OnAddFocus(object sender, DependencyPropertyChangedEventArgs e) => UpdateAdd();

    /// <summary>The options show while the add box is in use; the border turns ink, like a focused field.</summary>
    private void UpdateAdd()
    {
        var active = AddBox.IsKeyboardFocusWithin || _menuOpen || AddTitle.Text.Length > 0 || AddDate.IsDropDownOpen;
        AddOpts.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        AddBox.SetResourceReference(Border.BorderBrushProperty, AddBox.IsKeyboardFocusWithin ? "Z.Ink" : "Z.Line");
        AddGo.IsEnabled = AddTitle.Text.Trim().Length > 0;
    }

    private void OnAddChanged(object sender, TextChangedEventArgs e) => UpdateAdd();

    private void OnAddKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            Add();
        }
    }

    private void OnAddGo(object sender, RoutedEventArgs e)
    {
        Add();
        AddTitle.Focus();
    }

    private void Add()
    {
        if (Model is { } m && m.AddQuick(AddTitle.Text))
        {
            AddTitle.Text = "";
        }
    }

    private void OnAddDate(object? sender, SelectionChangedEventArgs e)
    {
        if (Model is not { } m || AddDate.SelectedDate is not { } d || m.Quick.Date == DateOnly.FromDateTime(d))
        {
            return;
        }
        m.SetQuick(m.Quick with { Due = "date", Date = DateOnly.FromDateTime(d) });
    }
}

using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;
using Zinwe.Agent.Tray.Widget;

namespace Zinwe.Agent.Tray.Views;

/// <summary>
/// Tasks (F-16), the owner-approved prototype tasks-v2: four filters in one row (All with its calendar, Today,
/// Upcoming, Done with its status menu), every task under a real date, and a floating + that opens the Add a to-do
/// sheet. The due words come from the clock at each render (TaskBoard), so yesterday's "Tomorrow" reads Today after
/// midnight. Done is final: a ticked task stays in place a few seconds with Add a note (never Undo), then moves to Done.
/// Each task is a white card on a light grey ground; ⋯ opens its menu (the details card's actions), press and hold opens
/// its details card. Content is black and greys; colour is kept for the status chips and a small dot on what is late
/// or due soon.
/// </summary>
public partial class TasksView : UserControl
{
    // The section headings drawn this render, top to bottom, for the pinned copy at the top of the list.
    private readonly List<(FrameworkElement Heading, Func<FrameworkElement> Copy)> _headings = [];

    // Each task's ⋯ drawn this render (the menu opens under it), and the task whose menu was open last render.
    private readonly Dictionary<int, Button> _moreButtons = [];
    private int? _menuShown;
    private bool _menuPicked;
    private Border? _menu;
    private string _menuKey = "";

    public TasksView()
    {
        InitializeComponent();
    }

    public TrayModel? Model { get; set; }

    /// <summary>
    /// The snack bar's height while it shows (0 when hidden): the floating + moves up above it, so it never covers it.
    /// </summary>
    public void SetSnackLift(double snackHeight) =>
        AddButton.Margin = new Thickness(0, 0, 14, snackHeight > 0 ? 12 + snackHeight + 14 : 14);

    public void Render()
    {
        if (Model is not { } m)
        {
            return;
        }
        // Rows are rebuilt below: a press that hasn't opened its card yet ends (a quick tap must not open one later).
        if (_hold is { } pressed)
        {
            pressed.Redrawn();
            if (!pressed.Opened)
            {
                _hold = null;
            }
        }
        var now = m.Now;
        var tasks = m.TaskList;
        var holding = m.Holding;
        var filter = m.ViewFilter;

        RenderSegments(m, tasks, now, holding, filter);
        RenderFilterLine(m, filter);
        RenderPop(m, tasks, now, holding);

        Rows.Children.Clear();
        _headings.Clear();
        _moreButtons.Clear();
        var sections = TaskBoard.Sections(tasks, m.View, now, holding, filter);
        foreach (var s in sections)
        {
            AddHeading(s.Name, s.Sub, s.Items.Count, null);
            foreach (var t in s.Items)
            {
                Rows.Children.Add(Row(t, m, holding));
            }
        }
        if (m.View == TaskView.Today && TaskBoard.DoneToday(tasks, now, holding) is { Count: > 0 } doneToday)
        {
            AddHeading("Done today", "", doneToday.Count,
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
        Empty.Visibility = sections.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        (EmptyTitle.Text, EmptyText.Text) = TaskBoard.EmptyText(m.View, filter);
        Dispatcher.BeginInvoke(UpdatePinned, DispatcherPriority.Loaded);
        RenderRowMenu(m, tasks, holding);
    }

    // ---- A task's ⋯ menu --------------------------------------------------------------------------------

    private void RenderRowMenu(TrayModel m, IReadOnlyList<MemberTask> tasks, IReadOnlySet<int> holding)
    {
        var task = m.RowMenu is { } id ? tasks.FirstOrDefault(t => t.Id == id) : null;
        if (m.RowMenu is not null && (task is null || !_moreButtons.ContainsKey(task.Id)))
        {
            Dispatcher.BeginInvoke(m.CloseMenu, DispatcherPriority.Background); // its task left the list
            task = null;
        }
        var was = _menuShown;
        _menuShown = task?.Id;
        if (task is null)
        {
            MenuLayer.Children.Clear();
            _menu = null;
            _menuKey = "";
            if (was is { } closed && !_menuPicked && _moreButtons.TryGetValue(closed, out var back))
            {
                // Esc or a click outside: the keyboard goes back to the ⋯ it came from.
                Dispatcher.BeginInvoke(() => back.Focus(), DispatcherPriority.Input);
            }
            _menuPicked = false;
            return;
        }

        // The menu is drawn again only when its lines change, so the status poll's re-render keeps it and its focus.
        var items = RowMenu.Items(task, m.OwnerFirst, holding);
        var key = task.Id + "|" + string.Join("|", items.Select(i => i.Label)) + "|" + HighContrastTheme.IsDark;
        var opened = was != task.Id;
        if (key != _menuKey || _menu is null)
        {
            _menuKey = key;
            MenuLayer.Children.Clear();
            var stack = new StackPanel();
            AutomationProperties.SetName(stack, "Task actions: " + task.Title);
            var taskId = task.Id;
            foreach (var (label, action, danger, bold) in items)
            {
                var text = Ui.Text(label, 13, bold ? FontWeights.SemiBold : FontWeights.Normal, danger ? "Z.RedText" : "Z.Ink");
                var b = Ui.Button(text, "Z.MenuRow", () =>
                {
                    _menuPicked = true;
                    // The task as it is now (a sync may have changed it since the menu opened).
                    if (m.TaskList.FirstOrDefault(x => x.Id == taskId) is { } now)
                    {
                        m.FromRowMenu(() => Run(m, now, action));
                    }
                    else
                    {
                        m.CloseMenu();
                    }
                }, label);
                b.MinHeight = 40;
                b.HorizontalContentAlignment = HorizontalAlignment.Stretch;
                stack.Children.Add(b);
            }
            _menu = Cycle(new Border
            {
                Width = 210, Child = stack, CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1), Padding = new Thickness(4),
                Effect = new DropShadowEffect { BlurRadius = 24, ShadowDepth = 8, Opacity = 0.18, Direction = 270, Color = Color.FromRgb(0x14, 0x12, 0x10) },
                Visibility = Visibility.Hidden, // until it is placed
            }.Res(Border.BackgroundProperty, "Z.Surface").Res(Border.BorderBrushProperty, "Z.Line"));
            MenuLayer.Children.Add(_menu);
        }
        var menu = _menu;
        var anchor = _moreButtons[task.Id];
        Dispatcher.BeginInvoke(() =>
        {
            if (!anchor.IsVisible || menu.Parent is null)
            {
                return;
            }
            menu.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var corner = anchor.TranslatePoint(new Point(anchor.ActualWidth, anchor.ActualHeight), Layout);
            var top = anchor.TranslatePoint(new Point(0, 0), Layout).Y;
            var height = menu.DesiredSize.Height;
            var below = corner.Y + 2;
            Canvas.SetLeft(menu, Math.Max(8, corner.X - menu.Width + 2));
            Canvas.SetTop(menu, below + height > Layout.ActualHeight - 8 ? Math.Max(8, top - height - 2) : below);
            menu.Visibility = Visibility.Visible;
            if (opened && menu.Child is Panel lines)
            {
                lines.Children.OfType<Button>().FirstOrDefault()?.Focus();
            }
        }, DispatcherPriority.Loaded);
    }

    private static void Run(TrayModel m, MemberTask t, RowMenuAction action)
    {
        switch (action)
        {
            case RowMenuAction.MarkDone: m.DoneTask(t); break;
            case RowMenuAction.OpenDetails: m.OpenDetail(t.Id); break;
            case RowMenuAction.Edit: m.OpenNewTask(t); break;
            case RowMenuAction.Tomorrow: m.MoveToTomorrow(t); break;
            case RowMenuAction.Delete: m.DeleteOwn(t); break;
            case RowMenuAction.Ask: m.AskInChat(t); break;
            case RowMenuAction.AddNote: m.OpenNote(t.Id); break;
            case RowMenuAction.Cant: m.CantFor(t); break;
        }
    }

    /// <summary>+ opens the Add a to-do sheet (from Upcoming it starts on Tomorrow).</summary>
    private void OnAdd(object sender, RoutedEventArgs e) => Model?.OpenNewTask();

    private void OnPopCatcher(object sender, MouseButtonEventArgs e)
    {
        Model?.ClosePop();
        Model?.CloseMenu();
        e.Handled = true;
    }

    // ---- The filters --------------------------------------------------------------------------------

    private void RenderSegments(TrayModel m, IReadOnlyList<MemberTask> tasks, DateTimeOffset now, IReadOnlySet<int> holding, TaskFilter filter)
    {
        Segs.Children.Clear();
        var order = new[] { TaskView.All, TaskView.Today, TaskView.Upcoming, TaskView.Done };
        for (var i = 0; i < order.Length; i++)
        {
            var view = order[i];
            var on = m.View == view;
            var count = TaskBoard.Count(tasks, view, now, holding, filter);
            var label = view switch
            {
                TaskView.All => TaskBoard.AllLabel(filter),
                TaskView.Today => "Today",
                TaskView.Upcoming => "Upcoming",
                _ => TaskBoard.DoneLabel(filter),
            };
            var seg = Segment(m, view, label, m.Settings.TextLarge ? 0 : count, on, count);
            seg.Margin = new Thickness(i == 0 ? 0 : 3, 0, i == order.Length - 1 ? 0 : 3, 0);
            Grid.SetColumn(seg, i);
            Segs.Children.Add(seg);
        }
    }

    /// <summary>
    /// One filter: black with white text (label, count and icons) when selected, else outlined in grey. The count is
    /// left out with large text (the label must fit); <paramref name="total"/> still names it for a screen reader.
    /// </summary>
    private static Border Segment(TrayModel m, TaskView view, string label, int count, bool on, int total)
    {
        var fg = on ? "Z.OnInv" : "Z.Ink2";
        var content = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        var text = Ui.Text(label, 12.5, on ? FontWeights.SemiBold : FontWeights.Normal, fg);
        text.VerticalAlignment = VerticalAlignment.Center;
        content.Children.Add(text);
        if (count > 0)
        {
            var n = Ui.Text(count.ToString(CultureInfo.InvariantCulture), 11, on ? FontWeights.SemiBold : FontWeights.Normal, on ? "Z.OnInv" : "Z.Muted");
            n.Margin = new Thickness(4, 1, 0, 0);
            n.VerticalAlignment = VerticalAlignment.Center;
            content.Children.Add(n);
        }
        var main = new Button { Content = content, Cursor = Cursors.Hand, Style = Bare(center: true), Padding = new Thickness(2, 0, 2, 0), Focusable = true };
        main.FocusVisualStyle = (Style)Application.Current.FindResource("Z.FocusRing");
        main.Click += (_, _) => m.SetView(view);
        AutomationProperties.SetName(main, $"{label}, {total}");
        AutomationProperties.SetItemStatus(main, on ? "selected" : "");

        var grid = new Grid();
        if (view == TaskView.Done)
        {
            // Done opens Done; its own ⌄ beside it (a thin line between) opens Show by status.
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var chev = new Path
            {
                Data = Geometry.Parse("M2,3.5 L5,6.5 L8,3.5"), StrokeThickness = 1.6, Width = 10, Height = 10,
                StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            };
            chev.SetResourceReference(Shape.StrokeProperty, on ? "Z.OnInv" : "Z.Muted");
            var split = new Border { Child = chev, BorderThickness = new Thickness(1, 0, 0, 0), Margin = new Thickness(0, 6, 0, 6), Padding = new Thickness(0, 0, 6, 0) };
            if (on)
            {
                split.BorderBrush = OnInkRule;
            }
            else
            {
                split.SetResourceReference(Border.BorderBrushProperty, "Z.Line");
            }
            var menu = new Button
            {
                Content = split, Cursor = Cursors.Hand, Style = Bare(), Width = 24, ToolTip = "Show by status",
                FocusVisualStyle = (Style)Application.Current.FindResource("Z.FocusRing"),
            };
            menu.Click += (_, _) => m.PressDone();
            AutomationProperties.SetName(menu, "Show by status");
            AutomationProperties.SetItemStatus(menu, m.Pop == TaskPop.Status ? "expanded" : "collapsed");
            Grid.SetColumn(menu, 1);
            grid.Children.Add(menu);
        }
        if (view == TaskView.All)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            var cal = new Button
            {
                Content = Ui.Glyph(Ui.Icon.Calendar, 14, on ? "Z.OnInv" : "Z.Muted", 2.1), Cursor = Cursors.Hand, Style = Bare(),
                Width = 26, Padding = new Thickness(8, 0, 0, 0), FocusVisualStyle = (Style)Application.Current.FindResource("Z.FocusRing"),
                ToolTip = "Pick a date",
            };
            cal.Click += (_, _) => m.ToggleCalendar();
            AutomationProperties.SetName(cal, "Pick a date");
            grid.Children.Add(cal);
            Grid.SetColumn(main, 1);
            main.Padding = new Thickness(0, 0, 4, 0);
        }
        grid.Children.Add(main);

        var seg = new Border
        {
            Child = grid, Height = 34, CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), SnapsToDevicePixels = true,
        };
        seg.SetResourceReference(Border.BackgroundProperty, on ? "Z.Inv" : "Z.Surface");
        seg.SetResourceReference(Border.BorderBrushProperty, on ? "Z.Inv" : "Z.Line2");
        if (!on)
        {
            seg.MouseEnter += (_, _) => seg.SetResourceReference(Border.BorderBrushProperty, "Z.Ink2");
            seg.MouseLeave += (_, _) => seg.SetResourceReference(Border.BorderBrushProperty, "Z.Line2");
        }
        return seg;
    }

    /// <summary>"Due on [Thursday 5 November ×]" or "Status [Doing ×]": what the filter in force shows, and how to clear it.</summary>
    private void RenderFilterLine(TrayModel m, TaskFilter filter)
    {
        FilterLine.Children.Clear();
        var (prefix, value, clear, aria) = m.View switch
        {
            TaskView.All when filter.Day is { } d =>
                ("Due on", d.ToString("dddd d MMMM", CultureInfo.InvariantCulture), (Action)(() => m.PickDay(null)), "Clear the date"),
            TaskView.Done when filter.Status is { } s => ("Status", TaskBoard.StatusName(s), () => m.PickStatus(null), "Show all done"),
            _ => ((string?)null, "", (Action)(() => { }), ""),
        };
        FilterLine.Visibility = prefix is null ? Visibility.Collapsed : Visibility.Visible;
        if (prefix is null)
        {
            return;
        }
        var lead = Ui.Text(prefix, 12.5, fg: "Z.Ink2");
        lead.VerticalAlignment = VerticalAlignment.Center;
        lead.Margin = new Thickness(0, 0, 8, 0);
        FilterLine.Children.Add(lead);
        var pill = new StackPanel { Orientation = Orientation.Horizontal };
        var name = Ui.Text(value, 12.5, FontWeights.SemiBold);
        name.VerticalAlignment = VerticalAlignment.Center;
        name.Margin = new Thickness(0, 0, 6, 0);
        pill.Children.Add(name);
        var x = new Border { Width = 18, Height = 18, CornerRadius = new CornerRadius(9), Child = Ui.Glyph("M6 6l12 12M18 6 6 18", 8, "Z.Ink2", 3.2) }
            .Res(Border.BackgroundProperty, "Z.Line");
        var xb = new Button { Content = x, Cursor = Cursors.Hand, Style = Bare(), FocusVisualStyle = (Style)Application.Current.FindResource("Z.FocusRing") };
        xb.Click += (_, _) => clear();
        AutomationProperties.SetName(xb, aria);
        pill.Children.Add(xb);
        FilterLine.Children.Add(new Border { CornerRadius = new CornerRadius(999), Padding = new Thickness(10, 3, 4, 3), Child = pill }
            .Res(Border.BackgroundProperty, "Z.Fill"));
        AutomationProperties.SetName(FilterLine, TaskBoard.FilterLine(m.View, filter) ?? "");
    }

    // ---- Pop-overs: the month on All, the status menu on Done ----------------------------------------------------

    private TaskPop _popShown = TaskPop.None;

    // Marks the control a pop-over focuses when it opens (the picked or today's day, the picked menu row).
    private const string FocusFirst = "focus-first";

    private void RenderPop(TrayModel m, IReadOnlyList<MemberTask> tasks, DateTimeOffset now, IReadOnlySet<int> holding)
    {
        PopCatcher.Visibility = m.Pop == TaskPop.None && m.RowMenu is null ? Visibility.Collapsed : Visibility.Visible;
        PopSlot.Content = m.Pop switch
        {
            TaskPop.Calendar => Calendar(m, tasks, now, holding),
            TaskPop.Status => StatusMenu(m, tasks, now, holding),
            _ => null,
        };
        if (m.Pop != _popShown && PopSlot.Content is DependencyObject pop)
        {
            // Just opened: the keyboard lands inside it (and Tab cycles there until Esc or a pick closes it).
            Dispatcher.BeginInvoke(() => FindTagged(pop)?.Focus(), DispatcherPriority.Loaded);
        }
        _popShown = m.Pop;
    }

    private static Button? FindTagged(DependencyObject root)
    {
        if (root is Button { Tag: FocusFirst } hit)
        {
            return hit;
        }
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            if (FindTagged(VisualTreeHelper.GetChild(root, i)) is { } found)
            {
                return found;
            }
        }
        return null;
    }

    private static Border PopCard(UIElement child, double width, HorizontalAlignment side) => Cycle(new Border
    {
        Width = width, Child = child, CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1), Padding = new Thickness(10),
        HorizontalAlignment = side, VerticalAlignment = VerticalAlignment.Top,
        Margin = side == HorizontalAlignment.Left ? new Thickness(12, 6, 0, 0) : new Thickness(0, 6, 12, 0),
        Effect = new DropShadowEffect { BlurRadius = 32, ShadowDepth = 12, Opacity = 0.14, Direction = 270, Color = Color.FromRgb(0x14, 0x12, 0x10) },
    }.Res(Border.BackgroundProperty, "Z.Surface").Res(Border.BorderBrushProperty, "Z.Line"));

    /// <summary>Tab and Ctrl+Tab stay inside a pop-over or card while it is open.</summary>
    internal static T Cycle<T>(T el) where T : FrameworkElement
    {
        KeyboardNavigation.SetTabNavigation(el, KeyboardNavigationMode.Cycle);
        KeyboardNavigation.SetControlTabNavigation(el, KeyboardNavigationMode.Cycle);
        KeyboardNavigation.SetDirectionalNavigation(el, KeyboardNavigationMode.Cycle);
        return el;
    }

    /// <summary>The month (Monday first): today in orange, a dot on days with open tasks, the picked day black; Today · Tomorrow · Any day.</summary>
    private static Border Calendar(TrayModel m, IReadOnlyList<MemberTask> tasks, DateTimeOffset now, IReadOnlySet<int> holding)
    {
        var today = TaskBoard.Today(now);
        var month = m.CalendarMonth;
        var stack = new StackPanel();
        AutomationProperties.SetName(stack, "Pick a date");

        var head = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition());
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var prev = MonthButton("‹", "Previous month", () => m.ShiftMonth(-1));
        var next = MonthButton("›", "Next month", () => m.ShiftMonth(1));
        var title = Ui.Text(month.ToString("MMMM yyyy", CultureInfo.InvariantCulture), 13.5, FontWeights.Bold);
        title.HorizontalAlignment = HorizontalAlignment.Center;
        title.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(title, 1);
        Grid.SetColumn(next, 2);
        head.Children.Add(prev);
        head.Children.Add(title);
        head.Children.Add(next);
        stack.Children.Add(head);

        var grid = new UniformGrid { Columns = 7 };
        foreach (var w in new[] { "M", "T", "W", "T", "F", "S", "S" })
        {
            var wd = Ui.Text(w, 11, FontWeights.SemiBold, "Z.Faint");
            wd.HorizontalAlignment = HorizontalAlignment.Center;
            wd.Margin = new Thickness(0, 4, 0, 4);
            grid.Children.Add(wd);
        }
        foreach (var (day, inMonth, has) in TaskBoard.CalendarDays(month, tasks, holding))
        {
            var picked = m.Filter.Day == day;
            var isToday = day == today;
            var num = Ui.Text(day.Day.ToString(CultureInfo.InvariantCulture), 13, isToday ? FontWeights.Bold : FontWeights.Normal,
                picked ? "Z.OnInv" : isToday ? "Z.Orange" : inMonth ? "Z.Ink" : "Z.Faint");
            num.HorizontalAlignment = HorizontalAlignment.Center;
            num.VerticalAlignment = VerticalAlignment.Center;
            var cell = new Grid();
            cell.Children.Add(num);
            if (has)
            {
                var dot = Ui.Dot(4, picked ? "Z.OnInv" : "Z.Muted");
                dot.VerticalAlignment = VerticalAlignment.Bottom;
                dot.HorizontalAlignment = HorizontalAlignment.Center;
                dot.Margin = new Thickness(0, 0, 0, 4);
                cell.Children.Add(dot);
            }
            var box = new Border { Height = 32, CornerRadius = new CornerRadius(8), Margin = new Thickness(1), Child = cell }
                .Res(Border.BackgroundProperty, picked ? "Z.Inv" : "Z.Surface");
            if (!picked)
            {
                box.MouseEnter += (_, _) => box.SetResourceReference(Border.BackgroundProperty, "Z.Fill");
                box.MouseLeave += (_, _) => box.SetResourceReference(Border.BackgroundProperty, "Z.Surface");
            }
            var d = day;
            var b = new Button { Content = box, Cursor = Cursors.Hand, Style = Bare(), FocusVisualStyle = (Style)Application.Current.FindResource("Z.FocusRing") };
            if (picked || (m.Filter.Day is null && isToday))
            {
                b.Tag = FocusFirst;
            }
            b.Click += (_, _) => m.PickDay(d);
            AutomationProperties.SetName(b, day.ToString("dddd d MMMM", CultureInfo.InvariantCulture) + (has ? ", has tasks" : ""));
            grid.Children.Add(b);
        }
        stack.Children.Add(grid);

        var foot = new UniformGrid { Columns = 3, Margin = new Thickness(0, 8, 0, 0) };
        foreach (var (label, day) in new (string, DateOnly?)[] { ("Today", today), ("Tomorrow", today.AddDays(1)), ("Any day", null) })
        {
            var t = Ui.Text(label, 12.5, FontWeights.SemiBold);
            t.HorizontalAlignment = HorizontalAlignment.Center;
            t.VerticalAlignment = VerticalAlignment.Center;
            var box = new Border { Height = 30, CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), Child = t, Margin = new Thickness(3, 0, 3, 0) }
                .Res(Border.BackgroundProperty, "Z.Surface").Res(Border.BorderBrushProperty, "Z.Line2");
            var b = new Button { Content = box, Cursor = Cursors.Hand, Style = Bare(), FocusVisualStyle = (Style)Application.Current.FindResource("Z.FocusRing") };
            b.Click += (_, _) => m.PickDay(day);
            AutomationProperties.SetName(b, label);
            foot.Children.Add(b);
        }
        stack.Children.Add(foot);
        return PopCard(stack, 264, HorizontalAlignment.Left);
    }

    private static Button MonthButton(string glyph, string aria, Action run)
    {
        var t = Ui.Text(glyph, 16, FontWeights.SemiBold, "Z.Ink2");
        t.HorizontalAlignment = HorizontalAlignment.Center;
        t.VerticalAlignment = VerticalAlignment.Center;
        t.Margin = new Thickness(0, -2, 0, 0);
        var box = new Border { Width = 28, Height = 28, CornerRadius = new CornerRadius(7), Child = t }.Res(Border.BackgroundProperty, "Z.Surface");
        box.MouseEnter += (_, _) => box.SetResourceReference(Border.BackgroundProperty, "Z.Fill");
        box.MouseLeave += (_, _) => box.SetResourceReference(Border.BackgroundProperty, "Z.Surface");
        var b = new Button { Content = box, Cursor = Cursors.Hand, Style = Bare(), FocusVisualStyle = (Style)Application.Current.FindResource("Z.FocusRing") };
        b.Click += (_, _) => run();
        AutomationProperties.SetName(b, aria);
        return b;
    }

    /// <summary>Done's menu: Everything done, then By status with a colour dot and how many each lists.</summary>
    private static Border StatusMenu(TrayModel m, IReadOnlyList<MemberTask> tasks, DateTimeOffset now, IReadOnlySet<int> holding)
    {
        var counts = TaskBoard.MenuCounts(tasks, now, holding, m.HeldWas);
        var stack = new StackPanel();
        AutomationProperties.SetName(stack, "Show by status");
        foreach (var (status, count) in counts)
        {
            if (status == TaskBoard.MenuStatuses[0])
            {
                stack.Children.Add(new Border { Height = 1, Margin = new Thickness(2, 4, 2, 4) }.Res(Border.BackgroundProperty, "Z.Line"));
                var mh = Ui.Text("By status", 11, FontWeights.SemiBold, "Z.Faint");
                mh.Margin = new Thickness(8, 4, 8, 4);
                stack.Children.Add(mh);
            }
            stack.Children.Add(MenuRow(m, status, count));
        }
        var card = PopCard(stack, 220, HorizontalAlignment.Right);
        card.Padding = new Thickness(6);
        return card;
    }

    private static Button MenuRow(TrayModel m, TaskStatusKey? status, int count)
    {
        var picked = m.Filter.Status == status;
        var grid = new Grid { Margin = new Thickness(8, 7, 8, 7) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        if (picked)
        {
            grid.Children.Add(Ui.Glyph(Ui.Icon.Check, 13, "Z.Ink", 2.6));
        }
        if (status is { } s)
        {
            var dot = Ui.Dot(8, TaskBoard.StatusDot(s));
            dot.Margin = new Thickness(0, 0, 8, 0);
            dot.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(dot, 1);
            grid.Children.Add(dot);
        }
        var name = status is { } st ? TaskBoard.StatusName(st) : "Everything done";
        var label = Ui.Text(name, 13, picked ? FontWeights.SemiBold : FontWeights.Normal);
        label.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(label, 2);
        grid.Children.Add(label);
        // "Everything done" counts what was ticked today, like the Done segment, and says so.
        var countText = status is null ? $"{count} today" : count.ToString(CultureInfo.InvariantCulture);
        var n = Ui.Text(countText, 13, fg: "Z.Faint");
        n.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(n, 3);
        grid.Children.Add(n);
        var box = new Border { CornerRadius = new CornerRadius(7), Child = grid }.Res(Border.BackgroundProperty, "Z.Surface");
        box.MouseEnter += (_, _) => box.SetResourceReference(Border.BackgroundProperty, "Z.Fill");
        box.MouseLeave += (_, _) => box.SetResourceReference(Border.BackgroundProperty, "Z.Surface");
        var b = new Button
        {
            Content = box, Cursor = Cursors.Hand, Style = Bare(), HorizontalContentAlignment = HorizontalAlignment.Stretch,
            FocusVisualStyle = (Style)Application.Current.FindResource("Z.FocusRing"),
        };
        b.Click += (_, _) => m.PickStatus(status);
        if (picked)
        {
            b.Tag = FocusFirst;
        }
        AutomationProperties.SetName(b, $"{name}, {countText}");
        AutomationProperties.SetItemStatus(b, picked ? "checked" : "");
        return b;
    }

    // ---- Sections -------------------------------------------------------------------------------

    // The line between Done and its ⌄ on the selected (black) filter.
    private static readonly Brush OnInkRule = Frozen(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF));

    private static SolidColorBrush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    /// <summary>"Today  Wed 4 Nov  3": the name in ink, the date grey, the count lighter grey.</summary>
    private void AddHeading(string name, string sub, int count, Func<FrameworkElement>? action)
    {
        var heading = Heading();
        Rows.Children.Add(heading);
        _headings.Add((heading, Heading));
        return;

        FrameworkElement Heading()
        {
            var row = new DockPanel { LastChildFill = false, MinHeight = 34, Margin = new Thickness(4, 12, 0, 4) };
            var title = Ui.Text(name, 13, FontWeights.SemiBold);
            title.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(title);
            if (sub.Length > 0)
            {
                var date = Ui.Text(sub, 12, fg: "Z.Muted");
                date.VerticalAlignment = VerticalAlignment.Center;
                date.Margin = new Thickness(6, 1, 0, 0);
                row.Children.Add(date);
            }
            var n = Ui.Text(count.ToString(CultureInfo.InvariantCulture), 12, fg: "Z.Faint");
            n.VerticalAlignment = VerticalAlignment.Center;
            n.Margin = new Thickness(6, 1, 0, 0);
            row.Children.Add(n);
            if (action?.Invoke() is { } a)
            {
                DockPanel.SetDock(a, Dock.Right);
                row.Children.Add(a);
            }
            AutomationProperties.SetName(row, sub.Length > 0 ? $"{name}, {sub}, {count}" : $"{name}, {count}");
            return new Border { Child = row }.Res(Border.BackgroundProperty, "Z.Fill");
        }
    }

    private void OnListScrolled(object sender, ScrollChangedEventArgs e)
    {
        UpdatePinned();
        if (e.VerticalChange != 0)
        {
            _hold?.Cancel(); // a scroll is not a press and hold
            Model?.CloseMenu(); // a ⋯ menu stays with its task: scrolling closes it
        }
    }

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

    private FrameworkElement Row(MemberTask t, TrayModel m, IReadOnlySet<int> holding)
    {
        var now = m.Now;
        var held = holding.Contains(t.Id);
        var ticked = TaskBoard.Ticked(t);
        // Done (own, approved) ticks black and strikes through; an owner's task waiting for the OK ticks grey and keeps its title.
        var struck = ticked && t.Status != MemberTaskStatus.Review;
        var open = m.OpenCard == t.Id;

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var check = Check(t, m, ticked, holding);
        grid.Children.Add(check);

        var title = new TextBlock
        {
            Text = t.Title, FontSize = 14, FontWeight = FontWeights.Medium, TextWrapping = TextWrapping.Wrap, LineHeight = 19.5,
            TextDecorations = struck ? TextDecorations.Strikethrough : null,
        }.Res(TextBlock.ForegroundProperty, struck ? "Z.Faint" : ticked ? "Z.Ink2" : "Z.Ink");
        var titleButton = new Button
        {
            Content = title, Cursor = Cursors.Hand, HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(0, 2, 0, 2),
            Template = Plain(), FocusVisualStyle = (Style)Application.Current.FindResource("Z.FocusRing"),
        };
        titleButton.Click += (_, _) => m.ToggleCard(t.Id);
        AutomationProperties.SetName(titleButton, (open ? "Hide details: " : "Show details: ") + t.Title);
        AutomationProperties.SetHelpText(titleButton, "Press and hold for the task's card");
        Grid.SetColumn(titleButton, 1);
        grid.Children.Add(titleButton);

        var more = MoreButton(t, m);
        Grid.SetColumn(more, 2);
        grid.Children.Add(more);
        _moreButtons[t.Id] = more;

        var (meta, note) = Meta(t, m, now, held);
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

        var tile = new Border
        {
            Child = grid, Padding = new Thickness(2, 8, 0, 8), CornerRadius = new CornerRadius(8), Background = Brushes.Transparent,
            SnapsToDevicePixels = true, RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new ScaleTransform(1, 1),
        };
        AttachHold(tile, t.Id, m, [check, note, more]);
        // A white card with a hairline and a soft lift (a second shape, not an Effect, so the text keeps its ClearType).
        var card = new Border
        {
            Child = tile, CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1), Padding = new Thickness(6, 2, 4, 2),
            SnapsToDevicePixels = true,
        }.Res(Border.BackgroundProperty, "Z.Surface").Res(Border.BorderBrushProperty, "Z.Line");
        var lifted = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        lifted.Children.Add(new Border { CornerRadius = new CornerRadius(12), Margin = new Thickness(0, 1, 0, -1), Background = Lift, IsHitTestVisible = false });
        lifted.Children.Add(card);
        return lifted;
    }

    private static readonly Brush Lift = Frozen(Color.FromArgb(0x0F, 0x14, 0x12, 0x10));

    /// <summary>
    /// The round tick: an outline while open, black when done, mid-grey while an owner's task waits for the OK.
    /// Done is final: on a ticked task it opens Add a note.
    /// </summary>
    private static Button Check(MemberTask t, TrayModel m, bool ticked, IReadOnlySet<int> holding)
    {
        var circle = new Border
        {
            Width = 19, Height = 19, CornerRadius = new CornerRadius(9.5), BorderThickness = new Thickness(1.5),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        };
        var waiting = t.Status == MemberTaskStatus.Review;
        var fill = !ticked ? "Z.Surface" : waiting ? "Z.Muted" : "Z.Inv";
        circle.SetResourceReference(Border.BackgroundProperty, fill);
        circle.SetResourceReference(Border.BorderBrushProperty, ticked ? fill : "Z.Line2");
        if (ticked)
        {
            var mark = new Path
            {
                Data = Geometry.Parse("M2,6.5 L4.5,9 L10,3.5"), StrokeThickness = 2.2, Width = 10, Height = 10, Stretch = Stretch.Uniform,
                StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            };
            mark.SetResourceReference(Shape.StrokeProperty, waiting ? "Z.Surface" : "Z.OnInv");
            circle.Child = mark;
            circle.BorderThickness = new Thickness(0); // a clean filled disc
        }
        var action = TaskBoard.TickActionOf(t, holding);
        var b = new Button
        {
            Content = circle, Width = 40, Height = 40, Margin = new Thickness(-10, -9, -2, -9), VerticalAlignment = VerticalAlignment.Top,
            Cursor = action == TickAction.None ? Cursors.Arrow : Cursors.Hand, Template = Plain(), IsEnabled = action != TickAction.None,
            FocusVisualStyle = (Style)Application.Current.FindResource("Z.FocusRing"),
        };
        if (!ticked)
        {
            b.MouseEnter += (_, _) => circle.SetResourceReference(Border.BorderBrushProperty, "Z.Ink2");
            b.MouseLeave += (_, _) => circle.SetResourceReference(Border.BorderBrushProperty, "Z.Line2");
        }
        b.Click += (_, _) => m.ToggleTick(t);
        AutomationProperties.SetName(b, (action == TickAction.AddNote ? "Done. Add a note to: " : "Mark done: ") + t.Title);
        AutomationProperties.SetItemStatus(b, ticked ? "done" : "open");
        return b;
    }

    /// <summary>The line under the title: the time, its status chip, steps, priority, Add a note.</summary>
    private static (WrapPanel Meta, Button? Note) Meta(MemberTask t, TrayModel m, DateTimeOffset now, bool held)
    {
        var meta = new WrapPanel { Margin = new Thickness(0, 2, 0, 0) };
        void Add(FrameworkElement el)
        {
            el.Margin = new Thickness(0, 0, 10, 3);
            el.VerticalAlignment = VerticalAlignment.Center;
            meta.Children.Add(el);
        }

        // Under a dated heading only the time shows; what's late or due within the hour gets a small orange dot.
        if (TaskBoard.RowDue(t, now) is { Text.Length: > 0 } due)
        {
            if (due.Urgent)
            {
                var line = new StackPanel { Orientation = Orientation.Horizontal };
                var dot = Ui.Dot(6, "Z.Orange");
                dot.Margin = new Thickness(0, 1, 5, 0);
                line.Children.Add(dot);
                line.Children.Add(Ui.Text(due.Text, 12, FontWeights.SemiBold));
                Add(line);
            }
            else
            {
                Add(Ui.Text(due.Text, 12, fg: "Z.Ink2"));
            }
        }
        if (TaskBoard.StatusChip(t, m.OwnerFirst) is { } chip)
        {
            Add(Chip(chip.Label, chip.Tone));
        }
        if (t.Steps.Count > 0)
        {
            Add(Ui.Text($"{t.Steps.Count(s => s.Done)}/{t.Steps.Count} steps", 12, fg: "Z.Ink2"));
        }
        if (t.High && !TaskBoard.Ticked(t))
        {
            Add(Ui.Text("High", 12, FontWeights.SemiBold, "Z.RedText"));
        }
        if (TaskBoard.Finished(t) && t.Status != MemberTaskStatus.Cancelled && t.DoneAt is { } doneAt && !held)
        {
            var when = TaskCard.Sent(doneAt, now);
            Add(Ui.Text("Done " + (when == "Yesterday" ? "yesterday" : when), 12, fg: "Z.Ink2"));
        }
        if (TaskBoard.WaitsToSend(t) && m.Offline)
        {
            Add(Ui.Text("Saved on this PC", 12, fg: "Z.Ink2"));
        }
        Button? note = null;
        // Done is final: a task just ticked (and every task in Done) offers Add a note, never Undo.
        if ((held || (m.View == TaskView.Done && TaskBoard.Ticked(t))) && TaskBoard.TickActionOf(t) == TickAction.AddNote)
        {
            var text = Ui.Text("Add a note", 12, FontWeights.SemiBold);
            note = new Button
            {
                Content = text, Cursor = Cursors.Hand, Template = Plain(), Padding = new Thickness(0, 4, 0, 4), Margin = new Thickness(0, -4, 0, -4),
                FocusVisualStyle = (Style)Application.Current.FindResource("Z.FocusRing"),
            };
            note.MouseEnter += (_, _) => text.TextDecorations = TextDecorations.Underline;
            note.MouseLeave += (_, _) => text.TextDecorations = null;
            note.Click += (_, _) => m.OpenNote(t.Id);
            AutomationProperties.SetName(note, "Add a note: " + t.Title);
            Add(note);
        }
        return (meta, note);
    }

    /// <summary>"(V) From Vishal": the owner's initial in a small black disc.</summary>
    internal static StackPanel From(string owner)
    {
        var from = new StackPanel { Orientation = Orientation.Horizontal };
        var initial = Ui.Text(owner.Length > 0 ? owner[..1].ToUpperInvariant() : "?", 9, FontWeights.Bold, "Z.OnInv");
        initial.HorizontalAlignment = HorizontalAlignment.Center;
        initial.VerticalAlignment = VerticalAlignment.Center;
        from.Children.Add(new Border { Width = 15, Height = 15, CornerRadius = new CornerRadius(7.5), Child = initial, Margin = new Thickness(0, 0, 4, 0) }
            .Res(Border.BackgroundProperty, "Z.Inv"));
        from.Children.Add(Ui.Text("From " + owner, 12, fg: "Z.Muted"));
        return from;
    }

    /// <summary>A status chip: the status's soft tint with its text colour (<c>Z.St{Tone}Text</c> / <c>Tint</c>).</summary>
    internal static Border Chip(string label, string tone, double size = 12) => new Border
    {
        CornerRadius = new CornerRadius(5), Padding = new Thickness(6, 1, 6, 1),
        Child = Ui.Text(label, size, FontWeights.SemiBold, $"Z.St{tone}Text"),
    }.Res(Border.BackgroundProperty, $"Z.St{tone}Tint");

    /// <summary>⋯ opens the task's menu (Mark done, Open details, …); press and hold the card for its details card.</summary>
    private static Button MoreButton(MemberTask t, TrayModel m)
    {
        var dots = new GeometryGroup();
        foreach (var x in new[] { 3.5, 8, 12.5 })
        {
            dots.Children.Add(new EllipseGeometry(new Point(x, 8), 1.2, 1.2));
        }
        var glyph = new Path { Data = dots, Width = 16, Height = 16 };
        glyph.SetResourceReference(Shape.FillProperty, "Z.Muted");
        var b = new Button
        {
            Content = glyph, Width = 36, Height = 36, Margin = new Thickness(0, -8, 0, -8), VerticalAlignment = VerticalAlignment.Top,
            Style = (Style)Application.Current.FindResource("Z.IconBtn"),
        };
        AutomationProperties.SetName(b, "More for " + t.Title);
        AutomationProperties.SetItemStatus(b, m.RowMenu == t.Id ? "expanded" : "collapsed");
        b.ToolTip = "More";
        b.Click += (_, _) => m.ToggleRowMenu(t.Id);
        return b;
    }

    // ---- Press and hold ------------------------------------------------------------------------

    private Hold? _hold;

    /// <summary>
    /// Press and hold the row (not its tick or Add a note) about half a second: it sinks slightly from
    /// <see cref="HoldGesture.SinkAfter"/>, and the details card opens at <see cref="HoldGesture.OpenAfter"/>. The
    /// click that ends a hold that opened the card is swallowed, so the title doesn't also toggle.
    /// </summary>
    private void AttachHold(Border tile, int id, TrayModel m, UIElement?[] skip)
    {
        tile.PreviewMouseLeftButtonDown += (_, e) =>
        {
            _hold?.Cancel();
            if (e.OriginalSource is DependencyObject src && skip.OfType<UIElement>().Any(s => IsInside(src, s)))
            {
                return;
            }
            _hold = new Hold(tile, e.GetPosition(tile), () => m.OpenDetail(id));
        };
        tile.PreviewMouseMove += (_, e) =>
        {
            if (_hold is { Tile: var held } h && held == tile && h.Moved(e.GetPosition(tile)))
            {
                h.Cancel();
            }
        };
        tile.PreviewMouseLeftButtonUp += (_, e) =>
        {
            if (_hold is { } h && h.Tile == tile)
            {
                if (h.Opened)
                {
                    e.Handled = true; // the card is open: no click on what was under the finger
                    Mouse.Capture(null);
                }
                h.Cancel();
                _hold = null;
            }
        };
        tile.MouseLeave += (_, _) =>
        {
            if (_hold is { Opened: false } h && h.Tile == tile)
            {
                h.Cancel();
            }
        };
        tile.PreviewKeyDown += (_, _) => _hold?.Cancel();
    }

    private static bool IsInside(DependencyObject node, UIElement ancestor)
    {
        for (var d = node; d is not null; d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
        {
            if (d == ancestor)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>One press on a row: a 30 ms clock walks it through <see cref="HoldGesture.Stage"/>.</summary>
    private sealed class Hold
    {
        private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(30) };
        private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
        private readonly HoldGesture.Press _press;
        private readonly Action _open;

        public Hold(Border tile, Point start, Action open)
        {
            Tile = tile;
            _press = new HoldGesture.Press(start.X, start.Y);
            _open = open;
            _timer.Tick += (_, _) => Step();
            _timer.Start();
        }

        public Border Tile { get; }
        public bool Opened => _press.Opened;

        public bool Moved(Point at) => _press.Move(at.X, at.Y);

        /// <summary>The list was drawn again: a press that hasn't opened the card yet ends here.</summary>
        public void Redrawn()
        {
            _press.Redrawn();
            if (!_press.Live)
            {
                Cancel();
            }
        }

        private void Step()
        {
            switch (_press.Step(_clock.Elapsed))
            {
                case null:
                    Cancel();
                    break;
                case HoldGesture.Stage.Sinking:
                    Sink(true);
                    break;
                case HoldGesture.Stage.Open:
                    _timer.Stop();
                    Sink(false);
                    _open();
                    break;
            }
        }

        private void Sink(bool on)
        {
            if (Tile.RenderTransform is ScaleTransform s && !s.IsFrozen)
            {
                s.ScaleX = s.ScaleY = on ? HoldGesture.SinkScale : 1;
            }
            if (on)
            {
                Tile.SetResourceReference(Border.BackgroundProperty, "Z.Fill");
            }
            else
            {
                Tile.Background = Brushes.Transparent;
            }
        }

        public void Cancel()
        {
            _press.Cancel();
            _timer.Stop();
            if (!Opened)
            {
                Sink(false);
            }
        }
    }

    private static Style? _bare, _bareCentered;

    /// <summary>
    /// A button that draws only its content and fills its slot: <see cref="Plain"/> plus the sizes the app's Fluent
    /// theme (ThemeMode="Light") would otherwise give every Button (left-aligned, 11 px padding, a 32 px minimum).
    /// </summary>
    internal static Style Bare(bool center = false)
    {
        ref var cached = ref center ? ref _bareCentered : ref _bare;
        if (cached is null)
        {
            var s = new Style(typeof(Button));
            s.Setters.Add(new Setter(Control.TemplateProperty, Plain(center ? HorizontalAlignment.Center : HorizontalAlignment.Stretch)));
            s.Setters.Add(new Setter(HorizontalAlignmentProperty, HorizontalAlignment.Stretch));
            s.Setters.Add(new Setter(VerticalAlignmentProperty, VerticalAlignment.Stretch));
            s.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(0)));
            s.Setters.Add(new Setter(MinHeightProperty, 0.0));
            s.Setters.Add(new Setter(MinWidthProperty, 0.0));
            s.Setters.Add(new Setter(FocusVisualStyleProperty, Application.Current.FindResource("Z.FocusRing")));
            cached = s;
        }
        return cached;
    }

    /// <summary>A button that draws only its content (the focus ring still shows for the keyboard).</summary>
    internal static ControlTemplate Plain(HorizontalAlignment content = HorizontalAlignment.Stretch)
    {
        var t = new ControlTemplate(typeof(Button));
        var grid = new FrameworkElementFactory(typeof(Grid));
        grid.SetValue(Panel.BackgroundProperty, Brushes.Transparent);
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(FrameworkElement.HorizontalAlignmentProperty, content);
        presenter.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        presenter.SetValue(FrameworkElement.MarginProperty, new TemplateBindingExtension(Control.PaddingProperty));
        grid.AppendChild(presenter);
        t.VisualTree = grid;
        return t;
    }
}

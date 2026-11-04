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
/// Tasks (F-16), built like the to-do apps people know: the day and Add task, four views below, every task under a
/// real date. The due words come from the clock at each render (TaskBoard), so yesterday's "Tomorrow" reads Today after
/// midnight. A tick never undoes itself: the task stays in place, struck through with Undo, then moves to Done; an
/// owner's task stays ticked (grey) with its "Waiting for {owner}'s OK" chip. Content is black and greys; colour is
/// kept for the status chips, the open view's count and a small dot on what is late or due soon.
/// </summary>
public partial class TasksView : UserControl
{
    private static readonly (TaskView View, string Label)[] ViewNames =
        [(TaskView.Today, "Today"), (TaskView.Upcoming, "Upcoming"), (TaskView.All, "All"), (TaskView.Done, "Done")];

    private readonly Dictionary<TaskView, RadioButton> _views = [];
    // The section headings drawn this render, top to bottom, for the pinned copy at the top of the list.
    private readonly List<(FrameworkElement Heading, Func<FrameworkElement> Copy)> _headings = [];

    public TasksView()
    {
        InitializeComponent();
        foreach (var (view, _) in ViewNames)
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
        // "Wednesday, 4 Nov · 4:03 PM": the weekday in ink, the rest grey.
        Clock.Inlines.Clear();
        Clock.Inlines.Add(new System.Windows.Documents.Run(now.ToLocalTime().ToString("dddd", System.Globalization.CultureInfo.InvariantCulture))
        {
            FontWeight = FontWeights.SemiBold, Foreground = Ui.B("Z.Ink"),
        });
        Clock.Inlines.Add(new System.Windows.Documents.Run(now.ToLocalTime().ToString(", d MMM", System.Globalization.CultureInfo.InvariantCulture)
            + " · " + TaskBoard.TimeLabel(now)));

        foreach (var (view, label) in ViewNames)
        {
            var count = TaskBoard.Count(tasks, view, now, holding);
            var on = m.View == view;
            var content = new StackPanel { Orientation = Orientation.Horizontal };
            content.Children.Add(new TextBlock { Text = label });
            if (count > 0)
            {
                var n = Ui.Text(count.ToString(), 11, on ? FontWeights.SemiBold : FontWeights.Normal, on ? "Z.Orange" : "Z.Faint");
                n.Margin = new Thickness(3, 1, 0, 0);
                n.VerticalAlignment = VerticalAlignment.Center;
                content.Children.Add(n);
            }
            var b = _views[view];
            b.Content = content;
            b.IsChecked = on;
            AutomationProperties.SetName(b, view == TaskView.Done ? $"{label}, {count} today" : $"{label}, {count}");
        }

        Rows.Children.Clear();
        _headings.Clear();
        var sections = TaskBoard.Sections(tasks, m.View, now, holding);
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
        (EmptyTitle.Text, EmptyText.Text) = TaskBoard.EmptyText(m.View);
        Dispatcher.BeginInvoke(UpdatePinned, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>Add task opens the Add a to-do sheet (from Upcoming it starts on Tomorrow).</summary>
    private void OnAdd(object sender, RoutedEventArgs e) => Model?.OpenNewTask();

    // ---- Sections -------------------------------------------------------------------------------

    /// <summary>"Today  Wed 4 Nov  3": the name in ink, the date grey, the count lighter grey.</summary>
    private void AddHeading(string name, string sub, int count, Func<FrameworkElement>? action)
    {
        var heading = Heading();
        Rows.Children.Add(heading);
        _headings.Add((heading, Heading));
        return;

        FrameworkElement Heading()
        {
            var row = new DockPanel { LastChildFill = false, MinHeight = 34, Margin = new Thickness(2, 10, 0, 0) };
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
            var n = Ui.Text(count.ToString(), 12, fg: "Z.Faint");
            n.VerticalAlignment = VerticalAlignment.Center;
            n.Margin = new Thickness(6, 1, 0, 0);
            row.Children.Add(n);
            if (action?.Invoke() is { } a)
            {
                DockPanel.SetDock(a, Dock.Right);
                row.Children.Add(a);
            }
            AutomationProperties.SetName(row, sub.Length > 0 ? $"{name}, {sub}, {count}" : $"{name}, {count}");
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

        grid.Children.Add(Check(t, m, ticked));

        var title = new TextBlock
        {
            Text = t.Title, FontSize = 14, TextWrapping = TextWrapping.Wrap, LineHeight = 19.5,
            TextDecorations = struck ? TextDecorations.Strikethrough : null,
        }.Res(TextBlock.ForegroundProperty, struck ? "Z.Faint" : ticked ? "Z.Ink2" : "Z.Ink");
        var titleButton = new Button
        {
            Content = title, Cursor = Cursors.Hand, HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(0, 2, 0, 2),
            Template = Plain(), FocusVisualStyle = (Style)Application.Current.FindResource("Z.FocusRing"),
        };
        titleButton.Click += (_, _) => m.ToggleCard(t.Id);
        AutomationProperties.SetName(titleButton, (open ? "Hide details: " : "Show details: ") + t.Title);
        Grid.SetColumn(titleButton, 1);
        grid.Children.Add(titleButton);

        var more = MoreButton(t, m);
        Grid.SetColumn(more, 2);
        grid.Children.Add(more);

        var meta = Meta(t, m, now, held);
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
            Child = grid, Padding = new Thickness(2, 8, 0, 8), BorderThickness = new Thickness(0, 0, 0, 1), SnapsToDevicePixels = true,
        }.Res(Border.BorderBrushProperty, "Z.Line");
    }

    /// <summary>The round tick: an outline while open, black when done, mid-grey while an owner's task waits for the OK.</summary>
    private static Button Check(MemberTask t, TrayModel m, bool ticked)
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
        var closed = !t.Mine && t.Status is MemberTaskStatus.Approved or MemberTaskStatus.Cancelled;
        var b = new Button
        {
            Content = circle, Width = 40, Height = 40, Margin = new Thickness(-10, -9, -2, -9), VerticalAlignment = VerticalAlignment.Top,
            Cursor = closed ? Cursors.Arrow : Cursors.Hand, Template = Plain(), IsEnabled = !closed,
            FocusVisualStyle = (Style)Application.Current.FindResource("Z.FocusRing"),
        };
        if (!ticked)
        {
            b.MouseEnter += (_, _) => circle.SetResourceReference(Border.BorderBrushProperty, "Z.Ink2");
            b.MouseLeave += (_, _) => circle.SetResourceReference(Border.BorderBrushProperty, "Z.Line2");
        }
        b.Click += (_, _) => m.ToggleTick(t);
        AutomationProperties.SetName(b, (ticked ? "Mark not done: " : "Mark done: ") + t.Title);
        AutomationProperties.SetItemStatus(b, ticked ? "done" : "open");
        return b;
    }

    /// <summary>The line under the title: the time, who it's from, its status chip, steps, priority, Undo.</summary>
    private static WrapPanel Meta(MemberTask t, TrayModel m, DateTimeOffset now, bool held)
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
                Add(Ui.Text(due.Text, 12, fg: "Z.Muted"));
            }
        }
        if (!t.Mine)
        {
            var owner = m.OwnerFirst;
            var from = new StackPanel { Orientation = Orientation.Horizontal };
            var initial = Ui.Text(owner.Length > 0 ? owner[..1].ToUpperInvariant() : "?", 9, FontWeights.Bold, "Z.OnInv");
            initial.HorizontalAlignment = HorizontalAlignment.Center;
            initial.VerticalAlignment = VerticalAlignment.Center;
            from.Children.Add(new Border { Width = 15, Height = 15, CornerRadius = new CornerRadius(7.5), Child = initial, Margin = new Thickness(0, 0, 4, 0) }
                .Res(Border.BackgroundProperty, "Z.Inv"));
            from.Children.Add(Ui.Text("From " + owner, 12, fg: "Z.Muted"));
            Add(from);
        }
        if (TaskBoard.StatusChip(t, m.OwnerFirst) is { } chip)
        {
            Add(new Border
            {
                CornerRadius = new CornerRadius(5), Padding = new Thickness(6, 1, 6, 1),
                Child = Ui.Text(chip.Label, 12, FontWeights.SemiBold, $"Z.St{chip.Tone}Text"),
            }.Res(Border.BackgroundProperty, $"Z.St{chip.Tone}Tint"));
        }
        if (t.Steps.Count > 0)
        {
            Add(Ui.Text($"{t.Steps.Count(s => s.Done)}/{t.Steps.Count} steps", 12, fg: "Z.Muted"));
        }
        if (t.High && !TaskBoard.Ticked(t))
        {
            Add(Ui.Text("High", 12, FontWeights.SemiBold, "Z.RedText"));
        }
        if (TaskBoard.Finished(t) && t.Status != MemberTaskStatus.Cancelled && t.DoneAt is { } doneAt && !held)
        {
            var when = TaskCard.Sent(doneAt, now);
            Add(Ui.Text("Done " + (when == "Yesterday" ? "yesterday" : when), 12, fg: "Z.Muted"));
        }
        if (TaskBoard.WaitsToSend(t) && m.Offline)
        {
            Add(Ui.Text("Saved on this PC", 12, fg: "Z.Muted"));
        }
        if (held)
        {
            var text = Ui.Text("Undo", 12, FontWeights.SemiBold);
            text.TextDecorations = TextDecorations.Underline;
            var undo = new Button
            {
                Content = text, Cursor = Cursors.Hand, Template = Plain(), Padding = new Thickness(0, 4, 0, 4), Margin = new Thickness(0, -4, 0, -4),
                FocusVisualStyle = (Style)Application.Current.FindResource("Z.FocusRing"),
            };
            undo.Click += (_, _) => m.ToggleTick(t);
            AutomationProperties.SetName(undo, "Undo: " + t.Title);
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
}

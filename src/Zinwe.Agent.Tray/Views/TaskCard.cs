using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Shapes;
using Zinwe.Agent.Tray.Widget;

namespace Zinwe.Agent.Tray.Views;

/// <summary>
/// The task card (Tasks tab and inside Chat): a tick on the member's own to-dos, the title, "Doing · Due in 12 min ·
/// 1/4 · Log", one quick action while closed, and a chevron that opens details, steps, notes and the actions.
/// The Log text opens what happened on the task.
/// </summary>
internal static class TaskCard
{
    // The Tasks tab rebuilds its cards every tick: the Log panel may only slide in once, when it opens.
    private static int? _logShown;

    public static FrameworkElement Build(MemberTask t, TrayModel m, bool open, double? width = null)
    {
        var now = m.Now;
        var owner = m.OwnerFirst;
        var lane = TaskBoard.LaneOf(t);
        var finished = TaskBoard.Finished(t);
        var stKey = lane switch
        {
            TaskLane.Doing => "Z.StDoing",
            TaskLane.Waiting => "Z.StWait",
            TaskLane.Done => "Z.StDone",
            _ => "Z.Muted",
        };
        var inChat = width is not null;

        var stack = new StackPanel();
        var head = new Grid();
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition());
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        if (t.Mine)
        {
            var tick = new ToggleButton
            {
                IsChecked = finished, Width = 44, Height = 44, Margin = new Thickness(-12, -10, -6, -10), Cursor = System.Windows.Input.Cursors.Hand,
                Template = TickTemplate(), FocusVisualStyle = (Style)Application.Current.FindResource("Z.FocusRing"),
            };
            AutomationProperties.SetName(tick, (finished ? "Mark not done: " : "Mark done: ") + t.Title);
            tick.Click += (_, _) =>
            {
                if (finished)
                {
                    m.ReopenTask(t);
                }
                else
                {
                    m.DoneTask(t);
                }
            };
            tick.VerticalAlignment = VerticalAlignment.Center;
            head.Children.Add(tick);
        }

        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var heading = new DockPanel { LastChildFill = true };
        if (t.IsNew && !finished)
        {
            var tag = Ui.Pill("New", "Z.OrangeTint", "Z.OrangeInk", 11, FontWeights.SemiBold);
            tag.Padding = new Thickness(5, 0, 5, 0);
            tag.Margin = new Thickness(6, 0, 0, 0);
            DockPanel.SetDock(tag, Dock.Right);
            heading.Children.Add(tag);
        }
        var title = Ui.Text(t.Title, 13.5, FontWeights.SemiBold, fg: finished ? "Z.Muted" : "Z.Ink", wrap: open || inChat, trim: !open && !inChat);
        title.ToolTip = t.Title;
        if (finished)
        {
            title.TextDecorations = TextDecorations.Strikethrough;
        }
        heading.Children.Add(title);
        texts.Children.Add(heading);
        var line = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 0) };
        line.Children.Add(Ui.Text(TaskBoard.LaneLabel(t, owner), 12, FontWeights.SemiBold, stKey));
        line.Children.Add(Ui.Text(" · ", 12, fg: "Z.Muted"));
        var tone = TaskBoard.DueToneOf(t, now);
        line.Children.Add(Ui.Text(TaskBoard.DueLabel(t, now), 12,
            tone == DueTone.Late ? FontWeights.Bold : tone == DueTone.Soon ? FontWeights.SemiBold : FontWeights.Normal,
            tone == DueTone.Late ? "Z.RedText" : tone == DueTone.Soon ? "Z.StDoing" : "Z.Muted"));
        if (t.Steps.Count > 0)
        {
            line.Children.Add(Ui.Text($" · {t.Steps.Count(s => s.Done)}/{t.Steps.Count}", 12, fg: "Z.Muted"));
        }
        var logOpen = m.OpenLog == t.Id;
        _logShown = m.OpenLog is null ? null : _logShown;
        var log = Ui.Button("Log", "Z.TextAction", () => m.ToggleLog(t.Id), (logOpen ? "Hide log: " : "Show log: ") + t.Title);
        log.Margin = new Thickness(4, -6, 0, -6);
        line.Children.Add(log);
        texts.Children.Add(line);
        if (t.Steps.Count > 0 && !open && !finished)
        {
            texts.Children.Add(Progress(t));
        }
        Grid.SetColumn(texts, 1);
        head.Children.Add(texts);

        if (!open && TaskBoard.Quick(t) is { } quick)
        {
            var q = Ui.Button(quick.Label, "Z.BtnSm.Pri", () => m.QuickRun(t), quick.Aria);
            q.Padding = new Thickness(10, 0, 10, 0);
            q.FontSize = 12;
            q.Margin = new Thickness(8, -6, 0, -6);
            q.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(q, 2);
            head.Children.Add(q);
        }

        var chevron = new Button
        {
            Width = 36, Height = 44, Margin = new Thickness(0, -6, -6, -6), Cursor = System.Windows.Input.Cursors.Hand,
            Template = ChevronTemplate(open), FocusVisualStyle = (Style)Application.Current.FindResource("Z.FocusRing"),
        };
        AutomationProperties.SetName(chevron, (open ? "Hide details: " : "Show details: ") + t.Title);
        chevron.Click += (_, _) => m.ToggleCard(t.Id);
        Grid.SetColumn(chevron, 3);
        head.Children.Add(chevron);
        stack.Children.Add(head);

        var myNote = t.Notes.LastOrDefault(n => n.By == "You");
        if (finished && !open && myNote is not null)
        {
            var note = Ui.Text("", 12, fg: "Z.Ink3", wrap: true);
            note.Inlines.Add(new System.Windows.Documents.Run("Your note: ") { FontWeight = FontWeights.SemiBold });
            note.Inlines.Add(new System.Windows.Documents.Run(myNote.Text));
            note.Margin = new Thickness(32, 6, 0, 0);
            stack.Children.Add(note);
        }

        if (open)
        {
            stack.Children.Add(Details(t, m, owner));
        }
        if (logOpen)
        {
            stack.Children.Add(LogPanel(t, m));
        }

        var card = new Border
        {
            CornerRadius = new CornerRadius(12), Padding = new Thickness(12, 10, 8, 10), Child = stack, SnapsToDevicePixels = true,
            BorderThickness = new Thickness(1),
        };
        if (width is { } w)
        {
            card.Width = w;
        }
        card.SetResourceReference(Border.BackgroundProperty, "Z.Surface");
        card.SetResourceReference(Border.BorderBrushProperty, open || logOpen ? "Z.Line2" : "Z.Line");
        return card;
    }

    /// <summary>The thin line under a closed task with steps: how far along it is.</summary>
    private static FrameworkElement Progress(MemberTask t)
    {
        var track = new Grid { Height = 3, Margin = new Thickness(0, 6, 0, 0) };
        track.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(t.Steps.Count(s => s.Done), 0.001), GridUnitType.Star) });
        track.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(t.Steps.Count(s => !s.Done), 0.001), GridUnitType.Star) });
        var done = new Border().Res(Border.BackgroundProperty, "Z.Progress");
        var left = new Border().Res(Border.BackgroundProperty, "Z.LineSoft");
        Grid.SetColumn(left, 1);
        track.Children.Add(done);
        track.Children.Add(left);
        return track;
    }

    /// <summary>What happened on the task: the Log text under its title opens this.</summary>
    private static StackPanel LogPanel(MemberTask t, TrayModel m)
    {
        var body = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        body.Children.Add(new Border { Height = 1, Margin = new Thickness(0, 0, 0, 8) }.Res(Border.BackgroundProperty, "Z.LineSoft"));
        if (t.Activity.Count == 0)
        {
            body.Children.Add(Ui.Text("Nothing logged yet.", 12, fg: "Z.Muted"));
        }
        foreach (var a in t.Activity.TakeLast(6))
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 3) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(66) });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            var when = Ui.Text(Sent(a.At, m.Now), 12, fg: "Z.Muted");
            when.SetResourceReference(TextBlock.FontFamilyProperty, "Z.Mono");
            var what = Ui.Text(a.Text, 12, fg: "Z.Ink2", wrap: true);
            Grid.SetColumn(what, 1);
            row.Children.Add(when);
            row.Children.Add(what);
            body.Children.Add(row);
        }
        if (t.RemoteId is not null)
        {
            var note = Ui.Button("Add a note", "Z.LinkBtn", () => m.OpenNote(t.Id), "Add a note: " + t.Title);
            note.MinHeight = 28;
            note.FontSize = 12;
            body.Children.Add(note);
        }
        if (_logShown != t.Id)
        {
            _logShown = t.Id;
            Motion.Arrive(body, 0, 6, 1, 160);
        }
        return body;
    }

    private static StackPanel Details(MemberTask t, TrayModel m, string owner)
    {
        var body = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        var rule = new Border { Height = 1, Margin = new Thickness(0, 0, 0, 8) };
        rule.SetResourceReference(Border.BackgroundProperty, "Z.LineSoft");
        body.Children.Add(rule);
        body.Children.Add(Ui.Text(t.Mine ? "Your own to-do · kept on this PC only"
            : $"From {owner} · {Sent(t.SentAt, m.Now)}{(t.NeedsOk ? $" · {owner} checks it when it’s done" : "")}", 12, fg: "Z.Muted", wrap: true));
        if (t.Details.Length > 0)
        {
            var d = Ui.Text(t.Details, 12.5, fg: "Z.Ink2", wrap: true);
            d.Margin = new Thickness(0, 8, 0, 0);
            body.Children.Add(d);
        }
        if (t.Steps.Count > 0)
        {
            var steps = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };
            var locked = TaskBoard.Locked(t);
            for (var i = 0; i < t.Steps.Count; i++)
            {
                var index = i;
                var s = t.Steps[i];
                steps.Children.Add(StepRow(s, locked, () => m.ToggleStep(t, index)));
            }
            body.Children.Add(steps);
        }
        foreach (var n in t.Notes)
        {
            var mine = n.By == "You";
            var text = Ui.Text("", 12, fg: mine ? "Z.Ink2" : "Z.InfoInk", wrap: true);
            text.Inlines.Add(new System.Windows.Documents.Run(n.By + ": ") { FontWeight = FontWeights.Bold });
            text.Inlines.Add(new System.Windows.Documents.Run(n.Text));
            var note = new Border { CornerRadius = new CornerRadius(10), Padding = new Thickness(10, 7, 10, 7), Margin = new Thickness(0, 8, 0, 0), Child = text };
            note.SetResourceReference(Border.BackgroundProperty, mine ? "Z.Bg" : "Z.InfoTint");
            body.Children.Add(note);
        }
        if (TaskBoard.Line(t, owner) is { Length: > 0 } line)
        {
            var l = Ui.Text(line, 12, fg: "Z.Ink3", wrap: true);
            l.Margin = new Thickness(0, 8, 0, 0);
            body.Children.Add(l);
        }
        if (t.Queued && m.Offline)
        {
            var q = Ui.Text("Saved on this PC · sends when you're back online", 12, FontWeights.SemiBold, "Z.WarnText", wrap: true);
            q.Margin = new Thickness(0, 8, 0, 0);
            body.Children.Add(q);
        }

        var actions = new List<(string Label, string Style, Action Run, bool Grow)>();
        var lane = TaskBoard.LaneOf(t);
        if (t.Mine)
        {
            actions.Add(("Edit", "Z.BtnSm", () => m.OpenNewTask(t), true));
            actions.Add(("Delete", "Z.BtnSm", () => m.DeleteOwn(t), false));
        }
        else if (lane == TaskLane.Todo && t.RemoteId is not null && TaskBoard.NeedsConfirm(t))
        {
            // An owner's task: confirm you'll do it (the owner sees "Accepted"), or start right away.
            actions.Add(("Confirm", "Z.BtnSm.Pri", () => m.ConfirmTask(t), true));
            actions.Add(("Start", "Z.BtnSm", () => m.StartTask(t), false));
            actions.Add(($"Ask {owner}", "Z.BtnSm", () => m.AskAbout(t), false));
        }
        else if (lane == TaskLane.Todo)
        {
            actions.Add((TaskBoard.NeedsConfirm(t) ? "Confirm & start" : "Start", "Z.BtnSm.Pri", () => m.StartTask(t), true));
            actions.Add(($"Ask {owner}", "Z.BtnSm", () => m.AskAbout(t), false));
        }
        else if (lane == TaskLane.Doing)
        {
            actions.Add(("Mark done", "Z.BtnSm.Pri", () => m.DoneTask(t), true));
            actions.Add(($"Ask {owner}", "Z.BtnSm", () => m.AskAbout(t), false));
        }
        else if (t.Status is MemberTaskStatus.Help or MemberTaskStatus.Cant)
        {
            actions.Add(("Carry on", "Z.BtnSm.Pri", () => m.CarryOn(t), true));
            if (t.RemoteId is not null)
            {
                actions.Add(("Add a note", "Z.BtnSm", () => m.OpenNote(t.Id), false));
            }
        }
        else if (t.RemoteId is not null && t.Status == MemberTaskStatus.Review)
        {
            actions.Add(("Add a note", "Z.BtnSm", () => m.OpenNote(t.Id), true));
        }
        else if (t.Status == MemberTaskStatus.Cancelled)
        {
            // Called off by the owner: nothing to do.
        }
        else if (TaskBoard.Finished(t))
        {
            actions.Add(("Add a note", "Z.BtnSm", () => m.OpenNote(t.Id), true));
        }
        if (actions.Count > 0)
        {
            var row = new Grid { Margin = new Thickness(0, 2, 0, -6) };
            for (var i = 0; i < actions.Count; i++)
            {
                var (label, style, run, grow) = actions[i];
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = grow ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });
                var b = Ui.Button(label, style, run, label + ": " + t.Title);
                b.Margin = new Thickness(0, 0, i < actions.Count - 1 ? 6 : 0, 0);
                Grid.SetColumn(b, i);
                row.Children.Add(b);
            }
            body.Children.Add(row);
        }
        return body;
    }

    private static Button StepRow(TaskStep s, bool locked, Action toggle)
    {
        var box = new Border { Width = 18, Height = 18, CornerRadius = new CornerRadius(5), Margin = new Thickness(0, 0, 10, 0) };
        if (s.Done)
        {
            box.SetResourceReference(Border.BackgroundProperty, "Z.Inv");
            var mark = Ui.Text("✓", 11, FontWeights.ExtraBold, "Z.OnInv");
            mark.HorizontalAlignment = HorizontalAlignment.Center;
            mark.VerticalAlignment = VerticalAlignment.Center;
            box.Child = mark;
        }
        else
        {
            box.BorderThickness = new Thickness(1.5);
            box.SetResourceReference(Border.BorderBrushProperty, "Z.Line2");
        }
        var label = Ui.Text(s.Label, 12.5, fg: s.Done ? "Z.Muted" : "Z.Ink");
        if (s.Done)
        {
            label.TextDecorations = TextDecorations.Strikethrough;
        }
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(box);
        content.Children.Add(label);
        var b = Ui.Button(content, "Z.MenuRow", toggle, (s.Done ? "Done: " : "Not done: ") + s.Label);
        b.Padding = new Thickness(0, 0, 4, 0);
        b.MinHeight = 32;
        b.IsEnabled = !locked;
        AutomationProperties.SetItemStatus(b, s.Done ? "checked" : "unchecked");
        return b;
    }

    private static string Sent(DateTimeOffset at, DateTimeOffset now)
    {
        var day = at.ToLocalTime().Date;
        return day == now.ToLocalTime().Date ? at.ToLocalTime().ToString("h:mm tt")
            : day == now.ToLocalTime().Date.AddDays(-1) ? "Yesterday"
            : at.ToLocalTime().ToString("ddd d MMM");
    }

    private static ControlTemplate TickTemplate()
    {
        var t = new ControlTemplate(typeof(ToggleButton));
        var grid = new FrameworkElementFactory(typeof(Grid));
        grid.SetValue(Panel.BackgroundProperty, Brushes.Transparent);
        var circle = new FrameworkElementFactory(typeof(Border), "c");
        circle.SetValue(FrameworkElement.WidthProperty, 20.0);
        circle.SetValue(FrameworkElement.HeightProperty, 20.0);
        circle.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
        circle.SetValue(Border.BorderThicknessProperty, new Thickness(1.5));
        circle.SetResourceReference(Border.BorderBrushProperty, "Z.Line2");
        circle.SetResourceReference(Border.BackgroundProperty, "Z.Surface");
        var mark = new FrameworkElementFactory(typeof(TextBlock), "m");
        mark.SetValue(TextBlock.TextProperty, "✓");
        mark.SetValue(TextBlock.FontSizeProperty, 12.0);
        mark.SetValue(TextBlock.FontWeightProperty, FontWeights.ExtraBold);
        mark.SetResourceReference(TextBlock.ForegroundProperty, "Z.OnInv");
        mark.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        mark.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        mark.SetValue(UIElement.VisibilityProperty, Visibility.Collapsed);
        circle.AppendChild(mark);
        grid.AppendChild(circle);
        t.VisualTree = grid;
        var on = new Trigger { Property = ToggleButton.IsCheckedProperty, Value = true };
        on.Setters.Add(new Setter(Border.BackgroundProperty, new DynamicResourceExtension("Z.Inv"), "c"));
        on.Setters.Add(new Setter(Border.BorderThicknessProperty, new Thickness(0), "c"));
        on.Setters.Add(new Setter(UIElement.VisibilityProperty, Visibility.Visible, "m"));
        t.Triggers.Add(on);
        return t;
    }

    private static ControlTemplate ChevronTemplate(bool open)
    {
        var t = new ControlTemplate(typeof(Button));
        var grid = new FrameworkElementFactory(typeof(Grid));
        grid.SetValue(Panel.BackgroundProperty, Brushes.Transparent);
        var box = new FrameworkElementFactory(typeof(Border));
        box.SetValue(FrameworkElement.WidthProperty, 28.0);
        box.SetValue(FrameworkElement.HeightProperty, 28.0);
        box.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
        box.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        var path = new FrameworkElementFactory(typeof(Path));
        path.SetValue(Path.DataProperty, Geometry.Parse(open ? "M0,6 L5,1 L10,6" : "M0,1 L5,6 L10,1"));
        path.SetValue(Shape.StrokeThicknessProperty, 2.4);
        path.SetValue(Shape.StrokeStartLineCapProperty, PenLineCap.Round);
        path.SetValue(Shape.StrokeEndLineCapProperty, PenLineCap.Round);
        path.SetValue(Shape.StrokeLineJoinProperty, PenLineJoin.Round);
        path.SetResourceReference(Shape.StrokeProperty, "Z.Muted");
        path.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        path.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        box.AppendChild(path);
        grid.AppendChild(box);
        t.VisualTree = grid;
        return t;
    }
}

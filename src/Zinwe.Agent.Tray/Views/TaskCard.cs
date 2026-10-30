using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using Zinwe.Agent.Tray.Widget;

namespace Zinwe.Agent.Tray.Views;

/// <summary>
/// The board's task card (Tasks tab and inside Chat): a tile or a round tick, the title, "Doing · Due in 12 min ·
/// 1/4", one quick action while closed, and a chevron that opens details, steps, notes and the actions.
/// </summary>
internal static class TaskCard
{
    public static FrameworkElement Build(MemberTask t, TrayModel m, bool open, double? width = null)
    {
        var now = m.Now;
        var owner = m.OwnerFirst;
        var lane = TaskBoard.LaneOf(t);
        var finished = TaskBoard.Finished(t);
        var (tileBg, tileFg, stKey) = lane switch
        {
            TaskLane.Doing => ("Z.OrangeTint", "Z.StDoing", "Z.StDoing"),
            TaskLane.Waiting => ("Z.InfoTint", "Z.Info", "Z.StWait"),
            TaskLane.Done => ("Z.OkTint", "Z.Ok", "Z.StDone"),
            _ => ("Z.Fill", "Z.Ink3", "Z.Ink3"),
        };
        if (t.IsNew)
        {
            (tileBg, tileFg) = ("Z.Orange", "Z.OnOrange");
        }

        var stack = new StackPanel();
        var head = new Grid();
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition());
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        FrameworkElement lead;
        if (t.Mine)
        {
            var tick = new ToggleButton
            {
                IsChecked = finished, Width = 44, Height = 44, Margin = new Thickness(-10, -10, -2, -10), Cursor = System.Windows.Input.Cursors.Hand,
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
            lead = tick;
        }
        else
        {
            lead = Ui.Tile(Ui.Icon.TaskTick, 24, tileBg, tileFg, 7, 12);
            lead.Margin = new Thickness(0, 0, 8, 0);
        }
        lead.VerticalAlignment = VerticalAlignment.Center;
        head.Children.Add(lead);

        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        texts.Children.Add(Ui.Text(t.Title, 13, FontWeights.Bold, trim: true));
        var line = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 1, 0, 0) };
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
        if (t.IsNew && !finished)
        {
            var tag = Ui.Pill("NEW", "Z.Orange", "Z.OnOrange", 10.5, FontWeights.ExtraBold);
            tag.Padding = new Thickness(5, 0, 5, 0);
            tag.Margin = new Thickness(6, 0, 0, 0);
            line.Children.Add(tag);
        }
        texts.Children.Add(line);
        Grid.SetColumn(texts, 1);
        head.Children.Add(texts);

        if (!open && TaskBoard.Quick(t) is { } quick)
        {
            var q = Ui.Button(quick.Label, quick.Primary ? "Z.BtnSm.Pri" : "Z.BtnSm.Dark", () => m.QuickRun(t), quick.Aria);
            q.Padding = new Thickness(10, 0, 10, 0);
            q.FontSize = 12;
            q.Margin = new Thickness(8, 0, 0, 0);
            Grid.SetColumn(q, 2);
            head.Children.Add(q);
        }

        var chevron = new Button
        {
            Width = 44, Height = 44, Margin = new Thickness(2, -4, -4, -4), Cursor = System.Windows.Input.Cursors.Hand,
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

        var card = new Border
        {
            CornerRadius = new CornerRadius(14), Padding = new Thickness(10, 8, 8, 8), Child = stack, SnapsToDevicePixels = true,
            BorderThickness = new Thickness(t.Mine ? 0 : 1),
        };
        if (width is { } w)
        {
            card.Width = w;
        }
        if (t.Mine)
        {
            card.SetResourceReference(Border.BackgroundProperty, "Z.Surface2");
            var frame = new Grid();
            var dash = new Rectangle { RadiusX = 14, RadiusY = 14, StrokeThickness = 1, StrokeDashArray = [4, 3], IsHitTestVisible = false };
            dash.SetResourceReference(Shape.StrokeProperty, "Z.Line2");
            frame.Children.Add(card);
            frame.Children.Add(dash);
            return frame;
        }
        card.SetResourceReference(Border.BackgroundProperty, "Z.Surface");
        card.SetResourceReference(Border.BorderBrushProperty, open ? "Z.Line2" : "Z.Line");
        card.Effect = new DropShadowEffect { BlurRadius = 20, ShadowDepth = 8, Opacity = 0.12, Direction = 270, Color = Color.FromRgb(0x14, 0x12, 0x10) };
        return card;
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
        if (t.Activity.Count > 0)
        {
            var label = Ui.Text("ACTIVITY", 10.5, FontWeights.Bold, "Z.Muted");
            label.Margin = new Thickness(0, 10, 0, 2);
            body.Children.Add(label);
            foreach (var a in t.Activity.TakeLast(6))
            {
                var entry = Ui.Text($"{Sent(a.At, m.Now)}{(a.At.ToLocalTime().Date == m.Now.ToLocalTime().Date ? "" : ", " + a.At.ToLocalTime().ToString("h:mm tt"))} · {a.Text}",
                    11.5, fg: "Z.Ink3", wrap: true);
                entry.Margin = new Thickness(0, 1, 0, 0);
                body.Children.Add(entry);
            }
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
            actions.Add(("Confirm", "Z.BtnSm.Dark", () => m.ConfirmTask(t), true));
            actions.Add(("Start", "Z.BtnSm", () => m.StartTask(t), false));
            actions.Add(($"Ask {owner}", "Z.BtnSm", () => m.AskAbout(t), false));
        }
        else if (lane == TaskLane.Todo)
        {
            actions.Add((TaskBoard.NeedsConfirm(t) ? "Confirm & start" : "Start", "Z.BtnSm.Dark", () => m.StartTask(t), true));
            actions.Add(($"Ask {owner}", "Z.BtnSm", () => m.AskAbout(t), false));
        }
        else if (lane == TaskLane.Doing)
        {
            actions.Add(("Mark done", "Z.BtnSm.Pri", () => m.DoneTask(t), true));
            actions.Add(($"Ask {owner}", "Z.BtnSm", () => m.AskAbout(t), false));
        }
        else if (t.Status is MemberTaskStatus.Help or MemberTaskStatus.Cant)
        {
            actions.Add(("Carry on", "Z.BtnSm.Dark", () => m.CarryOn(t), true));
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
            var row = new Grid { Margin = new Thickness(0, 6, 0, 0) };
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
        var box = new Border { Width = 16, Height = 16, CornerRadius = new CornerRadius(5), Margin = new Thickness(0, 0, 10, 0) };
        if (s.Done)
        {
            box.SetResourceReference(Border.BackgroundProperty, "Z.OkFill");
            box.Child = new TextBlock { Text = "✓", Foreground = Brushes.White, FontSize = 11, FontWeight = FontWeights.ExtraBold,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        }
        else
        {
            box.BorderThickness = new Thickness(1.5);
            box.SetResourceReference(Border.BorderBrushProperty, "Z.Off");
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
        b.Padding = new Thickness(4, 0, 4, 0);
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
        circle.SetValue(FrameworkElement.WidthProperty, 24.0);
        circle.SetValue(FrameworkElement.HeightProperty, 24.0);
        circle.SetValue(Border.CornerRadiusProperty, new CornerRadius(12));
        circle.SetValue(Border.BorderThicknessProperty, new Thickness(2));
        circle.SetResourceReference(Border.BorderBrushProperty, "Z.Line2");
        circle.SetResourceReference(Border.BackgroundProperty, "Z.Surface");
        var mark = new FrameworkElementFactory(typeof(TextBlock), "m");
        mark.SetValue(TextBlock.TextProperty, "✓");
        mark.SetValue(TextBlock.FontSizeProperty, 13.0);
        mark.SetValue(TextBlock.FontWeightProperty, FontWeights.ExtraBold);
        mark.SetValue(TextBlock.ForegroundProperty, Brushes.White);
        mark.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        mark.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        mark.SetValue(UIElement.VisibilityProperty, Visibility.Collapsed);
        circle.AppendChild(mark);
        grid.AppendChild(circle);
        t.VisualTree = grid;
        var on = new Trigger { Property = ToggleButton.IsCheckedProperty, Value = true };
        on.Setters.Add(new Setter(Border.BackgroundProperty, new DynamicResourceExtension("Z.OkFill"), "c"));
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
        box.SetValue(FrameworkElement.WidthProperty, 36.0);
        box.SetValue(FrameworkElement.HeightProperty, 36.0);
        box.SetValue(Border.CornerRadiusProperty, new CornerRadius(10));
        box.SetResourceReference(Border.BackgroundProperty, open ? "Z.Inv" : "Z.Fill");
        var path = new FrameworkElementFactory(typeof(Path));
        path.SetValue(Path.DataProperty, Geometry.Parse(open ? "M0,6 L5,1 L10,6" : "M0,1 L5,6 L10,1"));
        path.SetValue(Shape.StrokeThicknessProperty, 2.4);
        path.SetValue(Shape.StrokeStartLineCapProperty, PenLineCap.Round);
        path.SetValue(Shape.StrokeEndLineCapProperty, PenLineCap.Round);
        path.SetValue(Shape.StrokeLineJoinProperty, PenLineJoin.Round);
        path.SetResourceReference(Shape.StrokeProperty, open ? "Z.OnInv" : "Z.Ink3");
        path.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        path.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        box.AppendChild(path);
        grid.AppendChild(box);
        t.VisualTree = grid;
        return t;
    }
}

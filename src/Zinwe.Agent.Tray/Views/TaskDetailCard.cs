using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using Zinwe.Agent.Tray.Widget;

namespace Zinwe.Agent.Tray.Views;

/// <summary>
/// A task's details card (Tasks v2, opened by a press and hold on the row, or ⋯): an Apple-style modal card over the
/// dimmed window. Title, status chip and who it's from, the note, a grouped panel (Due, Time, From / Who can see it,
/// Priority) with icons in soft squares, Steps with a progress bar, Notes, Activity (newest first), and the actions
/// at the bottom: Mark done, then Ask / Add a note / Can't do it for an owner's task, Edit / Tomorrow / Delete for an
/// own to-do. Done is final: a done task offers only Add a note. × , Esc or a click outside closes it.
/// </summary>
internal static class TaskDetailCard
{
    // Icons (24-unit strokes, like Ui.Icon).
    private const string IcoClock = "M12 4a8 8 0 1 0 0 16 8 8 0 0 0 0-16zM12 7.5V12l3 2";
    private const string IcoFlag = "M5 21V4M5 4.5h11l-2.2 4 2.2 4H5";
    private const string IcoNote = "M5 3.5h10l4 4v13H5zM8.5 12h7M8.5 16h4.5";
    private const string IcoChat = "M12 4.5c4.7 0 7.8 2.9 7.8 6.7S16.7 18 12 18c-1 0-1.9-.1-2.8-.4L5 19.5l1.1-3.4C4.9 14.8 4.2 13.1 4.2 11.2 4.2 7.4 7.3 4.5 12 4.5z";
    private const string IcoCant = "M12 4.2a7.8 7.8 0 1 0 0 15.6 7.8 7.8 0 0 0 0-15.6zM6.6 17.4 17.4 6.6";
    private const string IcoPen = "M4.8 19.2l.8-3.8 10-10 3 3-10 10z";
    private const string IcoBin = "M4.8 7.2h14.4M9.6 7.2V5.4h4.8v1.8M6.6 7.2l.8 11.4h9.2l.8-11.4";
    private const string IcoMove = "M4.2 5.4h15.6v14.4H4.2zM4.2 10.2h15.6M8.4 3.4v3.6M15.6 3.4v3.6M12 13.2v3.6M10.2 15h3.6";
    private const string IcoX = "M6 6l12 12M18 6 6 18";

    /// <summary>The whole overlay: the dimmed window and the card on it.</summary>
    public static FrameworkElement Build(MemberTask t, TrayModel m, bool arriving = true)
    {
        var layer = new Grid();
        var scrim = new Border { Background = Ui.B("Z.DimCard") }.Res(Border.BackgroundProperty, "Z.DimCard");
        scrim.MouseLeftButtonDown += (_, e) =>
        {
            m.CloseDetail();
            e.Handled = true;
        };
        layer.Children.Add(scrim);
        var card = Card(t, m, arriving);
        layer.Children.Add(card);
        if (arriving)
        {
            Motion.Arrive(card, 0, 8, 0.94, 200);
        }
        return layer;
    }

    private static Border Card(MemberTask t, TrayModel m, bool arriving)
    {
        var owner = m.OwnerFirst;
        var now = m.Now;
        var open = !TaskBoard.Ticked(t);

        var shell = new Grid();
        shell.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        shell.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        shell.RowDefinitions.Add(new RowDefinition());
        shell.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // Title and the round close.
        var head = new Grid { Margin = new Thickness(16, 16, 14, 10) };
        head.ColumnDefinitions.Add(new ColumnDefinition());
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var title = Ui.Text(t.Title, 18, FontWeights.Bold, wrap: true);
        title.LineHeight = 23.5;
        head.Children.Add(title);
        var closeDisc = new Border { Width = 30, Height = 30, CornerRadius = new CornerRadius(15), Child = Ui.Glyph(IcoX, 11, "Z.Ink2", 3) }
            .Res(Border.BackgroundProperty, "Z.CloseFill");
        var close = new Button
        {
            Content = closeDisc, Cursor = Cursors.Hand, Style = TasksView.Bare(), Margin = new Thickness(10, -2, 0, 0),
            VerticalAlignment = VerticalAlignment.Top, FocusVisualStyle = (Style)Application.Current.FindResource("Z.FocusRing"),
            IsCancel = false,
        };
        close.Click += (_, _) => m.CloseDetail();
        AutomationProperties.SetName(close, "Close");
        Grid.SetColumn(close, 1);
        head.Children.Add(close);
        shell.Children.Add(head);

        // Status chip and who it's from.
        var sub = new WrapPanel { Margin = new Thickness(16, 0, 16, 12) };
        void Sub(FrameworkElement el)
        {
            el.Margin = new Thickness(0, 0, 10, 4);
            el.VerticalAlignment = VerticalAlignment.Center;
            sub.Children.Add(el);
        }
        var (chipLabel, chipTone) = ChipOf(t, owner);
        Sub(TasksView.Chip(chipLabel, chipTone, 12.5));
        Sub(t.Mine ? Ui.Text("Your own to-do", 12.5, fg: "Z.Muted") : TasksView.From(owner));
        Grid.SetRow(sub, 1);
        shell.Children.Add(sub);

        // The body scrolls; the actions stay at the bottom.
        var body = new StackPanel { Margin = new Thickness(12, 0, 12, 12) };
        if (t.Details.Length > 0)
        {
            body.Children.Add(Group(Note(Ui.Text(t.Details, 13.5, wrap: true))));
        }
        var (day, at) = TaskBoard.When(t);
        var late = TaskBoard.IsLate(t, now);
        var rows = new List<(string Icon, string Key, string Value, bool Strong)>
        {
            (Ui.Icon.Calendar, "Due", DayText(day, now), late),
            (IcoClock, "Time", (at is { } x ? "By " + TaskBoard.TimeLabel(x) : "No time") + (late && at is not null ? " · late" : ""), false),
            t.Mine
                ? (Ui.Icon.Person, "Who can see it", TaskBoard.OwnLine(t, m.TodoSharingOn) == TaskBoard.SharedTodoLine ? "You and your admin" : "Only you, on this PC", false)
                : (Ui.Icon.Person, "From", m.OwnerNameStart, false),
            (IcoFlag, "Priority", t.High ? "High" : "Normal", t.High),
        };
        body.Children.Add(Group(Rows(rows)));

        if (t.Steps.Count > 0)
        {
            var done = t.Steps.Count(s => s.Done);
            var steps = new StackPanel();
            var track = new Grid { Height = 4, Margin = new Thickness(12, 10, 12, 4), ClipToBounds = true };
            track.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(done, 0.001), GridUnitType.Star) });
            track.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(t.Steps.Count - done, 0.001), GridUnitType.Star) });
            var bar = new Border { CornerRadius = new CornerRadius(2) }.Res(Border.BackgroundProperty, "Z.Inv");
            var rest = new Border { CornerRadius = new CornerRadius(2) }.Res(Border.BackgroundProperty, "Z.Line");
            Grid.SetColumn(rest, 1);
            track.Children.Add(rest);
            track.Children.Add(bar);
            steps.Children.Add(track);
            var locked = TaskBoard.Locked(t);
            for (var i = 0; i < t.Steps.Count; i++)
            {
                var index = i;
                steps.Children.Add(Step(t.Steps[i], i > 0, locked, () => m.ToggleStep(t, index)));
            }
            body.Children.Add(Titled($"Steps · {done} of {t.Steps.Count}", Group(steps)));
        }
        if (t.Notes.Count > 0)
        {
            var notes = new StackPanel();
            for (var i = 0; i < t.Notes.Count; i++)
            {
                var n = t.Notes[i];
                var text = Ui.Text("", 13.5, wrap: true);
                text.Inlines.Add(new System.Windows.Documents.Run(n.By) { FontWeight = FontWeights.SemiBold });
                text.Inlines.Add(new System.Windows.Documents.Run(" · " + n.Text));
                notes.Children.Add(Note(text, divider: i > 0));
            }
            body.Children.Add(Titled("Notes", Group(notes)));
        }
        if (t.Activity.Count > 0)
        {
            var list = new StackPanel { Margin = new Thickness(12, 6, 12, 10) };
            var first = true;
            foreach (var a in t.Activity.Reverse())
            {
                var row = new Grid { Margin = new Thickness(0, 6, 0, 6) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
                row.ColumnDefinitions.Add(new ColumnDefinition());
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var dot = Ui.Dot(7, first ? "Z.Inv" : "Z.Line2");
                dot.VerticalAlignment = VerticalAlignment.Top;
                dot.HorizontalAlignment = HorizontalAlignment.Left;
                dot.Margin = new Thickness(3, 6, 0, 0);
                row.Children.Add(dot);
                var what = Ui.Text(a.Text, 13, FontWeights.Medium, wrap: true);
                Grid.SetColumn(what, 1);
                row.Children.Add(what);
                var when = Ui.Text(TaskCard.Sent(a.At, now), 13, fg: "Z.Faint");
                when.Margin = new Thickness(10, 0, 0, 0);
                Grid.SetColumn(when, 2);
                row.Children.Add(when);
                list.Children.Add(row);
                first = false;
            }
            body.Children.Add(Titled("Activity", Group(list)));
        }
        var scroll = new ScrollViewer
        {
            Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Style = (Style)Application.Current.FindResource("Z.Scroll"), Focusable = false,
        };
        Grid.SetRow(scroll, 2);
        shell.Children.Add(scroll);

        var foot = Footer(t, m, open, owner);
        Grid.SetRow(foot, 3);
        shell.Children.Add(foot);

        var card = new Border
        {
            Child = shell, CornerRadius = new CornerRadius(16), Margin = new Thickness(12, 56, 12, 16), ClipToBounds = true,
            Effect = new DropShadowEffect { BlurRadius = 50, ShadowDepth = 20, Opacity = 0.28, Direction = 270, Color = Color.FromRgb(0x14, 0x12, 0x10) },
        }.Res(Border.BackgroundProperty, "Z.DetailBg");
        AutomationProperties.SetName(card, "Task details: " + t.Title);
        if (arriving)
        {
            card.Loaded += (_, _) => close.Focus();
        }
        return card;
    }

    /// <summary>The chip on the card: an owner's task's status, or To do / Doing / Done for an own to-do.</summary>
    internal static (string Label, string Tone) ChipOf(MemberTask t, string owner) =>
        TaskBoard.StatusChip(t, owner) is { } chip ? chip
        : TaskBoard.StatusOf(t) switch
        {
            TaskStatusKey.Done => ("Done", "Approved"),
            TaskStatusKey.Doing => ("Doing", "Doing"),
            _ => ("To do", "Todo"),
        };

    /// <summary>"Today, Wed 4 Nov", "Tomorrow, Thu 5 Nov", "Yesterday, Tue 3 Nov", "Fri 6 Nov" or "No date".</summary>
    internal static string DayText(DateOnly? day, DateTimeOffset now)
    {
        if (day is not { } d)
        {
            return "No date";
        }
        var n = d.DayNumber - TaskBoard.Today(now).DayNumber;
        var word = n switch { 0 => "Today, ", 1 => "Tomorrow, ", -1 => "Yesterday, ", _ => "" };
        return word + TaskBoard.DayLabel(d);
    }

    private static Border Group(UIElement child) => new Border
    {
        Child = child, CornerRadius = new CornerRadius(12), ClipToBounds = true, Margin = new Thickness(0, 0, 0, 14),
    }.Res(Border.BackgroundProperty, "Z.Surface");

    private static StackPanel Titled(string title, Border group)
    {
        var stack = new StackPanel();
        var t = Ui.Text(title, 12, FontWeights.SemiBold, "Z.Muted");
        t.Margin = new Thickness(6, 0, 6, 6);
        stack.Children.Add(t);
        stack.Children.Add(group);
        return stack;
    }

    private static FrameworkElement Note(TextBlock text, bool divider = false)
    {
        text.LineHeight = 20;
        var b = new Border { Child = text, Padding = new Thickness(12, 10, 12, 10), BorderThickness = new Thickness(0, divider ? 1 : 0, 0, 0) }
            .Res(Border.BorderBrushProperty, "Z.Line");
        return b;
    }

    /// <summary>The grouped rows: an icon in a 26 px soft square, the name, the value on the right; hairlines between.</summary>
    private static StackPanel Rows(IReadOnlyList<(string Icon, string Key, string Value, bool Strong)> rows)
    {
        var stack = new StackPanel();
        for (var i = 0; i < rows.Count; i++)
        {
            var (icon, key, value, strong) = rows[i];
            var grid = new Grid { MinHeight = 44 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(50) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            var ico = Ui.Tile(icon, 26, "Z.IconSoft", "Z.Ink", 7, 15);
            ico.HorizontalAlignment = HorizontalAlignment.Left;
            ico.Margin = new Thickness(12, 0, 0, 0);
            grid.Children.Add(ico);
            var line = new Grid();
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            line.ColumnDefinitions.Add(new ColumnDefinition());
            var k = Ui.Text(key, 14);
            k.VerticalAlignment = VerticalAlignment.Center;
            line.Children.Add(k);
            var v = Ui.Text(value, 13.5, strong ? FontWeights.SemiBold : FontWeights.Normal, strong ? "Z.Ink" : "Z.Muted", wrap: true);
            v.TextAlignment = TextAlignment.Right;
            v.VerticalAlignment = VerticalAlignment.Center;
            v.Margin = new Thickness(12, 8, 12, 8);
            Grid.SetColumn(v, 1);
            line.Children.Add(v);
            var hair = new Border { Child = line, BorderThickness = new Thickness(0, i > 0 ? 1 : 0, 0, 0) }.Res(Border.BorderBrushProperty, "Z.Line");
            Grid.SetColumn(hair, 1);
            Grid.SetColumnSpan(hair, 2);
            grid.Children.Add(hair);
            AutomationProperties.SetName(grid, $"{key}: {value}");
            stack.Children.Add(grid);
        }
        return stack;
    }

    private static FrameworkElement Step(TaskStep s, bool divider, bool locked, Action toggle)
    {
        var disc = new Border
        {
            Width = 20, Height = 20, CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(s.Done ? 0 : 1.5),
            HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(12, 0, 0, 0),
        }.Res(Border.BackgroundProperty, s.Done ? "Z.Inv" : "Z.Surface").Res(Border.BorderBrushProperty, "Z.Line2");
        if (s.Done)
        {
            disc.Child = Ui.Glyph(Ui.Icon.Check, 11, "Z.OnInv", 3.4);
        }
        var grid = new Grid { MinHeight = 42 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.Children.Add(disc);
        var label = Ui.Text(s.Label, 14, fg: s.Done ? "Z.Faint" : "Z.Ink", wrap: true);
        label.VerticalAlignment = VerticalAlignment.Center;
        label.Margin = new Thickness(0, 10, 12, 10);
        if (s.Done)
        {
            label.TextDecorations = TextDecorations.Strikethrough;
        }
        var hair = new Border { Child = label, BorderThickness = new Thickness(0, divider ? 1 : 0, 0, 0) }.Res(Border.BorderBrushProperty, "Z.Line");
        Grid.SetColumn(hair, 1);
        grid.Children.Add(hair);
        var b = new Button
        {
            Content = grid, Cursor = locked ? Cursors.Arrow : Cursors.Hand, Style = TasksView.Bare(), IsEnabled = !locked,
            HorizontalContentAlignment = HorizontalAlignment.Stretch, FocusVisualStyle = (Style)Application.Current.FindResource("Z.FocusRingInset"),
        };
        b.Click += (_, _) => toggle();
        AutomationProperties.SetName(b, (s.Done ? "Untick " : "Tick ") + s.Label);
        AutomationProperties.SetItemStatus(b, s.Done ? "checked" : "unchecked");
        return b;
    }

    /// <summary>Mark done (or, done, Add a note and why it stays done), then the three icon actions.</summary>
    private static Border Footer(MemberTask t, TrayModel m, bool open, string owner)
    {
        var stack = new StackPanel();
        if (open)
        {
            stack.Children.Add(Primary(Ui.Icon.Check, t.Mine ? "Mark done" : $"Mark done · send to {owner}", () => m.FromDetail(() => m.DoneTask(t))));
            var acts = new UniformGrid { Columns = 3, Margin = new Thickness(0, 8, 0, 0) };
            if (t.Mine)
            {
                acts.Children.Add(Act(IcoPen, "Edit", false, () => m.FromDetail(() => m.OpenNewTask(t))));
                acts.Children.Add(Act(IcoMove, "Tomorrow", false, () => m.FromDetail(() => m.MoveToTomorrow(t)), "Move to tomorrow"));
                acts.Children.Add(Act(IcoBin, "Delete", true, () => m.FromDetail(() => m.DeleteOwn(t))));
            }
            else
            {
                acts.Children.Add(Act(IcoChat, "Ask " + owner, false, () => m.FromDetail(() => m.AskInChat(t))));
                acts.Children.Add(Act(IcoNote, "Add a note", false, () => m.FromDetail(() => m.OpenNote(t.Id))));
                acts.Children.Add(Act(IcoCant, "Can’t do it", true, () => m.FromDetail(() => m.CantFor(t))));
            }
            stack.Children.Add(acts);
        }
        else if (TaskBoard.TickActionOf(t) == TickAction.AddNote)
        {
            stack.Children.Add(Primary(IcoNote, "Add a note", () => m.FromDetail(() => m.OpenNote(t.Id))));
            var why = Ui.Text(TaskBoard.DoneStaysDone, 11.5, fg: "Z.Faint", wrap: true);
            why.TextAlignment = TextAlignment.Center;
            why.Margin = new Thickness(4, 6, 4, 0);
            stack.Children.Add(why);
        }
        else
        {
            var line = Ui.Text(TaskBoard.Line(t, owner), 12, fg: "Z.Muted", wrap: true);
            line.TextAlignment = TextAlignment.Center;
            stack.Children.Add(line);
        }
        return new Border { Child = stack, Padding = new Thickness(12, 10, 12, 12), BorderThickness = new Thickness(0, 1, 0, 0) }
            .Res(Border.BackgroundProperty, "Z.DetailBg").Res(Border.BorderBrushProperty, "Z.Line");
    }

    private static Button Primary(string icon, string label, Action run)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        var g = Ui.Glyph(icon, 16, "Z.OnInv", 2.4);
        g.Margin = new Thickness(0, 0, 8, 0);
        content.Children.Add(g);
        var text = Ui.Text(label, 14, FontWeights.SemiBold, "Z.OnInv");
        text.VerticalAlignment = VerticalAlignment.Center;
        content.Children.Add(text);
        var box = new Border { Height = 44, CornerRadius = new CornerRadius(12), Child = content }.Res(Border.BackgroundProperty, "Z.Inv");
        content.VerticalAlignment = VerticalAlignment.Center;
        var b = new Button
        {
            Content = box, Cursor = Cursors.Hand, Style = TasksView.Bare(), HorizontalContentAlignment = HorizontalAlignment.Stretch,
            FocusVisualStyle = (Style)Application.Current.FindResource("Z.FocusRing"),
        };
        b.Click += (_, _) => run();
        AutomationProperties.SetName(b, label);
        return b;
    }

    private static Button Act(string icon, string label, bool danger, Action run, string? aria = null)
    {
        var fg = danger ? "Z.RedText" : "Z.Ink";
        var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
        var g = Ui.Glyph(icon, 18, fg, 2);
        g.HorizontalAlignment = HorizontalAlignment.Center;
        stack.Children.Add(g);
        var t = Ui.Text(label, 11.5, FontWeights.SemiBold, fg, trim: true);
        t.HorizontalAlignment = HorizontalAlignment.Center;
        t.Margin = new Thickness(0, 4, 0, 0);
        stack.Children.Add(t);
        var box = new Border { CornerRadius = new CornerRadius(12), Padding = new Thickness(4, 8, 4, 7), Child = stack, Margin = new Thickness(4, 0, 4, 0) }
            .Res(Border.BackgroundProperty, "Z.Surface");
        var b = new Button
        {
            Content = box, Cursor = Cursors.Hand, Style = TasksView.Bare(), HorizontalContentAlignment = HorizontalAlignment.Stretch,
            FocusVisualStyle = (Style)Application.Current.FindResource("Z.FocusRing"),
        };
        b.Click += (_, _) => run();
        AutomationProperties.SetName(b, aria ?? label);
        return b;
    }
}

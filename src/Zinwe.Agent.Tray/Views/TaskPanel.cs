using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Zinwe.Agent.Core.Tasks;
using Zinwe.Agent.Tray.Widget;

namespace Zinwe.Agent.Tray.Views;

/// <summary>
/// v1.4 · the member task panel (prototype panel.html, member side): a card over the dimmed window with the status
/// pill, due and High, the task chat icon (a badge while something is new), ⋯ (Ask {owner} · Can't do it; an own to-do:
/// Move to tomorrow · Edit · Delete) and ×; tabs Overview · Chat · Activity. Overview: the title, who sent it, What's
/// happening with the member's wording and its buttons (Start, Tick step N, Start again, Mark it done) or the inline
/// Need help / Can't do it note, the journey bar, people ("You" first-person), steps (only the member's own or anyone's
/// tickable, in order when the task says), and the details. Chat: everyone on the task, with a composer. Activity: what
/// happened, newest first.
/// </summary>
internal static class TaskPanel
{
    private static readonly Dictionary<string, double> Offsets = new(StringComparer.Ordinal);

    public static FrameworkElement Build(TaskItem it, TrayModel m, bool arriving)
    {
        var layer = new Grid();
        var scrim = new Border().Res(Border.BackgroundProperty, "Z.DimCard");
        scrim.MouseLeftButtonDown += (_, e) =>
        {
            m.CloseDetail();
            e.Handled = true;
        };
        layer.Children.Add(scrim);
        var card = Card(it, m);
        layer.Children.Add(card);
        if (arriving)
        {
            Motion.Arrive(card, 0, 8, 0.96, 200);
        }
        return layer;
    }

    private static Border Card(TaskItem it, TrayModel m)
    {
        var t = it.Task;
        var c = it.Ctx;
        var tab = it.Own && m.PanelTab == "chat" ? "overview" : m.PanelTab;

        var shell = new Grid();
        shell.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        shell.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        shell.RowDefinitions.Add(new RowDefinition());
        shell.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // ---- Header: pill, due, High · chat, ⋯, × ----
        var head = new Grid { Margin = new Thickness(14, 12, 10, 4) };
        head.ColumnDefinitions.Add(new ColumnDefinition());
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var chips = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
        void Chip(FrameworkElement? el)
        {
            if (el is not null)
            {
                el.Margin = new Thickness(0, 0, 6, 6);
                chips.Children.Add(el);
            }
        }
        var pill = TeamTaskEngine.Pill(t, c);
        Chip(Tv.Pill(pill.Label, pill.Tone));
        Chip(Tv.Due(TeamTaskEngine.Due(t, c), icon: true));
        if (string.Equals(t.Priority, "high", StringComparison.OrdinalIgnoreCase))
        {
            Chip(Tv.High());
        }
        head.Children.Add(chips);
        var tools = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top };
        var key = it.Key;
        if (!it.Own)
        {
            var summary = m.TaskChatSummary(it);
            var chatBtn = Tv.Icon(Tv.IcoChat, () => m.SetPanelTab("chat"), "Task chat" + (summary.Unread > 0 ? $", {summary.Unread} new" : ""),
                tab == "chat" ? "Z.OrangeTint" : "Transparent", 17);
            if (summary.Unread > 0 && tab != "chat" && chatBtn.Content is Grid g)
            {
                var badge = new Border
                {
                    MinWidth = 16, Height = 16, CornerRadius = new CornerRadius(8), HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 2, 0), Padding = new Thickness(4, 0, 4, 0),
                    Child = Ui.Text(summary.Unread.ToString(System.Globalization.CultureInfo.InvariantCulture), 10, FontWeights.ExtraBold, "Z.OnOrange"),
                }.Res(Border.BackgroundProperty, "Z.Orange");
                ((TextBlock)badge.Child).HorizontalAlignment = HorizontalAlignment.Center;
                g.Children.Add(badge);
            }
            tools.Children.Add(chatBtn);
        }
        var menu = MenuItems(it, m);
        if (menu.Count > 0)
        {
            tools.Children.Add(Tv.Icon(Tv.IcoMore, m.TogglePanelMenu, "More", m.PanelMenu ? "Z.Fill" : "Transparent", 18, "Z.Ink2", 3));
        }
        tools.Children.Add(Tv.Icon(Tv.IcoX, m.CloseDetail, "Close", "Z.CloseFill", 11, "Z.Ink2", 3));
        Grid.SetColumn(tools, 1);
        head.Children.Add(tools);
        shell.Children.Add(head);

        // ---- Tabs ----
        var tabs = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(14, 2, 14, 0) };
        void Tab(string id, string label, string? count = null)
        {
            var on = tab == id;
            var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            if (id == "chat")
            {
                var ic = Ui.Glyph(Tv.IcoChat, 13, on ? "Z.Ink" : "Z.Muted", 2);
                ic.Margin = new Thickness(0, 0, 5, 0);
                row.Children.Add(ic);
            }
            row.Children.Add(Ui.Text(label, 13.5, on ? FontWeights.SemiBold : FontWeights.Normal, on ? "Z.Ink" : "Z.Muted"));
            if (count is { Length: > 0 })
            {
                var n = Ui.Text(count, 12.5, fg: "Z.Muted");
                n.Margin = new Thickness(8, 0, 0, 0);
                row.Children.Add(n);
            }
            var under = new Border { Height = 2, VerticalAlignment = VerticalAlignment.Bottom }.Res(Border.BackgroundProperty, on ? "Z.Ink" : "Z.Surface");
            var cell = new Grid { MinHeight = 38, Margin = new Thickness(0, 0, 18, 0), Background = Brushes.Transparent };
            cell.Children.Add(row);
            cell.Children.Add(under);
            var b = Tv.Bare(cell, () => m.SetPanelTab(id), label + (count is { Length: > 0 } ? ", " + count : ""));
            AutomationProperties.SetItemStatus(b, on ? "selected" : "");
            tabs.Children.Add(b);
        }
        Tab("overview", "Overview");
        if (!it.Own)
        {
            var n = m.TaskChatSummary(it).Count;
            Tab("chat", "Chat", n > 0 ? n.ToString(System.Globalization.CultureInfo.InvariantCulture) : null);
        }
        Tab("activity", "Activity");
        var tabsWrap = new StackPanel();
        tabsWrap.Children.Add(tabs);
        tabsWrap.Children.Add(new Border { Height = 1 }.Res(Border.BackgroundProperty, "Z.Line"));
        Grid.SetRow(tabsWrap, 1);
        shell.Children.Add(tabsWrap);

        // ---- Body ----
        var body = new StackPanel { Margin = new Thickness(14, 14, 14, 18) };
        switch (tab)
        {
            case "chat":
                ChatTab(body, it, m);
                break;
            case "activity":
                ActivityTab(body, it, m);
                break;
            default:
                Overview(body, it, m);
                break;
        }
        var scroll = new ScrollViewer { Content = body, Style = (Style)Application.Current.FindResource("Z.Scroll"), Focusable = false };
        var offsetKey = it.Key + ":" + tab;
        scroll.ScrollChanged += (_, _) => Offsets[offsetKey] = scroll.VerticalOffset;
        scroll.Loaded += (_, _) =>
        {
            if (tab == "chat")
            {
                scroll.ScrollToEnd();
            }
            else if (Offsets.TryGetValue(offsetKey, out var y))
            {
                scroll.ScrollToVerticalOffset(y);
            }
        };
        Grid.SetRow(scroll, 2);
        shell.Children.Add(scroll);

        if (tab == "chat")
        {
            var composer = Composer(it, m);
            Grid.SetRow(composer, 3);
            shell.Children.Add(composer);
        }

        // The ⋯ menu over the body, top right.
        var root = new Grid();
        root.Children.Add(shell);
        if (m.PanelMenu && menu.Count > 0)
        {
            var list = new StackPanel { Margin = new Thickness(4) };
            foreach (var (label, danger, run) in menu)
            {
                var row = Ui.Text(label, 13.5, FontWeights.Normal, danger ? "Z.RedText" : "Z.Ink");
                var cell = new Border { Padding = new Thickness(12, 0, 16, 0), MinHeight = 40, Child = row, CornerRadius = new CornerRadius(8), Background = Brushes.Transparent };
                row.VerticalAlignment = VerticalAlignment.Center;
                var b = Tv.Bare(cell, run, label);
                b.MouseEnter += (_, _) => cell.SetResourceReference(Border.BackgroundProperty, "Z.Hover");
                b.MouseLeave += (_, _) => cell.Background = Brushes.Transparent;
                list.Children.Add(b);
            }
            var pop = new Border
            {
                Child = list, CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1), HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 50, 14, 0), MinWidth = 170,
                Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 24, ShadowDepth = 6, Opacity = 0.18, Direction = 270 },
            }.Res(Border.BackgroundProperty, "Z.Surface").Res(Border.BorderBrushProperty, "Z.Line");
            var catcher = new Border { Background = Brushes.Transparent };
            catcher.MouseLeftButtonDown += (_, e) =>
            {
                m.TogglePanelMenu();
                e.Handled = true;
            };
            root.Children.Add(catcher);
            root.Children.Add(pop);
            pop.Loaded += (_, _) => (list.Children.OfType<Button>().FirstOrDefault())?.Focus();
        }

        var card = new Border
        {
            Child = root, CornerRadius = new CornerRadius(16), Margin = new Thickness(12, 52, 12, 14), ClipToBounds = true,
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 30, ShadowDepth = 8, Opacity = 0.25, Direction = 270 },
        }.Res(Border.BackgroundProperty, "Z.Surface");
        card.MouseLeftButtonDown += (_, e) => e.Handled = true; // a click inside never reaches the scrim
        AutomationProperties.SetName(card, "Task: " + t.Title);
        return card;
    }

    private static List<(string Label, bool Danger, Action Run)> MenuItems(TaskItem it, TrayModel m)
    {
        if (it.Own)
        {
            if (TeamTaskEngine.IsFinished(it.Task))
            {
                return [("Delete to-do", true, () => m.OwnMenu(it, "delete"))];
            }
            return
            [
                ("Move to tomorrow", false, () => m.OwnMenu(it, "tomorrow")), ("Edit", false, () => m.OwnMenu(it, "edit")),
                ("Delete to-do", true, () => m.OwnMenu(it, "delete")),
            ];
        }
        var status = TeamTaskEngine.StatusOf(it.Task);
        var list = new List<(string, bool, Action)>
        {
            ("Ask " + m.OwnerFirstStart, false, () =>
            {
                m.SetPanelTab("chat");
                m.ShowSnack($"Ask {m.OwnerFirst} here. Everyone on the task sees it.", [("OK", m.CloseSnack)]);
            }),
        };
        if (!TeamTaskEngine.IsFinished(it.Task) && status is not ("review" or "cantdo"))
        {
            list.Add(("Can’t do it", true, () => m.StartPanelCard("cantdo")));
        }
        return list;
    }

    // ---- Overview ---------------------------------------------------------------------------------

    private static void Overview(StackPanel body, TaskItem it, TrayModel m)
    {
        var t = it.Task;
        var c = it.Ctx;
        var title = Ui.Text(t.Title, 18, FontWeights.Bold, wrap: true);
        title.LineHeight = 24;
        body.Children.Add(title);
        var meta = it.Own
            ? "Your own to-do" + (t.CreatedAt is { } at ? " · added " + TeamTaskEngine.Sent(at, m.Now) : "")
              + (it.Local is { } own && TaskBoard.OwnLine(own, m.TodoSharingOn) == TaskBoard.SharedTodoLine ? " · you and your admin see it" : " · only you see it")
            : TeamTaskEngine.Meta(t, c);
        var metaText = Ui.Text(meta, 12.5, fg: "Z.Muted", wrap: true);
        metaText.Margin = new Thickness(0, 4, 0, 14);
        body.Children.Add(metaText);

        body.Children.Add(Happening(it, m));

        if (!it.Own)
        {
            body.Children.Add(Journey(TeamTaskEngine.Journey(t, c)));
            body.Children.Add(People(it, m));
        }

        var status = TeamTaskEngine.StatusOf(t);
        var memberBtns = !it.Own && !TeamTaskEngine.IsFinished(t) && status != "review";
        var rows = TeamTaskEngine.StepRows(t, c);
        if (rows.Count > 0 || memberBtns)
        {
            var steps = new StackPanel { Margin = new Thickness(0, 18, 0, 0) };
            steps.Children.Add(Tv.Caps(TeamTaskEngine.StepsHead(t)));
            if (rows.Count == 0)
            {
                steps.Children.Add(Ui.Text("No steps. Mark it done when it’s finished.", 13, fg: "Z.Muted", wrap: true));
            }
            for (var i = 0; i < rows.Count; i++)
            {
                steps.Children.Add(Step(it, m, rows[i], i == rows.Count - 1, TeamTaskEngine.InOrder(t)));
            }
            if (memberBtns && m.PanelCard is null)
            {
                var row = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
                var help = Tv.Btn("Need help", false, () => m.StartPanelCard("help"), "Need help with " + t.Title, small: true);
                help.Margin = new Thickness(0, 0, 8, 0);
                row.Children.Add(help);
                if (status != "cantdo")
                {
                    row.Children.Add(Tv.Btn("Can’t do it", false, () => m.StartPanelCard("cantdo"), "Can’t do " + t.Title, small: true));
                }
                steps.Children.Add(row);
            }
            body.Children.Add(steps);
        }

        if (!string.IsNullOrWhiteSpace(t.Note))
        {
            var d = new StackPanel { Margin = new Thickness(0, 18, 0, 0) };
            d.Children.Add(Tv.Caps("Details"));
            d.Children.Add(Ui.Text(t.Note.Trim(), 13.5, fg: "Z.Ink2", wrap: true));
            body.Children.Add(d);
        }
    }

    /// <summary>What's happening: the kicker, the sentence, extra lines, then the buttons or the inline note.</summary>
    private static Border Happening(TaskItem it, TrayModel m)
    {
        var h = TeamTaskEngine.Happen(it.Task, it.Ctx);
        var (bg, line) = Tv.HapKeys(h.Tone);
        var inner = new StackPanel { Margin = new Thickness(16, 12, 14, 14) };
        inner.Children.Add(Tv.Caps("What’s happening"));
        var text = Ui.Text(h.Text, 15.5, FontWeights.SemiBold, wrap: true);
        text.LineHeight = 22;
        inner.Children.Add(text);
        foreach (var s in h.Sub)
        {
            var sub = Ui.Text(s, 12.5, fg: "Z.Muted", wrap: true);
            sub.Margin = new Thickness(0, 4, 0, 0);
            inner.Children.Add(sub);
        }
        if (m.PanelCard is { } card && !it.Own)
        {
            var ph = card == "help" ? "What do you need?" : "Why can’t you do it?";
            var box = Inputs.Keep("panel-card:" + it.Key + ":" + card, () =>
            {
                var tb = Tv.Field(ph, multiLine: true);
                tb.Loaded += (_, _) => tb.Focus();
                return tb;
            });
            box.Margin = new Thickness(0, 12, 0, 8);
            inner.Children.Add(box);
            var row = new WrapPanel();
            var send = Tv.Btn(card == "help" ? "Ask for help" : "Tell " + m.OwnerFirstStart, true, () =>
            {
                var note = box.Text;
                box.Text = "";
                m.SubmitPanelCard(it, note);
            });
            send.Margin = new Thickness(0, 0, 8, 0);
            row.Children.Add(send);
            row.Children.Add(Tv.Btn("Cancel", false, () => m.StartPanelCard(null)));
            inner.Children.Add(row);
        }
        else if (h.Buttons.Count > 0)
        {
            var row = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
            foreach (var b in h.Buttons)
            {
                var btn = Tv.Btn(b.Label, b.Primary, () => m.RunTaskAct(it, b.Act), b.Label + ": " + it.Task.Title);
                btn.Margin = new Thickness(0, 0, 8, 0);
                row.Children.Add(btn);
            }
            inner.Children.Add(row);
        }
        var grid = new Grid();
        grid.Children.Add(inner);
        grid.Children.Add(new Rectangle { Width = 4, HorizontalAlignment = HorizontalAlignment.Left }.Res(Shape.FillProperty, Tv.Edge(h.Tone)));
        var cardBorder = new Border { CornerRadius = new CornerRadius(14), BorderThickness = new Thickness(1), Child = grid, ClipToBounds = true }
            .Res(Border.BackgroundProperty, bg).Res(Border.BorderBrushProperty, line);
        cardBorder.SizeChanged += (_, e) => grid.Clip = new RectangleGeometry(new Rect(0, 0, Math.Max(0, e.NewSize.Width - 2), Math.Max(0, e.NewSize.Height - 2)), 13, 13);
        AutomationProperties.SetName(cardBorder, "What’s happening: " + h.Text);
        return cardBorder;
    }

    private static FrameworkElement Journey(IReadOnlyList<JourneyStep> steps)
    {
        var grid = new Grid { Margin = new Thickness(0, 16, 0, 0) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var bar = Tv.JourneyBar(steps, 6);
        Grid.SetColumnSpan(bar, steps.Count);
        for (var i = 0; i < steps.Count; i++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            var s = steps[i];
            var lbl = Ui.Text(s.Label, 11.5, s.State is JourneyState.Now ? FontWeights.SemiBold : FontWeights.Medium,
                s.State switch { JourneyState.Now => "Z.TvWorkInk", JourneyState.Next => "Z.Muted", JourneyState.Red => "Z.RedText", JourneyState.Ok => "Z.Ok", _ => "Z.Ink" },
                trim: true);
            lbl.Margin = new Thickness(0, 6, 4, 0);
            Grid.SetRow(lbl, 1);
            Grid.SetColumn(lbl, i);
            grid.Children.Add(lbl);
            if (s.Time.Length > 0)
            {
                var time = Ui.Text(s.Time, 10.5, fg: "Z.Muted", trim: true);
                time.SetResourceReference(TextBlock.FontFamilyProperty, "Z.Mono");
                time.Margin = new Thickness(0, 2, 4, 0);
                Grid.SetRow(time, 2);
                Grid.SetColumn(time, i);
                grid.Children.Add(time);
            }
        }
        grid.Children.Add(bar);
        return grid;
    }

    private static FrameworkElement People(TaskItem it, TrayModel m)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 18, 0, 0) };
        panel.Children.Add(Tv.Caps(TeamTaskEngine.PeopleHead(it.Task, it.Ctx)));
        foreach (var p in TeamTaskEngine.PeopleRows(it.Task, it.Ctx))
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 10) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var ring = new Grid { Width = 36, Height = 36, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
            var pct = p.Total > 0 ? (double)p.Done / p.Total : p.Word is "Done" or "Approved" ? 1 : 0;
            ring.Children.Add(new Ellipse { StrokeThickness = 2.5 }.Res(Shape.StrokeProperty, "Z.TvTrack"));
            if (pct > 0)
            {
                ring.Children.Add(Arc(pct, p.Tone == TaskTone.Ok || p.Word == "Approved" ? "Z.Ok" : "Z.Orange"));
            }
            var initials = p.IsMe ? TeamTaskEngine.Initials(m.Profile.DisplayName) : p.Initials;
            var face = Tv.Face(initials.Length > 0 ? initials : "•", 28, p.IsMe ? "Z.TvMineBg" : "Z.Fill", "Z.Ink");
            face.HorizontalAlignment = HorizontalAlignment.Center;
            face.VerticalAlignment = VerticalAlignment.Center;
            ring.Children.Add(face);
            row.Children.Add(ring);
            var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            texts.Children.Add(Ui.Text(p.Name, 13.5, FontWeights.SemiBold, trim: true));
            if (p.Total > 0)
            {
                texts.Children.Add(Ui.Text($"{p.Done} of {p.Total} {(p.Total == 1 ? "step" : "steps")}", 12, fg: "Z.Muted"));
            }
            Grid.SetColumn(texts, 1);
            row.Children.Add(texts);
            var word = Tv.Pill(p.Word, p.Tone == TaskTone.Neutral ? TaskTone.Grey : p.Tone, 11.5);
            if (word.Child is StackPanel sp && sp.Children.Count > 1)
            {
                sp.Children.RemoveAt(0); // the people pills carry no dot
            }
            Grid.SetColumn(word, 2);
            row.Children.Add(word);
            AutomationProperties.SetName(row, $"{p.Name}: {p.Word}");
            panel.Children.Add(row);
        }
        return panel;
    }

    /// <summary>A progress ring arc from the top, clockwise, <paramref name="pct"/> of the way round.</summary>
    private static FrameworkElement Arc(double pct, string key)
    {
        if (pct >= 0.999)
        {
            return new Ellipse { StrokeThickness = 2.5 }.Res(Shape.StrokeProperty, key);
        }
        const double r = 16.75, cx = 18, cy = 18;
        var angle = pct * 2 * Math.PI;
        var end = new Point(cx + r * Math.Sin(angle), cy - r * Math.Cos(angle));
        var fig = new PathFigure { StartPoint = new Point(cx, cy - r) };
        fig.Segments.Add(new ArcSegment(end, new Size(r, r), 0, pct > 0.5, SweepDirection.Clockwise, true));
        return new Path { Data = new PathGeometry([fig]), StrokeThickness = 2.5, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round }
            .Res(Shape.StrokeProperty, key);
    }

    private static FrameworkElement Step(TaskItem it, TrayModel m, StepRow s, bool last, bool inOrder)
    {
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // The tick circle (a line under it joins the steps of an in-order task).
        var col = new Grid { Width = 26, Margin = new Thickness(0, 0, 10, 0) };
        if (inOrder && !last)
        {
            col.Children.Add(new Border { Width = 2, Margin = new Thickness(0, 26, 0, -4), HorizontalAlignment = HorizontalAlignment.Center }
                .Res(Border.BackgroundProperty, s.Done ? "Z.Ok" : "Z.Line"));
        }
        var circle = new Grid { Width = 24, Height = 24, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 1, 0, 0) };
        if (s.Done)
        {
            circle.Children.Add(new Ellipse().Res(Shape.FillProperty, "Z.Ok"));
            circle.Children.Add(Ui.Glyph(Tv.IcoTick, 13, "Z.Surface", 3));
        }
        else
        {
            var e = new Ellipse { StrokeThickness = s.State == "now" ? 2.5 : 1.8 }.Res(Shape.StrokeProperty, s.State == "now" ? "Z.Orange" : "Z.Line2");
            if (s.State == "blocked")
            {
                e.StrokeDashArray = [2, 2];
            }
            e.SetResourceReference(Shape.FillProperty, "Z.Surface");
            circle.Children.Add(e);
        }
        var tick = Tv.Bare(circle, () => m.TickStep(it, s.Index),
            s.Done ? $"Step {s.Index + 1} done: {s.Title}" : s.CanTick ? $"Tick step {s.Index + 1}: {s.Title}" : $"Step {s.Index + 1}: {s.Title}. {(s.Lock.Length > 0 ? s.Lock : TeamTaskEngine.WhoseStep(s))}",
            s.CanTick ? "Tick this step" : s.Lock.Length > 0 ? s.Lock : null);
        tick.VerticalAlignment = VerticalAlignment.Top;
        if (s.Done)
        {
            tick.IsHitTestVisible = false;
        }
        col.Children.Add(tick);
        row.Children.Add(col);

        var texts = new StackPanel { Margin = new Thickness(0, 3, 6, 12) };
        var title = Ui.Text(s.Title, 13.5, s.State == "now" ? FontWeights.SemiBold : FontWeights.Normal, s.Done ? "Z.Muted" : s.State == "blocked" ? "Z.Muted" : "Z.Ink", wrap: true);
        texts.Children.Add(title);
        if (s.By.Length > 0)
        {
            texts.Children.Add(Ui.Text(s.By, 12, fg: "Z.Muted"));
        }
        else if (s.State == "blocked" && s.Lock.Length > 0)
        {
            texts.Children.Add(Ui.Text(s.Lock, 12, fg: "Z.Muted"));
        }
        Grid.SetColumn(texts, 1);
        row.Children.Add(texts);
        if (!it.Own)
        {
            FrameworkElement owner = s.OwnerId is null
                ? new Border { Width = 24, Height = 24, CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1.5), Child = Ui.Text("•", 12, fg: "Z.Muted") }
                    .Res(Border.BorderBrushProperty, "Z.Faint")
                : Tv.Face(s.OwnerIsMe ? TeamTaskEngine.Initials(m.Profile.DisplayName) is { Length: > 0 } mi ? mi : "Me" : s.OwnerInitials, 24, s.OwnerIsMe ? "Z.TvMineBg" : "Z.Fill");
            if (owner is Border { Child: TextBlock tb } && s.OwnerId is null)
            {
                tb.HorizontalAlignment = HorizontalAlignment.Center;
                tb.VerticalAlignment = VerticalAlignment.Center;
            }
            owner.VerticalAlignment = VerticalAlignment.Top;
            owner.ToolTip = s.OwnerName;
            Grid.SetColumn(owner, 2);
            row.Children.Add(owner);
        }
        var wrap = new Border { CornerRadius = new CornerRadius(10), Padding = new Thickness(6, 4, 6, 0), Margin = new Thickness(-6, 0, -6, 2), Child = row };
        if (s.State == "now")
        {
            wrap.SetResourceReference(Border.BackgroundProperty, "Z.TvWorkBg");
        }
        return wrap;
    }

    // ---- Chat ---------------------------------------------------------------------------------------

    private static void ChatTab(StackPanel body, TaskItem it, TrayModel m)
    {
        var t = it.Task;
        var c = it.Ctx;
        var h = TeamTaskEngine.Happen(t, c);
        // The task in a line, with who sees this chat.
        var top = new StackPanel { Margin = new Thickness(14, 10, 12, 12) };
        top.Children.Add(Ui.Text(t.Title, 14, FontWeights.Bold, wrap: true));
        var say = Ui.Text(h.Text, 13, fg: "Z.Ink2", wrap: true);
        say.Margin = new Thickness(0, 4, 0, 10);
        top.Children.Add(say);
        var who = new Grid();
        who.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        who.ColumnDefinitions.Add(new ColumnDefinition());
        who.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var people = TeamTaskEngine.People(t, c);
        var me = TeamTaskEngine.Me(t, c);
        var others = people.Where(p => p.MemberId != me && !(t.People is null)).Select(p => p.Name).ToList();
        var faces = Tv.Faces([.. people.Select(p => p.MemberId == me || t.People is null ? TeamTaskEngine.Initials(m.Profile.DisplayName) : TeamTaskEngine.Initials(p.Name)),
            TeamTaskEngine.Initials(m.Status?.OwnerName ?? m.OwnerFirstStart)]);
        faces.Margin = new Thickness(0, 0, 8, 0);
        who.Children.Add(faces);
        var names = TeamTaskEngine.List([m.OwnerFirstStart, .. others.Select(n => n.Split(' ')[0]), "you"]) + " see this chat";
        var nt = Ui.Text(names, 12.5, fg: "Z.Muted", wrap: true);
        nt.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(nt, 1);
        who.Children.Add(nt);
        var details = Tv.Bare(new TextBlock { Text = "Task details", FontSize = 13, FontWeight = FontWeights.SemiBold, TextDecorations = TextDecorations.Underline }
            .Res(TextBlock.ForegroundProperty, "Z.Link"), () => m.SetPanelTab("overview"), "Task details");
        details.VerticalAlignment = VerticalAlignment.Center;
        details.Margin = new Thickness(8, 0, 0, 0);
        Grid.SetColumn(details, 2);
        who.Children.Add(details);
        top.Children.Add(who);
        body.Children.Add(Tv.EdgeCard(top, h.Tone, 12));

        if (t.GroupId is not null && c.GroupName is { Length: > 0 } group)
        {
            var also = new Grid { Margin = new Thickness(0, 12, 0, 4) };
            also.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            also.ColumnDefinitions.Add(new ColumnDefinition());
            var ic = Ui.Glyph(Tv.IcoChat, 12, "Z.Muted", 2);
            ic.VerticalAlignment = VerticalAlignment.Top;
            ic.Margin = new Thickness(0, 3, 6, 0);
            also.Children.Add(ic);
            var at = Ui.Text($"This task is also posted in the {group} chat. Replies on the card land here.", 12.5, fg: "Z.Muted", wrap: true);
            Grid.SetColumn(at, 1);
            also.Children.Add(at);
            body.Children.Add(also);
        }

        var lines = m.TaskChat(it);
        if (lines.Count == 0)
        {
            var empty = Ui.Text("No messages yet. Ask a question or share an update: everyone on the task sees it.", 13, fg: "Z.Muted", wrap: true);
            empty.Margin = new Thickness(4, 16, 4, 0);
            empty.TextAlignment = TextAlignment.Center;
            body.Children.Add(empty);
        }
        foreach (var l in lines)
        {
            body.Children.Add(Bubble(l));
        }
    }

    private static FrameworkElement Bubble(TaskChatLine l)
    {
        var row = new Grid { Margin = new Thickness(0, 12, 0, 0) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var face = Tv.Face(l.Initials.Length > 0 ? l.Initials : "•", 26, l.Mine ? "Z.TvMineBg" : l.Owner ? "Z.Inv" : "Z.Fill", l.Owner && !l.Mine ? "Z.OnInv" : "Z.Ink");
        face.VerticalAlignment = VerticalAlignment.Bottom;
        var texts = new StackPanel();
        texts.Children.Add(Ui.Text(l.Who + " · " + (l.Sending ? "Sending…" : TeamTaskEngine.Clock(l.At)), 11.5, fg: "Z.Muted"));
        var words = Ui.Text(l.Text, 13.5, wrap: true);
        words.Margin = new Thickness(0, 3, 0, 0);
        texts.Children.Add(words);
        var bubble = new Border
        {
            CornerRadius = new CornerRadius(14), Padding = new Thickness(12, 8, 12, 10), Child = texts, MaxWidth = 250,
            HorizontalAlignment = l.Mine ? HorizontalAlignment.Right : HorizontalAlignment.Left, Opacity = l.Sending ? 0.7 : 1,
        }.Res(Border.BackgroundProperty, l.Mine ? "Z.TvMineBg" : "Z.TvTheirsBg");
        if (l.Mine)
        {
            face.Margin = new Thickness(8, 0, 0, 0);
            Grid.SetColumn(bubble, 1);
            Grid.SetColumn(face, 2);
        }
        else
        {
            face.Margin = new Thickness(0, 0, 8, 0);
            Grid.SetColumn(bubble, 1);
        }
        row.Children.Add(face);
        row.Children.Add(bubble);
        AutomationProperties.SetName(row, $"{l.Who}: {l.Text}");
        return row;
    }

    private static FrameworkElement Composer(TaskItem it, TrayModel m)
    {
        var wrap = new Border { BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(12, 10, 12, 10) }
            .Res(Border.BorderBrushProperty, "Z.Line").Res(Border.BackgroundProperty, "Z.Surface");
        var key = it.Key;
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var box = Inputs.Keep("panel-chat:" + key, () =>
        {
            var tb = Tv.Field("Message everyone on this task…", multiLine: true);
            Tv.EnterSends(tb, text =>
            {
                if (m.Item(key) is { } item)
                {
                    m.SendTaskChat(item, text);
                }
            });
            tb.Loaded += (_, _) => tb.Dispatcher.BeginInvoke(() => tb.Focus(), DispatcherPriority.Input);
            return tb;
        });
        grid.Children.Add(box);
        var send = Tv.Send(() =>
        {
            var text = box.Text;
            if (!string.IsNullOrWhiteSpace(text) && m.Item(key) is { } item)
            {
                box.Text = "";
                m.SendTaskChat(item, text);
            }
        }, "Send to everyone on this task");
        send.Margin = new Thickness(8, 0, 0, 2);
        Grid.SetColumn(send, 1);
        grid.Children.Add(send);
        var stack = new StackPanel();
        stack.Children.Add(grid);
        var hint = Ui.Text("Enter sends · Shift + Enter for a new line", 11.5, fg: "Z.Muted");
        hint.Margin = new Thickness(4, 6, 0, 0);
        stack.Children.Add(hint);
        wrap.Child = stack;
        return wrap;
    }

    // ---- Activity ---------------------------------------------------------------------------------

    private static void ActivityTab(StackPanel body, TaskItem it, TrayModel m)
    {
        var lines = new List<(string Text, DateTimeOffset At)>();
        if (it.Own && it.Local is { } own)
        {
            lines.Add(("You added it", own.SentAt));
            lines.AddRange(own.Activity.Select(a => (a.Text, a.At)));
            if (own.DoneAt is { } done)
            {
                lines.Add(("You ticked the last step. Done", done));
            }
        }
        else
        {
            lines.AddRange((it.Task.Activity ?? []).Select(a => (OwnerTasks.Describe(a, m.OwnerFirstStart), a.At)));
        }
        if (lines.Count == 0)
        {
            body.Children.Add(Ui.Text("Nothing yet.", 13, fg: "Z.Muted"));
            return;
        }
        var first = true;
        foreach (var (text, at) in lines.OrderByDescending(l => l.At))
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 12) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var dot = Ui.Dot(8, first ? "Z.Orange" : "Z.Line2");
            dot.VerticalAlignment = VerticalAlignment.Top;
            dot.Margin = new Thickness(0, 6, 10, 0);
            row.Children.Add(dot);
            var tx = Ui.Text(text, 13.5, first ? FontWeights.SemiBold : FontWeights.Normal, wrap: true);
            Grid.SetColumn(tx, 1);
            row.Children.Add(tx);
            var when = Ui.Text(TeamTaskEngine.Sent(at, m.Now), 11.5, fg: "Z.Muted");
            when.Margin = new Thickness(8, 2, 0, 0);
            Grid.SetColumn(when, 2);
            row.Children.Add(when);
            body.Children.Add(row);
            first = false;
        }
    }
}

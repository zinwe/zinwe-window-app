using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Zinwe.Agent.Core.Tasks;

namespace Zinwe.Agent.Tray.Views;

/// <summary>
/// v1.4 · the task card (prototype card.html): the status pill, the due pill and High, the title, the member's sentence
/// with its tone dot, the journey bar, faces, the task chat count and the one button. In a chat thread
/// (<paramref name="thread"/> on <see cref="Build"/>) a footer adds the last reply, "N replies", "Open task chat" and a
/// reply box whose words go into the task chat. A click on the card opens the member task panel.
/// </summary>
internal static class TaskCardV2
{
    public static FrameworkElement Build(TaskItem it, TrayModel m, bool thread = false)
    {
        var t = it.Task;
        var c = it.Ctx;
        var h = TeamTaskEngine.Happen(t, c);
        var pill = TeamTaskEngine.Pill(t, c);
        var due = TeamTaskEngine.Due(t, c);
        var tone = h.Tone == TaskTone.Neutral && pill.Tone == TaskTone.Needs ? TaskTone.Needs : h.Tone;
        if (it.Own && !TeamTaskEngine.IsFinished(t))
        {
            tone = TaskTone.Work;
        }

        var body = new StackPanel { Margin = new Thickness(14, 12, 12, thread ? 10 : 12) };
        var top = new WrapPanel();
        void Top(FrameworkElement? el)
        {
            if (el is null)
            {
                return;
            }
            el.Margin = new Thickness(0, 0, 6, 4);
            top.Children.Add(el);
        }
        Top(Tv.Pill(pill.Label, pill.Tone));
        Top(Tv.Due(due));
        if (string.Equals(t.Priority, "high", StringComparison.OrdinalIgnoreCase))
        {
            Top(Tv.High());
        }
        body.Children.Add(top);

        var title = Ui.Text(t.Title, 14, FontWeights.SemiBold, wrap: true);
        title.Margin = new Thickness(0, 4, 0, 6);
        body.Children.Add(title);
        body.Children.Add(Tv.Say(TeamTaskEngine.CardLine(t, c), tone));
        if (!it.Own)
        {
            var bar = Tv.JourneyBar(TeamTaskEngine.Journey(t, c));
            bar.Margin = new Thickness(0, 10, 0, 0);
            body.Children.Add(bar);
        }

        // Foot: faces (a team task), the chat count, the button.
        var foot = new Grid { Margin = new Thickness(0, 8, 0, 0) };
        foot.ColumnDefinitions.Add(new ColumnDefinition());
        foot.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        foot.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var people = TeamTaskEngine.People(t, c);
        if (people.Count > 1)
        {
            var who = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            who.Children.Add(Tv.Faces([.. people.Select(p => TeamTaskEngine.Initials(p.Name))]));
            var names = people.Count > 3 ? people.Count + " people" : TeamTaskEngine.List(people.Select(p => p.Name.Split(' ')[0]));
            var n = Ui.Text(names, 12.5, fg: "Z.Ink2", trim: true);
            n.Margin = new Thickness(8, 0, 0, 0);
            n.VerticalAlignment = VerticalAlignment.Center;
            who.Children.Add(n);
            foot.Children.Add(who);
        }
        var key = it.Key;
        if (!it.Own)
        {
            var chat = m.TaskChatSummary(it);
            var count = Tv.ChatCount(chat.Count, chat.Unread, () => m.OpenPanel(key, "chat"));
            Grid.SetColumn(count, 1);
            foot.Children.Add(count);
        }
        if (h.Buttons.FirstOrDefault() is { } b)
        {
            var btn = Tv.Btn(b.Label, b.Primary, () => m.RunTaskAct(it, b.Act), b.Label + ": " + t.Title, small: true);
            btn.Margin = new Thickness(6, 0, 0, 0);
            Grid.SetColumn(btn, 2);
            foot.Children.Add(btn);
        }
        if (foot.Children.Count > 0)
        {
            body.Children.Add(foot);
        }

        var stack = new StackPanel();
        stack.Children.Add(body);
        if (thread && !it.Own)
        {
            stack.Children.Add(Footer(it, m));
        }

        var card = Tv.EdgeCard(stack, tone);
        card.Cursor = Cursors.Hand;
        card.MouseLeftButtonUp += (_, e) =>
        {
            if (OnControl(e.OriginalSource as DependencyObject, card))
            {
                return;
            }
            m.OpenPanel(key, thread ? "chat" : "overview");
            e.Handled = true;
        };
        card.MouseEnter += (_, _) => card.SetResourceReference(Border.BorderBrushProperty, "Z.Line2");
        card.MouseLeave += (_, _) => card.SetResourceReference(Border.BorderBrushProperty, "Z.Line");
        AutomationProperties.SetName(card, $"{t.Title}. {pill.Label}. {TeamTaskEngine.CardLine(t, c)}");
        if (thread)
        {
            card.MaxWidth = 300;
            card.HorizontalAlignment = HorizontalAlignment.Left;
        }
        return card;
    }

    /// <summary>True when the click landed on a control inside the card (a button, the reply box), not on the card itself.</summary>
    private static bool OnControl(DependencyObject? d, DependencyObject card)
    {
        for (var x = d; x is not null && !ReferenceEquals(x, card); x = x is Visual ? VisualTreeHelper.GetParent(x) : LogicalTreeHelper.GetParent(x))
        {
            if (x is ButtonBase or TextBox)
            {
                return true;
            }
        }
        return false;
    }

    private static void SendFrom(TextBox box, TrayModel m, int key)
    {
        var text = box.Text;
        if (!string.IsNullOrWhiteSpace(text) && m.Item(key) is { } item)
        {
            box.Text = "";
            m.ReplyOnCard(item, text);
        }
    }

    /// <summary>The thread footer: the last reply, "N replies · 1 new", "Open task chat", and "Reply to this task…".</summary>
    private static FrameworkElement Footer(TaskItem it, TrayModel m)
    {
        var key = it.Key;
        var chat = m.TaskChatSummary(it);
        var foot = new StackPanel { Margin = new Thickness(0) };
        foot.SetResourceReference(Panel.BackgroundProperty, "Z.Surface2");
        var rule = new Border { Height = 1 }.Res(Border.BackgroundProperty, "Z.Line");
        foot.Children.Add(rule);
        var inner = new StackPanel { Margin = new Thickness(14, 8, 12, 10) };
        if (chat.LastText is { Length: > 0 } last)
        {
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var face = chat.LastWho == m.OwnerFirstStart ? Tv.Face(TeamTaskEngine.Initials(m.Status?.OwnerName ?? chat.LastWho), 22, "Z.Inv", "Z.OnInv")
                : Tv.Face(TeamTaskEngine.Initials(chat.LastWho == "You" ? m.MyName : chat.LastWho), 22);
            face.Margin = new Thickness(0, 0, 8, 0);
            row.Children.Add(face);
            var text = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center };
            text.Inlines.Add(new System.Windows.Documents.Run((chat.LastWho ?? "") + " ") { FontWeight = FontWeights.Bold });
            text.Inlines.Add(new System.Windows.Documents.Run(last));
            text.SetResourceReference(TextBlock.ForegroundProperty, "Z.Ink");
            Grid.SetColumn(text, 1);
            row.Children.Add(text);
            if (chat.LastAt is { } at)
            {
                var when = Ui.Text(TeamTaskEngine.Clock(at), 11.5, fg: "Z.Muted");
                when.Margin = new Thickness(8, 0, 0, 0);
                when.VerticalAlignment = VerticalAlignment.Center;
                Grid.SetColumn(when, 2);
                row.Children.Add(when);
            }
            inner.Children.Add(Tv.Bare(row, () => m.OpenPanel(key, "chat"), "Open the task chat. Last reply: " + last));
        }
        var line = new Grid { Margin = new Thickness(0, 4, 0, 4) };
        line.ColumnDefinitions.Add(new ColumnDefinition());
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var n = new StackPanel { Orientation = Orientation.Horizontal };
        if (chat.Unread > 0)
        {
            var dot = Ui.Dot(7, "Z.Orange");
            dot.Margin = new Thickness(0, 0, 6, 0);
            n.Children.Add(dot);
        }
        n.Children.Add(Ui.Glyph(Tv.IcoChat, 13, "Z.Ink2", 2));
        var replies = chat.Count == 0 ? "No replies yet" : $"{chat.Count} {(chat.Count == 1 ? "reply" : "replies")}" + (chat.Unread > 0 ? $" · {chat.Unread} new" : "");
        var rt = Ui.Text(replies, 13, fg: "Z.Ink");
        rt.Margin = new Thickness(6, 0, 0, 0);
        n.Children.Add(rt);
        line.Children.Add(Tv.Bare(n, () => m.OpenPanel(key, "chat"), replies));
        var open = Tv.Bare(Ui.Text("Open task chat", 13, fg: "Z.Ink"), () => m.OpenPanel(key, "chat"), "Open task chat");
        Grid.SetColumn(open, 1);
        line.Children.Add(open);
        inner.Children.Add(line);

        // "Reply to this task…": the words go into the task chat.
        var boxRow = new Grid { Margin = new Thickness(0, 4, 0, 0) };
        boxRow.ColumnDefinitions.Add(new ColumnDefinition());
        boxRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var box = Inputs.Keep("card-reply:" + key, () =>
        {
            var tb = Tv.Field("Reply to this task…");
            tb.MinHeight = 38;
            tb.Padding = new Thickness(10, 0, 44, 0);
            tb.PreviewKeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter)
                {
                    e.Handled = true;
                    SendFrom(tb, m, key);
                }
            };
            return tb;
        });
        void Send() => SendFrom(box, m, key);
        boxRow.Children.Add(box);
        var sq = new Border { Width = 30, Height = 30, CornerRadius = new CornerRadius(8), Child = Ui.Glyph(Tv.IcoSend, 15, "Z.OnOrange", 2.4) }
            .Res(Border.BackgroundProperty, "Z.Orange");
        var send = Tv.Bare(sq, Send, "Send reply");
        send.HorizontalAlignment = HorizontalAlignment.Right;
        send.VerticalAlignment = VerticalAlignment.Center;
        send.Margin = new Thickness(0, 0, 4, 0);
        boxRow.Children.Add(send);
        inner.Children.Add(boxRow);
        foot.Children.Add(inner);
        return foot;
    }
}

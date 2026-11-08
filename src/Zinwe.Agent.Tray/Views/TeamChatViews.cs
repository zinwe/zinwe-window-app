using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Zinwe.Agent.Core.Chat;
using Zinwe.Agent.Core.Tasks;

namespace Zinwe.Agent.Tray.Views;

/// <summary>
/// v1.4 · the Chat tab's list, phone-like (prototype WinApp board): a grey "Search chats and people" field, the pill chips
/// All · Unread · Groups, then taller rows: the admin chat pinned on top, groups, direct chats and the teammates not
/// written to yet ("Say hi to Fatima"). A row's ⋯ shows only on hover (Pin · Mark as read/unread · Mute · Report).
/// </summary>
public sealed class ChatListView : UserControl
{
    private readonly TextBox _search;
    private readonly WrapPanel _chips = new() { Margin = new Thickness(12, 8, 12, 4) };
    private readonly StackPanel _rows = new();
    private readonly TextBlock _empty;
    private string _shown = "";

    public ChatListView()
    {
        _search = new TextBox { Style = (Style)Application.Current.FindResource("Z.TvSearch"), Tag = "Search chats and people", Margin = new Thickness(12, 12, 12, 0) };
        AutomationProperties.SetName(_search, "Search chats and people");
        _search.TextChanged += (_, _) => Model?.SetChatQuery(_search.Text);
        _empty = Ui.Text("", 13, fg: "Z.Muted", wrap: true);
        _empty.Margin = new Thickness(24, 30, 24, 0);
        _empty.TextAlignment = TextAlignment.Center;
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition());
        grid.Children.Add(_search);
        Grid.SetRow(_chips, 1);
        grid.Children.Add(_chips);
        var list = new StackPanel { Margin = new Thickness(0, 4, 0, 16) };
        list.Children.Add(_rows);
        list.Children.Add(_empty);
        var scroll = new ScrollViewer { Content = list, Style = (Style)Application.Current.FindResource("Z.Scroll") };
        Grid.SetRow(scroll, 2);
        grid.Children.Add(scroll);
        Content = grid;
    }

    public TrayModel? Model { get; set; }

    public void FocusSearch() => Dispatcher.BeginInvoke(() => _search.Focus(), DispatcherPriority.Input);

    public void Render()
    {
        if (Model is not { } m)
        {
            return;
        }
        if (_search.Text != m.ChatQuery && !_search.IsKeyboardFocused)
        {
            _search.Text = m.ChatQuery;
        }
        var rows = m.ChatRows;
        var sig = string.Join("#", rows) + $"|{m.ChatFilter}|{m.ConvMenu}|{m.ChatUnreadRows}|{m.ChatGroupCount}|{m.Now:yyyyMMddHHmm}";
        if (sig == _shown)
        {
            return;
        }
        _shown = sig;
        _chips.Children.Clear();
        _chips.Children.Add(Tv.Chip("All", 0, m.ChatFilter == ChatChip.All, () => m.SetChatFilter(ChatChip.All)));
        _chips.Children.Add(Tv.Chip("Unread", m.ChatUnreadRows, m.ChatFilter == ChatChip.Unread, () => m.SetChatFilter(ChatChip.Unread)));
        _chips.Children.Add(Tv.Chip("Groups", m.ChatGroupCount, m.ChatFilter == ChatChip.Groups, () => m.SetChatFilter(ChatChip.Groups)));
        _rows.Children.Clear();
        foreach (var r in rows)
        {
            _rows.Children.Add(Row(m, r));
        }
        _empty.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        _empty.Text = m.ChatFilter == ChatChip.Unread && m.ChatQuery.Trim().Length == 0 ? "No unread chats."
            : m.ChatFilter == ChatChip.Groups && m.ChatQuery.Trim().Length == 0 ? "You're not in a group yet."
            : $"No chats match “{m.ChatQuery.Trim()}”.";
    }

    private static FrameworkElement Row(TrayModel m, ChatRowView r)
    {
        var bold = r.Unread > 0 || r.Marked;
        var grid = new Grid { Margin = new Thickness(16, 10, 8, 10), MinHeight = 46 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });

        // Avatar: initials on a tint, a group icon, a presence dot.
        var av = new Grid { Width = 44, Height = 44, Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
        var disc = new Border { CornerRadius = new CornerRadius(22), BorderThickness = new Thickness(1) }
            .Res(Border.BackgroundProperty, r.IsAdmin ? "Z.OrangeTint" : r.IsGroup ? "Z.OkTint" : "Z.Fill").Res(Border.BorderBrushProperty, "Z.Line");
        disc.Child = r.IsGroup
            ? Ui.Glyph(Tv.IcoGroup, 20, "Z.Ok", 2)
            : new TextBlock { Text = r.Initials, FontSize = 14, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
                .Res(TextBlock.ForegroundProperty, r.IsAdmin ? "Z.OrangeInk" : "Z.Ink");
        if (disc.Child is FrameworkElement glyph)
        {
            glyph.HorizontalAlignment = HorizontalAlignment.Center;
            glyph.VerticalAlignment = VerticalAlignment.Center;
        }
        av.Children.Add(disc);
        if (r.Status is { Length: > 0 } status)
        {
            var dot = new Border
            {
                Width = 11, Height = 11, CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(2),
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 1, 1),
            }.Res(Border.BackgroundProperty, status.Contains("break", StringComparison.OrdinalIgnoreCase) ? "Z.Warn" : "Z.Ok")
             .Res(Border.BorderBrushProperty, "Z.Surface");
            dot.ToolTip = status;
            av.Children.Add(dot);
        }
        grid.Children.Add(av);

        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        texts.Children.Add(Ui.Text(r.Name, 14.5, bold ? FontWeights.Bold : FontWeights.SemiBold, trim: true));
        var line = Ui.Text(r.Line, 13, bold ? FontWeights.Medium : FontWeights.Normal, bold ? "Z.Ink" : r.LineMuted ? "Z.Muted" : "Z.Ink2", trim: true);
        line.Margin = new Thickness(0, 2, 0, 0);
        texts.Children.Add(line);
        Grid.SetColumn(texts, 1);
        grid.Children.Add(texts);

        var side = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0), HorizontalAlignment = HorizontalAlignment.Right };
        var top = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        if (r.Pinned && !r.IsAdmin || r.IsAdmin)
        {
            var pin = Ui.Glyph(Tv.IcoPin, 12, "Z.Muted", 2);
            pin.Margin = new Thickness(0, 0, 6, 0);
            pin.VerticalAlignment = VerticalAlignment.Center;
            top.Children.Add(pin);
        }
        if (r.Time.Length > 0)
        {
            top.Children.Add(Ui.Text(r.Time, 12, bold ? FontWeights.SemiBold : FontWeights.Normal, bold ? "Z.Orange" : "Z.Muted"));
        }
        side.Children.Add(top);
        if (bold)
        {
            var badge = new Border
            {
                MinWidth = 18, Height = 18, CornerRadius = new CornerRadius(9), Padding = new Thickness(5, 0, 5, 0), Margin = new Thickness(0, 4, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Right,
                Child = Ui.Text(r.Unread > 0 ? (r.Unread > 9 ? "9+" : r.Unread.ToString(CultureInfo.InvariantCulture)) : "", 10.5, FontWeights.ExtraBold, "Z.OnOrange"),
            }.Res(Border.BackgroundProperty, "Z.Orange");
            ((TextBlock)badge.Child).HorizontalAlignment = HorizontalAlignment.Center;
            if (r.Unread == 0)
            {
                badge.MinWidth = 10;
                badge.Height = 10;
                badge.Padding = new Thickness(0);
            }
            side.Children.Add(badge);
        }
        if (r.Muted)
        {
            side.Children.Add(Ui.Text("Muted", 11, fg: "Z.Muted"));
        }
        Grid.SetColumn(side, 2);
        grid.Children.Add(side);

        var row = new Grid();
        var main = Tv.Bare(grid, () => m.OpenConv(r.Key), $"{r.Name}{(r.Unread > 0 ? $", {r.Unread} unread" : "")}{(r.Pinned || r.IsAdmin ? ", pinned" : "")}. {r.Line}");
        row.Children.Add(main);

        // ⋯ only on hover (or while its menu is open), so the rows stay calm.
        var menuOpen = m.ConvMenu == r.Key;
        var more = Tv.Icon(Tv.IcoMore, () => m.ToggleConvMenu(r.Key), "More for " + r.Name, menuOpen ? "Z.Fill" : "Transparent", 16, "Z.Ink2", 3);
        more.HorizontalAlignment = HorizontalAlignment.Right;
        more.VerticalAlignment = VerticalAlignment.Center;
        more.Margin = new Thickness(0, 0, 4, 0);
        more.Opacity = menuOpen ? 1 : 0;
        more.GotKeyboardFocus += (_, _) => more.Opacity = 1;
        more.LostKeyboardFocus += (_, _) => more.Opacity = menuOpen ? 1 : 0;
        row.MouseEnter += (_, _) =>
        {
            more.Opacity = 1;
            row.SetResourceReference(Panel.BackgroundProperty, "Z.Hover");
        };
        row.MouseLeave += (_, _) =>
        {
            more.Opacity = menuOpen ? 1 : 0;
            row.Background = Brushes.Transparent;
        };
        row.Background = Brushes.Transparent;
        row.Children.Add(more);

        if (!menuOpen)
        {
            return row;
        }
        var items = new List<(string Label, bool Red, Action Run)>();
        if (!r.IsAdmin)
        {
            items.Add((r.Pinned ? "Unpin" : "Pin to top", false, () => m.TogglePin(r.Key)));
        }
        items.Add((r.Unread > 0 || r.Marked ? "Mark as read" : "Mark as unread", false, () => m.ToggleUnread(r)));
        items.Add((r.Muted ? "Unmute" : "Mute", false, () => m.ToggleMute(r.Key)));
        if (!r.IsAdmin && !r.IsGroup && ChatKeys.Parse(r.Key) is ("conv", _))
        {
            items.Add(("Report " + r.Name.Split(' ')[0], true, () => m.OpenReport(r.Key, null)));
        }
        var pop = new StackPanel { Margin = new Thickness(4) };
        foreach (var (label, red, run) in items)
        {
            var cell = new Border { Padding = new Thickness(12, 0, 14, 0), MinHeight = 38, CornerRadius = new CornerRadius(8), Background = Brushes.Transparent, Child = Ui.Text(label, 13.5, fg: red ? "Z.RedText" : "Z.Ink") };
            ((TextBlock)cell.Child).VerticalAlignment = VerticalAlignment.Center;
            var b = Tv.Bare(cell, run, label);
            b.MouseEnter += (_, _) => cell.SetResourceReference(Border.BackgroundProperty, "Z.Hover");
            b.MouseLeave += (_, _) => cell.Background = Brushes.Transparent;
            pop.Children.Add(b);
        }
        var card = new Border
        {
            Child = pop, CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1), HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, -8, 14, 6), MinWidth = 170,
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 20, ShadowDepth = 5, Opacity = 0.16, Direction = 270 },
        }.Res(Border.BackgroundProperty, "Z.Surface").Res(Border.BorderBrushProperty, "Z.Line");
        card.Loaded += (_, _) => pop.Children.OfType<Button>().FirstOrDefault()?.Focus();
        var wrap = new StackPanel();
        wrap.Children.Add(row);
        wrap.Children.Add(card);
        return wrap;
    }
}

/// <summary>
/// v1.4 · a group or direct chat (prototype WinApp board): date lines, the other people's bubbles with their names in a
/// group, the member's own on the right with "Seen by N", quoted replies, task cards with the thread footer, system lines,
/// Reply and Report on a line, and the composer (read-only note in an announce group). The report card sits over it.
/// </summary>
public sealed class TeamThreadView : UserControl
{
    private readonly StackPanel _lines = new() { Margin = new Thickness(12, 8, 12, 12) };
    private readonly ScrollViewer _scroll;
    private readonly Border _composer = new() { BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(12, 8, 12, 10) };
    private readonly ContentControl _overlay = new() { Focusable = false };
    private readonly TextBox _input;
    private string _shown = "";
    private int _lastCount = -1;
    private string? _lastKey;

    public TeamThreadView()
    {
        _scroll = new ScrollViewer { Content = _lines, Style = (Style)Application.Current.FindResource("Z.Scroll") };
        _composer.SetResourceReference(Border.BorderBrushProperty, "Z.Line");
        _composer.SetResourceReference(Border.BackgroundProperty, "Z.Surface");
        _input = Tv.Field("Message · Shift+Enter for a new line", multiLine: true, aria: "Message");
        _input.MinHeight = 44;
        Tv.EnterSends(_input, text =>
        {
            if (Model?.SendTeam(text) == false)
            {
                _input.Text = text;
            }
        });
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition());
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.Children.Add(_scroll);
        Grid.SetRow(_composer, 1);
        grid.Children.Add(_composer);
        Grid.SetRowSpan(_overlay, 2);
        grid.Children.Add(_overlay);
        Content = grid;
    }

    public TrayModel? Model { get; set; }

    public void FocusComposer() => Dispatcher.BeginInvoke(() => _input.Focus(), DispatcherPriority.Input);

    public void Render()
    {
        if (Model is not { } m)
        {
            return;
        }
        _scroll.Background = ChatPatternBrush.For(m.ChatPattern);
        var lines = m.TeamThread;
        var (canPost, note) = m.ThreadPosting;
        var sig = m.ChatOpen + "|" + string.Join("#", lines.Select(l => $"{l.Message}{l.Sending}"))
            + $"|{m.TeamReplyTo?.Id}|{canPost}|{m.Report}|{m.Now:yyyyMMddHHmm}|{m.ChatPattern}|"
            + string.Join(",", lines.Where(l => l.Message.TaskId is not null).Select(l => l.Message.TaskId is { } id && m.ItemByRemote(id) is { } i
                ? System.Text.Json.JsonSerializer.Serialize(i.Task) + m.TaskChatSummary(i) : ""));
        if (sig != _shown)
        {
            _shown = sig;
            BuildLines(m, lines);
            BuildComposer(m, canPost, note);
            BuildReport(m);
            var newKey = _lastKey != m.ChatOpen;
            if (newKey || lines.Count != _lastCount)
            {
                var atEnd = newKey || _scroll.VerticalOffset >= _scroll.ScrollableHeight - 24;
                if (atEnd)
                {
                    Dispatcher.BeginInvoke(_scroll.ScrollToEnd, DispatcherPriority.Loaded);
                }
            }
            _lastCount = lines.Count;
            _lastKey = m.ChatOpen;
        }
    }

    private void BuildLines(TrayModel m, IReadOnlyList<TeamLine> lines)
    {
        _lines.Children.Clear();
        var group = m.OpenConversation?.IsGroup ?? false;
        if (lines.Count == 0)
        {
            var (title, _) = m.ThreadHeader;
            var hi = Ui.Text(group ? "No messages yet. Say hello to the group." : $"Say hi to {title.Split(' ')[0]}.", 13, fg: "Z.Muted", wrap: true);
            hi.TextAlignment = TextAlignment.Center;
            hi.Margin = new Thickness(20, 40, 20, 0);
            _lines.Children.Add(hi);
            return;
        }
        DateOnly? lastDay = null;
        var lastSeen = lines.Select((l, i) => (l, i)).LastOrDefault(x => x.l.Mine && !x.l.Sending).i;
        var lastMine = lines.Select((l, i) => (l, i)).LastOrDefault(x => x.l.Mine).i;
        for (var i = 0; i < lines.Count; i++)
        {
            var l = lines[i];
            var day = DateOnly.FromDateTime(l.Message.At.ToLocalTime().DateTime);
            if (l.Message.At != default && day != lastDay)
            {
                _lines.Children.Add(DateLine(day, m.Now));
                lastDay = day;
            }
            var prev = i > 0 ? lines[i - 1] : null;
            var next = i < lines.Count - 1 ? lines[i + 1] : null;
            if (l.Message.System)
            {
                var sys = Ui.Text(l.Message.Body, 12, fg: "Z.Muted", wrap: true);
                sys.TextAlignment = TextAlignment.Center;
                sys.Margin = new Thickness(24, 8, 24, 8);
                _lines.Children.Add(sys);
                continue;
            }
            if (l.Message.TaskId is { } taskId)
            {
                if (m.ItemByRemote(taskId) is { } item)
                {
                    var card = TaskCardV2.Build(item, m, thread: true);
                    card.Margin = new Thickness(group ? 34 : 0, 8, 0, 8);
                    _lines.Children.Add(card);
                }
                else
                {
                    var t = Ui.Text($"{l.Who} posted a task for the group.", 12, fg: "Z.Muted");
                    t.HorizontalAlignment = HorizontalAlignment.Center;
                    t.Margin = new Thickness(0, 8, 0, 8);
                    _lines.Children.Add(t);
                }
                continue;
            }
            var firstOfRun = prev is null || prev.Mine != l.Mine || prev.Who != l.Who || prev.Message.TaskId is not null || prev.Message.System;
            var lastOfRun = next is null || next.Mine != l.Mine || next.Who != l.Who || next.Message.TaskId is not null || next.Message.System;
            _lines.Children.Add(Bubble(m, l, group && firstOfRun && !l.Mine, lastOfRun, l.Mine && i == lastMine && i == lastSeen, group));
        }
    }

    private static FrameworkElement DateLine(DateOnly day, DateTimeOffset now)
    {
        var today = DateOnly.FromDateTime(now.ToLocalTime().DateTime);
        var text = day == today ? "Today" : day == today.AddDays(-1) ? "Yesterday" : day.ToString("ddd, d MMM", CultureInfo.InvariantCulture);
        var grid = new Grid { Margin = new Thickness(0, 10, 0, 6) };
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.Children.Add(new Border { Height = 1, VerticalAlignment = VerticalAlignment.Center }.Res(Border.BackgroundProperty, "Z.Line"));
        var t = Ui.Text(text, 12, fg: "Z.Muted");
        t.Margin = new Thickness(10, 0, 10, 0);
        Grid.SetColumn(t, 1);
        grid.Children.Add(t);
        var r = new Border { Height = 1, VerticalAlignment = VerticalAlignment.Center }.Res(Border.BackgroundProperty, "Z.Line");
        Grid.SetColumn(r, 2);
        grid.Children.Add(r);
        return grid;
    }

    private static FrameworkElement Bubble(TrayModel m, TeamLine l, bool showName, bool lastOfRun, bool showSeen, bool group)
    {
        var msg = l.Message;
        var column = new StackPanel { MaxWidth = 268, HorizontalAlignment = l.Mine ? HorizontalAlignment.Right : HorizontalAlignment.Left };
        var inner = new StackPanel();
        if (showName)
        {
            inner.Children.Add(Ui.Text(l.Who, 12.5, FontWeights.Bold, msg.ByOwner ? "Z.OrangeInk" : "Z.Ok"));
        }
        if (msg.ReplyToId is not null && (msg.ReplyToBody is { Length: > 0 } || msg.ReplyToBy is { Length: > 0 }))
        {
            var q = new StackPanel();
            q.Children.Add(Ui.Text(msg.ReplyToBy ?? "", 11.5, FontWeights.SemiBold, "Z.Ink2", trim: true));
            q.Children.Add(Ui.Text(msg.ReplyToBody ?? "", 12, fg: "Z.Muted", trim: true));
            var quote = new Border { BorderThickness = new Thickness(3, 0, 0, 0), Padding = new Thickness(8, 4, 8, 4), Margin = new Thickness(0, 2, 0, 6), CornerRadius = new CornerRadius(4), Child = q }
                .Res(Border.BorderBrushProperty, "Z.Orange").Res(Border.BackgroundProperty, "Z.Fill");
            inner.Children.Add(quote);
        }
        var body = Ui.Text(msg.Deleted ? (l.Mine ? "You deleted this message" : "This message was deleted") : msg.Body, 14, fg: msg.Deleted ? "Z.Muted" : "Z.Ink", wrap: true);
        if (msg.Deleted)
        {
            body.FontStyle = FontStyles.Italic;
        }
        body.Margin = new Thickness(0, showName ? 2 : 0, 0, 0);
        inner.Children.Add(body);
        var bubble = new Border { CornerRadius = new CornerRadius(14), Padding = new Thickness(12, 8, 12, 9), Child = inner, BorderThickness = new Thickness(l.Mine ? 0 : 1), Opacity = l.Sending ? 0.7 : 1 }
            .Res(Border.BackgroundProperty, l.Mine ? "Z.TvMineBg" : "Z.Surface").Res(Border.BorderBrushProperty, "Z.Line");
        column.Children.Add(bubble);

        // Meta: the time (· Edited · Seen by N) and the line's actions.
        var meta = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = l.Mine ? HorizontalAlignment.Right : HorizontalAlignment.Left, Margin = new Thickness(2, 3, 2, 2) };
        var when = l.Sending ? "Sending…" : (msg.Edited && !msg.Deleted ? "Edited · " : "") + TeamTaskEngine.Clock(msg.At)
            + (showSeen ? group ? msg.SeenBy > 0 ? $" · Seen by {msg.SeenBy}" : "" : msg.SeenBy > 0 ? " · Seen" : "" : "");
        var time = Ui.Text(when, 11.5, fg: "Z.Muted");
        if (showSeen && msg.SeenNames is { Count: > 0 } names)
        {
            time.ToolTip = "Seen by " + string.Join(", ", names);
        }
        meta.Children.Add(time);
        if (!l.Sending && !msg.Deleted && msg.Id > 0)
        {
            meta.Children.Add(Ui.Text(" · ", 11.5, fg: "Z.Muted"));
            meta.Children.Add(Tv.Bare(Ui.Text("Reply", 11.5, FontWeights.SemiBold, "Z.Ink2"), () => m.StartTeamReply(msg), "Reply to " + l.Who));
            if (!l.Mine && !msg.ByOwner)
            {
                var report = Tv.Bare(Ui.Text("Report", 11.5, FontWeights.SemiBold, "Z.Muted"), () => m.OpenReport(m.ChatOpen ?? "", msg), "Report this message");
                var dot = Ui.Text(" · ", 11.5, fg: "Z.Muted");
                report.Opacity = dot.Opacity = 0;
                meta.Children.Add(dot);
                meta.Children.Add(report);
                column.MouseEnter += (_, _) => report.Opacity = dot.Opacity = 1;
                column.MouseLeave += (_, _) => report.Opacity = dot.Opacity = report.IsKeyboardFocused ? 1 : 0;
                report.GotKeyboardFocus += (_, _) => report.Opacity = dot.Opacity = 1;
            }
        }
        column.Children.Add(meta);

        var row = new Grid { Margin = new Thickness(0, showName ? 8 : 2, 0, 2), Background = Brushes.Transparent };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        if (!l.Mine)
        {
            FrameworkElement face = lastOfRun
                ? Tv.Face(l.Initials.Length > 0 ? l.Initials : "•", 26, msg.ByOwner ? "Z.OrangeTint" : "Z.Fill", msg.ByOwner ? "Z.OrangeInk" : "Z.Ink")
                : new Border { Width = 26 };
            face.VerticalAlignment = VerticalAlignment.Bottom;
            face.Margin = new Thickness(0, 0, 8, 22);
            row.Children.Add(face);
        }
        Grid.SetColumn(column, 1);
        row.Children.Add(column);
        AutomationProperties.SetName(row, $"{l.Who}: {msg.Body}. {when}");
        return row;
    }

    private void BuildComposer(TrayModel m, bool canPost, string note)
    {
        var stack = new StackPanel();
        if (!canPost)
        {
            var n = Ui.Text(note, 13, fg: "Z.Muted", wrap: true);
            n.TextAlignment = TextAlignment.Center;
            n.Margin = new Thickness(8, 6, 8, 6);
            stack.Children.Add(n);
            _composer.Child = stack;
            return;
        }
        if (m.TeamReplyTo is { } reply)
        {
            var chip = new Grid { Margin = new Thickness(0, 0, 0, 8) };
            chip.ColumnDefinitions.Add(new ColumnDefinition());
            chip.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var q = new StackPanel();
            q.Children.Add(Ui.Text("Replying to " + (reply.ByOwner ? m.OwnerFirstStart : reply.ByName.Split(' ')[0] is { Length: > 0 } f ? f : "yourself"), 12, FontWeights.SemiBold, "Z.Ink2"));
            q.Children.Add(Ui.Text(reply.Body, 12, fg: "Z.Muted", trim: true));
            var quote = new Border { BorderThickness = new Thickness(3, 0, 0, 0), Padding = new Thickness(8, 3, 8, 3), Child = q }.Res(Border.BorderBrushProperty, "Z.Orange");
            chip.Children.Add(quote);
            var x = Tv.Icon(Tv.IcoX, m.CancelTeamReply, "Cancel reply", "Transparent", 10, "Z.Ink2", 3);
            Grid.SetColumn(x, 1);
            chip.Children.Add(x);
            stack.Children.Add(chip);
        }
        var (title, _) = m.ThreadHeader;
        _input.Tag = m.TeamReplyTo is not null ? "Reply · Esc cancels" : m.Offline ? $"Message {title} (sends when you’re back online)" : "Message · Shift+Enter for a new line";
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Inputs.Detach(_input);
        row.Children.Add(_input);
        var send = Tv.Send(() =>
        {
            var text = _input.Text;
            if (!string.IsNullOrWhiteSpace(text) && m.SendTeam(text))
            {
                _input.Text = "";
            }
        }, "Send message", 40);
        send.Margin = new Thickness(8, 0, 0, 2);
        Grid.SetColumn(send, 1);
        row.Children.Add(send);
        stack.Children.Add(row);
        _composer.Child = stack;
    }

    private void BuildReport(TrayModel m)
    {
        if (m.Report is not { } r)
        {
            _overlay.Content = null;
            return;
        }
        var layer = new Grid();
        var scrim = new Border().Res(Border.BackgroundProperty, "Z.DimCard");
        scrim.MouseLeftButtonDown += (_, e) =>
        {
            m.CloseReport();
            e.Handled = true;
        };
        layer.Children.Add(scrim);
        var stack = new StackPanel { Margin = new Thickness(16, 16, 16, 14) };
        stack.Children.Add(Ui.Text(r.Message is not null ? "Report this message" : "Report " + r.Who, 16, FontWeights.Bold));
        if (r.Message is { } msg)
        {
            var q = new Border { BorderThickness = new Thickness(3, 0, 0, 0), Padding = new Thickness(10, 6, 10, 6), Margin = new Thickness(0, 10, 0, 0), CornerRadius = new CornerRadius(4),
                Child = Ui.Text(msg.ByName.Split(' ')[0] + ": " + msg.Body, 12.5, fg: "Z.Ink2", wrap: true) }
                .Res(Border.BorderBrushProperty, "Z.Line2").Res(Border.BackgroundProperty, "Z.Fill");
            stack.Children.Add(q);
        }
        var hint = Ui.Text(m.ReportHint, 12.5, fg: "Z.Muted", wrap: true);
        hint.Margin = new Thickness(0, 10, 0, 10);
        stack.Children.Add(hint);
        var box = Inputs.Keep("report:" + r.Key + ":" + r.Message?.Id, () => Tv.Field("What happened? (optional)", multiLine: true));
        stack.Children.Add(box);
        var buttons = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
        var send = Tv.Btn("Send report", true, () =>
        {
            var text = box.Text;
            box.Text = "";
            m.SendReport(text);
        });
        send.Margin = new Thickness(0, 0, 8, 0);
        buttons.Children.Add(send);
        buttons.Children.Add(Tv.Btn("Cancel", false, m.CloseReport));
        stack.Children.Add(buttons);
        var card = new Border
        {
            Child = stack, CornerRadius = new CornerRadius(16), Margin = new Thickness(14), VerticalAlignment = VerticalAlignment.Center,
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 30, ShadowDepth = 8, Opacity = 0.25, Direction = 270 },
        }.Res(Border.BackgroundProperty, "Z.Surface");
        card.MouseLeftButtonDown += (_, e) => e.Handled = true;
        AutomationProperties.SetName(card, "Report");
        layer.Children.Add(card);
        _overlay.Content = layer;
        card.Loaded += (_, _) => box.Focus();
    }
}

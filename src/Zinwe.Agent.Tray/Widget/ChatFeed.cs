using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using Zinwe.Agent.Core.Messages;
using Zinwe.Agent.Tray.Resources;

namespace Zinwe.Agent.Tray.Widget;

/// <summary>View model for one bubble in the Chat window (data-bound from XAML).</summary>
public sealed class ChatRow
{
    /// <summary>Stable identity: the message id, or "pending:&lt;outbox id&gt;" for a reply not sent yet.</summary>
    public string Key { get; init; } = "";
    /// <summary>Bold first line, only when the admin gave the message a heading distinct from its text.</summary>
    public string Title { get; init; } = "";
    public string Text { get; init; } = "";
    public bool IsMine { get; init; }
    /// <summary>The employee's reply is queued; the agent posts it within a few seconds.</summary>
    public bool IsPending { get; init; }
    /// <summary>"warning" / "critical" admin messages get a coloured edge and a label.</summary>
    public string Severity { get; init; } = "info";
    public DateTimeOffset At { get; init; }

    /// <summary>A "Today" / "Yesterday" / "Mon, 13 Oct" divider row instead of a bubble.</summary>
    public bool IsSeparator { get; init; }

    /// <summary>A "Message {owner}" button under the bubble (e.g. a break that wasn't approved: reply to explain).</summary>
    public bool OffersReply { get; init; }
    public string ReplyLabel { get; init; } = "";
    public Visibility ReplyVisibility => OffersReply ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>The clock time under the bubble ("10:42 AM"); "Sending…" while queued.</summary>
    public string Stamp => IsPending ? "Sending…" : At.ToLocalTime().ToString("h:mm tt");

    /// <summary>Who wrote it, for screen readers (the bubble's side shows it visually).</summary>
    public string AccessibleText => IsSeparator ? Text : $"{(IsMine ? "You" : "Admin")}, {Stamp}: {Title} {Text}".Trim();
    public bool IsImportant => !IsMine && Severity is "warning" or "critical";
    public string ImportanceLabel => Severity == "critical" ? "Urgent" : "Important";

    public Visibility TitleVisibility => string.IsNullOrWhiteSpace(Title) ? Visibility.Collapsed : Visibility.Visible;
    public Visibility TextVisibility => string.IsNullOrWhiteSpace(Text) ? Visibility.Collapsed : Visibility.Visible;
    public Visibility ImportanceVisibility => IsImportant ? Visibility.Visible : Visibility.Collapsed;
    public Visibility BubbleVisibility => IsSeparator ? Visibility.Collapsed : Visibility.Visible;
    public Visibility SeparatorVisibility => IsSeparator ? Visibility.Visible : Visibility.Collapsed;
    public HorizontalAlignment RowAlignment => IsMine ? HorizontalAlignment.Right : HorizontalAlignment.Left;
    public Brush BubbleBrush => IsMine ? MineFill : AdminFill;
    /// <summary>Hairline border; an "Important" / "Urgent" admin message is outlined in its colour instead.</summary>
    public Brush BubbleBorder => IsImportant ? AccentBrush : IsMine ? MineLine : AdminLine;
    private static readonly Brush MineLine = Freeze(Color.FromRgb(0xF6, 0xD3, 0xB6));
    private static readonly Brush AdminLine = Freeze(Color.FromRgb(0xE6, 0xE6, 0xE1));
    public double BubbleOpacity => IsPending ? 0.65 : 1.0;
    public Brush AccentBrush => !IsImportant ? Brushes.Transparent
        : Application.Current?.TryFindResource(Severity == "critical" ? "Zinwe.Bad" : "Zinwe.Warn") as Brush ?? Brushes.Orange;

    // Brand: the employee's own lines on the orange tint, the admin's on white (the chat sits on paper).
    private static readonly Brush MineFill = Freeze(Color.FromRgb(0xFF, 0xF1, 0xE6));
    private static readonly Brush AdminFill = Freeze(Color.FromRgb(0xFF, 0xFF, 0xFF));

    private static Brush Freeze(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}

/// <summary>
/// Builds the Chat window's conversation from the shared notifications file (chat lines only) plus the
/// replies still waiting in the outbox. Pure: no file or UI access, so it's unit-tested.
/// </summary>
public static partial class ChatFeed
{
    /// <summary>
    /// Oldest first (chat order), with a day divider before each new day. Protection notices are left out;
    /// pending replies go last. <paramref name="now"/> (default: the clock) names "Today" / "Yesterday".
    /// </summary>
    /// <param name="owner">The owner's name, when known ("Message Ravi").</param>
    public static IReadOnlyList<ChatRow> Build(
        IEnumerable<NotificationItem> items, IEnumerable<OutboxReply> pending, DateTimeOffset? now = null, string? owner = null)
    {
        var rows = items
            .Where(i => i.IsChat)
            .OrderBy(i => i.CreatedAtUtc)
            .Select(i =>
            {
                var (title, text) = Content(i, owner);
                var breakDecision = IsBreakNotApproved(i);
                return new ChatRow
                {
                    Key = i.Id,
                    Title = title,
                    Text = text,
                    IsMine = !i.IsFromAdmin,
                    // A break that wasn't approved is a question, not a reprimand: no orange "Important".
                    Severity = breakDecision ? "info" : (i.Severity ?? "info").ToLowerInvariant(),
                    At = i.CreatedAtUtc,
                    OffersReply = breakDecision,
                    ReplyLabel = breakDecision ? Strings.Format(Strings.ChatMessageOwner, ConsentText.Owner(owner)) : "",
                };
            })
            .ToList();

        rows.AddRange(pending
            .OrderBy(p => p.CreatedAtUtc)
            .Select(p => new ChatRow
            {
                Key = "pending:" + p.Id,
                Text = p.Body,
                IsMine = true,
                IsPending = true,
                At = p.CreatedAtUtc,
            }));
        return WithDayDividers(rows, (now ?? DateTimeOffset.Now).ToLocalTime());
    }

    private static List<ChatRow> WithDayDividers(List<ChatRow> rows, DateTimeOffset now)
    {
        var result = new List<ChatRow>(rows.Count + 4);
        DateTime? day = null;
        foreach (var row in rows)
        {
            var rowDay = row.At.ToLocalTime().Date;
            if (rowDay != day)
            {
                day = rowDay;
                result.Add(new ChatRow { Key = "day:" + rowDay.ToString("yyyy-MM-dd"), IsSeparator = true, Text = DayLabel(rowDay, now.Date), At = row.At });
            }
            result.Add(row);
        }
        return result;
    }

    /// <summary>"Today", "Yesterday", "Mon, 13 Oct" (this year) or "13 Oct 2025".</summary>
    public static string DayLabel(DateTime day, DateTime today) =>
        day == today ? "Today"
        : day == today.AddDays(-1) ? "Yesterday"
        : day.Year == today.Year ? day.ToString("ddd, d MMM")
        : day.ToString("d MMM yyyy");

    /// <summary>
    /// What a message shows. The dashboard sends a long chat note as a cut-down title ("…") plus the full
    /// text, so the title is dropped when the text already starts with it (same rule as the dashboard).
    /// </summary>
    public static (string Title, string Text) Content(NotificationItem item, string? owner = null)
    {
        var title = (item.Title ?? "").Trim();
        var body = (item.Body ?? "").Trim();
        if (item.IsFromAdmin && BreakNotApproved().Match(title) is { Success: true } m)
        {
            // "Your Lunch break at 2:08 PM wasn't approved" (+ the owner's note): said kindly, with the note.
            var who = ConsentText.Owner(owner, sentenceStart: true);
            return (Strings.Format(Strings.ChatBreakTitle, m.Groups["what"].Value, m.Groups["when"].Value),
                body.Length > 0
                    ? Strings.Format(Strings.ChatBreakNote, who, body)
                    : Strings.Format(Strings.ChatBreakNoNote, who));
        }
        if (body.Length == 0)
        {
            return ("", title);
        }
        if (!item.IsFromAdmin || title.Length == 0 || body.StartsWith(title.TrimEnd('…'), StringComparison.Ordinal))
        {
            return ("", body);
        }
        return (title, body);
    }

    /// <summary>The owner's "wasn't approved" note about one of the member's breaks.</summary>
    public static bool IsBreakNotApproved(NotificationItem item) =>
        item.IsFromAdmin && BreakNotApproved().IsMatch((item.Title ?? "").Trim());

    [GeneratedRegex(@"^Your (?<what>.+) at (?<when>.+) wasn['’]t approved\.?$", RegexOptions.CultureInvariant)]
    private static partial Regex BreakNotApproved();

    /// <summary>One line for a toast or the widget's chat card: heading and text joined, whitespace collapsed.</summary>
    public static string Preview(NotificationItem item, string? owner = null)
    {
        var (title, text) = Content(item, owner);
        var line = title.Length > 0 ? $"{title} — {text}" : text;
        return string.Join(' ', line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>Admin chat messages the employee hasn't opened yet.</summary>
    public static int UnreadCount(IEnumerable<NotificationItem> items) =>
        items.Count(i => i.IsChat && i.IsFromAdmin && !i.Read);

    /// <summary>The newest chat line, or null.</summary>
    public static NotificationItem? Latest(IEnumerable<NotificationItem> items) =>
        items.Where(i => i.IsChat).MaxBy(i => i.CreatedAtUtc);
}

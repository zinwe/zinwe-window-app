using Zinwe.Agent.Core.Messages;
using Zinwe.Agent.Tray.Widget;

namespace Zinwe.Agent.Tray.Tests;

public sealed class ChatFeedTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 14, 9, 0, 0, TimeSpan.Zero);

    private static NotificationItem Chat(string id, int minute, string title, string? body, string sender = "owner",
        bool read = false, string severity = "info") =>
        new(id, T0.AddMinutes(minute), severity, title, body, read, sender, NotificationItem.ChatKind);

    private static NotificationItem Notice(string id, int minute) =>
        new(id, T0.AddMinutes(minute), "warning", "USB copy blocked", "A copy to E: was stopped.", Kind: "notice");

    [Fact]
    public void Conversation_is_oldest_first_without_protection_notices()
    {
        var rows = ChatFeed.Build(
            [Chat("3", 3, "", "Thanks", sender: "member"), Notice("-1", 2), Chat("1", 1, "Hi", "Please lock your PC")],
            []).Where(r => !r.IsSeparator).ToList();

        Assert.Equal(["1", "3"], rows.Select(r => r.Key));
        Assert.False(rows[0].IsMine);
        Assert.True(rows[1].IsMine);
    }

    [Fact]
    public void Each_new_day_gets_a_divider_named_for_today_and_yesterday()
    {
        var today = new DateTimeOffset(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);
        NotificationItem At(string id, DateTimeOffset at) => new(id, at, "info", "", "hi", Kind: NotificationItem.ChatKind);
        var rows = ChatFeed.Build(
            [At("1", today.AddDays(-3)), At("2", today.AddDays(-1)), At("3", today.AddDays(-1).AddMinutes(5)), At("4", today)],
            [], now: today);

        Assert.Equal(
            ["day", "1", "day", "2", "3", "day", "4"],
            rows.Select(r => r.IsSeparator ? "day" : r.Key));
        var labels = rows.Where(r => r.IsSeparator).Select(r => r.Text).ToList();
        Assert.Equal("Yesterday", labels[1]);
        Assert.Equal("Today", labels[2]);
        Assert.Equal(today.AddDays(-3).ToLocalTime().ToString("ddd, d MMM"), labels[0]);
    }

    [Fact]
    public void Bubbles_show_the_clock_time()
    {
        var at = new DateTimeOffset(2026, 10, 14, 10, 42, 0, TimeSpan.Zero);
        var row = ChatFeed.Build([new NotificationItem("1", at, "info", "", "hi", Kind: NotificationItem.ChatKind)], [])
            .Single(r => !r.IsSeparator);
        Assert.Equal(at.ToLocalTime().ToString("h:mm tt"), row.Stamp);
    }

    [Fact]
    public void Queued_replies_show_last_as_sending()
    {
        var rows = ChatFeed.Build(
            [Chat("1", 1, "Hi", "Hello")],
            [new OutboxReply("abc", "On my way", T0.AddMinutes(5))]);

        var pending = rows[^1];
        Assert.Equal("pending:abc", pending.Key);
        Assert.True(pending.IsMine);
        Assert.True(pending.IsPending);
        Assert.Equal("Sending…", pending.Stamp);
        Assert.Equal("On my way", pending.Text);
    }

    [Fact]
    public void A_cut_down_title_is_not_repeated_above_its_full_text()
    {
        // The dashboard sends a long note as title "Please send the…" + the full text.
        var item = Chat("1", 0, "Please send the…", "Please send the invoice by Friday, thanks.");
        Assert.Equal(("", "Please send the invoice by Friday, thanks."), ChatFeed.Content(item));
    }

    [Fact]
    public void A_distinct_heading_stays_and_a_title_only_message_shows_as_text()
    {
        Assert.Equal(("USB storage is now blocked", "Copying to USB drives is off on this PC."),
            ChatFeed.Content(Chat("1", 0, "USB storage is now blocked", "Copying to USB drives is off on this PC.")));
        Assert.Equal(("", "Quick call at 3?"), ChatFeed.Content(Chat("2", 0, "Quick call at 3?", null)));
    }

    [Fact]
    public void Unread_counts_only_the_admins_unopened_chat_lines()
    {
        NotificationItem[] items =
        [
            Chat("1", 1, "Hi", "One"),
            Chat("2", 2, "", "Mine", sender: "member"),
            Chat("3", 3, "Hi", "Two", read: true),
            Notice("-1", 4),
        ];
        Assert.Equal(1, ChatFeed.UnreadCount(items));
        Assert.Equal("3", ChatFeed.Latest(items)!.Id); // the newer notice doesn't count as the last chat line
    }

    [Fact]
    public void Older_files_without_a_kind_split_by_id_sign()
    {
        Assert.True(new NotificationItem("42", T0, "info", "Hi").IsChat);    // server message id
        Assert.False(new NotificationItem("-42", T0, "info", "Hi").IsChat);  // the agent's own notice
        Assert.False(new NotificationItem(Guid.NewGuid().ToString(), T0, "info", "Hi").IsChat);
    }

    [Fact]
    public void Urgent_admin_messages_are_flagged_but_replies_never_are()
    {
        var urgent = ChatFeed.Build([Chat("1", 1, "", "Call me now", severity: "critical")], []).Single(r => !r.IsSeparator);
        Assert.True(urgent.IsImportant);
        Assert.Equal("Urgent", urgent.ImportanceLabel);
        var reply = ChatFeed.Build([Chat("2", 1, "", "ok", sender: "member", severity: "critical")], []).Single(r => !r.IsSeparator);
        Assert.False(reply.IsImportant);
    }

    [Fact]
    public void Preview_is_one_line()
    {
        Assert.Equal("Line one line two", ChatFeed.Preview(Chat("1", 0, "", "Line one\r\n  line two")));
    }

    [Theory]
    [InlineData("About your Lunch break at 2:08 PM", true)]
    [InlineData("About your log-out at 6:10 PM", true)]                 // FR-16
    [InlineData("Your Lunch break at 2:08 PM wasn't approved", true)]   // older backends
    [InlineData("Your log-out at 6:10 PM was approved", false)]
    [InlineData("About your leave next week", false)]
    public void A_questioned_break_or_log_out_offers_a_reply(string title, bool offers)
    {
        var row = ChatFeed.Build([Chat("1", 0, title, "Ravi would like a word. You can reply here.")], [])
            .Single(r => !r.IsSeparator);
        Assert.Equal(offers, row.OffersReply);
    }
}

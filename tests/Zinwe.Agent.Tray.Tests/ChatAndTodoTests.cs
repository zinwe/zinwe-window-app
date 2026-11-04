using Zinwe.Agent.Core.Messages;
using Zinwe.Agent.Tray.Widget;

namespace Zinwe.Agent.Tray.Tests;

/// <summary>The chat's "Not sent" and "Seen" lines (the Tasks tab's views: <see cref="TasksV2Tests"/>).</summary>
public sealed class ChatAndTodoTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 31, 11, 0, 0, TimeSpan.Zero);

    private static NotificationItem Line(string id, string sender, int minutes, bool readByOwner = false) =>
        new(id, Now.AddMinutes(minutes), "info", "", "Hello", Read: true, Sender: sender, Kind: "chat", ReadByOwner: readByOwner);

    private static IReadOnlyList<ThreadRow> Thread(IEnumerable<NotificationItem> lines, params OutboxReply[] pending) =>
        ChatThread.Build(lines, pending, [], [], "Ananya", Now);

    [Fact]
    public void A_refused_reply_reads_not_sent_and_is_no_longer_pending()
    {
        var refused = new OutboxReply("a1", "Can I leave at 5?", Now, Refused: true);

        var row = Thread([], refused)[^1];

        Assert.True(row.Failed);
        Assert.False(row.Pending);
        Assert.Equal("Not sent", row.Meta);
    }

    [Fact]
    public void Seen_shows_once_on_the_newest_line_the_owner_read()
    {
        var rows = Thread([Line("1", "member", -3, readByOwner: true), Line("2", "member", -2, readByOwner: true), Line("3", "member", -1)])
            .Where(r => r.Kind == ThreadKind.Text).ToList();

        Assert.DoesNotContain("Seen", rows[0].Meta);
        Assert.EndsWith(" · Seen", rows[1].Meta);
        Assert.DoesNotContain("Seen", rows[2].Meta);
    }

    [Fact]
    public void Only_the_owner_has_an_avatar_and_only_on_the_last_bubble_of_a_run()
    {
        var rows = Thread([Line("1", "owner", -3), Line("2", "owner", -2), Line("3", "member", -1)])
            .Where(r => r.Kind == ThreadKind.Text).ToList();

        Assert.Equal([false, true, false], rows.Select(r => r.HasAvatar));
    }
}

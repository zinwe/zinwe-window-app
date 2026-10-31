using Zinwe.Agent.Core.Messages;
using Zinwe.Agent.Tray.Widget;

namespace Zinwe.Agent.Tray.Tests;

/// <summary>The chat's "Not sent" and "Seen" lines, and the Tasks tab's Todo button.</summary>
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

    private static MemberTask Task(int id, DueKey due, MemberTaskStatus status = MemberTaskStatus.New, bool mine = false) =>
        new() { Id = id, Title = "Task " + id, DueKey = due, Status = status, Mine = mine };

    private static readonly MemberTask[] Board =
    [
        Task(1, DueKey.Overdue),
        Task(2, DueKey.Today, MemberTaskStatus.Progress),
        Task(3, DueKey.Today, MemberTaskStatus.Done),
        Task(4, DueKey.Tomorrow),
        Task(5, DueKey.Today, mine: true),
    ];

    private static int[] Shown(TaskFilter f) => [.. TaskBoard.Shown(Board, f, Now).Select(t => t.Id).Order()];

    [Fact]
    public void Todo_lists_overdue_and_today_but_nothing_finished()
    {
        Assert.Equal([1, 2, 5], Shown(TaskFilter.None with { Todo = true }));
    }

    [Fact]
    public void Todo_count_matches_the_list_under_the_other_filters()
    {
        foreach (var f in new[] { TaskFilter.None, TaskFilter.None with { From = "me" }, TaskFilter.None with { From = "owner" }, TaskFilter.None with { Status = "doing" } })
        {
            Assert.Equal(Shown(f with { Todo = true }).Length, TaskBoard.TodoCount(Board, f, Now));
        }
    }

    [Fact]
    public void Turning_todo_on_clears_a_date_and_done_and_off_leaves_the_rest()
    {
        var on = TaskBoard.ToggleTodo(new TaskFilter(Status: "done", Due: "tomorrow", From: "me"));
        Assert.Equal(new TaskFilter(Status: "all", Due: "any", From: "me", Todo: true), on);
        Assert.Equal(new TaskFilter(From: "me"), TaskBoard.ToggleTodo(on));
    }

    [Fact]
    public void Choosing_done_turns_todo_off_and_high_priority_sets_its_own_flag()
    {
        var todo = TaskFilter.None with { Todo = true };
        Assert.False(TaskBoard.WithStatus(todo, "done").Todo);
        Assert.True(TaskBoard.WithStatus(todo, "doing").Todo);
        var high = TaskBoard.WithStatus(todo, "high");
        Assert.True(high.High);
        Assert.Equal("all", high.Status);
        Assert.False(TaskBoard.WithStatus(high, "todo").High);
    }

    [Fact]
    public void An_empty_todo_says_so_kindly()
    {
        Assert.Equal("Nothing left for today.", TaskBoard.EmptyText(TaskFilter.None with { Todo = true }, "Ananya"));
    }
}

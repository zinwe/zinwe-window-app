using System.IO;
using Zinwe.Agent.Core.Backend;
using Zinwe.Agent.Core.Messages;
using Zinwe.Agent.Tray.Widget;

namespace Zinwe.Agent.Tray.Tests;

/// <summary>
/// FR-20 v2 · the owner's tasks in the tray: the server's statuses mapped to the Tasks tab, the member's actions shown
/// before the server confirms them, task notices, My to-dos kept apart, and task cards in Chat.
/// </summary>
public sealed class OwnerTasksTests : IDisposable
{
    private static readonly TimeSpan Offset = TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 10, 30));
    private static readonly DateTimeOffset Now = new(2026, 10, 30, 11, 0, 0, Offset); // Friday 11 AM local

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "zinwe-owner-tasks-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch
        {
            // best effort
        }
    }

    private static DeviceTask Remote(long id, string status = "sent", DateTimeOffset? dueAt = null, string priority = "normal",
        IReadOnlyList<DeviceTaskActivity>? activity = null, string helpNote = "") => new(
        id, "Task " + id, "Details " + id, dueAt, priority,
        [new DeviceTaskStep(10 * id + 1, "First", true), new DeviceTaskStep(10 * id + 2, "Second", false)],
        status, helpNote, Now.AddHours(-2), Now.AddHours(-1), Now.AddHours(-1), "owner", 900 + id,
        activity ?? [new DeviceTaskActivity(Now.AddHours(-2), "owner", "created")]);

    private static OutboxReply Action(long taskId, string action, string note = "", long? stepId = null, bool? stepDone = null, int minutes = 0) =>
        new(Guid.NewGuid().ToString("N"), note, Now.AddMinutes(minutes), OutboxReply.TaskActionKind,
            TaskId: taskId, Action: action, StepId: stepId, StepDone: stepDone);

    // ---- Status mapping ------------------------------------------------------------------------

    [Theory]
    [InlineData("sent", MemberTaskStatus.New, true, TaskLane.Todo)]
    [InlineData("seen", MemberTaskStatus.New, false, TaskLane.Todo)]
    [InlineData("accepted", MemberTaskStatus.Confirmed, false, TaskLane.Todo)]
    [InlineData("progress", MemberTaskStatus.Progress, false, TaskLane.Doing)]
    [InlineData("help", MemberTaskStatus.Help, false, TaskLane.Waiting)]
    [InlineData("review", MemberTaskStatus.Review, false, TaskLane.Waiting)]
    [InlineData("approved", MemberTaskStatus.Approved, false, TaskLane.Done)]
    [InlineData("cancelled", MemberTaskStatus.Cancelled, false, TaskLane.Done)]
    [InlineData("something_new", MemberTaskStatus.New, false, TaskLane.Todo)]
    public void Every_server_status_has_a_plain_place_on_the_board(string status, MemberTaskStatus expected, bool isNew, TaskLane lane)
    {
        var t = OwnerTasks.ToMemberTask(Remote(4, status), "Ananya", Now);

        Assert.Equal((expected, isNew), (t.Status, t.IsNew));
        Assert.Equal(lane, TaskBoard.LaneOf(t));
    }

    [Fact]
    public void Statuses_read_in_the_member_s_words()
    {
        Assert.Equal("Waiting for Ananya’s OK", TaskBoard.LaneLabel(OwnerTasks.ToMemberTask(Remote(1, "review"), "Ananya", Now), "Ananya"));
        Assert.Equal("Cancelled", TaskBoard.LaneLabel(OwnerTasks.ToMemberTask(Remote(1, "cancelled"), "Ananya", Now), "Ananya"));
        Assert.Equal("Ananya cancelled this task.", TaskBoard.Line(OwnerTasks.ToMemberTask(Remote(1, "cancelled"), "Ananya", Now), "Ananya"));
        Assert.True(TaskBoard.Finished(OwnerTasks.ToMemberTask(Remote(1, "cancelled"), "Ananya", Now)));
        Assert.Null(TaskBoard.Quick(OwnerTasks.ToMemberTask(Remote(1, "cancelled"), "Ananya", Now)));
    }

    [Fact]
    public void A_server_task_maps_due_priority_steps_notes_and_history()
    {
        var due = new DateTimeOffset(Now.Date.AddHours(16), Offset);
        var t = OwnerTasks.ToMemberTask(Remote(41, "help", due, "high",
            [
                new DeviceTaskActivity(Now.AddHours(-2), "owner", "created"),
                new DeviceTaskActivity(Now.AddHours(-1), "member", "started"),
                new DeviceTaskActivity(Now.AddMinutes(-30), "member", "step_done", "First"),
                new DeviceTaskActivity(Now.AddMinutes(-20), "owner", "edited", "title, steps"),
                new DeviceTaskActivity(Now.AddMinutes(-5), "member", "help", "Which room?"),
            ], helpNote: "Which room?"), "Ananya", Now);

        Assert.Equal((-41, 41L), (t.Id, t.RemoteId!.Value)); // never collides with the own to-dos' 1, 2, 3
        Assert.False(t.Mine);
        Assert.True(t.High);
        Assert.True(t.NeedsOk); // done waits for the owner's OK
        Assert.Equal(("Today, 4:00 PM", DueKey.Today, due), (t.Due, t.DueKey, t.DueAt!.Value));
        Assert.Equal("Details 41", t.Details);
        Assert.Equal([(411L, true), (412L, false)], t.Steps.Select(s => (s.Id!.Value, s.Done)));
        Assert.Equal(new TaskNote("You", "Which room?"), t.Notes.Single()); // the help note shows once; ticks and edits aren't notes
        Assert.Equal(["Ananya sent it", "You started it", "You ticked “First”", "Ananya changed it", "You asked for help"],
            t.Activity.Select(a => a.Text));
        Assert.Equal(Now.AddHours(-2), t.SentAt);
    }

    [Fact]
    public void Due_dates_read_like_the_board()
    {
        Assert.Equal(("No due date", DueKey.Later), Pick(OwnerTasks.DueOf(null, Now)));
        Assert.Equal(("Tomorrow", DueKey.Tomorrow), Pick(OwnerTasks.DueOf(new DateTimeOffset(Now.Date.AddDays(1), Offset), Now)));
        Assert.Equal(DueKey.Overdue, OwnerTasks.DueOf(new DateTimeOffset(Now.Date.AddDays(-1).AddHours(10), Offset), Now).Key);
        var later = OwnerTasks.DueOf(new DateTimeOffset(Now.Date.AddDays(5).AddHours(9), Offset), Now);
        Assert.Equal((DueKey.Later, DateOnly.FromDateTime(Now.Date.AddDays(5))), (later.Key, later.Date!.Value));

        static (string, DueKey) Pick((string Label, DueKey Key, DateOnly? Date) d) => (d.Label, d.Key);
    }

    [Fact]
    public void A_new_owner_task_asks_for_a_confirmation_first()
    {
        var sent = OwnerTasks.ToMemberTask(Remote(1, "sent"), "Ananya", Now);
        var accepted = OwnerTasks.ToMemberTask(Remote(1, "accepted"), "Ananya", Now);

        Assert.True(TaskBoard.NeedsConfirm(sent));
        Assert.Equal(("Confirm", "Confirm: Task 1"), (TaskBoard.Quick(sent)!.Label, TaskBoard.Quick(sent)!.Aria));
        Assert.Equal("Start", TaskBoard.Quick(accepted)!.Label);
        Assert.Equal(("Mark done", true), (TaskBoard.Quick(OwnerTasks.ToMemberTask(Remote(1, "progress"), "Ananya", Now))!.Label, true));
    }

    // ---- Optimistic actions -------------------------------------------------------------------------

    [Fact]
    public void Actions_show_at_once_in_the_order_they_were_made()
    {
        var tasks = new[] { Remote(1, "sent"), Remote(2, "progress") };

        var shown = OwnerTasks.Overlay(tasks,
        [
            Action(1, "seen", minutes: 1),
            Action(1, "accept", minutes: 2),
            Action(1, "start", minutes: 3),
            Action(2, "note", stepId: 22, stepDone: true, minutes: 1),
            Action(2, "done", minutes: 2),
        ]);

        Assert.Equal("progress", shown[0].Status);
        Assert.Equal("review", shown[1].Status);
        Assert.True(shown[1].Steps!.Single(s => s.Id == 22).Done);
        Assert.Equal(["created", "accepted", "started"], shown[0].Activity!.Select(a => a.Action)); // "seen" isn't a history line
        Assert.Equal(("step_done", "Second"), (shown[1].Activity![1].Action, shown[1].Activity![1].Note)); // as the server records it
    }

    [Fact]
    public void Help_keeps_its_note_and_a_note_is_not_shown_twice_once_the_server_has_it()
    {
        var help = OwnerTasks.Apply(Remote(1, "progress"), Action(1, "help", " Which room? "));
        Assert.Equal(("help", "Which room?"), (help.Status, help.HelpNote));

        var note = Action(1, "note", "Booked for 10 AM", minutes: 1);
        var serverHasIt = Remote(1, "progress", activity: [new DeviceTaskActivity(Now.AddMinutes(1).AddSeconds(2), "member", "note", "Booked for 10 AM")]);
        Assert.Single(OwnerTasks.Apply(serverHasIt, note).Activity!);
        Assert.Equal(2, OwnerTasks.Apply(Remote(1, "progress"), note).Activity!.Count);
    }

    [Fact]
    public void Finished_tasks_stay_finished_whatever_was_queued()
    {
        Assert.Equal("approved", OwnerTasks.Apply(Remote(1, "approved"), Action(1, "start")).Status);
        Assert.Equal("cancelled", OwnerTasks.Apply(Remote(1, "cancelled"), Action(1, "done")).Status);
        Assert.Equal("progress", OwnerTasks.Apply(Remote(1, "progress"), Action(1, "accept")).Status); // accept only from new
    }

    [Fact]
    public void Actions_on_other_tasks_and_chat_lines_leave_a_task_alone()
    {
        var tasks = new[] { Remote(1, "sent") };
        var chat = new OutboxReply("x", "hello", Now);

        Assert.Same(tasks, OwnerTasks.Overlay(tasks, [chat]));
        Assert.Equal("sent", OwnerTasks.Overlay(tasks, [Action(9, "start")]).Single().Status);
    }

    // ---- Notices -------------------------------------------------------------------------------------

    [Fact]
    public void The_first_run_announces_only_tasks_nobody_opened_yet()
    {
        var (notices, marks) = TaskNotices.Diff(null, [Remote(1, "sent"), Remote(2, "progress"), Remote(3, "approved")], "Ananya", Now);

        var n = Assert.Single(notices);
        Assert.Equal(("New task from Ananya", "task:-1", "task-new:1"), (n.Title, n.Go, n.Key));
        Assert.StartsWith("Task 1 · due ", n.Body);
        Assert.Equal([1L, 2L, 3L], marks.Keys.Order());
    }

    [Fact]
    public void Later_a_new_task_and_what_the_owner_did_are_announced_once()
    {
        var (_, marks) = TaskNotices.Diff(null, [Remote(1, "review")], "Ananya", Now);

        var approved = Remote(1, "approved", activity:
        [
            new DeviceTaskActivity(Now.AddHours(-2), "owner", "created"),
            new DeviceTaskActivity(Now.AddMinutes(-1), "member", "done"),
            new DeviceTaskActivity(Now, "owner", "approved"),
        ]);
        var (notices, next) = TaskNotices.Diff(marks, [approved, Remote(2, "sent")], "Ananya", Now);

        Assert.Equal(["Ananya approved your task", "New task from Ananya"], notices.Select(x => x.Title));
        Assert.Equal("Task 1", notices[0].Body);
        Assert.Empty(TaskNotices.Diff(next, [approved, Remote(2, "seen")], "Ananya", Now).Notices); // no repeats
    }

    [Fact]
    public void Members_own_actions_never_notify_them()
    {
        var (_, marks) = TaskNotices.Diff(null, [Remote(1, "progress")], "Ananya", Now);
        var mine = Remote(1, "review", activity:
            [new DeviceTaskActivity(Now.AddHours(-2), "owner", "created"), new DeviceTaskActivity(Now, "member", "done")]);

        Assert.Empty(TaskNotices.Diff(marks, [mine], "Ananya", Now).Notices);
        Assert.Equal("Reminder from Ananya", TaskNotices.OwnerLine("nudged", "Ananya"));
        Assert.Equal("Ananya reopened a task", TaskNotices.OwnerLine("reopened", "Ananya"));
        Assert.Equal("Ananya changed a task", TaskNotices.OwnerLine("edited", "Ananya"));
    }

    [Fact]
    public void The_seen_marks_survive_a_restart()
    {
        var store = new TaskSeenStore(_dir);
        Assert.Null(store.Load());
        store.Save(new Dictionary<long, DateTimeOffset> { [41] = Now });
        Assert.Equal(Now, new TaskSeenStore(_dir).Load()![41]);
    }

    // ---- My to-dos, chat cards, the history -------------------------------------------------------------

    [Fact]
    public void Own_to_dos_are_a_group_of_their_own_after_the_owners_tasks()
    {
        var own = new MemberTask { Id = 1, Title = "Water the plants", Mine = true, Status = MemberTaskStatus.New, DueKey = DueKey.Today };
        var ownDone = own with { Id = 2, Status = MemberTaskStatus.Done };
        var tasks = new[] { own, ownDone, OwnerTasks.ToMemberTask(Remote(5, "progress"), "Ananya", Now), OwnerTasks.ToMemberTask(Remote(6, "approved"), "Ananya", Now) };

        var groups = TaskBoard.Groups(TaskBoard.Shown(tasks, TaskFilter.None, Now), "all", "Ananya");

        Assert.Equal(["DOING", "DONE", TaskBoard.MyTodos], groups.Select(g => g.Label));
        Assert.Equal([1, 2], groups[^1].Items.Select(t => t.Id)); // open first
        Assert.All(groups.Take(2).SelectMany(g => g.Items), t => Assert.False(t.Mine));

        var done = TaskBoard.Groups(TaskBoard.Shown(tasks, new TaskFilter(Status: "done"), Now), "done", "Ananya");
        Assert.Equal(["DONE", TaskBoard.MyTodos], done.Select(g => g.Label));
    }

    [Fact]
    public void A_chat_line_with_a_task_becomes_the_live_task_card()
    {
        var task = OwnerTasks.ToMemberTask(Remote(41, "progress"), "Ananya", Now);
        var other = OwnerTasks.ToMemberTask(Remote(42, "sent"), "Ananya", Now);
        var items = new[]
        {
            new NotificationItem("900", Now.AddMinutes(-30), "info", "New task", "Task 41", Kind: NotificationItem.ChatKind, TaskId: 41),
            new NotificationItem("901", Now.AddMinutes(-20), "info", "", "Thanks", Kind: NotificationItem.ChatKind),
            new NotificationItem("902", Now.AddMinutes(-10), "info", "New task", "An old one", Kind: NotificationItem.ChatKind, TaskId: 7),
        };

        var rows = ChatThread.Build(items, [], [task, other], [], "Ananya", Now).Where(r => r.Kind != ThreadKind.Day).ToList();

        var card = rows.Single(r => r.Kind == ThreadKind.Task && r.Task!.RemoteId == 41);
        Assert.Equal(Now.AddMinutes(-30), card.At); // where the owner sent it
        Assert.Equal(MemberTaskStatus.Progress, card.Task!.Status); // live, not the status at send time
        Assert.Contains(rows, r => r.Kind == ThreadKind.Task && r.Task!.RemoteId == 42); // no line for it: by its send time
        Assert.Contains(rows, r => r.Kind == ThreadKind.Text && r.Text.Contains("An old one", StringComparison.Ordinal)); // task gone: plain text
        Assert.Equal(2, rows.Count(r => r.Kind == ThreadKind.Task));
    }

    [Fact]
    public void A_task_line_is_a_task_notice_not_a_new_message()
    {
        var items = new[]
        {
            new NotificationItem("900", Now, "info", "New task", "Task 41", Kind: NotificationItem.ChatKind, TaskId: 41),
            new NotificationItem("901", Now, "info", "", "Hello", Kind: NotificationItem.ChatKind),
        };

        Assert.Equal(1, ChatFeed.UnreadCount(items));
        var merged = NoticeHistory.Merge([new NoticeEntry("n1", "New task from Ananya", "Task 41", "task:-41", Now, false)], items, "Ananya");
        Assert.Equal(new UnreadCounts(Chat: 1, Tasks: 1, Other: 0), UnreadCounts.Of(merged));
    }
}

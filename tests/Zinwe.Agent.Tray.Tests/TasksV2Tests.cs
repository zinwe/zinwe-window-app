using Zinwe.Agent.Core.Backend;
using Zinwe.Agent.Core.Ipc;
using Zinwe.Agent.Core.Messages;
using Zinwe.Agent.Tray.Widget;

namespace Zinwe.Agent.Tray.Tests;

/// <summary>
/// F-16 · Tasks in the Zinwe window, rebuilt: due words worked out from the clock (not the words saved with a to-do),
/// the four views and their dated sections, the quick add, and the two ways a tick used to flip back by itself.
/// </summary>
public sealed class TasksV2Tests
{
    private static readonly TimeSpan Offset = TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 11, 4));
    private static readonly DateTimeOffset Now = new(2026, 11, 4, 15, 35, 0, Offset); // Wednesday 3:35 PM local
    private static readonly DateOnly Today = new(2026, 11, 4);

    private static DateTimeOffset At(int day, int hour, int minute = 0) => new(2026, 11, day, hour, minute, 0, Offset);

    private static MemberTask Own(int id, DateOnly? on, MemberTaskStatus status = MemberTaskStatus.New, DateTimeOffset? at = null) =>
        new() { Id = id, Title = "Task " + id, Mine = true, DueOn = on, DueAt = at, Status = status, SentAt = Now.AddDays(-3) };

    // ---- Due words from the real date (the stale "Today" / "Tomorrow" bug) ---------------------------------------

    [Fact]
    public void A_to_do_saved_for_tomorrow_reads_today_the_next_day_whatever_words_were_saved()
    {
        // Saved on Tuesday as "Tomorrow" (what SaveForm stores: DueDate null, DueOn the day).
        var saved = new MemberTask
        {
            Id = 1, Title = "Book a cab", Mine = true, Due = "Tomorrow", DueKey = DueKey.Tomorrow, DueOn = Today, SentAt = At(3, 17),
        };

        Assert.Equal(DueKey.Today, TaskBoard.KeyOf(saved, Now));
        Assert.Equal("Today", TaskBoard.DueLabel(saved, Now));
        Assert.Equal(("Today", TaskBoard.DayLabel(Today)), Single(TaskView.Today, [saved]));

        // Thursday: it's late now.
        var thursday = At(5, 9);
        Assert.Equal(DueKey.Overdue, TaskBoard.KeyOf(saved, thursday));
        Assert.Equal("Overdue · yesterday", TaskBoard.DueLabel(saved, thursday));
        Assert.True(TaskBoard.IsLate(saved, thursday));
    }

    [Fact]
    public void The_words_change_at_midnight()
    {
        var tomorrow = Own(1, Today.AddDays(1));
        Assert.Equal("Tomorrow · Thu 5 Nov", TaskBoard.DueLabel(tomorrow, At(4, 23, 59)));
        Assert.Equal("Today", TaskBoard.DueLabel(tomorrow, At(5, 0, 1)));
        Assert.False(TaskBoard.InView(tomorrow, TaskView.Today, At(4, 23, 59)));
        Assert.True(TaskBoard.InView(tomorrow, TaskView.Today, At(5, 0, 1)));
    }

    [Fact]
    public void A_row_from_before_due_on_counts_its_saved_word_from_the_day_it_was_made()
    {
        var old = new MemberTask { Id = 1, Title = "Old", Mine = true, Due = "Tomorrow", DueKey = DueKey.Tomorrow, SentAt = At(1, 10) };

        Assert.Equal(new DateOnly(2026, 11, 2), TaskBoard.When(old).Day);
        Assert.Equal(DueKey.Overdue, TaskBoard.KeyOf(old, Now));
        Assert.Null(TaskBoard.When(new MemberTask { DueKey = DueKey.Later, SentAt = Now }).Day); // no date stays no date
    }

    [Theory]
    [InlineData(17, 0, "By 5:00 PM")]
    [InlineData(15, 50, "Due in 15 min")]
    [InlineData(15, 0, "Was due 3:00 PM")]
    public void Times_today_read_by_due_in_or_was_due(int hour, int minute, string expected)
    {
        Assert.Equal(expected, TaskBoard.DueLabel(Own(1, null, at: At(4, hour, minute)), Now));
    }

    [Fact]
    public void Other_days_read_as_dates_and_late_days_say_how_late()
    {
        Assert.Equal("Overdue · yesterday", TaskBoard.DueLabel(Own(1, Today.AddDays(-1)), Now));
        Assert.Equal("Overdue · Mon 2 Nov", TaskBoard.DueLabel(Own(1, Today.AddDays(-2)), Now));
        Assert.Equal("Fri 6 Nov", TaskBoard.DueLabel(Own(1, Today.AddDays(2)), Now));
        Assert.Equal("Fri 6 Nov · 11:00 AM", TaskBoard.DueLabel(Own(1, null, at: At(6, 11)), Now));
        Assert.Equal("Tomorrow · Thu 5 Nov · 4:00 PM", TaskBoard.DueLabel(Own(1, null, at: At(5, 16)), Now));
        Assert.Equal("", TaskBoard.DueLabel(Own(1, null), Now));
        Assert.Equal("Today", TaskBoard.DueLabel(Own(1, Today), Now)); // no time: never late on the day
        Assert.False(TaskBoard.IsLate(Own(1, Today.AddDays(-1), MemberTaskStatus.Done), Now)); // ticked: not late
    }

    [Fact]
    public void An_owners_due_date_at_midnight_is_a_date_only_and_is_worked_out_again_each_time()
    {
        var d = new DeviceTask(9, "Report", DueAt: new DateTimeOffset(2026, 11, 5, 0, 0, 0, Offset), Status: "progress");
        var t = OwnerTasks.ToMemberTask(d, "Vishal", At(4, 9)); // mapped in the morning

        Assert.Equal((new DateOnly(2026, 11, 5), (DateTimeOffset?)null), TaskBoard.When(t));
        Assert.Equal("Today", TaskBoard.DueLabel(t, At(5, 8))); // the next day, from the same mapped task
    }

    // ---- The four views and their sections -------------------------------------------------------------------

    private static (string Name, string Sub) Single(TaskView view, IEnumerable<MemberTask> tasks)
    {
        var s = Assert.Single(TaskBoard.Sections(tasks, view, Now));
        return (s.Name, s.Sub);
    }

    private static MemberTask[] Board() =>
    [
        Own(1, Today.AddDays(-2)),
        Own(2, Today),
        Own(3, null, at: At(4, 17)),
        Own(4, Today.AddDays(1)),
        Own(5, Today.AddDays(3)),
        Own(6, null),
        Own(7, Today, MemberTaskStatus.Done) with { DoneAt = At(4, 10, 5) },
        Own(8, Today.AddDays(-1), MemberTaskStatus.Done) with { DoneAt = At(3, 18) },
    ];

    [Fact]
    public void Today_holds_what_is_late_and_due_today_and_upcoming_the_rest_with_a_date()
    {
        var today = TaskBoard.Sections(Board(), TaskView.Today, Now);
        Assert.Equal(["Overdue", "Today"], today.Select(s => s.Name));
        Assert.Equal([3, 2], today[1].Items.Select(t => t.Id)); // the one due at 5 PM before the one due some time today
        Assert.Equal(TaskBoard.DayLabel(Today), today[1].Sub);

        var upcoming = TaskBoard.Sections(Board(), TaskView.Upcoming, Now);
        Assert.Equal([("Tomorrow", "Thu 5 Nov"), ("Saturday", "Sat 7 Nov")], upcoming.Select(s => (s.Name, s.Sub)));

        var all = TaskBoard.Sections(Board(), TaskView.All, Now);
        Assert.Equal(["Overdue", "Today", "Tomorrow", "Saturday", "No date"], all.Select(s => s.Name));
        Assert.True(all[0].Late);

        var done = TaskBoard.Sections(Board(), TaskView.Done, Now);
        Assert.Equal([("Done today", 7), ("Earlier", 8)], done.Select(s => (s.Name, s.Items.Single().Id)));
    }

    [Fact]
    public void Each_view_counts_what_it_shows_and_done_counts_today()
    {
        Assert.Equal([3, 2, 6, 1], new[] { TaskView.Today, TaskView.Upcoming, TaskView.All, TaskView.Done }
            .Select(v => TaskBoard.Count(Board(), v, Now)));
        Assert.Equal([7], TaskBoard.DoneToday(Board(), Now).Select(t => t.Id));
    }

    [Fact]
    public void A_tick_holds_the_task_in_place_then_it_moves_to_done()
    {
        var ticked = Own(2, Today, MemberTaskStatus.Done) with { DoneAt = Now };
        var holding = new HashSet<int> { 2 };

        Assert.True(TaskBoard.InView(ticked, TaskView.Today, Now, holding));
        Assert.False(TaskBoard.InView(ticked, TaskView.Done, Now, holding));
        Assert.Empty(TaskBoard.DoneToday([ticked], Now, holding));

        Assert.False(TaskBoard.InView(ticked, TaskView.Today, Now));
        Assert.True(TaskBoard.InView(ticked, TaskView.Done, Now));
    }

    [Fact]
    public void An_owners_task_waiting_for_the_ok_is_ticked_not_late_and_sits_in_done()
    {
        var waiting = OwnerTasks.ToMemberTask(new DeviceTask(5, "Share the report", DueAt: At(4, 12), Status: "review",
            Activity: [new DeviceTaskActivity(At(4, 11, 40), "member", "done")]), "Vishal", Now);

        Assert.True(TaskBoard.Ticked(waiting));
        Assert.False(TaskBoard.IsLate(waiting, Now));
        Assert.Equal(At(4, 11, 40), waiting.DoneAt);
        Assert.True(TaskBoard.InView(waiting, TaskView.Done, Now));
        Assert.Equal([waiting], TaskBoard.DoneToday([waiting], Now));
        Assert.Equal(TaskView.Done, TaskBoard.ViewFor(waiting, Now));
    }

    [Fact]
    public void Opening_a_task_picks_the_view_that_shows_it()
    {
        Assert.Equal(TaskView.Today, TaskBoard.ViewFor(Own(1, Today.AddDays(-3)), Now));
        Assert.Equal(TaskView.Upcoming, TaskBoard.ViewFor(Own(1, Today.AddDays(1)), Now));
        Assert.Equal(TaskView.All, TaskBoard.ViewFor(Own(1, null), Now));
    }

    [Fact]
    public void Empty_views_say_so_plainly()
    {
        Assert.Equal(("Nothing due today", "Add a task above, or look at Upcoming."), TaskBoard.EmptyText(TaskView.Today));
        Assert.Equal("Nothing done yet", TaskBoard.EmptyText(TaskView.Done).Title);
    }

    [Fact]
    public void Done_keeps_when_it_was_ticked_and_reopening_clears_it()
    {
        var done = TaskBoard.MarkDone(Own(1, Today), Now);
        Assert.Equal((MemberTaskStatus.Done, (DateTimeOffset?)Now), (done.Status, done.DoneAt));
        Assert.Null(TaskBoard.Reopen(done).DoneAt);
    }

    // ---- Quick add ---------------------------------------------------------------------------------------------

    [Fact]
    public void Quick_add_is_today_unless_tomorrow_or_a_date_is_picked_with_an_optional_time()
    {
        Assert.Equal((Today, (DateTimeOffset?)null), TaskBoard.QuickDue(new QuickAdd(), Now));
        Assert.Equal((Today.AddDays(1), (DateTimeOffset?)At(5, 16)), TaskBoard.QuickDue(new QuickAdd("tomorrow", Time: new TimeOnly(16, 0)), Now));
        Assert.Equal(Today.AddDays(9), TaskBoard.QuickDue(new QuickAdd("date", Today.AddDays(9)), Now).Day);

        Assert.Equal("Due today, Wed 4 Nov, by 5:30 PM · press Enter to add",
            TaskBoard.QuickHint(new QuickAdd(Time: new TimeOnly(17, 30)), Now));
        Assert.Equal("Due tomorrow, Thu 5 Nov · press Enter to add", TaskBoard.QuickHint(new QuickAdd("tomorrow"), Now));
        Assert.Equal("Pick today or a later date.", TaskBoard.QuickHint(new QuickAdd("date", Today.AddDays(-1)), Now));
    }

    // ---- Owner's tasks: an action holds until the snapshot shows it (the tick that flipped back) -------------------

    private static OutboxReply Done(long taskId, DateTimeOffset at) =>
        new("a1", "", at.ToUniversalTime(), OutboxReply.TaskActionKind, TaskId: taskId, Action: TaskActions.Done);

    private static TasksReply Snapshot(DateTimeOffset syncedAt, string status, params DeviceTaskActivity[] activity) =>
        new(true, TaskBackend.On, [new DeviceTask(5, "Share the report", Status: status, Activity: activity)], syncedAt, 3);

    [Fact]
    public void A_posted_done_stays_applied_while_the_newer_snapshot_still_says_in_progress()
    {
        var done = Done(5, Now);
        // The agent posted it (gone from the outbox); the list it fetched afterwards was taken before the action landed,
        // or the action's answer carried no task. The old rule retired it on time alone and the tick flipped back.
        var stale = Snapshot(Now.AddSeconds(20), "progress", new DeviceTaskActivity(Now.AddHours(-1), "member", "started"));

        var kept = OwnerTasks.Retire([done], new HashSet<string>(), stale);

        Assert.Equal([done], kept);
        var shown = OwnerTasks.ToMemberTask(Assert.Single(OwnerTasks.Overlay(stale.Tasks!, kept)), "Vishal", Now);
        Assert.Equal(MemberTaskStatus.Review, shown.Status); // still ticked, "Waiting for Vishal's OK"
    }

    [Fact]
    public void The_action_retires_once_the_snapshot_shows_it()
    {
        var done = Done(5, Now);

        Assert.Empty(OwnerTasks.Retire([done], new HashSet<string>(), Snapshot(Now.AddSeconds(30), "review")));
        Assert.Empty(OwnerTasks.Retire([done], new HashSet<string>(), Snapshot(Now.AddSeconds(30), "approved")));
        Assert.Empty(OwnerTasks.Retire([done], new HashSet<string>(),
            Snapshot(Now.AddSeconds(30), "progress", new DeviceTaskActivity(Now.AddSeconds(5), "member", "done"))));
        // The owner moved it on afterwards (reopened): their move is the truth now.
        Assert.Empty(OwnerTasks.Retire([done], new HashSet<string>(),
            Snapshot(Now.AddMinutes(1), "progress", new DeviceTaskActivity(Now.AddSeconds(40), "owner", "reopened"))));
    }

    [Fact]
    public void A_waiting_action_stays_a_vanished_task_drops_it_and_a_refused_one_is_let_go_in_the_end()
    {
        var done = Done(5, Now);

        Assert.Equal([done], OwnerTasks.Retire([done], new HashSet<string> { "a1" }, Snapshot(Now.AddHours(1), "progress")));
        Assert.Empty(OwnerTasks.Retire([Done(77, Now)], new HashSet<string>(), Snapshot(Now.AddSeconds(30), "progress")));
        Assert.Empty(OwnerTasks.Retire([done], new HashSet<string>(), Snapshot(Now + OwnerTasks.GiveUpAfter + TimeSpan.FromSeconds(1), "progress")));
        Assert.Equal([done], OwnerTasks.Retire([done], new HashSet<string>(), null)); // no snapshot yet: keep
    }

    [Fact]
    public void A_step_tick_is_reflected_when_the_step_reads_the_same()
    {
        var tick = new OutboxReply("s1", "", Now.ToUniversalTime(), OutboxReply.TaskActionKind, TaskId: 5, Action: TaskActions.Note, StepId: 51, StepDone: true);
        DeviceTask Task(bool stepDone) => new(5, "Report", Status: "progress", Steps: [new DeviceTaskStep(51, "Draft", stepDone)]);

        Assert.False(OwnerTasks.Reflected(Task(false), tick));
        Assert.True(OwnerTasks.Reflected(Task(true), tick));
    }

    // ---- Own to-dos: a change not sent yet always wins -------------------------------------------------------------

    private const long Asha = 7;

    private static MemberTask Shared(MemberTaskStatus status, DateTimeOffset updated, DateTimeOffset? synced) => new()
    {
        Id = 2, Title = "Shared thing", Mine = true, ClientId = "aaaaaaaa", Status = status, DueOn = Today,
        UpdatedAt = updated, SyncedAt = synced, SyncedFor = Asha, SentAt = Now.AddDays(-1),
    };

    private static DeviceTodo Remote(DateTimeOffset updated, string status = DeviceTodo.Open, string title = "Shared thing") =>
        new("aaaaaaaa", title, Today, [], status, Now.AddDays(-1), updated);

    [Fact]
    public void A_tick_still_waiting_to_go_up_is_not_turned_back_by_a_copy_stamped_later()
    {
        // Ticked here at 3:35; the server's copy carries 3:40 from a PC whose clock runs ahead.
        var ticked = Shared(MemberTaskStatus.Done, Now, Now.AddHours(-1));
        var waiting = new HashSet<string> { "aaaaaaaa" };

        var t = Assert.Single(OwnTodos.Merge([ticked], [Remote(Now.AddMinutes(5))], Asha, Now, waiting));

        Assert.Equal(MemberTaskStatus.Done, t.Status);
        Assert.True(t.UpdatedAt > Now.AddMinutes(5)); // stamped after the server's copy, so the server takes it too
        Assert.True(OwnTodos.Dirty(t));
        Assert.Equal([t], OwnTodos.Unsent([t], new Dictionary<string, DateTimeOffset> { ["aaaaaaaa"] = Now }, Asha)); // queued again
    }

    [Fact]
    public void A_stale_cached_list_cannot_turn_a_tick_back()
    {
        var ticked = Shared(MemberTaskStatus.Done, Now, Now.AddHours(-1));

        // Sent already (not waiting): the service's cached list from before the tick is older, so ours stays.
        Assert.Equal(MemberTaskStatus.Done, Assert.Single(OwnTodos.Merge([ticked], [Remote(Now.AddHours(-1))], Asha, Now)).Status);
        // Still waiting: same.
        Assert.Equal(MemberTaskStatus.Done,
            Assert.Single(OwnTodos.Merge([ticked], [Remote(Now.AddHours(-1))], Asha, Now, new HashSet<string> { "aaaaaaaa" })).Status);
    }

    [Fact]
    public void Once_sent_a_newer_change_from_another_pc_still_wins()
    {
        var ticked = Shared(MemberTaskStatus.Done, Now, Now.AddHours(-1));

        var t = Assert.Single(OwnTodos.Merge([ticked], [Remote(Now.AddMinutes(5), title: "Renamed elsewhere")], Asha, Now));

        Assert.Equal(("Renamed elsewhere", MemberTaskStatus.New), (t.Title, t.Status));
    }

    [Fact]
    public void A_server_copy_that_reads_the_same_does_not_restamp_a_waiting_change()
    {
        var ticked = Shared(MemberTaskStatus.Done, Now, Now.AddHours(-1));

        var t = Assert.Single(OwnTodos.Merge([ticked], [Remote(Now, DeviceTodo.Done)], Asha, Now, new HashSet<string> { "aaaaaaaa" }));

        Assert.Equal(Now, t.UpdatedAt);
    }

    [Fact]
    public void A_merge_keeps_the_local_time_while_the_day_is_the_same_and_notes_when_it_was_done()
    {
        var local = Shared(MemberTaskStatus.New, Now.AddHours(-2), Now.AddHours(-2)) with { DueAt = At(4, 17) };

        var t = Assert.Single(OwnTodos.Merge([local], [Remote(Now, DeviceTodo.Done)], Asha, Now));

        Assert.Equal(At(4, 17), t.DueAt);
        Assert.Equal(Now, t.DoneAt);
    }
}

using System.IO;
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
        Assert.Equal(("Nothing due today", "Press + to add one, or look at Upcoming."), TaskBoard.EmptyText(TaskView.Today));
        Assert.Equal("Nothing done yet", TaskBoard.EmptyText(TaskView.Done).Title);
    }

    [Fact]
    public void Done_keeps_when_it_was_ticked()
    {
        var done = TaskBoard.MarkDone(Own(1, Today), Now);
        Assert.Equal((MemberTaskStatus.Done, (DateTimeOffset?)Now), (done.Status, done.DoneAt));
    }

    // ---- Done is final (owner rule): a tick is never undone, it offers Add a note ------------------------------

    [Fact]
    public void A_tick_on_an_open_task_marks_it_done_and_on_a_ticked_one_opens_add_a_note_never_reopen()
    {
        var open = Own(1, Today);
        Assert.Equal(TickAction.MarkDone, TaskBoard.TickActionOf(open));

        // Own to-dos included: done is in the reports.
        Assert.Equal(TickAction.AddNote, TaskBoard.TickActionOf(TaskBoard.MarkDone(open, Now)));
        // Ticked a moment ago (still in place): Add a note, not Undo.
        Assert.Equal(TickAction.AddNote, TaskBoard.TickActionOf(open, new HashSet<int> { 1 }));
        // An owner's task waiting for the OK, or approved: Add a note.
        var owner = open with { Mine = false, RemoteId = 9, Id = -9, NeedsOk = true };
        Assert.Equal(TickAction.AddNote, TaskBoard.TickActionOf(TaskBoard.MarkDone(owner, Now)));
        Assert.Equal(TickAction.AddNote, TaskBoard.TickActionOf(owner with { Status = MemberTaskStatus.Approved }));
        // Called off by the owner: nothing to tick or note.
        Assert.Equal(TickAction.None, TaskBoard.TickActionOf(owner with { Status = MemberTaskStatus.Cancelled }));
    }

    [Fact]
    public void Nothing_reopens_a_done_task_any_more()
    {
        // The old one-tap "reopen" (TaskBoard.Reopen) and the model's Undo / Reopen paths are gone.
        Assert.Null(typeof(TaskBoard).GetMethod("Reopen"));
        foreach (var name in new[] { "UndoDone", "ReopenTask", "EndHold" })
        {
            Assert.Null(typeof(TrayModel).GetMethod(name, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic
                | System.Reflection.BindingFlags.Instance));
        }
        Assert.Contains("stay done", TaskBoard.DoneStaysDone);
    }

    // ---- Filters: All's calendar and Done's status menu --------------------------------------------------------

    [Fact]
    public void A_day_picked_on_all_lists_every_task_due_that_day_done_ones_too()
    {
        var day = new TaskFilter(Day: Today);
        var shown = Board().Where(t => TaskBoard.InView(t, TaskView.All, Now, null, day)).Select(t => t.Id);
        Assert.Equal([2, 3, 7], shown); // due today, due today at 5 PM, and the one done today

        var tomorrow = new TaskFilter(Day: Today.AddDays(1));
        Assert.Equal([4], Board().Where(t => TaskBoard.InView(t, TaskView.All, Now, null, tomorrow)).Select(t => t.Id));
        Assert.Equal(1, TaskBoard.Count(Board(), TaskView.All, Now, null, tomorrow));

        // The day only filters All: Today is unchanged.
        Assert.Equal(TaskBoard.Count(Board(), TaskView.Today, Now), TaskBoard.Count(Board(), TaskView.Today, Now, null, tomorrow));

        // A past day lists under its own name, not under Today.
        var monday = new TaskFilter(Day: Today.AddDays(-2));
        var s = Assert.Single(TaskBoard.Sections(Board(), TaskView.All, Now, null, monday));
        Assert.Equal("Overdue", s.Name); // task 1 is open and late
        var yesterday = Assert.Single(TaskBoard.Sections(Board(), TaskView.All, Now, null, new TaskFilter(Day: Today.AddDays(-1))));
        Assert.Equal(("Yesterday", "Tue 3 Nov"), (yesterday.Name, yesterday.Sub)); // task 8, done

        Assert.Equal("5 Nov", TaskBoard.AllLabel(tomorrow));
        Assert.Equal("All", TaskBoard.AllLabel(new TaskFilter()));
        Assert.Equal("Due on Thursday 5 November", TaskBoard.FilterLine(TaskView.All, tomorrow));
        Assert.Null(TaskBoard.FilterLine(TaskView.Today, tomorrow));
        Assert.Equal(("Nothing due that day", "Pick another day, or clear the date."),
            TaskBoard.EmptyText(TaskView.All, new TaskFilter(Day: Today.AddDays(30))));
    }

    private static MemberTask Owner(int id, MemberTaskStatus status, DateOnly? on = null) =>
        new() { Id = -id, RemoteId = id, Title = "From Vishal " + id, Mine = false, DueOn = on ?? Today, Status = status, SentAt = Now.AddDays(-1) };

    private static MemberTask[] Statuses() =>
    [
        Own(1, Today),                                                              // To do
        Own(2, Today) with { Steps = [new TaskStep("a", true), new TaskStep("b", false)] }, // Doing (a step ticked)
        Owner(3, MemberTaskStatus.New),                                             // To do
        Owner(4, MemberTaskStatus.Confirmed),
        Owner(5, MemberTaskStatus.Progress),                                        // Doing
        Owner(6, MemberTaskStatus.Help),
        Owner(7, MemberTaskStatus.Cant),                                            // Needs help too
        Owner(8, MemberTaskStatus.Review) with { DoneAt = Now },                    // Waiting for OK
        Owner(9, MemberTaskStatus.Approved) with { DoneAt = Now.AddDays(-1) },
        Own(10, Today, MemberTaskStatus.Done) with { DoneAt = Now },
        Owner(11, MemberTaskStatus.Cancelled),                                      // only in Everything done
    ];

    [Fact]
    public void Every_task_has_the_status_the_menu_names()
    {
        Assert.Equal(
            [TaskStatusKey.Todo, TaskStatusKey.Doing, TaskStatusKey.Todo, TaskStatusKey.Confirmed, TaskStatusKey.Doing, TaskStatusKey.Help,
                TaskStatusKey.Help, TaskStatusKey.Waiting, TaskStatusKey.Approved, TaskStatusKey.Done, null],
            Statuses().Select(TaskBoard.StatusOf));
        Assert.Equal(["To do", "Confirmed", "Doing", "Needs help", "Waiting for OK", "Approved", "Done"], TaskBoard.MenuStatuses.Select(TaskBoard.StatusName));
    }

    [Fact]
    public void The_done_menu_counts_everything_done_then_each_status()
    {
        var counts = TaskBoard.MenuCounts(Statuses(), Now);
        Assert.Equal(
            [(null, 4), (TaskStatusKey.Todo, 2), (TaskStatusKey.Confirmed, 1), (TaskStatusKey.Doing, 2), (TaskStatusKey.Help, 2),
                (TaskStatusKey.Waiting, 1), (TaskStatusKey.Approved, 1), (TaskStatusKey.Done, 1)],
            counts.Select(c => (c.Status, c.Count)));
    }

    [Fact]
    public void A_status_picked_on_done_lists_those_tasks_and_names_itself()
    {
        var doing = new TaskFilter(Status: TaskStatusKey.Doing);
        Assert.Equal([2, -5], Statuses().Where(t => TaskBoard.InView(t, TaskView.Done, Now, null, doing)).Select(t => t.Id));
        var section = Assert.Single(TaskBoard.Sections(Statuses(), TaskView.Done, Now, null, doing));
        Assert.Equal("Doing", section.Name);
        Assert.Equal(2, TaskBoard.Count(Statuses(), TaskView.Done, Now, null, doing));

        var waiting = new TaskFilter(Status: TaskStatusKey.Waiting);
        Assert.Equal([-8], Assert.Single(TaskBoard.Sections(Statuses(), TaskView.Done, Now, null, waiting)).Items.Select(t => t.Id));
        Assert.Equal("Waiting", TaskBoard.DoneLabel(waiting));
        Assert.Equal("Done", TaskBoard.DoneLabel(new TaskFilter()));
        Assert.Equal("Status: Waiting for OK", TaskBoard.FilterLine(TaskView.Done, waiting));

        // A task ticked a moment ago (holding in place) has left the open statuses, and isn't in Done's yet.
        var ticked = TaskBoard.MarkDone(Own(1, Today), Now);
        var holding = new HashSet<int> { 1 };
        Assert.False(TaskBoard.InView(ticked, TaskView.Done, Now, holding, new TaskFilter(Status: TaskStatusKey.Todo)));
        Assert.False(TaskBoard.InView(ticked, TaskView.Done, Now, holding, new TaskFilter(Status: TaskStatusKey.Done)));
        Assert.True(TaskBoard.InView(ticked, TaskView.Done, Now, null, new TaskFilter(Status: TaskStatusKey.Done)));

        Assert.Equal("No tasks with this status", TaskBoard.EmptyText(TaskView.Done, new TaskFilter(Status: TaskStatusKey.Approved)).Title);
    }

    [Fact]
    public void The_calendar_starts_on_monday_and_dots_the_days_with_open_tasks()
    {
        var days = TaskBoard.CalendarDays(new DateOnly(2026, 11, 1), Board());
        Assert.Equal(42, days.Count);
        Assert.Equal(new DateOnly(2026, 10, 26), days[0].Day); // 1 Nov 2026 is a Sunday: the grid starts Monday 26 Oct
        Assert.Equal(DayOfWeek.Monday, days[0].Day.DayOfWeek);
        Assert.False(days[0].InMonth);
        Assert.True(days.Single(d => d.Day == Today).HasTasks);
        Assert.True(days.Single(d => d.Day == Today.AddDays(-2)).HasTasks);
        Assert.False(days.Single(d => d.Day == Today.AddDays(-1)).HasTasks); // only a done task that day
    }

    [Fact]
    public void Moving_an_own_to_do_to_tomorrow_keeps_its_time()
    {
        var timed = Own(1, Today, at: At(4, 16));
        var moved = TrayModel.MovedToTomorrow(timed, Now);
        Assert.Equal(Today.AddDays(1), TaskBoard.When(moved).Day);
        Assert.Equal(At(5, 16), TaskBoard.When(moved).At);

        var plain = TrayModel.MovedToTomorrow(Own(2, Today.AddDays(-3)), Now);
        Assert.Equal((Today.AddDays(1), (DateTimeOffset?)null), TaskBoard.When(plain));
        Assert.Equal("Tomorrow · Thu 5 Nov", TaskBoard.DueLabel(plain, Now));
    }

    [Fact]
    public void Press_and_hold_sinks_at_220_ms_and_opens_at_500_and_a_move_cancels_it()
    {
        Assert.Equal(HoldGesture.Stage.Pressed, HoldGesture.At(TimeSpan.FromMilliseconds(100)));
        Assert.Equal(HoldGesture.Stage.Sinking, HoldGesture.At(TimeSpan.FromMilliseconds(220)));
        Assert.Equal(HoldGesture.Stage.Sinking, HoldGesture.At(TimeSpan.FromMilliseconds(499)));
        Assert.Equal(HoldGesture.Stage.Open, HoldGesture.At(TimeSpan.FromMilliseconds(500)));
        Assert.False(HoldGesture.Cancels(3, 4)); // 5 px: a still finger
        Assert.True(HoldGesture.Cancels(6, 7));  // 9 px: a scroll or a drag
    }

    // ---- Rows, chips and the Add sheet ------------------------------------------------------------------------

    [Fact]
    public void A_row_under_a_dated_heading_shows_only_the_time_and_flags_what_needs_a_look()
    {
        Assert.Equal(("By 5:00 PM", false), TaskBoard.RowDue(Own(1, null, at: At(4, 17)), Now));
        Assert.Equal(("By 11:00 AM", false), TaskBoard.RowDue(Own(1, null, at: At(5, 11)), Now)); // not "Tomorrow · Thu 5 Nov" again
        Assert.Equal(("", false), TaskBoard.RowDue(Own(1, Today.AddDays(1)), Now)); // no time: nothing
        Assert.Equal(("Due in 15 min", true), TaskBoard.RowDue(Own(1, null, at: At(4, 15, 50)), Now));
        Assert.Equal(("Was due 3:00 PM", true), TaskBoard.RowDue(Own(1, null, at: At(4, 15)), Now));
        Assert.Equal(("Overdue · yesterday", true), TaskBoard.RowDue(Own(1, Today.AddDays(-1)), Now));
        Assert.Equal(("", false), TaskBoard.RowDue(Own(1, null, MemberTaskStatus.Done, At(4, 15)), Now)); // ticked: nothing
    }

    [Theory]
    [InlineData(MemberTaskStatus.New, "To do", "Todo")]
    [InlineData(MemberTaskStatus.Confirmed, "Confirmed", "Confirmed")]
    [InlineData(MemberTaskStatus.Progress, "Doing", "Doing")]
    [InlineData(MemberTaskStatus.Help, "Needs help", "Help")]
    [InlineData(MemberTaskStatus.Review, "Waiting for Vishal’s OK", "Wait")]
    [InlineData(MemberTaskStatus.Approved, "Approved", "Approved")]
    [InlineData(MemberTaskStatus.Cancelled, "Cancelled", "Cancelled")]
    public void An_owners_task_has_one_status_chip_coloured_by_its_status(MemberTaskStatus status, string label, string tone)
    {
        Assert.Equal((label, tone), TaskBoard.StatusChip(new MemberTask { Status = status }, "Vishal"));
        Assert.Null(TaskBoard.StatusChip(new MemberTask { Status = status, Mine = true }, "Vishal")); // own to-dos: the tick only
    }

    [Fact]
    public void Every_chip_tone_has_its_brushes_in_the_theme()
    {
        var tokens = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "Zinwe.Agent.Tray", "Theme", "Tokens.xaml"));
        foreach (var tone in new[] { "Todo", "Confirmed", "Doing", "Help", "Wait", "Approved", "Cancelled" })
        {
            Assert.Contains($"x:Key=\"Z.St{tone}Text\"", tokens);
            Assert.Contains($"x:Key=\"Z.St{tone}Tint\"", tokens);
        }
    }

    [Fact]
    public void The_add_sheet_says_the_day_and_time_in_one_line()
    {
        Assert.Equal("Due tomorrow, Thu 5 Nov, by 4:00 PM.", TaskBoard.DueHint(Today.AddDays(1), new TimeOnly(16, 0), Today));
        Assert.Equal("Due today, Wed 4 Nov.", TaskBoard.DueHint(Today, null, Today));
        Assert.Equal("Due Fri 6 Nov.", TaskBoard.DueHint(TaskBoard.FormDay("later", null, Today), null, Today)); // This week: the Friday
        Assert.Equal(Today.AddDays(9), TaskBoard.FormDay("date", Today.AddDays(9), Today));
        Assert.Equal("Pick today or a later date.", TaskBoard.DueHint(Today.AddDays(-1), null, Today));

        Assert.Null(TaskBoard.ByTimes[0]); // No time first
        Assert.Equal((new TimeOnly(9, 0), new TimeOnly(20, 30), 25), (TaskBoard.ByTimes[1]!.Value, TaskBoard.ByTimes[^1]!.Value, TaskBoard.ByTimes.Count));
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

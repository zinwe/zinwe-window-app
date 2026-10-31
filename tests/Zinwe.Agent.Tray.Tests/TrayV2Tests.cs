using System.IO;
using System.Text.Json;
using Zinwe.Agent.Core.Messages;
using Zinwe.Agent.Tray.Widget;

namespace Zinwe.Agent.Tray.Tests;

/// <summary>Tray v2 (prototype board "WinApp"): the pure rules behind Tasks, the notification history, the day log,
/// reminders, the chat thread and the task seam.</summary>
public sealed class TrayV2Tests : IDisposable
{
    private static readonly TimeSpan Offset = TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 10, 28));
    private static readonly DateTimeOffset Now = new(2026, 10, 28, 14, 10, 0, Offset); // Wednesday 2:10 PM local

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "zinwe-tray-v2-" + Guid.NewGuid().ToString("N"));

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

    private static MemberTask Task(int id, MemberTaskStatus status = MemberTaskStatus.New, DueKey due = DueKey.Later, bool mine = false,
        bool isNew = false, bool needsOk = false, bool confirm = false, bool high = false, DateTimeOffset? dueAt = null, int steps = 0) => new()
    {
        Id = id, Title = "Task " + id, Status = status, DueKey = due, Due = due.ToString(), Mine = mine, IsNew = isNew, NeedsOk = needsOk,
        Confirm = confirm, High = high, DueAt = dueAt, SentAt = Now.AddHours(-id),
        Steps = [.. Enumerable.Range(1, steps).Select(i => new TaskStep("Step " + i, false))],
    };

    // ---- Tasks ------------------------------------------------------------------------

    [Fact]
    public void Tasks_have_four_plain_lanes()
    {
        Assert.Equal(TaskLane.Todo, TaskBoard.LaneOf(Task(1, MemberTaskStatus.New)));
        Assert.Equal(TaskLane.Todo, TaskBoard.LaneOf(Task(1, MemberTaskStatus.Confirmed)));
        Assert.Equal(TaskLane.Doing, TaskBoard.LaneOf(Task(1, MemberTaskStatus.Progress)));
        Assert.Equal(TaskLane.Waiting, TaskBoard.LaneOf(Task(1, MemberTaskStatus.Help)));
        Assert.Equal(TaskLane.Waiting, TaskBoard.LaneOf(Task(1, MemberTaskStatus.Review)));
        Assert.Equal(TaskLane.Waiting, TaskBoard.LaneOf(Task(1, MemberTaskStatus.Cant)));
        Assert.Equal(TaskLane.Done, TaskBoard.LaneOf(Task(1, MemberTaskStatus.Approved)));
        Assert.Equal("Waiting for Ananya’s OK", TaskBoard.LaneLabel(Task(1, MemberTaskStatus.Review), "Ananya"));
        Assert.Equal("Can’t do this", TaskBoard.LaneLabel(Task(1, MemberTaskStatus.Cant), "Ananya"));
    }

    [Fact]
    public void One_tap_done_ticks_every_step_and_needs_ok_tasks_wait_for_the_owner()
    {
        var plain = TaskBoard.MarkDone(Task(1, MemberTaskStatus.Progress, steps: 3));
        Assert.Equal(MemberTaskStatus.Done, plain.Status);
        Assert.All(plain.Steps, s => Assert.True(s.Done));

        var checkedByOwner = TaskBoard.MarkDone(Task(2, MemberTaskStatus.Progress, needsOk: true));
        Assert.Equal(MemberTaskStatus.Review, checkedByOwner.Status);
        Assert.False(TaskBoard.Finished(checkedByOwner));
        Assert.Equal("Ananya checks it before it closes.", TaskBoard.Line(checkedByOwner, "Ananya"));
        Assert.Equal("Your admin can see it’s done.", TaskBoard.Line(plain, "your admin"));
    }

    [Fact]
    public void Ticking_a_step_starts_a_task_and_locked_tasks_ignore_ticks()
    {
        var started = TaskBoard.ToggleStep(Task(1, MemberTaskStatus.New, isNew: true, steps: 2), 0);
        Assert.Equal(MemberTaskStatus.Progress, started.Status);
        Assert.False(started.IsNew);
        Assert.True(started.Steps[0].Done);

        var waiting = Task(2, MemberTaskStatus.Help, steps: 2);
        Assert.Same(waiting, TaskBoard.ToggleStep(waiting, 0));
        Assert.True(TaskBoard.Locked(Task(3, MemberTaskStatus.New, confirm: true)));

        var all = TaskBoard.ToggleStep(TaskBoard.ToggleStep(Task(4, MemberTaskStatus.Progress, steps: 2), 0), 1);
        Assert.True(TaskBoard.AllStepsDone(all));
    }

    [Fact]
    public void Asking_and_cant_leave_a_note_and_wait_with_the_owner()
    {
        var asked = TaskBoard.Ask(Task(1, MemberTaskStatus.Progress), "Need info", " Which room? ");
        Assert.Equal(MemberTaskStatus.Help, asked.Status);
        Assert.Equal(new TaskNote("You", "Need info. Which room?"), asked.Notes.Single());

        var cant = TaskBoard.Cant(Task(2), "Not my area", "");
        Assert.Equal(MemberTaskStatus.Cant, cant.Status);
        Assert.Equal("Can’t do this: Not my area", cant.Notes.Single().Text);
        Assert.Equal(MemberTaskStatus.Progress, TaskBoard.CarryOn(cant).Status);
    }

    [Fact]
    public void Quick_actions_and_focus_follow_the_board()
    {
        Assert.Equal("Start", TaskBoard.Quick(Task(1))!.Label);
        Assert.Equal("Confirm", TaskBoard.Quick(Task(1, confirm: true))!.Label);
        Assert.Equal(("Mark done", true), (TaskBoard.Quick(Task(1, MemberTaskStatus.Progress))!.Label, TaskBoard.Quick(Task(1, MemberTaskStatus.Progress))!.Primary));
        Assert.Null(TaskBoard.Quick(Task(1, mine: true)));
        Assert.Null(TaskBoard.Quick(Task(1, MemberTaskStatus.Done)));

        var todoSoon = Task(1, due: DueKey.Today);
        var todoLater = Task(2, due: DueKey.Later);
        var doing = Task(3, MemberTaskStatus.Progress, due: DueKey.Later);
        Assert.Equal((doing, true), TaskBoard.Focus([todoSoon, todoLater, doing], null, Now));
        Assert.Equal((todoSoon, false), TaskBoard.Focus([todoLater, todoSoon], null, Now));
        Assert.Equal((null, false), TaskBoard.Focus([Task(4, MemberTaskStatus.Done)], null, Now));
    }

    [Fact]
    public void Due_labels_say_overdue_and_minutes_left()
    {
        var late = Task(1, due: DueKey.Today, dueAt: Now.AddMinutes(-5));
        Assert.True(TaskBoard.IsLate(late, Now));
        Assert.Equal("Overdue · Today", TaskBoard.DueLabel(late, Now));
        Assert.Equal(DueTone.Late, TaskBoard.DueToneOf(late, Now));

        var soon = Task(2, due: DueKey.Today, dueAt: Now.AddMinutes(12));
        Assert.Equal("Due in 12 min", TaskBoard.DueLabel(soon, Now));
        Assert.Equal(DueTone.Soon, TaskBoard.DueToneOf(soon, Now));
        Assert.Equal(DueTone.Normal, TaskBoard.DueToneOf(Task(3, due: DueKey.Tomorrow), Now));
    }

    [Fact]
    public void Filters_combine_and_chips_count_with_the_other_filters()
    {
        var tasks = new[]
        {
            Task(1, MemberTaskStatus.New, DueKey.Today, isNew: true, high: true),
            Task(2, MemberTaskStatus.Progress, DueKey.Tomorrow),
            Task(3, MemberTaskStatus.Help, DueKey.Today),
            Task(4, MemberTaskStatus.Done, DueKey.Today),
            Task(5, MemberTaskStatus.New, DueKey.Today, mine: true),
        };
        Assert.Equal([1, 3, 4, 5], TaskBoard.Shown(tasks, new TaskFilter(Due: "today"), Now).Select(t => t.Id).Order());
        Assert.Equal([1], TaskBoard.Shown(tasks, new TaskFilter(High: true), Now).Select(t => t.Id));
        Assert.Equal([5], TaskBoard.Shown(tasks, new TaskFilter(From: "me"), Now).Select(t => t.Id));
        Assert.Equal(2, TaskBoard.ChipCount(tasks, "todo", TaskFilter.None, Now));
        Assert.Equal(1, TaskBoard.ChipCount(tasks, "todo", new TaskFilter(From: "owner"), Now));
        Assert.Equal(0, TaskBoard.ChipCount(tasks, "all", TaskFilter.None, Now));
        Assert.True(new TaskFilter(Status: "done").IsFiltered);
        Assert.False(TaskFilter.None.IsFiltered);

        var groups = TaskBoard.Groups(TaskBoard.Shown(tasks, TaskFilter.None, Now), "all", "Ananya");
        Assert.Equal(["Doing", "To do", "With Ananya", "Done", TaskBoard.MyTodos], groups.Select(g => g.Label));
        Assert.Equal([5], groups[^1].Items.Select(t => t.Id)); // own to-dos stay apart
        Assert.Equal("No tasks match these filters.", TaskBoard.EmptyText(new TaskFilter(High: true), "Ananya"));
        Assert.Equal("Nothing to start.", TaskBoard.EmptyText(new TaskFilter(Status: "todo"), "Ananya"));
    }

    [Fact]
    public void The_task_seam_keeps_tasks_on_this_pc_and_undo_puts_a_deleted_one_back()
    {
        var source = new LocalTaskSource(samples: false, directory: _dir);
        Assert.False(source.IsSample);
        Assert.Empty(source.Load());
        source.Save(Task(source.NextId(), mine: true));
        source.Save(Task(source.NextId(), mine: true));
        var first = source.Load()[0];
        source.Remove(first.Id);
        source.Insert(0, first);
        Assert.Equal([1, 2], new LocalTaskSource(samples: false, directory: _dir).Load().Select(t => t.Id));

        var samples = new LocalTaskSource(samples: true, directory: _dir, clock: () => Now);
        Assert.True(samples.IsSample);
        Assert.Equal(3, samples.Load().Count);
        Assert.True(File.Exists(Path.Combine(_dir, "tasks-sample.json")));
    }

    // ---- Notification history: one read state for every counter ------------------------------

    [Fact]
    public void History_merges_chat_and_notices_and_counts_unread_per_tab()
    {
        var history = new NoticeHistory(_dir);
        history.Add("New task from Ananya", "Book the room", "task:2", Now.AddMinutes(-10));
        history.Add("It’s 9:20. Press Start day when you begin.", "", "startday", Now.AddMinutes(-5));
        history.Add("Break approved", "", "home", Now.AddMinutes(-4), read: true);
        var items = new[]
        {
            new NotificationItem("41", Now.AddMinutes(-3), "info", "", "Thanks Emily.", Kind: NotificationItem.ChatKind),
            new NotificationItem("42", Now.AddMinutes(-2), "info", "", "My reply", Sender: "member", Kind: NotificationItem.ChatKind),
            new NotificationItem("-7", Now.AddMinutes(-1), "warning", "Your company blocks some websites", "Rules apply"),
        };
        var list = history.Merge(items, "Ananya Rao");
        Assert.Equal(5, list.Count); // the member's own chat line is not a notification
        Assert.Equal("notice:-7", list[0].Id);
        Assert.Equal("New message from Ananya Rao", list.Single(e => e.Go == "chat").Title);

        var counts = UnreadCounts.Of(list);
        Assert.Equal((1, 1, 2, 4), (counts.Chat, counts.Tasks, counts.Other, counts.All));

        Assert.True(history.MarkRead(e => e.Go == "task:2"));
        Assert.Equal(0, UnreadCounts.Of(new NoticeHistory(_dir).Merge([], "Ananya")).Tasks);
    }

    [Fact]
    public void History_keeps_today_only_and_at_most_thirty()
    {
        var history = new NoticeHistory(_dir);
        history.Add("Yesterday", "", "home", Now.AddDays(-1));
        for (var i = 0; i < 35; i++)
        {
            history.Add("n" + i, "", "home", Now);
        }
        Assert.Equal(NoticeHistory.Max, history.Local.Count);
        history.Add("Old", "", "home", Now.AddDays(-2));
        history.DropBefore(Now.LocalDateTime.Date);
        Assert.DoesNotContain(history.Local, e => e.Title is "Old" or "Yesterday");
    }

    // ---- The work day log (real data) ------------------------------------------------------

    private static DayLog.Entry Log(int h, int m, string action, string state, string? breakReason = null, string? endReason = null, long worked = 0) =>
        new(new DateTimeOffset(2026, 10, 28, h, m, 0, Offset), action, state, worked, 0, breakReason, null, endReason);

    [Fact]
    public void The_timeline_reads_like_the_board()
    {
        var log = new[]
        {
            Log(9, 40, "start", "Working"),
            Log(13, 10, "break", "OnBreak", "Lunch"),
            Log(13, 40, "resume", "Working"),
            Log(17, 0, "end", "Ended", endReason: "Done for today"),
            Log(17, 30, "start", "Working"),
        };
        var lines = DayLog.Today(log, Now).Select(e => (e.Label, e.Kind)).ToList();
        Assert.Equal(
        [
            ("Started day", TimelineKind.In),
            ("Break · Lunch", TimelineKind.Break),
            ("Back from break · Lunch · 30 min", TimelineKind.In),
            ("Ended day · Done for today", TimelineKind.Out),
            ("Started again", TimelineKind.In),
        ], lines);
    }

    [Fact]
    public void Last_five_working_days_use_each_days_final_total_and_today_live()
    {
        var day = new DateTimeOffset(2026, 10, 20, 18, 0, 0, Offset);
        var log = Enumerable.Range(0, 6).SelectMany(d => new[]
        {
            new DayLog.Entry(day.AddDays(d).AddHours(-8), "start", "Working", 0, 0),
            new DayLog.Entry(day.AddDays(d), "end", "Ended", (7 + d) * 3600, 0),
        }).ToList();
        var week = DayLog.LastWorkingDays(log, Now, TimeSpan.FromHours(4));
        Assert.Equal(5, week.Count);
        Assert.Equal(("Today", 4.0), (week[^1].Day, week[^1].Hours));
        Assert.Equal(12.0, week[^2].Hours); // 27 Oct: the newest earlier day
        Assert.Equal("4 h 2 min", DayLog.HoursMinutes(TimeSpan.FromMinutes(242)));
        Assert.Equal("4:02", DayLog.Hm(TimeSpan.FromMinutes(242)));
    }

    [Fact]
    public void The_log_reader_skips_half_written_lines()
    {
        Directory.CreateDirectory(_dir);
        var file = Path.Combine(_dir, "log.jsonl");
        var good = JsonSerializer.Serialize(new { At = Now, Action = "start", State = "Working", WorkedSeconds = 0, BreakSeconds = 0 });
        File.WriteAllLines(file, [good, "{\"At\":", ""]);
        Assert.Single(DayLog.Read(file));
        Assert.Empty(DayLog.Read(Path.Combine(_dir, "missing.jsonl")));
    }

    // ---- Gentle reminders -----------------------------------------------------------------

    [Fact]
    public void Work_day_reminders_only_after_consent_and_each_only_once()
    {
        var morning = new DateTimeOffset(2026, 10, 28, 9, 20, 0, Offset);
        var fresh = WorkTimerState.NewForToday(morning);
        var settings = new TraySettings();

        Assert.Empty(Reminders.Due(settings, consentAgreed: false, true, fresh, [], morning, TimeSpan.FromHours(1)));
        Assert.Empty(Reminders.Due(settings, true, true, fresh, [], morning, TimeSpan.FromMinutes(2))); // just opened
        var start = Assert.Single(Reminders.Due(settings, true, true, fresh, [], morning, TimeSpan.FromHours(1)));
        Assert.Equal(("start", "It’s 9:20. Press Start day when you begin.", "startday"), (start.Key, start.Title, start.Go));

        var after = Reminders.Remember(settings, [start], morning);
        Assert.Empty(Reminders.Due(after, true, true, fresh, [], morning.AddMinutes(30), TimeSpan.FromHours(1)));
        Assert.Empty(Reminders.Due(settings with { DayRemind = false }, true, true, fresh, [], morning, TimeSpan.FromHours(1)));
    }

    [Fact]
    public void A_long_break_a_late_evening_and_a_task_due_soon_each_get_a_reminder()
    {
        var t = WorkTimerState.NewForToday(Now);
        t.Start(Now.AddHours(-4));
        t.ToggleBreak(Now.AddMinutes(-50), "Lunch");
        var brk = Assert.Single(Reminders.Due(new TraySettings(), true, true, t, [], Now, TimeSpan.FromHours(5)));
        Assert.Equal(("Still on your break?", "Lunch · 50 min so far", "endbreak"), (brk.Title, brk.Body, brk.Go));

        var evening = new DateTimeOffset(2026, 10, 28, 19, 5, 0, Offset);
        var working = WorkTimerState.NewForToday(evening);
        working.Start(evening.AddHours(-9));
        Assert.Equal("late", Assert.Single(Reminders.Due(new TraySettings(), true, true, working, [], evening, TimeSpan.FromHours(9))).Key);

        var due = Task(9, MemberTaskStatus.New, DueKey.Today, dueAt: Now.AddMinutes(20));
        var r = Assert.Single(Reminders.Due(new TraySettings { DayRemind = false }, true, true, working, [due], Now, TimeSpan.Zero));
        Assert.Equal(("Due in 20 min", "task:9"), (r.Title, r.Go));
        var nudged = Reminders.Remember(new TraySettings { DayRemind = false }, [r], Now);
        Assert.Contains(9, nudged.Nudged);
        Assert.Empty(Reminders.Due(nudged, true, true, working, [due], Now, TimeSpan.Zero));
    }

    // ---- Chat thread --------------------------------------------------------------------------

    [Fact]
    public void The_chat_has_date_chips_run_avatars_task_cards_and_pending_replies()
    {
        var yesterday = Now.AddDays(-1);
        var items = new[]
        {
            new NotificationItem("1", yesterday, "info", "", "Welcome!", Kind: NotificationItem.ChatKind),
            new NotificationItem("2", Now.AddMinutes(-30), "info", "", "Hi", Kind: NotificationItem.ChatKind),
            new NotificationItem("3", Now.AddMinutes(-29), "info", "", "The room one matters", Kind: NotificationItem.ChatKind),
            new NotificationItem("4", Now.AddMinutes(-20), "info", "", "Booked it", Sender: "member", Kind: NotificationItem.ChatKind),
            new NotificationItem("5", Now.AddMinutes(-10), "info", "About your Lunch break at 1:10 PM", "Keep it short", Kind: NotificationItem.ChatKind),
            new NotificationItem("-9", Now, "warning", "A protection notice", "not chat"),
        };
        var quote = new ReplyQuotes.Entry("Booked it", Now.AddMinutes(-20), "Ananya Rao", "The room one matters", "3");
        var task = Task(7, MemberTaskStatus.New) with { SentAt = Now.AddMinutes(-25) };
        var pending = new OutboxReply("x", "On it", Now);

        var rows = ChatThread.Build(items, [pending], [task, Task(8, mine: true)], [quote], "Ananya", Now);
        Assert.Equal(
            ["day:" + yesterday.LocalDateTime.ToString("yyyy-MM-dd"), "1", "day:" + Now.LocalDateTime.ToString("yyyy-MM-dd"), "2", "3", "task:7", "4", "5", "pending:x"],
            rows.Select(r => r.Key));
        Assert.Equal(["Yesterday", "Today"], rows.Where(r => r.Kind == ThreadKind.Day).Select(r => r.Text));
        Assert.False(rows.Single(r => r.Key == "2").HasAvatar); // same person next: the avatar waits for the last bubble
        Assert.True(rows.Single(r => r.Key == "3").HasAvatar); // a task card ends the run: the card itself has no avatar
        Assert.False(rows.Single(r => r.Key == "task:7").HasAvatar);
        Assert.Equal("Ananya Rao", rows.Single(r => r.Key == "4").Quote?.Who);
        Assert.Equal(ThreadKind.Sys, rows.Single(r => r.Key == "5").Kind);
        Assert.True(rows.Single(r => r.Key == "5").ToneNo);
        Assert.Equal("Sending…", rows[^1].Meta);
        Assert.Equal("Task: Task 7", rows.Single(r => r.Key == "task:7").Snippet);
    }

    [Fact]
    public void Reply_quotes_are_kept_on_this_pc_and_matched_by_text_and_time()
    {
        var quotes = new ReplyQuotes(_dir);
        quotes.Add("Booked it", new Quote("Ananya", "The room one", "3"), Now);
        var again = new ReplyQuotes(_dir).Items;
        Assert.NotNull(ReplyQuotes.Find(again, " Booked it ", Now.AddMinutes(2)));
        Assert.Null(ReplyQuotes.Find(again, "Booked it", Now.AddHours(1)));
        Assert.Null(ReplyQuotes.Find(again, "Something else", Now));
    }

    [Fact]
    public void Avatars_and_names_come_from_what_the_tray_knows()
    {
        Assert.Equal("Hijab", AvatarLibrary.Find("a07")?.Label); // FR-44: the shared library (PicturesTests)
        Assert.Null(AvatarLibrary.Find("preset:sun")); // the v1.3 local presets are gone
        Assert.Equal("EC", Avatars.Initials("Emily Carter"));
        Assert.Equal("", Avatars.Initials(null)); // no name: the official mark, not a drawn "Z"
        Assert.Equal("Ananya", Avatars.First("Ananya Rao"));
        Assert.Equal("Emily Carter", new MemberProfile("emily.carter@kitecrest.com").DisplayName);
        Assert.Equal("You", new MemberProfile().DisplayName);
    }

    [Fact]
    public void Settings_pause_is_for_an_hour()
    {
        var paused = new TraySettings { PausedUntil = Now.AddHours(1) };
        Assert.True(paused.Paused(Now));
        Assert.False(paused.Paused(Now.AddHours(2)));
        Assert.False(new TraySettings().Paused(Now));
    }
}

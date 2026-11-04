using System.Globalization;
using System.Text.Json.Serialization;

namespace Zinwe.Agent.Tray.Widget;

/// <summary>Where a task is, as the owner and the member both see it.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<MemberTaskStatus>))]
public enum MemberTaskStatus
{
    /// <summary>Just sent; not opened or started yet.</summary>
    New,
    /// <summary>Confirmed by the member (tasks that asked for a confirmation first).</summary>
    Confirmed,
    Progress,
    /// <summary>The member asked the owner a question about it.</summary>
    Help,
    /// <summary>Marked done; the owner checks it before it closes ("needs OK" tasks).</summary>
    Review,
    /// <summary>The member said they can't do it.</summary>
    Cant,
    Done,
    Approved,
    /// <summary>The owner called it off (FR-20 v2 side state).</summary>
    Cancelled,
}

/// <summary>The four plain states the member sees (board: To do · Doing · Needs help · Done).</summary>
public enum TaskLane { Todo, Doing, Waiting, Done }

[JsonConverter(typeof(JsonStringEnumConverter<DueKey>))]
public enum DueKey { Overdue, Today, Tomorrow, Later }

/// <param name="Id">The step's id on the server (owner's tasks); null for the member's own to-dos.</param>
public sealed record TaskStep(string Label, bool Done, long? Id = null);

/// <summary>One line of an owner's task's history, as the member reads it ("You started", "Ananya approved it").</summary>
public sealed record TaskActivityLine(DateTimeOffset At, string Text);

/// <param name="By">"You" for the member, otherwise the owner's name.</param>
public sealed record TaskNote(string By, string Text);

/// <summary>
/// One task in the member's Tasks tab: from the owner, or the member's own to-do (<see cref="Mine"/>).
/// Owner's tasks from the server (FR-20 v2) carry <see cref="RemoteId"/> and use <c>Id = -RemoteId</c>, so they
/// never collide with the own to-dos' ids (1, 2, 3 … in tasks.json).
/// </summary>
public sealed record MemberTask
{
    public int Id { get; init; }
    /// <summary>The task's id on the server (an owner's task, FR-20 v2); null for samples and own to-dos.</summary>
    public long? RemoteId { get; init; }
    /// <summary>What happened to it so far (owner's tasks), oldest first.</summary>
    public IReadOnlyList<TaskActivityLine> Activity { get; init; } = [];
    public string Title { get; init; } = "";
    public string Details { get; init; } = "";
    /// <summary>
    /// The due words as they read when the task was saved ("Today", "Tomorrow"). Kept in tasks.json for older builds,
    /// never shown: words go stale overnight, so the board works them out from the real date every time
    /// (<see cref="TaskBoard.DueLabel"/>).
    /// </summary>
    public string Due { get; init; } = "";
    /// <summary>The group as it was when saved; like <see cref="Due"/>, only for older builds (see <see cref="TaskBoard.KeyOf"/>).</summary>
    public DueKey DueKey { get; init; } = DueKey.Later;
    /// <summary>Exact due time when there is one (drives "Due in 12 min" and the due-soon reminder).</summary>
    public DateTimeOffset? DueAt { get; init; }
    /// <summary>A picked due date for the member's own tasks.</summary>
    public DateOnly? DueDate { get; init; }
    public bool High { get; init; }
    /// <summary>The owner asked for a confirmation before the member starts.</summary>
    public bool Confirm { get; init; }
    /// <summary>The owner checks it when it's done (Done goes to Review first).</summary>
    public bool NeedsOk { get; init; }
    /// <summary>Not opened yet (NEW tag, counts in the Tasks badge).</summary>
    public bool IsNew { get; init; }
    public IReadOnlyList<TaskNote> Notes { get; init; } = [];
    public IReadOnlyList<TaskStep> Steps { get; init; } = [];
    /// <summary>Changed while offline: saved on this PC, sent when back.</summary>
    public bool Queued { get; init; }
    /// <summary>The member's own to-do (dashed card, Edit / Delete).</summary>
    public bool Mine { get; init; }
    public MemberTaskStatus Status { get; init; } = MemberTaskStatus.New;
    public DateTimeOffset SentAt { get; init; }
    /// <summary>The day "Today" / "Tomorrow" meant when picked (they carry no <see cref="DueDate"/>): what the server gets.</summary>
    public DateOnly? DueOn { get; init; }
    /// <summary>
    /// An own to-do's id on the server. Set = shared: the member's admin can see it and it follows them to a new PC.
    /// Null = it stays on this PC (see <see cref="OwnTodos"/>).
    /// </summary>
    public string? ClientId { get; init; }
    /// <summary>When the member last changed a shared to-do; the newest change wins.</summary>
    public DateTimeOffset? UpdatedAt { get; init; }
    /// <summary>The <see cref="UpdatedAt"/> the server last confirmed.</summary>
    public DateTimeOffset? SyncedAt { get; init; }
    /// <summary>The member (server id) a shared to-do belongs to; null until the first list from the server.</summary>
    public long? SyncedFor { get; init; }
    /// <summary>Deleted, and the server doesn't know yet: hidden, kept so the next pull doesn't bring it back.</summary>
    public DateTimeOffset? DeletedAt { get; init; }
    /// <summary>When it was ticked (the Done view, "Done today"); null while it's open.</summary>
    public DateTimeOffset? DoneAt { get; init; }
}

/// <summary>The Tasks tab's four views (F-16): due by today, coming later, everything open, and what's ticked.</summary>
public enum TaskView { Today, Upcoming, All, Done }

/// <summary>
/// A dated heading on the Tasks tab and the tasks under it: "Overdue", "Today · Wed 4 Nov", "Tomorrow · Thu 5 Nov",
/// "Friday · Fri 6 Nov", "No date"; in Done, "Done today" and "Earlier".
/// </summary>
/// <param name="Sub">The date after the name ("Wed 4 Nov"), or "".</param>
/// <param name="Late">Overdue: the heading reads in the late colour.</param>
public sealed record TaskSection(string Name, string Sub, bool Late, IReadOnlyList<MemberTask> Items);

/// <summary>The one action shown on a closed card, so the common case is a single tap.</summary>
public sealed record QuickAction(string Label, bool Primary, string Aria);

/// <summary>
/// The Tasks rules, pure so they're unit-tested: four plain lanes, one-tap Done (final: no Undo, Add a note instead), the four
/// views with their dated headings, due words worked out from the clock, and which task Home puts in focus.
/// </summary>
public static class TaskBoard
{
    /// <summary>How long a ticked task stays where it was, ticked with Add a note, before it moves to Done.</summary>
    public static readonly TimeSpan TickHold = TimeSpan.FromSeconds(3);

    public static bool Finished(MemberTask t) => t.Status is MemberTaskStatus.Done or MemberTaskStatus.Approved or MemberTaskStatus.Cancelled;

    /// <summary>Done on the member's side: finished, or an owner's task waiting for the owner's OK (Review).</summary>
    public static bool Ticked(MemberTask t) => Finished(t) || t.Status == MemberTaskStatus.Review;

    public static TaskLane LaneOf(MemberTask t) => t.Status switch
    {
        MemberTaskStatus.New or MemberTaskStatus.Confirmed => TaskLane.Todo,
        MemberTaskStatus.Progress => TaskLane.Doing,
        MemberTaskStatus.Help or MemberTaskStatus.Review or MemberTaskStatus.Cant => TaskLane.Waiting,
        _ => TaskLane.Done,
    };

    public static bool NeedsConfirm(MemberTask t) => t.Status == MemberTaskStatus.New && t.Confirm;

    /// <summary>Done steps can't be ticked while the task waits for the owner or is finished.</summary>
    public static bool Locked(MemberTask t) => NeedsConfirm(t) || t.Status is MemberTaskStatus.Review or MemberTaskStatus.Help
        or MemberTaskStatus.Cant || Finished(t);

    public static string LaneLabel(MemberTask t, string ownerFirst) => t.Status switch
    {
        MemberTaskStatus.Review => $"Waiting for {ownerFirst}’s OK",
        MemberTaskStatus.Cant => "Can’t do this",
        MemberTaskStatus.Cancelled => "Cancelled",
        _ => LaneOf(t) switch
        {
            TaskLane.Todo => "To do",
            TaskLane.Doing => "Doing",
            TaskLane.Waiting => "Needs help",
            _ => "Done",
        },
    };

    // ---- When it's due, from the real date -------------------------------------------------

    /// <summary>
    /// The day a task is due and, when it has one, the exact time. From the owner's due time (a midnight or end-of-day
    /// time means a date only), else the picked date, else the day "Today" / "Tomorrow" meant when it was saved
    /// (<see cref="MemberTask.DueOn"/>). A row from before DueOn existed falls back to its saved word, counted from the
    /// day it was made, so it still moves along with the calendar.
    /// </summary>
    public static (DateOnly? Day, DateTimeOffset? At) When(MemberTask t)
    {
        if (t.DueAt is { } at)
        {
            var local = at.ToLocalTime();
            var dateOnly = local.TimeOfDay == TimeSpan.Zero || local.TimeOfDay >= new TimeSpan(23, 59, 0);
            return (DateOnly.FromDateTime(local.DateTime), dateOnly ? null : at);
        }
        if ((t.DueDate ?? t.DueOn) is { } day)
        {
            return (day, null);
        }
        if (t.SentAt == default)
        {
            return (null, null);
        }
        var made = DateOnly.FromDateTime(t.SentAt.LocalDateTime);
        return t.DueKey switch
        {
            DueKey.Today or DueKey.Overdue => (made, null),
            DueKey.Tomorrow => (made.AddDays(1), null),
            _ => (null, null),
        };
    }

    public static DateOnly Today(DateTimeOffset now) => DateOnly.FromDateTime(now.LocalDateTime);

    /// <summary>Days from today to the due day (negative = past); null without a date.</summary>
    public static int? DaysFrom(MemberTask t, DateTimeOffset now) => When(t).Day is { } d ? d.DayNumber - Today(now).DayNumber : null;

    /// <summary>The group as of <paramref name="now"/>: yesterday's "Tomorrow" is Today after midnight. No date reads as Later.</summary>
    public static DueKey KeyOf(MemberTask t, DateTimeOffset now) => DaysFrom(t, now) switch
    {
        null => DueKey.Later,
        < 0 => DueKey.Overdue,
        0 => DueKey.Today,
        1 => DueKey.Tomorrow,
        _ => DueKey.Later,
    };

    /// <summary>Sorting by due: exact time first within a day, then the rest of the day, later days, no date last.</summary>
    public static double Rank(MemberTask t, DateTimeOffset now)
    {
        var (day, at) = When(t);
        var start = Today(now).ToDateTime(TimeOnly.MinValue);
        return at is { } x ? (x.LocalDateTime - start).TotalSeconds
            : day is { } d ? (d.ToDateTime(TimeOnly.MinValue) - start).TotalSeconds + 86_399
            : double.MaxValue;
    }

    /// <summary>Past its time (or, without one, past its day) and not ticked yet.</summary>
    public static bool IsLate(MemberTask t, DateTimeOffset now)
    {
        if (Ticked(t))
        {
            return false;
        }
        var (day, at) = When(t);
        return at is { } x ? x <= now : day is { } d && d < Today(now);
    }

    /// <summary>Due within the hour (not late yet).</summary>
    public static bool IsSoon(MemberTask t, DateTimeOffset now) =>
        !Ticked(t) && !IsLate(t, now) && When(t).At is { } at && at - now <= TimeSpan.FromHours(1);

    /// <summary>"Wed 4 Nov".</summary>
    public static string DayLabel(DateOnly day) => day.ToString("ddd d MMM", CultureInfo.InvariantCulture);

    /// <summary>"4:00 PM".</summary>
    public static string TimeLabel(DateTimeOffset at) => at.ToLocalTime().ToString("h:mm tt", CultureInfo.InvariantCulture);

    /// <summary>
    /// The due words, worked out from the clock: "By 4:00 PM", "Due in 25 min", "Was due 4:00 PM", "Overdue · yesterday",
    /// "Today", "Tomorrow · Thu 5 Nov", "Fri 6 Nov · 11:00 AM"; "" without a date.
    /// </summary>
    public static string DueLabel(MemberTask t, DateTimeOffset now)
    {
        var (day, at) = When(t);
        if (day is not { } d)
        {
            return "";
        }
        var n = d.DayNumber - Today(now).DayNumber;
        if (IsLate(t, now))
        {
            return n == 0 && at is { } due ? "Was due " + TimeLabel(due) : "Overdue · " + (n == -1 ? "yesterday" : DayLabel(d));
        }
        if (n == 0 && at is { } x)
        {
            var minutes = (int)Math.Ceiling((x - now).TotalMinutes);
            return minutes <= 60 && !Ticked(t) ? $"Due in {Math.Max(1, minutes)} min" : "By " + TimeLabel(x);
        }
        var word = n == 0 ? "Today" : n == 1 ? "Tomorrow · " + DayLabel(d) : DayLabel(d);
        return at is { } y ? word + " · " + TimeLabel(y) : word;
    }

    /// <summary>How the due words read: late (bold), soon or due today (accent), else muted.</summary>
    public static DueTone DueToneOf(MemberTask t, DateTimeOffset now) =>
        IsLate(t, now) ? DueTone.Late
        : IsSoon(t, now) || (!Ticked(t) && DaysFrom(t, now) == 0) ? DueTone.Soon
        : DueTone.Normal;

    // ---- What the member does ---------------------------------------------------

    public static MemberTask Start(MemberTask t) => t with { Status = MemberTaskStatus.Progress, IsNew = false };

    /// <summary>One-tap Done: every step ticked; a "needs OK" task waits for the owner first.</summary>
    public static MemberTask MarkDone(MemberTask t, DateTimeOffset? at = null) => t with
    {
        Status = t.NeedsOk ? MemberTaskStatus.Review : MemberTaskStatus.Done,
        Steps = [.. t.Steps.Select(s => s with { Done = true })],
        IsNew = false,
        DoneAt = at ?? t.DoneAt,
    };

    // Done is final (owner rule): a tick is never undone, own to-dos included, because done work is in the day's
    // reports. Something changed later? The member adds a note to the task instead (see TickAction).

    /// <summary>
    /// What a tap on a task's round tick does: an open task is marked done; a ticked one (just now, waiting for the OK,
    /// or done) opens Add a note instead of reopening it; a cancelled task does nothing.
    /// </summary>
    public static TickAction TickActionOf(MemberTask t, IReadOnlySet<int>? holding = null) =>
        t.Status == MemberTaskStatus.Cancelled ? TickAction.None
        : Ticked(t) || (holding?.Contains(t.Id) ?? false) ? TickAction.AddNote
        : TickAction.MarkDone;

    /// <summary>The line on a done task's details card, under Add a note.</summary>
    public const string DoneStaysDone = "Done tasks stay done: they’re part of your day’s work and the reports.";

    /// <summary>Ticking a step of a task not started yet starts it.</summary>
    public static MemberTask ToggleStep(MemberTask t, int index)
    {
        if (index < 0 || index >= t.Steps.Count || Locked(t))
        {
            return t;
        }
        var steps = t.Steps.Select((s, i) => i == index ? s with { Done = !s.Done } : s).ToList();
        var start = t.Status is MemberTaskStatus.New or MemberTaskStatus.Confirmed;
        return t with { Steps = steps, Status = start ? MemberTaskStatus.Progress : t.Status, IsNew = false };
    }

    public static bool AllStepsDone(MemberTask t) => t.Steps.Count > 0 && t.Steps.All(s => s.Done);

    /// <summary>"Ask {owner}": a reason chip and / or a note; the task waits with the owner.</summary>
    public static MemberTask Ask(MemberTask t, string? reason, string? note) =>
        t with { Status = MemberTaskStatus.Help, IsNew = false, Notes = [.. t.Notes, new TaskNote("You", Join(reason, note))] };

    public static MemberTask Cant(MemberTask t, string? reason, string? note) =>
        t with { Status = MemberTaskStatus.Cant, IsNew = false, Notes = [.. t.Notes, new TaskNote("You", "Can’t do this: " + Join(reason, note))] };

    public static MemberTask CarryOn(MemberTask t) => t with { Status = MemberTaskStatus.Progress };

    public static MemberTask AddNote(MemberTask t, string text) => t with { Notes = [.. t.Notes, new TaskNote("You", text.Trim())] };

    /// <summary>"Need access. The shared drive" (either part may be empty).</summary>
    public static string Join(string? reason, string? note) =>
        string.Join(". ", new[] { reason?.Trim(), note?.Trim() }.Where(s => !string.IsNullOrEmpty(s)));

    // ---- What the Tasks tab shows (F-16: All · Today · Upcoming · Done) ------------------------------

    /// <summary>The statuses Done's menu offers (in its order); the first four are open, the last three ticked.</summary>
    public static IReadOnlyList<TaskStatusKey> MenuStatuses { get; } =
    [
        TaskStatusKey.Todo, TaskStatusKey.Confirmed, TaskStatusKey.Doing, TaskStatusKey.Help, TaskStatusKey.Waiting,
        TaskStatusKey.Approved, TaskStatusKey.Done,
    ];

    /// <summary>A status of the menu that lists open tasks (To do, Confirmed, Doing, Needs help).</summary>
    public static bool IsOpenStatus(TaskStatusKey s) => s is TaskStatusKey.Todo or TaskStatusKey.Confirmed or TaskStatusKey.Doing or TaskStatusKey.Help;

    /// <summary>
    /// The status of any task, own or the owner's, as the chips and Done's menu name it. An own to-do is Doing once a
    /// step is ticked (or it was started), else To do, and Done when ticked. A cancelled task has none (it is only in
    /// "Everything done").
    /// </summary>
    public static TaskStatusKey? StatusOf(MemberTask t) => t.Status switch
    {
        MemberTaskStatus.Cancelled => null,
        MemberTaskStatus.Approved => TaskStatusKey.Approved,
        MemberTaskStatus.Review => TaskStatusKey.Waiting,
        MemberTaskStatus.Done => TaskStatusKey.Done,
        _ when t.Mine => t.Status == MemberTaskStatus.Progress || t.Steps.Any(s => s.Done) ? TaskStatusKey.Doing : TaskStatusKey.Todo,
        MemberTaskStatus.Confirmed => TaskStatusKey.Confirmed,
        MemberTaskStatus.Progress => TaskStatusKey.Doing,
        MemberTaskStatus.Help or MemberTaskStatus.Cant => TaskStatusKey.Help,
        _ => TaskStatusKey.Todo,
    };

    /// <summary>"To do", "Confirmed", "Doing", "Needs help", "Waiting for OK", "Approved", "Done".</summary>
    public static string StatusName(TaskStatusKey s) => s switch
    {
        TaskStatusKey.Todo => "To do",
        TaskStatusKey.Confirmed => "Confirmed",
        TaskStatusKey.Doing => "Doing",
        TaskStatusKey.Help => "Needs help",
        TaskStatusKey.Waiting => "Waiting for OK",
        TaskStatusKey.Approved => "Approved",
        _ => "Done",
    };

    /// <summary>
    /// The menu's colour dot: the resource key of the status chip's text colour (<c>Z.St{Tone}Text</c>), Done in ink, so
    /// the dots follow dark mode and High Contrast like the chips.
    /// </summary>
    public static string StatusDot(TaskStatusKey s) => s switch
    {
        TaskStatusKey.Todo => "Z.StTodoText",
        TaskStatusKey.Confirmed => "Z.StConfirmedText",
        TaskStatusKey.Doing => "Z.StDoingText",
        TaskStatusKey.Help => "Z.StHelpText",
        TaskStatusKey.Waiting => "Z.StWaitText",
        TaskStatusKey.Approved => "Z.StApprovedText",
        _ => "Z.Ink",
    };

    /// <summary>
    /// True when a task shows in a view. <paramref name="holding"/>: tasks ticked in the last few seconds stay where
    /// they were until the hold ends. <paramref name="filter"/>: All with a day from its calendar lists every task due
    /// that day (done ones too); Done with a status from its menu lists the tasks with that status (open ones for an
    /// open status).
    /// </summary>
    public static bool InView(MemberTask t, TaskView view, DateTimeOffset now, IReadOnlySet<int>? holding = null, TaskFilter? filter = null)
    {
        var open = !Ticked(t) || (holding?.Contains(t.Id) ?? false);
        if (view == TaskView.All && filter?.Day is { } day)
        {
            return When(t).Day == day;
        }
        if (view == TaskView.Done)
        {
            return filter?.Status is { } status ? HasStatus(t, status, open, filter) : !open;
        }
        if (!open)
        {
            return false;
        }
        return view switch
        {
            TaskView.Today => DaysFrom(t, now) is <= 0,
            TaskView.Upcoming => DaysFrom(t, now) is >= 1,
            _ => true,
        };
    }

    // A just-ticked task (still holding) keeps the status it had before the tick (TaskFilter.HeldWas) for an open
    // filter, so it holds its place there too; it is not in a done status until the hold ends.
    private static bool HasStatus(MemberTask t, TaskStatusKey status, bool open, TaskFilter filter) =>
        IsOpenStatus(status)
            ? open && OpenStatus(t, filter) == status
            : !open && StatusOf(t) == status;

    private static TaskStatusKey? OpenStatus(MemberTask t, TaskFilter filter) =>
        !Ticked(t) ? StatusOf(t)
        : filter.HeldWas is { } held && held.TryGetValue(t.Id, out var was) ? was
        : null;

    /// <summary>
    /// The number beside a view: what it lists (with its filter); for Done without a status, what was ticked today.
    /// </summary>
    public static int Count(IEnumerable<MemberTask> tasks, TaskView view, DateTimeOffset now, IReadOnlySet<int>? holding = null,
        TaskFilter? filter = null) =>
        view == TaskView.Done && filter?.Status is null
            ? DoneToday(tasks, now, holding).Count
            : tasks.Count(t => InView(t, view, now, holding, filter));

    /// <summary>Done's menu: "Everything done" (null) and each status, with how many tasks each lists.</summary>
    public static IReadOnlyList<(TaskStatusKey? Status, int Count)> MenuCounts(IEnumerable<MemberTask> tasks, DateTimeOffset now,
        IReadOnlySet<int>? holding = null, IReadOnlyDictionary<int, TaskStatusKey>? heldWas = null)
    {
        var list = tasks.ToList();
        return
        [
            (null, list.Count(t => InView(t, TaskView.Done, now, holding))),
            .. MenuStatuses.Select(s => ((TaskStatusKey?)s, list.Count(t => InView(t, TaskView.Done, now, holding, new TaskFilter(Status: s) { HeldWas = heldWas })))),
        ];
    }

    /// <summary>Ticked today (and not still holding): folded at the bottom of Today.</summary>
    public static IReadOnlyList<MemberTask> DoneToday(IEnumerable<MemberTask> tasks, DateTimeOffset now, IReadOnlySet<int>? holding = null) =>
        [.. tasks.Where(t => InView(t, TaskView.Done, now, holding) && t.DoneAt is { } at && DateOnly.FromDateTime(at.LocalDateTime) == Today(now))
            .OrderByDescending(t => t.DoneAt)];

    /// <summary>
    /// The view as dated sections: Overdue, Today, Tomorrow, then each later day, then No date; the most pressing first
    /// inside each (exact times, then high priority). Done: "Done today", then "Earlier", newest first; Done with an
    /// open status: one section named after it, by due.
    /// </summary>
    public static IReadOnlyList<TaskSection> Sections(IEnumerable<MemberTask> tasks, TaskView view, DateTimeOffset now,
        IReadOnlySet<int>? holding = null, TaskFilter? filter = null)
    {
        var shown = tasks.Where(t => InView(t, view, now, holding, filter)).ToList();
        if (view == TaskView.Done && filter?.Status is { } status && IsOpenStatus(status))
        {
            return shown.Count == 0 ? []
                : [new TaskSection(StatusName(status), "", false, [.. shown.OrderBy(t => Rank(t, now)).ThenByDescending(t => t.High).ThenBy(t => t.Id)])];
        }
        if (view == TaskView.Done)
        {
            var today = DoneToday(shown, now, holding);
            var earlier = shown.Except(today).OrderByDescending(t => t.DoneAt ?? DateTimeOffset.MinValue).ThenBy(t => t.Id).ToList();
            var done = new List<TaskSection>();
            if (today.Count > 0)
            {
                done.Add(new TaskSection("Done today", "", false, today));
            }
            if (earlier.Count > 0)
            {
                done.Add(new TaskSection("Earlier", "", false, earlier));
            }
            return done;
        }
        return [.. shown
            .OrderBy(t => Rank(t, now)).ThenByDescending(t => t.High).ThenBy(t => t.Id)
            .GroupBy(t => SectionOf(t, now, filter?.Day is not null))
            .OrderBy(g => g.Key.Order)
            .Select(g => new TaskSection(g.Key.Name, g.Key.Sub, g.Key.Late, [.. g]))];
    }

    // <paramref name="picked"/>: All on a picked day, where a past day is named as it is (else a ticked task holding
    // its place stays under Today).
    private static (int Order, string Name, string Sub, bool Late) SectionOf(MemberTask t, DateTimeOffset now, bool picked)
    {
        if (When(t).Day is not { } day)
        {
            return (int.MaxValue, "No date", "", false);
        }
        var n = day.DayNumber - Today(now).DayNumber;
        return n switch
        {
            < 0 when IsLate(t, now) => (-1, "Overdue", "", true),
            < 0 when picked => (n, day == Today(now).AddDays(-1) ? "Yesterday" : day.ToString("dddd", CultureInfo.InvariantCulture), DayLabel(day), false),
            <= 0 => (0, "Today", DayLabel(Today(now)), false),
            1 => (1, "Tomorrow", DayLabel(day), false),
            _ => (n, day.ToString("dddd", CultureInfo.InvariantCulture), DayLabel(day), false),
        };
    }

    /// <summary>What an empty view says: a plain heading and one line.</summary>
    public static (string Title, string Line) EmptyText(TaskView view, TaskFilter? filter = null) => view switch
    {
        TaskView.All when filter?.Day is not null => ("Nothing due that day", "Pick another day, or clear the date."),
        TaskView.Done when filter?.Status is not null => ("No tasks with this status", "Pick another status, or clear it."),
        TaskView.Today => ("Nothing due today", "Press + to add one, or look at Upcoming."),
        TaskView.Upcoming => ("Nothing coming up", "Tasks for tomorrow and later show here."),
        TaskView.All => ("No open tasks", "Everything is done."),
        _ => ("Nothing done yet", "Ticked tasks show here."),
    };

    /// <summary>All's label: "All", or the picked day ("5 Nov").</summary>
    public static string AllLabel(TaskFilter? filter) =>
        filter?.Day is { } d ? d.ToString("d MMM", CultureInfo.InvariantCulture) : "All";

    /// <summary>Done's label: "Done", or the picked status ("Doing"; "Waiting" for Waiting for OK, to fit).</summary>
    public static string DoneLabel(TaskFilter? filter) =>
        filter?.Status is { } s ? s == TaskStatusKey.Waiting ? "Waiting" : StatusName(s) : "Done";

    /// <summary>The line under the filters while one is on: "Due on Thursday 5 November", "Status: Doing"; null without.</summary>
    public static string? FilterLine(TaskView view, TaskFilter? filter) => view switch
    {
        TaskView.All when filter?.Day is { } d => "Due on " + d.ToString("dddd d MMMM", CultureInfo.InvariantCulture),
        TaskView.Done when filter?.Status is { } s => "Status: " + StatusName(s),
        _ => null,
    };

    /// <summary>
    /// The calendar on All: the 42 days shown for <paramref name="month"/> (Monday first, with the end of the month
    /// before and the start of the next), and which have open tasks due (a small dot).
    /// </summary>
    public static IReadOnlyList<(DateOnly Day, bool InMonth, bool HasTasks)> CalendarDays(DateOnly month, IEnumerable<MemberTask> tasks,
        IReadOnlySet<int>? holding = null)
    {
        var first = new DateOnly(month.Year, month.Month, 1);
        var lead = ((int)first.DayOfWeek + 6) % 7; // Monday first
        var due = tasks.Where(t => !Ticked(t) || (holding?.Contains(t.Id) ?? false)).Select(t => When(t).Day).OfType<DateOnly>().ToHashSet();
        return [.. Enumerable.Range(0, 42).Select(i => first.AddDays(i - lead)).Select(d => (d, d.Month == first.Month, due.Contains(d)))];
    }

    /// <summary>The view that shows a task (opening it from a notice or Home): its day's view, else All, else Done.</summary>
    public static TaskView ViewFor(MemberTask t, DateTimeOffset now) =>
        InView(t, TaskView.Today, now) ? TaskView.Today
        : InView(t, TaskView.Upcoming, now) ? TaskView.Upcoming
        : InView(t, TaskView.All, now) ? TaskView.All
        : TaskView.Done;

    /// <summary>
    /// The due words on a row of the Tasks tab, where the heading already names the day: only the time ("By 11:00 AM"),
    /// or what needs a look now ("Overdue · yesterday", "Was due 3:00 PM", "Due in 25 min", flagged
    /// <paramref name="Urgent"/>: a small dot before them). "" for a task without a time, and for a ticked one.
    /// </summary>
    public static (string Text, bool Urgent) RowDue(MemberTask t, DateTimeOffset now)
    {
        var at = When(t).At;
        if (Ticked(t) || When(t).Day is null)
        {
            return ("", false);
        }
        if (IsLate(t, now) || IsSoon(t, now))
        {
            return (DueLabel(t, now), true);
        }
        return at is { } x ? ("By " + TimeLabel(x), false) : ("", false);
    }

    /// <summary>The day the sheet's due chip means: Today, Tomorrow, This week (the next Friday) or the picked date.</summary>
    public static DateOnly FormDay(string due, DateOnly? date, DateOnly today) => due switch
    {
        "tomorrow" => today.AddDays(1),
        "later" => today.AddDays(((int)DayOfWeek.Friday - (int)today.DayOfWeek + 7) % 7 is var add && add == 0 ? 7 : add),
        "date" when date is { } d => d,
        _ => today,
    };

    /// <summary>The Add / Edit sheet's line under the due choice: "Due tomorrow, Thu 5 Nov, by 4:00 PM."</summary>
    public static string DueHint(DateOnly day, TimeOnly? time, DateOnly today)
    {
        var n = day.DayNumber - today.DayNumber;
        if (n < 0)
        {
            return "Pick today or a later date.";
        }
        var when = n == 0 ? "today, " : n == 1 ? "tomorrow, " : "";
        var by = time is { } t ? ", by " + TimeLabel(new DateTimeOffset(day.ToDateTime(t, DateTimeKind.Local))) : "";
        return $"Due {when}{DayLabel(day)}{by}.";
    }

    /// <summary>The sheet's "By (optional)" choices: no time, then every half hour from 9:00 AM to 8:30 PM.</summary>
    public static IReadOnlyList<TimeOnly?> ByTimes { get; } =
        [null, .. Enumerable.Range(18, 24).Select(h => (TimeOnly?)new TimeOnly(h / 2, h % 2 * 30))];

    /// <summary>
    /// The one status chip on an owner's task, coloured by its status: the words and the tone, which names the
    /// <c>Z.St{Tone}Text</c> / <c>Z.St{Tone}Tint</c> brushes. Null for the member's own to-dos (the tick says it all).
    /// </summary>
    public static (string Label, string Tone)? StatusChip(MemberTask t, string ownerFirst) => t.Mine ? null : t.Status switch
    {
        MemberTaskStatus.Confirmed => ("Confirmed", "Confirmed"),
        MemberTaskStatus.Progress => ("Doing", "Doing"),
        MemberTaskStatus.Help => ("Needs help", "Help"),
        MemberTaskStatus.Cant => ("Can’t do this", "Help"),
        MemberTaskStatus.Review => ($"Waiting for {ownerFirst}’s OK", "Wait"),
        MemberTaskStatus.Approved => ("Approved", "Approved"),
        MemberTaskStatus.Done => ("Done", "Approved"),
        MemberTaskStatus.Cancelled => ("Cancelled", "Cancelled"),
        _ => ("To do", "Todo"),
    };

    /// <summary>The group the member's own to-dos go in: kept apart from the owner's tasks.</summary>
    public const string MyTodos = "My to-dos";

    public const string SharedTodoHint = "Just for you and your admin: your admin can see it, and it follows you to a new PC.";
    public const string LocalTodoHint = "Just for you: it stays on this PC, under My to-dos.";
    public const string SharedTodoLine = "Your own to-do · your admin can see this";
    public const string LocalTodoLine = "Your own to-do · kept on this PC only";

    /// <summary>The line on an own to-do's card. One that reached the server stays visible to the admin even with sharing off.</summary>
    public static string OwnLine(MemberTask t, bool sharingOn) =>
        t.ClientId is not null && (sharingOn || t.SyncedAt is not null) ? SharedTodoLine : LocalTodoLine;

    /// <summary>The add / edit sheet's line, said before the member saves. <paramref name="editing"/> is null for a new to-do.</summary>
    public static string FormHint(MemberTask? editing, bool sharingOn) => editing is null
        ? sharingOn ? SharedTodoHint : LocalTodoHint
        : sharingOn && editing.ClientId is null ? "Save a change and your admin can see it, and it follows you to a new PC."
        : "Change the name, date or steps.";

    /// <summary>True when this task's waiting change really goes somewhere (an own to-do on this PC only never does).</summary>
    public static bool WaitsToSend(MemberTask t) => t.Queued && (!t.Mine || t.ClientId is not null);

    /// <summary>Home's focus card: the task in progress (the one last started, else the soonest), else the next to do.</summary>
    public static (MemberTask? Task, bool Now) Focus(IEnumerable<MemberTask> tasks, int? focusId, DateTimeOffset now)
    {
        var list = tasks.ToList();
        var doing = list.Where(t => t.Status == MemberTaskStatus.Progress).OrderBy(t => Rank(t, now)).ToList();
        var focus = doing.FirstOrDefault(t => t.Id == focusId) ?? doing.FirstOrDefault();
        if (focus is not null)
        {
            return (focus, true);
        }
        return (list.Where(t => LaneOf(t) == TaskLane.Todo).OrderBy(t => Rank(t, now)).FirstOrDefault(), false);
    }

    /// <summary>The one action on a closed card: Start / Confirm while to do, Mark done while doing.</summary>
    public static QuickAction? Quick(MemberTask t)
    {
        if (t.Mine || Finished(t))
        {
            return null;
        }
        return LaneOf(t) switch
        {
            TaskLane.Todo => NeedsConfirm(t)
                ? new QuickAction("Confirm", false, (t.RemoteId is null ? "Confirm and start: " : "Confirm: ") + t.Title)
                : new QuickAction("Start", false, "Start: " + t.Title),
            TaskLane.Doing => new QuickAction("Mark done", true, "Mark done: " + t.Title),
            _ => null,
        };
    }

    /// <summary>The line under an open card's actions.</summary>
    public static string Line(MemberTask t, string ownerFirst) => Capital(t.Status switch
    {
        MemberTaskStatus.Review => $"{ownerFirst} checks it before it closes.",
        MemberTaskStatus.Help => t.RemoteId is null ? $"You asked {ownerFirst}. The answer arrives in Chat."
            : $"You asked {ownerFirst} for help. They see it on this task.",
        MemberTaskStatus.Cant => $"You told {ownerFirst} you can’t do this. They’ll reply in Chat.",
        MemberTaskStatus.Approved => $"Approved by {ownerFirst}.",
        MemberTaskStatus.Cancelled => $"{ownerFirst} cancelled this task.",
        MemberTaskStatus.Done => $"{ownerFirst} can see it’s done.",
        _ when NeedsConfirm(t) => $"{ownerFirst} asked you to confirm before you start.",
        _ => "",
    });

    private static string Capital(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];
}

public enum DueTone { Normal, Soon, Late }

/// <summary>The statuses Done's menu finds tasks by (own to-dos and the owner's alike).</summary>
public enum TaskStatusKey { Todo, Confirmed, Doing, Help, Waiting, Approved, Done }

/// <summary>
/// The Tasks tab's filters: a day picked on All's calendar, a status picked on Done's menu. Each applies only to its
/// own view; null = none.
/// </summary>
public sealed record TaskFilter(DateOnly? Day = null, TaskStatusKey? Status = null)
{
    /// <summary>
    /// The status each task ticked a moment ago had before its tick (the model's holds): under an open status (Done ▾
    /// To do, Doing …) such a task stays in place for <see cref="TaskBoard.TickHold"/>, like in the other views.
    /// </summary>
    public IReadOnlyDictionary<int, TaskStatusKey>? HeldWas { get; init; }
}

/// <summary>What a tap on a task's tick does (done is final: never back to open).</summary>
public enum TickAction { None, MarkDone, AddNote }

/// <summary>
/// Press and hold a task row: after <see cref="SinkAfter"/> the row sinks a little, at <see cref="OpenAfter"/> its
/// details open. Moving the pointer more than <see cref="Slop"/> px (a scroll or a drag) cancels it.
/// </summary>
public static class HoldGesture
{
    public static readonly TimeSpan SinkAfter = TimeSpan.FromMilliseconds(220);
    public static readonly TimeSpan OpenAfter = TimeSpan.FromMilliseconds(500);
    public const double Slop = 8;
    /// <summary>How far the row sinks (its scale) while held.</summary>
    public const double SinkScale = 0.975;

    public enum Stage { Pressed, Sinking, Open }

    public static Stage At(TimeSpan held) => held >= OpenAfter ? Stage.Open : held >= SinkAfter ? Stage.Sinking : Stage.Pressed;

    public static bool Cancels(double dx, double dy) => Math.Sqrt(dx * dx + dy * dy) > Slop;

    /// <summary>
    /// One press on a row, walked through its stages by a clock. It ends (no card) when the pointer moves past
    /// <see cref="Slop"/>, on <see cref="Cancel"/>, or when the list is drawn again before the card opened
    /// (<see cref="Redrawn"/>): the row under the finger is then a new one, and a quick tap must not open a card later.
    /// </summary>
    public sealed class Press(double x, double y)
    {
        /// <summary>Still running: it may yet sink or open.</summary>
        public bool Live { get; private set; } = true;

        /// <summary>It reached <see cref="OpenAfter"/>: the card opened.</summary>
        public bool Opened { get; private set; }

        /// <summary>The stage after <paramref name="held"/>; null once the press has ended.</summary>
        public Stage? Step(TimeSpan held)
        {
            if (!Live)
            {
                return null;
            }
            var stage = At(held);
            if (stage == Stage.Open)
            {
                Opened = true;
                Live = false;
            }
            return stage;
        }

        /// <summary>The pointer is at (<paramref name="atX"/>, <paramref name="atY"/>); true when that ended the press.</summary>
        public bool Move(double atX, double atY)
        {
            if (Live && Cancels(atX - x, atY - y))
            {
                Live = false;
                return true;
            }
            return false;
        }

        public void Cancel() => Live = false;

        public void Redrawn()
        {
            if (!Opened)
            {
                Live = false;
            }
        }
    }
}

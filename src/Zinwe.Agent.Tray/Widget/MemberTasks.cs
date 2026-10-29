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
    /// <summary>The due label as the board shows it ("Today, 4:00 PM", "Fri 30 Oct", "Tomorrow").</summary>
    public string Due { get; init; } = "";
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
}

/// <summary>A filter on the Tasks tab. Every part combines with the others.</summary>
public sealed record TaskFilter(string Status = "all", string Due = "any", string From = "any", bool High = false)
{
    public bool IsFiltered => Status != "all" || Due != "any" || From != "any" || High;
    public static readonly TaskFilter None = new();
}

/// <summary>A group on the Tasks tab ("DOING", "TO DO", "WITH {OWNER}", "DONE").</summary>
public sealed record TaskGroup(string Label, IReadOnlyList<MemberTask> Items);

/// <summary>The one action shown on a closed card, so the common case is a single tap.</summary>
public sealed record QuickAction(string Label, bool Primary, string Aria);

/// <summary>
/// The Tasks rules from the prototype board, pure so they're unit-tested: four plain lanes, one-tap Done (with
/// Undo at the call site), filters, groups, due labels and which task Home puts in focus.
/// </summary>
public static class TaskBoard
{
    public static readonly (string Key, string Label)[] StatusChips =
        [("all", "All"), ("new", "New"), ("todo", "To do"), ("doing", "In progress"), ("waiting", "Needs help"), ("done", "Done")];

    public static readonly (string Key, string Label)[] DueOptions =
        [("any", "Any date"), ("overdue", "Overdue"), ("today", "Today"), ("tomorrow", "Tomorrow"), ("week", "This week"), ("later", "Later")];

    public static (string Key, string Label)[] FromOptions(string ownerFirst) =>
        [("any", "Anyone"), ("owner", "From " + ownerFirst), ("me", "Added by me")];

    public static bool Finished(MemberTask t) => t.Status is MemberTaskStatus.Done or MemberTaskStatus.Approved or MemberTaskStatus.Cancelled;

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

    /// <summary>Sorting by due: exact time first, then overdue, today, tomorrow, later.</summary>
    public static double Rank(MemberTask t, DateTimeOffset now) =>
        t.DueAt is { } at ? (at - now.Date).TotalSeconds
        : t.DueDate is { } d ? (d.ToDateTime(TimeOnly.MinValue) - now.Date).TotalSeconds + 1
        : t.DueKey switch
        {
            DueKey.Overdue => -1,
            DueKey.Today => 86_000,
            DueKey.Tomorrow => 172_800,
            _ => 432_000,
        };

    public static bool IsLate(MemberTask t, DateTimeOffset now) =>
        !Finished(t) && (t.DueKey == DueKey.Overdue || (t.DueAt is { } at && at <= now));

    /// <summary>Due within 30 minutes (not late yet).</summary>
    public static bool IsSoon(MemberTask t, DateTimeOffset now) =>
        !Finished(t) && !IsLate(t, now) && t.DueAt is { } at && at - now <= TimeSpan.FromMinutes(30);

    public static string DueLabel(MemberTask t, DateTimeOffset now) =>
        IsLate(t, now) ? "Overdue · " + t.Due
        : IsSoon(t, now) ? $"Due in {Math.Max(1, (int)Math.Ceiling((t.DueAt!.Value - now).TotalMinutes))} min"
        : t.Due;

    /// <summary>How the due label reads: late (red, bold), soon or today (orange), else muted.</summary>
    public static DueTone DueToneOf(MemberTask t, DateTimeOffset now) =>
        IsLate(t, now) ? DueTone.Late
        : IsSoon(t, now) || (!Finished(t) && t.DueKey == DueKey.Today) ? DueTone.Soon
        : DueTone.Normal;

    // ---- What the member does ---------------------------------------------------

    public static MemberTask Start(MemberTask t) => t with { Status = MemberTaskStatus.Progress, IsNew = false };

    /// <summary>One-tap Done: every step ticked; a "needs OK" task waits for the owner first.</summary>
    public static MemberTask MarkDone(MemberTask t) => t with
    {
        Status = t.NeedsOk ? MemberTaskStatus.Review : MemberTaskStatus.Done,
        Steps = [.. t.Steps.Select(s => s with { Done = true })],
        IsNew = false,
    };

    /// <summary>The own-task tick un-done: back to new, steps cleared.</summary>
    public static MemberTask Reopen(MemberTask t) => t with
    {
        Status = MemberTaskStatus.New,
        Steps = [.. t.Steps.Select(s => s with { Done = false })],
    };

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

    // ---- What the Tasks tab shows ---------------------------------------------------

    public static bool PassesStatus(MemberTask t, string status) => status switch
    {
        "new" => !Finished(t) && t.IsNew,
        "todo" => LaneOf(t) == TaskLane.Todo,
        "doing" => LaneOf(t) == TaskLane.Doing,
        "waiting" => LaneOf(t) == TaskLane.Waiting,
        "done" => Finished(t),
        _ => true,
    };

    public static bool PassesDue(MemberTask t, string due, DateTimeOffset now)
    {
        var weekEnd = now.Date.AddDays(7);
        return due switch
        {
            "overdue" => IsLate(t, now),
            "today" => t.DueKey is DueKey.Today or DueKey.Overdue,
            "tomorrow" => t.DueKey == DueKey.Tomorrow,
            "week" => t.DueKey == DueKey.Later && (t.DueDate is null || t.DueDate.Value.ToDateTime(TimeOnly.MinValue) <= weekEnd),
            "later" => t.DueKey == DueKey.Later && t.DueDate is { } d && d.ToDateTime(TimeOnly.MinValue) > weekEnd,
            _ => true,
        };
    }

    public static bool PassesFrom(MemberTask t, string from) => from switch
    {
        "owner" => !t.Mine,
        "me" => t.Mine,
        _ => true,
    };

    private static bool PassesOthers(MemberTask t, TaskFilter f, DateTimeOffset now) =>
        PassesDue(t, f.Due, now) && PassesFrom(t, f.From) && (!f.High || t.High);

    public static IReadOnlyList<MemberTask> Shown(IEnumerable<MemberTask> tasks, TaskFilter f, DateTimeOffset now) =>
        [.. tasks.Where(t => PassesStatus(t, f.Status) && PassesOthers(t, f, now)).OrderBy(t => Rank(t, now)).ThenBy(t => t.Id)];

    /// <summary>How many tasks a status chip would list, given the other filters (0 for "All").</summary>
    public static int ChipCount(IEnumerable<MemberTask> tasks, string status, TaskFilter f, DateTimeOffset now) =>
        status == "all" ? 0 : tasks.Count(t => PassesStatus(t, status) && PassesOthers(t, f, now));

    /// <summary>The group the member's own to-dos go in: kept apart from the owner's tasks (they stay on this PC).</summary>
    public const string MyTodos = "MY TO-DOS";

    public static IReadOnlyList<TaskGroup> Groups(IReadOnlyList<MemberTask> shown, string statusFilter, string ownerFirst)
    {
        var owners = shown.Where(t => !t.Mine).ToList();
        var mine = shown.Where(t => t.Mine).ToList();
        var groups = new List<TaskGroup>();
        if (statusFilter == "done")
        {
            Add("DONE", owners);
        }
        else
        {
            Add("DOING", owners.Where(t => LaneOf(t) == TaskLane.Doing));
            Add("TO DO", owners.Where(t => LaneOf(t) == TaskLane.Todo));
            Add("WITH " + ownerFirst.ToUpperInvariant(), owners.Where(t => LaneOf(t) == TaskLane.Waiting));
            Add("DONE", owners.Where(Finished));
        }
        // Own to-dos last, open ones first (the order Shown gave them within each half).
        Add(MyTodos, mine.Where(t => !Finished(t)).Concat(mine.Where(Finished)));
        return groups;

        void Add(string label, IEnumerable<MemberTask> items)
        {
            var list = items.ToList();
            if (list.Count > 0)
            {
                groups.Add(new TaskGroup(label, list));
            }
        }
    }

    public static string EmptyText(TaskFilter f, string ownerFirst) =>
        f.Due != "any" || f.From != "any" || f.High ? "No tasks match these filters."
        : f.Status switch
        {
            "new" => $"Nothing new. You’ve opened everything {ownerFirst} sent.",
            "todo" => "Nothing to start.",
            "doing" => "Nothing in progress. Start a task from To do.",
            "waiting" => $"Nothing is with {ownerFirst} right now.",
            "done" => "Nothing done yet today.",
            _ => $"Nothing here yet. New tasks from {ownerFirst} arrive here, or add your own with +.",
        };

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
        MemberTaskStatus.Help => $"You asked {ownerFirst}. The answer arrives in Chat.",
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

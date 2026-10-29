using System.Globalization;
using System.IO;
using System.Text.Json;
using Zinwe.Agent.Core.Backend;
using Zinwe.Agent.Core.Messages;

namespace Zinwe.Agent.Tray.Widget;

/// <summary>
/// The owner's tasks from the server (FR-20 v2) as the Tasks tab shows them. Pure, so it's unit-tested:
/// <list type="bullet">
/// <item><see cref="ToMemberTask"/> maps the wire task (status, due, priority, steps, help note, activity);</item>
/// <item><see cref="Overlay"/> applies the member's actions that the agent hasn't confirmed yet, so a tap shows at
///   once (optimistic) and the next sync confirms - or quietly puts back - what the server really holds.</item>
/// </list>
/// </summary>
public static class OwnerTasks
{
    /// <summary>Owner's tasks use negative ids in the tray, so they never collide with the own to-dos' 1, 2, 3 ….</summary>
    public static int IdOf(long remoteId) => remoteId is > 0 and <= int.MaxValue ? -(int)remoteId : int.MinValue + 1;

    /// <summary>The server status → the member's status, and whether it's still unopened (NEW).</summary>
    public static (MemberTaskStatus Status, bool IsNew) StatusOf(string? status) => (status ?? "").ToLowerInvariant() switch
    {
        "sent" => (MemberTaskStatus.New, true),
        "seen" => (MemberTaskStatus.New, false),
        "accepted" => (MemberTaskStatus.Confirmed, false),
        "progress" => (MemberTaskStatus.Progress, false),
        "help" => (MemberTaskStatus.Help, false),
        "review" => (MemberTaskStatus.Review, false),
        "approved" => (MemberTaskStatus.Approved, false),
        "cancelled" or "canceled" => (MemberTaskStatus.Cancelled, false),
        _ => (MemberTaskStatus.New, false),
    };

    public static MemberTask ToMemberTask(DeviceTask d, string ownerFirst, DateTimeOffset now, bool queued = false)
    {
        var (status, isNew) = StatusOf(d.Status);
        var (due, key, date) = DueOf(d.DueAt, now);
        var activity = d.Activity ?? [];
        var notes = activity
            .Where(a => !string.IsNullOrWhiteSpace(a.Note) && CarriesWords(a.Action))
            .Select(a => new TaskNote(IsMember(a.By) ? "You" : ownerFirst, a.Note!.Trim()))
            .ToList();
        if (status == MemberTaskStatus.Help && !string.IsNullOrWhiteSpace(d.HelpNote) && !notes.Any(n => n.Text == d.HelpNote.Trim()))
        {
            notes.Add(new TaskNote("You", d.HelpNote.Trim()));
        }
        return new MemberTask
        {
            Id = IdOf(d.Id),
            RemoteId = d.Id,
            Title = d.Title,
            Details = (d.Note ?? "").Trim(),
            Due = due,
            DueKey = key,
            DueAt = d.DueAt,
            DueDate = date,
            High = string.Equals(d.Priority, "high", StringComparison.OrdinalIgnoreCase),
            // The contract's flow: the member confirms (accept) before starting; done waits for the owner's OK.
            Confirm = (d.Status ?? "") is "sent" or "seen",
            NeedsOk = true,
            IsNew = isNew,
            Status = status,
            Steps = [.. (d.Steps ?? []).Select(s => new TaskStep(s.Title, s.Done, s.Id))],
            Notes = notes,
            Activity = [.. activity.OrderBy(a => a.At).Select(a => new TaskActivityLine(a.At, Describe(a, ownerFirst)))],
            SentAt = d.CreatedAt ?? d.UpdatedAt ?? now,
            Queued = queued,
        };
    }

    /// <summary>"Today, 4:00 PM", "Tomorrow", "Fri 30 Oct", "No due date" + the key the filters and sorting use.</summary>
    public static (string Label, DueKey Key, DateOnly? Date) DueOf(DateTimeOffset? dueAt, DateTimeOffset now)
    {
        if (dueAt is not { } at)
        {
            return ("No due date", DueKey.Later, null);
        }
        var local = at.ToLocalTime();
        var today = now.ToLocalTime().Date;
        var day = local.Date;
        // A due date without a time (midnight, or the end of the day) reads without one.
        var time = local.TimeOfDay is { TotalMinutes: 0 } || local.TimeOfDay >= new TimeSpan(23, 59, 0)
            ? "" : ", " + local.ToString("h:mm tt", CultureInfo.CurrentCulture);
        if (day < today)
        {
            return (local.ToString("ddd d MMM", CultureInfo.CurrentCulture), DueKey.Overdue, DateOnly.FromDateTime(day));
        }
        if (day == today)
        {
            return ("Today" + time, DueKey.Today, DateOnly.FromDateTime(day));
        }
        if (day == today.AddDays(1))
        {
            return ("Tomorrow" + time, DueKey.Tomorrow, DateOnly.FromDateTime(day));
        }
        return (local.ToString("ddd d MMM", CultureInfo.CurrentCulture), DueKey.Later, DateOnly.FromDateTime(day));
    }

    /// <summary>
    /// Activity whose note is words someone wrote (a question, a note, the owner's comment) - shown as a note on the
    /// card. Step ticks carry the step's title and "edited" the changed fields: those are history, not notes.
    /// </summary>
    private static bool CarriesWords(string? action) => (action ?? "").ToLowerInvariant() is
        "help" or "note" or "done" or "approved" or "approve" or "reopened" or "reopen" or "cancelled" or "cancel" or "nudged" or "nudge";

    /// <summary>One history line in plain words (activity names from the backend, 15-api-reference "Tasks (FR-20 v2)").</summary>
    public static string Describe(DeviceTaskActivity a, string ownerFirst)
    {
        var member = IsMember(a.By);
        var who = member ? "You" : ownerFirst;
        var step = string.IsNullOrWhiteSpace(a.Note) ? "a step" : "“" + a.Note.Trim() + "”";
        return (a.Action ?? "").ToLowerInvariant() switch
        {
            "created" or "create" or "sent" => $"{ownerFirst} sent it",
            "seen" => "You opened it",
            "accept" or "accepted" => "You confirmed it",
            "start" or "started" or "progress" => member ? "You started it" : $"{ownerFirst} moved it to in progress",
            "help" => "You asked for help",
            "done" or "review" => "You marked it done",
            "step_done" => $"{who} ticked {step}",
            "step_undone" => $"{who} unticked {step}",
            "note" => $"{who} added a note",
            "approve" or "approved" => $"{ownerFirst} approved it",
            "reopen" or "reopened" => $"{ownerFirst} reopened it",
            "cancel" or "cancelled" => $"{ownerFirst} cancelled it",
            "nudge" or "nudged" => $"{ownerFirst} sent a reminder",
            "edit" or "edited" or "update" or "updated" => $"{ownerFirst} changed it",
            var other when other.Length > 0 => $"{who}: {other.Replace('_', ' ')}",
            _ => member ? "You updated it" : $"{ownerFirst} updated it",
        };
    }

    /// <summary>The activity name the backend records for a member action (so a queued line and the server's match).</summary>
    public static string ActivityName(OutboxReply a) => a.Action switch
    {
        TaskActions.Accept => "accepted",
        TaskActions.Start => "started",
        TaskActions.Note when a.StepId is not null => a.StepDone == false ? "step_undone" : "step_done",
        _ => a.Action ?? "",
    };

    private static bool IsMember(string? by) => string.Equals(by, "member", StringComparison.OrdinalIgnoreCase);

    // ---- The member's actions, shown before the server confirms them ------------------------------------------

    /// <summary>The server's tasks with the member's not-yet-confirmed actions applied, in the order they were made.</summary>
    public static IReadOnlyList<DeviceTask> Overlay(IReadOnlyList<DeviceTask> tasks, IEnumerable<OutboxReply> actions)
    {
        var byTask = actions.Where(a => a.IsTaskAction && a.TaskId is not null)
            .OrderBy(a => a.CreatedAtUtc)
            .GroupBy(a => a.TaskId!.Value)
            .ToDictionary(g => g.Key, g => g.ToList());
        if (byTask.Count == 0)
        {
            return tasks;
        }
        return [.. tasks.Select(t => byTask.TryGetValue(t.Id, out var list) ? list.Aggregate(t, Apply) : t)];
    }

    /// <summary>One member action on a task, as the server will apply it (contract FR-20 v2).</summary>
    public static DeviceTask Apply(DeviceTask t, OutboxReply a)
    {
        var note = string.IsNullOrWhiteSpace(a.Body) ? null : a.Body.Trim();
        var status = t.Status;
        var steps = t.Steps;
        var helpNote = t.HelpNote;
        switch (a.Action)
        {
            case TaskActions.Seen:
                status = status == "sent" ? "seen" : status;
                break;
            case TaskActions.Accept:
                status = status is "sent" or "seen" ? "accepted" : status;
                break;
            case TaskActions.Start:
                status = status is "approved" or "cancelled" ? status : "progress";
                break;
            case TaskActions.Help:
                status = status is "approved" or "cancelled" ? status : "help";
                helpNote = note ?? helpNote;
                break;
            case TaskActions.Done:
                status = status is "approved" or "cancelled" ? status : "review";
                break;
            case TaskActions.Note when a.StepId is { } stepId:
                steps = [.. (steps ?? []).Select(s => s.Id == stepId ? s with { Done = a.StepDone ?? !s.Done } : s)];
                break;
        }
        var activity = t.Activity ?? [];
        var name = ActivityName(a);
        if (a.StepId is { } sid)
        {
            note = (t.Steps ?? []).FirstOrDefault(x => x.Id == sid)?.Title ?? note; // the server notes the step's title
        }
        // Once the server has the line (same action and note, made around then), don't show it twice.
        var known = activity.Any(x => IsMember(x.By) && x.Action == name && (x.Note ?? "") == (note ?? "")
            && x.At >= a.CreatedAtUtc - TimeSpan.FromMinutes(2));
        if (!known && a.Action != TaskActions.Seen)
        {
            activity = [.. activity, new DeviceTaskActivity(a.CreatedAtUtc, "member", name, note)];
        }
        return t with { Status = status, Steps = steps, HelpNote = helpNote, Activity = activity };
    }
}

/// <summary>
/// Task notices for the history and the Tasks badge (FR-20 v2): a new task from the owner, and what the owner did
/// since the member last looked (approved, reopened, cancelled, a reminder, a change). Pure; the marks it returns are
/// kept in task-seen.json so a restart doesn't repeat them.
/// </summary>
public static class TaskNotices
{
    public sealed record Notice(string Title, string Body, string Go, string Key, DateTimeOffset At);

    /// <param name="marks">Per task id, the time of the newest owner change already noticed; null = first run
    /// (then only tasks nobody opened yet are announced, not the whole history).</param>
    public static (IReadOnlyList<Notice> Notices, Dictionary<long, DateTimeOffset> Marks) Diff(
        IReadOnlyDictionary<long, DateTimeOffset>? marks, IReadOnlyList<DeviceTask> tasks, string ownerFirst, DateTimeOffset now)
    {
        var notices = new List<Notice>();
        var next = new Dictionary<long, DateTimeOffset>();
        foreach (var t in tasks)
        {
            var lastOwner = (t.Activity ?? []).Where(a => !string.Equals(a.By, "member", StringComparison.OrdinalIgnoreCase))
                .OrderBy(a => a.At).LastOrDefault();
            var mark = lastOwner?.At ?? t.CreatedAt ?? now;
            next[t.Id] = mark;
            var go = "task:" + OwnerTasks.IdOf(t.Id).ToString(CultureInfo.InvariantCulture);
            var (due, _, _) = OwnerTasks.DueOf(t.DueAt, now);
            if (marks is null || !marks.TryGetValue(t.Id, out var seen))
            {
                // New to this PC: announce it unless it's already finished (or, on the first run, already opened).
                var open = t.Status is not ("approved" or "cancelled");
                if (open && (marks is not null || t.Status == "sent"))
                {
                    notices.Add(new Notice($"New task from {ownerFirst}", $"{t.Title} · due {Lower(due)}", go,
                        $"task-new:{t.Id}", t.CreatedAt ?? now));
                }
                continue;
            }
            if (lastOwner is not null && lastOwner.At > seen && IsAnnounced(lastOwner.Action))
            {
                notices.Add(new Notice(OwnerLine(lastOwner.Action, ownerFirst), t.Title, go,
                    $"task-owner:{t.Id}:{lastOwner.At.UtcTicks}", lastOwner.At));
            }
        }
        // Keep marks of tasks that dropped out of the list for a while (no repeat if they come back).
        if (marks is not null)
        {
            foreach (var (id, at) in marks)
            {
                next.TryAdd(id, at);
            }
        }
        return (notices, next);
    }

    private static string Lower(string due) => due == "No due date" ? "no date" : due;

    private static bool IsAnnounced(string? action) => (action ?? "").ToLowerInvariant() is not ("created" or "create" or "sent");


    /// <param name="ownerFirst">"Ananya", or "your admin" when the name isn't known (capitalised where it starts a line).</param>
    public static string OwnerLine(string? action, string ownerFirst) => Capital((action ?? "").ToLowerInvariant() switch
    {
        "approve" or "approved" => $"{ownerFirst} approved your task",
        "reopen" or "reopened" => $"{ownerFirst} reopened a task",
        "cancel" or "cancelled" => $"{ownerFirst} cancelled a task",
        "nudge" or "nudged" => $"Reminder from {ownerFirst}",
        "note" => $"{ownerFirst} added a note",
        "edit" or "edited" or "update" or "updated" => $"{ownerFirst} changed a task",
        _ => $"{ownerFirst} updated a task",
    });

    private static string Capital(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];
}

/// <summary>task-seen.json: the marks <see cref="TaskNotices.Diff"/> works from (best effort, like the other widget files).</summary>
public sealed class TaskSeenStore(string? directory = null)
{
    private readonly string _path = Path.Combine(directory ?? WidgetPaths.Directory, "task-seen.json");

    /// <summary>Null when there's no file yet (the first run).</summary>
    public Dictionary<long, DateTimeOffset>? Load()
    {
        try
        {
            return File.Exists(_path) ? JsonSerializer.Deserialize<Dictionary<long, DateTimeOffset>>(File.ReadAllText(_path)) : null;
        }
        catch
        {
            return null;
        }
    }

    public void Save(Dictionary<long, DateTimeOffset> marks)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(marks));
            File.Move(tmp, _path, overwrite: true);
        }
        catch
        {
            // Best effort: at worst a notice shows again.
        }
    }
}

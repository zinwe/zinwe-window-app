using Zinwe.Agent.Core.Backend;

namespace Zinwe.Agent.Tray.Widget;

/// <summary>
/// The member's own to-dos between this PC and their admin's dashboard: pure rules, so they're unit-tested.
/// tasks.json stays the member's copy: every change lands there first, works offline, and reaches the server later.
///
/// A to-do with a <see cref="MemberTask.ClientId"/> is shared: the member was told their admin can see it. One without
/// stays on this PC and nothing here ever sends it. That covers every to-do made before sharing existed (the app
/// promised those stay here) until the member changes one in the Edit sheet, which says what saving does. Ticking or
/// deleting such a to-do is not sharing it. It also covers to-dos made while sharing was off (an older service, a plan
/// without tasks): those were promised the same, so they don't go up later on their own.
///
/// Merge rule, the same as the backend's: the last write wins by <see cref="MemberTask.UpdatedAt"/>; a delete wins over
/// an edit of the same age or older; an edit made after a delete brings the to-do back. Notes stay on this PC.
/// </summary>
public static class OwnTodos
{
    /// <summary>The most steps the backend takes on one to-do.</summary>
    public const int MaxSteps = 50;

    /// <summary>Changed here and the server hasn't confirmed it yet.</summary>
    public static bool Dirty(MemberTask t) => t.ClientId is not null && t.SyncedAt != t.UpdatedAt;

    /// <summary>
    /// A change's time: whole milliseconds (the server keeps microseconds, this PC ticks in 100 ns: the same write must
    /// compare equal on both sides), and always later than <paramref name="after"/>, so a change is never lost to an
    /// earlier one when the clock steps back.
    /// </summary>
    public static DateTimeOffset Stamp(DateTimeOffset now, DateTimeOffset? after = null)
    {
        var utc = now.ToUniversalTime();
        var at = new DateTimeOffset(utc.Ticks - (utc.Ticks % TimeSpan.TicksPerMillisecond), TimeSpan.Zero);
        return after is { } last && at <= last ? last.AddMilliseconds(1) : at;
    }

    /// <summary>A new or edited to-do becomes shared. <paramref name="member"/> may still be unknown (first run, offline).</summary>
    public static MemberTask Share(MemberTask t, long? member) =>
        t.ClientId is null ? t with { ClientId = Guid.NewGuid().ToString("N"), SyncedFor = member } : t;

    /// <summary>The Edit sheet's save. Only a real change shares a to-do that was staying on this PC.</summary>
    public static MemberTask AfterEdit(MemberTask old, MemberTask edited, bool sharingOn, long? member) =>
        sharingOn && Changed(old, edited) ? Share(edited, member) : edited;

    public static bool Changed(MemberTask old, MemberTask edited) =>
        old.Title != edited.Title || TaskBoard.When(old).Day != TaskBoard.When(edited).Day || old.DueDate != edited.DueDate
        || !old.Steps.Select(s => s.Label).SequenceEqual(edited.Steps.Select(s => s.Label));

    /// <summary>The server's copy says what this PC's row says (the parts that go up: name, date, steps, done, deleted).</summary>
    public static bool Same(MemberTask t, DeviceTodo d) =>
        t.Title == d.Title && (t.DueDate ?? t.DueOn) == d.DueDate && TaskBoard.Finished(t) == (d.Status == DeviceTodo.Done)
        && (t.DeletedAt is not null) == d.Deleted
        && t.Steps.Take(MaxSteps).Select(s => (s.Label, s.Done)).SequenceEqual((d.Steps ?? []).Select(s => (s.Title, s.Done)));

    /// <summary>What goes up: a shared to-do as it stands now. Notes and details stay here.</summary>
    public static DeviceTodo ToWire(MemberTask t) => new(
        t.ClientId ?? throw new ArgumentException("Only a shared to-do goes to the server.", nameof(t)),
        t.Title,
        t.DueDate ?? t.DueOn,
        [.. t.Steps.Take(MaxSteps).Select(s => new DeviceTodoStep(s.Label, s.Done))],
        TaskBoard.Finished(t) ? DeviceTodo.Done : DeviceTodo.Open,
        UpdatedAt: t.UpdatedAt ?? default,
        Deleted: t.DeletedAt is not null);

    /// <summary>The label and group for a due day.</summary>
    public static (string Label, DueKey Key) DueOf(DateOnly? due, DateOnly today)
    {
        if (due is not { } date)
        {
            return ("", DueKey.Later);
        }
        var days = date.DayNumber - today.DayNumber;
        return days switch
        {
            < 0 => (date.ToString("ddd d MMM"), DueKey.Overdue),
            0 => ("Today", DueKey.Today),
            1 => ("Tomorrow", DueKey.Tomorrow),
            _ => (date.ToString("ddd d MMM"), DueKey.Later),
        };
    }

    /// <summary>
    /// A to-do from the server over what this PC has (<paramref name="t"/> is a new row, or the older local one).
    /// Done is final (owner rule): a newer copy that says open (a PC still on 1.3.8 unticked it) never reopens a to-do
    /// that is done here. Its name, date and steps are taken; it stays done with its steps ticked, and is stamped just
    /// after the server's copy, so Done goes back up and wins there too.
    /// </summary>
    private static MemberTask Apply(MemberTask t, DeviceTodo d, long? member, DateOnly today, DateTimeOffset now)
    {
        var (due, key) = DueOf(d.DueDate, today);
        var steps = (d.Steps ?? []).Select(s => new TaskStep(s.Title, s.Done)).ToList();
        var keepDone = d.Status != DeviceTodo.Done && TaskBoard.Finished(t);
        if (keepDone)
        {
            steps = [.. steps.Select(s => s with { Done = true })];
        }
        var status = d.Status == DeviceTodo.Done ? MemberTaskStatus.Done
            : keepDone ? t.Status
            : t.Status == MemberTaskStatus.New && steps.Any(s => s.Done) ? MemberTaskStatus.Progress
            : t.Status;
        var applied = t with
        {
            Title = d.Title, Due = due, DueKey = key, DueDate = d.DueDate, DueOn = d.DueDate, Steps = steps, Status = status,
            UpdatedAt = d.UpdatedAt, SyncedAt = d.UpdatedAt, SyncedFor = member, DeletedAt = null, Queued = false,
            // The time of day stays on this PC (the server keeps dates only): kept while the day is the same.
            DueAt = t.DueAt is { } dueAt && DateOnly.FromDateTime(dueAt.LocalDateTime) == d.DueDate ? dueAt : null,
            DoneAt = status == MemberTaskStatus.Done || keepDone ? t.DoneAt ?? d.UpdatedAt : null,
        };
        // Kept done over a newer "open": a change of ours again, stamped after the server's, so it goes up.
        return keepDone ? applied with { UpdatedAt = Stamp(now, d.UpdatedAt) } : applied;
    }

    /// <summary>
    /// The list from the server into this PC's copy. Local rows that are not shared are never touched. A shared row
    /// that belongs to another member (someone else connected this PC under the same Windows user) leaves: it's safe
    /// on the server, or, not sent yet, stays on this PC and is never sent as the new member's.
    ///
    /// A change made here that hasn't gone up yet (<paramref name="waiting"/>: client ids still in the outbox, or not
    /// handed to it) always wins (F-16). The list may be older than it (the service's cached list after a restart), or
    /// carry a later stamp from a PC whose clock runs ahead; the old rule let either turn a ticked to-do back to open.
    /// When the server's copy is stamped at or after ours, ours is stamped again just after it, so the server takes it too.
    /// </summary>
    public static IReadOnlyList<MemberTask> Merge(IReadOnlyList<MemberTask> local, IReadOnlyList<DeviceTodo> remote, long? member, DateTimeOffset now,
        IReadOnlySet<string>? waiting = null)
    {
        var today = DateOnly.FromDateTime(now.LocalDateTime);
        var rows = new List<MemberTask>(local.Count);
        foreach (var t in local)
        {
            if (t.ClientId is null || t.SyncedFor is null || t.SyncedFor == member || member is null)
            {
                rows.Add(t.ClientId is not null && t.SyncedFor is null && member is not null ? t with { SyncedFor = member } : t);
            }
            else if (Dirty(t) && t.DeletedAt is null)
            {
                rows.Add(t with { ClientId = null, UpdatedAt = null, SyncedAt = null, SyncedFor = null, Queued = false });
            }
        }

        var next = local.Count == 0 ? 1 : local.Max(t => t.Id) + 1;
        foreach (var d in remote.Where(d => d.ClientId.Length > 0))
        {
            var i = rows.FindIndex(t => t.ClientId == d.ClientId);
            if (i < 0)
            {
                if (!d.Deleted)
                {
                    rows.Add(Apply(new MemberTask
                    {
                        Id = next++, Mine = true, ClientId = d.ClientId, SentAt = d.CreatedAt ?? d.UpdatedAt,
                    }, d, member, today, now));
                }
                continue;
            }
            var t = rows[i];
            var at = t.UpdatedAt ?? DateTimeOffset.MinValue;
            if (Dirty(t) && (waiting?.Contains(t.ClientId!) ?? false))
            {
                if (d.UpdatedAt >= at && !Same(t, d))
                {
                    rows[i] = t with { UpdatedAt = Stamp(now, d.UpdatedAt) };
                }
                continue;
            }
            if (d.Deleted)
            {
                if (d.UpdatedAt >= at)
                {
                    rows.RemoveAt(i); // deleted on another PC, or our own delete confirmed
                }
            }
            else if (d.UpdatedAt > at)
            {
                rows[i] = Apply(t, d, member, today, now);
            }
            else if (d.UpdatedAt == at && t.DeletedAt is null && t.SyncedAt != at)
            {
                rows[i] = t with { SyncedAt = at, Queued = false };
            }
        }
        return rows;
    }

    /// <summary>
    /// What still has to go up and was not handed to the outbox in this run of the tray (a to-do queued while the
    /// service was away, or dropped when a plan had no tasks). Once per change, so a to-do the backend refuses isn't
    /// sent every few seconds.
    /// </summary>
    public static IReadOnlyList<MemberTask> Unsent(IEnumerable<MemberTask> local, IReadOnlyDictionary<string, DateTimeOffset> handed, long? member) =>
        [.. local.Where(t => Dirty(t) && t.UpdatedAt is not null && (t.SyncedFor is null || t.SyncedFor == member)
            && !(handed.TryGetValue(t.ClientId!, out var at) && at == t.UpdatedAt))];
}

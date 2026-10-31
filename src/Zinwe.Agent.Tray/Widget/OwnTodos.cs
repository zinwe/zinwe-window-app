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
        old.Title != edited.Title || old.DueKey != edited.DueKey || old.DueDate != edited.DueDate
        || !old.Steps.Select(s => s.Label).SequenceEqual(edited.Steps.Select(s => s.Label));

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

    /// <summary>A to-do from the server over what this PC has (<paramref name="t"/> is a new row, or the older local one).</summary>
    private static MemberTask Apply(MemberTask t, DeviceTodo d, long? member, DateOnly today)
    {
        var (due, key) = DueOf(d.DueDate, today);
        var steps = (d.Steps ?? []).Select(s => new TaskStep(s.Title, s.Done)).ToList();
        var status = d.Status == DeviceTodo.Done ? MemberTaskStatus.Done
            : TaskBoard.Finished(t) ? MemberTaskStatus.New
            : t.Status == MemberTaskStatus.New && steps.Any(s => s.Done) ? MemberTaskStatus.Progress
            : t.Status;
        return t with
        {
            Title = d.Title, Due = due, DueKey = key, DueDate = d.DueDate, DueOn = d.DueDate, Steps = steps, Status = status,
            UpdatedAt = d.UpdatedAt, SyncedAt = d.UpdatedAt, SyncedFor = member, DeletedAt = null, Queued = false,
        };
    }

    /// <summary>
    /// The list from the server into this PC's copy. Local rows that are not shared are never touched. A shared row
    /// that belongs to another member (someone else connected this PC under the same Windows user) leaves: it's safe
    /// on the server, or, not sent yet, stays on this PC and is never sent as the new member's.
    /// </summary>
    public static IReadOnlyList<MemberTask> Merge(IReadOnlyList<MemberTask> local, IReadOnlyList<DeviceTodo> remote, long? member, DateTimeOffset now)
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
                    }, d, member, today));
                }
                continue;
            }
            var t = rows[i];
            var at = t.UpdatedAt ?? DateTimeOffset.MinValue;
            if (d.Deleted)
            {
                if (d.UpdatedAt >= at)
                {
                    rows.RemoveAt(i); // deleted on another PC, or our own delete confirmed
                }
            }
            else if (d.UpdatedAt > at)
            {
                rows[i] = Apply(t, d, member, today);
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

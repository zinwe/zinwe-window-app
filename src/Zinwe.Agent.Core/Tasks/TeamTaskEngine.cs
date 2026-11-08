using System.Globalization;
using Zinwe.Agent.Core.Backend;

namespace Zinwe.Agent.Core.Tasks;

/// <summary>The colour a task's sentence, card edge and What's happening card take (the prototype's data-tone).</summary>
public enum TaskTone { Neutral, Needs, Work, Red, Ok, Grey }

/// <summary>The member's two lists and Done: a button for you (or your own open to-do), waiting on someone else, finished.</summary>
public enum TaskBucket { Needs, Wait, Done }

/// <summary>One piece of the journey bar (Sent · Seen · Working · Done · OK).</summary>
public enum JourneyState { Past, Now, Next, Ok, Red }

/// <summary>
/// What the engine needs besides the task: whose side (<paramref name="MeId"/>; null = work it out from the task), the
/// owner's first name mid-sentence (<paramref name="Boss"/>, "your admin" when unknown) and at a sentence start
/// (<paramref name="BossStart"/>), the clock, the group the task was posted in, and whether it is the member's own to-do.
/// </summary>
public sealed record TaskContext(long? MeId, string Boss, string BossStart, DateTimeOffset Now, string? GroupName = null, bool Own = false);

/// <param name="Act">"start", "finish" or "tick:&lt;step index&gt;".</param>
public sealed record TaskButton(string Label, string Act, bool Primary);

/// <summary>The heart of the panel: one sentence, a short form for cards, extra lines, and at most two buttons.</summary>
public sealed record Happening(TaskTone Tone, string Text, string Short, IReadOnlyList<string> Sub, IReadOnlyList<TaskButton> Buttons);

public sealed record TaskPill(string Label, TaskTone Tone);

public sealed record JourneyStep(string Label, string Time, JourneyState State);

/// <param name="Kind">"" · "today" (due today) · "over" (late): the due pill's colour.</param>
public sealed record TaskDue(string Chip, string Short, string Kind, bool Late);

/// <summary>A person on the task as the member sees them: "You" first-person, their word ("Your turn", "Done") and steps.</summary>
public sealed record PersonRow(long? MemberId, string Name, string Initials, bool IsMe, string Word, TaskTone Tone, int Done, int Total);

/// <param name="State">"done" · "now" · "blocked" · "todo".</param>
/// <param name="Lock">Why the member can't tick it ("Only Karan can tick this step"), or "".</param>
/// <param name="By">Who ticked it and when ("Karan · 1:05 PM"), or "".</param>
public sealed record StepRow(int Index, long Id, string Title, bool Done, string State, bool CanTick, string Lock, string OwnerName,
    string OwnerInitials, long? OwnerId, bool OwnerIsMe, string By);

/// <summary>
/// v1.4 · the member side of the team task (prototype engine_win.js over panel.js: tvHappenMember, tvPill, tvJourney,
/// tvPersonState, tvSteps, tvDue). Pure, so every wording is unit-tested. Works on a 1.3 task too: no people = the
/// member alone, no step owners = anyone (so every step is theirs).
/// </summary>
public static class TeamTaskEngine
{
    // ---- Reading the task -------------------------------------------------------------------------------

    /// <summary>The status in the engine's words: "accepted" (1.3's confirm) reads as seen, "canceled" as cancelled.</summary>
    public static string StatusOf(DeviceTask t) => (t.Status ?? "").ToLowerInvariant() switch
    {
        "accepted" => "seen",
        "canceled" => "cancelled",
        "" => "sent",
        var s => s,
    };

    public static bool IsFinished(DeviceTask t) => StatusOf(t) is "approved" or "cancelled";

    public static bool IsAny(DeviceTask t) => string.Equals(t.Mode, "any", StringComparison.OrdinalIgnoreCase);

    public static bool InOrder(DeviceTask t) => t.InOrder == true;

    /// <summary>True for a v1.4 task (it lists its people); a 1.3 task takes the 1.3 actions.</summary>
    public static bool IsTeamShape(DeviceTask t) => t.People is not null;

    /// <summary>Everyone on the task. A 1.3 task: just this member.</summary>
    public static IReadOnlyList<DeviceTaskPerson> People(DeviceTask t, TaskContext c) =>
        t.People is { Count: > 0 } people ? people : [new DeviceTaskPerson(c.MeId ?? t.Me?.MemberId ?? t.Member ?? 0, "You", t.Me?.State)];

    /// <summary>The member's own id on this task: given, from the task's <c>me</c>, or the only person on it.</summary>
    public static long? Me(DeviceTask t, TaskContext c) =>
        c.MeId ?? t.Me?.MemberId ?? (t.People is { Count: 1 } one ? one[0].MemberId : t.People is null ? t.Member : null);

    public static bool Team(DeviceTask t, TaskContext c) => People(t, c).Count > 1;

    /// <summary>A step is the member's: nobody's in particular, theirs, or (their id unknown) one the backend lets them tick.</summary>
    public static bool IsMine(DeviceTask t, DeviceTaskStep s, TaskContext c)
    {
        if (s.OwnerId is not { } owner || t.People is null)
        {
            return true;
        }
        return Me(t, c) is { } me ? owner == me : t.Me?.CanTick?.Contains(s.Id) == true;
    }

    public static (int N, int Done, int Cur) Steps(DeviceTask t)
    {
        var st = t.Steps ?? [];
        var cur = -1;
        for (var i = 0; i < st.Count; i++)
        {
            if (!st[i].Done)
            {
                cur = i;
                break;
            }
        }
        return (st.Count, st.Count(s => s.Done), cur);
    }

    private static string First(string? name) => string.IsNullOrWhiteSpace(name) ? "" : name.Trim().Split(' ')[0];

    public static string Initials(string? name)
    {
        var parts = (name ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length switch
        {
            0 => "",
            1 => parts[0][..Math.Min(2, parts[0].Length)].ToUpperInvariant(),
            _ => (parts[0][..1] + parts[^1][..1]).ToUpperInvariant(),
        };
    }

    private static string NameOf(DeviceTask t, long? id, TaskContext c) =>
        id is null ? "" : People(t, c).FirstOrDefault(p => p.MemberId == id)?.Name ?? "";

    private static string FirstOf(DeviceTask t, long? id, TaskContext c) =>
        id is { } x && Me(t, c) == x ? "You" : First(NameOf(t, id, c)) is { Length: > 0 } f ? f : "a teammate";

    /// <summary>"Priya", "Priya and Emily", "Priya, Emily and Karan".</summary>
    public static string List(IEnumerable<string> names)
    {
        var l = names.Where(n => !string.IsNullOrEmpty(n)).ToList();
        return l.Count <= 1 ? l.FirstOrDefault() ?? "" : string.Join(", ", l.Take(l.Count - 1)) + " and " + l[^1];
    }

    private static string Quote(string? s) => "“" + (s ?? "").Trim() + "”";

    /// <summary>The person who asked for help / said they can't (their state), else the member.</summary>
    private static long? Who(DeviceTask t, TaskContext c, string state) =>
        t.People?.FirstOrDefault(p => string.Equals(p.State, state, StringComparison.OrdinalIgnoreCase))?.MemberId ?? Me(t, c);

    /// <summary>
    /// The owner sent it back and the member hasn't picked it up again yet: a reopen note, in progress, and no start
    /// or tick from the member since the owner's reopen.
    /// </summary>
    public static bool Reopened(DeviceTask t, TaskContext c)
    {
        if (c.Own || string.IsNullOrWhiteSpace(t.ReopenNote) || StatusOf(t) != "progress")
        {
            return false;
        }
        var acts = t.Activity ?? [];
        var reopen = acts.Where(a => !IsMember(a.By) && (a.Action ?? "").ToLowerInvariant() is "reopen" or "reopened")
            .Select(a => (DateTimeOffset?)a.At).Max();
        if (reopen is not { } at)
        {
            return !acts.Any(a => IsMember(a.By) && IsPickUp(a.Action) && a.At >= (t.StatusChangedAt ?? DateTimeOffset.MaxValue));
        }
        return !acts.Any(a => IsMember(a.By) && IsPickUp(a.Action) && a.At > at);
    }

    private static bool IsMember(string? by) => string.Equals(by, "member", StringComparison.OrdinalIgnoreCase);

    private static bool IsPickUp(string? action) => (action ?? "").ToLowerInvariant() is
        "start" or "started" or "tick" or "ticked" or "step_done" or "finish" or "finished" or "done" or "progress";

    // ---- Due --------------------------------------------------------------------------------------------

    /// <summary>The due day (from today: 0 = today) and time ("" when the due is a date only), or null without one.</summary>
    public static (int Day, string Time, DateTimeOffset At)? When(DeviceTask t, DateTimeOffset now)
    {
        if (t.DueAt is not { } at)
        {
            return null;
        }
        var local = at.ToLocalTime();
        var dateOnly = local.TimeOfDay == TimeSpan.Zero || local.TimeOfDay >= new TimeSpan(23, 59, 0);
        var d = DateOnly.FromDateTime(local.DateTime).DayNumber - DateOnly.FromDateTime(now.ToLocalTime().DateTime).DayNumber;
        return (d, dateOnly ? "" : Clock(local), at);
    }

    public static string Clock(DateTimeOffset at) => at.ToLocalTime().ToString("h:mm tt", CultureInfo.InvariantCulture);

    public static string DayLabel(DateTimeOffset at) => at.ToLocalTime().ToString("ddd d MMM", CultureInfo.InvariantCulture);

    /// <summary>"Due today 6:00 PM · in 3 h", "Tomorrow 6:00 PM", "Mon 9 Nov, 12:00 PM", "1 day late" (tvDue).</summary>
    public static TaskDue Due(DeviceTask t, TaskContext c)
    {
        if (When(t, c.Now) is not { } w)
        {
            return new TaskDue("", "", "", false);
        }
        var (d, time, at) = w;
        var label = DayLabel(at);
        var word = d switch { 0 => "Today", 1 => "Tomorrow", -1 => "Yesterday", _ => label };
        var rel = d switch { 0 => "today", 1 => "tomorrow", -1 => "yesterday", _ => label };
        var status = StatusOf(t);
        var open = !IsFinished(t) && status != "review" && !(c.Own && status == "approved");
        var shortText = d is 0 or 1 ? word + (time.Length > 0 ? " " + time : "") : label + (time.Length > 0 ? ", " + time : "");
        var chip = "Due " + rel + (time.Length > 0 ? (d is 0 or 1 ? " " : ", ") + time : "");
        if (open && d < 0)
        {
            var late = d == -1 ? "1 day late" : -d + " days late";
            return new TaskDue(late, late, "over", true);
        }
        if (open && d == 0 && time.Length > 0)
        {
            var left = (int)Math.Ceiling((at - c.Now).TotalMinutes);
            if (left <= 0)
            {
                var was = "Was due " + time;
                return new TaskDue(was, was, "over", true);
            }
            chip += " · in " + (left >= 60 ? (int)Math.Round(left / 60.0) + " h" : left + " min");
        }
        return new TaskDue(chip, shortText, open && d == 0 ? "today" : "", false);
    }

    /// <summary>Day number for the filters: no due date counts as today (it needs a look now, as on the prototype).</summary>
    public static int DayOf(DeviceTask t, DateTimeOffset now) => When(t, now)?.Day ?? 0;

    // ---- The pill, the journey, the people ----------------------------------------------------------------

    /// <summary>The status pill, member side (engine_win.js tvPill).</summary>
    public static TaskPill Pill(DeviceTask t, TaskContext c)
    {
        var s = StatusOf(t);
        if (c.Own)
        {
            return s == "approved" ? new("Done", TaskTone.Ok) : new("To-do", TaskTone.Grey);
        }
        if (Reopened(t, c))
        {
            return new("Reopened", TaskTone.Red);
        }
        return s switch
        {
            "sent" or "seen" => new("New", TaskTone.Needs),
            "review" => new("Waiting for OK", TaskTone.Ok),
            "help" => new("Asked for help", TaskTone.Red),
            "progress" => new("In progress", TaskTone.Work),
            "cantdo" => new("Can’t do it", TaskTone.Red),
            "approved" => new("Approved", TaskTone.Ok),
            "cancelled" => new("Cancelled", TaskTone.Grey),
            _ => new("New", TaskTone.Needs),
        };
    }

    /// <summary>One person's state as the panel words it (tvPersonState), with "You" for the member.</summary>
    public static PersonRow Person(DeviceTask t, DeviceTaskPerson p, TaskContext c)
    {
        var st = t.Steps ?? [];
        var me = Me(t, c);
        var isMe = me is { } m && m == p.MemberId || t.People is null;
        var mine = st.Where(s => t.People is null ? true : s.OwnerId == p.MemberId).ToList();
        var md = mine.Count(s => s.Done);
        var status = StatusOf(t);
        var info = Steps(t);
        (string Word, TaskTone Tone) w;
        var server = (p.State ?? "").ToLowerInvariant();
        if (status == "approved")
        {
            w = ("Approved", TaskTone.Ok);
        }
        else if (status == "cancelled")
        {
            w = ("Cancelled", TaskTone.Grey);
        }
        else if (status == "help" && Who(t, c, "help") == p.MemberId || server == "help")
        {
            w = ("Needs help", TaskTone.Red);
        }
        else if (status == "cantdo" && Who(t, c, "cantdo") == p.MemberId || server == "cantdo")
        {
            w = ("Can’t do it", TaskTone.Red);
        }
        else if (status == "review" || (mine.Count > 0 && md == mine.Count) || server == "done")
        {
            w = ("Done", TaskTone.Ok);
        }
        else if (status == "sent" || server == "sent")
        {
            w = ("Not seen", TaskTone.Neutral);
        }
        else if (InOrder(t) && info.Cur >= 0 && (st[info.Cur].OwnerId == p.MemberId || FirstOpen(st, p.MemberId) > info.Cur))
        {
            var ow = st[info.Cur].OwnerId;
            w = ow == p.MemberId ? ("Their turn", TaskTone.Work)
                : ("Waits for " + (ow is null ? "step " + (info.Cur + 1) : FirstOf(t, ow, c)), TaskTone.Neutral);
        }
        else if (md > 0 || status == "progress" || server == "progress")
        {
            w = ("On it", TaskTone.Work);
        }
        else
        {
            w = ("Seen", TaskTone.Neutral);
        }
        if (isMe)
        {
            w = w.Word switch { "Their turn" => ("Your turn", w.Tone), "Not seen" => ("New", w.Tone), _ => w };
        }
        var name = isMe ? "You" : p.Name;
        var total = t.People is null ? 0 : mine.Count;
        return new PersonRow(p.MemberId, name, Initials(isMe && t.People is null ? "" : p.Name), isMe, w.Word, w.Tone,
            t.People is null ? 0 : md, total);
    }

    private static int FirstOpen(IReadOnlyList<DeviceTaskStep> st, long member)
    {
        for (var i = 0; i < st.Count; i++)
        {
            if (st[i].OwnerId == member && !st[i].Done)
            {
                return i;
            }
        }
        return -1;
    }

    public static IReadOnlyList<PersonRow> PeopleRows(DeviceTask t, TaskContext c) => [.. People(t, c).Select(p => Person(t, p, c))];

    /// <summary>Sent · Seen n/N · Working · Done n/N · OK, with the times they happened (tvJourney, Windows labels).</summary>
    public static IReadOnlyList<JourneyStep> Journey(DeviceTask t, TaskContext c)
    {
        var status = StatusOf(t);
        var people = People(t, c);
        var team = people.Count > 1;
        var acts = t.Activity ?? [];
        var idx = status switch
        {
            "sent" => 0,
            "seen" => 1,
            "progress" or "help" or "cantdo" => 2,
            "review" => 3,
            "approved" => 4,
            _ => (t.Steps ?? []).Any(s => s.Done) ? 2 : 1,
        };
        var seenN = status == "sent" ? 0
            : t.People is { Count: > 0 } ps && ps.Any(p => p.SeenAt is not null || (p.State is { } s && s != "sent"))
                ? ps.Count(p => p.SeenAt is not null || (p.State is { } s2 && s2 != "sent"))
                : people.Count;
        var doneN = status is "approved" or "review" ? people.Count : people.Count(p => Person(t, p, c).Word == "Done");
        DateTimeOffset? Act(params string[] names) => acts.Where(a => names.Contains((a.Action ?? "").ToLowerInvariant()))
            .Select(a => (DateTimeOffset?)a.At).Min();
        DateTimeOffset? Last(params string[] names) => acts.Where(a => names.Contains((a.Action ?? "").ToLowerInvariant()))
            .Select(a => (DateTimeOffset?)a.At).Max();
        var times = new DateTimeOffset?[]
        {
            t.CreatedAt ?? Act("created", "create", "sent"),
            t.People?.Select(p => p.SeenAt).Where(x => x is not null).Min() ?? Act("seen"),
            Act("start", "started", "progress", "step_done", "tick", "ticked"),
            Last("done", "review", "finish", "finished") ?? t.People?.Select(p => p.DoneAt).Where(x => x is not null).Max(),
            Last("approve", "approved"),
        };
        var labels = new[] { "Sent", team ? $"Seen {seenN}/{people.Count}" : "Seen", "Working", team ? $"Done {doneN}/{people.Count}" : "Done", "OK" };
        return [.. labels.Select((l, i) =>
        {
            var st = i < idx ? JourneyState.Past : i == idx ? JourneyState.Now : JourneyState.Next;
            if (status == "approved")
            {
                st = JourneyState.Ok;
            }
            if (i == 2 && status is "help" or "cantdo")
            {
                st = JourneyState.Red;
            }
            var time = i <= idx && times[i] is { } at ? Clock(at) : "";
            return new JourneyStep(l, time, st);
        })];
    }

    // ---- What's happening (member side) ----------------------------------------------------------------

    /// <summary>The sentence and its buttons (tvHappenMember with the Windows overrides).</summary>
    public static Happening Happen(DeviceTask t, TaskContext c)
    {
        var sub = new List<string>();
        var btns = new List<TaskButton>();
        if (!string.IsNullOrWhiteSpace(t.Repeat))
        {
            sub.Add("Repeats every " + t.Repeat + (string.IsNullOrWhiteSpace(t.NextOn) ? "" : " · next on " + NextOn(t.NextOn)));
        }
        var status = StatusOf(t);
        var info = Steps(t);
        var due = Due(t, c);
        var me = Me(t, c);
        var team = Team(t, c);
        var st = t.Steps ?? [];
        Happening H(TaskTone tone, string text, string shortText) => new(tone, text, shortText, sub, btns);

        if (status == "cancelled")
        {
            return H(TaskTone.Grey, c.BossStart + " cancelled this task.", "Cancelled");
        }
        if (status == "approved")
        {
            return c.Own ? H(TaskTone.Ok, "Done. Nice work.", "Done") : H(TaskTone.Ok, "Approved by " + c.Boss + ". Nice work.", "Approved");
        }
        if (status == "review")
        {
            return H(TaskTone.Ok, "You’re done. Waiting for " + c.Boss + "’s OK.", "Waiting for " + c.Boss + "’s OK");
        }
        if (Reopened(t, c))
        {
            sub.Clear();
            btns.Add(new("Start again", "start", true));
            return H(TaskTone.Red, c.BossStart + " reopened it: " + Quote(t.ReopenNote), "Reopened");
        }
        if (status == "help")
        {
            var who = Who(t, c, "help");
            var note = string.IsNullOrWhiteSpace(t.HelpNote) ? "" : ": " + Quote(t.HelpNote);
            return who is { } w && w != me && me is not null
                ? H(TaskTone.Red, FirstOf(t, w, c) + " asked " + c.Boss + " for help" + note, FirstOf(t, w, c) + " asked for help")
                : H(TaskTone.Red, "You asked " + c.Boss + " for help" + note, "Waiting for help");
        }
        if (status == "cantdo")
        {
            var who = Who(t, c, "cantdo");
            var note = string.IsNullOrWhiteSpace(t.CantdoNote) ? "." : ": " + Quote(t.CantdoNote);
            return who is { } w && w != me && me is not null
                ? H(TaskTone.Red, FirstOf(t, w, c) + " can’t do it" + note, FirstOf(t, w, c) + " can’t do it")
                : H(TaskTone.Red, "You told " + c.Boss + " you can’t do it" + note, "You can’t do it");
        }
        if (status is "sent" or "seen")
        {
            btns.Add(new("Start", "start", true));
            if (IsAny(t) && team)
            {
                sub.Add("Sent to " + (c.GroupName ?? "your team") + ". The first of you to start takes it.");
            }
            return H(TaskTone.Needs, "New from " + c.Boss + "." + (due.Chip.Length > 0 ? " " + due.Chip + "." : ""),
                "New" + (due.Short.Length > 0 ? " · " + due.Short : ""));
        }
        // In progress
        if (InOrder(t) && info.Cur >= 0)
        {
            var ow = st[info.Cur].OwnerId;
            if (IsMine(t, st[info.Cur], c))
            {
                btns.Add(new("Tick step " + (info.Cur + 1), "tick:" + info.Cur, true));
                return H(TaskTone.Work, $"Your turn: step {info.Cur + 1} of {info.N}, {st[info.Cur].Title}.", "Your turn · step " + (info.Cur + 1));
            }
            var myLeft = st.Any(s => !s.Done && s.OwnerId is not null && IsMine(t, s, c));
            if (!myLeft)
            {
                return H(TaskTone.Ok, "Your part is done. " + FirstOf(t, ow, c) + " is next.", "Your part is done");
            }
            return H(TaskTone.Neutral, $"Waits for {FirstOf(t, ow, c)}: step {info.Cur + 1} ({st[info.Cur].Title}).", "Waits for " + FirstOf(t, ow, c));
        }
        var next = -1;
        for (var i = 0; i < st.Count; i++)
        {
            if (!st[i].Done && IsMine(t, st[i], c))
            {
                next = i;
                break;
            }
        }
        if (next >= 0)
        {
            btns.Add(new("Tick step " + (next + 1), "tick:" + next, true));
            return H(TaskTone.Work, $"You’re on it: {info.Done} of {info.N} steps done.", $"On it · {info.Done} of {info.N}");
        }
        var others = People(t, c).Where(p => p.MemberId != me && Person(t, p, c).Word != "Done").Select(p => First(p.Name)).ToList();
        var mineDone = me is { } id && t.People?.FirstOrDefault(p => p.MemberId == id) is { } mp
            && (string.Equals(mp.State, "done", StringComparison.OrdinalIgnoreCase) || mp.DoneAt is not null);
        if (team && (info.N > 0 || mineDone))
        {
            return H(TaskTone.Ok, "Your part is done." + (others.Count > 0 ? " Waiting for " + List(others) + "." : ""), "Your part is done");
        }
        btns.Add(new("Mark it done", "finish", true));
        return H(TaskTone.Work, "You’re on it.", "On it");
    }

    private static string NextOn(string nextOn) =>
        DateOnly.TryParse(nextOn, CultureInfo.InvariantCulture, out var d) ? d.ToString("ddd d MMM", CultureInfo.InvariantCulture) : nextOn;

    /// <summary>Which of the member's lists the task is in (tvBucket).</summary>
    public static TaskBucket Bucket(DeviceTask t, TaskContext c) =>
        IsFinished(t) ? TaskBucket.Done
        : c.Own ? TaskBucket.Needs
        : Happen(t, c).Buttons.Count > 0 ? TaskBucket.Needs
        : TaskBucket.Wait;

    /// <summary>The card's one line (tvCard): "New from Ananya", "Sent back: “…”", "1 of 2 steps done", else the short sentence.</summary>
    public static string CardLine(DeviceTask t, TaskContext c)
    {
        var status = StatusOf(t);
        if (IsAny(t) && status is "sent" or "seen" && Team(t, c))
        {
            return "New for " + (c.GroupName ?? "your team") + " · the first to start takes it";
        }
        if (!c.Own && status is "sent" or "seen")
        {
            return "New from " + c.Boss;
        }
        if (Reopened(t, c))
        {
            return "Sent back: " + Quote(t.ReopenNote);
        }
        if (c.Own && status != "approved")
        {
            var (n, done, _) = Steps(t);
            return n > 0 ? $"{done} of {n} steps done" : "Your own to-do";
        }
        var h = Happen(t, c);
        return h.Short.Length > 0 ? h.Short : h.Text;
    }

    /// <summary>The card's button: the first of the sentence's.</summary>
    public static TaskButton? Primary(DeviceTask t, TaskContext c) => Happen(t, c).Buttons.FirstOrDefault();

    /// <summary>The steps as the member sees them: only theirs (or anyone's) tickable, in-order steps wait their turn.</summary>
    public static IReadOnlyList<StepRow> StepRows(DeviceTask t, TaskContext c)
    {
        var st = t.Steps ?? [];
        var info = Steps(t);
        var status = StatusOf(t);
        var team = Team(t, c);
        var done = IsFinished(t);
        var me = Me(t, c);
        return [.. st.Select((s, i) =>
        {
            var now = !s.Done && i == info.Cur && status is "progress" or "help" && !done && (InOrder(t) || !team);
            var blocked = InOrder(t) && !s.Done && info.Cur >= 0 && i > info.Cur;
            var mine = IsMine(t, s, c);
            var canTick = mine && !s.Done && !blocked && status is not ("sent" or "seen") && !done;
            var ownerIsMe = s.OwnerId is { } o && (me == o || t.People is null);
            var lockText = s.Done ? ""
                : blocked ? "This step waits for step " + (info.Cur + 1)
                : !mine && s.OwnerId is { } other ? "Only " + FirstOf(t, other, c) + " can tick this step"
                : "";
            var byId = s.DoneById ?? s.OwnerId;
            var by = s.Done ? (byId is { } b && b == me ? "You" : byId is null ? (team ? "Done" : "You") : FirstOf(t, byId, c))
                + (s.DoneAt is { } at ? " · " + Clock(at) : "") : "";
            var ownerName = s.OwnerId is null || t.People is null ? (t.People is null ? "You" : "Anyone") : ownerIsMe ? "You" : NameOf(t, s.OwnerId, c);
            return new StepRow(i, s.Id, s.Title, s.Done, s.Done ? "done" : now ? "now" : blocked ? "blocked" : "todo", canTick, lockText,
                ownerName, s.OwnerId is null || t.People is null ? "" : Initials(NameOf(t, s.OwnerId, c)), s.OwnerId, ownerIsMe, by);
        })];
    }

    /// <summary>"Steps · 1 of 3 done · in order".</summary>
    public static string StepsHead(DeviceTask t)
    {
        var (n, d, _) = Steps(t);
        return n == 0 ? "Steps" : $"Steps · {d} of {n} done" + (InOrder(t) ? " · in order" : "");
    }

    /// <summary>"People · 3", plus "· the first to start takes it" on an any-one task.</summary>
    public static string PeopleHead(DeviceTask t, TaskContext c) =>
        "People · " + People(t, c).Count + (IsAny(t) && Team(t, c) ? " · the first to start takes it" : "");

    /// <summary>What the step lock says when the member taps a step that isn't theirs (the prototype's toast).</summary>
    public static string WhoseStep(StepRow s) =>
        s.OwnerIsMe ? "This step is yours." : s.OwnerId is null ? "Anyone on the task can do this step." : s.OwnerName + " does this step.";

    /// <summary>"From Ananya · Sent Fri 6 Nov, 4:30 PM · in the Design chat".</summary>
    public static string Meta(DeviceTask t, TaskContext c)
    {
        var sent = t.CreatedAt is { } at ? Sent(at, c.Now) : "";
        return "From " + c.Boss + (sent.Length > 0 ? " · Sent " + sent : "") + (c.GroupName is { Length: > 0 } g && t.GroupId is not null ? " · in the " + g + " chat" : "");
    }

    /// <summary>"today, 9:00 AM" · "yesterday, 5:10 PM" · "Fri 6 Nov, 4:30 PM".</summary>
    public static string Sent(DateTimeOffset at, DateTimeOffset now)
    {
        var d = DateOnly.FromDateTime(at.ToLocalTime().DateTime).DayNumber - DateOnly.FromDateTime(now.ToLocalTime().DateTime).DayNumber;
        return (d switch { 0 => "today", -1 => "yesterday", _ => DayLabel(at) }) + ", " + Clock(at);
    }

    /// <summary>Sorting: the due day, then the time (no time = end of the day), then the id.</summary>
    public static double Rank(DeviceTask t, DateTimeOffset now)
    {
        if (When(t, now) is not { } w)
        {
            return 1440;
        }
        var minutes = w.Time.Length > 0 ? w.At.ToLocalTime().TimeOfDay.TotalMinutes : 1440;
        return w.Day * 1441 + minutes;
    }
}

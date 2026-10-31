using System.IO;
using System.Text.Json;

namespace Zinwe.Agent.Tray.Widget;

public enum TimelineKind { In, Break, Out }

/// <summary>One line of "Today so far" / My time › Today.</summary>
public sealed record TimelineEvent(DateTimeOffset At, string Label, TimelineKind Kind)
{
    public string Time => At.ToLocalTime().ToString("h:mm tt");
}

/// <summary>Hours worked on one day, for the "Last 5 working days" bars.</summary>
public sealed record DayHours(string Day, double Hours, bool IsToday);

/// <summary>
/// Reads the member's own work-timing log (work-timing-log.jsonl, one line per Start / break / back / End that the
/// widget writes and the agent sends) for the Home timeline and My time. Real data: nothing here is a sample.
/// </summary>
public static class DayLog
{
    public sealed record Entry(DateTimeOffset At, string Action, string State, long WorkedSeconds, long BreakSeconds,
        string? BreakReason = null, string? BreakNote = null, string? EndReason = null, string? EndNote = null, string? Host = null);

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    public static IReadOnlyList<Entry> Read(string? path = null)
    {
        var file = path ?? WidgetPaths.WorkTimingLog;
        var list = new List<Entry>();
        try
        {
            if (!File.Exists(file))
            {
                return list;
            }
            foreach (var line in File.ReadLines(file))
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }
                try
                {
                    if (JsonSerializer.Deserialize<Entry>(line, Json) is { } e)
                    {
                        list.Add(e);
                    }
                }
                catch (JsonException)
                {
                    // A half-written line: skip it.
                }
            }
        }
        catch
        {
            // Unreadable: an empty timeline.
        }
        return list;
    }

    /// <summary>Today's lines, oldest first, in the board's words.</summary>
    public static IReadOnlyList<TimelineEvent> Today(IEnumerable<Entry> log, DateTimeOffset now)
    {
        var day = now.ToLocalTime().Date;
        var events = new List<TimelineEvent>();
        var started = false;
        string? breakReason = null;
        DateTimeOffset? breakAt = null;
        foreach (var e in log.Where(e => e.At.ToLocalTime().Date == day).OrderBy(e => e.At))
        {
            switch (e.Action)
            {
                case "start":
                    events.Add(new TimelineEvent(e.At, started ? "Started again" : "Started day", TimelineKind.In));
                    started = true;
                    break;
                case "break":
                    breakReason = e.BreakReason;
                    breakAt = e.At;
                    events.Add(new TimelineEvent(e.At, "Break · " + (breakReason ?? "Break"), TimelineKind.Break));
                    break;
                case "resume":
                    var mins = breakAt is { } b ? Math.Max(1, (int)Math.Round((e.At - b).TotalMinutes)) : 0;
                    events.Add(new TimelineEvent(e.At, $"Back from break · {breakReason ?? "Break"} · {mins} min", TimelineKind.In));
                    breakReason = null;
                    breakAt = null;
                    break;
                case "end":
                    events.Add(new TimelineEvent(e.At, "Ended day" + (e.EndReason is { Length: > 0 } r ? " · " + r : ""), TimelineKind.Out));
                    break;
                case "moved":
                    events.Add(new TimelineEvent(e.At, "Moved to " + (e.Host ?? "another PC"), TimelineKind.Out));
                    break;
                case "carried":
                    var so = HoursMinutes(TimeSpan.FromSeconds(e.WorkedSeconds));
                    events.Add(new TimelineEvent(e.At, e.Host is { Length: > 0 } from ? $"Carried on from {from} · {so} so far" : $"Carried on · {so} so far", TimelineKind.In));
                    break;
            }
        }
        return events;
    }

    /// <summary>The day's breaks, wherever they were taken: those carried here, plus the ones since.</summary>
    public static int BreaksToday(IEnumerable<TimelineEvent> today, WorkTimerState timer) =>
        timer.CarriedBreaks + today.Count(e => e.Kind == TimelineKind.Break && !(e.At < timer.CarriedAtUtc));

    /// <summary>When the day first started, wherever that was.</summary>
    public static DateTimeOffset? FirstStart(IEnumerable<TimelineEvent> today, WorkTimerState timer) =>
        timer.FirstStartUtc ?? today.FirstOrDefault(e => e.Kind == TimelineKind.In)?.At;

    /// <summary>
    /// The last five working days (days with worked time), oldest first; today last with <paramref name="today"/>
    /// (the live total) even before any work.
    /// </summary>
    public static IReadOnlyList<DayHours> LastWorkingDays(IEnumerable<Entry> log, DateTimeOffset now, TimeSpan today, int days = 5)
    {
        var todayDate = now.ToLocalTime().Date;
        var earlier = log
            .GroupBy(e => e.At.ToLocalTime().Date)
            .Where(g => g.Key < todayDate)
            .Select(g => (Day: g.Key, Seconds: g.Max(e => e.WorkedSeconds)))
            .Where(d => d.Seconds > 0)
            .OrderByDescending(d => d.Day)
            .Take(days - 1)
            .OrderBy(d => d.Day)
            .Select(d => new DayHours(d.Day.ToString("ddd"), d.Seconds / 3600.0, false))
            .ToList();
        earlier.Add(new DayHours("Today", today.TotalHours, true));
        return earlier;
    }

    /// <summary>"4 h 2 min" / "35 min".</summary>
    public static string HoursMinutes(TimeSpan t)
    {
        var h = (int)t.TotalHours;
        return h > 0 ? $"{h} h {t.Minutes} min" : $"{(int)t.TotalMinutes} min";
    }

    /// <summary>"4:02": hours and minutes only (no ticking seconds in the corner of the eye).</summary>
    public static string Hm(TimeSpan t) => $"{(int)t.TotalHours}:{t.Minutes:D2}";
}

/// <summary>A gentle reminder (a toast while Zinwe is minimized, always a line in the history).</summary>
public sealed record Reminder(string Key, string Title, string Body, string Action, string Go);

/// <summary>
/// The three things people forget (start the day, a break that ran long, ending the day) plus due-soon tasks.
/// Only about the clock, never about PC use; work-day reminders only after the member agreed. Pure.
/// </summary>
public static class Reminders
{
    public static IReadOnlyList<Reminder> Due(
        TraySettings settings, bool consentAgreed, bool workHoursOn, WorkTimerState timer, IEnumerable<MemberTask> tasks,
        DateTimeOffset now, TimeSpan trayRunning)
    {
        var list = new List<Reminder>();
        var local = now.ToLocalTime();
        var shown = settings.RemindedDay == local.ToString("yyyy-MM-dd") ? settings.Reminded : [];
        if (settings.DayRemind && consentAgreed && workHoursOn)
        {
            if (timer.State == WorkState.NotStarted && trayRunning >= TimeSpan.FromMinutes(10)
                && local.Hour is >= 8 and < 17 && !shown.Contains("start"))
            {
                list.Add(new Reminder("start", $"It’s {local:h:mm}. Press Start day when you begin.",
                    "You can turn work-day reminders off in Settings.", "Start day", "startday"));
            }
            if (timer is { State: WorkState.OnBreak, LastTransitionAtUtc: { } since } && now - since >= TimeSpan.FromMinutes(45))
            {
                var key = "brk" + since.ToUnixTimeSeconds();
                if (!shown.Contains(key))
                {
                    list.Add(new Reminder(key, "Still on your break?",
                        $"{timer.BreakReason ?? "Break"} · {(int)Math.Round((now - since).TotalMinutes)} min so far", "End break", "endbreak"));
                }
            }
            if (timer.State == WorkState.Working && local.Hour >= 19 && !shown.Contains("late"))
            {
                list.Add(new Reminder("late", "Still working?", "It’s after 7 PM. Press End day when you finish.", "End day", "endday"));
            }
        }
        if (settings.DueRemind)
        {
            foreach (var t in tasks)
            {
                if (t.DueAt is { } at && !TaskBoard.Finished(t) && t.Status != MemberTaskStatus.Review
                    && at > now && at - now <= TimeSpan.FromMinutes(30) && !settings.Nudged.Contains(t.Id))
                {
                    list.Add(new Reminder("due" + t.Id, $"Due in {(int)Math.Ceiling((at - now).TotalMinutes)} min", t.Title, "Open", "task:" + t.Id));
                }
            }
        }
        return list;
    }

    /// <summary>Settings after <paramref name="shown"/> went out (so each goes once).</summary>
    public static TraySettings Remember(TraySettings settings, IEnumerable<Reminder> shown, DateTimeOffset now)
    {
        var day = now.ToLocalTime().ToString("yyyy-MM-dd");
        var keys = settings.RemindedDay == day ? settings.Reminded.ToList() : [];
        var nudged = settings.Nudged.ToList();
        foreach (var r in shown)
        {
            if (r.Key.StartsWith("due", StringComparison.Ordinal) && int.TryParse(r.Key[3..], out var id))
            {
                nudged.Add(id);
            }
            else
            {
                keys.Add(r.Key);
            }
        }
        return settings with { RemindedDay = day, Reminded = keys, Nudged = nudged };
    }
}

namespace Zinwe.Agent.Tray.Widget;

/// <summary>
/// The one input view behind "Take a break" and "Log out": a title, quick reason chips and a note. Both actions
/// share the same rules so the employee learns them once:
/// <list type="bullet">
/// <item>a reason is required (one tap),</item>
/// <item>"Other" also needs a few words, because a bare "Other" tells the admin nothing.</item>
/// </list>
/// The admin sees the reason and note on the dashboard's Activity page.
/// </summary>
public sealed class WorkPrompt
{
    public const string Other = "Other";

    // Tray v2 (prototype board): the bottom sheet behind Take a break / End day.
    public static readonly WorkPrompt Break = new(
        title: "Taking a break",
        reasons: ["Lunch", "Meeting", "Personal", Other],
        confirm: "Start break",
        note: "Back by 2 (optional)",
        otherNote: "What's the break for?");

    public static readonly WorkPrompt LogOut = new(
        title: "Ending your day",
        reasons: ["Done for today", "Leaving early", "Half day", "Not feeling well", Other],
        confirm: "End day",
        note: "What I worked on today (optional)",
        otherNote: "Why are you ending your day?");

    /// <summary>
    /// The reason the sheet picks for the member (they can change it): Lunch around midday when there was no lunch
    /// break yet, "Done for today" from 5 PM.
    /// </summary>
    public string? Likely(DateTimeOffset now, bool hadLunch = false)
    {
        var local = now.ToLocalTime().TimeOfDay;
        if (this == Break)
        {
            return local >= TimeSpan.FromHours(12) && local < TimeSpan.FromHours(14.5) && !hadLunch ? "Lunch" : null;
        }
        return local >= TimeSpan.FromHours(17) ? "Done for today" : null;
    }

    /// <summary>The line under the sheet's title.</summary>
    public string Hint(int openTasks) => this == LogOut && openTasks > 0
        ? $"Pick a reason. Your {openTasks} open tasks stay in Tasks for tomorrow."
        : "Pick a reason.";

    private readonly string _note;
    private readonly string _otherNote;

    private WorkPrompt(string title, IReadOnlyList<string> reasons, string confirm, string note, string otherNote)
    {
        Title = title;
        Reasons = reasons;
        ConfirmLabel = confirm;
        _note = note;
        _otherNote = otherNote;
    }

    public string Title { get; }

    public string Subtitle => "Pick a reason. Your admin sees it in Activity.";

    public IReadOnlyList<string> Reasons { get; }

    public string ConfirmLabel { get; }

    public bool NoteRequired(string? reason) => reason == Other;

    public string NotePlaceholder(string? reason) => NoteRequired(reason) ? $"{_otherNote} (required)" : _note;

    /// <summary>Null when the employee can confirm; otherwise what's still missing, in plain words.</summary>
    public string? Missing(string? reason, string? note) =>
        string.IsNullOrWhiteSpace(reason) ? "Pick a reason to continue."
        : NoteRequired(reason) && string.IsNullOrWhiteSpace(note) ? "Add a few words for your admin."
        : null;
}

/// <summary>What the work card says about the day, from the timer state. Pure, so it's unit-tested.</summary>
public static class WorkSummary
{
    /// <summary>"Good morning" / "Good afternoon" / "Good evening" for the local hour.</summary>
    public static string Greeting(DateTimeOffset now) => now.ToLocalTime().Hour switch
    {
        < 12 => "Good morning",
        < 17 => "Good afternoon",
        _ => "Good evening",
    };

    /// <summary>
    /// The big number: whatever is running right now. On a break that's the current break (so the employee sees
    /// it count); otherwise the time worked today.
    /// </summary>
    public static TimeSpan Hero(WorkTimerState t, DateTimeOffset now) =>
        t is { State: WorkState.OnBreak, LastTransitionAtUtc: { } since } && now > since ? now - since
        : t.State == WorkState.OnBreak ? TimeSpan.Zero
        : t.LiveWorked(now);

    /// <summary>
    /// The line under the big number, naming it: "Worked today · logged in since 9:02 AM",
    /// "Lunch break · since 1:05 PM", "Worked today · logged out at 6:10 PM".
    /// </summary>
    public static string Status(WorkTimerState t)
    {
        var since = t.LastTransitionAtUtc?.ToLocalTime().ToString("h:mm tt");
        var first = t.FirstStartUtc?.ToLocalTime().ToString("h:mm tt") ?? since; // a day that moved here began elsewhere
        return t.State switch
        {
            WorkState.Working => first is null ? "Worked today" : $"Worked today · logged in since {first}",
            WorkState.Moved => MovedLine(t),
            WorkState.OnBreak => $"{BreakName(t.BreakReason)}{(since is null ? "" : $" · since {since}")}",
            WorkState.Ended => since is null ? "Worked today · logged out" : $"Worked today · logged out at {since}",
            _ => "Not logged in yet today",
        };
    }

    /// <summary>The second, smaller line: the other total ("Worked today 3:12:40" on a break, else "Breaks today …").</summary>
    public static string? Secondary(WorkTimerState t, DateTimeOffset now)
    {
        if (t.State == WorkState.OnBreak)
        {
            return $"Worked today {Clock(t.LiveWorked(now))}";
        }
        var breaks = t.LiveBreak(now);
        return breaks > TimeSpan.Zero ? $"Breaks today {Clock(breaks)}" : null;
    }

    public static string Clock(TimeSpan t) => $"{(int)t.TotalHours}:{t.Minutes:D2}:{t.Seconds:D2}";

    /// <summary>"Your day moved to LAPTOP-X": what the strip says on the PC the day left.</summary>
    public static string MovedLine(WorkTimerState t) =>
        string.IsNullOrWhiteSpace(t.MovedTo) ? "Your day moved to another PC" : $"Your day moved to {t.MovedTo}";

    private static string BreakName(string? reason) =>
        string.IsNullOrWhiteSpace(reason) ? "On a break"
        : reason.EndsWith("break", StringComparison.OrdinalIgnoreCase) ? reason
        : $"{reason} break";

    /// <summary>The pill at the top right of the card.</summary>
    public static string Pill(WorkState state) => state switch
    {
        WorkState.Working => "Working",
        WorkState.OnBreak => "On break",
        WorkState.Ended => "Logged out",
        WorkState.Moved => "Moved",
        _ => "Not started",
    };
}

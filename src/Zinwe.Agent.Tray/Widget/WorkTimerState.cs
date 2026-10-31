using System.Text.Json.Serialization;

namespace Zinwe.Agent.Tray.Widget;

/// <summary>The employee's work-timing state for the day. Employee-initiated and fully visible.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<WorkState>))]
public enum WorkState
{
    /// <summary>The workday has not been started yet today.</summary>
    NotStarted,
    /// <summary>Clocked in and working; worked time accumulates.</summary>
    Working,
    /// <summary>On a break; break time accumulates, worked time is paused.</summary>
    OnBreak,
    /// <summary>The workday has been ended; nothing accumulates.</summary>
    Ended,
    /// <summary>The member started their day on another PC, so it moved there; nothing accumulates here. Start day
    /// takes it back. Not a log-out: no reason, no approval.</summary>
    Moved,
}

/// <summary>
/// Pure work-timing state machine and accumulator. No I/O, no WPF, no clock of its own:
/// every mutator takes the current <see cref="DateTimeOffset"/>, so it is fully unit-testable.
///
/// Rules
/// - Worked seconds accrue only while <see cref="State"/> is <see cref="WorkState.Working"/>.
/// - Break seconds accrue only while <see cref="State"/> is <see cref="WorkState.OnBreak"/>.
/// - On any transition the elapsed time since <see cref="LastTransitionAtUtc"/> is settled into the
///   bucket for the state being LEFT, then the new state begins.
/// - A new calendar day (local date) resets the day via <see cref="RollOverIfNewDay"/>.
///
/// This type is the seam the verifier can reference from a test project (it has no Tray-exe dependency).
/// </summary>
public sealed class WorkTimerState
{
    /// <summary>Local calendar date this state belongs to (yyyy-MM-dd).</summary>
    public string Date { get; set; } = "";

    public WorkState State { get; set; } = WorkState.NotStarted;

    /// <summary>Settled worked seconds (does NOT include time since the last transition while Working).</summary>
    public long WorkedSeconds { get; set; }

    /// <summary>Settled break seconds (does NOT include time since the last transition while OnBreak).</summary>
    public long BreakSeconds { get; set; }

    /// <summary>When the current state began. null only before the first Start of the day.</summary>
    public DateTimeOffset? LastTransitionAtUtc { get; set; }

    /// <summary>
    /// Why the employee is on a break (e.g. "Lunch"), picked when they pressed Break; null otherwise.
    /// Reported to the owner's Activity page with the break.
    /// </summary>
    public string? BreakReason { get; set; }

    /// <summary>Optional message the employee added to the break (e.g. "Bank errand"); null otherwise.</summary>
    public string? BreakNote { get; set; }

    /// <summary>Why the employee logged out (e.g. "Done for today"), picked in the widget; null until they do.</summary>
    public string? EndReason { get; set; }

    /// <summary>The message the employee added when logging out; null otherwise.</summary>
    public string? EndNote { get; set; }

    /// <summary>Where the day went (hostname) while <see cref="State"/> is <see cref="WorkState.Moved"/>; null otherwise.</summary>
    public string? MovedTo { get; set; }

    /// <summary>When the last Start was pressed here. A move older than this is old news.</summary>
    public DateTimeOffset? LastStartAtUtc { get; set; }

    /// <summary>Settled worked seconds when the last Start was pressed: what's above it was earned since.</summary>
    public long WorkedAtStart { get; set; }

    /// <summary>Settled break seconds when the last Start was pressed.</summary>
    public long BreakAtStart { get; set; }

    /// <summary>The hand-over last taken in (its time is its identity, so each is taken once).</summary>
    public DateTimeOffset? CarriedAtUtc { get; set; }

    /// <summary>How many breaks the day had when it moved here.</summary>
    public int CarriedBreaks { get; set; }

    /// <summary>When the day first started, on whichever PC. Null unless it moved here.</summary>
    public DateTimeOffset? FirstStartUtc { get; set; }

    /// <summary>Longest break reason we keep (matches the backend's limit).</summary>
    public const int MaxBreakReasonLength = 120;

    /// <summary>Longest break note we keep (matches the backend's limit).</summary>
    public const int MaxBreakNoteLength = 280;

    /// <summary>True when the employee can press Start (fresh day, after End, or to take a moved day back).</summary>
    [JsonIgnore]
    public bool CanStart => State is WorkState.NotStarted or WorkState.Ended or WorkState.Moved;

    /// <summary>True when Break/Resume is meaningful (clocked in).</summary>
    [JsonIgnore]
    public bool CanToggleBreak => State is WorkState.Working or WorkState.OnBreak;

    /// <summary>True when the day can be ended.</summary>
    [JsonIgnore]
    public bool CanEnd => State is WorkState.Working or WorkState.OnBreak;

    /// <summary>Settles time elapsed in the current state into its bucket, up to <paramref name="now"/>.</summary>
    private void Settle(DateTimeOffset now)
    {
        if (LastTransitionAtUtc is not { } since)
        {
            return;
        }
        var elapsed = now - since;
        if (elapsed < TimeSpan.Zero)
        {
            // Clock went backwards (e.g. DST / manual change); don't credit negative time.
            elapsed = TimeSpan.Zero;
        }
        switch (State)
        {
            case WorkState.Working:
                WorkedSeconds += (long)elapsed.TotalSeconds;
                break;
            case WorkState.OnBreak:
                BreakSeconds += (long)elapsed.TotalSeconds;
                break;
        }
        LastTransitionAtUtc = now;
    }

    /// <summary>Begins the workday (or a new session after End). No-op if already clocked in.</summary>
    public bool Start(DateTimeOffset now)
    {
        if (!CanStart)
        {
            return false;
        }
        State = WorkState.Working;
        EndReason = null;
        EndNote = null;
        MovedTo = null;
        LastTransitionAtUtc = now;
        LastStartAtUtc = now;
        WorkedAtStart = WorkedSeconds;
        BreakAtStart = BreakSeconds;
        return true;
    }

    /// <summary>
    /// The member's day moved to <paramref name="hostname"/> at <paramref name="at"/>: the timer stops there, so the
    /// minutes until this PC heard about it aren't counted twice. No reason, no approval. Returns false when there's
    /// nothing to do: old news (Start was pressed here since, or it's about another day) or already showing it.
    /// </summary>
    public bool MoveAway(string hostname, DateTimeOffset at, DateTimeOffset now)
    {
        if (LastStartAtUtc > at || !IsToday(at))
        {
            return false;
        }
        if (State == WorkState.Moved)
        {
            var changed = MovedTo != hostname; // it moved on again, to a third PC
            MovedTo = hostname;
            return changed;
        }
        Settle(at < now ? at : now);
        State = WorkState.Moved;
        MovedTo = hostname;
        BreakReason = null;
        BreakNote = null;
        EndReason = null;
        EndNote = null;
        LastTransitionAtUtc = at;
        return true;
    }

    /// <summary>
    /// The member's day moved here at <paramref name="at"/>: the day's totals become the server's, wherever they were
    /// earned, plus what this PC has settled since Start was pressed. Returns false when already taken, about another
    /// day, or the day isn't on this PC.
    /// </summary>
    public bool CarryOn(DateTimeOffset at, long worked, long onBreak, int breaks, DateTimeOffset? firstStart)
    {
        if (CarriedAtUtc == at || State is WorkState.NotStarted or WorkState.Moved || !IsToday(at))
        {
            return false;
        }
        var sinceStart = (Worked: 0L, Break: 0L);
        if (LastStartAtUtc is not null)
        {
            sinceStart = (Math.Max(0, WorkedSeconds - WorkedAtStart), Math.Max(0, BreakSeconds - BreakAtStart));
        }
        WorkedSeconds = worked + sinceStart.Worked;
        BreakSeconds = onBreak + sinceStart.Break;
        WorkedAtStart = worked;
        BreakAtStart = onBreak;
        if (LastTransitionAtUtc < at && State is WorkState.Working or WorkState.OnBreak)
        {
            LastTransitionAtUtc = at; // time up to the move is in the totals
        }
        CarriedAtUtc = at;
        CarriedBreaks = breaks;
        FirstStartUtc = firstStart ?? FirstStartUtc;
        return true;
    }

    private bool IsToday(DateTimeOffset at) => at.ToLocalTime().ToString("yyyy-MM-dd") == Date;

    /// <summary>
    /// Toggles between Working and OnBreak. Going on a break records <paramref name="reason"/>
    /// (trimmed, capped); coming back clears it. Returns false when not clocked in.
    /// </summary>
    public bool ToggleBreak(DateTimeOffset now, string? reason = null, string? note = null)
    {
        if (!CanToggleBreak)
        {
            return false;
        }
        Settle(now);
        State = State == WorkState.Working ? WorkState.OnBreak : WorkState.Working;
        var onBreak = State == WorkState.OnBreak;
        BreakReason = onBreak ? Clean(reason, MaxBreakReasonLength) : null;
        BreakNote = onBreak ? Clean(note, MaxBreakNoteLength) : null;
        LastTransitionAtUtc = now;
        return true;
    }

    private static string? Clean(string? value, int max)
    {
        var text = value?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }
        return text.Length > max ? text[..max] : text;
    }

    /// <summary>
    /// Ends the workday (the employee logs out), recording why (<paramref name="reason"/>) and their
    /// <paramref name="note"/>, trimmed and capped. Returns false when not clocked in.
    /// </summary>
    public bool End(DateTimeOffset now, string? reason = null, string? note = null)
    {
        if (!CanEnd)
        {
            return false;
        }
        Settle(now);
        State = WorkState.Ended;
        BreakReason = null;
        BreakNote = null;
        EndReason = Clean(reason, MaxBreakReasonLength);
        EndNote = Clean(note, MaxBreakNoteLength);
        LastTransitionAtUtc = now;
        return true;
    }

    /// <summary>Live worked time including any unsettled time since the last transition.</summary>
    public TimeSpan LiveWorked(DateTimeOffset now) => Live(now, WorkState.Working, WorkedSeconds);

    /// <summary>Live break time including any unsettled time since the last transition.</summary>
    public TimeSpan LiveBreak(DateTimeOffset now) => Live(now, WorkState.OnBreak, BreakSeconds);

    private TimeSpan Live(DateTimeOffset now, WorkState accruingIn, long settledSeconds)
    {
        var total = TimeSpan.FromSeconds(settledSeconds);
        if (State == accruingIn && LastTransitionAtUtc is { } since)
        {
            var elapsed = now - since;
            if (elapsed > TimeSpan.Zero)
            {
                total += elapsed;
            }
        }
        return total;
    }

    /// <summary>
    /// If <paramref name="now"/> falls on a different local day than <see cref="Date"/>, settles the old
    /// day and resets to a fresh NotStarted day. Returns true when a reset happened.
    /// </summary>
    public bool RollOverIfNewDay(DateTimeOffset now)
    {
        var today = now.ToLocalTime().ToString("yyyy-MM-dd");
        if (Date == today)
        {
            return false;
        }
        // Settle whatever was running so the (now closed) day's totals are final before we discard them.
        Settle(now);
        Date = today;
        State = WorkState.NotStarted;
        WorkedSeconds = 0;
        BreakSeconds = 0;
        LastTransitionAtUtc = null;
        BreakReason = null;
        BreakNote = null;
        EndReason = null;
        EndNote = null;
        MovedTo = null;
        LastStartAtUtc = null;
        WorkedAtStart = 0;
        BreakAtStart = 0;
        CarriedAtUtc = null;
        CarriedBreaks = 0;
        FirstStartUtc = null;
        return true;
    }

    /// <summary>A fresh state stamped with today's local date.</summary>
    public static WorkTimerState NewForToday(DateTimeOffset now) => new()
    {
        Date = now.ToLocalTime().ToString("yyyy-MM-dd"),
        State = WorkState.NotStarted,
    };
}

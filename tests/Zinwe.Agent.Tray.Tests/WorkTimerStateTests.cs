using Zinwe.Agent.Tray.Widget;

namespace Zinwe.Agent.Tray.Tests;

public sealed class WorkTimerStateTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 9, 9, 0, 0, TimeSpan.Zero);

    private static WorkTimerState Fresh() => WorkTimerState.NewForToday(T0);

    [Fact]
    public void New_day_starts_not_started_with_zeroed_buckets()
    {
        var s = Fresh();
        Assert.Equal(WorkState.NotStarted, s.State);
        Assert.Equal(0, s.WorkedSeconds);
        Assert.Equal(0, s.BreakSeconds);
        Assert.True(s.CanStart);
        Assert.False(s.CanToggleBreak);
        Assert.False(s.CanEnd);
    }

    [Fact]
    public void Worked_time_accrues_only_while_working()
    {
        var s = Fresh();
        Assert.True(s.Start(T0));

        // 30 min working
        Assert.Equal(TimeSpan.FromMinutes(30), s.LiveWorked(T0.AddMinutes(30)));

        // go on break at +30; worked freezes, break grows
        Assert.True(s.ToggleBreak(T0.AddMinutes(30)));
        Assert.Equal(TimeSpan.FromMinutes(30), s.LiveWorked(T0.AddMinutes(45)));
        Assert.Equal(TimeSpan.FromMinutes(15), s.LiveBreak(T0.AddMinutes(45)));

        // resume at +45; worked grows again, break freezes
        Assert.True(s.ToggleBreak(T0.AddMinutes(45)));
        Assert.Equal(TimeSpan.FromMinutes(40), s.LiveWorked(T0.AddMinutes(55)));
        Assert.Equal(TimeSpan.FromMinutes(15), s.LiveBreak(T0.AddMinutes(55)));
    }

    [Fact]
    public void Break_keeps_its_reason_until_resumed()
    {
        var s = Fresh();
        s.Start(T0);
        Assert.True(s.ToggleBreak(T0.AddMinutes(30), "  Lunch  "));
        Assert.Equal("Lunch", s.BreakReason);
        Assert.Equal(T0.AddMinutes(30), s.LastTransitionAtUtc);

        Assert.True(s.ToggleBreak(T0.AddMinutes(60), "ignored when resuming"));
        Assert.Null(s.BreakReason);
        Assert.Null(s.BreakNote);

        s.ToggleBreak(T0.AddMinutes(65), "Personal", "  Bank errand  ");
        Assert.Equal("Bank errand", s.BreakNote);
        s.ToggleBreak(T0.AddMinutes(68));
        Assert.Null(s.BreakNote);

        s.ToggleBreak(T0.AddMinutes(70), new string('x', 500));
        Assert.Equal(WorkTimerState.MaxBreakReasonLength, s.BreakReason!.Length);
        s.End(T0.AddMinutes(80));
        Assert.Null(s.BreakReason);
    }

    [Fact]
    public void End_settles_and_freezes_everything()
    {
        var s = Fresh();
        s.Start(T0);
        s.End(T0.AddHours(1));

        Assert.Equal(WorkState.Ended, s.State);
        Assert.Equal(3600, s.WorkedSeconds);
        // Nothing accrues after End.
        Assert.Equal(TimeSpan.FromHours(1), s.LiveWorked(T0.AddHours(3)));
    }

    [Fact]
    public void The_end_of_day_note_keeps_its_lines_up_to_a_thousand_characters_and_a_break_note_stays_short()
    {
        var s = Fresh();
        s.Start(T0);
        s.ToggleBreak(T0.AddMinutes(10), "Personal", new string('b', 600));
        Assert.Equal(WorkTimerState.MaxBreakNoteLength, s.BreakNote!.Length);
        s.ToggleBreak(T0.AddMinutes(20));

        s.End(T0.AddHours(8), "Done for today", "Quotes for Sharma Traders\r\nFixed the sign-in page\r\n" + new string('x', 2000));

        Assert.Equal(1000, WorkTimerState.MaxEndNoteLength);
        Assert.Equal(WorkTimerState.MaxEndNoteLength, s.EndNote!.Length);
        Assert.StartsWith("Quotes for Sharma Traders\r\nFixed the sign-in page\r\n", s.EndNote);
    }

    [Fact]
    public void Transitions_are_gated_by_state()
    {
        var s = Fresh();
        Assert.False(s.ToggleBreak(T0)); // can't break before starting
        Assert.False(s.End(T0));         // can't end before starting

        s.Start(T0);
        Assert.False(s.Start(T0.AddMinutes(1))); // already working

        s.End(T0.AddMinutes(10));
        Assert.False(s.ToggleBreak(T0.AddMinutes(11))); // ended
        Assert.True(s.CanStart);                        // but can start a new session
        Assert.True(s.Start(T0.AddMinutes(12)));
    }

    [Fact]
    public void A_second_session_after_end_keeps_the_days_total()
    {
        var s = Fresh();
        s.Start(T0);
        s.End(T0.AddHours(1));          // 1h worked
        s.Start(T0.AddHours(2));        // back from lunch, same day
        s.End(T0.AddHours(3));          // +1h

        Assert.Equal(TimeSpan.FromHours(2), s.LiveWorked(T0.AddHours(4)));
    }

    [Fact]
    public void Clock_going_backwards_never_credits_negative_time()
    {
        var s = Fresh();
        s.Start(T0.AddHours(1));
        // "now" is earlier than the start (manual clock change / DST): no negative worked time.
        s.End(T0);
        Assert.Equal(0, s.WorkedSeconds);
        Assert.Equal(TimeSpan.Zero, s.LiveWorked(T0.AddMinutes(-5)));
    }

    [Fact]
    public void Rollover_to_a_new_day_resets_to_a_fresh_day()
    {
        var s = Fresh();
        s.Start(T0);
        s.ToggleBreak(T0.AddHours(1));

        var nextDay = T0.AddDays(1).AddHours(1);
        Assert.True(s.RollOverIfNewDay(nextDay));
        Assert.Equal(WorkState.NotStarted, s.State);
        Assert.Equal(0, s.WorkedSeconds);
        Assert.Equal(0, s.BreakSeconds);
        Assert.Null(s.LastTransitionAtUtc);
        Assert.Equal(nextDay.ToLocalTime().ToString("yyyy-MM-dd"), s.Date);

        // Same day = no reset.
        Assert.False(s.RollOverIfNewDay(nextDay.AddHours(2)));
    }

    [Fact]
    public void Sub_second_transitions_truncate_not_round()
    {
        var s = Fresh();
        s.Start(T0);
        s.End(T0.AddSeconds(90).AddMilliseconds(800)); // 90.8s -> 90
        Assert.Equal(90, s.WorkedSeconds);
    }
}

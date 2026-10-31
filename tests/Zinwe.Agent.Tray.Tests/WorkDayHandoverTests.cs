using System.IO;
using System.Text.Json;
using Zinwe.Agent.Tray.Widget;

namespace Zinwe.Agent.Tray.Tests;

/// <summary>One person, one work day, across PCs: the timer on the PC the day left, and on the PC it came to.</summary>
public sealed class WorkDayHandoverTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 9, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Moved = T0.AddHours(2);

    private static WorkTimerState Working()
    {
        var s = WorkTimerState.NewForToday(T0);
        s.Start(T0);
        return s;
    }

    // ---- The PC the day left ----------------------------------------------------------------------

    [Fact]
    public void The_timer_stops_at_the_move_not_when_this_pc_heard_of_it()
    {
        var s = Working();

        Assert.True(s.MoveAway("LAPTOP-X", Moved, Moved.AddSeconds(50)));

        Assert.Equal(WorkState.Moved, s.State);
        Assert.Equal("LAPTOP-X", s.MovedTo);
        Assert.Equal(7200, s.WorkedSeconds);
        Assert.Equal(TimeSpan.FromHours(2), s.LiveWorked(Moved.AddHours(3)));
        Assert.False(s.CanEnd);
        Assert.False(s.CanToggleBreak);
    }

    [Fact]
    public void A_break_stops_at_the_move_too_and_nothing_is_left_of_it()
    {
        var s = Working();
        s.ToggleBreak(T0.AddHours(1), "Lunch", "Bank errand");

        s.MoveAway("LAPTOP-X", Moved, Moved.AddSeconds(30));

        Assert.Equal((3600L, 3600L), (s.WorkedSeconds, s.BreakSeconds));
        Assert.Null(s.BreakReason);
        Assert.Null(s.BreakNote);
    }

    [Fact]
    public void The_same_news_on_every_heartbeat_changes_nothing()
    {
        var s = Working();
        Assert.True(s.MoveAway("LAPTOP-X", Moved, Moved.AddSeconds(30)));
        Assert.False(s.MoveAway("LAPTOP-X", Moved, Moved.AddMinutes(5)));
        Assert.Equal(7200, s.WorkedSeconds);
    }

    [Fact]
    public void Moving_on_to_a_third_pc_updates_the_name()
    {
        var s = Working();
        s.MoveAway("LAPTOP-X", Moved, Moved.AddSeconds(30));
        Assert.True(s.MoveAway("HOME-PC", Moved, Moved.AddMinutes(5)));
        Assert.Equal("Your day moved to HOME-PC", WorkSummary.MovedLine(s));
    }

    [Fact]
    public void Start_day_takes_it_back_and_the_old_news_is_ignored()
    {
        var s = Working();
        s.MoveAway("LAPTOP-X", Moved, Moved.AddSeconds(30));

        Assert.True(s.CanStart);
        var back = Moved.AddHours(1);
        Assert.True(s.Start(back));
        Assert.Equal(WorkState.Working, s.State);
        Assert.Null(s.MovedTo);

        // A heartbeat that left the server before it heard of the Start still says "moved".
        Assert.False(s.MoveAway("LAPTOP-X", Moved, back.AddSeconds(3)));
        Assert.Equal(WorkState.Working, s.State);
    }

    [Fact]
    public void A_move_from_yesterday_is_not_about_today()
    {
        var s = Working();
        Assert.False(s.MoveAway("LAPTOP-X", T0.AddDays(-1), T0));
        Assert.Equal(WorkState.Working, s.State);
    }

    [Fact]
    public void Midnight_clears_the_moved_state_like_any_other_day()
    {
        var s = Working();
        s.MoveAway("LAPTOP-X", Moved, Moved.AddSeconds(30));

        Assert.True(s.RollOverIfNewDay(T0.AddDays(1)));

        Assert.Equal(WorkState.NotStarted, s.State);
        Assert.Null(s.MovedTo);
        Assert.Null(s.LastStartAtUtc);
    }

    [Fact]
    public void The_moved_state_is_a_quiet_one_no_reminder_no_opening()
    {
        var morning = new DateTimeOffset(2026, 10, 28, 9, 20, 0, TimeSpan.FromHours(5.5));
        var s = WorkTimerState.NewForToday(morning);
        s.MoveAway("LAPTOP-X", morning.AddMinutes(-5), morning);

        Assert.Empty(Reminders.Due(new TraySettings(), true, true, s, [], morning, TimeSpan.FromHours(1)));
        Assert.Equal("Moved", WorkSummary.Pill(s.State));
        Assert.Equal("Your day moved to LAPTOP-X", WorkSummary.Status(s));
    }

    // ---- The PC the day came to ----------------------------------------------------------------------

    [Fact]
    public void Start_day_continues_from_the_carried_time_not_from_zero()
    {
        var s = WorkTimerState.NewForToday(T0);
        s.Start(Moved);

        Assert.True(s.CarryOn(Moved, 7200, 600, 1, T0));

        Assert.Equal(TimeSpan.FromSeconds(7200 + 90), s.LiveWorked(Moved.AddSeconds(90)));
        Assert.Equal(TimeSpan.FromSeconds(600), s.LiveBreak(Moved.AddSeconds(90)));
        Assert.Equal((1, T0), (s.CarriedBreaks, s.FirstStartUtc!.Value));
    }

    [Fact]
    public void Each_hand_over_is_taken_once()
    {
        var s = WorkTimerState.NewForToday(T0);
        s.Start(Moved);
        s.CarryOn(Moved, 7200, 0, 0, T0);
        s.ToggleBreak(Moved.AddMinutes(10));

        Assert.False(s.CarryOn(Moved, 7200, 0, 0, T0));

        Assert.Equal(7200 + 600, s.WorkedSeconds);
    }

    [Fact]
    public void What_was_earned_here_before_the_news_arrived_is_kept()
    {
        var s = WorkTimerState.NewForToday(T0);
        s.Start(Moved); // offline: a break and back before the PC reconnected
        s.ToggleBreak(Moved.AddMinutes(10), "Tea");
        s.ToggleBreak(Moved.AddMinutes(15));

        s.CarryOn(Moved, 7200, 600, 1, T0);

        Assert.Equal((7200L + 600, 600L + 300), (s.WorkedSeconds, s.BreakSeconds));
        Assert.Equal(Moved.AddMinutes(15), s.LastTransitionAtUtc); // the running stretch isn't touched
    }

    [Fact]
    public void The_servers_total_replaces_this_pcs_own_time_it_already_holds_it()
    {
        // Worked here 1 h, ended; worked on the other PC; back here. The carry adds up the whole day.
        var s = WorkTimerState.NewForToday(T0);
        s.Start(T0);
        s.End(T0.AddHours(1));
        s.Start(Moved);

        s.CarryOn(Moved, 5400, 0, 0, T0);

        Assert.Equal(5400, s.WorkedSeconds);
    }

    [Fact]
    public void A_move_back_takes_the_newer_total_over_the_old_one()
    {
        var s = Working();
        s.MoveAway("LAPTOP-X", Moved, Moved.AddSeconds(30));
        var back = Moved.AddHours(1);
        s.Start(back);

        s.CarryOn(back, 10800, 0, 0, T0);

        Assert.Equal(10800, s.WorkedSeconds);
        Assert.Equal(TimeSpan.FromSeconds(10800 + 60), s.LiveWorked(back.AddMinutes(1)));
    }

    [Fact]
    public void A_start_older_than_the_hand_over_does_not_count_the_time_before_it_twice()
    {
        var s = WorkTimerState.NewForToday(T0);
        s.Start(T0); // this state predates LastStartAtUtc being kept: nothing to tell it apart
        s.LastStartAtUtc = null;

        s.CarryOn(Moved, 7200, 0, 0, T0);

        Assert.Equal(TimeSpan.FromHours(2), s.LiveWorked(Moved));
    }

    [Fact]
    public void Nothing_to_carry_on_a_pc_with_no_day_running()
    {
        var fresh = WorkTimerState.NewForToday(T0);
        Assert.False(fresh.CarryOn(Moved, 7200, 0, 0, T0));
        var moved = Working();
        moved.MoveAway("LAPTOP-X", Moved, Moved);
        Assert.False(moved.CarryOn(Moved, 7200, 0, 0, T0));
    }

    [Fact]
    public void A_carry_from_yesterday_is_not_about_today()
    {
        var s = Working();
        Assert.False(s.CarryOn(T0.AddDays(-1), 7200, 0, 0, T0));
        Assert.Equal(0, s.WorkedSeconds);
    }

    [Fact]
    public void Status_names_the_real_start_of_the_day()
    {
        var s = WorkTimerState.NewForToday(T0);
        s.Start(Moved);
        s.CarryOn(Moved, 7200, 0, 0, T0);
        Assert.Contains(T0.ToLocalTime().ToString("h:mm tt"), WorkSummary.Status(s));
    }

    [Fact]
    public void The_state_survives_a_restart()
    {
        var s = Working();
        s.CarryOn(T0, 3600, 60, 2, T0.AddHours(-1));
        s.MoveAway("LAPTOP-X", Moved, Moved);

        var back = JsonSerializer.Deserialize<WorkTimerState>(JsonSerializer.Serialize(s))!;

        Assert.Equal(WorkState.Moved, back.State);
        Assert.Equal("LAPTOP-X", back.MovedTo);
        Assert.Equal((2, T0), (back.CarriedBreaks, back.CarriedAtUtc!.Value));
    }

    // ---- Today's timeline ---------------------------------------------------------------------------

    private static DayLog.Entry Line(DateTimeOffset at, string action, long worked = 0, string? host = null, string? reason = null) =>
        new(at, action, "Working", worked, 0, reason, Host: host);

    [Fact]
    public void The_timeline_says_where_the_day_went_and_where_it_came_from()
    {
        var today = DayLog.Today(
        [
            Line(T0, "start"),
            Line(Moved, "moved", 7200, "LAPTOP-X"),
            Line(Moved.AddHours(1), "start"),
            Line(Moved.AddHours(1), "carried", 10800, "LAPTOP-X"),
        ], Moved.AddHours(2));

        Assert.Equal(
            ["Started day", "Moved to LAPTOP-X", "Started again", "Carried on from LAPTOP-X · 3 h 0 min so far"],
            today.Select(e => e.Label));
    }

    [Fact]
    public void A_restart_after_the_day_ended_elsewhere_names_no_pc()
    {
        var today = DayLog.Today([Line(T0, "carried", 600, "")], T0.AddHours(1));
        Assert.Equal("Carried on · 10 min so far", Assert.Single(today).Label);
    }

    [Fact]
    public void Breaks_and_the_start_of_the_day_count_the_whole_day()
    {
        var s = WorkTimerState.NewForToday(T0);
        s.Start(Moved);
        s.CarryOn(Moved, 7200, 600, 2, T0);
        var today = DayLog.Today(
        [
            Line(T0.AddMinutes(30), "break", reason: "Lunch"), // on this PC, before the move: already in the carry
            Line(Moved, "start"),
            Line(Moved.AddMinutes(10), "break", reason: "Tea"),
        ], Moved.AddHours(1));

        Assert.Equal(3, DayLog.BreaksToday(today, s));
        Assert.Equal(T0, DayLog.FirstStart(today, s));
    }

    [Fact]
    public void Without_a_hand_over_they_read_as_before()
    {
        var s = WorkTimerState.NewForToday(T0);
        var today = DayLog.Today([Line(T0, "start"), Line(T0.AddMinutes(5), "break", reason: "Tea")], T0.AddHours(1));
        Assert.Equal(1, DayLog.BreaksToday(today, s));
        Assert.Equal(T0, DayLog.FirstStart(today, s));
    }

    [Fact]
    public void The_log_writes_the_pc_name_and_reads_it_back()
    {
        var dir = Path.Combine(Path.GetTempPath(), "zinwe-handover-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var file = Path.Combine(dir, "log.jsonl");
            File.WriteAllText(file, JsonSerializer.Serialize(new { At = T0, Action = "moved", State = "Moved", WorkedSeconds = 60, BreakSeconds = 0, Host = "LAPTOP-X" }));
            Assert.Equal("LAPTOP-X", Assert.Single(DayLog.Read(file)).Host);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}

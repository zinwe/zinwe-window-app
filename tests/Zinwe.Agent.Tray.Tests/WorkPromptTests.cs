using Zinwe.Agent.Tray.Widget;

namespace Zinwe.Agent.Tray.Tests;

/// <summary>The one input view behind "Take a break" and "Log out", and the work card's wording.</summary>
public sealed class WorkPromptTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 15, 9, 0, 0, TimeSpan.Zero);

    [Theory]
    [MemberData(nameof(Prompts))]
    public void A_reason_is_required_and_Other_needs_words(WorkPrompt prompt)
    {
        Assert.Equal("Pick a reason to continue.", prompt.Missing(null, "anything"));
        Assert.Null(prompt.Missing(prompt.Reasons[0], ""));                  // a normal reason: note optional
        Assert.Equal("Add a few words for your admin.", prompt.Missing(WorkPrompt.Other, "   "));
        Assert.Null(prompt.Missing(WorkPrompt.Other, "Bank errand"));
        Assert.Contains(WorkPrompt.Other, prompt.Reasons);
        Assert.EndsWith("(required)", prompt.NotePlaceholder(WorkPrompt.Other));
        Assert.DoesNotContain("(required)", prompt.NotePlaceholder(prompt.Reasons[0]));
    }

    public static TheoryData<WorkPrompt> Prompts => new() { WorkPrompt.Break, WorkPrompt.LogOut };

    [Fact]
    public void Break_and_log_out_read_as_their_own_actions()
    {
        Assert.Equal(("Taking a break", "Start break"), (WorkPrompt.Break.Title, WorkPrompt.Break.ConfirmLabel));
        // Tray v2 (prototype board): "Ending your day" / "End day".
        Assert.Equal(("Ending your day", "End day"), (WorkPrompt.LogOut.Title, WorkPrompt.LogOut.ConfirmLabel));
        Assert.Contains("Lunch", WorkPrompt.Break.Reasons);
        Assert.Contains("Done for today", WorkPrompt.LogOut.Reasons);
    }

    [Fact]
    public void Logging_out_keeps_the_reason_until_the_next_log_in()
    {
        var t = WorkTimerState.NewForToday(T0);
        t.Start(T0);
        Assert.True(t.End(T0.AddHours(8), "  Leaving early ", "Doctor's appointment"));
        Assert.Equal(("Leaving early", "Doctor's appointment"), (t.EndReason, t.EndNote));

        t.Start(T0.AddHours(9));
        Assert.Null(t.EndReason);
        Assert.Null(t.EndNote);
    }

    [Fact]
    public void The_card_names_what_is_running_in_log_in_and_log_out_words()
    {
        var t = WorkTimerState.NewForToday(T0);
        Assert.Equal("Not logged in yet today", WorkSummary.Status(t));
        Assert.Equal("Not started", WorkSummary.Pill(t.State));

        t.Start(T0);
        Assert.StartsWith("Worked today · logged in since ", WorkSummary.Status(t));
        Assert.Equal(TimeSpan.FromHours(3), WorkSummary.Hero(t, T0.AddHours(3)));

        t.ToggleBreak(T0.AddHours(3), "Lunch");
        Assert.StartsWith("Lunch break · since ", WorkSummary.Status(t));
        Assert.Equal("On break", WorkSummary.Pill(t.State));
        // On a break the big number is the break itself; worked time moves to the second line.
        Assert.Equal(TimeSpan.FromMinutes(20), WorkSummary.Hero(t, T0.AddHours(3).AddMinutes(20)));
        Assert.Equal("Worked today 3:00:00", WorkSummary.Secondary(t, T0.AddHours(3).AddMinutes(20)));

        t.ToggleBreak(T0.AddHours(4));
        Assert.Equal("Breaks today 1:00:00", WorkSummary.Secondary(t, T0.AddHours(4)));

        t.End(T0.AddHours(8), "Done for today");
        Assert.StartsWith("Worked today · logged out at ", WorkSummary.Status(t));
    }

    [Fact]
    public void The_sheet_picks_the_likely_reason_and_says_open_tasks_stay()
    {
        var offset = TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 10, 15));
        DateTimeOffset At(int h, int m = 0) => new(2026, 10, 15, h, m, 0, offset);
        Assert.Equal("Lunch", WorkPrompt.Break.Likely(At(13), hadLunch: false));
        Assert.Null(WorkPrompt.Break.Likely(At(13), hadLunch: true));
        Assert.Null(WorkPrompt.Break.Likely(At(10)));
        Assert.Equal("Done for today", WorkPrompt.LogOut.Likely(At(17, 30), hadLunch: true));
        Assert.Null(WorkPrompt.LogOut.Likely(At(15), hadLunch: true));
        Assert.Equal("Pick a reason. Your 3 open tasks stay in Tasks for tomorrow.", WorkPrompt.LogOut.Hint(3));
        Assert.Equal("Pick a reason.", WorkPrompt.Break.Hint(3));
        Assert.DoesNotContain("Tea / coffee", WorkPrompt.Break.Reasons);
    }

    [Theory]
    [InlineData(8, "Good morning")]
    [InlineData(13, "Good afternoon")]
    [InlineData(19, "Good evening")]
    public void Greeting_follows_the_local_hour(int hour, string expected)
    {
        var local = new DateTimeOffset(2026, 10, 15, hour, 0, 0, TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 10, 15)));
        Assert.Equal(expected, WorkSummary.Greeting(local));
    }
}

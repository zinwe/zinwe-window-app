using System.IO;
using Zinwe.Agent.Core.Ipc;
using Zinwe.Agent.Core.Work;
using Zinwe.Agent.Tray.Resources;
using Zinwe.Agent.Tray.Widget;

namespace Zinwe.Agent.Tray.Tests;

/// <summary>The employee-facing fixes of 2026-10-17: consent choice, findability, clear states, kinder chat.</summary>
public sealed class FixRoundWidgetTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "zinwe-widget-" + Guid.NewGuid().ToString("N"));

    private static AgentStatus Status(AgentState state, string? company = null, bool removed = false,
        string? notice = null, MonitoringScope? scope = null, string? owner = null) =>
        new(state, "1.0.3", "pc", 1, null, null, CompanyName: company, OwnerName: owner, Removed: removed,
            NoticeVersion: notice, Monitoring: scope);

    // ---- Consent notice ----

    [Fact]
    public void The_notice_names_the_employer_and_lists_only_what_is_on()
    {
        var view = ConsentText.Build(new MonitoringScope(false, true, false, false, true), "Kulkarni & Associates", "Ravi");

        Assert.StartsWith("Kulkarni & Associates uses Zinwe", view.Intro);
        Assert.Contains("only sees that this PC is connected and online", view.Intro);
        Assert.Equal("What Kulkarni & Associates can see", view.SeesHeading);
        Assert.Equal(2, view.Sees.Count);
        Assert.Contains(view.Sees, s => s.StartsWith("Copies to USB drives", StringComparison.Ordinal));
        Assert.Contains(view.Sees, s => s.StartsWith("When you log in", StringComparison.Ordinal));
        Assert.DoesNotContain(view.Sees, s => s.Contains("apps", StringComparison.Ordinal));
        Assert.Contains("message Ravi", view.DeclineBody);
        Assert.Contains("Kulkarni & Associates will see that you didn't agree", view.DeclineBody);
    }

    [Fact]
    public void File_names_and_email_subjects_are_disclosed_when_switched_on()
    {
        var view = ConsentText.Build(MonitoringScope.Everything, null, null);

        Assert.Contains(view.Sees, s => s.Contains("names of the files", StringComparison.Ordinal)
            && s.Contains("subjects of emails", StringComparison.Ordinal));
        Assert.StartsWith("Your employer uses Zinwe", view.Intro);
        Assert.Equal("What your employer can see", view.SeesHeading);
        Assert.Contains("message your admin", view.DeclineBody);
        Assert.Equal(4, view.Never.Count);
    }

    [Fact]
    public void Nothing_switched_on_says_so()
    {
        var view = ConsentText.Build(new MonitoringScope(false, false, false, false, false), "Acme", null, chatOn: false);
        Assert.Equal([Strings.ConsentSeesNothing], view.Sees);
    }

    [Fact]
    public void The_consent_file_records_agree_decline_and_clear()
    {
        var store = new ConsentStore(_dir);
        Assert.Equal(ConsentState.Pending, store.State("2026-10-17.atrw"));

        store.Decline("2026-10-17.atrw", DateTimeOffset.Now);
        Assert.Equal(ConsentState.Declined, store.State("2026-10-17.atrw"));
        // An older service (no "State") must never read a decline as an agreement: no AcceptedAt in the file.
        Assert.DoesNotContain("AcceptedAt\": \"", File.ReadAllText(Path.Combine(_dir, "consent.json")));

        store.Accept(ConsentNotice.FullVersion, DateTimeOffset.Now);
        Assert.Equal(ConsentState.Given, store.State("2026-10-17.atrw"));
        Assert.Equal(ConsentState.Pending, store.State("2026-11-01.a")); // new wording: asked again

        store.Clear();
        Assert.Null(store.Load());
    }

    // ---- Which view ----

    [Fact]
    public void Service_not_running_shows_the_card_and_keeps_the_day_for_someone_who_answered()
    {
        Assert.Equal(new WidgetLayout(WidgetScreen.None, true, false, false), WidgetLayout.From(null, ConsentState.Pending));
        Assert.Equal(new WidgetLayout(WidgetScreen.Main, true, false, false), WidgetLayout.From(null, ConsentState.Given));
    }

    [Theory]
    [InlineData(AgentState.NotEnrolled, ConsentState.Pending, WidgetScreen.Connect, false)]
    [InlineData(AgentState.Enrolling, ConsentState.Given, WidgetScreen.Connect, false)]
    [InlineData(AgentState.Connected, ConsentState.Pending, WidgetScreen.Consent, false)]
    [InlineData(AgentState.Offline, ConsentState.Given, WidgetScreen.Main, false)]
    [InlineData(AgentState.UpdateRequired, ConsentState.Declined, WidgetScreen.Main, true)]
    public void Each_state_has_one_view(AgentState state, ConsentState consent, WidgetScreen screen, bool declinedBanner)
    {
        var layout = WidgetLayout.From(Status(state), consent);
        Assert.Equal(screen, layout.Screen);
        Assert.Equal(declinedBanner, layout.DeclinedBanner);
        Assert.False(layout.NotRunning);
    }

    [Fact]
    public void A_declined_member_still_gets_the_day_and_the_chat()
    {
        Assert.Equal(WidgetScreen.Main, WidgetLayout.From(Status(AgentState.Connected), ConsentState.Declined).Screen);
    }

    [Fact]
    public void A_removed_pc_explains_itself_above_the_connect_form()
    {
        var layout = WidgetLayout.From(Status(AgentState.Rejected, "Kulkarni & Associates", removed: true), ConsentState.Pending);
        Assert.Equal(WidgetScreen.Connect, layout.Screen);
        Assert.True(layout.Removed);
    }

    // ---- Opening by itself ----

    [Theory]
    [InlineData(AgentState.NotEnrolled, ConsentState.Pending, WorkState.NotStarted, AutoOpenReason.Connect)]
    [InlineData(AgentState.Connected, ConsentState.Pending, WorkState.NotStarted, AutoOpenReason.Consent)]
    [InlineData(AgentState.Connected, ConsentState.Given, WorkState.NotStarted, AutoOpenReason.StartDay)]
    [InlineData(AgentState.Offline, ConsentState.Declined, WorkState.NotStarted, AutoOpenReason.StartDay)]
    [InlineData(AgentState.Connected, ConsentState.Given, WorkState.Working, AutoOpenReason.None)]
    [InlineData(AgentState.Rejected, ConsentState.Pending, WorkState.NotStarted, AutoOpenReason.None)]
    public void The_widget_opens_itself_when_something_waits_for_the_member(
        AgentState state, ConsentState consent, WorkState today, AutoOpenReason expected)
    {
        Assert.Equal(expected, AutoOpen.Decide(Status(state), consent, today, workHoursOn: true));
    }

    [Fact]
    public void No_work_hours_on_the_plan_means_no_good_morning_pop_up()
    {
        Assert.Equal(AutoOpenReason.None,
            AutoOpen.Decide(Status(AgentState.Connected), ConsentState.Given, WorkState.NotStarted, workHoursOn: false));
        Assert.Equal(AutoOpenReason.None, AutoOpen.Decide(null, ConsentState.Pending, WorkState.NotStarted, true));
    }

    // ---- Fitting the screen (1366×768 at 100 %, 1920×1080 at 150 % ≈ 1280×~690 DIPs) ----

    [Theory]
    [InlineData(728, 733, 712)]
    [InlineData(690, 733, 674)]
    [InlineData(1040, 500, 1024)]
    public void The_widget_is_never_taller_than_the_screen(double areaHeight, double contentHeight, double expectedMax)
    {
        var max = WindowFit.MaxHeight(areaHeight);
        Assert.Equal(expectedMax, max);
        var height = Math.Min(contentHeight, max);
        var top = WindowFit.Top(202, height, 0, areaHeight);
        Assert.True(top >= 0 && top + height <= areaHeight, $"top {top} height {height}");
    }

    [Fact]
    public void A_window_that_grew_past_the_bottom_moves_up_and_a_new_view_is_centred()
    {
        Assert.Equal(1080 - 8 - 600, WindowFit.Top(700, 600, 0, 1080));
        Assert.Equal(300, WindowFit.Top(300, 400, 0, 1080)); // fits: unchanged
        Assert.Equal(8, WindowFit.Top(-50, 100, 0, 1080));
        Assert.Equal(240, WindowFit.CenteredTop(600, 0, 1080));
    }

    // ---- Status in words ----

    [Fact]
    public void The_status_line_says_who_the_pc_is_connected_to()
    {
        Assert.Equal("Connected to Kulkarni & Associates", StatusText.Line(Status(AgentState.Connected, "Kulkarni & Associates")));
        Assert.Equal("Connected", StatusText.Line(Status(AgentState.Connected)));
        Assert.Equal("Offline — your times are saved and will sync", StatusText.Line(Status(AgentState.Offline)));
        Assert.Equal("Zinwe isn't running", StatusText.Line(null));
        Assert.Equal("Removed from Acme", StatusText.Line(Status(AgentState.Rejected, "Acme", removed: true)));
        Assert.Equal(StatusTone.Ok, StatusText.Tone(Status(AgentState.Connected)));
        Assert.Equal(StatusTone.Bad, StatusText.Tone(null));
    }

    [Fact]
    public void A_removed_pc_is_explained_without_jargon()
    {
        var (headline, detail, _) = StatusText.Describe(Status(AgentState.Rejected, "Kulkarni & Associates", removed: true));
        Assert.Equal("Removed from Kulkarni & Associates", headline);
        Assert.Equal("Kulkarni & Associates removed this PC from Zinwe. Monitoring has stopped.", detail);
        Assert.DoesNotContain("enrollment", detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Not_running_says_what_to_do()
    {
        var (_, detail, tone) = StatusText.Describe(null);
        Assert.Contains("Restart your PC", detail);
        Assert.Equal(StatusTone.Bad, tone);
    }

    // ---- Chat: a break that wasn't approved ----

    private static NotificationItem Denied(string? note) => new(
        "9", DateTimeOffset.Now, "warning", "Your Lunch break at 2:08 PM wasn't approved", note, Kind: NotificationItem.ChatKind);

    [Fact]
    public void A_denied_break_shows_the_owner_note_kindly_with_a_reply_action()
    {
        var row = Assert.Single(ChatFeed.Build([Denied("Please take lunch after 1.")], [], owner: "Ravi"), r => !r.IsSeparator);

        Assert.Equal("About your Lunch break at 2:08 PM", row.Title);
        Assert.Contains("Ravi", row.Text);
        Assert.Contains("Please take lunch after 1.", row.Text);
        Assert.False(row.IsImportant); // no orange "Important"
        Assert.True(row.OffersReply);
        Assert.Equal("Message Ravi", row.ReplyLabel);
    }

    [Fact]
    public void A_denied_break_without_a_note_invites_a_reply()
    {
        var (title, text) = ChatFeed.Content(Denied(null));
        Assert.Equal("About your Lunch break at 2:08 PM", title);
        Assert.Contains("Your admin marked this break as not approved", text);
        Assert.Contains("reply here", text);
        Assert.True(ChatFeed.IsBreakNotApproved(Denied(null)));
    }

    // ---- Strings come from the resource file ----

    [Fact]
    public void Strings_are_read_from_the_resource_file()
    {
        Assert.Equal("I don't agree", Strings.ConsentDecline);
        Assert.Equal("What Zinwe records on this PC", Strings.RecordsLink);
        Assert.Equal("Work time today: 2 h 5 min", Strings.Format(Strings.WorkTimeToday, Strings.Format(Strings.DurationHoursMinutes, 2, 5)));
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }
}

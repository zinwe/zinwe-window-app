using System.Text.Json;
using Zinwe.Agent.Core.Backend;
using Zinwe.Agent.Core.Tasks;
using Zinwe.Agent.Core.Work;

namespace Zinwe.Agent.Core.Tests;

/// <summary>
/// v1.4 · the member side of team tasks (prototype engine_win.js / panel.js): the What's happening sentence and its
/// buttons for every state, the pill, the journey bar, people in the first person, steps (only your own tickable,
/// in-order locks), the card line and the lists - and that a 1.3 task (no people, no step owners) still reads right.
/// </summary>
public sealed class TeamTaskEngineTests
{
    private const long Priya = 4, Karan = 5, Emily = 6;
    private static readonly DateTimeOffset Now = new DateTimeOffset(2026, 11, 7, 15, 0, 0, TimeSpan.Zero).ToLocalTime();
    private static readonly TaskContext Ctx = new(Priya, "Ananya", "Ananya", Now, "Design");

    private static DeviceTaskPerson P(long id, string name, string state = "progress") => new(id, name, state);

    private static DeviceTask Lumen(string status = "progress", params bool[] done) => new(
        6, "Launch the Lumen landing page", Status: status, InOrder: true, Mode: "all",
        People: [P(Priya, "Priya Nair"), P(Karan, "Karan Malhotra"), P(Emily, "Emily Carter")],
        Steps:
        [
            new(1, "Write the copy", done.ElementAtOrDefault(0), Priya),
            new(2, "Pick the images", done.ElementAtOrDefault(1), Priya),
            new(3, "Build the page", done.ElementAtOrDefault(2), Karan),
            new(4, "Check on phones", done.ElementAtOrDefault(3), Emily),
        ],
        DueAt: Now.AddHours(3), CreatedAt: Now.AddDays(-2));

    // ---- What's happening ------------------------------------------------------------------------

    [Fact]
    public void New_task_says_who_sent_it_and_offers_Start()
    {
        var t = new DeviceTask(11, "Write the case study", Status: "sent", DueAt: Now.Date.AddDays(1).AddHours(18), People: [P(Priya, "Priya Nair", "sent")]);
        var h = TeamTaskEngine.Happen(t, Ctx);
        Assert.Equal(TaskTone.Needs, h.Tone);
        Assert.StartsWith("New from Ananya. Due tomorrow 6:00 PM.", h.Text);
        Assert.Equal(("Start", "start"), (h.Buttons.Single().Label, h.Buttons.Single().Act));
        Assert.Equal(new TaskPill("New", TaskTone.Needs), TeamTaskEngine.Pill(t, Ctx));
        Assert.Equal("New from Ananya", TeamTaskEngine.CardLine(t, Ctx));
        Assert.Equal(TaskBucket.Needs, TeamTaskEngine.Bucket(t, Ctx));
    }

    [Fact]
    public void In_order_your_turn_ticks_the_current_step()
    {
        var t = Lumen("progress", true);
        var h = TeamTaskEngine.Happen(t, Ctx);
        Assert.Equal("Your turn: step 2 of 4, Pick the images.", h.Text);
        Assert.Equal(("Tick step 2", "tick:1"), (h.Buttons[0].Label, h.Buttons[0].Act));
        Assert.Equal("Your turn · step 2", h.Short);
    }

    [Fact]
    public void In_order_after_your_part_it_names_who_is_next()
    {
        var h = TeamTaskEngine.Happen(Lumen("progress", true, true), Ctx);
        Assert.Equal(TaskTone.Ok, h.Tone);
        Assert.Equal("Your part is done. Karan is next.", h.Text);
        Assert.Empty(h.Buttons);
        Assert.Equal(TaskBucket.Wait, TeamTaskEngine.Bucket(Lumen("progress", true, true), Ctx));
    }

    [Fact]
    public void In_order_waiting_for_someone_before_your_step()
    {
        var t = Lumen() with
        {
            Steps = [new(1, "Finalise the pricing table", false, Karan), new(2, "Design the cover", false, Priya)],
        };
        var h = TeamTaskEngine.Happen(t, Ctx);
        Assert.Equal("Waits for Karan: step 1 (Finalise the pricing table).", h.Text);
        var rows = TeamTaskEngine.StepRows(t, Ctx);
        Assert.False(rows[0].CanTick);
        Assert.Equal("Only Karan can tick this step", rows[0].Lock);
        Assert.False(rows[1].CanTick);
        Assert.Equal("This step waits for step 1", rows[1].Lock);
    }

    [Fact]
    public void Review_waits_for_the_owners_OK()
    {
        var t = Lumen("review", true, true, true, true);
        Assert.Equal("You’re done. Waiting for Ananya’s OK.", TeamTaskEngine.Happen(t, Ctx).Text);
        Assert.Equal(new TaskPill("Waiting for OK", TaskTone.Ok), TeamTaskEngine.Pill(t, Ctx));
    }

    [Fact]
    public void Reopened_says_why_and_offers_Start_again_until_picked_up()
    {
        var reopenedAt = Now.AddMinutes(-30);
        var t = new DeviceTask(21, "Update the pricing banner", Status: "progress", ReopenNote: "Use the new orange, not the old red.",
            People: [P(Priya, "Priya Nair")],
            Activity: [new(reopenedAt, "owner", "reopened", "Use the new orange, not the old red.")]);
        var h = TeamTaskEngine.Happen(t, Ctx);
        Assert.Equal("Ananya reopened it: “Use the new orange, not the old red.”", h.Text);
        Assert.Equal("Start again", h.Buttons.Single().Label);
        Assert.Equal("Sent back: “Use the new orange, not the old red.”", TeamTaskEngine.CardLine(t, Ctx));
        Assert.Equal(new TaskPill("Reopened", TaskTone.Red), TeamTaskEngine.Pill(t, Ctx));

        var picked = t with { Activity = [.. t.Activity!, new DeviceTaskActivity(Now, "member", "started")] };
        Assert.False(TeamTaskEngine.Reopened(picked, Ctx));
        Assert.Equal("You’re on it.", TeamTaskEngine.Happen(picked, Ctx).Text);
    }

    [Fact]
    public void The_backends_restart_flag_decides_Start_again()
    {
        var t = new DeviceTask(21, "X", Status: "progress", ReopenNote: "Fix the colour", People: [P(Priya, "Priya Nair")],
            Me: new DeviceTaskMe(Restart: true));
        Assert.Equal("Start again", TeamTaskEngine.Happen(t, Ctx).Buttons[0].Label);
        Assert.False(TeamTaskEngine.Reopened(t with { Me = new DeviceTaskMe(Restart: false) }, Ctx));
    }

    [Fact]
    public void A_teammates_help_reason_comes_from_their_line()
    {
        var t = new DeviceTask(1, "X", Status: "help", HelpNote: "",
            People: [P(Priya, "Priya Nair"), new DeviceTaskPerson(Karan, "Karan Malhotra", "help", Note: "Which API key?")]);
        Assert.Equal("Karan asked Ananya for help: “Which API key?”", TeamTaskEngine.Happen(t, Ctx).Text);
    }

    [Fact]
    public void Help_and_cant_do_are_told_in_the_first_person()
    {
        var help = new DeviceTask(1, "X", Status: "help", HelpNote: "Which logo?", People: [P(Priya, "Priya Nair", "help")]);
        Assert.Equal("You asked Ananya for help: “Which logo?”", TeamTaskEngine.Happen(help, Ctx).Text);
        Assert.Equal(new TaskPill("Asked for help", TaskTone.Red), TeamTaskEngine.Pill(help, Ctx));
        var cant = help with { Status = "cantdo", CantdoNote = "On leave Friday", People = [P(Priya, "Priya Nair", "cantdo")] };
        Assert.Equal("You told Ananya you can’t do it: “On leave Friday”", TeamTaskEngine.Happen(cant, Ctx).Text);
    }

    [Fact]
    public void Approved_and_cancelled()
    {
        Assert.Equal("Approved by Ananya. Nice work.", TeamTaskEngine.Happen(Lumen("approved"), Ctx).Text);
        Assert.Equal("Ananya cancelled this task.", TeamTaskEngine.Happen(Lumen("cancelled"), Ctx).Text);
        Assert.Equal(TaskBucket.Done, TeamTaskEngine.Bucket(Lumen("approved"), Ctx));
    }

    [Fact]
    public void No_steps_offers_Mark_it_done_and_team_waits_for_the_others_after()
    {
        var solo = new DeviceTask(3, "Call the printer company", Status: "progress", People: [P(Priya, "Priya Nair")]);
        Assert.Equal(("You’re on it.", "Mark it done", "finish"),
            (TeamTaskEngine.Happen(solo, Ctx).Text, TeamTaskEngine.Happen(solo, Ctx).Buttons[0].Label, TeamTaskEngine.Happen(solo, Ctx).Buttons[0].Act));
        var team = solo with { People = [P(Priya, "Priya Nair", "done"), P(Emily, "Emily Carter")] };
        Assert.Equal("Your part is done. Waiting for Emily.", TeamTaskEngine.Happen(team, Ctx).Text);
    }

    [Fact]
    public void Any_one_of_them_task_says_the_first_to_start_takes_it()
    {
        var t = new DeviceTask(24, "Fix the 2nd floor printer", Status: "sent", Mode: "any", GroupId: 3,
            People: [P(Priya, "Priya Nair", "sent"), P(Emily, "Emily Carter", "sent"), P(9, "Fatima Sheikh", "sent")]);
        Assert.Equal("New for Design · the first to start takes it", TeamTaskEngine.CardLine(t, Ctx));
        Assert.Contains("Sent to Design. The first of you to start takes it.", TeamTaskEngine.Happen(t, Ctx).Sub);
        Assert.Equal("People · 3 · the first to start takes it", TeamTaskEngine.PeopleHead(t, Ctx));
    }

    // ---- People, journey, steps ------------------------------------------------------------------

    [Fact]
    public void People_read_in_the_first_person()
    {
        var rows = TeamTaskEngine.PeopleRows(Lumen("progress", true), Ctx);
        Assert.Equal("You", rows[0].Name);
        Assert.Equal("Your turn", rows[0].Word);
        Assert.Equal((1, 2), (rows[0].Done, rows[0].Total));
        Assert.Equal("Waits for You", rows[1].Word);
        Assert.Equal("KM", rows[1].Initials);
    }

    [Fact]
    public void Journey_counts_people_on_a_team_task()
    {
        var j = TeamTaskEngine.Journey(Lumen("progress", true, true), Ctx);
        Assert.Equal(["Sent", "Seen 3/3", "Working", "Done 1/3", "OK"], j.Select(s => s.Label));
        Assert.Equal([JourneyState.Past, JourneyState.Past, JourneyState.Now, JourneyState.Next, JourneyState.Next], j.Select(s => s.State));
        Assert.All(TeamTaskEngine.Journey(Lumen("approved"), Ctx), s => Assert.Equal(JourneyState.Ok, s.State));
        Assert.Equal(JourneyState.Red, TeamTaskEngine.Journey(Lumen("help"), Ctx)[2].State);
    }

    [Fact]
    public void Only_your_steps_and_anyones_are_tickable_and_not_before_Start()
    {
        var t = new DeviceTask(8, "Prep", Status: "progress", People: [P(Priya, "Priya Nair"), P(Karan, "Karan Malhotra")],
            Steps: [new(1, "Mine", false, Priya), new(2, "Karan's", false, Karan), new(3, "Anyone's", false, null)]);
        var rows = TeamTaskEngine.StepRows(t, Ctx);
        Assert.Equal([true, false, true], rows.Select(r => r.CanTick));
        Assert.Equal(["You", "Karan Malhotra", "Anyone"], rows.Select(r => r.OwnerName));
        Assert.All(TeamTaskEngine.StepRows(t with { Status = "seen" }, Ctx), r => Assert.False(r.CanTick));
        Assert.Equal("Anyone on the task can do this step.", TeamTaskEngine.WhoseStep(rows[2]));
    }

    // ---- 1.3 tasks (graceful fallback) ----------------------------------------------------------

    [Fact]
    public void A_13_task_without_people_or_owners_is_the_members_alone()
    {
        const string json = """
            {"id": 41, "title": "Book the room", "status": "progress", "steps": [{"id": 1, "title": "Check", "done": true},
             {"id": 2, "title": "Book it", "done": false}], "created_at": "2026-10-30T09:00:00+05:30"}
            """;
        var t = JsonSerializer.Deserialize(json, BackendJsonContext.Default.DeviceTask)!;
        var c = Ctx with { MeId = null };
        Assert.False(TeamTaskEngine.IsTeamShape(t));
        Assert.Single(TeamTaskEngine.People(t, c));
        Assert.Equal("You’re on it: 1 of 2 steps done.", TeamTaskEngine.Happen(t, c).Text);
        Assert.Equal("Tick step 2", TeamTaskEngine.Happen(t, c).Buttons[0].Label);
        Assert.True(TeamTaskEngine.StepRows(t, c)[1].CanTick);
        Assert.Equal(["Sent", "Seen", "Working", "Done", "OK"], TeamTaskEngine.Journey(t, c).Select(s => s.Label));
        Assert.Equal("You", TeamTaskEngine.PeopleRows(t, c)[0].Name);
    }

    [Fact]
    public void Accepted_from_13_reads_as_new()
    {
        var t = new DeviceTask(1, "X", Status: "accepted");
        Assert.Equal("Start", TeamTaskEngine.Happen(t, Ctx with { MeId = null }).Buttons[0].Label);
    }

    [Fact]
    public void Team_task_json_reads_people_steps_owners_chat_and_me()
    {
        const string json = """
            {"id": 6, "title": "Lumen", "status": "progress", "mode": "all", "in_order": true, "repeat": "", "next_on": null,
             "group_id": 3, "people": [{"member_id": 4, "name": "Priya Nair", "state": "progress", "steps_total": 2, "steps_done": 1,
             "seen_at": "2026-11-05T09:30:00+05:30", "done_at": null}],
             "steps": [{"id": 1, "title": "Copy", "done": true, "owner_id": 4, "done_by_id": 4, "done_at": "2026-11-05T10:00:00+05:30"}],
             "help_note": "", "cantdo_note": "", "reopen_note": "", "nudged_at": null,
             "chat": {"count": 3, "unread": 1, "last": {"text": "Great", "by": "Ananya", "at": "2026-11-07T12:15:00+05:30"}},
             "member": 4, "me": {"turn": true, "can_tick": [2], "state": "progress"}}
            """;
        var t = JsonSerializer.Deserialize(json, BackendJsonContext.Default.DeviceTask)!;
        Assert.True(t.InOrder);
        Assert.Equal(3, t.GroupId);
        Assert.Equal(4, t.People!.Single().MemberId);
        Assert.Equal(4, t.Steps![0].OwnerId);
        Assert.Equal((3, 1, "Great"), (t.Chat!.Count, t.Chat.Unread, t.Chat.Last!.Text));
        Assert.Equal([2L], t.Me!.CanTick!);
        Assert.Equal(4, TeamTaskEngine.Me(t, Ctx with { MeId = null }));
    }

    // ---- Due words ------------------------------------------------------------------------------

    [Fact]
    public void Due_words_follow_the_prototype()
    {
        var today6 = new DateTimeOffset(Now.Date.AddHours(18), Now.Offset);
        var t = new DeviceTask(1, "X", Status: "progress", DueAt: today6);
        var due = TeamTaskEngine.Due(t, Ctx with { Now = new DateTimeOffset(Now.Date.AddHours(15), Now.Offset) });
        Assert.Equal("Due today 6:00 PM · in 3 h", due.Chip);
        Assert.Equal("Today 6:00 PM", due.Short);
        Assert.Equal("today", due.Kind);
        var late = TeamTaskEngine.Due(t with { DueAt = today6.AddDays(-2) }, Ctx);
        Assert.Equal(("2 days late", true), (late.Chip, late.Late));
        Assert.Equal("", TeamTaskEngine.Due(t with { DueAt = null }, Ctx).Chip);
    }

    // ---- The v1.4 actions on a 1.3 backend ----------------------------------------------------------

    [Fact]
    public void Team_actions_turn_into_13_words_for_an_older_backend()
    {
        Assert.Equal((TaskActions.Note, (string?)null, (long?)7, (bool?)true), TaskActions.ForBackend(TaskActions.Tick, null, 7, teamTasks: false));
        Assert.Equal(TaskActions.Done, TaskActions.ForBackend(TaskActions.Finish, null, null, false).Action);
        var cant = TaskActions.ForBackend(TaskActions.Cantdo, "On leave", null, false);
        Assert.Equal((TaskActions.Help, "Can’t do it: On leave"), (cant.Action, cant.Note));
        Assert.Equal((TaskActions.Tick, (long?)7), (TaskActions.ForBackend(TaskActions.Tick, null, 7, true).Action, TaskActions.ForBackend(TaskActions.Tick, null, 7, true).StepId));
    }

    // ---- Break limit ---------------------------------------------------------------------------------

    [Fact]
    public void Break_note_shows_near_and_over_the_limit_only_when_allowed()
    {
        var rules = new WorkRules(BreakAllowedMin: 75, TellMemberOver: true);
        Assert.Null(BreakLimit.Note(rules, TimeSpan.FromMinutes(30)));
        Assert.Equal("You’ve used 1 h 10 min of your 1 h 15 min break time today.", BreakLimit.Note(rules, TimeSpan.FromMinutes(70)));
        Assert.Equal("You’ve used 1 h 30 min of your 1 h 15 min break time today. That’s 15 min over.", BreakLimit.Note(rules, TimeSpan.FromMinutes(90)));
        Assert.Null(BreakLimit.Note(rules with { TellMemberOver = false }, TimeSpan.FromMinutes(90)));
        Assert.Null(BreakLimit.Note(null, TimeSpan.FromMinutes(90)));
        Assert.Null(BreakLimit.Note(rules with { BreakAllowedMin = 0 }, TimeSpan.FromMinutes(90)));
    }

    [Fact]
    public void Heartbeat_reads_work_rules_and_older_ones_without()
    {
        const string json = """
            {"device_id": 1, "server_time": "2026-11-07T10:00:00Z", "next_heartbeat_seconds": 60, "policy_version": 3,
             "work_rules": {"days": "1111110", "start": "10:00", "end": "19:00", "break_allowed_min": 75, "tell_member_over": true, "late_grace_min": 15}}
            """;
        var beat = JsonSerializer.Deserialize(json, BackendJsonContext.Default.HeartbeatResponse)!;
        Assert.Equal(75, beat.WorkRules!.BreakAllowedMin);
        Assert.Equal("1111110", beat.WorkRules.Days);
        var old = JsonSerializer.Deserialize("""{"device_id": 1, "server_time": "2026-11-07T10:00:00Z", "next_heartbeat_seconds": 60, "policy_version": 3}""",
            BackendJsonContext.Default.HeartbeatResponse)!;
        Assert.Null(old.WorkRules);
    }
}

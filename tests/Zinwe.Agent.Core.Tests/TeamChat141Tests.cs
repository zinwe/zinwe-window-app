using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Zinwe.Agent.Core.Backend;
using Zinwe.Agent.Core.Chat;
using Zinwe.Agent.Core.Ipc;
using Zinwe.Agent.Core.Messages;
using Zinwe.Agent.Core.Work;

namespace Zinwe.Agent.Core.Tests;

/// <summary>
/// v1.4.1 · deleted groups (a 404 on one conversation drops only it; only the list's 404 turns the chat off), editing and
/// deleting your own lines through the outbox, @mentions, asking for a day off, and the heartbeat's day notes.
/// </summary>
public sealed class TeamChat141Tests : IDisposable
{
    private const string TwoGroups = """
        [{"id": 3, "kind": "group", "name": "Design", "unread": 1, "pinned": true, "muted": false,
          "members": [{"member_id": 4, "name": "Priya Nair"}, {"member_id": 9, "name": "Emily Carter"}]},
         {"id": 5, "kind": "group", "name": "Development", "unread": 0}]
        """;

    private const string Lines = """
        [{"id": 90, "by": {"kind": "member", "member_id": 9, "name": "Emily Carter"}, "body": "@Priya can you check?", "at": "2026-11-08T09:58:00Z"},
         {"id": 91, "by": {"kind": "member", "member_id": 4, "name": "Priya Nair"}, "body": "On it", "at": "2026-11-08T09:59:00Z", "mine": true}]
        """;

    private readonly TempDir _dir = new();
    private readonly FakeBackend _backend = new();
    private DateTimeOffset _now = new(2026, 11, 8, 10, 0, 0, TimeSpan.Zero);

    public void Dispose() => _dir.Dispose();

    private MessageOutbox Outbox() => new(_dir.Path);

    private TeamChatSync Sync(MessageOutbox? outbox = null) =>
        new(_backend.Client(), outbox ?? Outbox(), NullLogger<TeamChatSync>.Instance, () => _now);

    private int Calls(string method, string path) => _backend.Requests.Count(r => r.Method.Method == method && r.Path == path);

    /// <summary>The backend: <paramref name="list"/> for GET chat/, and <paramref name="deleted"/> answering 404 on its own routes.</summary>
    private void Serve(string list = TwoGroups, long? deleted = null, Func<SeenRequest, HttpResponseMessage?>? more = null)
    {
        _backend.Respond = r =>
        {
            if (more?.Invoke(r) is { } custom)
            {
                return custom;
            }
            if (deleted is { } d && r.Path.StartsWith($"/api/devices/chat/{d}/", StringComparison.Ordinal))
            {
                return FakeBackend.Json(HttpStatusCode.NotFound, """{"detail": "Not found."}""");
            }
            return r.Path switch
            {
                "/api/devices/chat/" => FakeBackend.Json(HttpStatusCode.OK, list),
                "/api/devices/chat/people/" => FakeBackend.Json(HttpStatusCode.OK, "[]"),
                "/api/devices/leave/" => FakeBackend.Json(HttpStatusCode.OK, "[]"),
                var p when p.EndsWith("/messages/", StringComparison.Ordinal) && r.Method == HttpMethod.Get => FakeBackend.Json(HttpStatusCode.OK, Lines),
                _ => FakeBackend.Json(HttpStatusCode.OK, "{}"),
            };
        };
    }

    // ---- Deleted groups -----------------------------------------------------------------------------

    [Fact]
    public async Task A_404_on_the_open_thread_drops_only_that_group_and_keeps_the_chat_on()
    {
        Serve(deleted: 3);
        var outbox = Outbox();
        outbox.AppendChat(OutboxReply.ChatPin, "conv:5");
        var sync = Sync(outbox);
        await sync.SyncAsync("tok", CancellationToken.None); // the list, with both groups
        outbox.AppendChat(OutboxReply.ChatMute, "conv:3"); // queued while it was being deleted... posted next round
        sync.Focus("conv:3");
        _now += TimeSpan.FromSeconds(1);
        await sync.SyncAsync("tok", CancellationToken.None);

        var snap = sync.Snapshot;
        Assert.Equal(TaskBackend.On, snap.Backend);
        Assert.Equal([5L], snap.Conversations!.Select(c => c.Id));
        Assert.Equal([3L], snap.Gone!);
        Assert.Null(snap.Thread);
        Assert.Empty(outbox.PendingChat());

        // Not "chat off": the list is asked again at its usual 15 s pace, not after 10 minutes.
        var lists = Calls("GET", "/api/devices/chat/");
        _now += TeamChatSync.ListEvery;
        await sync.SyncAsync("tok", CancellationToken.None);
        Assert.Equal(lists + 1, Calls("GET", "/api/devices/chat/"));
    }

    [Fact]
    public async Task A_line_sent_to_a_deleted_group_is_dropped_with_the_rest_of_its_queue_and_others_still_go()
    {
        Serve(deleted: 3);
        var outbox = Outbox();
        outbox.AppendChat(OutboxReply.ChatSend, "conv:3", "Hello?");
        outbox.AppendChat(OutboxReply.ChatSend, "conv:3", "Anyone?");
        outbox.AppendChat(OutboxReply.ChatSend, "conv:5", "Build is green");
        var sync = Sync(outbox);
        await sync.SyncAsync("tok", CancellationToken.None);

        Assert.Equal(1, Calls("POST", "/api/devices/chat/3/messages/")); // the second one wasn't even tried
        Assert.Equal(1, Calls("POST", "/api/devices/chat/5/messages/"));
        Assert.Empty(outbox.PendingChat());
        Assert.Contains(3L, sync.Snapshot.Gone!);
        Assert.Equal(TaskBackend.On, sync.Snapshot.Backend);
    }

    [Fact]
    public async Task A_read_mark_404_drops_the_group()
    {
        Serve(more: r => r.Path == "/api/devices/chat/3/read/" ? FakeBackend.Json(HttpStatusCode.NotFound, "{}") : null);
        var sync = Sync();
        sync.Focus("conv:3");
        await sync.SyncAsync("tok", CancellationToken.None);
        Assert.Contains(3L, sync.Snapshot.Gone!);
        Assert.DoesNotContain(sync.Snapshot.Conversations!, c => c.Id == 3);
    }

    [Fact]
    public async Task A_group_gone_from_the_list_is_gone_but_an_empty_list_deletes_nothing()
    {
        Serve();
        var sync = Sync();
        await sync.SyncAsync("tok", CancellationToken.None);
        Serve(list: """[{"id": 5, "kind": "group", "name": "Development"}]""");
        _now += TeamChatSync.ListEvery;
        await sync.SyncAsync("tok", CancellationToken.None);
        Assert.Equal([3L], sync.Snapshot.Gone!);

        Serve(list: "[]"); // a plan without team chat: [] for a device
        _now += TeamChatSync.ListEvery;
        await sync.SyncAsync("tok", CancellationToken.None);
        Assert.Equal([3L], sync.Snapshot.Gone!);
    }

    [Fact]
    public async Task Only_the_list_404_turns_the_chat_off()
    {
        _backend.Respond = _ => FakeBackend.Json(HttpStatusCode.NotFound, "{}");
        var sync = Sync();
        sync.Focus("conv:3");
        await sync.SyncAsync("tok", CancellationToken.None);
        Assert.Equal(TaskBackend.Off, sync.Snapshot.Backend);
        Assert.Empty(sync.Snapshot.Gone!); // nothing was "deleted": the chat isn't there at all
        Assert.Equal(0, Calls("GET", "/api/devices/chat/3/messages/"));
    }

    [Fact]
    public async Task The_client_tells_a_deleted_conversation_from_a_missing_feature()
    {
        _backend.Respond = _ => FakeBackend.Json(HttpStatusCode.NotFound, "{}");
        var client = _backend.Client();
        Assert.True((await client.FetchConversationAsync("tok", 3, CancellationToken.None)).Gone);
        Assert.Equal(ChatWriteOutcome.Gone, await client.MarkConversationReadAsync("tok", 3, CancellationToken.None));
        Assert.Equal(ChatWriteOutcome.Gone, await client.PostConversationMessageAsync("tok", 3, "Hi", null, CancellationToken.None));
        _backend.Respond = _ => FakeBackend.Json(HttpStatusCode.Forbidden, "{}");
        Assert.False((await client.FetchConversationAsync("tok", 3, CancellationToken.None)).Gone);
        Assert.Equal(ChatWriteOutcome.Off, await client.PostConversationMessageAsync("tok", 3, "Hi", null, CancellationToken.None));
    }

    // ---- Edit and delete ----------------------------------------------------------------------------

    [Fact]
    public async Task Edit_and_delete_go_through_the_outbox_and_show_at_once()
    {
        Serve(more: r => r.Method == HttpMethod.Patch && r.Path == "/api/devices/chat/3/messages/91/"
            ? FakeBackend.Json(HttpStatusCode.OK, """{"id": 91, "by": {"kind": "member", "member_id": 4, "name": "Priya Nair"}, "body": "On it now", "edited": true, "at": "2026-11-08T09:59:00Z"}""")
            : r.Method == HttpMethod.Delete ? FakeBackend.Json(HttpStatusCode.OK, """{"id": 90, "body": "", "deleted": true, "at": "2026-11-08T09:58:00Z"}""") : null);
        var outbox = Outbox();
        var sync = Sync(outbox);
        sync.Focus("conv:3");
        await sync.SyncAsync("tok", CancellationToken.None);

        outbox.AppendChat(OutboxReply.ChatEdit, "conv:3", " On it now ", messageId: 91);
        outbox.AppendChat(OutboxReply.ChatDelete, "conv:3", messageId: 90);
        Serve(more: r => r.Method == HttpMethod.Get && r.Path == "/api/devices/chat/3/messages/"
            ? FakeBackend.Json(HttpStatusCode.ServiceUnavailable, "{}") // the re-pull fails: what was applied still shows
            : r.Method == HttpMethod.Patch ? FakeBackend.Json(HttpStatusCode.OK, """{"id": 91, "body": "On it now", "edited": true, "at": "2026-11-08T09:59:00Z"}""")
            : r.Method == HttpMethod.Delete ? FakeBackend.Json(HttpStatusCode.NoContent, "") : null);
        sync.Focus("conv:3");
        await sync.SyncAsync("tok", CancellationToken.None);

        var patch = _backend.Requests.Single(r => r.Method == HttpMethod.Patch);
        Assert.Equal(("/api/devices/chat/3/messages/91/", "{\"body\":\"On it now\"}"), (patch.Path, patch.Body));
        Assert.Equal(1, Calls("DELETE", "/api/devices/chat/3/messages/90/"));
        var lines = sync.Snapshot.Thread!.Messages!;
        Assert.Equal(("On it now", true, true), (lines.Single(m => m.Id == 91).Body, lines.Single(m => m.Id == 91).Edited, lines.Single(m => m.Id == 91).Mine == true));
        Assert.Equal((true, ""), (lines.Single(m => m.Id == 90).Deleted, lines.Single(m => m.Id == 90).Body));
        Assert.Empty(outbox.PendingChat());
    }

    [Fact]
    public async Task An_edit_after_15_minutes_comes_back_as_a_notice_in_the_servers_words()
    {
        Serve(more: r => r.Method == HttpMethod.Patch
            ? FakeBackend.Json(HttpStatusCode.BadRequest, """{"body": ["You can change a message for 15 minutes after sending it."]}""") : null);
        var outbox = Outbox();
        var item = outbox.AppendChat(OutboxReply.ChatEdit, "conv:3", "Late fix", messageId: 91);
        var sync = Sync(outbox);
        await sync.SyncAsync("tok", CancellationToken.None);

        var notice = sync.Snapshot.Notices!.Single();
        Assert.Equal((item.Id, "conv:3", "edit", "You can change a message for 15 minutes after sending it."),
            (notice.Id, notice.Key, notice.Action, notice.Text));
        Assert.Empty(outbox.PendingChat());
        _now += TeamChatSync.NoticeKeep + TimeSpan.FromSeconds(1);
        await sync.SyncAsync("tok", CancellationToken.None);
        Assert.Empty(sync.Snapshot.Notices!);
    }

    [Fact]
    public async Task A_404_on_a_line_keeps_a_chat_that_is_still_listed()
    {
        Serve(more: r => r.Method == HttpMethod.Delete ? FakeBackend.Json(HttpStatusCode.NotFound, "{}") : null);
        var outbox = Outbox();
        outbox.AppendChat(OutboxReply.ChatDelete, "conv:3", messageId: 4242);
        var sync = Sync(outbox);
        await sync.SyncAsync("tok", CancellationToken.None);
        Assert.Empty(sync.Snapshot.Gone!);
        Assert.Contains(sync.Snapshot.Conversations!, c => c.Id == 3);
        Assert.Equal("That message couldn’t be deleted.", sync.Snapshot.Notices!.Single().Text);
    }

    [Fact]
    public void Own_lines_can_be_changed_for_15_minutes_only()
    {
        var sent = new TeamMessage(91, Body: "On it", At: _now);
        Assert.True(ChatEdits.CanChange(sent, mine: true, _now.AddMinutes(14).AddSeconds(59)));
        Assert.False(ChatEdits.CanChange(sent, mine: true, _now.AddMinutes(15)));
        Assert.False(ChatEdits.CanChange(sent, mine: false, _now));
        Assert.False(ChatEdits.CanChange(sent with { Deleted = true }, mine: true, _now));
        Assert.False(ChatEdits.CanChange(sent with { TaskId = 6 }, mine: true, _now));
        Assert.False(ChatEdits.CanChange(sent with { System = true }, mine: true, _now));
        Assert.False(ChatEdits.CanChange(sent with { Id = 0 }, mine: true, _now)); // still sending
        Assert.Equal(TimeSpan.FromMinutes(5), ChatEdits.Left(sent, _now.AddMinutes(10)));
        Assert.Equal(TimeSpan.Zero, ChatEdits.Left(sent, _now.AddHours(1)));
    }

    // ---- Pins and mutes -----------------------------------------------------------------------------

    [Fact]
    public async Task A_refused_pin_says_why_in_the_servers_words()
    {
        Serve(more: r => r.Method == HttpMethod.Patch && r.Body.Contains("pinned", StringComparison.Ordinal)
            ? FakeBackend.Json(HttpStatusCode.BadRequest, """{"pinned": ["You can pin up to 3 chats."]}""") : null);
        var outbox = Outbox();
        outbox.AppendChat(OutboxReply.ChatPin, "conv:5");
        outbox.AppendChat(OutboxReply.ChatMute, "conv:5");
        var sync = Sync(outbox);
        await sync.SyncAsync("tok", CancellationToken.None);
        Assert.Equal("You can pin up to 3 chats.", sync.Snapshot.Notices!.Single().Text);
        Assert.Equal(2, Calls("PATCH", "/api/devices/chat/5/"));
        var dev = sync.Snapshot.Conversations!.Single(c => c.Id == 5);
        Assert.False(dev.Pinned);
    }

    // ---- Day off ------------------------------------------------------------------------------------

    private const string Asks = """
        [{"id": 4, "member_id": 4, "date": "2026-11-12", "half": true, "status": "requested", "note": "Family function"},
         {"id": 2, "date": "2026-11-02", "half": false, "status": "approved", "note": ""}]
        """;

    [Fact]
    public async Task Asking_for_a_day_off_posts_it_and_pulls_the_asks_back()
    {
        var asked = false;
        Serve(more: r => r.Path != "/api/devices/leave/" ? null
            : r.Method == HttpMethod.Post ? Created()
            : FakeBackend.Json(HttpStatusCode.OK, asked ? Asks : "[]"));
        HttpResponseMessage Created()
        {
            asked = true;
            return FakeBackend.Json(HttpStatusCode.Created, """{"id": 4}""");
        }
        var outbox = Outbox();
        var sync = Sync(outbox);
        await sync.SyncAsync("tok", CancellationToken.None);
        Assert.Empty(sync.Snapshot.Leave!);

        outbox.AppendLeave(LeaveDays.Request(new DateOnly(2026, 11, 12), half: true, " Family function "));
        _now += TimeSpan.FromSeconds(2);
        await sync.SyncAsync("tok", CancellationToken.None);

        var post = _backend.Requests.Single(r => r.Method == HttpMethod.Post && r.Path == "/api/devices/leave/");
        Assert.Equal("{\"date\":\"2026-11-12\",\"half\":true,\"note\":\"Family function\"}", post.Body);
        var asks = sync.Snapshot.Leave!;
        Assert.Equal(2, asks.Count);
        Assert.Equal((4L, true, "requested", "Family function"), (asks[0].Id, asks[0].Half, asks[0].Status, asks[0].Note));
        Assert.Empty(outbox.PendingChat());
    }

    [Fact]
    public async Task A_backend_without_leave_shows_no_asks_and_a_refused_ask_says_why()
    {
        Serve(more: r => r.Path != "/api/devices/leave/" ? null
            : r.Method == HttpMethod.Post ? FakeBackend.Json(HttpStatusCode.BadRequest, """{"date": ["Pick today or a later day."]}""")
            : FakeBackend.Json(HttpStatusCode.NotFound, "{}"));
        var outbox = Outbox();
        outbox.AppendLeave(new LeaveRequest("2026-11-01", false, ""));
        var sync = Sync(outbox);
        await sync.SyncAsync("tok", CancellationToken.None);
        Assert.Null(sync.Snapshot.Leave);
        Assert.Equal("Pick today or a later day.", sync.Snapshot.Notices!.Single().Text);

        // Asked again only after a while (not every second).
        var gets = Calls("GET", "/api/devices/leave/");
        _now += TimeSpan.FromMinutes(1);
        await sync.SyncAsync("tok", CancellationToken.None);
        Assert.Equal(gets, Calls("GET", "/api/devices/leave/"));
    }

    [Fact]
    public async Task The_day_off_sheet_pulls_the_asks_at_once()
    {
        Serve();
        var sync = Sync();
        await sync.SyncAsync("tok", CancellationToken.None);
        _now += TimeSpan.FromSeconds(20);
        sync.Focus(TeamChatSync.LeaveFocus);
        Assert.True(sync.Due);
        await sync.SyncAsync("tok", CancellationToken.None);
        Assert.Equal(2, Calls("GET", "/api/devices/leave/"));
        Assert.Null(sync.Snapshot.Thread);
    }

    [Fact]
    public void Leave_words_and_rules()
    {
        var today = new DateOnly(2026, 11, 8);
        Assert.Equal("Pick a day in the last 30 days or later.", LeaveDays.Check(today.AddDays(-31), today));
        Assert.Null(LeaveDays.Check(today.AddDays(-30), today));
        Assert.Null(LeaveDays.Check(today.AddDays(-1), today));
        Assert.Equal("Yesterday · Full day", LeaveDays.Label(new LeaveAsk(1, today.AddDays(-1).ToString("yyyy-MM-dd")), today));
        Assert.Null(LeaveDays.Check(today, today));
        Assert.Equal("Pick a day.", LeaveDays.Check(null, today));
        Assert.Equal(("Waiting", "Approved", "Declined"), (LeaveDays.StatusLabel("requested"), LeaveDays.StatusLabel("APPROVED"), LeaveDays.StatusLabel("declined")));
        Assert.Equal("Tomorrow · Half day", LeaveDays.Label(new LeaveAsk(1, "2026-11-09", true), today));
        Assert.Equal("Thu, 12 Nov · Full day", LeaveDays.Label(new LeaveAsk(1, "2026-11-12"), today));
        var recent = LeaveDays.Recent([new LeaveAsk(1, "2026-11-01"), new LeaveAsk(2, "2026-11-20"), new LeaveAsk(3, "2026-11-10"), new LeaveAsk(4, "2026-11-05")], today);
        Assert.Equal([3L, 2L, 4L, 1L], recent.Select(a => a.Id));
        Assert.Empty(LeaveJson.Asks("<html>"));
        Assert.Single(LeaveJson.Asks("""{"results": [{"id": "7", "date": "2026-11-12"}, {"oops": 1}]}"""));
    }

    [Fact]
    public void The_heartbeat_says_holiday_and_leave_today()
    {
        const string json = """
            {"device_id": 1, "server_time": "2026-11-09T10:00:00Z", "next_heartbeat_seconds": 60, "policy_version": 3,
             "work_rules": {"days": "1111110", "start": "10:00", "end": "19:00", "break_allowed_min": 75,
                            "working_today": false, "holiday_today": "Diwali", "leave_today": null}}
            """;
        var rules = JsonSerializer.Deserialize(json, BackendJsonContext.Default.HeartbeatResponse)!.WorkRules!;
        Assert.Equal((false, "Diwali"), (rules.WorkingToday, rules.HolidayToday));
        Assert.Equal("Today is a holiday: Diwali. Enjoy the day.", LeaveDays.TodayNote(rules));
        Assert.Equal("You have the day off today. Enjoy it.", LeaveDays.TodayNote(new WorkRules(LeaveToday: "full")));
        Assert.Equal("You have half the day off today.", LeaveDays.TodayNote(new WorkRules(LeaveToday: "half")));
        Assert.Equal("Today isn’t a working day for you.", LeaveDays.TodayNote(new WorkRules(WorkingToday: false)));
        Assert.Null(LeaveDays.TodayNote(new WorkRules(WorkingToday: true)));
        Assert.Null(LeaveDays.TodayNote(null));
    }

    // ---- @mentions ----------------------------------------------------------------------------------

    private static readonly string[] Names = ["Priya Nair", "Emily Carter", "Ananya Rao", "Priyanka Shah"];

    [Fact]
    public void Mentions_find_full_and_first_names_but_not_inside_words_or_emails()
    {
        var spans = Mentions.Find("Hi @Priya Nair and @emily, ask @Ananya. Not mail@Priya.com, @Priyanka too, @Zed", Names);
        Assert.Equal(["Priya Nair", "emily", "Ananya", "Priyanka", "Zed"], spans.Select(s => s.Name));
        Assert.Equal((3, 11), (spans[0].Start, spans[0].Length));
        Assert.Empty(Mentions.Find("email me @ 5", Names));
        Assert.Empty(Mentions.Find("@lowercase stranger", Names));
    }

    [Fact]
    public void A_line_mentions_me_by_full_or_first_name_only()
    {
        Assert.True(Mentions.MentionsMe("@Priya Nair can you check?", "Priya Nair"));
        Assert.True(Mentions.MentionsMe("thanks @priya!", "Priya Nair"));
        Assert.False(Mentions.MentionsMe("@Priyanka can you check?", "Priya Nair"));
        Assert.False(Mentions.MentionsMe("Priya, can you check?", "Priya Nair"));
        Assert.False(Mentions.MentionsMe("@Priya", null));
    }

    [Fact]
    public void The_composer_finds_the_name_being_typed_suggests_and_inserts()
    {
        Assert.Equal((4, "Em"), Mentions.Query("Hey @Em", 7)!.Value);
        Assert.Equal((0, ""), Mentions.Query("@", 1)!.Value);
        Assert.Equal((0, "Priya N"), Mentions.Query("@Priya N", 8)!.Value);
        Assert.Null(Mentions.Query("@Priya Nair ", 12)); // finished
        Assert.Null(Mentions.Query("mail@Pri", 8)); // an email
        Assert.Null(Mentions.Query("@Pri\nya", 7));
        Assert.Null(Mentions.Query("no at here", 5));

        var people = Names.Select((n, i) => new TeamMember(i + 1, n)).ToList();
        Assert.Equal(["Priya Nair", "Priyanka Shah"], Mentions.Suggest("pri", people).Select(p => p.Name));
        Assert.Equal(["Emily Carter"], Mentions.Suggest("car", people).Select(p => p.Name));
        Assert.Equal(4, Mentions.Suggest("", people).Count);
        Assert.Empty(Mentions.Suggest("zed", people));

        var (text, caret) = Mentions.Insert("Hey @Em, look", 4, 7, "Emily Carter");
        Assert.Equal(("Hey @Emily Carter , look", 18), (text, caret));
        Assert.Equal(("@Priya Nair ", 12), Mentions.Insert("@pr", 0, 3, "Priya Nair"));
    }

    // ---- The pipe -----------------------------------------------------------------------------------

    [Fact]
    public void The_141_parts_cross_the_pipe_and_a_140_reply_reads_as_none()
    {
        var reply = new ChatReply(true, TaskBackend.On, [], [], Version: 2, Gone: [3], Leave: [new LeaveAsk(4, "2026-11-12", true, "approved")],
            Notices: [new ChatNotice("abc", "conv:3", "edit", "You can change a message for 15 minutes after sending it.")]);
        var back = AgentPipeClient.ParseChat(PipeProtocol.Serialize(reply))!;
        Assert.Equal([3L], back.Gone!);
        Assert.Equal(("approved", true), (back.Leave!.Single().Status, back.Leave!.Single().Half));
        Assert.Equal("abc", back.Notices!.Single().Id);
        var old = AgentPipeClient.ParseChat("""{"supported": true, "backend": "on", "version": 3}""")!;
        Assert.Null(old.Gone);
        Assert.Null(old.Leave);
        Assert.Null(old.Notices);
    }
}

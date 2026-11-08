using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Zinwe.Agent.Core.Backend;
using Zinwe.Agent.Core.Chat;
using Zinwe.Agent.Core.Ipc;
using Zinwe.Agent.Core.Messages;

namespace Zinwe.Agent.Core.Tests;

/// <summary>
/// v1.4 · team chat on the device: the lenient JSON reader, the client calls (fake HTTP), the service's sync (outbox out,
/// list + open thread in, read marks, the 3 s / 15 s cadence) and the graceful fallback to an older backend.
/// </summary>
public sealed class TeamChatTests : IDisposable
{
    private const string Convs = """
        [{"id": 3, "kind": "group", "name": "Design", "announce": false, "unread": 2,
          "members": [{"member_id": 1, "name": "Ananya Rao"}, {"member_id": 6, "name": "Emily Carter"}],
          "last_message": {"id": 90, "by": {"kind": "member", "member_id": 6, "name": "Emily Carter"}, "body": "Hero options are in Figma",
                           "at": "2026-11-07T11:02:00+05:30"}},
         {"id": 7, "kind": "direct", "with": {"member_id": 5, "name": "Karan Malhotra"}, "unread_count": "0",
          "last_message_at": "2026-11-07T13:06:00+05:30"},
         {"id": 9, "kind": "group", "name": "Everyone at Kitecrest", "announce": true}]
        """;

    private const string Msgs = """
        {"results": [
          {"id": 92, "by": {"kind": "owner", "member_id": null, "name": "Ananya Rao"}, "body": "Option B.", "at": "2026-11-07T11:10:00+05:30",
           "reply_to": {"id": 90, "by": {"name": "Emily Carter"}, "body": "Hero options"}, "seen_by": ["Emily", "Fatima"]},
          {"id": 90, "by": {"kind": "member", "member_id": 6, "name": "Emily Carter"}, "body": "Hero options", "at": "2026-11-07T11:02:00+05:30"},
          {"id": 93, "by": {"kind": "member", "member_id": 4, "name": "Priya Nair"}, "body": "On it", "at": "2026-11-07T11:14:00+05:30",
           "mine": true, "seen_by": 3, "edited": true},
          {"id": 94, "by": "owner", "body": "", "task_id": 24, "at": "2026-11-07T14:48:00+05:30"},
          {"oops": true}
        ]}
        """;

    private readonly TempDir _dir = new();
    private readonly FakeBackend _backend = new();
    private DateTimeOffset _now = new(2026, 11, 7, 10, 0, 0, TimeSpan.Zero);

    public void Dispose() => _dir.Dispose();

    private TeamChatSync Sync(MessageOutbox? outbox = null) =>
        new(_backend.Client(), outbox ?? new MessageOutbox(_dir.Path), NullLogger<TeamChatSync>.Instance, () => _now);

    private int Calls(string method, string path) => _backend.Requests.Count(r => r.Method.Method == method && r.Path == path);

    // ---- Reading -------------------------------------------------------------------------------------

    [Fact]
    public void Conversations_read_groups_directs_and_announce()
    {
        var list = ChatJson.Conversations(Convs);
        Assert.Equal([3L, 7L, 9L], list.Select(c => c.Id));
        Assert.True(list[0].IsGroup);
        Assert.Equal(2, list[0].Unread);
        Assert.Equal("Emily Carter", list[0].Last!.ByName);
        Assert.False(list[1].IsGroup);
        Assert.Equal(("Karan Malhotra", 5L), (list[1].Name, list[1].WithMemberId!.Value));
        Assert.NotNull(list[1].LastAt);
        Assert.True(list[2].Announce);
    }

    [Fact]
    public void Messages_read_oldest_first_with_replies_seen_by_tasks_and_skip_garbage()
    {
        var m = ChatJson.Messages(Msgs);
        Assert.Equal([90L, 92L, 93L, 94L], m.Select(x => x.Id));
        Assert.True(m[1].ByOwner);
        Assert.Equal(("Emily Carter", "Hero options"), (m[1].ReplyToBy, m[1].ReplyToBody));
        Assert.Equal((2, "Emily"), (m[1].SeenBy, m[1].SeenNames![0]));
        Assert.Equal((true, 3, true), (m[2].Mine, m[2].SeenBy, m[2].Edited));
        Assert.Equal(24, m[3].TaskId);
        Assert.Equal("owner", m[3].ByKind);
    }

    [Fact]
    public void Odd_json_reads_as_nothing_not_an_error()
    {
        Assert.Empty(ChatJson.Conversations("<html>"));
        Assert.Empty(ChatJson.Messages("{\"detail\": \"x\"}"));
        Assert.Equal(("conv", 12L), ChatKeys.Parse("conv:12")!.Value);
        Assert.Null(ChatKeys.Parse("conv:x"));
        Assert.Null(ChatKeys.Parse("nope"));
    }

    [Fact]
    public void The_api_v14_shapes_read_last_lines_directs_and_seen_by_objects()
    {
        var list = ChatJson.Conversations("""
            [{"id": 3, "kind": "group", "name": "Design", "announce": false, "member_id": null, "members": [{"member_id": 9, "name": "Emily Carter", "role": "designer"}],
              "unread": 1, "last": {"text": "Hero options are in Figma", "by": "Emily", "at": "2026-11-07T05:32:00Z", "task_id": null},
              "last_message_at": "2026-11-07T05:32:00Z", "pinned": true, "muted": false, "read_only": false, "can_post": true},
             {"id": 14, "kind": "direct", "name": "Karan Malhotra", "member_id": 7, "members": [], "unread": 0,
              "last": {"text": "", "by": "", "at": "2026-11-07T05:00:00Z", "task_id": 6}, "can_post": true},
             {"id": 2, "kind": "group", "name": "Everyone", "announce": true, "can_post": false}]
            """);
        Assert.Equal(("Hero options are in Figma", "Emily"), (list[0].Last!.Body, list[0].Last!.ByName));
        Assert.True(list[0].Pinned);
        Assert.Equal((false, 7L), (list[1].IsGroup, list[1].WithMemberId!.Value));
        Assert.Equal(6, list[1].Last!.TaskId);
        Assert.True(list[2].Announce);
        Assert.False(list[2].ReadOnly); // announce: read-only for members comes from Announce, not from left the group
        var line = ChatJson.Messages("""
            [{"id": 88, "by": {"kind": "member", "member_id": 7, "name": "Karan Malhotra"}, "body": "Starting after lunch.",
              "reply_to": {"id": 85, "by": {"kind": "owner", "member_id": null, "name": "Ananya Rao"}, "body": "Over to you."},
              "task_id": null, "system": false, "edited": false, "deleted": false, "at": "2026-11-07T06:40:00Z",
              "seen_by": [{"member_id": null, "name": "Ananya Rao"}, {"member_id": 4, "name": "Priya Nair"}]}]
            """).Single();
        Assert.Equal((2, "Ananya Rao", "Ananya Rao"), (line.SeenBy, line.SeenNames![0], line.ReplyToBy));
    }

    [Fact]
    public async Task Pin_mute_and_unread_go_to_the_backend()
    {
        var outbox = new MessageOutbox(_dir.Path);
        outbox.AppendChat(OutboxReply.ChatPin, "conv:3");
        outbox.AppendChat(OutboxReply.ChatMute, "conv:3");
        outbox.AppendChat(OutboxReply.ChatUnread, "conv:3");
        _backend.Respond = r => r.Path == "/api/devices/chat/" ? FakeBackend.Json(HttpStatusCode.OK, Convs) : FakeBackend.Json(HttpStatusCode.OK, "{}");
        await Sync(outbox).SyncAsync("tok", CancellationToken.None);
        var patches = _backend.Requests.Where(r => r.Method == HttpMethod.Patch).Select(r => (r.Path, r.Body)).ToList();
        Assert.Equal([("/api/devices/chat/3/", "{\"pinned\":true}"), ("/api/devices/chat/3/", "{\"muted\":true}"), ("/api/devices/chat/3/", "{\"unread\":true}")], patches);
        Assert.Empty(outbox.PendingChat());
    }

    // ---- The client ----------------------------------------------------------------------------------

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.MethodNotAllowed)]
    public async Task An_older_backend_has_no_team_chat(HttpStatusCode code)
    {
        _backend.Respond = _ => FakeBackend.Json(code, "{}");
        var client = _backend.Client();
        Assert.Null(await client.GetConversationsAsync("tok", CancellationToken.None));
        Assert.Null(await client.GetTaskChatAsync("tok", 6, CancellationToken.None));
        Assert.Equal(ChatWriteOutcome.Off, await client.PostTaskChatAsync("tok", 6, "Hi", CancellationToken.None));
    }

    [Fact]
    public async Task Posting_a_line_sends_body_and_reply_to_with_the_device_token()
    {
        _backend.Respond = _ => FakeBackend.Json(HttpStatusCode.Created, "{}");
        Assert.Equal(ChatWriteOutcome.Done, await _backend.Client().PostConversationMessageAsync("tok", 3, " On it ", 90, CancellationToken.None));
        var seen = _backend.Requests.Single();
        Assert.Equal(("/api/devices/chat/3/messages/", "Device"), (seen.Path, seen.AuthScheme));
        Assert.Equal("{\"body\":\"On it\",\"reply_to\":90}", seen.Body);
    }

    [Fact]
    public async Task A_refused_line_is_refused_and_a_server_error_throws()
    {
        _backend.Respond = _ => FakeBackend.Json(HttpStatusCode.BadRequest, "{\"body\": [\"Only the owner posts here.\"]}");
        Assert.Equal(ChatWriteOutcome.Refused, await _backend.Client().PostConversationMessageAsync("tok", 9, "Hi", null, CancellationToken.None));
        _backend.Respond = _ => FakeBackend.Json(HttpStatusCode.InternalServerError, "{}");
        await Assert.ThrowsAsync<HttpRequestException>(() => _backend.Client().PostConversationMessageAsync("tok", 9, "Hi", null, CancellationToken.None));
    }

    // ---- The service's sync -------------------------------------------------------------------------

    private void Serve(string? convs = Convs, string msgs = Msgs, string taskChat = "[]")
    {
        _backend.Respond = r => r.Path switch
        {
            "/api/devices/chat/" when convs is null => FakeBackend.Json(HttpStatusCode.NotFound, "{}"),
            "/api/devices/chat/" => FakeBackend.Json(HttpStatusCode.OK, convs),
            "/api/devices/chat/people/" => FakeBackend.Json(HttpStatusCode.OK, """[{"member_id": 9, "name": "Fatima Sheikh", "role": "design"}]"""),
            var p when p.EndsWith("/messages/", StringComparison.Ordinal) && r.Method == HttpMethod.Get => FakeBackend.Json(HttpStatusCode.OK, msgs),
            var p when p.EndsWith("/chat/", StringComparison.Ordinal) && p.StartsWith("/api/devices/tasks/", StringComparison.Ordinal) && r.Method == HttpMethod.Get
                => taskChat == "404" ? FakeBackend.Json(HttpStatusCode.NotFound, "{}") : FakeBackend.Json(HttpStatusCode.OK, taskChat),
            var p when p.Contains("/tasks/", StringComparison.Ordinal) && p.EndsWith("/chat/", StringComparison.Ordinal) && taskChat == "404"
                => FakeBackend.Json(HttpStatusCode.NotFound, "{}"),
            _ => FakeBackend.Json(HttpStatusCode.OK, "{}"),
        };
    }

    [Fact]
    public async Task Sync_pulls_the_list_and_people_then_the_focused_thread_and_marks_it_read()
    {
        Serve();
        var sync = Sync();
        sync.Focus("conv:3");
        await sync.SyncAsync("tok", CancellationToken.None);

        var snap = sync.Snapshot;
        Assert.Equal(TaskBackend.On, snap.Backend);
        Assert.Equal(3, snap.Conversations!.Count);
        Assert.Equal("Fatima Sheikh", snap.People!.Single().Name);
        Assert.Equal("conv:3", snap.Thread!.Key);
        Assert.Equal(4, snap.Thread.Messages!.Count);
        Assert.Equal(1, Calls("POST", "/api/devices/chat/3/read/"));
        Assert.Equal(0, snap.Conversations!.First(c => c.Id == 3).Unread); // read as soon as we told the backend
        Assert.True(snap.Version > 0);
    }

    [Fact]
    public async Task Open_thread_is_pulled_every_3_s_and_the_list_every_15_s()
    {
        Serve();
        var sync = Sync();
        sync.Focus("conv:3");
        await sync.SyncAsync("tok", CancellationToken.None);
        _now += TimeSpan.FromSeconds(3);
        sync.Focus("conv:3");
        Assert.True(sync.Due);
        await sync.SyncAsync("tok", CancellationToken.None);
        Assert.Equal(2, Calls("GET", "/api/devices/chat/3/messages/"));
        Assert.Equal(1, Calls("GET", "/api/devices/chat/"));
        _now += TimeSpan.FromSeconds(13);
        await sync.SyncAsync("tok", CancellationToken.None);
        Assert.Equal(2, Calls("GET", "/api/devices/chat/"));
        Assert.Equal(2, Calls("GET", "/api/devices/chat/3/messages/")); // the tray stopped asking: the focus went stale
        Assert.Null(sync.Snapshot.Thread);
    }

    [Fact]
    public async Task Queued_lines_reports_and_direct_chats_are_posted_in_order()
    {
        Serve();
        var outbox = new MessageOutbox(_dir.Path);
        outbox.AppendChat(OutboxReply.ChatSend, "conv:3", "On it", messageId: 92);
        outbox.AppendChat(OutboxReply.ChatReport, "conv:7", "Rude messages", messageId: 55);
        outbox.AppendChat(OutboxReply.ChatSend, "direct:9", "Hi Fatima");
        _backend.Respond = r => r.Path switch
        {
            "/api/devices/chat/direct/" => FakeBackend.Json(HttpStatusCode.Created, """{"id": 12, "kind": "direct", "with": {"member_id": 9, "name": "Fatima Sheikh"}}"""),
            "/api/devices/chat/" => FakeBackend.Json(HttpStatusCode.OK, Convs),
            _ => FakeBackend.Json(HttpStatusCode.OK, "[]"),
        };
        var sync = Sync(outbox);
        await sync.SyncAsync("tok", CancellationToken.None);

        var posts = _backend.Requests.Where(r => r.Method == HttpMethod.Post).Select(r => (r.Path, r.Body)).ToList();
        Assert.Equal(("/api/devices/chat/3/messages/", "{\"body\":\"On it\",\"reply_to\":92}"), posts[0]);
        Assert.Equal(("/api/devices/chat/7/report/", "{\"note\":\"Rude messages\",\"message_id\":55}"), posts[1]);
        Assert.Equal(("/api/devices/chat/direct/", "{\"member_id\":9}"), posts[2]);
        Assert.Equal(("/api/devices/chat/12/messages/", "{\"body\":\"Hi Fatima\"}"), posts[3]);
        Assert.Empty(outbox.PendingChat());
    }

    [Fact]
    public async Task A_network_error_keeps_the_queue_for_later()
    {
        var outbox = new MessageOutbox(_dir.Path);
        outbox.AppendChat(OutboxReply.ChatSend, "conv:3", "On it");
        _backend.Respond = _ => throw new HttpRequestException("offline");
        await Sync(outbox).SyncAsync("tok", CancellationToken.None);
        Assert.Single(outbox.PendingChat());
    }

    [Fact]
    public async Task An_older_backend_turns_team_chat_off_and_a_task_line_goes_as_a_note()
    {
        Serve(convs: null, taskChat: "404");
        var outbox = new MessageOutbox(_dir.Path);
        outbox.AppendChat(OutboxReply.ChatSend, "task:41", "Booked the big room");
        var sync = Sync(outbox);
        sync.Focus("task:41");
        await sync.SyncAsync("tok", CancellationToken.None);

        Assert.Equal(TaskBackend.Off, sync.Snapshot.Backend);
        Assert.Empty(sync.Snapshot.Conversations!);
        Assert.False(sync.Snapshot.Thread!.Supported);
        var note = _backend.Requests.Single(r => r.Path == "/api/devices/tasks/41/action/");
        Assert.Equal("{\"action\":\"note\",\"note\":\"Booked the big room\"}", note.Body);
        Assert.Empty(outbox.PendingChat());

        // Off is asked again only after a while.
        var before = Calls("GET", "/api/devices/chat/");
        _now += TimeSpan.FromMinutes(1);
        await sync.SyncAsync("tok", CancellationToken.None);
        Assert.Equal(before, Calls("GET", "/api/devices/chat/"));
    }

    [Fact]
    public async Task Clear_forgets_the_old_members_chat()
    {
        Serve();
        var sync = Sync();
        await sync.SyncAsync("tok", CancellationToken.None);
        sync.Clear();
        Assert.Equal(TaskBackend.Unknown, sync.Snapshot.Backend);
        Assert.Empty(sync.Snapshot.Conversations!);
    }

    // ---- The pipe --------------------------------------------------------------------------------------

    [Fact]
    public void Chat_reply_crosses_the_pipe_and_an_older_service_reads_as_no_chat()
    {
        var reply = new ChatReply(true, TaskBackend.On, ChatJson.Conversations(Convs), [], new ChatThread("conv:3", ChatJson.Messages(Msgs)), Version: 4);
        var back = AgentPipeClient.ParseChat(PipeProtocol.Serialize(reply))!;
        Assert.Equal(3, back.Conversations!.Count);
        Assert.Equal(4, back.Thread!.Messages!.Count);
        Assert.Equal(4, back.Version);
        Assert.Null(AgentPipeClient.ParseChat(PipeProtocol.Serialize(PipeProtocol.NotSupported)));
        Assert.Null(AgentPipeClient.ParseChat(null));
        Assert.Null(AgentPipeClient.ParseChat("garbage"));
    }

    [Fact]
    public void An_older_tray_reads_a_newer_status_and_the_other_way_round()
    {
        var status = PipeProtocol.DeserializeStatus("""{"state": "Connected", "agentVersion": "1.3.11", "hostname": "PC"}""")!;
        Assert.Null(status.ChatVersion);
        Assert.Null(status.WorkRules);
        var request = PipeProtocol.DeserializeRequest("""{"type": "chat", "focus": "task:6"}""")!;
        Assert.Equal("task:6", request.Focus);
    }
}

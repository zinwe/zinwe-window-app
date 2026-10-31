using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Zinwe.Agent.Core.Backend;
using Zinwe.Agent.Core.Ipc;
using Zinwe.Agent.Core.Messages;
using Zinwe.Agent.Core.Tasks;

namespace Zinwe.Agent.Core.Tests;

/// <summary>
/// The member's own to-dos on the device API: the client calls (fake HTTP), the outbox kind "todo" (never chat, newest
/// state only), the agent's sync (out with retry, in, older backends and plans without tasks) and the pipe's reply.
/// </summary>
public sealed class TodosApiTests : IDisposable
{
    private const string TodosPath = "/api/devices/todos/";
    private const string Id = "0123456789abcdef0123456789abcdef";

    // The contract's Todo, as the backend sends it (snake_case, with fields the agent doesn't use).
    private const string TodoJson = """
        {"id": 12, "client_id": "0123456789abcdef0123456789abcdef", "member_id": 7, "member_name": "Emily Carter",
         "title": "Update the client tracker", "due_date": "2026-11-02",
         "steps": [{"title": "Open the sheet", "done": true}, {"title": "Send it", "done": false}],
         "status": "open", "done_at": null, "created_at": "2026-10-30T09:00:00+05:30",
         "updated_at": "2026-10-30T09:30:00.125000+05:30", "deleted": false}
        """;

    private static readonly string ListJson = $$"""{"member_id": 7, "todos": [{{TodoJson}}]}""";

    private readonly TempDir _dir = new();
    private readonly FakeBackend _backend = new();
    private DateTimeOffset _now = new(2026, 10, 30, 10, 0, 0, TimeSpan.Zero);

    public void Dispose() => _dir.Dispose();

    private static DeviceTodo Todo(string id = Id, string title = "Call Karan", bool deleted = false, int minutes = 0) =>
        new(id, title, new DateOnly(2026, 11, 2), [new DeviceTodoStep("Find the number", true)], DeviceTodo.Open,
            UpdatedAt: new DateTimeOffset(2026, 10, 30, 9, minutes, 0, TimeSpan.Zero), Deleted: deleted);

    private TodoSync Sync(MessageOutbox outbox, string? dataDir = null) =>
        new(_backend.Client(), outbox, NullLogger<TodoSync>.Instance, dataDir, () => _now);

    private int Calls(string method) => _backend.Requests.Count(r => r.Method.Method == method);

    // ---- BackendClient ---------------------------------------------------------------------------

    [Fact]
    public async Task GetTodos_reads_the_contract_with_the_device_token()
    {
        _backend.Respond = _ => FakeBackend.Json(HttpStatusCode.OK, ListJson);

        var list = await _backend.Client().GetTodosAsync("tok", CancellationToken.None);

        Assert.Equal(7, list!.MemberId);
        var t = Assert.Single(list.Todos!);
        Assert.Equal((Id, "Update the client tracker", "open", false), (t.ClientId, t.Title, t.Status, t.Deleted));
        Assert.Equal(new DateOnly(2026, 11, 2), t.DueDate);
        Assert.Equal([("Open the sheet", true), ("Send it", false)], t.Steps!.Select(s => (s.Title, s.Done)));
        Assert.Equal(new DateTimeOffset(2026, 10, 30, 9, 30, 0, 125, TimeSpan.FromHours(5.5)), t.UpdatedAt);
        var seen = _backend.Requests.Single();
        Assert.Equal(("GET", TodosPath, "Device", "tok"), (seen.Method.Method, seen.Path, seen.AuthScheme, seen.AuthParameter));
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]  // an older backend
    [InlineData(HttpStatusCode.Forbidden)] // a plan without tasks
    public async Task GetTodos_is_null_when_the_backend_keeps_none_for_this_pc(HttpStatusCode code)
    {
        _backend.Respond = _ => FakeBackend.Json(code, "{\"detail\": \"Nope.\"}");

        Assert.Null(await _backend.Client().GetTodosAsync("tok", CancellationToken.None));
    }

    [Fact]
    public async Task GetTodos_skips_rows_without_an_id_and_tolerates_missing_fields()
    {
        _backend.Respond = _ => FakeBackend.Json(HttpStatusCode.OK, """{"todos": [{"title": "no id"}, {"client_id": "abcdefgh", "title": "Bare"}]}""");

        var list = await _backend.Client().GetTodosAsync("tok", CancellationToken.None);

        Assert.Null(list!.MemberId);
        Assert.Equal("Bare", Assert.Single(list.Todos!).Title);
    }

    [Fact]
    public async Task GetTodos_throws_on_a_server_error_and_a_401_means_the_pc_was_removed()
    {
        _backend.Respond = _ => new HttpResponseMessage(HttpStatusCode.BadGateway);
        await Assert.ThrowsAsync<HttpRequestException>(() => _backend.Client().GetTodosAsync("tok", CancellationToken.None));

        _backend.Respond = _ => FakeBackend.Json(HttpStatusCode.Unauthorized, "{\"detail\": \"Invalid device token.\"}");
        await Assert.ThrowsAsync<DeviceRejectedException>(() => _backend.Client().GetTodosAsync("tok", CancellationToken.None));
    }

    [Fact]
    public async Task SendTodo_puts_the_whole_to_do_as_a_buffered_body()
    {
        _backend.Respond = _ => FakeBackend.Json(HttpStatusCode.Created, TodoJson);

        Assert.Equal(TodoWriteOutcome.Done, await _backend.Client().SendTodoAsync("tok", Todo()));

        var seen = _backend.Requests.Single();
        Assert.Equal(("PUT", $"{TodosPath}{Id}/"), (seen.Method.Method, seen.Path));
        Assert.Equal(seen.Body.Length, seen.ContentLength); // not chunked (WSGI reads it empty)
        var body = JsonDocument.Parse(seen.Body).RootElement;
        Assert.Equal("Call Karan", body.GetProperty("title").GetString());
        Assert.Equal("2026-11-02", body.GetProperty("due_date").GetString());
        Assert.Equal("open", body.GetProperty("status").GetString());
        Assert.Equal("""[{"title":"Find the number","done":true}]""", body.GetProperty("steps").GetRawText());
        Assert.Equal(Todo().UpdatedAt, body.GetProperty("updated_at").GetDateTimeOffset());
    }

    [Fact]
    public async Task SendTodo_deletes_with_only_the_time_so_the_title_does_not_travel()
    {
        _backend.Respond = _ => FakeBackend.Json(HttpStatusCode.OK, TodoJson);

        await _backend.Client().SendTodoAsync("tok", Todo(deleted: true));

        var seen = _backend.Requests.Single();
        Assert.Equal(("DELETE", $"{TodosPath}{Id}/"), (seen.Method.Method, seen.Path));
        Assert.Equal(["updated_at"], JsonDocument.Parse(seen.Body).RootElement.EnumerateObject().Select(p => p.Name));
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, TodoWriteOutcome.Refused)]
    [InlineData(HttpStatusCode.Conflict, TodoWriteOutcome.Refused)] // the PC isn't linked to a member
    [InlineData(HttpStatusCode.Forbidden, TodoWriteOutcome.Off)]
    [InlineData(HttpStatusCode.NotFound, TodoWriteOutcome.Off)]
    [InlineData(HttpStatusCode.MethodNotAllowed, TodoWriteOutcome.Off)]
    public async Task SendTodo_says_what_became_of_it(HttpStatusCode code, TodoWriteOutcome expected)
    {
        _backend.Respond = _ => FakeBackend.Json(code, "{}");

        Assert.Equal(expected, await _backend.Client().SendTodoAsync("tok", Todo()));
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task SendTodo_throws_when_it_is_worth_retrying(HttpStatusCode code)
    {
        _backend.Respond = _ => FakeBackend.Json(code, "{}");

        await Assert.ThrowsAsync<HttpRequestException>(() => _backend.Client().SendTodoAsync("tok", Todo()));
    }

    // ---- Outbox: todo ---------------------------------------------------------------------------------

    [Fact]
    public void A_todo_item_is_never_chat_feedback_or_a_task_action()
    {
        var outbox = new MessageOutbox(_dir.Path);

        var item = outbox.ReplaceTodo(Todo())!;

        Assert.Equal((OutboxReply.TodoKind, Id), (item.Kind, item.TodoId));
        Assert.True(item.IsTodo);
        Assert.False(item.IsChatReply);
        Assert.Empty(outbox.Pending());
        Assert.Empty(outbox.Sendable());
        Assert.Empty(outbox.PendingFeedback());
        Assert.Empty(outbox.PendingTaskActions());
        Assert.Null(outbox.PendingPicture());
        Assert.True(outbox.HasPendingOtherThanPicture); // so the agent sends it within seconds
    }

    [Fact]
    public async Task The_chat_sync_leaves_a_todo_item_alone()
    {
        var outbox = new MessageOutbox(_dir.Path);
        outbox.ReplaceTodo(Todo());
        _backend.Respond = _ => FakeBackend.Json(HttpStatusCode.OK, "[]");
        var writer = new NotificationWriter(_dir.Path);
        var chat = new MessageSync(_backend.Client(), writer, outbox, NullLogger<MessageSync>.Instance);

        await chat.SyncAsync("tok", CancellationToken.None);

        Assert.DoesNotContain(_backend.Requests, r => r.Method == HttpMethod.Post);
        Assert.Single(outbox.PendingTodos());
    }

    [Fact]
    public void Only_the_newest_state_of_each_to_do_waits()
    {
        var outbox = new MessageOutbox(_dir.Path);
        outbox.Append("A chat line stays");
        outbox.ReplaceTodo(Todo(title: "First"));
        outbox.ReplaceTodo(Todo("ffffffffffffffff", "Another"));
        outbox.ReplaceTodo(Todo(title: "Second", minutes: 1));

        var waiting = new MessageOutbox(_dir.Path).PendingTodos(); // from the file, as the service reads it

        Assert.Equal(["Another", "Second"], waiting.Select(i => MessageOutbox.ReadTodo(i)!.Title));
        Assert.Single(outbox.Pending());
    }

    [Fact]
    public void The_queued_to_do_reads_back_whole()
    {
        var outbox = new MessageOutbox(_dir.Path);
        outbox.ReplaceTodo(Todo(deleted: true));

        var back = MessageOutbox.ReadTodo(Assert.Single(outbox.PendingTodos()))!;

        Assert.True(back.Deleted);
        Assert.Equal(Todo().UpdatedAt, back.UpdatedAt);
        Assert.Equal(new DateOnly(2026, 11, 2), back.DueDate);
        Assert.Equal("Find the number", back.Steps!.Single().Title);
    }

    [Fact]
    public void A_file_from_an_older_build_still_reads_and_an_unreadable_todo_is_nothing()
    {
        Directory.CreateDirectory(_dir.Path);
        File.WriteAllText(Path.Combine(_dir.Path, "messages-outbox.json"), """
            {"Items": [{"Id": "r", "Body": "Hello", "CreatedAtUtc": "2026-10-30T09:00:00Z", "Kind": "reply"},
                       {"Id": "x", "Body": "not json", "CreatedAtUtc": "2026-10-30T09:01:00Z", "Kind": "todo", "TodoId": "abc"},
                       {"Id": "f", "Body": "from the future", "CreatedAtUtc": "2026-10-30T09:02:00Z", "Kind": "from_the_future"}]}
            """);
        var outbox = new MessageOutbox(_dir.Path);

        Assert.Equal(["r"], outbox.Pending().Select(i => i.Id));
        Assert.Null(MessageOutbox.ReadTodo(Assert.Single(outbox.PendingTodos())));
    }

    [Fact]
    public void RemoveTodos_leaves_everything_else()
    {
        var outbox = new MessageOutbox(_dir.Path);
        outbox.Append("Hello");
        outbox.AppendTaskAction(41, TaskActions.Start);
        outbox.ReplaceTodo(Todo());

        outbox.RemoveTodos();

        Assert.Empty(outbox.PendingTodos());
        Assert.Single(outbox.Pending());
        Assert.Single(outbox.PendingTaskActions());
    }

    // ---- TodoSync ------------------------------------------------------------------------------------

    [Fact]
    public async Task Sync_sends_what_is_queued_then_pulls_the_list()
    {
        var outbox = new MessageOutbox(_dir.Path);
        outbox.ReplaceTodo(Todo());
        outbox.ReplaceTodo(Todo("ffffffffffffffff", deleted: true));
        _backend.Respond = r => r.Method == HttpMethod.Get
            ? FakeBackend.Json(HttpStatusCode.OK, ListJson)
            : FakeBackend.Json(HttpStatusCode.OK, TodoJson);
        var sync = Sync(outbox);

        await sync.SyncAsync("tok", CancellationToken.None);

        Assert.Equal(["PUT", "DELETE", "GET"], _backend.Requests.Select(r => r.Method.Method));
        Assert.Empty(outbox.PendingTodos());
        var s = sync.Snapshot;
        Assert.Equal((true, TaskBackend.On, 7L, _now), (s.Supported, s.Backend, s.MemberId!.Value, s.SyncedAt!.Value));
        Assert.Equal("Update the client tracker", s.Todos!.Single().Title);
    }

    [Fact]
    public async Task Offline_a_to_do_stays_queued_and_goes_out_on_the_next_round()
    {
        var outbox = new MessageOutbox(_dir.Path);
        outbox.ReplaceTodo(Todo());
        outbox.ReplaceTodo(Todo("ffffffffffffffff", "Later one", minutes: 1));
        var fail = true;
        _backend.Respond = r => fail && r.Body.Contains("Later one", StringComparison.Ordinal)
            ? new HttpResponseMessage(HttpStatusCode.BadGateway)
            : FakeBackend.Json(HttpStatusCode.OK, r.Method == HttpMethod.Get ? ListJson : TodoJson);
        var sync = Sync(outbox);

        await sync.SyncAsync("tok", CancellationToken.None);

        Assert.Equal("Later one", MessageOutbox.ReadTodo(Assert.Single(outbox.PendingTodos()))!.Title); // the first one left
        Assert.Equal(TaskBackend.Unknown, sync.Snapshot.Backend);

        fail = false;
        await sync.SyncAsync("tok", CancellationToken.None);

        Assert.Empty(outbox.PendingTodos());
        Assert.Equal(TaskBackend.On, sync.Snapshot.Backend);
    }

    [Fact]
    public async Task A_refused_to_do_is_dropped_and_the_rest_still_go()
    {
        var outbox = new MessageOutbox(_dir.Path);
        outbox.ReplaceTodo(Todo(title: "Refused"));
        outbox.ReplaceTodo(Todo("ffffffffffffffff", "Fine", minutes: 1));
        _backend.Respond = r => r.Body.Contains("Refused", StringComparison.Ordinal)
            ? FakeBackend.Json(HttpStatusCode.BadRequest, "{\"title\": [\"Too long.\"]}")
            : FakeBackend.Json(HttpStatusCode.OK, r.Method == HttpMethod.Get ? ListJson : TodoJson);

        await Sync(outbox).SyncAsync("tok", CancellationToken.None);

        Assert.Empty(outbox.PendingTodos());
        Assert.Equal(2, Calls("PUT"));
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task A_plan_without_tasks_turns_it_off_and_the_queue_is_dropped_because_the_tray_keeps_the_to_do(HttpStatusCode code)
    {
        var outbox = new MessageOutbox(_dir.Path);
        outbox.ReplaceTodo(Todo());
        outbox.ReplaceTodo(Todo("ffffffffffffffff", minutes: 1));
        _backend.Respond = _ => FakeBackend.Json(code, "{\"module\": \"team_chat\"}");
        var sync = Sync(outbox);

        await sync.SyncAsync("tok", CancellationToken.None);

        Assert.Equal(TaskBackend.Off, sync.Snapshot.Backend);
        Assert.Empty(outbox.PendingTodos());
        Assert.Equal(1, Calls("PUT"));
        Assert.Equal(0, Calls("GET"));
    }

    [Fact]
    public async Task While_off_it_asks_again_every_ten_minutes_and_comes_back_on_its_own()
    {
        var outbox = new MessageOutbox(_dir.Path);
        _backend.Respond = _ => FakeBackend.Json(HttpStatusCode.Forbidden, "{}");
        var sync = Sync(outbox);

        await sync.SyncAsync("tok", CancellationToken.None);
        await sync.SyncAsync("tok", CancellationToken.None);

        Assert.Equal(TaskBackend.Off, sync.Snapshot.Backend);
        Assert.Equal(1, Calls("GET"));

        _now += TodoSync.OffRecheck;
        _backend.Respond = _ => FakeBackend.Json(HttpStatusCode.OK, ListJson);
        await sync.SyncAsync("tok", CancellationToken.None);

        Assert.Equal((TaskBackend.On, 2), (sync.Snapshot.Backend, Calls("GET")));
    }

    [Fact]
    public async Task An_unreadable_item_is_dropped_without_a_call()
    {
        Directory.CreateDirectory(_dir.Path);
        File.WriteAllText(Path.Combine(_dir.Path, "messages-outbox.json"),
            """{"Items": [{"Id": "x", "Body": "not json", "CreatedAtUtc": "2026-10-30T09:01:00Z", "Kind": "todo", "TodoId": "abc"}]}""");
        var outbox = new MessageOutbox(_dir.Path);
        _backend.Respond = _ => FakeBackend.Json(HttpStatusCode.OK, ListJson);

        await Sync(outbox).SyncAsync("tok", CancellationToken.None);

        Assert.Empty(outbox.PendingTodos());
        Assert.Equal(0, Calls("PUT"));
    }

    [Fact]
    public async Task The_version_goes_up_only_when_the_list_changes()
    {
        _backend.Respond = _ => FakeBackend.Json(HttpStatusCode.OK, ListJson);
        var sync = Sync(new MessageOutbox(_dir.Path));
        Assert.Equal(0, sync.Version);

        await sync.SyncAsync("tok", CancellationToken.None);
        await sync.SyncAsync("tok", CancellationToken.None);
        Assert.Equal(1, sync.Version);

        _backend.Respond = _ => FakeBackend.Json(HttpStatusCode.OK, ListJson.Replace("client tracker", "new tracker"));
        await sync.SyncAsync("tok", CancellationToken.None);
        Assert.Equal(2, sync.Version);
    }

    [Fact]
    public async Task The_list_survives_a_service_restart_and_clear_forgets_it()
    {
        var data = Path.Combine(_dir.Path, "agent");
        var outbox = new MessageOutbox(_dir.Path);
        _backend.Respond = _ => FakeBackend.Json(HttpStatusCode.OK, ListJson);
        await Sync(outbox, data).SyncAsync("tok", CancellationToken.None);

        var restarted = Sync(outbox, data);

        Assert.Equal((TaskBackend.On, 7L), (restarted.Snapshot.Backend, restarted.Snapshot.MemberId!.Value));
        Assert.Equal("Update the client tracker", restarted.Snapshot.Todos!.Single().Title);

        outbox.ReplaceTodo(Todo());
        restarted.Clear();

        Assert.Equal((TaskBackend.Unknown, null), (restarted.Snapshot.Backend, restarted.Snapshot.MemberId));
        Assert.Empty(restarted.Snapshot.Todos!);
        Assert.Empty(outbox.PendingTodos()); // never sent as the next member's
        Assert.Equal(TaskBackend.Unknown, Sync(outbox, data).Snapshot.Backend);
    }

    [Fact]
    public async Task A_network_failure_keeps_the_last_list()
    {
        _backend.Respond = _ => FakeBackend.Json(HttpStatusCode.OK, ListJson);
        var sync = Sync(new MessageOutbox(_dir.Path));
        await sync.SyncAsync("tok", CancellationToken.None);

        _backend.Respond = _ => throw new HttpRequestException("no network");
        await sync.SyncAsync("tok", CancellationToken.None);

        Assert.Equal(TaskBackend.On, sync.Snapshot.Backend);
        Assert.Single(sync.Snapshot.Todos!);
    }

    // ---- The pipe ------------------------------------------------------------------------------------

    [Fact]
    public void The_todos_reply_round_trips_on_the_pipe()
    {
        var reply = new TodosReply(true, TaskBackend.On, [Todo(deleted: true)], 7, _now, 3);

        var line = PipeProtocol.Serialize(reply);

        Assert.DoesNotContain('\n', line);
        var back = AgentPipeClient.ParseTodos(line)!;
        Assert.Equal((true, TaskBackend.On, 3, 7L, _now), (back.Supported, back.Backend, back.Version, back.MemberId!.Value, back.SyncedAt!.Value));
        var t = back.Todos!.Single();
        Assert.Equal((Id, true, new DateOnly(2026, 11, 2), Todo().UpdatedAt), (t.ClientId, t.Deleted, t.DueDate!.Value, t.UpdatedAt));
    }

    [Fact]
    public void A_service_without_todos_reads_as_nothing_never_as_an_error()
    {
        Assert.Null(AgentPipeClient.ParseTodos(null));
        Assert.Null(AgentPipeClient.ParseTodos(PipeProtocol.Serialize(PipeProtocol.NotSupported)));
        Assert.Null(AgentPipeClient.ParseTodos("<html>"));
    }

    [Fact]
    public void The_status_says_whether_the_service_knows_todos_and_older_ones_simply_do_not()
    {
        var newer = new AgentStatus(AgentState.Connected, "1.3.5", "PC-01", 7, null, null, TodosVersion: 4);
        Assert.Equal(4, PipeProtocol.DeserializeStatus(PipeProtocol.Serialize(newer))!.TodosVersion);

        var older = PipeProtocol.DeserializeStatus(
            """{"state":"Connected","agentVersion":"1.3.4","hostname":"PC-01","deviceId":7,"lastHeartbeatAt":null,"message":null,"tasksVersion":3}""");
        Assert.Null(older!.TodosVersion); // the tray then writes no todo items
        Assert.Equal(3, older.TasksVersion);
    }

    [Fact]
    public void The_todos_request_round_trips()
    {
        var line = PipeProtocol.Serialize(new PipeProtocol.Request(PipeProtocol.RequestTypes.Todos));

        Assert.Equal("todos", PipeProtocol.DeserializeRequest(line)!.Type);
    }
}

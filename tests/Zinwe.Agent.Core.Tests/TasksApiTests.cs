using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Zinwe.Agent.Core.Backend;
using Zinwe.Agent.Core.Ipc;
using Zinwe.Agent.Core.Messages;
using Zinwe.Agent.Core.Tasks;

namespace Zinwe.Agent.Core.Tests;

/// <summary>
/// FR-20 v2 · the owner's tasks on the device API: the client calls (fake HTTP), the agent's sync (outbox task_action
/// out with retry, GET tasks in, older backends), and the pipe's tasks reply staying compatible both ways.
/// </summary>
public sealed class TasksApiTests : IDisposable
{
    private const string TasksPath = "/api/devices/tasks/";

    // The contract's Task, as the backend sends it (snake_case, with fields the agent doesn't use).
    private const string TaskJson = """
        {"id": 41, "member_id": 7, "member_name": "Emily Carter", "title": "Book the meeting room", "note": "The big room",
         "due_at": "2026-10-30T16:00:00+05:30", "priority": "high",
         "steps": [{"id": 1, "title": "Check the calendar", "done": true}, {"id": 2, "title": "Book it", "done": false}],
         "status": "sent", "help_note": "", "created_at": "2026-10-30T09:00:00+05:30", "updated_at": "2026-10-30T09:00:00+05:30",
         "status_changed_at": "2026-10-30T09:00:00+05:30", "status_changed_by": "owner", "message_id": 900,
         "activity": [{"at": "2026-10-30T09:00:00+05:30", "by": "owner", "action": "created", "note": ""}]}
        """;

    private readonly TempDir _dir = new();
    private readonly FakeBackend _backend = new();

    public void Dispose() => _dir.Dispose();

    private int Calls(string method, string pathStart) =>
        _backend.Requests.Count(r => r.Method.Method == method && r.Path.StartsWith(pathStart, StringComparison.Ordinal));

    // ---- BackendClient ---------------------------------------------------------------------------

    [Fact]
    public async Task GetTasks_reads_the_contract_with_the_device_token()
    {
        _backend.Respond = _ => FakeBackend.Json(HttpStatusCode.OK, $"[{TaskJson}]");

        var tasks = await _backend.Client().GetTasksAsync("tok", CancellationToken.None);

        var t = Assert.Single(tasks!);
        Assert.Equal(41, t.Id);
        Assert.Equal("Book the meeting room", t.Title);
        Assert.Equal("high", t.Priority);
        Assert.Equal("sent", t.Status);
        Assert.Equal(900, t.MessageId);
        Assert.Equal([1L, 2L], t.Steps!.Select(s => s.Id));
        Assert.True(t.Steps![0].Done);
        Assert.Equal("created", t.Activity!.Single().Action);
        Assert.Equal(new DateTimeOffset(2026, 10, 30, 16, 0, 0, TimeSpan.FromHours(5.5)), t.DueAt);
        var seen = _backend.Requests.Single();
        Assert.Equal(("GET", TasksPath, "Device", "tok"), (seen.Method.Method, seen.Path, seen.AuthScheme, seen.AuthParameter));
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]   // an older backend: no tasks endpoint
    [InlineData(HttpStatusCode.Forbidden)]  // a plan without tasks
    public async Task GetTasks_is_null_when_the_backend_has_no_tasks_for_this_pc(HttpStatusCode code)
    {
        _backend.Respond = _ => FakeBackend.Json(code, "{\"detail\": \"Not found.\"}");

        Assert.Null(await _backend.Client().GetTasksAsync("tok", CancellationToken.None));
    }

    [Fact]
    public async Task GetTasks_throws_on_a_server_error_so_it_is_retried()
    {
        _backend.Respond = _ => new HttpResponseMessage(HttpStatusCode.BadGateway);

        await Assert.ThrowsAsync<HttpRequestException>(() => _backend.Client().GetTasksAsync("tok", CancellationToken.None));
    }

    [Fact]
    public async Task GetTasks_skips_rows_without_an_id_and_tolerates_missing_fields()
    {
        _backend.Respond = _ => FakeBackend.Json(HttpStatusCode.OK, """[{"title": "no id"}, {"id": "5", "title": "Bare"}]""");

        var t = Assert.Single((await _backend.Client().GetTasksAsync("tok", CancellationToken.None))!);
        Assert.Equal(5, t.Id); // a string id still reads
        Assert.Equal("sent", t.Status);
        Assert.Null(t.Steps);
    }

    [Fact]
    public async Task A_401_on_tasks_means_the_pc_was_removed()
    {
        _backend.Respond = _ => FakeBackend.Json(HttpStatusCode.Unauthorized, "{\"detail\": \"Invalid device token.\"}");

        await Assert.ThrowsAsync<DeviceRejectedException>(() => _backend.Client().GetTasksAsync("tok", CancellationToken.None));
    }

    [Fact]
    public async Task PostTaskAction_sends_only_the_parts_it_has_as_a_buffered_body()
    {
        _backend.Respond = _ => FakeBackend.Json(HttpStatusCode.OK, TaskJson.Replace("\"sent\"", "\"help\""));
        var client = _backend.Client();

        var (outcome, task) = await client.PostTaskActionAsync("tok", 41, TaskActions.Help, " Which room? ");
        await client.PostTaskActionAsync("tok", 41, TaskActions.Note, stepId: 2, stepDone: true);
        await client.PostTaskActionAsync("tok", 41, TaskActions.Start, note: "   ");

        Assert.Equal(TaskActionOutcome.Done, outcome);
        Assert.Equal("help", task!.Status);
        var bodies = _backend.Requests.Select(r => JsonDocument.Parse(r.Body).RootElement).ToList();
        Assert.All(_backend.Requests, r => Assert.Equal("/api/devices/tasks/41/action/", r.Path));
        Assert.All(_backend.Requests, r => Assert.Equal(r.Body.Length, r.ContentLength)); // not chunked (WSGI reads it empty)
        Assert.Equal("""{"action":"help","note":"Which room?"}""", _backend.Requests[0].Body);
        Assert.Equal("""{"action":"note","step_id":2,"step_done":true}""", _backend.Requests[1].Body);
        Assert.Equal("""{"action":"start"}""", _backend.Requests[2].Body);
        Assert.Equal(3, bodies.Count);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Conflict)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task PostTaskAction_refusals_are_final(HttpStatusCode code)
    {
        _backend.Respond = _ => FakeBackend.Json(code, "{\"detail\": \"no\"}");

        var (outcome, task) = await _backend.Client().PostTaskActionAsync("tok", 41, TaskActions.Done);

        Assert.Equal(TaskActionOutcome.Refused, outcome);
        Assert.Null(task);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task PostTaskAction_throws_when_it_is_worth_retrying(HttpStatusCode code)
    {
        _backend.Respond = _ => new HttpResponseMessage(code);

        await Assert.ThrowsAsync<HttpRequestException>(() => _backend.Client().PostTaskActionAsync("tok", 41, TaskActions.Done));
    }

    [Fact]
    public async Task A_message_line_carries_its_task_and_the_widget_file_gets_the_task_id()
    {
        _backend.Respond = _ => FakeBackend.Json(HttpStatusCode.OK, $$"""
            [{"id": 900, "sender": "owner", "title": "New task", "body": "Book the meeting room", "severity": "info",
              "created_at": "2026-10-30T09:00:00+05:30", "kind": "task", "task": {{TaskJson}}},
             {"id": 901, "sender": "owner", "title": "", "body": "Thanks", "severity": "info", "created_at": "2026-10-30T09:05:00+05:30"}]
            """);

        var messages = await _backend.Client().GetMessagesAsync("tok", CancellationToken.None);
        var writer = new NotificationWriter(_dir.Path);
        writer.MergeChat(messages);

        Assert.Equal("task", messages[0].Kind);
        Assert.Equal(41, messages[0].Task!.Id);
        Assert.Null(messages[1].Task);
        using var file = JsonDocument.Parse(File.ReadAllText(writer.FilePath));
        var items = file.RootElement.GetProperty("Items").EnumerateArray().ToDictionary(i => i.GetProperty("Id").GetString()!);
        Assert.Equal(41, items["900"].GetProperty("TaskId").GetInt64());
        Assert.False(items["901"].TryGetProperty("TaskId", out _)); // left out, so older trays read the file as before
    }

    // ---- Outbox: task_action -----------------------------------------------------------------------

    [Fact]
    public void Task_actions_wait_in_the_outbox_but_are_never_chat_lines()
    {
        var outbox = new MessageOutbox(_dir.Path);
        outbox.Append("hello");
        var action = outbox.AppendTaskAction(41, TaskActions.Help, " Which room? ");
        outbox.AppendTaskAction(41, TaskActions.Note, stepId: 2, stepDone: false);

        Assert.Equal(["hello"], outbox.Pending().Select(p => p.Body));
        var actions = new MessageOutbox(_dir.Path).PendingTaskActions(); // from the file, as the service reads it
        Assert.Equal([TaskActions.Help, TaskActions.Note], actions.Select(a => a.Action));
        Assert.Equal("Which room?", actions[0].Body);
        Assert.Equal((41L, 2L, false), (actions[1].TaskId!.Value, actions[1].StepId!.Value, actions[1].StepDone!.Value));
        Assert.True(outbox.TryRemove(action.Id));
        Assert.False(outbox.TryRemove(action.Id)); // already gone (posted): the caller sends the opposite action
        Assert.Single(outbox.PendingTaskActions());
    }

    [Fact]
    public void An_outbox_written_by_an_older_build_still_reads()
    {
        Directory.CreateDirectory(_dir.Path);
        File.WriteAllText(Path.Combine(_dir.Path, "messages-outbox.json"), """
            {"Items": [{"Id": "a", "Body": "hi", "CreatedAtUtc": "2026-10-30T09:00:00Z", "Kind": "reply", "Rating": null},
                       {"Id": "b", "Body": "great", "CreatedAtUtc": "2026-10-30T09:01:00Z", "Kind": "feedback", "Rating": 9},
                       {"Id": "c", "Body": "from a newer build", "CreatedAtUtc": "2026-10-30T09:02:00Z", "Kind": "something_new"}]}
            """);
        var outbox = new MessageOutbox(_dir.Path);

        Assert.Equal(["a"], outbox.Pending().Select(p => p.Id)); // an unknown kind is never posted as chat
        Assert.Equal(["b"], outbox.PendingFeedback().Select(p => p.Id));
        Assert.Empty(outbox.PendingTaskActions());
    }

    // ---- TaskSync ------------------------------------------------------------------------------------

    private DateTimeOffset _now = new(2026, 10, 30, 10, 0, 0, TimeSpan.Zero);

    private TaskSync Sync(MessageOutbox outbox, string? dataDir = null) =>
        new(_backend.Client(), outbox, NullLogger<TaskSync>.Instance, dataDir, () => _now);

    [Fact]
    public async Task Sync_posts_queued_actions_in_order_then_pulls_the_tasks()
    {
        var outbox = new MessageOutbox(_dir.Path);
        outbox.AppendTaskAction(41, TaskActions.Start);
        outbox.AppendTaskAction(41, TaskActions.Done);
        _backend.Respond = r => r.Method == HttpMethod.Get
            ? FakeBackend.Json(HttpStatusCode.OK, $"[{TaskJson.Replace("\"sent\"", "\"review\"")}]")
            : FakeBackend.Json(HttpStatusCode.OK, TaskJson);
        var sync = Sync(outbox);

        await sync.SyncAsync("tok", CancellationToken.None);

        Assert.Equal(["POST", "POST", "GET"], _backend.Requests.Select(r => r.Method.Method));
        Assert.Contains("\"start\"", _backend.Requests[0].Body);
        Assert.Contains("\"done\"", _backend.Requests[1].Body);
        Assert.Empty(outbox.PendingTaskActions());
        Assert.False(sync.HasPendingActions);
        Assert.Equal((true, TaskBackend.On, "review"), (sync.Snapshot.Supported, sync.Snapshot.Backend, sync.Snapshot.Tasks!.Single().Status));
        Assert.Equal(_now, sync.Snapshot.SyncedAt);
    }

    [Fact]
    public async Task Offline_actions_stay_queued_and_go_out_on_the_next_round()
    {
        var outbox = new MessageOutbox(_dir.Path);
        outbox.AppendTaskAction(41, TaskActions.Start);
        outbox.AppendTaskAction(41, TaskActions.Help, "Stuck");
        _backend.Respond = _ => throw new HttpRequestException("No route to host");
        var sync = Sync(outbox);

        await sync.SyncAsync("tok", CancellationToken.None); // never throws
        Assert.Equal(2, outbox.PendingTaskActions().Count);
        Assert.True(sync.HasPendingActions);
        Assert.Equal(TaskBackend.Unknown, sync.Snapshot.Backend);

        // The first one got through, then the network dropped again: only the second waits.
        var calls = 0;
        _backend.Respond = _ => ++calls == 1 ? FakeBackend.Json(HttpStatusCode.OK, TaskJson) : throw new HttpRequestException("gone");
        await sync.SyncAsync("tok", CancellationToken.None);
        Assert.Equal([TaskActions.Help], outbox.PendingTaskActions().Select(a => a.Action));

        _backend.Respond = r => r.Method == HttpMethod.Get ? FakeBackend.Json(HttpStatusCode.OK, "[]") : FakeBackend.Json(HttpStatusCode.OK, TaskJson);
        await sync.SyncAsync("tok", CancellationToken.None);
        Assert.Empty(outbox.PendingTaskActions());
        Assert.Equal(2, _backend.Requests.Count(r => r.Method == HttpMethod.Post && r.Body.Contains("Stuck"))); // failed once, then sent
    }

    [Fact]
    public async Task A_refused_action_is_dropped_and_the_next_one_still_goes()
    {
        var outbox = new MessageOutbox(_dir.Path);
        outbox.AppendTaskAction(41, TaskActions.Done);
        outbox.AppendTaskAction(42, TaskActions.Start);
        _backend.Respond = r => r.Path.Contains("/41/", StringComparison.Ordinal)
            ? FakeBackend.Json(HttpStatusCode.BadRequest, "{\"action\": [\"Not allowed now.\"]}")
            : r.Method == HttpMethod.Get ? FakeBackend.Json(HttpStatusCode.OK, "[]") : FakeBackend.Json(HttpStatusCode.OK, "{}");

        await Sync(outbox).SyncAsync("tok", CancellationToken.None);

        Assert.Empty(outbox.PendingTaskActions());
        Assert.Equal(2, Calls("POST", TasksPath));
    }

    [Fact]
    public async Task An_older_backend_turns_tasks_off_and_is_asked_again_after_a_while()
    {
        var outbox = new MessageOutbox(_dir.Path);
        _backend.Respond = _ => new HttpResponseMessage(HttpStatusCode.NotFound);
        var sync = Sync(outbox);

        await sync.SyncAsync("tok", CancellationToken.None);
        Assert.Equal((true, TaskBackend.Off), (sync.Snapshot.Supported, sync.Snapshot.Backend));
        Assert.Empty(sync.Snapshot.Tasks!);
        var offVersion = sync.Version;

        _now += TimeSpan.FromMinutes(5);
        await sync.SyncAsync("tok", CancellationToken.None);
        Assert.Equal(1, Calls("GET", TasksPath)); // not hammered

        _now += TaskSync.OffRecheck;
        _backend.Respond = _ => FakeBackend.Json(HttpStatusCode.OK, $"[{TaskJson}]"); // the backend was upgraded
        await sync.SyncAsync("tok", CancellationToken.None);
        Assert.Equal(TaskBackend.On, sync.Snapshot.Backend);
        Assert.True(sync.Version > offVersion);
    }

    [Fact]
    public async Task The_version_moves_only_when_the_tasks_change_and_the_list_survives_a_restart()
    {
        var outbox = new MessageOutbox(_dir.Path);
        var data = Path.Combine(_dir.Path, "agent");
        var json = $"[{TaskJson}]";
        _backend.Respond = _ => FakeBackend.Json(HttpStatusCode.OK, json);
        var sync = Sync(outbox, data);
        Assert.Equal(0, sync.Version);

        await sync.SyncAsync("tok", CancellationToken.None);
        var first = sync.Version;
        await sync.SyncAsync("tok", CancellationToken.None);
        Assert.Equal(first, sync.Version);

        json = $"[{TaskJson.Replace("\"sent\"", "\"seen\"")}]";
        await sync.SyncAsync("tok", CancellationToken.None);
        Assert.Equal(first + 1, sync.Version);

        var restarted = Sync(outbox, data); // offline after a restart: the last list still shows
        Assert.Equal((TaskBackend.On, "seen"), (restarted.Snapshot.Backend, restarted.Snapshot.Tasks!.Single().Status));
        Assert.True(restarted.Version > 0);

        restarted.Clear(); // removed / another member
        Assert.Equal(TaskBackend.Unknown, restarted.Snapshot.Backend);
        Assert.Empty(Sync(outbox, data).Snapshot.Tasks ?? []);
    }

    [Fact]
    public async Task The_answer_to_an_action_shows_before_the_next_pull()
    {
        var outbox = new MessageOutbox(_dir.Path);
        _backend.Respond = _ => FakeBackend.Json(HttpStatusCode.OK, $"[{TaskJson}]");
        var sync = Sync(outbox);
        await sync.SyncAsync("tok", CancellationToken.None);

        outbox.AppendTaskAction(41, TaskActions.Accept);
        _backend.Respond = r => r.Method == HttpMethod.Post
            ? FakeBackend.Json(HttpStatusCode.OK, TaskJson.Replace("\"sent\"", "\"accepted\""))
            : throw new HttpRequestException("dropped after the post");
        await sync.SyncAsync("tok", CancellationToken.None);

        Assert.Equal("accepted", sync.Snapshot.Tasks!.Single().Status);
    }

    // ---- The pipe ------------------------------------------------------------------------------------

    [Fact]
    public void The_tasks_reply_round_trips_on_the_pipe()
    {
        var task = JsonSerializer.Deserialize(TaskJson, BackendJsonContext.Default.DeviceTask)!;
        var reply = new TasksReply(true, TaskBackend.On, [task], _now, 3);

        var line = PipeProtocol.Serialize(reply);

        Assert.DoesNotContain('\n', line);
        var back = AgentPipeClient.ParseTasks(line)!;
        Assert.Equal((true, TaskBackend.On, 3, _now), (back.Supported, back.Backend, back.Version, back.SyncedAt));
        Assert.Equal(task.Title, back.Tasks!.Single().Title);
        Assert.Equal(2, back.Tasks!.Single().Steps!.Count);
    }

    [Fact]
    public void A_service_without_tasks_reads_as_no_tasks_never_as_an_error()
    {
        Assert.Null(AgentPipeClient.ParseTasks(null)); // an older service closes the pipe without a word
        Assert.Null(AgentPipeClient.ParseTasks(PipeProtocol.Serialize(PipeProtocol.NotSupported))); // a newer one says so
        Assert.Null(AgentPipeClient.ParseTasks("<html>")); // garbage
        Assert.Equal("""{"accepted":false,"message":"not supported"}""", PipeProtocol.Serialize(PipeProtocol.NotSupported));
    }

    [Fact]
    public void The_status_says_whether_the_service_knows_tasks_and_older_ones_simply_do_not()
    {
        var newer = new AgentStatus(AgentState.Connected, "1.3.1", "PC-01", 7, null, null, TasksVersion: 4);
        Assert.Contains("\"tasksVersion\":4", PipeProtocol.Serialize(newer));
        Assert.Equal(4, PipeProtocol.DeserializeStatus(PipeProtocol.Serialize(newer))!.TasksVersion);

        var older = PipeProtocol.DeserializeStatus(
            """{"state":"Connected","agentVersion":"1.3.0","hostname":"PC-01","deviceId":7,"lastHeartbeatAt":null,"message":null}""");
        Assert.Null(older!.TasksVersion);

        // An older tray reading a newer service: fields it doesn't know are skipped (same rule, a field from the future).
        var future = PipeProtocol.DeserializeStatus(
            """{"state":"Connected","agentVersion":"9.9.9","hostname":"PC-01","deviceId":7,"lastHeartbeatAt":null,"message":null,"somethingNew":{"a":1}}""");
        Assert.Equal(AgentState.Connected, future!.State);
    }

    [Fact]
    public async Task The_tasks_request_round_trips_and_unknown_types_still_parse()
    {
        var line = PipeProtocol.Serialize(new PipeProtocol.Request(PipeProtocol.RequestTypes.Tasks));
        Assert.Equal("tasks", PipeProtocol.DeserializeRequest(line)!.Type);
        using var reader = new StreamReader(new MemoryStream("{\"type\":\"from_the_future\",\"x\":1}\n"u8.ToArray()));
        Assert.Equal("from_the_future", (await PipeProtocol.ReadRequestAsync(reader, CancellationToken.None))?.Type);
    }

    [Fact]
    public async Task Client_returns_null_for_tasks_when_the_service_is_not_running()
    {
        Assert.Null(await AgentPipeClient.ExchangeAsync("Zinwe.Agent.test-" + Guid.NewGuid().ToString("N"),
            new PipeProtocol.Request(PipeProtocol.RequestTypes.Tasks), TimeSpan.FromMilliseconds(200), CancellationToken.None));
    }
}

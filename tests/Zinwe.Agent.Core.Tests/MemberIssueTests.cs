using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Zinwe.Agent.Core.Backend;
using Zinwe.Agent.Core.Ipc;
using Zinwe.Agent.Core.Messages;

namespace Zinwe.Agent.Core.Tests;

/// <summary>
/// F-13 · "Report an issue" from the Zinwe window: it rides the member's outbox as kind "issue" (never a chat line) and the
/// agent posts it to POST /api/devices/issues/ for the Zinwe team, the way it posts feedback.
/// </summary>
public sealed class MemberIssueTests : IDisposable
{
    private const string IssuesPath = "/api/devices/issues/";

    private readonly TempDir _dir = new();
    private readonly FakeBackend _backend = new();
    private readonly MessageOutbox _outbox;
    private readonly MessageSync _sync;
    private HttpStatusCode _answer = HttpStatusCode.Created;

    public MemberIssueTests()
    {
        _outbox = new MessageOutbox(_dir.Path);
        _sync = new MessageSync(_backend.Client(), new NotificationWriter(_dir.Path), _outbox, NullLogger<MessageSync>.Instance);
        _backend.Respond = r => (r.Method.Method, r.Path) switch
        {
            ("POST", IssuesPath) => FakeBackend.Json(_answer, "{\"id\": 12}"),
            ("GET", "/api/devices/messages/") => FakeBackend.Json(HttpStatusCode.OK, "[]"),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        };
    }

    public void Dispose() => _dir.Dispose();

    private static MemberIssueRequest Report(string? screenshot = null) =>
        new("The Tasks tab shows yesterday's tasks as Today", MemberIssueRequest.WindowPage,
            new MemberIssueContext("1.3.8", "Microsoft Windows 10.0.22631", "Tasks"), screenshot);

    [Fact]
    public void An_issue_is_kept_apart_from_chat_feedback_and_tasks_and_reads_back()
    {
        _outbox.Append("On my way");
        var item = Assert.IsType<OutboxReply>(_outbox.AppendIssue(Report("iVBORw0KGgo=")));

        Assert.Equal(["On my way"], _outbox.Pending().Select(r => r.Body));
        Assert.Empty(_outbox.PendingFeedback());
        Assert.Empty(_outbox.PendingTaskActions());
        Assert.Equal([item.Id], _outbox.PendingIssues().Select(i => i.Id));
        Assert.Equal(Report("iVBORw0KGgo="), MessageOutbox.ReadIssue(Assert.Single(_outbox.PendingIssues())));
        Assert.Null(MessageOutbox.ReadIssue(item with { Body = "not json" }));
    }

    [Fact]
    public async Task The_agent_posts_it_with_the_contracts_names_and_clears_it()
    {
        _outbox.AppendIssue(Report("iVBORw0KGgo="));
        Assert.True(_sync.HasPendingReplies); // sync right away, like a reply or feedback

        await _sync.SyncAsync("tok", CancellationToken.None);

        var post = Assert.Single(_backend.Requests, r => r.Path == IssuesPath);
        Assert.Equal(("Device", "tok"), (post.AuthScheme, post.AuthParameter));
        using var json = JsonDocument.Parse(post.Body);
        var root = json.RootElement;
        Assert.Equal("The Tasks tab shows yesterday's tasks as Today", root.GetProperty("description").GetString());
        Assert.Equal("Zinwe window", root.GetProperty("page").GetString());
        Assert.Equal("1.3.8", root.GetProperty("context").GetProperty("app_version").GetString());
        Assert.Equal("Microsoft Windows 10.0.22631", root.GetProperty("context").GetProperty("os").GetString());
        Assert.Equal("Tasks", root.GetProperty("context").GetProperty("screen").GetString());
        Assert.Equal("iVBORw0KGgo=", root.GetProperty("screenshot_b64").GetString());
        Assert.Empty(_outbox.PendingIssues());
        Assert.False(_sync.HasPendingReplies);
    }

    [Fact]
    public async Task Without_a_screenshot_the_field_is_left_out()
    {
        _outbox.AppendIssue(Report());

        await _sync.SyncAsync("tok", CancellationToken.None);

        using var json = JsonDocument.Parse(Assert.Single(_backend.Requests, r => r.Path == IssuesPath).Body);
        Assert.False(json.RootElement.TryGetProperty("screenshot_b64", out _));
    }

    [Fact]
    public async Task A_refusal_drops_it_and_a_server_error_keeps_it_for_the_next_round()
    {
        _outbox.AppendIssue(Report());
        _answer = HttpStatusCode.InternalServerError;
        await _sync.SyncAsync("tok", CancellationToken.None);
        Assert.Single(_outbox.PendingIssues()); // retried later

        _answer = HttpStatusCode.TooManyRequests;
        await _sync.SyncAsync("tok", CancellationToken.None);
        Assert.Single(_outbox.PendingIssues()); // throttled: later too

        _answer = HttpStatusCode.BadRequest;
        await _sync.SyncAsync("tok", CancellationToken.None);
        Assert.Empty(_outbox.PendingIssues()); // refused for good (an older backend, a bad picture)
    }

    [Fact]
    public void The_status_says_whether_the_service_sends_issues_and_older_ones_simply_do_not()
    {
        var newer = new AgentStatus(AgentState.Connected, "1.3.8", "PC-01", 7, null, null, Issues: true);
        Assert.True(PipeProtocol.DeserializeStatus(PipeProtocol.Serialize(newer))!.Issues);

        var older = PipeProtocol.DeserializeStatus(
            """{"state":"Connected","agentVersion":"1.3.7","hostname":"PC-01","deviceId":7,"lastHeartbeatAt":null,"message":null,"todosVersion":2}""");
        Assert.False(older!.Issues); // the tray then writes no issue items
    }

    [Fact]
    public async Task An_unreadable_item_is_dropped_without_a_call()
    {
        Directory.CreateDirectory(_dir.Path);
        File.WriteAllText(Path.Combine(_dir.Path, "messages-outbox.json"),
            """{"Items":[{"Id":"x1","Body":"{}","CreatedAtUtc":"2026-11-04T10:00:00+00:00","Kind":"issue"}]}""");

        await _sync.SyncAsync("tok", CancellationToken.None);

        Assert.DoesNotContain(_backend.Requests, r => r.Path == IssuesPath);
        Assert.Empty(_outbox.PendingIssues());
    }
}

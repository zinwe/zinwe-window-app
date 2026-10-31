using System.Net;
using System.Text.Json;
using Zinwe.Agent.Core.Backend;
using Zinwe.Agent.Core.Ipc;
using Zinwe.Agent.Core.Messages;
using Zinwe.Agent.Core.Work;

namespace Zinwe.Agent.Core.Tests;

/// <summary>One work day across PCs: the heartbeat's new fields, on their way from the backend to the tray.</summary>
public sealed class WorkDayHandoverContractTests : IDisposable
{
    private readonly TempDir _widget = new();
    private readonly TempDir _agent = new();
    private static readonly DateTimeOffset Today = DateTimeOffset.Now.Date.AddHours(9);

    public WorkDayHandoverContractTests() => Directory.CreateDirectory(_widget.Path);

    private static async Task<HeartbeatResponse> Beat(string extra)
    {
        var backend = new FakeBackend
        {
            Respond = _ => FakeBackend.Json(HttpStatusCode.OK,
                "{\"device_id\":1,\"server_time\":\"2026-10-15T09:00:00Z\",\"next_heartbeat_seconds\":60,\"policy_version\":1" + extra + "}"),
        };
        return await backend.Client().HeartbeatAsync("tok", new HeartbeatRequest("pc", "win", "1.3.4", "working", 0, 0), CancellationToken.None);
    }

    [Fact]
    public async Task An_older_backend_says_nothing_about_the_day()
    {
        var beat = await Beat("");
        Assert.Null(beat.DayMoved);
        Assert.Null(beat.DayCarry);
    }

    [Fact]
    public async Task The_old_pc_hears_where_the_day_went()
    {
        var beat = await Beat(""","day_moved":{"to_hostname":"LAPTOP-X","at":"2026-10-15T08:30:00Z"}""");

        Assert.Equal(new WorkDayMoved("LAPTOP-X", new DateTimeOffset(2026, 10, 15, 8, 30, 0, TimeSpan.Zero)), beat.DayMoved);
        Assert.Null(beat.DayCarry);
    }

    [Fact]
    public async Task The_new_pc_hears_what_to_carry_on_from()
    {
        var beat = await Beat(
            ""","day_carry":{"at":"2026-10-15T08:30:00+00:00","worked_seconds":7200,"break_seconds":600,"breaks":1,"first_start":"2026-10-15T06:00:00+00:00","from_hostname":"OFFICE-PC"}""");

        var carry = beat.DayCarry!;
        Assert.Equal((7200L, 600L, 1, "OFFICE-PC"), (carry.WorkedSeconds, carry.BreakSeconds, carry.Breaks, carry.FromHostname));
        Assert.Equal(new DateTimeOffset(2026, 10, 15, 6, 0, 0, TimeSpan.Zero), carry.FirstStart);
    }

    [Fact]
    public async Task A_day_with_no_start_on_record_has_a_null_first_start()
    {
        var beat = await Beat(""","day_carry":{"at":"2026-10-15T08:30:00Z","worked_seconds":60,"break_seconds":0,"breaks":0,"first_start":null,"from_hostname":""}""");
        Assert.Null(beat.DayCarry!.FirstStart);
    }

    [Fact]
    public void The_pipe_status_carries_them_to_the_tray()
    {
        var moved = new WorkDayMoved("LAPTOP-X", new DateTimeOffset(2026, 10, 15, 8, 30, 0, TimeSpan.Zero));
        var carry = new WorkDayCarry(moved.At, 7200, 600, 1, moved.At.AddHours(-2), "OFFICE-PC");
        var status = new AgentStatus(AgentState.Connected, "1.3.4", "PC-01", 7, null, null, DayMoved: moved, DayCarry: carry);

        Assert.Equal(status, PipeProtocol.DeserializeStatus(PipeProtocol.Serialize(status)));
    }

    [Fact]
    public void A_status_from_an_older_service_has_neither()
    {
        var status = PipeProtocol.DeserializeStatus(
            """{"state":"Connected","agentVersion":"1.3.0","hostname":"PC-01","deviceId":7,"lastHeartbeatAt":null,"message":null}""");
        Assert.Null(status!.DayMoved);
        Assert.Null(status.DayCarry);
    }

    [Fact]
    public void A_moved_day_is_reported_as_not_started_with_no_reasons()
    {
        File.WriteAllText(Path.Combine(_widget.Path, "work-timing.json"),
            $$"""{"Date":"{{Today:yyyy-MM-dd}}","State":"Moved","WorkedSeconds":7200,"BreakSeconds":0,"MovedTo":"LAPTOP-X"}""");

        var status = new WorkStatusReader(_widget.Path).Read(Today);

        Assert.Equal("not_started", status.State);
        Assert.Null(status.EndReason);
    }

    [Fact]
    public void The_backends_own_news_is_never_reported_back_as_a_change()
    {
        var log = new WorkTransitionLog(new LocalWidgetFolder(_widget.Path), _agent.Path);
        var file = Path.Combine(_widget.Path, "work-timing-log.jsonl");
        foreach (var (at, action, state) in new[]
                 {
                     (Today, "start", "Working"),
                     (Today.AddHours(1), "moved", "Moved"),
                     (Today.AddHours(2), "start", "Working"),
                     (Today.AddHours(2), "carried", "Working"),
                 })
        {
            File.AppendAllText(file, JsonSerializer.Serialize(new { At = at, Action = action, State = state, WorkedSeconds = 0, BreakSeconds = 0 }) + Environment.NewLine);
        }

        var events = log.Pending(Today.AddHours(3));

        Assert.Equal([Today, Today.AddHours(2)], events.Select(e => e.At));
    }

    public void Dispose()
    {
        _widget.Dispose();
        _agent.Dispose();
    }
}

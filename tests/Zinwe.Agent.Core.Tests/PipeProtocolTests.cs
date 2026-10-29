using System.Text;
using Zinwe.Agent.Core.Identity;
using Zinwe.Agent.Core.Ipc;

namespace Zinwe.Agent.Core.Tests;

public sealed class PipeProtocolTests
{
    [Fact]
    public void Status_round_trips_with_readable_state_names()
    {
        var status = new AgentStatus(AgentState.Connected, "0.1.0", "PC-01", 7, DateTimeOffset.UtcNow, null);

        var line = PipeProtocol.Serialize(status);

        Assert.Contains("\"state\":\"Connected\"", line);
        Assert.DoesNotContain('\n', line);
        Assert.Equal(status, PipeProtocol.DeserializeStatus(line));
    }

    [Fact]
    public void Status_carries_the_policy_in_force()
    {
        var status = new AgentStatus(AgentState.Offline, "0.1.0", "PC-01", 7, null, null, PolicyVersion: 3, UsbMode: "block");

        var line = PipeProtocol.Serialize(status);

        Assert.Contains("\"policyVersion\":3", line);
        Assert.Contains("\"usbMode\":\"block\"", line);
        Assert.Equal(status, PipeProtocol.DeserializeStatus(line));
    }

    [Fact]
    public void Status_from_an_older_service_without_policy_fields_still_parses()
    {
        var status = PipeProtocol.DeserializeStatus(
            """{"state":"Connected","agentVersion":"0.1.0","hostname":"PC-01","deviceId":7,"lastHeartbeatAt":null,"message":null}""");

        Assert.Equal(AgentState.Connected, status?.State);
        Assert.Null(status?.PolicyVersion);
        Assert.Null(status?.UsbMode);
    }

    [Theory]
    [InlineData("{\"type\":\"status\"}", "status")]
    [InlineData("not json", null)]
    [InlineData("", null)]
    public async Task ReadRequest_tolerates_garbage(string line, string? expectedType)
    {
        using var reader = new StreamReader(new MemoryStream(Encoding.UTF8.GetBytes(line + "\n")));

        var request = await PipeProtocol.ReadRequestAsync(reader, CancellationToken.None);

        Assert.Equal(expectedType, request?.Type);
    }

    [Fact]
    public async Task Oversized_request_is_rejected()
    {
        var line = "{\"type\":\"" + new string('x', PipeProtocol.MaxRequestChars) + "\"}"; // over the limit (256 K since FR-44 photos)
        using var reader = new StreamReader(new MemoryStream(Encoding.UTF8.GetBytes(line + "\n")));

        Assert.Null(await PipeProtocol.ReadRequestAsync(reader, CancellationToken.None));
    }

    [Fact]
    public async Task Client_returns_null_when_service_is_not_running()
    {
        // Nothing listens on the pipe in the test process unless the real agent happens to be running.
        var status = await AgentPipeClient.GetStatusAsync(TimeSpan.FromMilliseconds(300));
        Assert.True(status is null || status.AgentVersion.Length > 0);
    }

    [Fact]
    public void Enroll_request_round_trips_with_email_and_id()
    {
        var line = PipeProtocol.Serialize(new PipeProtocol.Request(PipeProtocol.RequestTypes.Enroll, "asha@acme.in", "ZM-7KQ2-9F3M"));

        Assert.Contains("\"type\":\"enroll\"", line);
        Assert.Contains("\"email\":\"asha@acme.in\"", line);
        Assert.Contains("\"memberId\":\"ZM-7KQ2-9F3M\"", line);
        var back = PipeProtocol.DeserializeRequest(line);
        Assert.Equal("asha@acme.in", back?.Email);
        Assert.Equal("ZM-7KQ2-9F3M", back?.MemberId);
    }

    [Fact]
    public void Enroll_ack_round_trips()
    {
        var line = PipeProtocol.Serialize(new PipeProtocol.EnrollAck(true, "Connecting this PC…"));
        var ack = PipeProtocol.DeserializeAck(line);
        Assert.True(ack?.Accepted);
        Assert.Equal("Connecting this PC…", ack?.Message);
    }

    [Fact]
    public async Task Submit_enrollment_returns_null_when_service_is_not_running()
    {
        // On a PC with Zinwe installed (or the dev agent running) the pipe is real: never send it a fake
        // enrollment -- the test only covers the "nothing listening" path.
        if (await AgentPipeClient.GetStatusAsync(TimeSpan.FromMilliseconds(300)) is not null)
        {
            return;
        }
        Assert.Null(await AgentPipeClient.SubmitEnrollmentAsync("a@b.in", "ZM-X", TimeSpan.FromMilliseconds(300)));
    }

    [Fact]
    public void MachineInfo_reads_this_pc()
    {
        Assert.Matches("^[0-9a-f-]{36}$", MachineInfo.MachineId);
        Assert.StartsWith("Windows", MachineInfo.OsVersion);
        Assert.False(string.IsNullOrWhiteSpace(MachineInfo.Hostname));
    }
}

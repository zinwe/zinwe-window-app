using System.IO.Pipes;
using System.Text.Json;
using System.Text.Json.Serialization;
using Zinwe.Agent.Core.Features.Web;

namespace Zinwe.Agent.Core.Ipc;

/// <summary>
/// Service ↔ tray protocol: one UTF-8 JSON line request, one JSON line response, then close.
/// Kept deliberately tiny; add request types here (e.g. "consent", "alerts") as modules land.
/// </summary>
public static class PipeProtocol
{
    /// <summary>"Zinwe.Agent" for the installed agent; "Zinwe.Agent.dev" for the local dev run (<see cref="AgentInstance"/>).</summary>
    public static string PipeName { get; } = AgentInstance.Suffixed("Zinwe.Agent", ".");

    /// <summary>
    /// A tray→service request. <see cref="Email"/>/<see cref="MemberId"/> are set only for "enroll";
    /// <see cref="Usage"/> only for "usage".
    /// </summary>
    public sealed record Request(string Type, string? Email = null, string? MemberId = null, IReadOnlyList<ForegroundReport>? Usage = null);

    /// <summary>Reply to an "enroll" or "update" request: whether the service took it on, and what to tell the member.</summary>
    public sealed record EnrollAck(bool Accepted, string Message);

    public static class RequestTypes
    {
        public const string Status = "status";
        /// <summary>Member typed their email + id in the widget to connect this PC.</summary>
        public const string Enroll = "enroll";
        /// <summary>Member pressed "Update now": download, verify and install the offered release.</summary>
        public const string Update = "update";
        /// <summary>
        /// The tray's readings of what was in front of the member (app, and website domain when a browser was in
        /// front): the service in Session 0 can't see the desktop, so the tray, in the member's session, reads it.
        /// </summary>
        public const string Usage = "usage";
    }

    /// <summary>
    /// FIX-015: the service takes an "enroll" only while this PC isn't connected (the widget shows Connect only then).
    /// A connected PC can't be quietly re-pointed at another member over the pipe.
    /// </summary>
    public static bool AcceptsEnrollment(AgentState state) =>
        state is AgentState.NotEnrolled or AgentState.Enrolling or AgentState.Rejected;

    internal static string Serialize(Request request) => JsonSerializer.Serialize(request, PipeJsonContext.Default.Request);

    internal static Request? DeserializeRequest(string line) => JsonSerializer.Deserialize(line, PipeJsonContext.Default.Request);

    public static string Serialize(AgentStatus status) => JsonSerializer.Serialize(status, PipeJsonContext.Default.AgentStatus);

    public static AgentStatus? DeserializeStatus(string line) => JsonSerializer.Deserialize(line, PipeJsonContext.Default.AgentStatus);

    public static string Serialize(EnrollAck ack) => JsonSerializer.Serialize(ack, PipeJsonContext.Default.EnrollAck);

    public static EnrollAck? DeserializeAck(string line) => JsonSerializer.Deserialize(line, PipeJsonContext.Default.EnrollAck);

    /// <summary>Server side: read one request line. Returns null on a malformed or empty request.</summary>
    public static async Task<Request?> ReadRequestAsync(StreamReader reader, CancellationToken ct)
    {
        var line = await reader.ReadLineAsync(ct);
        if (string.IsNullOrWhiteSpace(line) || line.Length > 32_768)
        {
            return null;
        }
        try
        {
            return DeserializeRequest(line);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>
/// Client used by the tray app. Never throws for "service not running" -- returns null instead.
/// FIX-015: before anything is sent, the pipe's server process must be the real Zinwe service
/// (<see cref="PipePeers.IsTrustedService"/>); an impostor holding the pipe name is treated as "not running", so
/// it never receives the member's email + Member ID.
/// </summary>
public static class AgentPipeClient
{
    public static async Task<AgentStatus?> GetStatusAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        var line = await ExchangeAsync(new PipeProtocol.Request(PipeProtocol.RequestTypes.Status), timeout, ct);
        try
        {
            return line is null ? null : PipeProtocol.DeserializeStatus(line);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Ask the service to install the offered update. Returns null if the service isn't reachable.</summary>
    public static Task<PipeProtocol.EnrollAck?> RequestUpdateAsync(TimeSpan timeout, CancellationToken ct = default) =>
        SendAsync(new PipeProtocol.Request(PipeProtocol.RequestTypes.Update), timeout, ct);

    /// <summary>Send the tray's foreground readings. Returns null if the service isn't reachable (the caller keeps them).</summary>
    public static Task<PipeProtocol.EnrollAck?> ReportUsageAsync(IReadOnlyList<ForegroundReport> usage, TimeSpan timeout, CancellationToken ct = default) =>
        SendAsync(new PipeProtocol.Request(PipeProtocol.RequestTypes.Usage, Usage: usage), timeout, ct);

    /// <summary>Hand the member-typed email + id to the service to enrol with. Returns null if the service isn't reachable.</summary>
    public static Task<PipeProtocol.EnrollAck?> SubmitEnrollmentAsync(string email, string memberId, TimeSpan timeout, CancellationToken ct = default) =>
        SendAsync(new PipeProtocol.Request(PipeProtocol.RequestTypes.Enroll, email, memberId), timeout, ct);

    private static async Task<PipeProtocol.EnrollAck?> SendAsync(PipeProtocol.Request request, TimeSpan timeout, CancellationToken ct)
    {
        var line = await ExchangeAsync(request, timeout, ct);
        try
        {
            return line is null ? null : PipeProtocol.DeserializeAck(line);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// One request line out, one reply line back -- only after the server proved to be the Zinwe service. Null when
    /// nothing is listening, the server isn't ours, or the service refused this client (it then closes without a reply).
    /// </summary>
    private static Task<string?> ExchangeAsync(PipeProtocol.Request request, TimeSpan timeout, CancellationToken ct) =>
        ExchangeAsync(PipeProtocol.PipeName, request, timeout, ct);

    /// <summary>As above on any pipe name (tests use a private one).</summary>
    internal static async Task<string?> ExchangeAsync(string pipeName, PipeProtocol.Request request, TimeSpan timeout, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);

            await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(cts.Token);
            if (!PipePeers.IsTrustedService(pipe, AppContext.BaseDirectory))
            {
                return null; // not the Zinwe service: say nothing to it
            }

            using var reader = new StreamReader(pipe, leaveOpen: true);
            await using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
            await writer.WriteLineAsync(PipeProtocol.Serialize(request).AsMemory(), cts.Token);

            return await reader.ReadLineAsync(cts.Token);
        }
        catch (Exception e) when (e is OperationCanceledException or IOException or TimeoutException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(PipeProtocol.Request))]
[JsonSerializable(typeof(ForegroundReport))]
[JsonSerializable(typeof(List<ForegroundReport>))]
[JsonSerializable(typeof(PipeProtocol.EnrollAck))]
[JsonSerializable(typeof(AgentStatus))]
internal sealed partial class PipeJsonContext : JsonSerializerContext;

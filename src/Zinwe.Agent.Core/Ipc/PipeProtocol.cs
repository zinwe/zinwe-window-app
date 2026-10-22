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

/// <summary>Client used by the tray app. Never throws for "service not running" -- returns null instead.</summary>
public static class AgentPipeClient
{
    public static async Task<AgentStatus?> GetStatusAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);

            await using var pipe = new NamedPipeClientStream(".", PipeProtocol.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(cts.Token);

            using var reader = new StreamReader(pipe, leaveOpen: true);
            await using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
            await writer.WriteLineAsync(PipeProtocol.Serialize(new PipeProtocol.Request(PipeProtocol.RequestTypes.Status)).AsMemory(), cts.Token);

            var line = await reader.ReadLineAsync(cts.Token);
            return line is null ? null : PipeProtocol.DeserializeStatus(line);
        }
        catch (Exception e) when (e is OperationCanceledException or IOException or TimeoutException or JsonException or UnauthorizedAccessException)
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
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);

            await using var pipe = new NamedPipeClientStream(".", PipeProtocol.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(cts.Token);

            using var reader = new StreamReader(pipe, leaveOpen: true);
            await using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
            await writer.WriteLineAsync(PipeProtocol.Serialize(request).AsMemory(), cts.Token);

            var line = await reader.ReadLineAsync(cts.Token);
            return line is null ? null : PipeProtocol.DeserializeAck(line);
        }
        catch (Exception e) when (e is OperationCanceledException or IOException or TimeoutException or JsonException or UnauthorizedAccessException)
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

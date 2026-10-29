using System.IO.Pipes;
using System.Text.Json;
using System.Text.Json.Serialization;
using Zinwe.Agent.Core.Features.Web;

namespace Zinwe.Agent.Core.Ipc;

/// <summary>
/// Service ↔ tray protocol: one UTF-8 JSON line request, one JSON line response, then close.
/// Kept deliberately tiny; add request types here (e.g. "consent", "alerts") as modules land.
/// Compatibility: a request type the service doesn't know is answered with <see cref="NotSupported"/> (since 1.3.1;
/// older services close the pipe without a word, which every client reads as null), and replies only ever gain
/// optional fields, so an older tray keeps reading a newer service and the other way round.
/// </summary>
public static class PipeProtocol
{
    /// <summary>"Zinwe.Agent" for the installed agent; "Zinwe.Agent.dev" for the local dev run (<see cref="AgentInstance"/>).</summary>
    public static string PipeName { get; } = AgentInstance.Suffixed("Zinwe.Agent", ".");

    /// <summary>
    /// A tray→service request. <see cref="Email"/>/<see cref="MemberId"/> are set only for "enroll";
    /// <see cref="Usage"/> only for "usage"; <see cref="Picture"/> only for "picture" (FR-44, trailing and optional:
    /// older services ignore it).
    /// </summary>
    public sealed record Request(
        string Type, string? Email = null, string? MemberId = null, IReadOnlyList<ForegroundReport>? Usage = null,
        PictureChoice? Picture = null);

    /// <summary>
    /// The longest request line the service reads. A "picture" request carries a ≤ 150 KB JPEG as base64 (≈ 200 KB);
    /// 1.3.1 services read at most 32 KB and close the pipe on anything longer, which the tray reads as "not supported".
    /// </summary>
    public const int MaxRequestChars = 262_144;

    /// <summary>Reply to an "enroll" or "update" request: whether the service took it on, and what to tell the member.</summary>
    public sealed record EnrollAck(bool Accepted, string Message);

    /// <summary>The message of the reply to a request type this service doesn't know.</summary>
    public const string NotSupportedMessage = "not supported";

    /// <summary>The reply to a request type this service doesn't know (an older service only closes the pipe).</summary>
    public static readonly EnrollAck NotSupported = new(false, NotSupportedMessage);

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
        /// <summary>
        /// FR-20 v2: the owner's tasks the service last synced (<see cref="TasksReply"/>). The tray asks when
        /// <see cref="AgentStatus.TasksVersion"/> changes; a status without it is an older service (own to-dos only).
        /// </summary>
        public const string Tasks = "tasks";
        /// <summary>
        /// FR-44: set or remove the member's picture (<see cref="Request.Picture"/>). The service keeps it in the outbox
        /// (sent when online) and answers at once; the status's <see cref="AgentStatus.Picture"/> shows it. A status
        /// without <see cref="AgentStatus.Picture"/> is an older service: the tray never sends this request to it.
        /// </summary>
        public const string Picture = "picture";
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

    public static string Serialize(TasksReply reply) => JsonSerializer.Serialize(reply, PipeJsonContext.Default.TasksReply);

    /// <summary>
    /// A "tasks" reply. A <see cref="NotSupported"/> ack (or any other shape) reads as <c>Supported = false</c>, so a
    /// service that doesn't know tasks never breaks the tray.
    /// </summary>
    public static TasksReply? DeserializeTasks(string line) => JsonSerializer.Deserialize(line, PipeJsonContext.Default.TasksReply);

    /// <summary>Server side: read one request line. Returns null on a malformed or empty request.</summary>
    public static async Task<Request?> ReadRequestAsync(StreamReader reader, CancellationToken ct)
    {
        var line = await reader.ReadLineAsync(ct);
        if (string.IsNullOrWhiteSpace(line) || line.Length > MaxRequestChars)
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

    /// <summary>
    /// The owner's tasks the service holds (FR-20 v2). Null when the service isn't reachable or doesn't know tasks
    /// (an older service closes the pipe, a newer one without them says "not supported"): own to-dos only, then.
    /// </summary>
    public static async Task<TasksReply?> GetTasksAsync(TimeSpan timeout, CancellationToken ct = default) =>
        ParseTasks(await ExchangeAsync(new PipeProtocol.Request(PipeProtocol.RequestTypes.Tasks), timeout, ct));

    /// <summary>A reply line → tasks; null for nothing, garbage or a "not supported" answer.</summary>
    internal static TasksReply? ParseTasks(string? line)
    {
        try
        {
            return line is null ? null : PipeProtocol.DeserializeTasks(line) is { Supported: true } reply ? reply : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// FR-44: set or remove the member's picture. Null when the service isn't reachable; an older service answers
    /// "not supported" (or closes the pipe: null too). Accepted = queued, it shows on the next status.
    /// </summary>
    public static Task<PipeProtocol.EnrollAck?> SetPictureAsync(PictureChoice choice, TimeSpan timeout, CancellationToken ct = default) =>
        SendAsync(new PipeProtocol.Request(PipeProtocol.RequestTypes.Picture, Picture: choice), timeout, ct);

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
[JsonSerializable(typeof(TasksReply))]
[JsonSerializable(typeof(PictureStatus))]
[JsonSerializable(typeof(PictureChoice))]
internal sealed partial class PipeJsonContext : JsonSerializerContext;

using System.Text.Json;
using System.Text.Json.Serialization;
using Zinwe.Agent.Core.Policy;

namespace Zinwe.Agent.Core.Backend;

// Wire contracts for /api/devices/* (see backend z_devices). Django speaks snake_case;
// the source-generated context below maps PascalCase properties automatically.

// Enrollment now identifies a MEMBER: the owner adds a member (name + email) and gets a member id
// (e.g. ZM-XXXX-XXXX); the PC enrols with that member's email + id. No org-wide key.
public sealed record EnrollRequest(
    string Email,
    string MemberId,
    string MachineId,
    string Hostname,
    string OsVersion,
    string AgentVersion,
    string Platform = "windows");

public sealed record EnrollResponse(long DeviceId, string DeviceToken, bool ReEnrolled);

// Heartbeat now also carries the member's work status (read from the widget) so the owner sees working/on break.
// WorkChangedAt + BreakReason feed the owner's Activity timeline (start / break with reason / back / end).
public sealed record HeartbeatRequest(
    string Hostname,
    string OsVersion,
    string AgentVersion,
    string WorkState,
    long WorkedSeconds,
    long BreakSeconds,
    DateTimeOffset? WorkChangedAt = null,
    string? BreakReason = null,
    string? BreakNote = null,
    string? EndReason = null,
    string? EndNote = null,
    IReadOnlyList<WorkEvent>? WorkEvents = null,
    string? ConsentVersion = null,
    DateTimeOffset? ConsentAt = null,
    // The member's answer to the monitoring notice ("given" | "declined" + the version). Left out entirely while
    // they haven't answered, so a backend that doesn't know the field (or doesn't allow null) is unaffected.
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ConsentDecision? Consent = null);

/// <summary>Heartbeat <c>consent</c>: <paramref name="State"/> is "given" or "declined"; <paramref name="Version"/> the notice version.</summary>
public sealed record ConsentDecision(string State, string Version);

/// <summary>
/// One change the member made in the widget, at its exact time: <paramref name="State"/> is "working" (logged in /
/// back), "on_break" or "ended" (logged out); <paramref name="Reason"/>/<paramref name="Note"/> are the break's or
/// the log-out's. Sent in <see cref="HeartbeatRequest.WorkEvents"/> until the backend has them.
/// </summary>
public sealed record WorkEvent(DateTimeOffset At, string State, string? Reason = null, string? Note = null);

/// <param name="UpdateRequired">The agent is below a required release (or the minimum) and must be updated.</param>
/// <param name="DownloadUrl">Public download page (manual fallback when the one-click update can't run).</param>
/// <param name="Update">A newer release published from the admin app, when there is one.</param>
/// <param name="ConsentState">The backend's view of the member's consent ("pending" | "given" | "declined"); null = older backend.</param>
/// <param name="UsbRestore">True once after the owner pressed "Restore USB": put USB back to the policy and send usb.restored.</param>
/// <param name="CompanyName">The employer's name, when the backend sends it (shown on the consent screen and widget).</param>
/// <param name="OwnerName">The owner's name, when the backend sends it ("Message Ravi").</param>
public sealed record HeartbeatResponse(
    long DeviceId,
    DateTimeOffset ServerTime,
    int NextHeartbeatSeconds,
    int PolicyVersion,
    bool UpdateRequired = false,
    string? DownloadUrl = null,
    AgentUpdate? Update = null,
    string? ConsentState = null,
    bool? UsbRestore = null,
    string? CompanyName = null,
    string? OwnerName = null);

/// <summary>
/// A newer Windows agent release the PC should install. <paramref name="DownloadUrl"/> is either a path on our own
/// backend ("/api/devices/releases/5/download/", fetched with this PC's device token) or an external https URL;
/// the agent installs it only if the file's SHA-256 (and size, when given) match. Empty URL/hash = no installer
/// published (only a minimum version is set): the member updates manually.
/// </summary>
public sealed record AgentUpdate(
    string Version, string? Notes, bool Required, string? DownloadUrl, string? Sha256, long? Size);

/// <summary>One rolled-up usage metric uploaded via POST /api/devices/metrics/upload/.</summary>
public sealed record MetricUpload(
    DateTimeOffset BucketStart, string Category, string Key, long Count, long Bytes, long Seconds);

public sealed record MetricUploadRequest(MetricUpload[] Metrics);

public sealed record MetricUploadResponse(int Accepted);

// GET /api/devices/policy/current/ returns an AgentPolicy (Zinwe.Agent.Core.Policy): {"version": n, "usb": {"mode": "..."}}.

/// <summary>One event in POST /api/devices/events/upload/. <see cref="Data"/> is sent as a JSON object.</summary>
public sealed record EventUpload(string Id, string Type, DateTimeOffset OccurredAt, string Severity, JsonElement Data);

public sealed record EventUploadRequest(EventUpload[] Events);

/// <summary>One line of the member's conversation (GET /api/devices/messages/). Shown in the employee widget.</summary>
/// <param name="Sender">"owner" = an admin message/alert; "member" = the member's own reply. Trailing/optional for back-compat.</param>
/// <param name="ReadByMember">Server-side "the member has read it" (only meaningful for owner lines).</param>
public sealed record AgentMessage(
    long Id, string Title, string? Body, string Severity, DateTimeOffset CreatedAt, string Sender = "owner",
    bool ReadByMember = false);

/// <summary>POST /api/devices/messages/ — the member replies in the thread.</summary>
public sealed record MemberReplyRequest(string Body);

/// <summary><see cref="Accepted"/> = newly stored; <see cref="Duplicates"/> = ids the backend already had (a retry).</summary>
public sealed record EventUploadResponse(int Accepted, int Duplicates);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(EnrollRequest))]
[JsonSerializable(typeof(EnrollResponse))]
[JsonSerializable(typeof(HeartbeatRequest))]
[JsonSerializable(typeof(HeartbeatResponse))]
[JsonSerializable(typeof(AgentPolicy))]
[JsonSerializable(typeof(EventUploadRequest))]
[JsonSerializable(typeof(EventUploadResponse))]
[JsonSerializable(typeof(AgentMessage[]))]
[JsonSerializable(typeof(MemberReplyRequest))]
[JsonSerializable(typeof(MetricUploadRequest))]
[JsonSerializable(typeof(MetricUploadResponse))]
[JsonSerializable(typeof(Dictionary<string, string[]>))]
internal sealed partial class BackendJsonContext : JsonSerializerContext;

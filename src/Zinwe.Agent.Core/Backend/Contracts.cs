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
/// <param name="Picture">FR-44: this PC's member's picture, raw (read it with <see cref="MemberPictures.Read"/>). The key
/// missing (<see cref="JsonValueKind.Undefined"/>) = a backend without team pictures; JSON null = no picture.</param>
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
    string? OwnerName = null,
    JsonElement Picture = default);

/// <summary>
/// A newer Windows agent release the PC should install. <paramref name="DownloadUrl"/> is either a path on our own
/// backend ("/api/devices/releases/5/download/", fetched with this PC's device token) or an external https URL;
/// the agent installs it only if the file's SHA-256 (and size, when given) match and it is Authenticode-signed by a
/// pinned Zinwe certificate (FIX-016). Empty URL/hash = no installer
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
/// <param name="Kind">"task" on the line the owner's new task created (FR-20 v2); null/other on older backends.</param>
/// <param name="Task">The task that line carries (FR-20 v2): the widget draws a task card instead of a bubble.</param>
/// <param name="ReadByOwner">Server-side "the owner has read it" (only meaningful for the member's lines): "Seen" in the chat.</param>
public sealed record AgentMessage(
    long Id, string Title, string? Body, string Severity, DateTimeOffset CreatedAt, string Sender = "owner",
    bool ReadByMember = false, string? Kind = null, DeviceTask? Task = null, bool ReadByOwner = false);

/// <summary>
/// A task the owner gave this PC's member (FR-20 v2, GET /api/devices/tasks/). Status: sent → seen → accepted →
/// progress → review → approved, side states help and cancelled. Unknown extra fields are ignored, and every field
/// but the id has a default, so a backend that adds or drops one doesn't break the agent.
/// </summary>
public sealed record DeviceTask(
    long Id,
    string Title = "",
    string? Note = "",
    DateTimeOffset? DueAt = null,
    string? Priority = "normal",
    IReadOnlyList<DeviceTaskStep>? Steps = null,
    string Status = "sent",
    string? HelpNote = "",
    DateTimeOffset? CreatedAt = null,
    DateTimeOffset? UpdatedAt = null,
    DateTimeOffset? StatusChangedAt = null,
    string? StatusChangedBy = null,
    long? MessageId = null,
    IReadOnlyList<DeviceTaskActivity>? Activity = null,
    string? MemberName = null);

/// <summary>One step of a <see cref="DeviceTask"/>.</summary>
public sealed record DeviceTaskStep(long Id, string Title = "", bool Done = false);

/// <summary>One line of a task's history: <paramref name="By"/> is "owner" or "member".</summary>
public sealed record DeviceTaskActivity(DateTimeOffset At, string By = "", string Action = "", string? Note = null);

/// <summary>
/// POST /api/devices/tasks/&lt;id&gt;/action/ — what the member did: "seen" | "accept" | "start" | "help" | "done" |
/// "note". A step tick is "note" with <paramref name="StepId"/> + <paramref name="StepDone"/>. Empty parts are left out.
/// </summary>
public sealed record TaskActionRequest(
    string Action,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Note = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? StepId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? StepDone = null);

/// <summary>The member's task actions (the contract's names).</summary>
public static class TaskActions
{
    public const string Seen = "seen";
    public const string Accept = "accept";
    public const string Start = "start";
    public const string Help = "help";
    public const string Done = "done";
    public const string Note = "note";

    public static bool IsKnown(string? action) => action is Seen or Accept or Start or Help or Done or Note;
}

/// <summary>What happened to a task action the agent posted.</summary>
public enum TaskActionOutcome
{
    /// <summary>Stored; the backend's view of the task came back.</summary>
    Done,
    /// <summary>Refused for good (4xx other than 429: an older backend, a task that's gone, a step that doesn't exist).
    /// Sending it again won't help; the next sync shows the real state.</summary>
    Refused,
}

/// <summary>
/// One of the member's own to-dos as the device API has it (GET /api/devices/todos/), also what the tray queues in the
/// outbox. <see cref="ClientId"/> is the Windows app's id; <see cref="UpdatedAt"/> the member's own change time, which
/// decides who wins (last write wins, a delete wins over older edits). Unknown extra fields are ignored.
/// </summary>
/// <param name="Status">"open" or "done".</param>
/// <param name="Deleted">Deleted on one of the member's PCs: the others drop it.</param>
public sealed record DeviceTodo(
    string ClientId = "",
    string Title = "",
    DateOnly? DueDate = null,
    IReadOnlyList<DeviceTodoStep>? Steps = null,
    string Status = DeviceTodo.Open,
    DateTimeOffset? CreatedAt = null,
    DateTimeOffset UpdatedAt = default,
    bool Deleted = false)
{
    public const string Open = "open";
    public const string Done = "done";
}

/// <summary>One step of a <see cref="DeviceTodo"/>.</summary>
public sealed record DeviceTodoStep(string Title = "", bool Done = false);

/// <summary>GET /api/devices/todos/ (device). <see cref="MemberId"/> says whose list it is, so the tray never mixes two members' to-dos.</summary>
public sealed record DeviceTodoList(long? MemberId = null, IReadOnlyList<DeviceTodo>? Todos = null);

/// <summary>PUT /api/devices/todos/&lt;client id&gt;/.</summary>
public sealed record TodoSaveRequest(
    string Title, DateOnly? DueDate, IReadOnlyList<DeviceTodoStep> Steps, string Status, DateTimeOffset UpdatedAt);

/// <summary>DELETE /api/devices/todos/&lt;client id&gt;/.</summary>
public sealed record TodoDeleteRequest(DateTimeOffset UpdatedAt);

/// <summary>What happened to a to-do the agent sent.</summary>
public enum TodoWriteOutcome
{
    /// <summary>Stored, or older than what the backend has: either way, done with it.</summary>
    Done,
    /// <summary>Refused for good (a 4xx other than 403/404/405/429): sending it again won't help.</summary>
    Refused,
    /// <summary>403/404/405: an older backend, or a plan without tasks. The to-dos stay on this PC.</summary>
    Off,
}

/// <summary>
/// FR-44: a member's picture. <see cref="Kind"/> "avatar" = one of the library's drawn people (<see cref="Key"/>, e.g.
/// "a07"); "photo" = an uploaded 256 × 256 JPEG at <see cref="Url"/> (signed, no auth header; the URL changes whenever
/// the picture changes, so it doubles as the cache key). No picture at all is null.
/// </summary>
public sealed record MemberPicture(string Kind, string? Key = null, string? Url = null, DateTimeOffset? UpdatedAt = null)
{
    public const string AvatarKind = "avatar";
    public const string PhotoKind = "photo";

    [JsonIgnore]
    public bool IsAvatar => Kind == AvatarKind && !string.IsNullOrEmpty(Key);

    [JsonIgnore]
    public bool IsPhoto => Kind == PhotoKind && !string.IsNullOrEmpty(Url);
}

/// <summary>Reading the <c>picture</c> object wherever the backend sends it (heartbeat, <c>PUT me/picture/</c>).</summary>
public static class MemberPictures
{
    /// <summary>Library keys: short, lower-case, stable ("a07"). Anything else is never sent.</summary>
    public static bool IsValidKey(string? key) =>
        key is { Length: >= 1 and <= 32 } && key.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '-');

    /// <summary>
    /// The picture in <paramref name="element"/>. <paramref name="present"/> is false when the field is missing (a
    /// backend without pictures: keep what we have); a JSON null, or a shape this agent doesn't know, is "no picture".
    /// </summary>
    public static MemberPicture? Read(JsonElement element, out bool present)
    {
        present = element.ValueKind != JsonValueKind.Undefined;
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }
        var kind = Text(element, "kind");
        if (kind == MemberPicture.AvatarKind && Text(element, "key") is { } key && IsValidKey(key))
        {
            return new MemberPicture(kind, Key: key);
        }
        if (kind == MemberPicture.PhotoKind && Text(element, "url") is { Length: > 0 } url)
        {
            DateTimeOffset? updated = element.TryGetProperty("updated_at", out var u) && u.ValueKind == JsonValueKind.String
                && u.TryGetDateTimeOffset(out var at) ? at : null;
            return new MemberPicture(kind, Url: url, UpdatedAt: updated);
        }
        return null;
    }

    /// <summary>The reply to <c>PUT me/picture/</c>: <c>{"picture": {…}}</c>, or the picture object on its own.</summary>
    public static MemberPicture? ReadReply(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty("picture", out var inner)
            ? Read(inner, out _)
            : Read(root, out _);

    private static string? Text(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}

/// <summary>What became of a picture change the agent sent (<c>PUT</c> / <c>DELETE me/picture/</c>).</summary>
public enum PictureOutcome
{
    /// <summary>Stored: <see cref="PictureResult.Picture"/> is the backend's view (null after a removal).</summary>
    Saved,
    /// <summary>Refused for good (a 4xx other than 404/405/429: unknown avatar, too large, not an image). Sending it
    /// again won't help; the real picture shows.</summary>
    Refused,
    /// <summary>404/405: a backend without team pictures. The tray hides the control.</summary>
    Unsupported,
}

/// <param name="Code">The backend's refusal code (<c>unknown_avatar</c>, <c>too_large</c>, <c>not_an_image</c>,
/// <c>too_big_dimensions</c>), when it sent one.</param>
public sealed record PictureResult(PictureOutcome Outcome, MemberPicture? Picture = null, string? Code = null, string? Detail = null);

/// <summary><c>PUT me/picture/</c> with a library avatar.</summary>
public sealed record AvatarPictureRequest(string Kind, string Key);

/// <summary>POST /api/devices/messages/ — the member replies in the thread.</summary>
public sealed record MemberReplyRequest(string Body);

/// <summary>POST /api/devices/feedback/ — the member's feedback on Zinwe from the Zinwe window (FR-14).</summary>
public sealed record MemberFeedbackRequest(int Rating, string Message);

/// <summary><see cref="Accepted"/> = newly stored; <see cref="Duplicates"/> = ids the backend already had (a retry).</summary>
public sealed record EventUploadResponse(int Accepted, int Duplicates);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    NumberHandling = JsonNumberHandling.AllowReadingFromString)]
[JsonSerializable(typeof(EnrollRequest))]
[JsonSerializable(typeof(EnrollResponse))]
[JsonSerializable(typeof(HeartbeatRequest))]
[JsonSerializable(typeof(HeartbeatResponse))]
[JsonSerializable(typeof(AgentPolicy))]
[JsonSerializable(typeof(EventUploadRequest))]
[JsonSerializable(typeof(EventUploadResponse))]
[JsonSerializable(typeof(AgentMessage[]))]
[JsonSerializable(typeof(MemberReplyRequest))]
[JsonSerializable(typeof(MemberFeedbackRequest))]
[JsonSerializable(typeof(DeviceTask))]
[JsonSerializable(typeof(DeviceTask[]))]
[JsonSerializable(typeof(TaskActionRequest))]
[JsonSerializable(typeof(DeviceTodo))]
[JsonSerializable(typeof(DeviceTodo[]))]
[JsonSerializable(typeof(DeviceTodoList))]
[JsonSerializable(typeof(TodoSaveRequest))]
[JsonSerializable(typeof(TodoDeleteRequest))]
[JsonSerializable(typeof(AvatarPictureRequest))]
[JsonSerializable(typeof(MetricUploadRequest))]
[JsonSerializable(typeof(MetricUploadResponse))]
[JsonSerializable(typeof(Dictionary<string, string[]>))]
internal sealed partial class BackendJsonContext : JsonSerializerContext;

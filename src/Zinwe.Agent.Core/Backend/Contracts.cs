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
/// <param name="DayMoved">The member started their day on another PC: this PC's work session has moved there. The PC
/// itself stays connected and protected. Sent on every heartbeat of that day; null = the day isn't elsewhere.</param>
/// <param name="DayCarry">The member's day moved to this PC: what its timer carries on from. Sent on every heartbeat of
/// that day (the tray takes each one once); null = nothing to carry.</param>
/// <param name="WebListVersion">Names the set of sites this PC should hold ("" = none). It moves when a new list version
/// ships, which the policy version doesn't show. Null = an older backend.</param>
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
    JsonElement Picture = default,
    WorkDayMoved? DayMoved = null,
    WorkDayCarry? DayCarry = null,
    string? WebListVersion = null,
    WorkRules? WorkRules = null);

/// <summary>
/// v1.4 · the work rules in force for this PC (company ← department ← device), carried on the heartbeat. Null = an older
/// backend: no break-limit note. <paramref name="Days"/> is "1111110" (Monday first); times are "HH:mm".
/// </summary>
/// <param name="BreakAllowedMin">The break time allowed per day, in minutes (0 = no limit).</param>
/// <param name="TellMemberOver">Show the member how much of their break time they used, near and over the limit.</param>
public sealed record WorkRules(
    string? Days = null,
    string? Start = null,
    string? End = null,
    int BreakAllowedMin = 0,
    bool TellMemberOver = true,
    int LateGraceMin = 0,
    bool? WorkingToday = null,
    string? HolidayToday = null,
    string? LeaveToday = null);

/// <summary>Where the member's day went, and when (<see cref="HeartbeatResponse.DayMoved"/>).</summary>
public sealed record WorkDayMoved(string ToHostname, DateTimeOffset At);

/// <summary>
/// The member's day so far, wherever it ran, as it stood when it moved to this PC at <paramref name="At"/>
/// (<see cref="HeartbeatResponse.DayCarry"/>). <paramref name="FromHostname"/> is empty when the day had ended elsewhere
/// and this PC simply started again.
/// </summary>
public sealed record WorkDayCarry(
    DateTimeOffset At,
    long WorkedSeconds = 0,
    long BreakSeconds = 0,
    int Breaks = 0,
    DateTimeOffset? FirstStart = null,
    string? FromHostname = null);

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
/// <param name="File">v1.5: the file on this line (FileJSON, read leniently), or null. The widget then shows the file card and
/// <see cref="Chat.ChatFile.Caption"/> instead of <see cref="Body"/> (the server's fallback text).</param>
public sealed record AgentMessage(
    long Id, string Title, string? Body, string Severity, DateTimeOffset CreatedAt, string Sender = "owner",
    bool ReadByMember = false, string? Kind = null, DeviceTask? Task = null, bool ReadByOwner = false, Chat.ChatFile? File = null);

/// <summary>
/// A task the owner gave this PC's member (FR-20 v2, GET /api/devices/tasks/). Status: sent → seen → accepted →
/// progress → review → approved, side states help, cantdo (v1.4) and cancelled. Unknown extra fields are ignored, and
/// every field but the id has a default, so a backend that adds or drops one doesn't break the agent.
/// v1.4 (team tasks) adds the trailing fields: several people (<paramref name="People"/>), "any one of them" tasks
/// (<paramref name="Mode"/>), steps in order, step owners, the task chat summary and the device's own <paramref name="Me"/>
/// block. A 1.3 backend leaves them out: the task is then one person's (this member's), steps are anyone's.
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
    string? MemberName = null,
    string? Mode = null,
    bool? InOrder = null,
    string? Repeat = null,
    string? NextOn = null,
    long? GroupId = null,
    IReadOnlyList<DeviceTaskPerson>? People = null,
    string? CantdoNote = null,
    string? ReopenNote = null,
    DateTimeOffset? NudgedAt = null,
    DeviceTaskChat? Chat = null,
    long? Member = null,
    DeviceTaskMe? Me = null);

/// <summary>v1.4 · one person on a task: <paramref name="State"/> is sent | seen | progress | done | help | cantdo;
/// <paramref name="Note"/> their help / can't-do reason while it stands.</summary>
public sealed record DeviceTaskPerson(
    long MemberId,
    string Name = "",
    string? State = null,
    int StepsTotal = 0,
    int StepsDone = 0,
    DateTimeOffset? SeenAt = null,
    DateTimeOffset? DoneAt = null,
    string? Role = null,
    DateTimeOffset? StartedAt = null,
    string? Note = null);

/// <summary>v1.4 · the task chat in a line: how many messages, how many this member hasn't read, and the last one.</summary>
public sealed record DeviceTaskChat(int Count = 0, int Unread = 0, DeviceTaskChatLast? Last = null);

/// <summary>The last line of a task chat: <paramref name="By"/> is the writer's first name as the backend sends it.</summary>
public sealed record DeviceTaskChatLast(string? Text = null, string? By = null, DateTimeOffset? At = null);

/// <summary>
/// v1.4 · the device's own view of a task: whose turn it is (<paramref name="Turn"/>), which steps this member may tick
/// (<paramref name="CanTick"/>), their own state, and - when the backend sends it - their member id.
/// </summary>
/// <remarks><c>restart</c> = the owner reopened it and this member hasn't pressed Start again yet.</remarks>
public sealed record DeviceTaskMe(bool Turn = false, IReadOnlyList<long>? CanTick = null, string? State = null, long? MemberId = null,
    bool? Restart = null);

/// <summary>
/// One step of a <see cref="DeviceTask"/>. v1.4: <paramref name="OwnerId"/> = who does it (null = anyone on the task),
/// <paramref name="DoneById"/> / <paramref name="DoneAt"/> = who ticked it, and when.
/// </summary>
public sealed record DeviceTaskStep(long Id, string Title = "", bool Done = false, long? OwnerId = null, long? DoneById = null,
    DateTimeOffset? DoneAt = null);

/// <summary>One line of a task's history: <paramref name="By"/> is "owner" or "member"; v1.4 may say which member (<paramref name="MemberId"/>, <paramref name="MemberName"/>).</summary>
public sealed record DeviceTaskActivity(DateTimeOffset At, string By = "", string Action = "", string? Note = null,
    long? MemberId = null, string? MemberName = null);

/// <summary>
/// POST /api/devices/tasks/&lt;id&gt;/action/ — what the member did: "seen" | "accept" | "start" | "help" | "done" |
/// "note". A step tick is "note" with <paramref name="StepId"/> + <paramref name="StepDone"/>. Empty parts are left out.
/// </summary>
public sealed record TaskActionRequest(
    string Action,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Note = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? StepId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? StepDone = null);

/// <summary>
/// The member's task actions (the contract's names). 1.3: seen · accept · start · help · done · note (a step tick is
/// "note" + step id). v1.4 (team tasks) adds tick / untick (a step), cantdo and finish (a task without steps), and
/// "start" takes an "any one of them" task. <see cref="ForBackend"/> turns a v1.4 action into its 1.3 form for a task
/// from a backend without team tasks.
/// </summary>
public static class TaskActions
{
    public const string Seen = "seen";
    public const string Accept = "accept";
    public const string Start = "start";
    public const string Help = "help";
    public const string Done = "done";
    public const string Note = "note";
    public const string Tick = "tick";
    public const string Untick = "untick";
    public const string Cantdo = "cantdo";
    public const string Finish = "finish";

    public static bool IsKnown(string? action) => action is Seen or Accept or Start or Help or Done or Note
        or Tick or Untick or Cantdo or Finish;

    /// <summary>
    /// What to post for <paramref name="action"/>: as is to a v1.4 backend (<paramref name="teamTasks"/>), else the 1.3
    /// words: tick / untick = "note" with the step, finish = "done", cantdo = "help" with "Can’t do it: …".
    /// </summary>
    public static (string Action, string? Note, long? StepId, bool? StepDone) ForBackend(string action, string? note, long? stepId,
        bool teamTasks)
    {
        if (teamTasks)
        {
            return (action, note, stepId, null);
        }
        return action switch
        {
            Tick => (Note, null, stepId, true),
            Untick => (Note, null, stepId, false),
            Finish => (Done, note, null, null),
            Cantdo => (Help, "Can’t do it: " + (string.IsNullOrWhiteSpace(note) ? "no reason given" : note.Trim()), null, null),
            _ => (action, note, stepId, stepId is null ? null : true),
        };
    }
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
/// <param name="FileId">v1.5: the uploaded file this line carries (the body is then its caption and may be blank).</param>
public sealed record MemberReplyRequest(
    string Body,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? FileId = null);

/// <summary>v1.4 · POST a line to a group / direct chat (<c>chat/&lt;id&gt;/messages/</c>) or a task chat (<c>tasks/&lt;id&gt;/chat/</c>).</summary>
/// <param name="FileId">v1.5: the uploaded file this line carries (the body is then its caption and may be blank).</param>
public sealed record ChatMessageRequest(
    string Body,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? ReplyTo = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? FileId = null);

/// <summary>
/// v1.5 · what became of a file line the agent posted: the outcome, the field the backend refused (<c>file_id</c> = the
/// upload expired: upload it again) and its own sentence.
/// </summary>
public sealed record FilePostResult(ChatWriteOutcome Outcome, string? Field = null, string? Error = null);

/// <summary>v1.5 · what became of a registration: the file's record (not on a line yet), or why not.</summary>
public sealed record FileRegisterResult(ChatWriteOutcome Outcome, Chat.ChatFile? File = null, string? Error = null, int Status = 0);

/// <summary>
/// v1.5 round 2 · POST chat/files/: register a file that stays on this PC (JSON, no bytes). <paramref name="LocalRef"/> is
/// opaque to the server (the path here); the thumbnail is a ≤ 40 KB JPEG as base64; the hash may follow by PATCH.
/// </summary>
public sealed record FileRegisterRequest(
    string Name,
    long Size,
    string Mime,
    [property: JsonPropertyName("local_ref")] string LocalRef,
    [property: JsonPropertyName("sha256"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Sha256 = null,
    [property: JsonPropertyName("thumb_b64"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ThumbB64 = null,
    [property: JsonPropertyName("width"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Width = null,
    [property: JsonPropertyName("height"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Height = null,
    [property: JsonPropertyName("duration_s"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? DurationS = null,
    [property: JsonPropertyName("batch_id"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? BatchId = null);

/// <summary>v1.5 round 2 · PATCH chat/files/&lt;id&gt;/ (the holder only): the hash once made, or "gone" (changed, moved or deleted here).</summary>
public sealed record FilePatchRequest(
    [property: JsonPropertyName("sha256"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Sha256 = null,
    [property: JsonPropertyName("gone"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? Gone = null);

/// <summary>v1.5 round 2 · a get ticket asked for one file: the ticket, or Gone (410/404: "isn't shared any more"), or Off.</summary>
public sealed record FileTicketResult(ChatWriteOutcome Outcome, Chat.FileTicket? Ticket = null, string? Error = null);

/// <summary>v1.5 · what became of a download: stored in the stream, gone (expired, not allowed), too big, or refused.</summary>
public enum FileFetchOutcome
{
    Done,
    Gone,
    TooBig,
    Refused,
}

/// <param name="Name">The name from the reply's Content-Disposition (null when it sent none).</param>
public sealed record FileFetchResult(FileFetchOutcome Outcome, string? Name = null, string? ContentType = null);

/// <summary>v1.4 · POST chat/&lt;id&gt;/report/: a message (or, without one, the person) goes to the admin with the note.</summary>
public sealed record ChatReportRequest(
    string Note,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? MessageId = null);

/// <summary>v1.4 · PATCH chat/&lt;id&gt;/: the member's own pin / mute / "mark as unread" for a conversation. Unset parts are left out.</summary>
public sealed record ChatPrefsRequest(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? Pinned = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? Muted = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? Unread = null);

/// <summary>v1.4 · POST chat/direct/: open (or find) the direct chat with a teammate.</summary>
public sealed record DirectChatRequest(long MemberId);

/// <summary>What became of a chat line, report or direct chat the agent sent (v1.4).</summary>
public enum ChatWriteOutcome
{
    /// <summary>Stored.</summary>
    Done,
    /// <summary>Refused for good (a 4xx other than 403/404/405/429): sending it again won't help.</summary>
    Refused,
    /// <summary>403/404/405: a backend (or plan) without it.</summary>
    Off,
    /// <summary>v1.4.1 · 404 on one conversation's own route: that group (or chat) was deleted. Only it goes, not the chat.</summary>
    Gone,
}

/// <summary>
/// v1.4.1 · what became of an edit, a delete or a day-off ask: the outcome, the backend's own sentence when it refused
/// ("You can change a message for 15 minutes after sending it."), and the line it sent back (edit / delete).
/// </summary>
public sealed record ChatWriteResult(ChatWriteOutcome Outcome, string? Error = null, Chat.TeamMessage? Line = null);

/// <summary>v1.4.1 · POST /api/devices/leave/: ask for a day (or half a day) off. <paramref name="Date"/> is "yyyy-MM-dd".</summary>
public sealed record LeaveRequest(string Date, bool Half, string Note);

/// <summary>POST /api/devices/feedback/ — the member's feedback on Zinwe from the Zinwe window (FR-14).</summary>
public sealed record MemberFeedbackRequest(int Rating, string Message);

/// <summary>
/// POST /api/devices/issues/ — "Report an issue" from the Zinwe window (F-13). It goes to the Zinwe team, not the owner.
/// <paramref name="ScreenshotB64"/> is a PNG of the window, base64 (at most <see cref="MaxScreenshotBytes"/> decoded);
/// left out when the member didn't attach one.
/// </summary>
public sealed record MemberIssueRequest(
    string Description,
    string Page,
    MemberIssueContext Context,
    [property: JsonPropertyName("screenshot_b64"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ScreenshotB64 = null)
{
    public const string WindowPage = "Zinwe window";
    public const int MaxDescriptionLength = 2000;
    public const int MaxScreenshotBytes = 5 * 1024 * 1024;
}

/// <summary>Where the member was: the agent's version, Windows, and the window's screen ("Tasks", "Me › Settings").</summary>
public sealed record MemberIssueContext(string AppVersion, string Os, string Screen);

/// <summary><see cref="Accepted"/> = newly stored; <see cref="Duplicates"/> = ids the backend already had (a retry).</summary>
public sealed record EventUploadResponse(int Accepted, int Duplicates);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    NumberHandling = JsonNumberHandling.AllowReadingFromString)]
[JsonSerializable(typeof(EnrollRequest))]
[JsonSerializable(typeof(EnrollResponse))]
[JsonSerializable(typeof(HeartbeatRequest))]
[JsonSerializable(typeof(HeartbeatResponse))]
[JsonSerializable(typeof(AgentPolicy))]
[JsonSerializable(typeof(Zinwe.Agent.Core.Features.Web.WebBlockSet))]
[JsonSerializable(typeof(EventUploadRequest))]
[JsonSerializable(typeof(EventUploadResponse))]
[JsonSerializable(typeof(AgentMessage[]))]
[JsonSerializable(typeof(MemberReplyRequest))]
[JsonSerializable(typeof(MemberFeedbackRequest))]
[JsonSerializable(typeof(MemberIssueRequest))]
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
[JsonSerializable(typeof(ChatMessageRequest))]
[JsonSerializable(typeof(ChatReportRequest))]
[JsonSerializable(typeof(DirectChatRequest))]
[JsonSerializable(typeof(ChatPrefsRequest))]
[JsonSerializable(typeof(LeaveRequest))]
[JsonSerializable(typeof(FileRegisterRequest))]
[JsonSerializable(typeof(FilePatchRequest))]
internal sealed partial class BackendJsonContext : JsonSerializerContext;

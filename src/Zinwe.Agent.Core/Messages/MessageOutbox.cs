using System.Text.Json;
using System.Text.Json.Serialization;
using Zinwe.Agent.Core.Backend;

namespace Zinwe.Agent.Core.Messages;

/// <summary>
/// One pending item waiting to be posted. Id is a client guid (for removal after posting). <see cref="Kind"/> is
/// "reply" (a chat line to the admin), "feedback" (FR-14: the member's rating of Zinwe, 1-10, for the Zinwe team) or
/// "task_action" (FR-20 v2: what the member did on an owner's task - <see cref="TaskId"/>, <see cref="Action"/>,
/// <see cref="Body"/> = the note, and for a step tick <see cref="StepId"/> + <see cref="StepDone"/>) or "picture"
/// (FR-44: the member's new picture - <see cref="Action"/> "avatar" / "photo" / "remove", <see cref="Body"/> = the
/// avatar key or the photo's JPEG as base64; only the newest one is kept, and only the service writes it) or "todo"
/// (one of the member's own to-dos as it stands now - <see cref="TodoId"/> = its client id, <see cref="Body"/> = the
/// <see cref="DeviceTodo"/> as JSON; only the newest state of each to-do is kept) or "issue" (F-13: "Report an issue"
/// for the Zinwe team - <see cref="Body"/> = the <see cref="MemberIssueRequest"/> as JSON, screenshot included).
/// The task fields are trailing and optional, so files written by older builds still read; builds before 1.3.1
/// posted every kind they didn't know as chat, which is why picture, todo and issue items are written only while a
/// service that knows them is running.
/// <see cref="Refused"/> marks a chat reply the backend turned down for good: it stays here, shown as "Not sent", and
/// isn't posted again until the member presses Retry.
/// </summary>
public sealed record OutboxReply(
    string Id, string Body, DateTimeOffset CreatedAtUtc, string Kind = OutboxReply.ReplyKind, int? Rating = null,
    long? TaskId = null, string? Action = null, long? StepId = null, bool? StepDone = null, bool Refused = false,
    string? TodoId = null, string? ChatKey = null, long? MessageId = null, long? MemberId = null,
    string? FilePath = null, string? FileName = null, long? FileSize = null, string? FileId = null, DateTimeOffset? UploadedAt = null,
    string? Error = null, string? BatchId = null, string? Mime = null, string? ThumbB64 = null, int? Width = null, int? Height = null,
    double? DurationS = null, bool Copied = false)
{
    /// <summary>
    /// v1.5 · a file to send in a chat: <see cref="ChatKey"/> ("conv:12", "direct:4", or <see cref="AdminKey"/> for the admin
    /// chat), <see cref="FilePath"/> = where the file stays (round 2: the member's own path, or our copy of a file on a
    /// network or removable drive, <see cref="Copied"/>), <see cref="FileName"/> / <see cref="FileSize"/> for the "Sending…"
    /// bubble, <see cref="Body"/> = the caption (only on the last file of a batch). Round 2 adds the batch, the mime, the
    /// tray's thumbnail (a ≤ 40 KB JPEG as base64), the size in pixels and a video's length. The service registers it,
    /// keeps the <see cref="FileId"/> (and <see cref="UploadedAt"/>) here so a network error never registers twice, then
    /// posts the line with that file_id. Refused for good = <see cref="Refused"/> with <see cref="Error"/> (the words to
    /// show): "Failed", with Retry. Older services leave this kind alone.
    /// </summary>
    public const string ChatFileKind = "chat_file";

    /// <summary>The chat key of the admin chat (a file to the admin goes to <c>POST messages/</c>).</summary>
    public const string AdminKey = "admin";

    [JsonIgnore]
    public bool IsChatFile => string.Equals(Kind, ChatFileKind, StringComparison.Ordinal);

    public const string ReplyKind = "reply";
    public const string FeedbackKind = "feedback";
    public const string TaskActionKind = "task_action";
    public const string PictureKind = "picture";
    public const string TodoKind = "todo";
    public const string IssueKind = "issue";
    /// <summary>
    /// v1.4 team chat: <see cref="Action"/> "send" (<see cref="Body"/> to the thread <see cref="ChatKey"/>, quoting
    /// <see cref="MessageId"/>), "report" (<see cref="MessageId"/> or the person in <see cref="ChatKey"/>, the note in
    /// <see cref="Body"/>) or "direct" (start the chat with <see cref="MemberId"/>). Written only while a service that knows
    /// them runs (<c>AgentStatus.ChatVersion</c>).
    /// </summary>
    public const string ChatKind = "chat";
    public const string ChatSend = "send";
    public const string ChatReport = "report";
    public const string ChatDirect = "direct";
    /// <summary>The member's own list choices for a conversation (PATCH chat/&lt;id&gt;/): pin, unpin, mute, unmute, unread, read.</summary>
    public const string ChatPin = "pin";
    public const string ChatUnpin = "unpin";
    public const string ChatMute = "mute";
    public const string ChatUnmute = "unmute";
    public const string ChatUnread = "unread";
    public const string ChatRead = "read";
    /// <summary>
    /// v1.4.1 · "edit" (<see cref="MessageId"/> in <see cref="ChatKey"/> gets <see cref="Body"/>), "delete" (that line goes
    /// for everyone), "leave" (<see cref="ChatKey"/> = <see cref="LeaveKey"/>, <see cref="Body"/> = the
    /// <see cref="LeaveRequest"/> as JSON). Written only while a 1.4.1 service runs (a 1.4.0 one drops what it doesn't know).
    /// </summary>
    public const string ChatEdit = "edit";
    public const string ChatDelete = "delete";
    public const string ChatLeave = "leave";
    public const string LeaveKey = "leave";

    [JsonIgnore]
    public bool IsChat => string.Equals(Kind, ChatKind, StringComparison.Ordinal);

    public bool IsFeedback => string.Equals(Kind, FeedbackKind, StringComparison.Ordinal);

    [JsonIgnore]
    public bool IsIssue => string.Equals(Kind, IssueKind, StringComparison.Ordinal);

    [JsonIgnore]
    public bool IsTaskAction => string.Equals(Kind, TaskActionKind, StringComparison.Ordinal);

    [JsonIgnore]
    public bool IsPicture => string.Equals(Kind, PictureKind, StringComparison.Ordinal);

    [JsonIgnore]
    public bool IsTodo => string.Equals(Kind, TodoKind, StringComparison.Ordinal);

    /// <summary>A line of the chat (not feedback, not a task action, nor a kind a newer build added).</summary>
    [JsonIgnore]
    public bool IsChatReply => Kind is null or "" || string.Equals(Kind, ReplyKind, StringComparison.Ordinal);
}

/// <summary>
/// The member's outbound replies, written by the WIDGET and drained by the AGENT via a shared per-user file
/// (<c>%LOCALAPPDATA%\Zinwe\messages-outbox.json</c>) — the same cross-process pattern as the notifications file.
/// Both ends go through this type. Every operation is locked + best-effort (a read/write fault is swallowed).
/// </summary>
public sealed class MessageOutbox
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private const string FileName = "messages-outbox.json";

    private readonly IWidgetFolder _folder;
    private readonly Lock _lock = new();

    /// <summary>The current user's own outbox (the widget; tests). <paramref name="directory"/> defaults to %LOCALAPPDATA%\Zinwe.</summary>
    public MessageOutbox(string? directory = null) : this(new LocalWidgetFolder(directory))
    {
    }

    /// <summary>The outbox in whichever user's folder <paramref name="folder"/> resolves (the service: the signed-in member).</summary>
    public MessageOutbox(IWidgetFolder folder) => _folder = folder;

    /// <summary>The file path right now ("" when nobody is signed in).</summary>
    public string FilePath => _folder.Use(dir => Path.Combine(dir, FileName), "");

    private sealed class OutboxFile
    {
        public List<OutboxReply> Items { get; set; } = [];
    }

    /// <summary>Queue a reply to be posted by the agent. Returns the new item's id.</summary>
    public string Append(string body) => Add(new OutboxReply(Guid.NewGuid().ToString("N"), body, DateTimeOffset.UtcNow));

    /// <summary>Queue the member's feedback on Zinwe (FR-14; rating 1-10, clamped). Returns the new item's id.</summary>
    public string AppendFeedback(int rating, string message) => Add(new OutboxReply(
        Guid.NewGuid().ToString("N"), message, DateTimeOffset.UtcNow, OutboxReply.FeedbackKind, Math.Clamp(rating, 1, 10)));

    /// <summary>
    /// Queue "Report an issue" (F-13) for the agent to post. Returns the item, or null when it couldn't be written (nobody
    /// signed in, or the file is locked). Only the tray calls this, and only while a service that knows issues is running.
    /// </summary>
    public OutboxReply? AppendIssue(MemberIssueRequest issue)
    {
        var body = JsonSerializer.Serialize(issue, BackendJsonContext.Default.MemberIssueRequest);
        var item = new OutboxReply(Guid.NewGuid().ToString("N"), body, DateTimeOffset.UtcNow, OutboxReply.IssueKind);
        lock (_lock)
        {
            return _folder.Use(dir =>
            {
                var path = Path.Combine(dir, FileName);
                var file = Load(path);
                file.Items.Add(item);
                return Save(path, file) ? item : null;
            }, null);
        }
    }

    /// <summary>Issue reports still waiting to be posted, oldest first.</summary>
    public IReadOnlyList<OutboxReply> PendingIssues() => [.. All().Where(i => i.IsIssue)];

    /// <summary>The report in a queued item; null when the item can't be read.</summary>
    public static MemberIssueRequest? ReadIssue(OutboxReply item)
    {
        try
        {
            return JsonSerializer.Deserialize(item.Body, BackendJsonContext.Default.MemberIssueRequest) is { Description.Length: > 0 } issue
                ? issue : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Queue what the member did on an owner's task (FR-20 v2). The agent posts it to
    /// <c>tasks/&lt;id&gt;/action/</c>; it stays here, and is retried, until the backend stored or refused it.
    /// </summary>
    public OutboxReply AppendTaskAction(long taskId, string action, string? note = null, long? stepId = null, bool? stepDone = null)
    {
        var item = new OutboxReply(Guid.NewGuid().ToString("N"), (note ?? "").Trim(), DateTimeOffset.UtcNow,
            OutboxReply.TaskActionKind, TaskId: taskId, Action: action, StepId: stepId, StepDone: stepId is null ? null : stepDone);
        Add(item);
        return item;
    }

    /// <summary>v1.4 · queue a team chat line, report or new direct chat (see <see cref="OutboxReply.ChatKind"/>).</summary>
    public OutboxReply AppendChat(string action, string chatKey, string? body = null, long? messageId = null, long? memberId = null)
    {
        var item = new OutboxReply(Guid.NewGuid().ToString("N"), (body ?? "").Trim(), DateTimeOffset.UtcNow, OutboxReply.ChatKind,
            Action: action, ChatKey: chatKey, MessageId: messageId, MemberId: memberId);
        Add(item);
        return item;
    }

    /// <summary>v1.4 · team chat items still waiting, oldest first.</summary>
    public IReadOnlyList<OutboxReply> PendingChat() => [.. All().Where(i => i.IsChat)];

    /// <summary>v1.4.1 · queue an ask for a day off (posted by the service with the chat items).</summary>
    public OutboxReply AppendLeave(LeaveRequest request) =>
        AppendChat(OutboxReply.ChatLeave, OutboxReply.LeaveKey, JsonSerializer.Serialize(request, BackendJsonContext.Default.LeaveRequest));

    /// <summary>The day-off ask in a queued item; null when it can't be read.</summary>
    public static LeaveRequest? ReadLeave(OutboxReply item)
    {
        try
        {
            return JsonSerializer.Deserialize(item.Body, BackendJsonContext.Default.LeaveRequest) is { Date.Length: > 0 } request ? request : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>v1.4.1 · drop every waiting chat item for one thread (that group was deleted).</summary>
    public void RemoveChat(string chatKey) => Remove([.. All().Where(i => i.IsChat && i.ChatKey == chatKey).Select(i => i.Id)]);

    /// <summary>
    /// Queue the member's new picture (FR-44), replacing any earlier one still waiting (the newest choice wins).
    /// Returns the item, or null when it couldn't be written (nobody signed in, or the file is locked).
    /// </summary>
    public OutboxReply? ReplacePicture(string action, string body)
    {
        var item = new OutboxReply(Guid.NewGuid().ToString("N"), body, DateTimeOffset.UtcNow, OutboxReply.PictureKind, Action: action);
        lock (_lock)
        {
            return _folder.Use(dir =>
            {
                var path = Path.Combine(dir, FileName);
                var file = Load(path);
                file.Items.RemoveAll(i => i.IsPicture);
                file.Items.Add(item);
                return Save(path, file) ? item : null;
            }, null);
        }
    }

    /// <summary>The member's picture change still waiting to be sent (the newest, when an older build left several).</summary>
    public OutboxReply? PendingPicture() => All().LastOrDefault(i => i.IsPicture);

    /// <summary>Drop every waiting picture change (the PC was removed, or connected as someone else).</summary>
    public void RemovePictures() => Remove([.. All().Where(i => i.IsPicture).Select(i => i.Id)]);

    /// <summary>True while anything but a picture change or a file waits (those keep their own retry pace).</summary>
    public bool HasPendingOtherThanPicture => All().Any(i => !i.IsPicture && !i.IsChatFile && !i.Refused);

    /// <summary>
    /// v1.5 · queue the files of one send (see <see cref="OutboxReply.ChatFileKind"/>), in one write so a batch never goes
    /// half. Each gets a fresh id and "now" (in order). Returns them, or null when they couldn't be written. Only the tray
    /// calls this, and only while a service that sends files is running.
    /// </summary>
    public IReadOnlyList<OutboxReply>? AppendChatFiles(IReadOnlyList<OutboxReply> files)
    {
        var now = DateTimeOffset.UtcNow;
        var items = files.Select((f, i) => f with
        {
            Id = Guid.NewGuid().ToString("N"), Kind = OutboxReply.ChatFileKind, CreatedAtUtc = now.AddTicks(i), Refused = false,
        }).ToList();
        lock (_lock)
        {
            return _folder.Use<IReadOnlyList<OutboxReply>?>(dir =>
            {
                var file = Path.Combine(dir, FileName);
                var all = Load(file);
                all.Items.AddRange(items);
                return Save(file, all) ? items : null;
            }, null);
        }
    }

    /// <summary>v1.5 · files still waiting (or failed), oldest first.</summary>
    public IReadOnlyList<OutboxReply> PendingFiles() => [.. All().Where(i => i.IsChatFile)];

    /// <summary>v1.5 · change one waiting item in place (the stored file id, a refusal). False when it is gone.</summary>
    public bool Update(string id, Func<OutboxReply, OutboxReply> change)
    {
        lock (_lock)
        {
            return _folder.Use(dir =>
            {
                var path = Path.Combine(dir, FileName);
                var file = Load(path);
                var index = file.Items.FindIndex(i => i.Id == id);
                if (index < 0)
                {
                    return false;
                }
                file.Items[index] = change(file.Items[index]);
                return Save(path, file);
            }, false);
        }
    }

    /// <summary>Task actions still waiting to be posted, oldest first.</summary>
    public IReadOnlyList<OutboxReply> PendingTaskActions() => [.. All().Where(i => i.IsTaskAction)];

    /// <summary>
    /// Queue one of the member's own to-dos as it stands now, replacing any older state of it still waiting (ten ticks
    /// offline are one write). Returns the item, or null when it couldn't be written (nobody signed in, or the file is
    /// locked). Only the tray calls this, and only while a service that knows to-dos is running.
    /// </summary>
    public OutboxReply? ReplaceTodo(DeviceTodo todo)
    {
        var body = JsonSerializer.Serialize(todo, BackendJsonContext.Default.DeviceTodo);
        var item = new OutboxReply(Guid.NewGuid().ToString("N"), body, DateTimeOffset.UtcNow, OutboxReply.TodoKind, TodoId: todo.ClientId);
        lock (_lock)
        {
            return _folder.Use(dir =>
            {
                var path = Path.Combine(dir, FileName);
                var file = Load(path);
                file.Items.RemoveAll(i => i.IsTodo && i.TodoId == todo.ClientId);
                file.Items.Add(item);
                return Save(path, file) ? item : null;
            }, null);
        }
    }

    /// <summary>To-dos still waiting to be sent, oldest first.</summary>
    public IReadOnlyList<OutboxReply> PendingTodos() => [.. All().Where(i => i.IsTodo)];

    /// <summary>The to-do in a queued item; null when the item can't be read.</summary>
    public static DeviceTodo? ReadTodo(OutboxReply item)
    {
        try
        {
            return JsonSerializer.Deserialize(item.Body, BackendJsonContext.Default.DeviceTodo) is { ClientId.Length: > 0 } todo ? todo : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Drop every waiting to-do (the PC was removed or connected as someone else, or the plan has no tasks).</summary>
    public void RemoveTodos() => Remove([.. All().Where(i => i.IsTodo).Select(i => i.Id)]);

    /// <summary>
    /// Take back a task action that hasn't been posted yet (Undo). True when it was still waiting: once the agent
    /// posted it, it's gone from the file and the caller sends the opposite action instead.
    /// </summary>
    public bool TryRemove(string id)
    {
        lock (_lock)
        {
            return _folder.Use(dir =>
            {
                var path = Path.Combine(dir, FileName);
                var file = Load(path);
                if (file.Items.RemoveAll(i => i.Id == id) == 0)
                {
                    return false;
                }
                Save(path, file);
                return true;
            }, false);
        }
    }

    private string Add(OutboxReply reply)
    {
        lock (_lock)
        {
            _folder.Use(dir =>
            {
                var path = Path.Combine(dir, FileName);
                var file = Load(path);
                file.Items.Add(reply);
                Save(path, file);
                return true;
            }, false);
        }
        return reply.Id;
    }

    /// <summary>Chat replies still waiting to be posted, oldest first (feedback and task actions are not part of the chat).</summary>
    public IReadOnlyList<OutboxReply> Pending() => [.. All().Where(i => i.IsChatReply)];

    /// <summary>Chat replies the agent should post now: <see cref="Pending"/> without the ones the backend refused.</summary>
    public IReadOnlyList<OutboxReply> Sendable() => [.. Pending().Where(i => !i.Refused)];

    /// <summary>The backend turned these replies down: keep them for the member, don't post them again.</summary>
    public void MarkRefused(IEnumerable<string> ids) => SetRefused(ids, true);

    /// <summary>The member pressed Retry: the agent posts this reply again on its next round.</summary>
    public void Retry(string id) => SetRefused([id], false);

    private void SetRefused(IEnumerable<string> ids, bool refused)
    {
        var set = new HashSet<string>(ids, StringComparer.Ordinal);
        if (set.Count == 0)
        {
            return;
        }
        lock (_lock)
        {
            _folder.Use(dir =>
            {
                var path = Path.Combine(dir, FileName);
                var file = Load(path);
                file.Items = [.. file.Items.Select(i => set.Contains(i.Id) ? i with { Refused = refused } : i)];
                Save(path, file);
                return true;
            }, false);
        }
    }

    /// <summary>Feedback still waiting to be posted, oldest first.</summary>
    public IReadOnlyList<OutboxReply> PendingFeedback() => [.. All().Where(i => i.IsFeedback)];

    /// <summary>Everything waiting, oldest first.</summary>
    public IReadOnlyList<OutboxReply> All()
    {
        lock (_lock)
        {
            return _folder.Use<IReadOnlyList<OutboxReply>>(
                dir => [.. Load(Path.Combine(dir, FileName)).Items.OrderBy(i => i.CreatedAtUtc)], []);
        }
    }

    /// <summary>Remove the given ids after they've been posted.</summary>
    public void Remove(IEnumerable<string> ids)
    {
        var set = new HashSet<string>(ids, StringComparer.Ordinal);
        if (set.Count == 0)
        {
            return;
        }
        lock (_lock)
        {
            _folder.Use(dir =>
            {
                var path = Path.Combine(dir, FileName);
                var file = Load(path);
                if (file.Items.RemoveAll(i => set.Contains(i.Id)) > 0)
                {
                    Save(path, file);
                }
                return true;
            }, false);
        }
    }

    private static OutboxFile Load(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var file = JsonSerializer.Deserialize<OutboxFile>(File.ReadAllText(path), Json);
                if (file is not null)
                {
                    file.Items.RemoveAll(i => i is null || string.IsNullOrWhiteSpace(i.Id));
                    return file;
                }
            }
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            // unreadable / mid-write: treat as empty
        }
        return new OutboxFile();
    }

    private static bool Save(string path, OutboxFile file)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(file, Json));
            File.Move(tmp, path, overwrite: true);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false; // best-effort
        }
    }
}

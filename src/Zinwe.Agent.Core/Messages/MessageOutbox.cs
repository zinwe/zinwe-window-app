using System.Text.Json;
using System.Text.Json.Serialization;

namespace Zinwe.Agent.Core.Messages;

/// <summary>
/// One pending item waiting to be posted. Id is a client guid (for removal after posting). <see cref="Kind"/> is
/// "reply" (a chat line to the admin), "feedback" (FR-14: the member's rating of Zinwe, 1-10, for the Zinwe team) or
/// "task_action" (FR-20 v2: what the member did on an owner's task - <see cref="TaskId"/>, <see cref="Action"/>,
/// <see cref="Body"/> = the note, and for a step tick <see cref="StepId"/> + <see cref="StepDone"/>).
/// The task fields are trailing and optional, so files written by older builds still read.
/// </summary>
public sealed record OutboxReply(
    string Id, string Body, DateTimeOffset CreatedAtUtc, string Kind = OutboxReply.ReplyKind, int? Rating = null,
    long? TaskId = null, string? Action = null, long? StepId = null, bool? StepDone = null)
{
    public const string ReplyKind = "reply";
    public const string FeedbackKind = "feedback";
    public const string TaskActionKind = "task_action";

    public bool IsFeedback => string.Equals(Kind, FeedbackKind, StringComparison.Ordinal);

    [JsonIgnore]
    public bool IsTaskAction => string.Equals(Kind, TaskActionKind, StringComparison.Ordinal);

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

    /// <summary>Task actions still waiting to be posted, oldest first.</summary>
    public IReadOnlyList<OutboxReply> PendingTaskActions() => [.. All().Where(i => i.IsTaskAction)];

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

    private static void Save(string path, OutboxFile file)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(file, Json));
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // best-effort
        }
    }
}

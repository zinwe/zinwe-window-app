using System.Globalization;
using System.Text.Json;
using Zinwe.Agent.Core.Ipc;

namespace Zinwe.Agent.Core.Chat;

// v1.4 team chat on the device (§1b /api/devices/chat/*, plus the task chat of §1a). The backend's JSON is read
// leniently (ChatJson) into these plain records, which is also what the service hands the tray over the pipe. Every
// member but the id has a default and new members are only ever added at the end, so an older tray reads a newer
// service and the other way round.

/// <summary>A teammate: someone in a group, the other side of a direct chat, or a person this member may message.</summary>
/// <param name="Status">"Working", "On a break"… when the backend says; null otherwise.</param>
public sealed record TeamMember(long MemberId, string Name = "", string? Role = null, string? Status = null);

/// <summary>
/// One conversation of the member's list: a group (the owner is in every group) or a direct chat with a teammate. The
/// admin chat is not one of these: it stays on /api/devices/messages/.
/// </summary>
/// <param name="Kind">"group" or "direct".</param>
/// <param name="Announce">Only the owner posts here: the member reads it.</param>
/// <param name="WithMemberId">A direct chat: the teammate on the other side.</param>
/// <param name="ReadOnly">The member can't post: they left the group, or the teammate left (history stays 30 days).</param>
public sealed record TeamConversation(
    long Id,
    string Kind = TeamConversation.GroupKind,
    string Name = "",
    string? Icon = null,
    bool Announce = false,
    IReadOnlyList<TeamMember>? Members = null,
    long? WithMemberId = null,
    int Unread = 0,
    TeamMessage? Last = null,
    DateTimeOffset? LastAt = null,
    bool Muted = false,
    bool Pinned = false,
    bool ReadOnly = false)
{
    public const string GroupKind = "group";
    public const string DirectKind = "direct";

    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsGroup => !string.Equals(Kind, DirectKind, StringComparison.OrdinalIgnoreCase);

    /// <summary>The thread key the pipe and the outbox use for it.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string Key => ChatKeys.Conversation(Id);
}

/// <summary>
/// One line of a group, a direct chat or a task chat. <paramref name="ByKind"/> is "owner" (the admin) or "member";
/// <paramref name="Mine"/> is the backend's own flag when it sends one (else the tray compares
/// <paramref name="ByMemberId"/> with its member id). A <paramref name="TaskId"/> line is a task card. v1.5: a
/// <paramref name="File"/> line shows the file card and the file's caption instead of <paramref name="Body"/> (the server's
/// fallback text "📎 name (size)", which a backend or app without files shows as it is).
/// </summary>
public sealed record TeamMessage(
    long Id,
    string ByKind = "member",
    long? ByMemberId = null,
    string ByName = "",
    string Body = "",
    long? ReplyToId = null,
    string? ReplyToBy = null,
    string? ReplyToBody = null,
    long? TaskId = null,
    bool System = false,
    bool Edited = false,
    bool Deleted = false,
    DateTimeOffset At = default,
    int SeenBy = 0,
    IReadOnlyList<string>? SeenNames = null,
    bool? Mine = null,
    ChatFile? File = null)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public bool ByOwner => string.Equals(ByKind, "owner", StringComparison.OrdinalIgnoreCase);
}

/// <summary>The messages of the thread the member has open (a conversation or a task chat).</summary>
/// <param name="Supported">False when the backend has no such thread (a 1.3 backend's task: its notes stand in).</param>
public sealed record ChatThread(string Key, IReadOnlyList<TeamMessage>? Messages = null, bool Supported = true,
    DateTimeOffset? SyncedAt = null);

/// <summary>
/// The service's reply to a "chat" pipe request (v1.4): the member's conversations, the teammates they can message, and
/// the open thread. Every member has a default, so a "not supported" ack reads as <see cref="Supported"/> = false.
/// </summary>
/// <param name="Backend">See <see cref="TaskBackend"/>: Off = a backend (or plan) without team chat: the admin chat only.</param>
/// <param name="Version">Goes up whenever anything here changes; also on the status (<see cref="AgentStatus.ChatVersion"/>).</param>
/// <param name="Gone">v1.4.1 · conversations that were deleted (a group the owner deleted): the tray closes them and forgets
/// their local choices. Null from a 1.4.0 service.</param>
/// <param name="Leave">v1.4.1 · the member's recent asks for a day off. Null = a 1.4.0 service or a backend without leave
/// (the tray then shows no "Ask for a day off").</param>
/// <param name="Notices">v1.4.1 · what the backend refused from the outbox (an edit after 15 minutes, a day-off ask), each
/// shown once by its <see cref="ChatNotice.Id"/>.</param>
public sealed record ChatReply(
    bool Supported = false,
    string Backend = TaskBackend.Unknown,
    IReadOnlyList<TeamConversation>? Conversations = null,
    IReadOnlyList<TeamMember>? People = null,
    ChatThread? Thread = null,
    DateTimeOffset? SyncedAt = null,
    int Version = 0,
    IReadOnlyList<long>? Gone = null,
    IReadOnlyList<Work.LeaveAsk>? Leave = null,
    IReadOnlyList<ChatNotice>? Notices = null);

/// <summary>v1.4.1 · one outbox item the backend refused: its id, the thread, the action, and the words to show.</summary>
public sealed record ChatNotice(string Id, string Key = "", string Action = "", string Text = "", DateTimeOffset At = default);

/// <summary>The keys a thread goes by: "conv:12" (a group or direct chat), "task:6" (a task chat), "direct:4" (a direct
/// chat with member 4 that doesn't exist yet: the service starts it before the first message).</summary>
public static class ChatKeys
{
    public static string Conversation(long id) => "conv:" + id.ToString(CultureInfo.InvariantCulture);

    public static string Task(long id) => "task:" + id.ToString(CultureInfo.InvariantCulture);

    public static string Direct(long memberId) => "direct:" + memberId.ToString(CultureInfo.InvariantCulture);

    /// <summary>("conv" | "task" | "direct", id), or null for anything else.</summary>
    public static (string Kind, long Id)? Parse(string? key)
    {
        var i = key?.IndexOf(':') ?? -1;
        if (key is null || i <= 0)
        {
            return null;
        }
        var kind = key[..i];
        return kind is "conv" or "task" or "direct" && long.TryParse(key[(i + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0
            ? (kind, id)
            : null;
    }
}

/// <summary>
/// Reads the backend's chat JSON without trusting its exact shape: a list or a <c>{"results": […]}</c> page, ids as
/// numbers or strings, <c>by</c> as an object or a name, <c>seen_by</c> as a count or a list. Anything unreadable is
/// skipped, never thrown, so one odd line can't hide the whole chat.
/// </summary>
public static class ChatJson
{
    public static IReadOnlyList<TeamConversation> Conversations(string json) =>
        Items(json, "results", "conversations", "chats", "items").Select(Conversation).OfType<TeamConversation>().ToList();

    public static IReadOnlyList<TeamMessage> Messages(string json) =>
        Items(json, "results", "messages", "items").Select(Message).OfType<TeamMessage>().OrderBy(m => m.At).ThenBy(m => m.Id).ToList();

    public static IReadOnlyList<TeamMember> People(string json) =>
        Items(json, "results", "people", "members", "items").Select(Member).OfType<TeamMember>().ToList();

    /// <summary>One conversation object (also the reply to <c>POST direct/</c>, bare or as <c>{"conversation": …}</c>).</summary>
    public static TeamConversation? ConversationReply(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            return root.ValueKind == JsonValueKind.Object && root.TryGetProperty("conversation", out var inner)
                ? Conversation(inner)
                : Conversation(root);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static List<JsonElement> Items(string json, params string[] wrappers)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
            {
                foreach (var w in wrappers)
                {
                    if (root.TryGetProperty(w, out var list) && list.ValueKind == JsonValueKind.Array)
                    {
                        root = list;
                        break;
                    }
                }
                if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("groups", out var groups))
                {
                    // {"groups": […], "directs": […]}
                    var all = groups.ValueKind == JsonValueKind.Array ? groups.EnumerateArray().Select(e => e.Clone()).ToList() : [];
                    if (root.TryGetProperty("directs", out var directs) && directs.ValueKind == JsonValueKind.Array)
                    {
                        all.AddRange(directs.EnumerateArray().Select(e => e.Clone()));
                    }
                    return all;
                }
            }
            return root.ValueKind == JsonValueKind.Array ? [.. root.EnumerateArray().Select(e => e.Clone())] : [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public static TeamConversation? Conversation(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object || Long(e, "id") is not { } id)
        {
            return null;
        }
        var members = new List<TeamMember>();
        if (e.TryGetProperty("members", out var ms) && ms.ValueKind == JsonValueKind.Array)
        {
            members.AddRange(ms.EnumerateArray().Select(Member).OfType<TeamMember>());
        }
        else if (e.TryGetProperty("member_ids", out var ids) && ids.ValueKind == JsonValueKind.Array)
        {
            members.AddRange(ids.EnumerateArray().Select(x => LongOf(x)).OfType<long>().Select(x => new TeamMember(x)));
        }
        var kind = (Text(e, "kind") ?? Text(e, "type") ?? TeamConversation.GroupKind).ToLowerInvariant();
        TeamMember? with = null;
        foreach (var name in new[] { "with", "other", "member", "peer" })
        {
            if (e.TryGetProperty(name, out var w) && Member(w) is { } m)
            {
                with = m;
                break;
            }
        }
        long? withId = with?.MemberId ?? Long(e, "with_member_id") ?? Long(e, "member_id");
        if (kind == TeamConversation.DirectKind && withId is null && members.Count == 1)
        {
            withId = members[0].MemberId;
        }
        if (with is not null && members.All(m => m.MemberId != with.MemberId))
        {
            members.Add(with);
        }
        TeamMessage? last = null;
        foreach (var name in new[] { "last_message", "last" })
        {
            if (e.TryGetProperty(name, out var l) && l.ValueKind == JsonValueKind.Object)
            {
                last = Message(l) ?? LastLine(l);
                break;
            }
        }
        var title = Text(e, "name") ?? Text(e, "title") ?? with?.Name ?? "";
        if (kind == TeamConversation.DirectKind && title.Length == 0 && with is not null)
        {
            title = with.Name;
        }
        var readOnly = Bool(e, "read_only") ?? (Text(e, "left_at") is { Length: > 0 } || Bool(e, "left") == true);
        if (Bool(e, "can_post") == false && !(Bool(e, "announce") ?? false))
        {
            readOnly = true;
        }
        return new TeamConversation(
            id, kind == TeamConversation.DirectKind ? TeamConversation.DirectKind : TeamConversation.GroupKind, title, Text(e, "icon"),
            Bool(e, "announce") ?? false, members, withId,
            Int(e, "unread") ?? Int(e, "unread_count") ?? 0, last,
            Time(e, "last_message_at") ?? (last is { At: var la } && la != default ? la : null),
            Bool(e, "muted") ?? false, Bool(e, "pinned") ?? false, readOnly);
    }

    // A last line without an id: {"text": "...", "by": "Emily", "at": "..."}.
    private static TeamMessage? LastLine(JsonElement e)
    {
        var body = Text(e, "text") ?? Text(e, "body");
        if (body is null)
        {
            return null;
        }
        var (kind, mid, name) = By(e);
        return new TeamMessage(0, kind, mid, name, body, TaskId: Long(e, "task_id"), At: Time(e, "at") ?? Time(e, "created_at") ?? default,
            Mine: Bool(e, "mine"), File: e.TryGetProperty("file", out var file) ? ChatFiles.FromJson(file) : null);
    }

    public static TeamMessage? Message(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object || Long(e, "id") is not { } id)
        {
            return null;
        }
        var (kind, mid, name) = By(e);
        long? replyId = null;
        string? replyBy = null, replyBody = null;
        if (e.TryGetProperty("reply_to", out var r))
        {
            if (r.ValueKind == JsonValueKind.Object)
            {
                replyId = Long(r, "id");
                replyBy = By(r).Name is { Length: > 0 } rb ? rb : null;
                replyBody = Text(r, "body") ?? Text(r, "text");
            }
            else
            {
                replyId = LongOf(r);
            }
        }
        var seen = 0;
        List<string>? names = null;
        if (e.TryGetProperty("seen_by", out var s))
        {
            if (s.ValueKind == JsonValueKind.Array)
            {
                seen = s.GetArrayLength();
                names = [.. s.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String ? x.GetString()
                    : x.ValueKind == JsonValueKind.Object ? Text(x, "name") : null).OfType<string>()];
            }
            else
            {
                seen = LongOf(s) is { } n ? (int)Math.Clamp(n, 0, 10_000) : 0;
            }
        }
        long? taskId = Long(e, "task_id");
        if (taskId is null && e.TryGetProperty("task", out var task) && task.ValueKind == JsonValueKind.Object)
        {
            taskId = Long(task, "id");
        }
        return new TeamMessage(
            id, kind, mid, name, Text(e, "body") ?? Text(e, "text") ?? "", replyId, replyBy, replyBody, taskId,
            Bool(e, "system") ?? false, Bool(e, "edited") ?? (Text(e, "edited_at") is { Length: > 0 }),
            Bool(e, "deleted") ?? (Text(e, "deleted_at") is { Length: > 0 }),
            Time(e, "at") ?? Time(e, "created_at") ?? default, seen, names, Bool(e, "mine") ?? Bool(e, "is_mine"),
            e.TryGetProperty("file", out var file) ? ChatFiles.FromJson(file) : null);
    }

    private static (string Kind, long? MemberId, string Name) By(JsonElement e)
    {
        if (e.TryGetProperty("by", out var by))
        {
            if (by.ValueKind == JsonValueKind.Object)
            {
                return ((Text(by, "kind") ?? "member").ToLowerInvariant(), Long(by, "member_id") ?? Long(by, "id"), Text(by, "name") ?? "");
            }
            if (by.ValueKind == JsonValueKind.String)
            {
                var v = by.GetString() ?? "";
                return v is "owner" or "member" ? (v, Long(e, "member_id"), Text(e, "sender_name") ?? "") : ("member", Long(e, "member_id"), v);
            }
        }
        var sender = (Text(e, "sender") ?? "member").ToLowerInvariant();
        return (sender == "owner" ? "owner" : "member", Long(e, "sender_member_id") ?? Long(e, "member_id"), Text(e, "sender_name") ?? Text(e, "name") ?? "");
    }

    public static TeamMember? Member(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object || (Long(e, "member_id") ?? Long(e, "id")) is not { } id)
        {
            return null;
        }
        var status = Text(e, "status") ?? (Bool(e, "online") is { } on ? on ? "Online" : null : null);
        return new TeamMember(id, Text(e, "name") ?? Text(e, "full_name") ?? "", Text(e, "role"), status);
    }

    private static string? Text(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static long? Long(JsonElement e, string name) => e.TryGetProperty(name, out var v) ? LongOf(v) : null;

    private static long? LongOf(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.Number when v.TryGetInt64(out var n) => n,
        JsonValueKind.String when long.TryParse(v.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) => n,
        _ => null,
    };

    private static int? Int(JsonElement e, string name) => Long(e, name) is { } n ? (int)Math.Clamp(n, 0, 1_000_000) : null;

    private static bool? Bool(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) ? v.ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => null } : null;

    private static DateTimeOffset? Time(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.TryGetDateTimeOffset(out var at) ? at : null;
}

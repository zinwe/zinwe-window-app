using System.Text.Json;
using Microsoft.Extensions.Logging;
using Zinwe.Agent.Core.Backend;
using Zinwe.Agent.Core.Ipc;
using Zinwe.Agent.Core.Messages;
using Zinwe.Agent.Core.Work;

namespace Zinwe.Agent.Core.Chat;

/// <summary>
/// v1.4 · the member's team chat, kept in step by the service on the chat loop (the tray makes no network calls):
/// <list type="number">
/// <item>posts what the member queued in the shared outbox (<see cref="OutboxReply.ChatKind"/>: a line, a report, a new
///   direct chat, a pin or mute, and since 1.4.1 an edit, a delete or an ask for a day off), in order, until the backend
///   stored or refused it;</item>
/// <item>pulls the conversation list (and the teammates one can message) every <see cref="ListEvery"/>;</item>
/// <item>pulls the thread the tray has open (<see cref="Focus"/>: a conversation or a task chat) every
///   <see cref="FocusEvery"/> while the tray keeps asking, and tells the backend it was read;</item>
/// <item>pulls the member's asks for a day off every <see cref="LeaveEvery"/> (at once after an ask, or while the tray shows them).</item>
/// </list>
/// A backend without team chat (the LIST answers 404/403/405) turns the snapshot <see cref="TaskBackend.Off"/>: the tray then
/// shows only the admin chat, as 1.3.11 did, and the agent asks again every <see cref="OffRecheck"/>. A 404 on ONE
/// conversation's own routes (its lines, read mark, a post, an edit) only means that conversation was deleted (v1.4.1): it is
/// dropped (<see cref="ChatReply.Gone"/>) with its queued items, and the chat stays on. A task chat the backend doesn't have
/// (a 1.3 task) comes back with <see cref="ChatThread.Supported"/> = false, and a line for it is posted as a task note.
/// Never throws to the caller except on cancellation.
/// </summary>
public sealed class TeamChatSync
{
    public static readonly TimeSpan ListEvery = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan FocusEvery = TimeSpan.FromSeconds(3);
    /// <summary>The tray asks every 3 s while a thread is open; a focus not asked for this long is let go.</summary>
    public static readonly TimeSpan FocusFresh = TimeSpan.FromSeconds(12);
    public static readonly TimeSpan OffRecheck = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan PeopleEvery = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan LeaveEvery = TimeSpan.FromMinutes(5);
    /// <summary>A refusal is shown by the tray once; the service keeps it this long for a tray that wasn't asking.</summary>
    public static readonly TimeSpan NoticeKeep = TimeSpan.FromMinutes(10);

    /// <summary>The pseudo focus the tray sends while its day-off sheet is open: the asks are pulled at once.</summary>
    public const string LeaveFocus = "leave";

    private readonly BackendClient _backend;
    private readonly MessageOutbox _outbox;
    private readonly ILogger _logger;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Lock _lock = new();

    private ChatReply _snapshot = new(Supported: true);
    private string _fingerprint = "";
    private IReadOnlyList<TeamConversation> _conversations = [];
    private IReadOnlyList<TeamMember> _people = [];
    private readonly Dictionary<string, ChatThread> _threads = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _readUpTo = new(StringComparer.Ordinal);
    private readonly List<long> _gone = [];
    private readonly List<ChatNotice> _notices = [];
    private IReadOnlyList<LeaveAsk>? _leave;
    private string _backendState = TaskBackend.Unknown;
    private DateTimeOffset _nextList = DateTimeOffset.MinValue;
    private DateTimeOffset _nextPeople = DateTimeOffset.MinValue;
    private DateTimeOffset _nextThread = DateTimeOffset.MinValue;
    private DateTimeOffset _nextLeave = DateTimeOffset.MinValue;
    private (string Key, DateTimeOffset At)? _focus;

    public TeamChatSync(BackendClient backend, MessageOutbox outbox, ILogger<TeamChatSync> logger, Func<DateTimeOffset>? clock = null)
    {
        _backend = backend;
        _outbox = outbox;
        _logger = logger;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>What the tray gets over the pipe (the focused thread included).</summary>
    public ChatReply Snapshot => Volatile.Read(ref _snapshot);

    /// <summary>Goes up on every change of <see cref="Snapshot"/>.</summary>
    public int Version => Snapshot.Version;

    /// <summary>
    /// The tray's "chat" request: which thread it shows now (null = none). Pulled at once, then every 3 s.
    /// <see cref="LeaveFocus"/> = its day-off sheet is open: the asks are pulled at once (no thread).
    /// </summary>
    public ChatReply Focus(string? key)
    {
        lock (_lock)
        {
            if (key == LeaveFocus)
            {
                if (_focus?.Key != LeaveFocus)
                {
                    _nextLeave = DateTimeOffset.MinValue;
                }
                _focus = (LeaveFocus, _clock());
            }
            else if (ChatKeys.Parse(key) is null)
            {
                _focus = null;
            }
            else
            {
                if (_focus?.Key != key)
                {
                    _nextThread = DateTimeOffset.MinValue; // a newly opened thread loads at once
                }
                _focus = (key!, _clock());
            }
            PublishLocked();
        }
        return Snapshot;
    }

    /// <summary>True when there is something to do right now (queued items, a due pull).</summary>
    public bool Due
    {
        get
        {
            var now = _clock();
            return HasQueued || now >= _nextList || now >= _nextLeave || (FocusKey(now) is not null && now >= _nextThread);
        }
    }

    private bool HasQueued
    {
        get
        {
            try
            {
                return _outbox.PendingChat().Count > 0;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    /// <summary>The open thread the tray still asks for (never the day-off pseudo focus).</summary>
    private string? FocusKey(DateTimeOffset now) => _focus is { } f && f.Key != LeaveFocus && now - f.At <= FocusFresh ? f.Key : null;

    public async Task SyncAsync(string deviceToken, CancellationToken ct)
    {
        var drained = new Drained(false, false);
        try
        {
            drained = await DrainAsync(deviceToken, ct);
        }
        catch (Exception e) when (IsDeferred(e, ct))
        {
            _logger.LogDebug("Team chat send deferred: {Error}", e.Message);
        }
        catch (DeviceRejectedException)
        {
            return; // the heartbeat owns "removed"
        }

        var now = _clock();
        try
        {
            if (now >= _nextList || drained.Chat)
            {
                await PullListAsync(deviceToken, now, ct);
            }
            var isTask = FocusKey(now)?.StartsWith("task:", StringComparison.Ordinal) == true;
            if (FocusKey(now) is { } key && (now >= _nextThread || drained.Chat) && (isTask || _backendState != TaskBackend.Off))
            {
                await PullThreadAsync(deviceToken, key, ct);
                _nextThread = _clock() + FocusEvery;
            }
        }
        catch (Exception e) when (IsDeferred(e, ct))
        {
            _logger.LogDebug("Team chat sync deferred: {Error}", e.Message);
        }
        catch (DeviceRejectedException)
        {
            // the heartbeat owns "removed"
        }
        try
        {
            if (now >= _nextLeave || drained.Leave)
            {
                await PullLeaveAsync(deviceToken, now, ct);
            }
        }
        catch (Exception e) when (IsDeferred(e, ct))
        {
            _logger.LogDebug("Day-off asks sync deferred: {Error}", e.Message);
            _nextLeave = now + LeaveEvery;
        }
        catch (DeviceRejectedException)
        {
            // the heartbeat owns "removed"
        }
        Publish();
    }

    /// <summary>The PC was removed or connected as someone else: forget the old member's chat.</summary>
    public void Clear()
    {
        lock (_lock)
        {
            _conversations = [];
            _people = [];
            _threads.Clear();
            _readUpTo.Clear();
            _gone.Clear();
            _notices.Clear();
            _leave = null;
            _backendState = TaskBackend.Unknown;
            _nextList = _nextPeople = _nextThread = _nextLeave = DateTimeOffset.MinValue;
            _focus = null;
            PublishLocked();
        }
    }

    private async Task PullListAsync(string token, DateTimeOffset now, CancellationToken ct)
    {
        var list = await _backend.GetConversationsAsync(token, ct);
        if (list is null)
        {
            // Only the LIST missing means the chat itself is off (an older backend or plan).
            if (_backendState != TaskBackend.Off)
            {
                _logger.LogInformation("This backend has no team chat for the PC (older backend or plan): the admin chat only");
            }
            lock (_lock)
            {
                _backendState = TaskBackend.Off;
                _conversations = [];
                _people = [];
            }
            _nextList = now + OffRecheck;
            return;
        }
        if (now >= _nextPeople || _backendState != TaskBackend.On)
        {
            var people = await _backend.GetChatPeopleAsync(token, ct);
            lock (_lock)
            {
                _people = people ?? [];
            }
            _nextPeople = now + PeopleEvery;
        }
        List<long> vanished;
        lock (_lock)
        {
            // A conversation that was listed and no longer is was deleted (a member removed from a group keeps it, read-only).
            // An empty list is not "everything was deleted": a plan without team chat sends [] to a device.
            vanished = _backendState == TaskBackend.On && list.Count > 0
                ? [.. _conversations.Select(c => c.Id).Where(id => list.All(c => c.Id != id))]
                : [];
            _backendState = TaskBackend.On;
            // A thread being read stays read: its count goes to 0 as soon as we told the backend.
            _conversations = [.. list.Select(c => _readUpTo.ContainsKey(c.Key) && FocusKey(now) == c.Key ? c with { Unread = 0 } : c)];
        }
        foreach (var id in vanished)
        {
            Drop(id);
        }
        _nextList = now + ListEvery;
    }

    private async Task PullThreadAsync(string token, string key, CancellationToken ct)
    {
        var (kind, id) = ChatKeys.Parse(key)!.Value;
        if (kind == "direct")
        {
            // Not started yet: shows empty until the first line makes it (then the tray moves to its conv: key).
            Remember(new ChatThread(key, [], true, _clock()));
            return;
        }
        IReadOnlyList<TeamMessage>? messages;
        if (kind == "task")
        {
            messages = await _backend.GetTaskChatAsync(token, id, ct);
        }
        else
        {
            var (lines, gone) = await _backend.FetchConversationAsync(token, id, ct);
            if (gone)
            {
                Drop(id);
                return;
            }
            messages = lines;
        }
        Remember(new ChatThread(key, messages ?? [], messages is not null, _clock()));
        if (messages is null)
        {
            return;
        }
        // Read: the newest line from someone else is past what we last reported.
        var newest = messages.Where(m => m.Mine != true).Select(m => m.Id).DefaultIfEmpty(0).Max();
        var reported = _readUpTo.GetValueOrDefault(key);
        var unread = kind == "conv" && _conversations.FirstOrDefault(c => c.Id == id) is { Unread: > 0 };
        if (newest > reported || unread || !_readUpTo.ContainsKey(key))
        {
            try
            {
                if (kind == "task")
                {
                    await _backend.MarkTaskChatReadAsync(token, id, ct);
                }
                else if (await _backend.MarkConversationReadAsync(token, id, ct) == ChatWriteOutcome.Gone)
                {
                    Drop(id);
                    return;
                }
                lock (_lock)
                {
                    _readUpTo[key] = newest;
                    _conversations = [.. _conversations.Select(c => c.Id == id && kind == "conv" ? c with { Unread = 0 } : c)];
                }
            }
            catch (Exception e) when (IsDeferred(e, ct))
            {
                _logger.LogDebug("Marking a chat read deferred: {Error}", e.Message);
            }
        }
    }

    private async Task PullLeaveAsync(string token, DateTimeOffset now, CancellationToken ct)
    {
        var asks = await _backend.GetLeaveAsync(token, ct);
        lock (_lock)
        {
            _leave = asks;
        }
        _nextLeave = now + (asks is null ? OffRecheck : LeaveEvery);
    }

    /// <summary>
    /// v1.4.1 · one conversation was deleted (404 on its own routes, or gone from the list): forget it, its thread, its read
    /// mark and everything still queued for it. The chat itself stays on.
    /// </summary>
    private void Drop(long id)
    {
        var key = ChatKeys.Conversation(id);
        lock (_lock)
        {
            _conversations = [.. _conversations.Where(c => c.Id != id)];
            _threads.Remove(key);
            _readUpTo.Remove(key);
            if (!_gone.Contains(id))
            {
                _gone.Add(id);
                if (_gone.Count > 20)
                {
                    _gone.RemoveAt(0);
                }
            }
        }
        try
        {
            _outbox.RemoveChat(key);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // best effort: a later post for it is dropped as Gone anyway
        }
        _logger.LogInformation("Chat {Id} was deleted on the server: dropped here", id);
    }

    private void Remember(ChatThread thread)
    {
        lock (_lock)
        {
            _threads[thread.Key] = thread;
            if (_threads.Count > 12)
            {
                foreach (var old in _threads.Values.Where(t => t.Key != _focus?.Key).OrderBy(t => t.SyncedAt).Take(_threads.Count - 12).ToList())
                {
                    _threads.Remove(old.Key);
                }
            }
        }
    }

    private readonly record struct Drained(bool Chat, bool Leave);

    /// <summary>Post each queued item in order; drop the ones stored or refused. Stops on a network error.</summary>
    private async Task<Drained> DrainAsync(string token, CancellationToken ct)
    {
        var pending = _outbox.PendingChat();
        if (pending.Count == 0)
        {
            return new Drained(false, false);
        }
        var finished = new List<string>();
        var gone = new HashSet<string>(StringComparer.Ordinal);
        var leave = false;
        try
        {
            foreach (var item in pending)
            {
                if (item.ChatKey is { } k && gone.Contains(k))
                {
                    finished.Add(item.Id); // its conversation was deleted a moment ago
                    continue;
                }
                var result = await PostAsync(token, item, ct);
                finished.Add(item.Id);
                leave |= item.Action == OutboxReply.ChatLeave;
                if (result.Outcome == ChatWriteOutcome.Gone && item.Action is OutboxReply.ChatEdit or OutboxReply.ChatDelete or OutboxReply.ChatReport
                    && ChatKeys.Parse(item.ChatKey) is ("conv", var checkId)
                    && await _backend.GetConversationsAsync(token, ct) is { } listed && listed.Any(c => c.Id == checkId))
                {
                    // A 404 on a line's own route can mean the line, not the chat: the chat is still listed, so it stays.
                    result = result with { Outcome = ChatWriteOutcome.Refused };
                }
                switch (result.Outcome)
                {
                    case ChatWriteOutcome.Gone when ChatKeys.Parse(item.ChatKey) is ("conv", var convId):
                        gone.Add(item.ChatKey!);
                        Drop(convId);
                        break;
                    case ChatWriteOutcome.Refused or ChatWriteOutcome.Off or ChatWriteOutcome.Gone:
                        _logger.LogWarning("The backend refused a team chat {Action}; dropping it", item.Action);
                        if (NoticeText(item, result) is { } text)
                        {
                            AddNotice(new ChatNotice(item.Id, item.ChatKey ?? "", item.Action ?? "", text, _clock()));
                        }
                        break;
                    case ChatWriteOutcome.Done:
                        Applied(item, result);
                        break;
                }
            }
        }
        finally
        {
            _outbox.Remove(finished);
        }
        return new Drained(finished.Count > 0, leave);
    }

    /// <summary>What the member is told when the backend refused an item (null = nothing worth a word: a quiet drop as in 1.4).</summary>
    private static string? NoticeText(OutboxReply item, ChatWriteResult result) => item.Action switch
    {
        OutboxReply.ChatEdit => result.Error ?? "That change didn’t go through.",
        OutboxReply.ChatDelete => result.Error ?? "That message couldn’t be deleted.",
        OutboxReply.ChatLeave => result.Outcome == ChatWriteOutcome.Off
            ? "Asking for a day off isn’t on for your team yet."
            : result.Error ?? "Your ask didn’t go through. Try again.",
        OutboxReply.ChatPin => result.Error,
        _ => null,
    };

    private void AddNotice(ChatNotice notice)
    {
        lock (_lock)
        {
            _notices.RemoveAll(n => n.At < notice.At - NoticeKeep);
            _notices.Add(notice);
            if (_notices.Count > 10)
            {
                _notices.RemoveAt(0);
            }
        }
    }

    /// <summary>A stored edit, delete, pin or mute shows at once in what the tray reads (the next pull confirms it).</summary>
    private void Applied(OutboxReply item, ChatWriteResult result)
    {
        if (ChatKeys.Parse(item.ChatKey) is not ("conv", var id))
        {
            return;
        }
        lock (_lock)
        {
            switch (item.Action)
            {
                case OutboxReply.ChatEdit or OutboxReply.ChatDelete when item.MessageId is { } mid && _threads.TryGetValue(item.ChatKey!, out var thread):
                    var updated = (thread.Messages ?? []).Select(m => m.Id != mid ? m
                        : result.Line is { } line && line.Id == mid ? line with { Mine = line.Mine ?? m.Mine }
                        : item.Action == OutboxReply.ChatDelete ? m with { Deleted = true, Body = "" }
                        : m with { Body = item.Body, Edited = true });
                    _threads[item.ChatKey!] = thread with { Messages = [.. updated] };
                    break;
                case OutboxReply.ChatPin or OutboxReply.ChatUnpin or OutboxReply.ChatMute or OutboxReply.ChatUnmute:
                    _conversations = [.. _conversations.Select(c => c.Id != id ? c : item.Action switch
                    {
                        OutboxReply.ChatPin => c with { Pinned = true },
                        OutboxReply.ChatUnpin => c with { Pinned = false },
                        OutboxReply.ChatMute => c with { Muted = true },
                        _ => c with { Muted = false },
                    })];
                    break;
            }
        }
    }

    private async Task<ChatWriteResult> PostAsync(string token, OutboxReply item, CancellationToken ct)
    {
        if (item.Action == OutboxReply.ChatLeave)
        {
            return MessageOutbox.ReadLeave(item) is { } ask
                ? await _backend.AskForLeaveAsync(token, ask, ct)
                : new ChatWriteResult(ChatWriteOutcome.Refused);
        }
        if (ChatKeys.Parse(item.ChatKey) is not { } target)
        {
            return item.Action == OutboxReply.ChatDirect && item.MemberId is { } who
                ? new ChatWriteResult((await StartDirectAsync(token, who, ct)).Outcome)
                : new ChatWriteResult(ChatWriteOutcome.Refused);
        }
        var (kind, id) = target;
        switch (item.Action)
        {
            case OutboxReply.ChatSend when item.Body.Length > 0:
                if (kind == "task")
                {
                    var outcome = await _backend.PostTaskChatAsync(token, id, item.Body, ct);
                    if (outcome == ChatWriteOutcome.Off)
                    {
                        // A 1.3 backend: the words go on the task as a note.
                        var (action, _) = await _backend.PostTaskActionAsync(token, id, TaskActions.Note, item.Body, ct: ct);
                        return new ChatWriteResult(action == TaskActionOutcome.Done ? ChatWriteOutcome.Done : ChatWriteOutcome.Refused);
                    }
                    return new ChatWriteResult(outcome);
                }
                if (kind == "direct")
                {
                    var (started, conv) = await StartDirectAsync(token, id, ct);
                    if (conv is null)
                    {
                        return new ChatWriteResult(started == ChatWriteOutcome.Done ? ChatWriteOutcome.Refused : started);
                    }
                    id = conv.Id;
                }
                return new ChatWriteResult(await _backend.PostConversationMessageAsync(token, id, item.Body, item.MessageId, ct));
            case OutboxReply.ChatEdit when kind == "conv" && item.MessageId is { } editId && item.Body.Length > 0:
                return await _backend.EditConversationMessageAsync(token, id, editId, item.Body, ct);
            case OutboxReply.ChatDelete when kind == "conv" && item.MessageId is { } deleteId:
                return await _backend.DeleteConversationMessageAsync(token, id, deleteId, ct);
            case OutboxReply.ChatReport when kind is "conv":
                return new ChatWriteResult(await _backend.ReportChatAsync(token, id, item.MessageId,
                    string.IsNullOrWhiteSpace(item.Body) ? "Reported from the Zinwe app" : item.Body, ct));
            case OutboxReply.ChatDirect:
                return new ChatWriteResult((await StartDirectAsync(token, kind == "direct" ? id : item.MemberId ?? id, ct)).Outcome);
            case OutboxReply.ChatPin or OutboxReply.ChatUnpin or OutboxReply.ChatMute or OutboxReply.ChatUnmute or OutboxReply.ChatUnread
                when kind == "conv":
                return await _backend.UpdateConversationPrefsAsync(token, id, item.Action switch
                {
                    OutboxReply.ChatPin => new ChatPrefsRequest(Pinned: true),
                    OutboxReply.ChatUnpin => new ChatPrefsRequest(Pinned: false),
                    OutboxReply.ChatMute => new ChatPrefsRequest(Muted: true),
                    OutboxReply.ChatUnmute => new ChatPrefsRequest(Muted: false),
                    _ => new ChatPrefsRequest(Unread: true),
                }, ct);
            case OutboxReply.ChatRead when kind == "conv":
                return new ChatWriteResult(await _backend.MarkConversationReadAsync(token, id, ct));
            default:
                return new ChatWriteResult(ChatWriteOutcome.Refused);
        }
    }

    private async Task<(ChatWriteOutcome Outcome, TeamConversation? Conversation)> StartDirectAsync(string token, long memberId, CancellationToken ct)
    {
        var existing = _conversations.FirstOrDefault(c => !c.IsGroup && c.WithMemberId == memberId);
        if (existing is not null)
        {
            return (ChatWriteOutcome.Done, existing);
        }
        var (outcome, conv) = await _backend.StartDirectAsync(token, memberId, ct);
        if (conv is not null)
        {
            lock (_lock)
            {
                var withId = conv.WithMemberId is null ? conv with { WithMemberId = memberId, Kind = TeamConversation.DirectKind } : conv;
                _conversations = [.. _conversations.Where(c => c.Id != withId.Id), withId];
            }
        }
        return (outcome, conv);
    }

    private void Publish()
    {
        lock (_lock)
        {
            PublishLocked();
        }
    }

    private void PublishLocked()
    {
        var now = _clock();
        _notices.RemoveAll(n => n.At < now - NoticeKeep);
        var focus = FocusKey(now);
        var thread = focus is not null && _threads.TryGetValue(focus, out var t) ? t : null;
        var reply = new ChatReply(true, _backendState, _conversations, _people, thread, now, Gone: [.. _gone], Leave: _leave,
            Notices: [.. _notices]);
        var print = JsonSerializer.Serialize(reply with { SyncedAt = null, Thread = thread is null ? null : thread with { SyncedAt = null } },
            PipeJsonContext.Default.ChatReply);
        var current = Volatile.Read(ref _snapshot);
        var changed = print != _fingerprint;
        _fingerprint = print;
        Volatile.Write(ref _snapshot, reply with { Version = changed ? current.Version + 1 : current.Version });
    }

    private static bool IsDeferred(Exception e, CancellationToken ct) =>
        e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested;
}

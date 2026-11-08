using System.Text.Json;
using Microsoft.Extensions.Logging;
using Zinwe.Agent.Core.Backend;
using Zinwe.Agent.Core.Ipc;
using Zinwe.Agent.Core.Messages;

namespace Zinwe.Agent.Core.Chat;

/// <summary>
/// v1.4 · the member's team chat, kept in step by the service on the chat loop (the tray makes no network calls):
/// <list type="number">
/// <item>posts what the member queued in the shared outbox (<see cref="OutboxReply.ChatKind"/>: a line, a report, a new
///   direct chat), in order, until the backend stored or refused it;</item>
/// <item>pulls the conversation list (and the teammates one can message) every <see cref="ListEvery"/>;</item>
/// <item>pulls the thread the tray has open (<see cref="Focus"/>: a conversation or a task chat) every
///   <see cref="FocusEvery"/> while the tray keeps asking, and tells the backend it was read.</item>
/// </list>
/// A backend without team chat (404/403/405) turns the snapshot <see cref="TaskBackend.Off"/>: the tray then shows only the
/// admin chat, as 1.3.11 did, and the agent asks again every <see cref="OffRecheck"/>. A task chat the backend doesn't have
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
    private string _backendState = TaskBackend.Unknown;
    private DateTimeOffset _nextList = DateTimeOffset.MinValue;
    private DateTimeOffset _nextPeople = DateTimeOffset.MinValue;
    private DateTimeOffset _nextThread = DateTimeOffset.MinValue;
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

    /// <summary>The tray's "chat" request: which thread it shows now (null = none). Pulled at once, then every 3 s.</summary>
    public ChatReply Focus(string? key)
    {
        lock (_lock)
        {
            if (ChatKeys.Parse(key) is null)
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
            return HasQueued || now >= _nextList || (FocusKey(now) is not null && now >= _nextThread);
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

    private string? FocusKey(DateTimeOffset now) => _focus is { } f && now - f.At <= FocusFresh ? f.Key : null;

    public async Task SyncAsync(string deviceToken, CancellationToken ct)
    {
        var refreshFocus = false;
        try
        {
            refreshFocus = await DrainAsync(deviceToken, ct);
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
            if (now >= _nextList || refreshFocus)
            {
                await PullListAsync(deviceToken, now, ct);
            }
            var isTask = FocusKey(now)?.StartsWith("task:", StringComparison.Ordinal) == true;
            if (FocusKey(now) is { } key && (now >= _nextThread || refreshFocus) && (isTask || _backendState != TaskBackend.Off))
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
            _backendState = TaskBackend.Unknown;
            _nextList = _nextPeople = _nextThread = DateTimeOffset.MinValue;
            _focus = null;
            PublishLocked();
        }
    }

    private async Task PullListAsync(string token, DateTimeOffset now, CancellationToken ct)
    {
        var list = await _backend.GetConversationsAsync(token, ct);
        if (list is null)
        {
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
        lock (_lock)
        {
            _backendState = TaskBackend.On;
            // A thread being read stays read: its count goes to 0 as soon as we told the backend.
            _conversations = [.. list.Select(c => _readUpTo.ContainsKey(c.Key) && FocusKey(now) == c.Key ? c with { Unread = 0 } : c)];
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
        var messages = kind == "task"
            ? await _backend.GetTaskChatAsync(token, id, ct)
            : await _backend.GetConversationMessagesAsync(token, id, ct);
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
                else
                {
                    await _backend.MarkConversationReadAsync(token, id, ct);
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

    /// <summary>Post each queued item in order; drop the ones stored or refused. Stops on a network error. True when something was sent.</summary>
    private async Task<bool> DrainAsync(string token, CancellationToken ct)
    {
        var pending = _outbox.PendingChat();
        if (pending.Count == 0)
        {
            return false;
        }
        var finished = new List<string>();
        try
        {
            foreach (var item in pending)
            {
                var outcome = await PostAsync(token, item, ct);
                finished.Add(item.Id);
                if (outcome == ChatWriteOutcome.Refused)
                {
                    _logger.LogWarning("The backend refused a team chat {Action}; dropping it", item.Action);
                }
            }
        }
        finally
        {
            _outbox.Remove(finished);
        }
        return finished.Count > 0;
    }

    private async Task<ChatWriteOutcome> PostAsync(string token, OutboxReply item, CancellationToken ct)
    {
        if (ChatKeys.Parse(item.ChatKey) is not { } target)
        {
            return item.Action == OutboxReply.ChatDirect && item.MemberId is { } who
                ? (await StartDirectAsync(token, who, ct)).Outcome
                : ChatWriteOutcome.Refused;
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
                        return action == TaskActionOutcome.Done ? ChatWriteOutcome.Done : ChatWriteOutcome.Refused;
                    }
                    return outcome;
                }
                if (kind == "direct")
                {
                    var (started, conv) = await StartDirectAsync(token, id, ct);
                    if (conv is null)
                    {
                        return started == ChatWriteOutcome.Done ? ChatWriteOutcome.Refused : started;
                    }
                    id = conv.Id;
                }
                return await _backend.PostConversationMessageAsync(token, id, item.Body, item.MessageId, ct);
            case OutboxReply.ChatReport when kind is "conv":
                return await _backend.ReportChatAsync(token, id, item.MessageId,
                    string.IsNullOrWhiteSpace(item.Body) ? "Reported from the Zinwe app" : item.Body, ct);
            case OutboxReply.ChatDirect:
                return (await StartDirectAsync(token, kind == "direct" ? id : item.MemberId ?? id, ct)).Outcome;
            default:
                return ChatWriteOutcome.Refused;
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
        var focus = FocusKey(_clock());
        var thread = focus is not null && _threads.TryGetValue(focus, out var t) ? t : null;
        var reply = new ChatReply(true, _backendState, _conversations, _people, thread, _clock());
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


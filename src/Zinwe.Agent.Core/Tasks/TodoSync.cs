using System.Text.Json;
using Microsoft.Extensions.Logging;
using Zinwe.Agent.Core.Backend;
using Zinwe.Agent.Core.Ipc;
using Zinwe.Agent.Core.Messages;

namespace Zinwe.Agent.Core.Tasks;

/// <summary>
/// The member's own to-dos, kept in step by the service on the chat loop, the same way as <see cref="TaskSync"/>:
/// <list type="number">
/// <item>sends what the member changed (the widget queues <c>todo</c> items in the shared outbox: they survive offline
///   and a restart, and are retried until the backend stored or refused them);</item>
/// <item>then pulls <c>GET todos/</c> into <see cref="Snapshot"/>, which the pipe hands to the widget (request "todos").
///   <see cref="Version"/> goes up whenever the snapshot changes; the status carries it. The widget merges the list
///   into its own tasks.json, which stays the member's copy: this class never touches that file.</item>
/// </list>
/// An older backend (404) or a plan without tasks (403) turns the snapshot <see cref="TaskBackend.Off"/>: the to-dos stay
/// on the PC, whatever was queued is dropped (the widget still has it, marked unsent), and the agent asks again every
/// <see cref="OffRecheck"/>. The last snapshot is kept in the agent's data folder. Never throws to the caller except on
/// cancellation.
/// </summary>
public sealed class TodoSync
{
    public static readonly TimeSpan OffRecheck = TimeSpan.FromMinutes(10);

    private const string CacheFile = "todos-cache.json";

    private readonly BackendClient _backend;
    private readonly MessageOutbox _outbox;
    private readonly ILogger<TodoSync> _logger;
    private readonly string? _cachePath;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Lock _lock = new();
    private TodosReply _snapshot = new(Supported: true);
    private string _fingerprint = "";
    private DateTimeOffset _offCheckedAt = DateTimeOffset.MinValue;

    public TodoSync(BackendClient backend, MessageOutbox outbox, ILogger<TodoSync> logger, string? dataDirectory = null,
        Func<DateTimeOffset>? clock = null)
    {
        _backend = backend;
        _outbox = outbox;
        _logger = logger;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _cachePath = dataDirectory is null ? null : Path.Combine(dataDirectory, CacheFile);
        if (LoadCache() is { } cached)
        {
            Publish(TaskBackend.On, cached.Todos, cached.MemberId, cached.SyncedAt);
        }
    }

    /// <summary>What the widget gets over the pipe. Always <see cref="TodosReply.Supported"/> (this agent knows to-dos).</summary>
    public TodosReply Snapshot => Volatile.Read(ref _snapshot);

    /// <summary>Goes up on every change of <see cref="Snapshot"/> (0 = nothing synced yet).</summary>
    public int Version => Snapshot.Version;

    public async Task SyncAsync(string deviceToken, CancellationToken ct)
    {
        try
        {
            var now = _clock();
            if (!await DrainAsync(deviceToken, ct))
            {
                TurnOff(now);
                return;
            }
            if (Snapshot.Backend == TaskBackend.Off && now - _offCheckedAt < OffRecheck)
            {
                return;
            }
            var list = await _backend.GetTodosAsync(deviceToken, ct);
            if (list is null)
            {
                TurnOff(now);
                return;
            }
            Publish(TaskBackend.On, list.Todos ?? [], list.MemberId, now);
            SaveCache(list, now);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            _logger.LogDebug("To-do sync deferred: {Error}", e.Message);
        }
        catch (DeviceRejectedException)
        {
            // The heartbeat owns "removed" (it wipes the identity and calls Clear).
        }
    }

    /// <summary>The PC was removed or connected as someone else: forget the old member's to-dos and what they queued.</summary>
    public void Clear()
    {
        _offCheckedAt = DateTimeOffset.MinValue;
        _outbox.RemoveTodos();
        Publish(TaskBackend.Unknown, [], null, null);
        DeleteCache();
    }

    private void TurnOff(DateTimeOffset now)
    {
        _offCheckedAt = now;
        if (Snapshot.Backend != TaskBackend.Off)
        {
            _logger.LogInformation("This backend keeps no to-dos for the PC (older backend or plan): they stay on the PC");
        }
        _outbox.RemoveTodos();
        Publish(TaskBackend.Off, [], null, now);
        DeleteCache();
    }

    /// <summary>
    /// Send each queued to-do in order; remove the ones the backend stored or refused. Stops on a network error. False
    /// when the backend has no to-dos for this PC.
    /// </summary>
    private async Task<bool> DrainAsync(string deviceToken, CancellationToken ct)
    {
        var pending = _outbox.PendingTodos();
        if (pending.Count == 0)
        {
            return true;
        }
        var finished = new List<string>();
        try
        {
            foreach (var item in pending)
            {
                if (MessageOutbox.ReadTodo(item) is not { } todo)
                {
                    finished.Add(item.Id); // nothing any backend could take: drop it
                    continue;
                }
                var outcome = await _backend.SendTodoAsync(deviceToken, todo, ct);
                if (outcome == TodoWriteOutcome.Off)
                {
                    return false;
                }
                finished.Add(item.Id); // stored or refused for good: either way, never again
                if (outcome == TodoWriteOutcome.Refused)
                {
                    _logger.LogWarning("The backend refused a to-do; dropping it");
                }
            }
            return true;
        }
        finally
        {
            _outbox.Remove(finished);
            if (finished.Count > 0)
            {
                _logger.LogInformation("Sent {Count} to-do(s)", finished.Count);
            }
        }
    }

    private void Publish(string backend, IReadOnlyList<DeviceTodo> todos, long? memberId, DateTimeOffset? syncedAt)
    {
        lock (_lock)
        {
            var print = backend + "|" + memberId + "|" + JsonSerializer.Serialize(todos.ToArray(), BackendJsonContext.Default.DeviceTodoArray);
            var current = Volatile.Read(ref _snapshot);
            var changed = print != _fingerprint;
            _fingerprint = print;
            Volatile.Write(ref _snapshot, new TodosReply(
                Supported: true, Backend: backend, Todos: todos, MemberId: memberId, SyncedAt: syncedAt,
                Version: changed ? current.Version + 1 : current.Version));
        }
    }

    private (DateTimeOffset SyncedAt, long? MemberId, DeviceTodo[] Todos)? LoadCache()
    {
        try
        {
            if (_cachePath is null || !File.Exists(_cachePath))
            {
                return null;
            }
            using var doc = JsonDocument.Parse(File.ReadAllText(_cachePath));
            var syncedAt = doc.RootElement.GetProperty("synced_at").GetDateTimeOffset();
            var list = doc.RootElement.GetProperty("list").Deserialize(BackendJsonContext.Default.DeviceTodoList);
            return (syncedAt, list?.MemberId, [.. list?.Todos ?? []]);
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException or KeyNotFoundException
            or InvalidOperationException or FormatException)
        {
            return null; // unreadable: the next sync rebuilds it
        }
    }

    private void SaveCache(DeviceTodoList list, DateTimeOffset syncedAt)
    {
        if (_cachePath is null)
        {
            return;
        }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_cachePath)!);
            var json = "{\"synced_at\": " + JsonSerializer.Serialize(syncedAt) + ", \"list\": "
                + JsonSerializer.Serialize(list, BackendJsonContext.Default.DeviceTodoList) + "}";
            var tmp = _cachePath + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, _cachePath, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug("Could not keep the to-do list on disk: {Error}", e.Message);
        }
    }

    private void DeleteCache()
    {
        try
        {
            if (_cachePath is not null && File.Exists(_cachePath))
            {
                File.Delete(_cachePath);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // best effort
        }
    }
}

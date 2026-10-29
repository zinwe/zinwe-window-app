using System.Text.Json;
using Microsoft.Extensions.Logging;
using Zinwe.Agent.Core.Backend;
using Zinwe.Agent.Core.Ipc;
using Zinwe.Agent.Core.Messages;

namespace Zinwe.Agent.Core.Tasks;

/// <summary>
/// The owner's tasks for this PC's member (FR-20 v2), kept in step by the service on the chat loop:
/// <list type="number">
/// <item>posts what the member did (the widget queues <c>task_action</c> items in the shared outbox: they survive
///   offline and a restart, and are retried until the backend stored or refused them - same pattern as chat replies);</item>
/// <item>then pulls <c>GET tasks/</c> into <see cref="Snapshot"/>, which the pipe hands to the widget (request
///   "tasks"). <see cref="Version"/> goes up whenever the snapshot changes; the status carries it, so the widget
///   fetches only when something is new.</item>
/// </list>
/// An older backend (404) or a plan without tasks (403) turns the snapshot <see cref="TaskBackend.Off"/>: the widget
/// then shows only the member's own to-dos, and the agent asks again every <see cref="OffRecheck"/>.
/// The last snapshot is kept in the agent's data folder, so tasks still show offline after a restart.
/// Never throws to the caller except on cancellation.
/// </summary>
public sealed class TaskSync
{
    public static readonly TimeSpan OffRecheck = TimeSpan.FromMinutes(10);

    private const string CacheFile = "tasks-cache.json";

    private readonly BackendClient _backend;
    private readonly MessageOutbox _outbox;
    private readonly ILogger<TaskSync> _logger;
    private readonly string? _cachePath;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Lock _lock = new();
    private TasksReply _snapshot = new(Supported: true);
    private string _fingerprint = "";
    private DateTimeOffset _offCheckedAt = DateTimeOffset.MinValue;

    public TaskSync(BackendClient backend, MessageOutbox outbox, ILogger<TaskSync> logger, string? dataDirectory = null,
        Func<DateTimeOffset>? clock = null)
    {
        _backend = backend;
        _outbox = outbox;
        _logger = logger;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _cachePath = dataDirectory is null ? null : Path.Combine(dataDirectory, CacheFile);
        if (LoadCache() is { } cached)
        {
            Publish(TaskBackend.On, cached.Tasks, cached.SyncedAt);
        }
    }

    /// <summary>What the widget gets over the pipe. Always <see cref="TasksReply.Supported"/> (this agent knows tasks).</summary>
    public TasksReply Snapshot => Volatile.Read(ref _snapshot);

    /// <summary>Goes up on every change of <see cref="Snapshot"/> (0 = nothing synced yet).</summary>
    public int Version => Snapshot.Version;

    /// <summary>True while the member did something on a task the agent hasn't posted yet (sync right away).</summary>
    public bool HasPendingActions => _outbox.PendingTaskActions().Count > 0;

    public async Task SyncAsync(string deviceToken, CancellationToken ct)
    {
        try
        {
            await DrainAsync(deviceToken, ct);

            var now = _clock();
            if (Snapshot.Backend == TaskBackend.Off && now - _offCheckedAt < OffRecheck)
            {
                return;
            }
            var tasks = await _backend.GetTasksAsync(deviceToken, ct);
            if (tasks is null)
            {
                _offCheckedAt = now;
                if (Snapshot.Backend != TaskBackend.Off)
                {
                    _logger.LogInformation("This backend has no tasks for the PC (older backend or plan): own to-dos only");
                }
                Publish(TaskBackend.Off, [], now);
                DeleteCache();
                return;
            }
            Publish(TaskBackend.On, tasks, now);
            SaveCache(tasks, now);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            _logger.LogDebug("Task sync deferred: {Error}", e.Message);
        }
        catch (DeviceRejectedException)
        {
            // The heartbeat owns "removed" (it wipes the identity and calls Clear).
        }
    }

    /// <summary>The PC was removed or connected as someone else: forget the old member's tasks.</summary>
    public void Clear()
    {
        _offCheckedAt = DateTimeOffset.MinValue;
        Publish(TaskBackend.Unknown, [], null);
        DeleteCache();
    }

    /// <summary>Post each queued action in order; remove the ones the backend stored or refused. Stops on a network error.</summary>
    private async Task DrainAsync(string deviceToken, CancellationToken ct)
    {
        var pending = _outbox.PendingTaskActions();
        if (pending.Count == 0)
        {
            return;
        }
        var finished = new List<string>();
        try
        {
            foreach (var item in pending)
            {
                if (item.TaskId is not { } id || !TaskActions.IsKnown(item.Action))
                {
                    finished.Add(item.Id); // nothing any backend could take: drop it
                    continue;
                }
                var (outcome, task) = await _backend.PostTaskActionAsync(
                    deviceToken, id, item.Action!, item.Body, item.StepId, item.StepDone, ct);
                finished.Add(item.Id); // stored or refused for good: either way, never again
                if (outcome == TaskActionOutcome.Refused)
                {
                    _logger.LogWarning("The backend refused task action {Action} on task {TaskId}; dropping it", item.Action, id);
                }
                else if (task is not null)
                {
                    Replace(task);
                }
            }
        }
        finally
        {
            _outbox.Remove(finished);
            if (finished.Count > 0)
            {
                _logger.LogInformation("Sent {Count} task action(s)", finished.Count);
            }
        }
    }

    /// <summary>The backend's answer to an action is the freshest view of that task: show it at once.</summary>
    private void Replace(DeviceTask task)
    {
        var current = Snapshot;
        if (current.Backend != TaskBackend.On)
        {
            return;
        }
        var list = (current.Tasks ?? []).Select(t => t.Id == task.Id ? task : t).ToList();
        Publish(TaskBackend.On, list, current.SyncedAt);
    }

    private void Publish(string backend, IReadOnlyList<DeviceTask> tasks, DateTimeOffset? syncedAt)
    {
        lock (_lock)
        {
            var print = backend + "|" + JsonSerializer.Serialize(tasks.ToArray(), BackendJsonContext.Default.DeviceTaskArray);
            var current = Volatile.Read(ref _snapshot);
            var changed = print != _fingerprint;
            _fingerprint = print;
            Volatile.Write(ref _snapshot, new TasksReply(
                Supported: true, Backend: backend, Tasks: tasks, SyncedAt: syncedAt,
                Version: changed ? current.Version + 1 : current.Version));
        }
    }

    private sealed record CacheFileContent(DateTimeOffset SyncedAt, DeviceTask[] Tasks);

    private (DateTimeOffset SyncedAt, DeviceTask[] Tasks)? LoadCache()
    {
        try
        {
            if (_cachePath is null || !File.Exists(_cachePath))
            {
                return null;
            }
            using var doc = JsonDocument.Parse(File.ReadAllText(_cachePath));
            var syncedAt = doc.RootElement.GetProperty("synced_at").GetDateTimeOffset();
            var tasks = doc.RootElement.GetProperty("tasks").Deserialize(BackendJsonContext.Default.DeviceTaskArray) ?? [];
            return (syncedAt, tasks);
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException or KeyNotFoundException
            or InvalidOperationException or FormatException)
        {
            return null; // unreadable: the next sync rebuilds it
        }
    }

    private void SaveCache(DeviceTask[] tasks, DateTimeOffset syncedAt)
    {
        if (_cachePath is null)
        {
            return;
        }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_cachePath)!);
            var json = "{\"synced_at\": " + JsonSerializer.Serialize(syncedAt) + ", \"tasks\": "
                + JsonSerializer.Serialize(tasks, BackendJsonContext.Default.DeviceTaskArray) + "}";
            var tmp = _cachePath + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, _cachePath, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug("Could not keep the task list on disk: {Error}", e.Message);
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

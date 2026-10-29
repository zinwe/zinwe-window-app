using Zinwe.Agent.Core.Backend;

namespace Zinwe.Agent.Core.Ipc;

/// <summary>Whether the backend has tasks for this PC (FR-20 v2).</summary>
public static class TaskBackend
{
    /// <summary>Not synced yet (just started, or the PC was removed).</summary>
    public const string Unknown = "unknown";
    /// <summary>The backend has the tasks API and this plan includes it.</summary>
    public const string On = "on";
    /// <summary>An older backend (404) or a plan without tasks (403): own to-dos only.</summary>
    public const string Off = "off";
}

/// <summary>
/// The service's reply to a "tasks" pipe request (FR-20 v2): the owner's tasks for this PC's member as the backend
/// last sent them. Every member has a default, so a "not supported" ack (<c>{"accepted":false,…}</c>) reads as
/// <see cref="Supported"/> = false. Add new members only as optional trailing parameters.
/// </summary>
/// <param name="Supported">True from a service that knows tasks.</param>
/// <param name="Backend">See <see cref="TaskBackend"/>.</param>
/// <param name="SyncedAt">When the agent last fetched them (after posting the member's queued actions).</param>
/// <param name="Version">Goes up whenever the list changes; also on the status (<see cref="AgentStatus.TasksVersion"/>).</param>
public sealed record TasksReply(
    bool Supported = false,
    string Backend = TaskBackend.Unknown,
    IReadOnlyList<DeviceTask>? Tasks = null,
    DateTimeOffset? SyncedAt = null,
    int Version = 0);

using Zinwe.Agent.Core;
using Zinwe.Agent.Core.Backend;
using Zinwe.Agent.Core.Identity;
using Zinwe.Agent.Core.Ipc;
using Zinwe.Agent.Core.Policy;

namespace Zinwe.Agent.Service;

/// <summary>The latest <see cref="AgentStatus"/>: written by <see cref="AgentWorker"/>, read by <see cref="PipeServer"/>.</summary>
internal sealed class AgentStateHolder
{
    private readonly Lock _lock = new();
    private AgentStatus _status = new(AgentState.NotEnrolled, AgentInfo.Version, MachineInfo.Hostname, null, null, null);

    public AgentStatus Current => Volatile.Read(ref _status);

    private AgentUpdate? _offer;

    /// <summary>The full release offer from the last heartbeat (download link + hash), for <see cref="Update.AgentUpdater"/>.</summary>
    public AgentUpdate? Offer
    {
        get
        {
            lock (_lock)
            {
                return _offer;
            }
        }
    }

    public void Set(
        AgentState state,
        long? deviceId = null,
        DateTimeOffset? lastHeartbeatAt = null,
        string? message = null,
        string? downloadUrl = null)
    {
        lock (_lock)
        {
            var previous = Current;
            Volatile.Write(ref _status, previous with
            {
                State = state,
                DeviceId = deviceId ?? (state == AgentState.NotEnrolled ? null : previous.DeviceId),
                LastHeartbeatAt = lastHeartbeatAt ?? previous.LastHeartbeatAt,
                Message = message,
                // Only meaningful for UpdateRequired; cleared on any other state so a stale link can't linger.
                DownloadUrl = downloadUrl,
            });
        }
    }

    /// <summary>
    /// The release the backend offers (null = up to date). A different version resets the one-click progress;
    /// the same version keeps it, so a running download isn't wiped by the next heartbeat.
    /// </summary>
    public void SetUpdate(AgentUpdate? update)
    {
        lock (_lock)
        {
            _offer = update;
            var previous = Current;
            var sameVersion = update is not null && update.Version == previous.UpdateVersion;
            Volatile.Write(ref _status, previous with
            {
                UpdateVersion = update?.Version,
                UpdateNotes = update?.Notes,
                UpdateInstallable = update is not null
                    && !string.IsNullOrWhiteSpace(update.DownloadUrl) && !string.IsNullOrWhiteSpace(update.Sha256),
                UpdateStage = sameVersion ? previous.UpdateStage : null,
                UpdateMessage = sameVersion ? previous.UpdateMessage : null,
            });
        }
    }

    /// <summary>Progress of the one-click update (see <see cref="UpdateStages"/>), shown in the widget.</summary>
    public void SetUpdateStage(string? stage, string? message = null)
    {
        lock (_lock)
        {
            Volatile.Write(ref _status, Current with { UpdateStage = stage, UpdateMessage = message });
        }
    }

    /// <summary>The policy in force survives state changes: protection doesn't depend on being connected.</summary>
    public void SetPolicy(AgentPolicy? policy)
    {
        lock (_lock)
        {
            Volatile.Write(ref _status, Current with
            {
                PolicyVersion = policy?.Version,
                UsbMode = policy?.Usb.Mode.ToWire(),
                Entitlements = policy?.Entitlements,
            });
        }
    }
}

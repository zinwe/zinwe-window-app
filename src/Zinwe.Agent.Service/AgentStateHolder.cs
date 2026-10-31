using Zinwe.Agent.Core;
using Zinwe.Agent.Core.Backend;
using Zinwe.Agent.Core.Identity;
using Zinwe.Agent.Core.Ipc;
using Zinwe.Agent.Core.Policy;
using Zinwe.Agent.Core.Work;

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
        string? downloadUrl = null,
        bool removed = false)
    {
        lock (_lock)
        {
            var previous = Current;
            Volatile.Write(ref _status, previous with
            {
                State = state,
                DeviceId = deviceId ?? (state == AgentState.NotEnrolled || removed ? null : previous.DeviceId),
                LastHeartbeatAt = lastHeartbeatAt ?? previous.LastHeartbeatAt,
                Message = message,
                // Only meaningful for UpdateRequired; cleared on any other state so a stale link can't linger.
                DownloadUrl = downloadUrl,
                // Only with Rejected: the owner removed this PC. Any other state (a new connect) clears it.
                Removed = removed && state == AgentState.Rejected,
                ServerConsentState = removed ? null : previous.ServerConsentState,
            });
        }
    }

    /// <summary>What the backend says about the company and the member's consent (each kept when not sent).</summary>
    public void SetServerInfo(string? companyName, string? ownerName, string? consentState)
    {
        lock (_lock)
        {
            var previous = Current;
            Volatile.Write(ref _status, previous with
            {
                CompanyName = Clean(companyName) ?? previous.CompanyName,
                OwnerName = Clean(ownerName) ?? previous.OwnerName,
                ServerConsentState = Clean(consentState) ?? previous.ServerConsentState,
            });
        }
    }

    /// <summary>Where the member's day is, as the last heartbeat said (both null = nothing moved). Offline, the last word stays.</summary>
    public void SetWorkDay(WorkDayMoved? moved, WorkDayCarry? carry)
    {
        lock (_lock)
        {
            Volatile.Write(ref _status, Current with { DayMoved = moved, DayCarry = carry });
        }
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

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
            var previous = Current;
            var scope = MonitoringScope.From(policy);
            Volatile.Write(ref _status, previous with
            {
                PolicyVersion = policy?.Version,
                UsbMode = policy?.Usb.Mode.ToWire(),
                Entitlements = policy?.Entitlements,
                // The consent screen lists exactly what this policy can record, and asks again when that grows.
                Monitoring = policy is null ? null : scope,
                NoticeVersion = ConsentNotice.VersionFor(scope),
                CompanyName = Clean(policy?.CompanyName) ?? previous.CompanyName,
                OwnerName = Clean(policy?.OwnerName) ?? previous.OwnerName,
            });
        }
    }
}

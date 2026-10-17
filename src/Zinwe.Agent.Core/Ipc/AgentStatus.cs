using System.Text.Json.Serialization;

namespace Zinwe.Agent.Core.Ipc;

[JsonConverter(typeof(JsonStringEnumConverter<AgentState>))]
public enum AgentState
{
    /// <summary>No device identity and no enrollment key yet -- waiting for the installer/admin.</summary>
    NotEnrolled,
    Enrolling,
    /// <summary>Last heartbeat succeeded.</summary>
    Connected,
    /// <summary>Enrolled, but the backend is unreachable. Protection keeps running on cached policy.</summary>
    Offline,
    /// <summary>The Member ID was refused, or the owner removed this PC (<see cref="AgentStatus.Removed"/>).
    /// Waits for the member to connect again with a (new) Member ID.</summary>
    Rejected,
    /// <summary>This agent is below the required version. Protection and the work day keep running; the widget asks
    /// the employee to install the update (one click, or <see cref="AgentStatus.DownloadUrl"/>).</summary>
    UpdateRequired,
}

/// <summary>
/// Snapshot the service hands to the tray app over the named pipe.
/// Add new members only as optional trailing parameters: an older tray must keep reading a newer service.
/// </summary>
/// <param name="PolicyVersion">Policy in force (cached or fetched); null before the first policy sync.</param>
/// <param name="UsbMode">"allow" | "read_only" | "block", or null when no policy is in force yet.</param>
public sealed record AgentStatus(
    AgentState State,
    string AgentVersion,
    string Hostname,
    long? DeviceId,
    DateTimeOffset? LastHeartbeatAt,
    string? Message,
    int? PolicyVersion = null,
    string? UsbMode = null,
    /// <summary>Where to download the update when <see cref="State"/> is <see cref="AgentState.UpdateRequired"/>.</summary>
    string? DownloadUrl = null,
    /// <summary>A newer release offered by the backend (required when <see cref="State"/> is UpdateRequired,
    /// otherwise optional); null when up to date.</summary>
    string? UpdateVersion = null,
    /// <summary>What's new in <see cref="UpdateVersion"/>, written by the Zinwe team.</summary>
    string? UpdateNotes = null,
    /// <summary>Where the one-click update is: see <see cref="UpdateStages"/>; null when not started.</summary>
    string? UpdateStage = null,
    /// <summary>Plain-language detail for the stage (progress, or why it failed).</summary>
    string? UpdateMessage = null,
    /// <summary>True when an installer was published, so "Update now" can run (else: manual download only).</summary>
    bool UpdateInstallable = false,
    /// <summary>What the owner's plan switches on (<see cref="Policy.PlanModules"/>), from the policy in force;
    /// null when unknown (no policy yet, or an older backend), which hides nothing.</summary>
    IReadOnlyList<string>? Entitlements = null,
    /// <summary>The employer's name from the backend, when it sends one (also kept after a removal).</summary>
    string? CompanyName = null,
    /// <summary>The owner's name from the backend, when it sends one.</summary>
    string? OwnerName = null,
    /// <summary>With <see cref="AgentState.Rejected"/>: the owner removed this PC (rules cleared, monitoring stopped).</summary>
    bool Removed = false,
    /// <summary>The consent notice version in force (<see cref="Work.ConsentNotice.VersionFor"/>); null = the full notice.</summary>
    string? NoticeVersion = null,
    /// <summary>What can be recorded under the policy in force, for the consent screen; null = everything.</summary>
    Work.MonitoringScope? Monitoring = null,
    /// <summary>The backend's view of the member's consent ("pending" | "given" | "declined"); null = unknown.</summary>
    string? ServerConsentState = null);

/// <summary>The one-click update's stages, in order ("failed" can follow any of them).</summary>
public static class UpdateStages
{
    /// <summary>Downloading the installer and checking its SHA-256.</summary>
    public const string Downloading = "downloading";
    /// <summary>msiexec is running: the tray closes itself so its files can be replaced; the new service reopens it.</summary>
    public const string Installing = "installing";
    /// <summary>Dev / dry-run: the installer is downloaded and verified but deliberately not run.</summary>
    public const string Ready = "ready";
    public const string Failed = "failed";
}

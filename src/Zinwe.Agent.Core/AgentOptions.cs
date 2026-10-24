namespace Zinwe.Agent.Core;

/// <summary>
/// Bound from the "Agent" config section. Sources, lowest to highest priority:
/// appsettings.json, appsettings.{Environment}.json, HKLM\SOFTWARE\Zinwe\Agent, ZINWE_Agent__* env vars.
/// </summary>
public sealed class AgentOptions
{
    public const string Section = "Agent";

    /// <summary>Django backend root, e.g. https://prod.backend.zinwe.io (no /api suffix).</summary>
    public string BackendBaseUrl { get; set; } = "";

    /// <summary>Member email for enrollment. Dev/test only; production comes from the MSI via the registry.</summary>
    public string? MemberEmail { get; set; }

    /// <summary>Member id (e.g. ZM-XXXX-XXXX) for enrollment. Dev/test only; production via the MSI registry.</summary>
    public string? MemberId { get; set; }

    /// <summary>Used until the backend tells us otherwise in a heartbeat response.</summary>
    public int HeartbeatSeconds { get; set; } = 60;

    /// <summary>
    /// How often the owner↔member chat is pulled (seconds, clamped 5..300). A reply the member types is sent
    /// within a couple of seconds regardless.
    /// </summary>
    public int MessagePollSeconds { get; set; } = 15;

    public string DataDirectory { get; set; } = AgentInfo.DefaultDataDirectory;

    public EnforcementOptions Enforcement { get; set; } = new();
}

/// <summary>Agent:Enforcement.</summary>
public sealed class EnforcementOptions
{
    /// <summary>
    /// True = log what would change, change nothing (dev boxes: appsettings.Development.json sets it).
    /// Even when false, real enforcement also requires the process to be LocalSystem; see UsbEnforcerSelector.
    /// </summary>
    public bool DryRun { get; set; }
}

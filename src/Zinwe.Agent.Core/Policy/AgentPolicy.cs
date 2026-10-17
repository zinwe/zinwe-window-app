using System.Text.Json.Serialization;

namespace Zinwe.Agent.Core.Policy;

/// <summary>What the owner chose for USB storage on their PCs (dashboard → Rules).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<UsbMode>))]
public enum UsbMode
{
    /// <summary>USB drives work normally.</summary>
    [JsonStringEnumMemberName("allow")]
    Allow,

    /// <summary>Files can be opened from USB drives, but nothing can be copied onto them.</summary>
    [JsonStringEnumMemberName("read_only")]
    ReadOnly,

    /// <summary>USB drives can't be opened at all.</summary>
    [JsonStringEnumMemberName("block")]
    Block,
}

public static class UsbModeExtensions
{
    /// <summary>The backend's spelling ("allow" | "read_only" | "block").</summary>
    public static string ToWire(this UsbMode mode) => mode switch
    {
        UsbMode.Allow => "allow",
        UsbMode.ReadOnly => "read_only",
        UsbMode.Block => "block",
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown USB mode."),
    };

    /// <summary>Employee-facing wording for the tray.</summary>
    public static string ToDisplay(this UsbMode mode) => mode switch
    {
        UsbMode.Allow => "Allowed",
        UsbMode.ReadOnly => "Read-only",
        UsbMode.Block => "Blocked",
        _ => "Unknown",
    };
}

public sealed record UsbPolicy(UsbMode Mode);

/// <summary>How websites are handled: just watch usage, or also block the list.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<WebMode>))]
public enum WebMode
{
    /// <summary>Record usage only; don't block anything.</summary>
    [JsonStringEnumMemberName("monitor")]
    Monitor,

    /// <summary>Record usage and block the configured categories + custom domains.</summary>
    [JsonStringEnumMemberName("block")]
    Block,
}

/// <summary>Owner's internet-usage + website-blocking rule. Null block ⇒ the agent keeps its local defaults.</summary>
public sealed record WebPolicy(
    bool Enabled,
    WebMode Mode,
    IReadOnlyList<string> Categories,
    IReadOnlyList<string> CustomDomains);

/// <summary>Owner's data-transfer visibility rule (sizes & counts only unless details are turned on).</summary>
public sealed record DataTransferPolicy(
    bool Enabled,
    long UsbWarnBytes,
    long LanWarnBytes,
    long EmailWarnBytes,
    bool IncludeDetails,
    // Optional/trailing for back-compat: an older backend that omits it leaves lockdown off.
    bool UsbLockdown = false);

/// <summary>Owner's remote-software rule: always detects; may also block the listed tools (dry-run aware).</summary>
public sealed record RemoteSoftwarePolicy(
    bool Enabled,
    bool BlockingEnabled,
    IReadOnlyList<string> BlockedTools,
    IReadOnlyList<string> ExtraToolExecutables);

/// <summary>
/// The parts of Zinwe the owner's plan switches on (the backend's <c>Plan.modules</c>). The backend already clamps
/// the policy it sends to the plan; the agent uses this only to hide what the member can't use on this plan
/// (the work-day buttons and the chat). An older backend sends no list: then everything stays on.
/// </summary>
public static class PlanModules
{
    public const string WorkTime = "work_time";
    public const string TeamChat = "team_chat";

    /// <summary>True when <paramref name="entitlements"/> includes <paramref name="module"/>, or there's no list.</summary>
    public static bool Allows(IReadOnlyList<string>? entitlements, string module) =>
        entitlements is null || entitlements.Contains(module);
}

/// <summary>
/// The owner's rules for this PC, as returned by GET /api/devices/policy/current/ and cached in policy.json.
/// <see cref="Version"/> goes up by one each time the owner changes anything.
///
/// Feature blocks are optional: a policy that omits one (e.g. an older backend that only sends <see cref="Usb"/>)
/// leaves that feature on its local appsettings defaults, so the agent stays compatible as the backend grows.
/// </summary>
public sealed record AgentPolicy(
    int Version,
    UsbPolicy Usb,
    WebPolicy? Web = null,
    DataTransferPolicy? DataTransfer = null,
    RemoteSoftwarePolicy? RemoteSoftware = null,
    // Plan (2026-10-15): what the owner's plan switches on (see PlanModules) and its PC limit (0 = none).
    // Optional/trailing: an older backend omits them, and then nothing is hidden.
    IReadOnlyList<string>? Entitlements = null,
    int? MaxDevices = null,
    // The employer's name and its owner, when the backend sends them (consent screen, widget header). Optional.
    string? CompanyName = null,
    string? OwnerName = null)
{
    /// <summary>
    /// What a PC enforces once it no longer belongs to a company (removed by the owner): USB allowed and every
    /// feature off, so no blocklist, read-only USB or blocked tool is left behind.
    /// </summary>
    public static AgentPolicy Released { get; } = new(
        0,
        new UsbPolicy(UsbMode.Allow),
        new WebPolicy(false, WebMode.Monitor, [], []),
        new DataTransferPolicy(false, long.MaxValue, long.MaxValue, long.MaxValue, false, false),
        new RemoteSoftwarePolicy(false, false, [], []),
        Entitlements: []);

    /// <summary>
    /// The serializer fills missing members with null and accepts integers for enums, so check the shape
    /// before acting on it. A malformed policy is never applied. Only <see cref="Usb"/> is required; the
    /// optional feature blocks, when present, are validated where they are mapped onto each feature's options.
    /// </summary>
    [JsonIgnore] // computed guard, not part of the wire contract or policy.json
    public bool IsWellFormed => Version >= 0 && Usb is not null && Enum.IsDefined(Usb.Mode);
}

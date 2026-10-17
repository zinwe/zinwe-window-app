using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Zinwe.Agent.Core.Policy;
using Zinwe.Agent.Core.Usb;

namespace Zinwe.Agent.Core.Events;

/// <summary>Event types the backend knows how to word for the owner's activity feed.</summary>
public static partial class EventTypes
{
    public const string UsbConnected = "usb.connected";
    public const string UsbBlocked = "usb.blocked";
    public const string UsbPolicyApplied = "usb.policy_applied";
    /// <summary>The owner pressed "Restore USB" and this PC put USB back to the policy.</summary>
    public const string UsbRestored = "usb.restored";

    /// <summary>Same rule as the backend: dotted lowercase segments, at least two.</summary>
    public static bool IsValid(string? type) => type is not null && Pattern().IsMatch(type);

    [GeneratedRegex(@"^[a-z_]+(\.[a-z_]+)+$", RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();
}

public static class EventSeverity
{
    public const string Info = "info";
    public const string Warning = "warning";
    public const string Critical = "critical";

    public static bool IsValid(string? severity) => severity is Info or Warning or Critical;
}

/// <summary>
/// Something that happened on this PC, waiting in the local queue to be uploaded.
/// <see cref="Id"/> is a client-generated UUID, so a re-sent event is recognised as a duplicate by the backend.
/// </summary>
public sealed record AgentEvent(string Id, string Type, DateTimeOffset OccurredAt, string Severity, string DataJson)
{
    /// <summary>Backend limit on the serialized "data" object.</summary>
    public const int MaxDataBytes = 4096;

    /// <summary>Longest string we put in any data field; keeps titles readable and payloads small.</summary>
    internal const int MaxFieldLength = 200;

    public static AgentEvent Create(string type, string severity, JsonObject data, DateTimeOffset? occurredAt = null)
    {
        var at = occurredAt ?? DateTimeOffset.Now;
        // Whole milliseconds: 7-digit fractions are valid ISO 8601 but not every parser accepts them.
        at = new DateTimeOffset(at.Ticks - (at.Ticks % TimeSpan.TicksPerMillisecond), at.Offset);
        var evt = new AgentEvent(Guid.NewGuid().ToString("D"), type, at, severity, data.ToJsonString());
        evt.Validate();
        return evt;
    }

    /// <summary>Mirrors the backend's rules, so one bad event can't get a whole upload batch refused.</summary>
    public void Validate()
    {
        if (!Guid.TryParse(Id, out _))
        {
            throw new ArgumentException($"Event id must be a UUID (got '{Id}').");
        }
        if (!EventTypes.IsValid(Type))
        {
            throw new ArgumentException($"Event type '{Type}' must look like 'area.action'.");
        }
        if (!EventSeverity.IsValid(Severity))
        {
            throw new ArgumentException($"Event severity '{Severity}' must be info, warning or critical.");
        }
        JsonNode? data;
        try
        {
            data = JsonNode.Parse(DataJson);
        }
        catch (JsonException e)
        {
            throw new ArgumentException("Event data is not valid JSON.", e);
        }
        if (data is not JsonObject)
        {
            throw new ArgumentException("Event data must be a JSON object.");
        }
        if (Encoding.UTF8.GetByteCount(DataJson) > MaxDataBytes)
        {
            throw new ArgumentException($"Event data is larger than {MaxDataBytes} bytes.");
        }
    }
}

/// <summary>Builders for the events the agent emits. Data shapes are part of the backend contract.</summary>
public static class AgentEvents
{
    public static AgentEvent UsbConnected(UsbDeviceArrival device, DateTimeOffset? at = null)
    {
        var data = new JsonObject { ["device_name"] = Clip(device.DeviceName) };
        AddIfPresent(data, "drive_letter", device.DriveLetter);
        AddIfPresent(data, "vendor", device.Vendor);
        AddIfPresent(data, "serial", device.Serial);
        return AgentEvent.Create(EventTypes.UsbConnected, EventSeverity.Info, data, at);
    }

    /// <param name="dryRun">True when the agent is in test mode: the drive was NOT really blocked.</param>
    public static AgentEvent UsbBlocked(UsbDeviceArrival device, bool dryRun, DateTimeOffset? at = null)
    {
        var data = new JsonObject { ["device_name"] = Clip(device.DeviceName) };
        if (dryRun)
        {
            data["dry_run"] = true;
        }
        return AgentEvent.Create(EventTypes.UsbBlocked, EventSeverity.Warning, data, at);
    }

    public static AgentEvent UsbPolicyApplied(UsbMode mode, bool dryRun, DateTimeOffset? at = null) =>
        AgentEvent.Create(EventTypes.UsbPolicyApplied, EventSeverity.Info,
            new JsonObject { ["mode"] = mode.ToWire(), ["dry_run"] = dryRun }, at);

    /// <summary>USB put back to the policy's <paramref name="mode"/> after the owner pressed "Restore USB".</summary>
    public static AgentEvent UsbRestored(UsbMode mode, bool dryRun, DateTimeOffset? at = null) =>
        AgentEvent.Create(EventTypes.UsbRestored, EventSeverity.Info,
            new JsonObject { ["mode"] = mode.ToWire(), ["dry_run"] = dryRun }, at);

    private static void AddIfPresent(JsonObject data, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            data[name] = Clip(value);
        }
    }

    private static string Clip(string? value)
    {
        var trimmed = string.IsNullOrWhiteSpace(value) ? "USB drive" : value.Trim();
        return trimmed.Length <= AgentEvent.MaxFieldLength ? trimmed : trimmed[..AgentEvent.MaxFieldLength];
    }
}

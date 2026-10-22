using System.Security.Principal;
using Microsoft.Extensions.Logging;
using Zinwe.Agent.Core.Policy;

namespace Zinwe.Agent.Core.Usb;

/// <summary>Turns a <see cref="UsbMode"/> into the PC's actual setting.</summary>
public interface IUsbEnforcer
{
    /// <summary>True when nothing on the PC is changed (test mode); events carry this as "dry_run".</summary>
    bool IsDryRun { get; }

    /// <summary>Idempotent. Throws on registry/permission failures; <see cref="UsbProtection"/> catches and logs.</summary>
    void Apply(UsbMode mode);
}

/// <summary>Reads / writes DWORDs under HKLM. Abstracted so tests never touch the real registry.</summary>
public interface IRegistryWriter
{
    /// <summary>The value, or null when the value (or key) does not exist.</summary>
    int? ReadDword(string keyPath, string valueName);

    void SetDword(string keyPath, string valueName, int value);

    /// <summary>No-op when the value (or key) does not exist.</summary>
    void DeleteValue(string keyPath, string valueName);
}

/// <summary>One registry edit under <see cref="KeyPath"/>: set <see cref="ValueName"/> to <see cref="Dword"/>, or delete it when null.</summary>
public sealed record RegistryChange(string KeyPath, string ValueName, int? Dword)
{
    public override string ToString() =>
        (Dword is { } v ? $"set {ValueName}={v}" : $"delete {ValueName}") + $" @{RemovableStoragePolicy.ShortName(KeyPath)}";
}

/// <summary>Restarts the USB storage devices that are connected now, so a new rule applies without unplugging.</summary>
public interface IUsbStorageRefresher
{
    /// <summary>Best effort, never throws. Returns how many devices were restarted.</summary>
    int RefreshConnectedDrives();
}

/// <summary>Tests / dev: restarts nothing.</summary>
public sealed class NullUsbStorageRefresher : IUsbStorageRefresher
{
    public int RefreshConnectedDrives() => 0;
}

/// <summary>
/// Windows' documented "Removable Storage Access" policy (the values Group Policy writes for "...: Deny read/write
/// access") for every class a member could copy files to — removable disks, CD/DVD, and phones/cameras (WPD) —
/// plus the USB-wide <c>StorageDevicePolicies\WriteProtect</c>, which also covers USB drives that report
/// themselves as fixed disks (many USB SSDs / hard drives). No driver needed; Windows enforces them (FR-01: only the
/// removable-disks class was set, so fixed-type USB drives and phones stayed writable).
/// </summary>
public static class RemovableStoragePolicy
{
    private const string Root = @"SOFTWARE\Policies\Microsoft\Windows\RemovableStorageDevices\";

    /// <summary>Removable Disks class (the original key; kept as KeyPath for the installer's cleanup).</summary>
    public const string KeyPath = Root + "{53f5630d-b6bf-11d0-94f2-00a0c91efb8b}";

    /// <summary>CD and DVD drives (burning).</summary>
    public const string CdDvdKeyPath = Root + "{53f56308-b6bf-11d0-94f2-00a0c91efb8b}";

    /// <summary>WPD devices: phones, cameras, media players (MTP / PTP) — both WPD class GUIDs.</summary>
    public const string WpdKeyPath = Root + "{6AC27878-A6FA-4155-BA85-F98F491D4F33}";
    public const string WpdKeyPath2 = Root + "{F33FDC04-D1AC-4E8E-9A30-19BBD4B108AE}";

    /// <summary>USB mass storage write-protect, whatever the drive reports itself as.</summary>
    public const string WriteProtectKeyPath = @"SYSTEM\CurrentControlSet\Control\StorageDevicePolicies";
    public const string WriteProtect = "WriteProtect";

    public const string DenyRead = "Deny_Read";
    public const string DenyWrite = "Deny_Write";

    /// <summary>Every device-class key the policy writes Deny_Read / Deny_Write under.</summary>
    public static readonly IReadOnlyList<string> ClassKeys = [KeyPath, CdDvdKeyPath, WpdKeyPath, WpdKeyPath2];

    /// <summary>
    /// Ordered so a transition never passes through a looser state than either end: every write-deny is set before
    /// any read-deny goes (block → read_only), and every read-deny is set before... nothing loosens on the way.
    /// </summary>
    public static IReadOnlyList<RegistryChange> ChangesFor(UsbMode mode)
    {
        var changes = new List<RegistryChange>();
        switch (mode)
        {
            case UsbMode.Allow:
                foreach (var key in ClassKeys)
                {
                    changes.Add(new(key, DenyRead, null));
                    changes.Add(new(key, DenyWrite, null));
                }
                changes.Add(new(WriteProtectKeyPath, WriteProtect, null));
                break;
            case UsbMode.ReadOnly:
                foreach (var key in ClassKeys)
                {
                    changes.Add(new(key, DenyWrite, 1));
                }
                changes.Add(new(WriteProtectKeyPath, WriteProtect, 1));
                foreach (var key in ClassKeys)
                {
                    changes.Add(new(key, DenyRead, null));
                }
                break;
            case UsbMode.Block:
                foreach (var key in ClassKeys)
                {
                    changes.Add(new(key, DenyRead, 1));
                    changes.Add(new(key, DenyWrite, 1));
                }
                changes.Add(new(WriteProtectKeyPath, WriteProtect, 1));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown USB mode.");
        }
        return changes;
    }

    /// <summary>"…\{53f5630d-…}" → "53f5630d", "…\StorageDevicePolicies" → "StorageDevicePolicies" (logs, tests).</summary>
    public static string ShortName(string keyPath)
    {
        var last = keyPath[(keyPath.LastIndexOf('\\') + 1)..];
        return last.StartsWith('{') ? last[1..9] : last;
    }
}

/// <summary>The real enforcer. Only ever built by <see cref="UsbEnforcerSelector"/> when running as LocalSystem.</summary>
public sealed class RegistryUsbEnforcer(
    IRegistryWriter registry, ILogger<RegistryUsbEnforcer> logger, IUsbStorageRefresher? refresher = null) : IUsbEnforcer
{
    private readonly IUsbStorageRefresher _refresher = refresher ?? new NullUsbStorageRefresher();

    public bool IsDryRun => false;

    public void Apply(UsbMode mode)
    {
        var changed = 0;
        foreach (var change in RemovableStoragePolicy.ChangesFor(mode))
        {
            // Skip what's already in place, so re-applying the same rule (every policy sync, every start) changes
            // nothing — and doesn't restart anyone's USB drive.
            if (registry.ReadDword(change.KeyPath, change.ValueName) == change.Dword)
            {
                continue;
            }
            if (change.Dword is { } value)
            {
                registry.SetDword(change.KeyPath, change.ValueName, value);
            }
            else
            {
                registry.DeleteValue(change.KeyPath, change.ValueName);
            }
            changed++;
        }
        if (changed == 0)
        {
            logger.LogDebug("USB storage already {Mode}", mode.ToWire());
            return;
        }
        // Windows applies these when a drive is connected; restart the ones plugged in now so the rule takes
        // effect at once instead of at the next unplug (FR-01: a drive left in stayed writable).
        var restarted = _refresher.RefreshConnectedDrives();
        logger.LogInformation("USB storage set to {Mode} ({Changes} changes; {Restarted} connected drive(s) restarted)",
            mode.ToWire(), changed, restarted);
    }
}

/// <summary>Test mode: logs exactly what the registry enforcer would do and changes nothing.</summary>
public sealed class DryRunUsbEnforcer(ILogger<DryRunUsbEnforcer> logger) : IUsbEnforcer
{
    public bool IsDryRun => true;

    public void Apply(UsbMode mode)
    {
        var changes = string.Join(", ", RemovableStoragePolicy.ChangesFor(mode));
        logger.LogInformation("[dry run] USB {Mode}: would {Changes} under HKLM, then restart connected USB drives",
            mode.ToWire(), changes);
    }
}

/// <summary>
/// SAFETY RULE: the registry enforcer is used only when the process is LocalSystem (i.e. the installed service)
/// AND Agent:Enforcement:DryRun is false. A developer's `dotnet run`, a test, or a misconfigured box gets the
/// dry-run enforcer, so nothing short of the real installed service can lock a PC's USB ports.
/// </summary>
public static class UsbEnforcerSelector
{
    public static IUsbEnforcer Select(
        bool runningAsSystem,
        bool dryRunConfigured,
        Func<IUsbEnforcer> createRegistryEnforcer,
        ILoggerFactory loggers)
    {
        var logger = loggers.CreateLogger(typeof(UsbEnforcerSelector));
        if (dryRunConfigured)
        {
            logger.LogWarning("Agent:Enforcement:DryRun is on: USB rules will be logged, not applied.");
            return new DryRunUsbEnforcer(loggers.CreateLogger<DryRunUsbEnforcer>());
        }
        if (!runningAsSystem)
        {
            logger.LogWarning("Not running as LocalSystem: USB rules will be logged, not applied (test mode).");
            return new DryRunUsbEnforcer(loggers.CreateLogger<DryRunUsbEnforcer>());
        }
        return createRegistryEnforcer();
    }

    public static bool IsRunningAsSystem()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.IsSystem;
    }
}

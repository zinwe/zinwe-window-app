using System.Security;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Zinwe.Agent.Core.Events;
using Zinwe.Agent.Core.Policy;

namespace Zinwe.Agent.Core.Usb;

/// <summary>
/// The USB module: applies the current mode through the selected <see cref="IUsbEnforcer"/> and turns device
/// arrivals into events. Never throws: a registry or disk failure is logged and the agent keeps running.
/// </summary>
public sealed class UsbProtection(IUsbEnforcer enforcer, EventQueue events, ILogger<UsbProtection> logger)
{
    private const int Unknown = -1;
    private int _mode = Unknown;

    /// <summary>The last mode successfully applied, or null before the first apply.</summary>
    public UsbMode? CurrentMode => Volatile.Read(ref _mode) is var m and not Unknown ? (UsbMode)m : null;

    public bool IsDryRun => enforcer.IsDryRun;

    /// <summary>Returns false when the setting could not be written (the caller retries later).</summary>
    /// <param name="report">Queue a usb.policy_applied event (true for a newly received policy, false for the startup re-apply).</param>
    public bool Apply(UsbMode mode, bool report)
    {
        try
        {
            enforcer.Apply(mode);
        }
        catch (Exception e) when (e is UnauthorizedAccessException or SecurityException or IOException or InvalidOperationException)
        {
            logger.LogError(e, "Could not apply USB mode {Mode}", mode.ToWire());
            return false;
        }

        Volatile.Write(ref _mode, (int)mode);
        if (report)
        {
            TryEnqueue(() => AgentEvents.UsbPolicyApplied(mode, enforcer.IsDryRun));
        }
        return true;
    }

    /// <summary>
    /// Owner-requested restore: re-apply <paramref name="mode"/> (the policy's) even if it is already the last mode
    /// applied - an over-limit lockdown writes read-only behind this class's back - and queue <c>usb.restored</c>.
    /// The event confirms the owner's own action and holds nothing about the member, so it is sent even before
    /// consent. Returns false when the setting could not be written.
    /// </summary>
    public bool Restore(UsbMode mode)
    {
        try
        {
            enforcer.Apply(mode);
        }
        catch (Exception e) when (e is UnauthorizedAccessException or SecurityException or IOException or InvalidOperationException)
        {
            logger.LogError(e, "Could not restore USB mode {Mode}", mode.ToWire());
            return false;
        }
        Volatile.Write(ref _mode, (int)mode);
        TryEnqueue(() => AgentEvents.UsbRestored(mode, enforcer.IsDryRun), always: true);
        return true;
    }

    /// <summary>Called by the watcher (any thread).</summary>
    public void OnDeviceArrived(UsbDeviceArrival device)
    {
        var blocked = CurrentMode == UsbMode.Block;
        logger.LogInformation("USB drive {Name} plugged in ({Outcome})", device.DeviceName, blocked ? "blocked" : "allowed");
        TryEnqueue(() => blocked
            ? AgentEvents.UsbBlocked(device, enforcer.IsDryRun)
            : AgentEvents.UsbConnected(device));
    }

    private void TryEnqueue(Func<AgentEvent> build, bool always = false)
    {
        try
        {
            var evt = build();
            var dropped = always ? events.EnqueueAlways(evt) : events.Enqueue(evt);
            if (dropped > 0)
            {
                logger.LogWarning("Event queue full ({Capacity}); dropped the {Dropped} oldest event(s)", events.Capacity, dropped);
            }
        }
        catch (Exception e) when (e is SqliteException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            logger.LogError(e, "Could not record a USB event");
        }
    }
}

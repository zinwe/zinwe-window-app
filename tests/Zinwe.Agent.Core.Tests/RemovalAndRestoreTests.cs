using System.Net;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Zinwe.Agent.Core.Backend;
using Zinwe.Agent.Core.Events;
using Zinwe.Agent.Core.Identity;
using Zinwe.Agent.Core.Policy;
using Zinwe.Agent.Core.Usb;

namespace Zinwe.Agent.Core.Tests;

/// <summary>A removed PC drops the company's rules; the owner's "Restore USB" puts USB back and confirms it.</summary>
public sealed class RemovalAndRestoreTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly FakeBackend _backend = new();
    private readonly FakeEnforcer _enforcer = new(dryRun: false);
    private readonly FakeFeaturePolicyApply _features = new();
    private readonly PolicyStore _store;
    private readonly EventQueue _queue;
    private readonly UsbProtection _usb;
    private readonly PolicySync _sync;

    public RemovalAndRestoreTests()
    {
        _store = new PolicyStore(_dir.Path);
        // Recording paused (no consent): confirmations of the owner's own action must still get through.
        _queue = new EventQueue(_dir.Path, recordingAllowed: () => false);
        _usb = new UsbProtection(_enforcer, _queue, NullLogger<UsbProtection>.Instance);
        _sync = new PolicySync(_backend.Client(), _store, _usb, NullLogger<PolicySync>.Instance, _features);
    }

    [Fact]
    public void Removal_clears_every_rule_and_the_cache()
    {
        _store.Save(new AgentPolicy(7, new UsbPolicy(UsbMode.Block),
            new WebPolicy(true, WebMode.Block, ["gambling"], ["example.com"]),
            new DataTransferPolicy(true, 1, 1, 1, true, UsbLockdown: true),
            new RemoteSoftwarePolicy(true, true, ["anydesk"], [])));
        _sync.ApplyCached();

        _sync.ClearForRemoval();

        Assert.Null(_sync.Current);
        Assert.Equal(UsbMode.Allow, _enforcer.Applied[^1]);
        Assert.Null(_store.Load());
        Assert.False(File.Exists(_store.FilePath));
        var released = _features.Last!;
        Assert.False(released.Web!.Enabled);
        Assert.False(released.DataTransfer!.Enabled);
        Assert.False(released.DataTransfer.UsbLockdown);
        Assert.False(released.RemoteSoftware!.Enabled);
        Assert.False(released.RemoteSoftware.BlockingEnabled);
        Assert.Equal(UsbMode.Allow, released.Usb.Mode);
    }

    [Fact]
    public void A_restart_after_removal_applies_nothing()
    {
        _store.Save(new AgentPolicy(7, new UsbPolicy(UsbMode.Block)));
        _sync.ClearForRemoval();
        _enforcer.Applied.Clear();

        var fresh = new PolicySync(_backend.Client(), _store, _usb, NullLogger<PolicySync>.Instance, _features);
        Assert.Null(fresh.ApplyCached());
        Assert.Empty(_enforcer.Applied);
    }

    [Fact]
    public void Restore_reapplies_the_policy_usb_rule_and_reports_usb_restored_even_before_consent()
    {
        _store.Save(new AgentPolicy(2, new UsbPolicy(UsbMode.Allow)));
        _sync.ApplyCached();
        _enforcer.Apply(UsbMode.ReadOnly); // the over-limit lockdown writes read-only behind UsbProtection's back

        Assert.True(_sync.RestoreUsb());

        Assert.Equal(UsbMode.Allow, _enforcer.Applied[^1]);
        Assert.Equal(UsbMode.Allow, _usb.CurrentMode);
        var evt = Assert.Single(_queue.PeekBatch(10));
        Assert.Equal(EventTypes.UsbRestored, evt.Type);
        Assert.Equal("allow", JsonNode.Parse(evt.DataJson)!["mode"]!.GetValue<string>());
    }

    [Fact]
    public void Restore_without_a_policy_allows_usb()
    {
        Assert.True(_sync.RestoreUsb());
        Assert.Equal(UsbMode.Allow, _enforcer.Applied[^1]);
    }

    [Fact]
    public void A_failed_restore_reports_nothing()
    {
        _enforcer.FailWith = new UnauthorizedAccessException("no");
        Assert.False(_sync.RestoreUsb());
        Assert.Equal(0, _queue.Count());
    }

    [Fact]
    public void Ordinary_events_are_still_dropped_before_consent()
    {
        _usb.Apply(UsbMode.Block, report: true);
        Assert.Equal(0, _queue.Count());
    }

    [Fact]
    public void The_removal_marker_round_trips_and_deletes()
    {
        var marker = new RemovalMarker(_dir.Path);
        Assert.Null(marker.Load());

        var at = new DateTimeOffset(2026, 10, 17, 14, 0, 0, TimeSpan.FromHours(5.5));
        marker.Save(new RemovalRecord("Kulkarni & Associates", at));
        Assert.Equal(new RemovalRecord("Kulkarni & Associates", at), marker.Load());

        marker.Delete();
        Assert.Null(marker.Load());
    }

    [Fact]
    public async Task A_heartbeat_401_is_the_signal_for_removal()
    {
        _backend.Respond = _ => FakeBackend.Json(HttpStatusCode.Unauthorized, """{"detail": "Device has been removed."}""");
        await Assert.ThrowsAsync<DeviceRejectedException>(() =>
            _backend.Client().HeartbeatAsync("tok", new HeartbeatRequest("pc", "win", "1", "not_started", 0, 0), CancellationToken.None));
    }

    public void Dispose() => _dir.Dispose();
}

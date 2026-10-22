using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Zinwe.Agent.Core.Policy;
using Zinwe.Agent.Core.Usb;

namespace Zinwe.Agent.Core.Tests;

public sealed class UsbEnforcementTests
{
    [Theory]
    [InlineData(false, false)] // dotnet run as a normal user, dry run off → still dry run
    [InlineData(false, true)]
    [InlineData(true, true)]   // the installed service with DryRun=true → dry run
    public void Registry_enforcer_is_never_built_unless_system_and_dry_run_off(bool isSystem, bool dryRun)
    {
        var loggers = new ListLoggerFactory();
        var built = false;

        var enforcer = UsbEnforcerSelector.Select(isSystem, dryRun, () =>
        {
            built = true;
            return new FakeEnforcer();
        }, loggers);

        Assert.IsType<DryRunUsbEnforcer>(enforcer);
        Assert.True(enforcer.IsDryRun);
        Assert.False(built);
        Assert.Contains(loggers.Entries, e => e.Level == LogLevel.Warning);
    }

    [Fact]
    public void Registry_enforcer_is_used_only_as_system_with_dry_run_off()
    {
        var real = new FakeEnforcer();

        var enforcer = UsbEnforcerSelector.Select(runningAsSystem: true, dryRunConfigured: false, () => real, new ListLoggerFactory());

        Assert.Same(real, enforcer);
    }

    [Fact]
    public void The_test_process_is_not_system()
    {
        // The guard the service relies on: anything run by a developer or CI resolves to dry run.
        Assert.False(UsbEnforcerSelector.IsRunningAsSystem());
    }

    [Fact]
    public void Policy_key_is_the_documented_removable_disks_class()
    {
        Assert.Equal(
            @"SOFTWARE\Policies\Microsoft\Windows\RemovableStorageDevices\{53f5630d-b6bf-11d0-94f2-00a0c91efb8b}",
            RemovableStoragePolicy.KeyPath);
    }

    private static readonly string[] Classes = ["53f5630d", "53f56308", "6AC27878", "F33FDC04"];

    [Fact]
    public void Read_only_denies_writes_on_every_storage_class_and_write_protects_usb()
    {
        var registry = new FakeRegistryWriter();
        var enforcer = new RegistryUsbEnforcer(registry, NullLogger<RegistryUsbEnforcer>.Instance);

        enforcer.Apply(UsbMode.ReadOnly);

        var expected = Classes.Select(c => $"{c}/Deny_Write=1").Append("StorageDevicePolicies/WriteProtect=1");
        Assert.Equal(expected.Order(), registry.Values.Select(kv => $"{kv.Key}={kv.Value}").Order());
        Assert.DoesNotContain(registry.Values.Keys, k => k.EndsWith("Deny_Read", StringComparison.Ordinal));
    }

    [Fact]
    public void Block_denies_reads_and_writes_everywhere()
    {
        var registry = new FakeRegistryWriter();
        new RegistryUsbEnforcer(registry, NullLogger<RegistryUsbEnforcer>.Instance).Apply(UsbMode.Block);

        foreach (var c in Classes)
        {
            Assert.Equal(1, registry.Values[$"{c}/Deny_Read"]);
            Assert.Equal(1, registry.Values[$"{c}/Deny_Write"]);
        }
        Assert.Equal(1, registry.Values["StorageDevicePolicies/WriteProtect"]);
    }

    [Fact]
    public void Allow_removes_everything_it_set()
    {
        var registry = new FakeRegistryWriter();
        var enforcer = new RegistryUsbEnforcer(registry, NullLogger<RegistryUsbEnforcer>.Instance);
        enforcer.Apply(UsbMode.Block);

        enforcer.Apply(UsbMode.Allow);

        Assert.Empty(registry.Values);
    }

    [Fact]
    public void Block_to_read_only_never_loosens_writes_on_the_way()
    {
        var registry = new FakeRegistryWriter();
        var enforcer = new RegistryUsbEnforcer(registry, NullLogger<RegistryUsbEnforcer>.Instance);
        enforcer.Apply(UsbMode.Block);
        registry.Operations.Clear();

        enforcer.Apply(UsbMode.ReadOnly);

        // Writes stay denied throughout: only read-denies are removed.
        Assert.All(registry.Operations, op => Assert.StartsWith("delete", op));
        Assert.All(registry.Operations, op => Assert.EndsWith("Deny_Read", op));
    }

    [Fact]
    public void Connected_drives_restart_only_when_the_rule_changes()
    {
        var registry = new FakeRegistryWriter();
        var refresher = new FakeRefresher();
        var enforcer = new RegistryUsbEnforcer(registry, NullLogger<RegistryUsbEnforcer>.Instance, refresher);

        enforcer.Apply(UsbMode.ReadOnly);
        Assert.Equal(1, refresher.Calls);

        registry.Operations.Clear();
        enforcer.Apply(UsbMode.ReadOnly); // every policy sync / restart re-applies the same rule
        Assert.Equal(1, refresher.Calls);
        Assert.Empty(registry.Operations);

        enforcer.Apply(UsbMode.Allow);
        Assert.Equal(2, refresher.Calls); // back to writable: restarted so it takes effect too
    }

    [Fact]
    public void Dry_run_enforcer_only_logs_what_it_would_do()
    {
        var loggers = new ListLoggerFactory();
        var enforcer = new DryRunUsbEnforcer(loggers.CreateLogger<DryRunUsbEnforcer>());

        enforcer.Apply(UsbMode.Block);

        Assert.True(enforcer.IsDryRun);
        var line = Assert.Single(loggers.Entries).Message;
        Assert.Contains("set Deny_Read=1", line);
        Assert.Contains("set Deny_Write=1", line);
        Assert.Contains("set WriteProtect=1", line);
    }

    [Theory]
    [InlineData("SanDisk Ultra USB Device", "SanDisk Ultra")]
    [InlineData("Kingston DataTraveler 3.0 USB Device", "Kingston DataTraveler 3.0")]
    [InlineData("  ", "USB drive")]
    [InlineData(null, "USB drive")]
    public void Model_names_are_cleaned(string? model, string expected)
    {
        Assert.Equal(expected, UsbDeviceNames.CleanModel(model));
    }

    [Theory]
    [InlineData(@"USBSTOR\DISK&VEN_SANDISK&PROD_ULTRA&REV_1.00\4C530001231220115372&0", "SANDISK", "4C530001231220115372")]
    [InlineData(@"USBSTOR\DISK&VEN_WESTERN_DIGITAL&PROD_X\ABC&0", "WESTERN DIGITAL", "ABC")]
    [InlineData(@"SCSI\DISK&VEN_NVME\5&1", "NVME", null)]
    [InlineData(null, null, null)]
    public void Vendor_and_serial_are_parsed_from_the_pnp_id(string? pnp, string? vendor, string? serial)
    {
        Assert.Equal(vendor, UsbDeviceNames.VendorFromPnpId(pnp));
        Assert.Equal(serial, UsbDeviceNames.SerialFromPnpId(pnp));
    }
}

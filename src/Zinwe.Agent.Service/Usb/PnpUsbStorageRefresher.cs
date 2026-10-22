using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;
using Zinwe.Agent.Core.Usb;

namespace Zinwe.Agent.Service.Usb;

/// <summary>
/// Restarts the USB storage devices connected right now (USB disks of any kind, and phones/cameras over MTP) with
/// Windows' own <c>pnputil /restart-device</c>, so a new USB rule applies at once (FR-01). Windows applies the
/// removable-storage policy when a device starts; without this a drive left plugged in stayed writable until it was
/// unplugged. Only the LocalSystem service builds this (with the registry enforcer). Never throws.
/// </summary>
internal sealed class PnpUsbStorageRefresher(ILogger<PnpUsbStorageRefresher> logger) : IUsbStorageRefresher
{
    private static readonly TimeSpan PerDeviceTimeout = TimeSpan.FromSeconds(20);

    public int RefreshConnectedDrives()
    {
        var restarted = 0;
        foreach (var id in ConnectedDeviceIds())
        {
            if (Restart(id))
            {
                restarted++;
            }
        }
        return restarted;
    }

    private IEnumerable<string> ConnectedDeviceIds()
    {
        var ids = new List<string>();
        try
        {
            using (var disks = new ManagementObjectSearcher("SELECT PNPDeviceID FROM Win32_DiskDrive WHERE InterfaceType='USB'"))
            {
                foreach (var disk in disks.Get())
                {
                    using (disk)
                    {
                        if (disk["PNPDeviceID"] is string id && id.Length > 0)
                        {
                            ids.Add(id);
                        }
                    }
                }
            }
            using (var phones = new ManagementObjectSearcher("SELECT PNPDeviceID FROM Win32_PnPEntity WHERE PNPClass='WPD'"))
            {
                foreach (var phone in phones.Get())
                {
                    using (phone)
                    {
                        if (phone["PNPDeviceID"] is string id && id.Length > 0)
                        {
                            ids.Add(id);
                        }
                    }
                }
            }
        }
        catch (Exception e) when (e is ManagementException or COMException or UnauthorizedAccessException)
        {
            logger.LogWarning(e, "Could not list connected USB storage devices; the new USB rule applies when they're reconnected");
        }
        return ids.Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private bool Restart(string deviceId)
    {
        try
        {
            var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "pnputil.exe"))
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add("/restart-device");
            psi.ArgumentList.Add(deviceId);
            using var process = Process.Start(psi);
            if (process is null)
            {
                return false;
            }
            if (!process.WaitForExit(PerDeviceTimeout))
            {
                try
                {
                    process.Kill();
                }
                catch (InvalidOperationException)
                {
                    // already gone
                }
                logger.LogWarning("Restarting USB device {Device} timed out", deviceId);
                return false;
            }
            // 0 = restarted; 3010 = needs a reboot to finish (still counts: Windows will apply the rule then).
            if (process.ExitCode is 0 or 3010)
            {
                return true;
            }
            logger.LogWarning("pnputil could not restart USB device {Device} (exit {Code})", deviceId, process.ExitCode);
            return false;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            logger.LogWarning(e, "Could not restart USB device {Device}", deviceId);
            return false;
        }
    }
}

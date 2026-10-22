using Microsoft.Win32;
using Zinwe.Agent.Core.Usb;

namespace Zinwe.Agent.Service.Usb;

/// <summary>
/// The only code that writes machine policy. Lives in the service (not Core) so no test can construct it, and is
/// only instantiated by <see cref="UsbEnforcerSelector"/> when the process is LocalSystem and dry run is off.
/// </summary>
internal sealed class HklmRegistryWriter : IRegistryWriter
{
    public int? ReadDword(string keyPath, string valueName)
    {
        using var key = Hklm().OpenSubKey(keyPath, writable: false);
        return key?.GetValue(valueName) is int value ? value : null;
    }

    public void SetDword(string keyPath, string valueName, int value)
    {
        using var key = Hklm().CreateSubKey(keyPath, writable: true);
        key.SetValue(valueName, value, RegistryValueKind.DWord);
    }

    public void DeleteValue(string keyPath, string valueName)
    {
        using var key = Hklm().OpenSubKey(keyPath, writable: true);
        key?.DeleteValue(valueName, throwOnMissingValue: false);
    }

    private static RegistryKey Hklm() => RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
}

using System.Reflection;

namespace Zinwe.Agent.Core;

/// <summary>Identity constants shared by the service, the tray app and the installer.</summary>
public static class AgentInfo
{
    /// <summary>Windows service name. Must match ServiceInstall/@Name in installer/Package.wxs.</summary>
    public const string ServiceName = "ZinweAgent";

    /// <summary>
    /// HKLM\SOFTWARE\Zinwe\Agent -- written by the MSI (EnrollmentKey, BackendBaseUrl). A named instance
    /// (<see cref="AgentInstance"/>, e.g. the local dev run) reads its own key, never the installed agent's.
    /// </summary>
    public static string RegistryKeyPath { get; } = AgentInstance.Suffixed(@"SOFTWARE\Zinwe\Agent", "-");

    public static string Version { get; } =
        typeof(AgentInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            .Split('+')[0]
        ?? "0.0.0";

    /// <summary>%ProgramData%\Zinwe\Agent -- device identity and local state (Agent-&lt;instance&gt; for a named instance).</summary>
    public static string DefaultDataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Zinwe", AgentInstance.Suffixed("Agent", "-"));
}

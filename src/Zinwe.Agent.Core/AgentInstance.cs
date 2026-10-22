namespace Zinwe.Agent.Core;

/// <summary>
/// Which copy of Zinwe this process is, so a developer's local build can run next to the installed one (FR-05).
///
/// The installed agent (MSI) has no instance name and uses the plain names: pipe <c>Zinwe.Agent</c>, widget folder
/// <c>%LOCALAPPDATA%\Zinwe</c>, registry <c>HKLM\SOFTWARE\Zinwe\Agent</c>, tray lock <c>Local\Zinwe.Agent.Tray</c>.
/// The local dev run (<c>run-zinwe run</c>) sets <c>ZINWE_INSTANCE=dev</c>, and every one of those names gets the
/// suffix (<c>Zinwe.Agent.dev</c>, <c>%LOCALAPPDATA%\Zinwe-dev</c>, <c>SOFTWARE\Zinwe\Agent-dev</c>, ...), so the two
/// never share a pipe, files or settings — the dev agent can't pick up the installed agent's production server
/// address or member credential, and the dev widget doesn't quit because the installed one is running.
/// </summary>
public static class AgentInstance
{
    public const string EnvironmentVariable = "ZINWE_INSTANCE";

    /// <summary>"" for the installed agent, else a short lowercase name such as "dev".</summary>
    public static string Name { get; } = Normalize(Environment.GetEnvironmentVariable(EnvironmentVariable));

    public static bool IsDefault => Name.Length == 0;

    /// <summary><paramref name="baseName"/> as is for the installed agent, else baseName + separator + instance.</summary>
    public static string Suffixed(string baseName, string separator) => Apply(baseName, separator, Name);

    /// <summary>Lowercase letters, digits and dashes only, at most 20 characters ("" when nothing usable).</summary>
    public static string Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return "";
        }
        var clean = new string(raw.Trim().ToLowerInvariant().Where(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-').ToArray());
        return clean.Length > 20 ? clean[..20] : clean;
    }

    public static string Apply(string baseName, string separator, string instance) =>
        string.IsNullOrEmpty(instance) ? baseName : baseName + separator + instance;
}

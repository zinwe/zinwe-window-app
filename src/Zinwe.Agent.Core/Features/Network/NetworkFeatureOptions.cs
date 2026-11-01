namespace Zinwe.Agent.Core.Features.Network;

/// <summary>
/// Settings for upload monitoring (webmail / New Outlook / browser uploads by destination domain), bound from
/// config section <c>Agent:Features:network</c>. OFF by default: it needs the LocalSystem service to open an ETW
/// session, and it's the newest signal, so an operator opts in. Visibility + alert only — it never blocks a send.
/// </summary>
public sealed class NetworkFeatureOptions
{
    public const string Section = "Agent:Features:network";

    /// <summary>Master switch. Off by default (needs the SYSTEM service + ETW).</summary>
    public bool Enabled { get; set; }

    /// <summary>How often accumulated per-domain upload bytes are written to the metrics store (seconds).</summary>
    public int FlushSeconds { get; set; } = 10;

    /// <summary>In-memory cap on the IP→host map, so a busy box can't grow it without bound.</summary>
    public int MaxTrackedHosts { get; set; } = 1000;

    /// <summary>
    /// Only attribute uploads from these processes (names, no ".exe"). Empty = use the built-in
    /// webmail/Outlook set (<see cref="NetworkApps.Default"/>).
    /// </summary>
    public List<string> Apps { get; set; } = [];

    /// <summary>
    /// When &gt; 0, sending this many bytes to one site within <see cref="WarnWindowMinutes"/> raises one warning
    /// event. 0 disables the alert (monitoring only). Comes from the owner's website uploads limit.
    /// </summary>
    public long WarnBytes { get; set; }

    /// <summary>
    /// The rolling window of <see cref="WarnBytes"/>. Long enough for one big upload on a slow office line (1 GB takes
    /// about 25 minutes at 5 Mbps), short enough that a day of small sends never adds up to an alert.
    /// </summary>
    public int WarnWindowMinutes { get; set; } = 60;
}

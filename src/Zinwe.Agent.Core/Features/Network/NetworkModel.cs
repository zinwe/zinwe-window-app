namespace Zinwe.Agent.Core.Features.Network;

/// <summary>Metric categories the network feature rolls into <see cref="Metrics.MetricsStore"/>.</summary>
public static class NetworkMetrics
{
    /// <summary>Bytes UPLOADED per destination domain (host), e.g. "drive.google.com". Sizes only, never content.</summary>
    public const string Upload = "net.upload";

    /// <summary>Internet data used (bytes sent + received) per app/process, e.g. "whatsapp" (FR-04).</summary>
    public const string AppData = "web.data.app";

    /// <summary>Internet data used per site (registrable domain), e.g. "chatgpt.com", when known via DNS (FR-04).</summary>
    public const string SiteData = "web.data.site";
}

/// <summary>Event types the network feature emits.</summary>
public static class NetworkEventTypes
{
    /// <summary>Uploads to one domain crossed the admin's limit within the window.</summary>
    public const string UploadOverLimit = "net.upload_over_limit";
}

/// <summary>The apps whose uploads we attribute (webmail browsers + New/classic Outlook). Process names, no ".exe".</summary>
public static class NetworkApps
{
    public static readonly IReadOnlyCollection<string> Default = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "chrome", "msedge", "firefox", "brave", "opera", "opera_gx", "vivaldi", "chromium", "arc",
        "olk", "outlook", "thunderbird",
    };
}

/// <summary>A DNS answer observed on the box: <paramref name="Host"/> currently resolves to <paramref name="IpAddress"/>.</summary>
public readonly record struct DnsAnswer(string Host, string IpAddress);

/// <summary>Bytes a process sent to a remote IP (one ETW network event). Upload direction only.</summary>
public readonly record struct UploadSample(string? ProcessName, string RemoteIpAddress, long BytesSent);

/// <summary>Bytes a process sent OR received over the network (one ETW event) — for data-used totals (FR-04).</summary>
public readonly record struct TrafficSample(string? ProcessName, string? RemoteIpAddress, long Bytes, bool Outbound);

/// <summary>
/// Streams the two signals the collector correlates: DNS answers (to learn which host an IP belongs to) and
/// per-connection SEND byte counts. The service implements this with user-mode ETW (no driver, no TLS inspection);
/// tests and non-elevated/dev contexts use <see cref="NullNetworkTraceSource"/>. Must never throw.
/// </summary>
public interface INetworkTraceSource : IDisposable
{
    event EventHandler<DnsAnswer>? DnsAnswered;
    event EventHandler<UploadSample>? BytesSent;

    /// <summary>Every send and receive (both directions), for data-used totals per app and site.</summary>
    event EventHandler<TrafficSample>? Traffic;

    /// <summary>Begin tracing. A failure to start (e.g. not elevated) is swallowed; the source just emits nothing.</summary>
    void Start();
}

/// <summary>Default when ETW can't run (tests, dev as a plain user): emits nothing, so no uploads are recorded.</summary>
public sealed class NullNetworkTraceSource : INetworkTraceSource
{
    public event EventHandler<DnsAnswer>? DnsAnswered { add { } remove { } }
    public event EventHandler<UploadSample>? BytesSent { add { } remove { } }
    public event EventHandler<TrafficSample>? Traffic { add { } remove { } }

    public void Start() { }

    public void Dispose() { }
}

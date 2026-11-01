using System.Net;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Zinwe.Agent.Core.Events;
using Zinwe.Agent.Core.Metrics;

namespace Zinwe.Agent.Core.Features.Network;

/// <summary>
/// Correlates DNS answers with per-connection SEND bytes to attribute UPLOAD volume to a destination DOMAIN, and
/// rolls it into <see cref="MetricsStore"/> under <see cref="NetworkMetrics.Upload"/> (key = host). Pure of any OS
/// dependency (the ETW plumbing lives in the service), so it is fully unit-testable with fake samples.
///
/// Consent-first: only byte COUNTS per host are kept — never a URL, a payload, or anything typed. An upload whose
/// remote IP has no known host (never resolved) is skipped in the metric, so only named sites are recorded. Never throws.
///
/// The website uploads limit (<see cref="NetworkFeatureOptions.WarnBytes"/>): what's measured is bytes a browser or mail
/// app SENT to one site (registrable domain, or the bare address when the name isn't known) within a rolling window.
/// That is a stand-in for "one upload": the agent can't see files, only sizes, so a long video call or many small
/// uploads to one site count as well, and an upload that takes longer than the window may not reach the limit. A site
/// alerts once, then again only after its window has dropped back under the limit.
///
/// Chrome and Edge usually look names up themselves, so Windows' DNS events never name their sites (seen on a real
/// trace, 1 Nov 2026). Their uploads are then counted under the address, and the alert also says which site was open in
/// the browser at the time. That's a hint, not proof: the upload may belong to a tab in the background.
/// </summary>
public sealed class NetworkUploadCollector(
    MetricsStore metrics, EventQueue events, NetworkFeatureOptions options, ILogger<NetworkUploadCollector> logger,
    Web.ForegroundInbox? foreground = null)
{
    /// <summary>How long after the tray last saw a site it still counts as "open" for an alert.</summary>
    private static readonly TimeSpan OpenSiteWindow = TimeSpan.FromMinutes(5);

    private readonly Lock _lock = new();
    private readonly Dictionary<string, string> _ipToHost = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<string> _ipOrder = new(); // FIFO eviction to honour MaxTrackedHosts
    private readonly Dictionary<string, long> _pending = new(StringComparer.OrdinalIgnoreCase); // host → bytes this window
    private readonly Dictionary<string, SiteSend> _sends = new(StringComparer.OrdinalIgnoreCase); // site or address → bytes this flush
    private readonly Dictionary<string, Window> _windows = new(StringComparer.OrdinalIgnoreCase);

    private sealed class SiteSend
    {
        public long Bytes;
        public string? App;
        public bool Named;
    }

    private sealed class Window
    {
        public readonly Queue<(DateTimeOffset At, long Bytes)> Slices = new();
        public long Total;
        public bool Alerted;
    }

    /// <summary>Remember which host an IP belongs to (latest DNS answer wins).</summary>
    public void OnDnsAnswer(DnsAnswer answer)
    {
        var host = Normalize(answer.Host);
        if (host is null || string.IsNullOrWhiteSpace(answer.IpAddress))
        {
            return;
        }
        lock (_lock)
        {
            if (!_ipToHost.ContainsKey(answer.IpAddress))
            {
                _ipOrder.Enqueue(answer.IpAddress);
                while (_ipOrder.Count > Math.Max(1, options.MaxTrackedHosts))
                {
                    _ipToHost.Remove(_ipOrder.Dequeue());
                }
            }
            _ipToHost[answer.IpAddress] = host;
        }
    }

    /// <summary>Credit a tracked app's upload bytes to the host its remote IP resolved to (unknown IP → no metric).</summary>
    public void OnUpload(UploadSample sample)
    {
        if (sample.BytesSent <= 0 || !IsTrackedApp(sample.ProcessName))
        {
            return;
        }
        lock (_lock)
        {
            var named = _ipToHost.TryGetValue(sample.RemoteIpAddress, out var host);
            if (named)
            {
                _pending[host!] = _pending.GetValueOrDefault(host!) + sample.BytesSent;
            }
            if (options.WarnBytes > 0)
            {
                CountForLimit(named ? NetworkUsageCollector.SiteOf(host) ?? host! : sample.RemoteIpAddress, named, sample);
            }
        }
    }

    /// <summary>
    /// An upload to a site that hides its name (a browser using its own DNS) still counts, under its address. One to
    /// the office network doesn't: that's the shared folders limit, not a website.
    /// </summary>
    private void CountForLimit(string site, bool named, UploadSample sample)
    {
        if (!named && !IsPublicAddress(site))
        {
            return;
        }
        if (!_sends.TryGetValue(site, out var send))
        {
            _sends[site] = send = new SiteSend { Named = named };
        }
        send.Bytes += sample.BytesSent;
        send.App = sample.ProcessName;
    }

    /// <summary>Write accumulated per-host upload bytes to the metrics store and raise any over-limit alerts.</summary>
    public void Flush(DateTimeOffset? at = null)
    {
        List<KeyValuePair<string, long>> ready;
        List<KeyValuePair<string, SiteSend>> sends;
        lock (_lock)
        {
            ready = [.. _pending];
            _pending.Clear();
            sends = [.. _sends];
            _sends.Clear();
        }

        var when = at ?? DateTimeOffset.Now;
        foreach (var (host, bytes) in ready)
        {
            TryAddMetric(host, bytes, when);
        }
        if (options.WarnBytes > 0)
        {
            AdvanceWindows(sends, when);
        }
    }

    private void AdvanceWindows(List<KeyValuePair<string, SiteSend>> sends, DateTimeOffset when)
    {
        // First drop what has aged out: a site back under the limit may alert again.
        var cutoff = when.AddMinutes(-Math.Max(1, options.WarnWindowMinutes));
        foreach (var (site, window) in _windows.ToList())
        {
            while (window.Slices.TryPeek(out var slice) && slice.At < cutoff)
            {
                window.Total -= window.Slices.Dequeue().Bytes;
            }
            if (window.Total < options.WarnBytes)
            {
                window.Alerted = false;
            }
            if (window.Slices.Count == 0)
            {
                _windows.Remove(site);
            }
        }

        foreach (var (site, send) in sends)
        {
            if (!_windows.TryGetValue(site, out var window))
            {
                _windows[site] = window = new Window();
            }
            window.Slices.Enqueue((when, send.Bytes));
            window.Total += send.Bytes;
            if (window.Total >= options.WarnBytes && !window.Alerted)
            {
                window.Alerted = true;
                var open = send.Named ? null : NetworkUsageCollector.SiteOf(foreground?.RecentSite(OpenSiteWindow));
                TryEnqueue(UploadOverLimitEvent(site, send, open, window.Total, options.WarnBytes, options.WarnWindowMinutes, when));
            }
        }
    }

    private void TryAddMetric(string host, long bytes, DateTimeOffset when)
    {
        try
        {
            metrics.Add(NetworkMetrics.Upload, host, count: 1, bytes: bytes, seconds: 0, at: when);
        }
        catch (Exception e) when (e is SqliteException or IOException or ArgumentException or InvalidOperationException)
        {
            logger.LogError(e, "Could not record an upload metric for {Host}", host);
        }
    }

    private void TryEnqueue(AgentEvent evt)
    {
        try
        {
            events.Enqueue(evt);
        }
        catch (Exception e) when (e is SqliteException or IOException or ArgumentException)
        {
            logger.LogError(e, "Could not record an upload-over-limit event");
        }
    }

    private bool IsTrackedApp(string? process)
    {
        if (string.IsNullOrWhiteSpace(process))
        {
            return false;
        }
        var allow = options.Apps.Count > 0 ? (IReadOnlyCollection<string>)options.Apps : NetworkApps.Default;
        return allow.Contains(process.Trim(), StringComparer.OrdinalIgnoreCase);
    }

    private static string? Normalize(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return null;
        }
        var h = host.Trim().TrimEnd('.').ToLowerInvariant();
        return h.Length == 0 || !h.Contains('.') ? null : h;
    }

    /// <summary>False for loopback, link-local and the private ranges (office network, VPN addresses).</summary>
    internal static bool IsPublicAddress(string address)
    {
        if (!IPAddress.TryParse(address, out var ip) || IPAddress.IsLoopback(ip))
        {
            return false;
        }
        if (ip.IsIPv4MappedToIPv6)
        {
            ip = ip.MapToIPv4();
        }
        if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            return !(ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6UniqueLocal);
        }
        var b = ip.GetAddressBytes();
        return !(b[0] == 10 || (b[0] == 172 && b[1] is >= 16 and < 32) || (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254));
    }

    private static AgentEvent UploadOverLimitEvent(
        string site, SiteSend send, string? openSite, long totalBytes, long limitBytes, int minutes, DateTimeOffset when)
    {
        var where = send.Named ? site : openSite is null ? $"a site with no name ({site})" : $"a site, with {openSite} open";
        var data = new JsonObject
        {
            ["summary"] = $"Large upload to {where}: over the {HumanSize(limitBytes)} limit",
            ["host"] = site,
            ["named"] = send.Named,
            ["app"] = send.App,
            ["total_bytes"] = totalBytes,
            ["limit_bytes"] = limitBytes,
            ["window_minutes"] = minutes,
        };
        if (openSite is not null)
        {
            data["open_site"] = openSite;
        }
        return AgentEvent.Create(NetworkEventTypes.UploadOverLimit, EventSeverity.Warning, data, when);
    }

    private static string HumanSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return $"{(unit == 0 ? value : Math.Round(value, 1))} {units[unit]}";
    }
}

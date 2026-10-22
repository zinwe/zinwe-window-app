using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Zinwe.Agent.Core.Metrics;

namespace Zinwe.Agent.Core.Features.Network;

/// <summary>
/// Internet data used on this PC (FR-04): bytes sent + received, rolled up per APP (process, e.g. "whatsapp",
/// "chrome") under <see cref="NetworkMetrics.AppData"/>, and per SITE (e.g. "chatgpt.com") under
/// <see cref="NetworkMetrics.SiteData"/> whenever the remote address was resolved through Windows DNS. Sizes only:
/// never a URL, a payload or anything typed. Pure of any OS dependency, so it's fully unit-testable. Never throws.
/// </summary>
public sealed class NetworkUsageCollector(MetricsStore metrics, NetworkFeatureOptions options, ILogger<NetworkUsageCollector> logger)
{
    private readonly Lock _lock = new();
    private readonly Dictionary<string, string> _ipToSite = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<string> _ipOrder = new();
    private readonly Dictionary<string, long> _apps = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, long> _sites = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Remember which site an IP belongs to (latest DNS answer wins), as its registrable domain.</summary>
    public void OnDnsAnswer(DnsAnswer answer)
    {
        var site = SiteOf(answer.Host);
        if (site is null || string.IsNullOrWhiteSpace(answer.IpAddress))
        {
            return;
        }
        lock (_lock)
        {
            if (!_ipToSite.ContainsKey(answer.IpAddress))
            {
                _ipOrder.Enqueue(answer.IpAddress);
                while (_ipOrder.Count > Math.Max(1, options.MaxTrackedHosts))
                {
                    _ipToSite.Remove(_ipOrder.Dequeue());
                }
            }
            _ipToSite[answer.IpAddress] = site;
        }
    }

    /// <summary>Count one network transfer (either direction) for its app and, when known, its site.</summary>
    public void OnTraffic(TrafficSample sample)
    {
        if (sample.Bytes <= 0)
        {
            return;
        }
        var app = AppOf(sample.ProcessName);
        lock (_lock)
        {
            if (app is not null)
            {
                _apps[app] = _apps.GetValueOrDefault(app) + sample.Bytes;
            }
            if (sample.RemoteIpAddress is { Length: > 0 } ip && _ipToSite.TryGetValue(ip, out var site))
            {
                _sites[site] = _sites.GetValueOrDefault(site) + sample.Bytes;
            }
        }
    }

    /// <summary>Write the accumulated bytes to the metrics store (one row per app / site per hour bucket).</summary>
    public void Flush(DateTimeOffset? at = null)
    {
        List<KeyValuePair<string, long>> apps, sites;
        lock (_lock)
        {
            if (_apps.Count == 0 && _sites.Count == 0)
            {
                return;
            }
            apps = [.. _apps];
            sites = [.. _sites];
            _apps.Clear();
            _sites.Clear();
        }
        var when = at ?? DateTimeOffset.Now;
        foreach (var (app, bytes) in apps)
        {
            TryAdd(NetworkMetrics.AppData, app, bytes, when);
        }
        foreach (var (site, bytes) in sites)
        {
            TryAdd(NetworkMetrics.SiteData, site, bytes, when);
        }
    }

    private void TryAdd(string category, string key, long bytes, DateTimeOffset when)
    {
        try
        {
            metrics.Add(category, key, count: 1, bytes: bytes, seconds: 0, at: when);
        }
        catch (Exception e) when (e is SqliteException or IOException or ArgumentException or InvalidOperationException)
        {
            logger.LogError(e, "Could not record data used for {Key}", key);
        }
    }

    /// <summary>"Chrome.exe" → "chrome"; system idle / empty names are skipped.</summary>
    public static string? AppOf(string? process)
    {
        if (string.IsNullOrWhiteSpace(process))
        {
            return null;
        }
        var name = process.Trim();
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            name = name[..^4];
        }
        name = name.ToLowerInvariant();
        return name is "idle" or "system idle process" ? null : name;
    }

    // Two-label public suffixes common in our markets, so "bbc.co.uk" stays whole (not "co.uk").
    private static readonly HashSet<string> TwoLabelSuffixes = new(StringComparer.OrdinalIgnoreCase)
    {
        "co.in", "net.in", "org.in", "gov.in", "ac.in", "edu.in", "co.uk", "org.uk", "ac.uk", "gov.uk",
        "com.au", "net.au", "org.au", "co.nz", "com.sg", "com.br", "co.jp", "com.cn", "com.mx",
    };

    /// <summary>
    /// The site a host belongs to: its registrable domain ("rr3---sn-x.googlevideo.com" → "googlevideo.com",
    /// "www.bbc.co.uk" → "bbc.co.uk"), so one site doesn't scatter over hundreds of server names.
    /// </summary>
    public static string? SiteOf(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return null;
        }
        var h = host.Trim().TrimEnd('.').ToLowerInvariant();
        if (h.Length == 0 || h.Contains(' ') || !h.Contains('.') || h.EndsWith(".local", StringComparison.Ordinal) ||
            System.Net.IPAddress.TryParse(h, out _))
        {
            return null;
        }
        var labels = h.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (labels.Length <= 2)
        {
            return string.Join('.', labels);
        }
        var lastTwo = labels[^2] + "." + labels[^1];
        var take = TwoLabelSuffixes.Contains(lastTwo) ? 3 : 2;
        return string.Join('.', labels[^take..]);
    }
}

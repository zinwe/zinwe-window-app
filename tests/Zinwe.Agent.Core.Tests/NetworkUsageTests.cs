using Microsoft.Extensions.Logging.Abstractions;
using Zinwe.Agent.Core.Features.Network;
using Zinwe.Agent.Core.Metrics;
using Zinwe.Agent.Core.Policy;

namespace Zinwe.Agent.Core.Tests;

/// <summary>FR-04: internet data used per app and per site.</summary>
public sealed class NetworkUsageTests
{
    private static readonly DateTimeOffset T = new(2026, 10, 22, 9, 0, 0, TimeSpan.Zero);

    private static NetworkUsageCollector Build(TempDir dir, out MetricsStore metrics)
    {
        metrics = new MetricsStore(dir.Path);
        return new NetworkUsageCollector(metrics, new NetworkFeatureOptions(), NullLogger<NetworkUsageCollector>.Instance);
    }

    [Theory]
    [InlineData("chatgpt.com", "chatgpt.com")]
    [InlineData("rr3---sn-a5mekn6s.googlevideo.com", "googlevideo.com")]
    [InlineData("web.whatsapp.com.", "whatsapp.com")]
    [InlineData("www.bbc.co.uk", "bbc.co.uk")]
    [InlineData("mail.example.co.in", "example.co.in")]
    [InlineData("WWW.YouTube.COM", "youtube.com")]
    [InlineData("printer.local", null)]
    [InlineData("localhost", null)]
    [InlineData("142.250.1.1", null)]
    [InlineData("", null)]
    public void SiteOf_gives_the_registrable_domain(string host, string? expected) =>
        Assert.Equal(expected, NetworkUsageCollector.SiteOf(host));

    [Theory]
    [InlineData("WhatsApp.exe", "whatsapp")]
    [InlineData("chrome", "chrome")]
    [InlineData("Idle", null)]
    [InlineData("  ", null)]
    [InlineData(null, null)]
    public void AppOf_normalises_process_names(string? process, string? expected) =>
        Assert.Equal(expected, NetworkUsageCollector.AppOf(process));

    [Fact]
    public void Both_directions_count_per_app_and_per_known_site()
    {
        using var dir = new TempDir();
        var usage = Build(dir, out var metrics);
        usage.OnDnsAnswer(new DnsAnswer("chatgpt.com", "104.18.32.47"));

        usage.OnTraffic(new TrafficSample("chrome", "104.18.32.47", 1_000, Outbound: true));
        usage.OnTraffic(new TrafficSample("chrome", "104.18.32.47", 9_000, Outbound: false));
        usage.OnTraffic(new TrafficSample("WhatsApp", "157.240.1.1", 5_000, Outbound: false)); // site unknown: app only
        usage.Flush(T);

        var rows = metrics.Snapshot();
        Assert.Equal(10_000, rows.Single(r => r.Category == NetworkMetrics.AppData && r.Key == "chrome").Bytes);
        Assert.Equal(5_000, rows.Single(r => r.Category == NetworkMetrics.AppData && r.Key == "whatsapp").Bytes);
        Assert.Equal(10_000, rows.Single(r => r.Category == NetworkMetrics.SiteData && r.Key == "chatgpt.com").Bytes);
        Assert.DoesNotContain(rows, r => r.Category == NetworkMetrics.SiteData && r.Key != "chatgpt.com");
    }

    [Fact]
    public void Server_names_of_one_site_roll_up_together()
    {
        using var dir = new TempDir();
        var usage = Build(dir, out var metrics);
        usage.OnDnsAnswer(new DnsAnswer("rr1---sn-x.googlevideo.com", "10.0.0.1"));
        usage.OnDnsAnswer(new DnsAnswer("rr2---sn-y.googlevideo.com", "10.0.0.2"));
        usage.OnTraffic(new TrafficSample("msedge", "10.0.0.1", 300, false));
        usage.OnTraffic(new TrafficSample("msedge", "10.0.0.2", 700, false));
        usage.Flush(T);

        Assert.Equal(1_000, metrics.Snapshot().Single(r => r.Category == NetworkMetrics.SiteData).Bytes);
    }

    [Fact]
    public void Nothing_to_flush_writes_nothing_and_flush_resets_the_window()
    {
        using var dir = new TempDir();
        var usage = Build(dir, out var metrics);
        usage.Flush(T);
        Assert.Empty(metrics.Snapshot());

        usage.OnTraffic(new TrafficSample("chrome", null, 100, false));
        usage.Flush(T);
        usage.Flush(T);
        Assert.Equal(100, metrics.Snapshot().Single().Bytes);
    }

    [Fact]
    public void Measuring_follows_online_protection_in_the_policy()
    {
        var defaults = new NetworkFeatureOptions();
        var on = new WebPolicy(true, WebMode.Monitor, [], []);
        var off = on with { Enabled = false };

        Assert.True(NetworkPolicyMap.FromPolicy(defaults, on).Enabled);
        Assert.False(NetworkPolicyMap.FromPolicy(defaults, off).Enabled);
        Assert.False(NetworkPolicyMap.FromPolicy(defaults, null).Enabled); // no policy yet: defaults (off)
        Assert.True(NetworkPolicyMap.FromPolicy(new NetworkFeatureOptions { Enabled = true }, off).Enabled); // config can force it
    }
}

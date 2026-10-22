using Microsoft.Extensions.Logging.Abstractions;
using Zinwe.Agent.Core.Events;
using Zinwe.Agent.Core.Features.Web;
using Zinwe.Agent.Core.Ipc;
using Zinwe.Agent.Core.Metrics;

namespace Zinwe.Agent.Core.Tests;

/// <summary>
/// The tray reads what's in front of the member (the service in Session 0 can't) and posts it over the pipe; the web
/// feature records it as app time and website usage under the owner's settings.
/// </summary>
public class ForegroundReportTests
{
    [Fact]
    public void Inbox_keeps_good_reports_and_drops_bad_ones()
    {
        var inbox = new ForegroundInbox();
        var accepted = inbox.Post([
            new ForegroundReport("chrome", "YouTube.com", 60, 4),
            new ForegroundReport("", null, 15, 1),          // no app
            new ForegroundReport("winword", null, 0, 1),    // no time
            new ForegroundReport("excel", null, 9999, 1),   // more than one batch could hold
        ]);
        Assert.Equal(1, accepted);
        var drained = inbox.Drain();
        Assert.Single(drained);
        Assert.Equal("youtube.com", drained[0].Domain);
        Assert.Empty(inbox.Drain());
        Assert.True(inbox.TrayActive(TimeSpan.FromMinutes(2)));
    }

    [Fact]
    public void Usage_request_round_trips_over_the_pipe_protocol()
    {
        var request = new PipeProtocol.Request(PipeProtocol.RequestTypes.Usage, Usage: [new ForegroundReport("msedge", "zinwe.io", 30, 2)]);
        var line = PipeProtocol.Serialize(request);
        var back = PipeProtocol.DeserializeRequest(line);
        Assert.Equal(PipeProtocol.RequestTypes.Usage, back!.Type);
        Assert.Equal("zinwe.io", back.Usage![0].Domain);
        Assert.Equal(30, back.Usage[0].Seconds);
    }

    [Fact]
    public void Module_records_tray_reports_as_app_time_and_website_usage()
    {
        using var dir = new TempDir();
        var (module, metrics, inbox) = Build(dir, new WebFeatureOptions());
        inbox.Post([new ForegroundReport("chrome", "youtube.com", 60, 4), new ForegroundReport("winword", null, 45, 3)]);

        module.RecordTrayReports();

        var rows = metrics.Snapshot();
        Assert.Contains(rows, r => r.Category == WebMetrics.ForegroundApp && r.Key == "chrome" && r.Seconds == 60 && r.Count == 4);
        Assert.Contains(rows, r => r.Category == WebMetrics.ForegroundApp && r.Key == "winword" && r.Seconds == 45);
        Assert.Contains(rows, r => r.Category == WebMetrics.WebDomain && r.Key == "youtube.com" && r.Seconds == 60);
        Assert.DoesNotContain(rows, r => r.Category == WebMetrics.WebDomain && r.Key == "winword");
    }

    [Fact]
    public void Owner_settings_decide_what_is_recorded()
    {
        using var dir = new TempDir();
        var (module, metrics, inbox) = Build(dir, new WebFeatureOptions { CollectWebDomains = false });
        inbox.Post([new ForegroundReport("chrome", "youtube.com", 60, 4)]);

        module.RecordTrayReports();

        var rows = metrics.Snapshot();
        Assert.Contains(rows, r => r.Category == WebMetrics.ForegroundApp);
        Assert.DoesNotContain(rows, r => r.Category == WebMetrics.WebDomain);
    }

    [Fact]
    public void A_domain_from_a_non_browser_is_ignored()
    {
        using var dir = new TempDir();
        var (module, metrics, inbox) = Build(dir, new WebFeatureOptions());
        inbox.Post([new ForegroundReport("notepad", "example.com", 15, 1)]);

        module.RecordTrayReports();

        Assert.DoesNotContain(metrics.Snapshot(), r => r.Category == WebMetrics.WebDomain);
    }

    private static (WebFeatureModule Module, MetricsStore Metrics, ForegroundInbox Inbox) Build(TempDir dir, WebFeatureOptions options)
    {
        var events = new EventQueue(dir.Path);
        var metrics = new MetricsStore(dir.Path);
        var inbox = new ForegroundInbox();
        var protection = new WebProtection(new DryRunWebBlocker(NullLogger<DryRunWebBlocker>.Instance), events, NullLogger<WebProtection>.Instance);
        var sampler = new ForegroundUsageSampler(new FakeForegroundWindowSource(), metrics, NullLogger<ForegroundUsageSampler>.Instance);
        var domains = new BrowserDomainSampler(new NullActiveTabSource(), metrics, NullLogger<BrowserDomainSampler>.Instance);
        var module = new WebFeatureModule(protection, sampler, domains, new FakeBlockedAttemptSource(), new Blocklist([]), options,
            NullLogger<WebFeatureModule>.Instance, inbox: inbox, metrics: metrics);
        return (module, metrics, inbox);
    }
}

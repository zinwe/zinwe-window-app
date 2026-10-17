using System.Net;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Zinwe.Agent.Core.Backend;
using Zinwe.Agent.Core.Events;
using Zinwe.Agent.Core.Features.Web;
using Zinwe.Agent.Core.Metrics;

namespace Zinwe.Agent.Core.Tests;

/// <summary>The 2026-10-17 contract additions (consent, usb_restore, company) and website-blocking notes.</summary>
public sealed class FixRoundContractTests
{
    private static readonly HeartbeatRequest Beat = new("pc", "win", "1.0.3", "not_started", 0, 0);

    [Fact]
    public async Task Heartbeat_sends_the_consent_decision_with_the_old_fields_only_for_an_agreement()
    {
        var backend = new FakeBackend
        {
            Respond = _ => FakeBackend.Json(HttpStatusCode.OK,
                """{"device_id": 1, "server_time": "2026-10-17T10:00:00Z", "next_heartbeat_seconds": 60, "policy_version": 1}"""),
        };
        var at = new DateTimeOffset(2026, 10, 17, 9, 0, 0, TimeSpan.Zero);

        await backend.Client().HeartbeatAsync("tok", Beat with
        {
            ConsentVersion = "2026-10-17.atrw",
            ConsentAt = at,
            Consent = new ConsentDecision("given", "2026-10-17.atrw"),
        }, CancellationToken.None);
        var given = JsonNode.Parse(backend.Requests[^1].Body)!;
        Assert.Equal("given", given["consent"]!["state"]!.GetValue<string>());
        Assert.Equal("2026-10-17.atrw", given["consent"]!["version"]!.GetValue<string>());
        Assert.Equal("2026-10-17.atrw", given["consent_version"]!.GetValue<string>());

        await backend.Client().HeartbeatAsync("tok", Beat with { Consent = new ConsentDecision("declined", "2026-10-17.atrw") },
            CancellationToken.None);
        var declined = JsonNode.Parse(backend.Requests[^1].Body)!;
        Assert.Equal("declined", declined["consent"]!["state"]!.GetValue<string>());
        Assert.Null(declined["consent_version"]); // the old field means "given"

        await backend.Client().HeartbeatAsync("tok", Beat, CancellationToken.None);
        var pending = JsonNode.Parse(backend.Requests[^1].Body)!.AsObject();
        Assert.False(pending.ContainsKey("consent")); // left out entirely, never "consent": null
    }

    [Fact]
    public async Task Heartbeat_reads_consent_state_usb_restore_and_the_company()
    {
        var backend = new FakeBackend
        {
            Respond = _ => FakeBackend.Json(HttpStatusCode.OK, """
                {"device_id": 1, "server_time": "2026-10-17T10:00:00Z", "next_heartbeat_seconds": 60, "policy_version": 1,
                 "consent_state": "declined", "usb_restore": true, "company_name": "Kulkarni & Associates", "owner_name": "Ravi Kulkarni"}
                """),
        };

        var beat = await backend.Client().HeartbeatAsync("tok", Beat, CancellationToken.None);

        Assert.Equal("declined", beat.ConsentState);
        Assert.True(beat.UsbRestore);
        Assert.Equal("Kulkarni & Associates", beat.CompanyName);
        Assert.Equal("Ravi Kulkarni", beat.OwnerName);
    }

    [Theory]
    [InlineData("""{"device_id": 1, "server_time": "2026-10-17T10:00:00Z", "next_heartbeat_seconds": 60, "policy_version": 1}""")]
    [InlineData("""{"device_id": 1, "server_time": "2026-10-17T10:00:00Z", "next_heartbeat_seconds": 60, "policy_version": 1, "usb_restore": null, "consent_state": null}""")]
    public async Task An_older_backend_or_nulls_keep_todays_behaviour(string body)
    {
        var backend = new FakeBackend { Respond = _ => FakeBackend.Json(HttpStatusCode.OK, body) };

        var beat = await backend.Client().HeartbeatAsync("tok", Beat, CancellationToken.None);

        Assert.NotEqual(true, beat.UsbRestore);
        Assert.Null(beat.ConsentState);
        Assert.Null(beat.CompanyName);
    }

    [Fact]
    public async Task A_refused_credential_says_which_field()
    {
        var backend = new FakeBackend
        {
            Respond = _ => FakeBackend.Json(HttpStatusCode.BadRequest, """{"email": ["This email does not match the member id."]}"""),
        };
        var e = await Assert.ThrowsAsync<EnrollmentRejectedException>(() => backend.Client().EnrollAsync(
            new EnrollRequest("a@b.in", "ZM-AAAA-BBBB", "m", "pc", "win", "1"), CancellationToken.None));
        Assert.Equal("email", e.Field);
    }

    [Fact]
    public void The_policy_may_carry_the_company_and_old_policies_still_parse()
    {
        using var dir = new TempDir();
        var store = new Policy.PolicyStore(dir.Path);
        store.Save(new Policy.AgentPolicy(1, new Policy.UsbPolicy(Policy.UsbMode.Allow), CompanyName: "Acme"));
        Assert.Equal("Acme", store.Load()!.CompanyName);
    }

    // ---- Website blocking explains itself ----

    private sealed class RecordingNotices : IWebBlockNotices
    {
        public List<string> InForce { get; } = [];
        public List<(string Domain, string? Category)> Blocked { get; } = [];

        public void BlocklistInForce(Blocklist list) => InForce.Add(string.Join(",", list.Categories));

        public void SiteBlocked(string domain, string? category) => Blocked.Add((domain, category));
    }

    private sealed class ManualClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static (WebFeatureModule Module, FakeHostsFileWriter Hosts, RecordingNotices Notices) Module(
        WebFeatureOptions options, Blocklist list, TempDir dir, BlockedSiteThrottle? throttle = null)
    {
        var events = new EventQueue(dir.Path);
        var metrics = new MetricsStore(dir.Path);
        var hosts = new FakeHostsFileWriter();
        var protection = new WebProtection(new HostsFileWebBlocker(hosts, NullLogger<HostsFileWebBlocker>.Instance),
            events, NullLogger<WebProtection>.Instance);
        var sampler = new ForegroundUsageSampler(new FakeForegroundWindowSource(), metrics, NullLogger<ForegroundUsageSampler>.Instance);
        var domains = new BrowserDomainSampler(new NullActiveTabSource(), metrics, NullLogger<BrowserDomainSampler>.Instance);
        var notices = new RecordingNotices();
        var module = new WebFeatureModule(protection, sampler, domains, new FakeBlockedAttemptSource(), list, options,
            NullLogger<WebFeatureModule>.Instance, notices, throttle);
        return (module, hosts, notices);
    }

    [Fact]
    public async Task Blocking_switched_off_takes_the_old_block_list_out_of_the_hosts_file()
    {
        using var dir = new TempDir();
        var (module, hosts, _) = Module(new WebFeatureOptions { BlockingEnabled = false, CollectForegroundUsage = false },
            new Blocklist([new BlockRule("a.test", "gambling")]), dir);

        await module.StartAsync(CancellationToken.None);
        await module.StopAsync(CancellationToken.None);

        Assert.Equal(1, hosts.Cleared);
        Assert.Equal(0, hosts.Sets);
    }

    [Fact]
    public async Task The_web_feature_off_also_clears_it()
    {
        using var dir = new TempDir();
        var (module, hosts, _) = Module(new WebFeatureOptions { Enabled = false }, Blocklist.Empty, dir);

        await module.StartAsync(CancellationToken.None);

        Assert.Equal(1, hosts.Cleared);
    }

    [Fact]
    public async Task A_block_list_in_force_is_announced_and_a_blocked_site_is_named_once_per_window()
    {
        using var dir = new TempDir();
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        var (module, _, notices) = Module(new WebFeatureOptions { CollectForegroundUsage = false },
            new Blocklist([new BlockRule("gambling-sample.test", "gambling")]), dir, new BlockedSiteThrottle(clock));

        await module.StartAsync(CancellationToken.None);
        Assert.Equal(["gambling"], notices.InForce);

        module.NoticeIfBlocked("www.gambling-sample.test");
        module.NoticeIfBlocked("gambling-sample.test");
        module.NoticeIfBlocked("example.com");
        Assert.Equal([("gambling-sample.test", (string?)"gambling")], notices.Blocked);

        clock.Now += BlockedSiteThrottle.Window;
        module.NoticeIfBlocked("gambling-sample.test");
        Assert.Equal(2, notices.Blocked.Count);
        await module.StopAsync(CancellationToken.None);
    }

    [Fact]
    public void The_words_say_it_is_the_company_rule_not_the_internet()
    {
        var (title, body) = WebBlockWording.Blocked("bet.example", "gambling");
        Assert.Equal("Zinwe blocked bet.example — your company's rule", title);
        Assert.Contains("Your internet is fine", body);

        var list = new Blocklist([new BlockRule("a.test", "gambling"), new BlockRule("b.test", "custom")]);
        var (_, inForce) = WebBlockWording.InForce(list);
        Assert.Contains("gambling sites", inForce);
        Assert.Contains("sites your company added", inForce);
        Assert.Contains(" and ", inForce);
        Assert.Equal(WebBlockWording.StableId(list), WebBlockWording.StableId(new Blocklist(list.Rules)));
        Assert.True(WebBlockWording.StableId(list) < 0);
    }

    [Fact]
    public void Find_matches_the_domain_and_its_www_form()
    {
        var list = new Blocklist([new BlockRule("a.test", "malware")]);
        Assert.Equal("malware", list.Find("https://www.a.test/x")?.Category);
        Assert.Null(list.Find("b.test"));
        Assert.Null(list.Find(null));
    }
}

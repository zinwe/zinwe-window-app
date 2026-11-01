using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Zinwe.Agent.Core.Events;
using Zinwe.Agent.Core.Features.Network;
using Zinwe.Agent.Core.Features.Web;
using Zinwe.Agent.Core.Metrics;
using Zinwe.Agent.Core.Policy;
using Zinwe.Agent.Core.Usb;

namespace Zinwe.Agent.Core.Tests;

/// <summary>The real site lists on a PC: fetched set, disk cache, allow list, older backends, the hosts file at size.</summary>
public sealed class WebBlockSetTests : IDisposable
{
    private static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> Sites =
        new Dictionary<string, IReadOnlyList<string>>
        {
            ["adult"] = ["hidden-site.example", "www.other-hidden.example"],
            ["gambling"] = ["bet365.example", "m.betfair.example"],
            ["social"] = ["facebook.example"],
            ["custom"] = ["stale-custom.example"],
        };

    private readonly TempDir _dir = new();
    private readonly WebBlockSetStore _store;

    public WebBlockSetTests()
    {
        Directory.CreateDirectory(_dir.Path);
        _store = new WebBlockSetStore(_dir.Path);
    }

    public void Dispose() => _dir.Dispose();

    private static WebFeatureOptions ListOptions(string[] categories, string[]? custom = null, string[]? allow = null) => new()
    {
        ListVersion = "v1",
        UseBuiltInSampleList = false,
        Categories = [.. categories],
        CustomDomains = [.. custom ?? []],
        AllowDomains = [.. allow ?? []],
    };

    // ---- Blocklist from the set ----

    [Fact]
    public void Lists_switched_on_come_from_the_set_and_the_sample_is_gone()
    {
        var list = Blocklist.FromOptions(ListOptions(["adult", "gambling"]), new WebBlockSet("v1", Sites));

        Assert.Contains(list.Rules, r => r is { Domain: "hidden-site.example", Category: "adult" });
        Assert.Contains(list.Rules, r => r is { Domain: "bet365.example", Category: "gambling" });
        Assert.DoesNotContain(list.Rules, r => r.Domain == "facebook.example");
        Assert.DoesNotContain(list.Rules, r => r.Domain.EndsWith(".test", StringComparison.Ordinal));
    }

    [Fact]
    public void Allow_list_wins_over_every_list_and_covers_subdomains()
    {
        var list = Blocklist.FromOptions(
            ListOptions(["adult", "gambling"], allow: ["bet365.example", "betfair.example", "HIDDEN-SITE.example"]),
            new WebBlockSet("v1", Sites));

        Assert.Null(list.Find("bet365.example"));
        Assert.Null(list.Find("m.betfair.example"));
        Assert.Null(list.Find("www.hidden-site.example"));
        Assert.NotNull(list.Find("other-hidden.example"));
    }

    [Fact]
    public void Allow_list_wins_over_own_sites_too_so_no_allowed_site_is_ever_blocked()
    {
        // The backend keeps the two apart; if a stale policy didn't, the PC still never blocks an allowed site.
        var list = Blocklist.FromOptions(ListOptions(["gambling"], custom: ["both.example"], allow: ["both.example"]), new WebBlockSet("v1", Sites));

        Assert.Null(list.Find("both.example"));
    }

    [Fact]
    public void Own_sites_come_from_the_policy_not_a_stale_set()
    {
        var list = Blocklist.FromOptions(ListOptions(["gambling", "custom"], custom: ["new-custom.example"]), new WebBlockSet("v0", Sites));

        Assert.NotNull(list.Find("new-custom.example"));
        Assert.Null(list.Find("stale-custom.example"));
    }

    [Fact]
    public void A_stale_set_honours_lists_switched_off_since()
    {
        var list = Blocklist.FromOptions(ListOptions(["gambling"]), new WebBlockSet("v0", Sites));

        Assert.Equal(["gambling"], list.Categories);
    }

    [Fact]
    public void No_lists_switched_on_blocks_only_own_sites()
    {
        var list = Blocklist.FromOptions(ListOptions([], custom: ["a.example"]), new WebBlockSet("v1", Sites));

        Assert.Equal(["custom"], list.Categories);
        Assert.Single(list.Rules);
    }

    [Fact]
    public void A_new_install_with_no_set_yet_still_blocks_own_sites()
    {
        var list = Blocklist.FromOptions(ListOptions(["adult"], custom: ["a.example"]));

        Assert.Equal(["custom"], list.Categories);
    }

    [Fact]
    public void Older_backend_keeps_the_built_in_sample_and_own_sites()
    {
        var options = WebPolicyMap.FromPolicy(new WebFeatureOptions(), new WebPolicy(true, WebMode.Block, ["malware"], ["a.example"]));

        Assert.True(options.UseBuiltInSampleList);
        Assert.Null(options.ListVersion);
        var list = Blocklist.FromOptions(options, new WebBlockSet("old", Sites));
        Assert.NotNull(list.Find("malware-sample.test"));
        Assert.NotNull(list.Find("a.example"));
    }

    [Fact]
    public void A_policy_with_lists_maps_over()
    {
        var options = WebPolicyMap.FromPolicy(new WebFeatureOptions(),
            new WebPolicy(true, WebMode.Block, ["adult"], ["a.example"], ["b.example"], "v9"));

        Assert.False(options.UseBuiltInSampleList);
        Assert.Equal("v9", options.ListVersion);
        Assert.Equal(["b.example"], options.AllowDomains);
    }

    // ---- Disk cache ----

    [Fact]
    public void The_set_survives_a_restart()
    {
        _store.Save(new WebBlockSet("v1", Sites));

        var restarted = new WebBlockSetStore(_dir.Path);
        Assert.Null(restarted.Current);
        var loaded = restarted.Load();

        Assert.Equal("v1", loaded!.Version);
        Assert.Equal(Sites["gambling"], loaded.Sites["gambling"]);
        Assert.Same(loaded, restarted.Current);
    }

    [Fact]
    public void A_broken_cache_file_counts_as_none()
    {
        File.WriteAllText(_store.FilePath, "{ not json");
        Assert.Null(_store.Load());
    }

    [Fact]
    public void Delete_forgets_it_in_memory_and_on_disk()
    {
        _store.Save(new WebBlockSet("v1", Sites));
        _store.Delete();

        Assert.Null(_store.Current);
        Assert.False(File.Exists(_store.FilePath));
    }

    [Fact]
    public void Save_refuses_an_empty_set_name()
    {
        Assert.Throws<ArgumentException>(() => _store.Save(new WebBlockSet("", Sites)));
    }

    // ---- Fetching only on change ----

    private sealed class CountingFeatures : Features.IFeaturePolicyApply
    {
        public int Applied { get; private set; }

        public Task ApplyAsync(AgentPolicy policy, CancellationToken ct)
        {
            Applied++;
            return Task.CompletedTask;
        }
    }

    private const string PolicyJson = """
        {"version": 5, "usb": {"mode": "allow"},
         "web": {"enabled": true, "mode": "block", "categories": ["gambling"], "custom_domains": [], "allow_domains": [], "list_version": "v1"}}
        """;

    private const string SetJson = """{"version": "v1", "sites": {"gambling": ["bet365.example"], "custom": []}}""";
    private const string SetJson2 = """{"version": "v2", "sites": {"gambling": ["bet365.example", "new.example"], "custom": []}}""";

    private (PolicySync Sync, FakeBackend Backend, CountingFeatures Features) Build()
    {
        var backend = new FakeBackend();
        var queue = new EventQueue(_dir.Path);
        var usb = new UsbProtection(new FakeEnforcer(dryRun: true), queue, NullLogger<UsbProtection>.Instance);
        var features = new CountingFeatures();
        var sync = new PolicySync(backend.Client(), new PolicyStore(_dir.Path), usb, NullLogger<PolicySync>.Instance, features, _store);
        return (sync, backend, features);
    }

    private static int Calls(FakeBackend backend, string end) => backend.Requests.Count(r => r.Path.EndsWith(end, StringComparison.Ordinal));

    [Fact]
    public async Task A_new_policy_brings_its_set_and_the_features_start_on_it()
    {
        var (sync, backend, features) = Build();
        backend.Respond = r => FakeBackend.Json(HttpStatusCode.OK, r.Path.EndsWith("blocklist/", StringComparison.Ordinal) ? SetJson : PolicyJson);

        Assert.True(await sync.SyncAsync("tok", 5, CancellationToken.None, "v1"));

        Assert.Equal("v1", _store.Current!.Version);
        Assert.Equal(1, features.Applied);
    }

    [Fact]
    public async Task The_same_name_fetches_nothing()
    {
        var (sync, backend, features) = Build();
        backend.Respond = r => FakeBackend.Json(HttpStatusCode.OK, r.Path.EndsWith("blocklist/", StringComparison.Ordinal) ? SetJson : PolicyJson);
        await sync.SyncAsync("tok", 5, CancellationToken.None, "v1");
        var before = backend.Requests.Count;

        Assert.False(await sync.SyncAsync("tok", 5, CancellationToken.None, "v1"));

        Assert.Equal(before, backend.Requests.Count);
        Assert.Equal(1, features.Applied);
    }

    [Fact]
    public async Task A_new_list_version_fetches_the_set_without_touching_the_policy()
    {
        var (sync, backend, features) = Build();
        backend.Respond = r => FakeBackend.Json(HttpStatusCode.OK, r.Path.EndsWith("blocklist/", StringComparison.Ordinal) ? SetJson : PolicyJson);
        await sync.SyncAsync("tok", 5, CancellationToken.None, "v1");
        backend.Respond = _ => FakeBackend.Json(HttpStatusCode.OK, SetJson2);

        Assert.True(await sync.SyncAsync("tok", 5, CancellationToken.None, "v2"));

        Assert.Equal("v2", _store.Current!.Version);
        Assert.Equal(2, features.Applied);
        Assert.Equal(1, Calls(backend, "policy/current/"));
    }

    [Fact]
    public async Task A_failed_fetch_keeps_what_is_in_force_and_tries_again()
    {
        var (sync, backend, features) = Build();
        backend.Respond = r => FakeBackend.Json(HttpStatusCode.OK, r.Path.EndsWith("blocklist/", StringComparison.Ordinal) ? SetJson : PolicyJson);
        await sync.SyncAsync("tok", 5, CancellationToken.None, "v1");

        backend.Respond = _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        Assert.False(await sync.SyncAsync("tok", 5, CancellationToken.None, "v2"));
        Assert.Equal("v1", _store.Current!.Version);

        backend.Respond = _ => FakeBackend.Json(HttpStatusCode.OK, SetJson2);
        Assert.True(await sync.SyncAsync("tok", 5, CancellationToken.None, "v2"));
        Assert.Equal("v2", _store.Current!.Version);
    }

    [Fact]
    public async Task Offline_after_a_restart_the_cached_set_is_in_force_before_any_network_call()
    {
        _store.Save(new WebBlockSet("v1", Sites));
        new PolicyStore(_dir.Path).Save(new AgentPolicy(5, new UsbPolicy(UsbMode.Allow), new WebPolicy(true, WebMode.Block, ["gambling"], [], [], "v1")));
        var restarted = new WebBlockSetStore(_dir.Path);
        var backend = new FakeBackend(); // throws if called
        var queue = new EventQueue(_dir.Path);
        var usb = new UsbProtection(new FakeEnforcer(dryRun: true), queue, NullLogger<UsbProtection>.Instance);
        var sync = new PolicySync(backend.Client(), new PolicyStore(_dir.Path), usb, NullLogger<PolicySync>.Instance, new CountingFeatures(), restarted);

        sync.ApplyCached();

        Assert.Equal("v1", restarted.Current!.Version);
        Assert.Empty(backend.Requests);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task An_older_backend_is_never_asked_for_a_set()
    {
        var (sync, backend, _) = Build();
        const string older = """{"version": 5, "usb": {"mode": "allow"}, "web": {"enabled": true, "mode": "block", "categories": ["malware"], "custom_domains": []}}""";
        backend.Respond = _ => FakeBackend.Json(HttpStatusCode.OK, older);

        await sync.SyncAsync("tok", 5, CancellationToken.None);

        Assert.Equal(0, Calls(backend, "blocklist/"));
        Assert.Null(sync.Current!.Web!.ListVersion);
    }

    [Fact]
    public async Task An_agent_just_updated_from_before_the_lists_fetches_the_policy_again()
    {
        // policy.json written by 1.3.6: same version as the server, no list_version.
        new PolicyStore(_dir.Path).Save(new AgentPolicy(5, new UsbPolicy(UsbMode.Allow), new WebPolicy(true, WebMode.Block, ["gambling"], [])));
        var (sync, backend, _) = Build();
        sync.ApplyCached();
        backend.Respond = r => FakeBackend.Json(HttpStatusCode.OK, r.Path.EndsWith("blocklist/", StringComparison.Ordinal) ? SetJson : PolicyJson);

        Assert.True(await sync.SyncAsync("tok", 5, CancellationToken.None, "v1"));

        Assert.Equal("v1", sync.Current!.Web!.ListVersion);
        Assert.Equal("v1", _store.Current!.Version);
        Assert.False(await sync.SyncAsync("tok", 5, CancellationToken.None, "v1"));
    }

    [Fact]
    public async Task Removal_deletes_the_cached_set()
    {
        var (sync, backend, _) = Build();
        backend.Respond = r => FakeBackend.Json(HttpStatusCode.OK, r.Path.EndsWith("blocklist/", StringComparison.Ordinal) ? SetJson : PolicyJson);
        await sync.SyncAsync("tok", 5, CancellationToken.None, "v1");

        sync.ClearForRemoval();

        Assert.Null(_store.Current);
        Assert.False(File.Exists(_store.FilePath));
    }

    // ---- The hosts file at real size ----

    [Fact]
    public void Hosts_lines_group_names_and_every_name_is_there_once()
    {
        var names = Enumerable.Range(0, 20).Select(i => $"site{i}.example").ToList();

        var text = HostsBlock.ApplyToHosts("127.0.0.1 keep.me\r\n", names);

        var lines = text.Split("\r\n").Where(l => l.StartsWith(HostsBlock.Sink, StringComparison.Ordinal)).ToList();
        Assert.Equal(3, lines.Count);
        Assert.All(lines, l => Assert.True(l.Split(' ').Length - 1 <= HostsBlock.HostsPerLine));
        Assert.Equal(names, lines.SelectMany(l => l.Split(' ').Skip(1)));
        Assert.Contains("127.0.0.1 keep.me", text);
    }

    [Fact]
    public void Writing_the_same_list_twice_changes_nothing_and_clearing_restores_the_file()
    {
        const string original = "# comment\r\n127.0.0.1 keep.me\r\n";
        var names = Enumerable.Range(0, 20000).Select(i => $"site{i}.example").ToList();

        var once = HostsBlock.ApplyToHosts(original, names);
        var twice = HostsBlock.ApplyToHosts(once, names);

        Assert.Equal(once, twice);
        Assert.Equal(original, HostsBlock.ApplyToHosts(once, []));
    }

    [Fact]
    public void Atomic_replace_swaps_the_whole_file_and_leaves_no_temp_behind()
    {
        var path = Path.Combine(_dir.Path, "hosts");
        File.WriteAllText(path, "old");

        AtomicFile.Replace(path, "new", new UTF8Encoding(false));

        Assert.Equal("new", File.ReadAllText(path));
        Assert.Equal(["hosts"], Directory.GetFiles(_dir.Path, "hosts*").Select(Path.GetFileName));
    }

    [Fact]
    public void Atomic_replace_creates_a_missing_file()
    {
        var path = Path.Combine(_dir.Path, "hosts");

        AtomicFile.Replace(path, "new", new UTF8Encoding(false));

        Assert.Equal("new", File.ReadAllText(path));
    }

    [Fact]
    public void Atomic_replace_gives_up_on_a_locked_file_and_keeps_it_whole()
    {
        var path = Path.Combine(_dir.Path, "hosts");
        File.WriteAllText(path, "old");
        using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var e = Record.Exception(() => AtomicFile.Replace(path, "new", new UTF8Encoding(false), TimeSpan.FromMilliseconds(1)));
            Assert.True(e is IOException or UnauthorizedAccessException);
        }

        Assert.Equal("old", File.ReadAllText(path));
        Assert.Equal(["hosts"], Directory.GetFiles(_dir.Path, "hosts*").Select(Path.GetFileName));
    }

    [Fact]
    public void A_list_refresh_is_no_news_for_the_member()
    {
        var one = new Blocklist([new BlockRule("a.example", "gambling")]);
        var two = new Blocklist([new BlockRule("a.example", "gambling"), new BlockRule("b.example", "gambling")]);
        var three = new Blocklist([new BlockRule("a.example", "gambling"), new BlockRule("v.example", "video")]);

        Assert.Equal(WebBlockWording.StableId(one), WebBlockWording.StableId(two));
        Assert.NotEqual(WebBlockWording.StableId(one), WebBlockWording.StableId(three));
        Assert.Contains("video streaming sites", WebBlockWording.InForce(three).Body);
    }

    // ---- The website uploads limit ----

    private static readonly DateTimeOffset T = new(2026, 11, 1, 10, 0, 0, TimeSpan.Zero);

    private NetworkUploadCollector Uploads(long limit, out EventQueue events, int minutes = 60)
    {
        var metrics = new MetricsStore(_dir.Path);
        events = new EventQueue(_dir.Path);
        return new NetworkUploadCollector(metrics, events,
            new NetworkFeatureOptions { WarnBytes = limit, WarnWindowMinutes = minutes },
            NullLogger<NetworkUploadCollector>.Instance);
    }

    [Fact]
    public void The_limit_comes_from_the_policy_unless_transfer_is_off()
    {
        var on = new DataTransferPolicy(true, 1, 2, 3, false, false, 500);
        var off = on with { Enabled = false };
        var older = new DataTransferPolicy(true, 1, 2, 3, false);
        var defaults = new NetworkFeatureOptions();

        Assert.Equal(500, NetworkPolicyMap.FromPolicy(defaults, null, on).WarnBytes);
        Assert.Equal(0, NetworkPolicyMap.FromPolicy(defaults, null, off).WarnBytes);
        Assert.Equal(0, NetworkPolicyMap.FromPolicy(defaults, null, older).WarnBytes);
    }

    [Fact]
    public void Sends_to_one_site_add_up_across_its_server_names_and_alert_once()
    {
        var c = Uploads(10_000_000, out var events);
        c.OnDnsAnswer(new DnsAnswer("upload1.drive.example.com", "5.6.7.8"));
        c.OnDnsAnswer(new DnsAnswer("upload2.drive.example.com", "5.6.7.9"));

        c.OnUpload(new UploadSample("chrome", "5.6.7.8", 6_000_000));
        c.Flush(T);
        c.OnUpload(new UploadSample("chrome", "5.6.7.9", 6_000_000));
        c.Flush(T.AddMinutes(5));
        c.OnUpload(new UploadSample("chrome", "5.6.7.9", 6_000_000));
        c.Flush(T.AddMinutes(6));

        var evt = Assert.Single(events.PeekBatch(10));
        Assert.Equal(NetworkEventTypes.UploadOverLimit, evt.Type);
        Assert.Contains("example.com", evt.DataJson);
        Assert.Contains("chrome", evt.DataJson);
    }

    [Fact]
    public void A_slow_trickle_outside_the_window_never_alerts()
    {
        var c = Uploads(10_000_000, out var events);
        c.OnDnsAnswer(new DnsAnswer("example.com", "5.6.7.8"));

        for (var i = 0; i < 8; i++)
        {
            c.OnUpload(new UploadSample("chrome", "5.6.7.8", 2_000_000));
            c.Flush(T.AddHours(i));
        }

        Assert.Empty(events.PeekBatch(10));
    }

    [Fact]
    public void A_site_can_alert_again_once_its_window_has_dropped_back()
    {
        var c = Uploads(10_000_000, out var events);
        c.OnDnsAnswer(new DnsAnswer("example.com", "5.6.7.8"));

        c.OnUpload(new UploadSample("chrome", "5.6.7.8", 11_000_000));
        c.Flush(T);
        c.OnUpload(new UploadSample("chrome", "5.6.7.8", 11_000_000));
        c.Flush(T.AddHours(3));

        Assert.Equal(2, events.PeekBatch(10).Count);
    }

    [Fact]
    public void A_site_with_no_known_name_still_counts_under_its_address()
    {
        var c = Uploads(10_000_000, out var events);

        c.OnUpload(new UploadSample("msedge", "93.184.216.34", 11_000_000));
        c.Flush(T);

        var evt = Assert.Single(events.PeekBatch(10));
        Assert.Contains("93.184.216.34", evt.DataJson);
        Assert.Contains("a site with no name", evt.DataJson);
    }

    [Fact]
    public void An_unnamed_upload_says_which_site_was_open_when_the_tray_knows()
    {
        var inbox = new ForegroundInbox();
        inbox.Post([new ForegroundReport("chrome", "www.drive.example.com", 15, 1)]);
        var metrics = new MetricsStore(_dir.Path);
        var events = new EventQueue(_dir.Path);
        var c = new NetworkUploadCollector(metrics, events, new NetworkFeatureOptions { WarnBytes = 10 },
            NullLogger<NetworkUploadCollector>.Instance, inbox);

        c.OnUpload(new UploadSample("chrome", "93.184.216.34", 11));
        c.Flush(T);

        var evt = Assert.Single(events.PeekBatch(10));
        Assert.Contains("\"open_site\":\"example.com\"", evt.DataJson);
        Assert.Contains("with example.com open", evt.DataJson);
    }

    [Theory]
    [InlineData("192.168.1.20")]
    [InlineData("10.0.0.5")]
    [InlineData("172.20.1.1")]
    [InlineData("127.0.0.1")]
    [InlineData("fe80::1")]
    public void The_office_network_is_not_a_website(string address)
    {
        var c = Uploads(10_000_000, out var events);

        c.OnUpload(new UploadSample("chrome", address, 11_000_000));
        c.Flush(T);

        Assert.Empty(events.PeekBatch(10));
    }

    [Fact]
    public void Apps_other_than_browsers_and_mail_do_not_count()
    {
        var c = Uploads(10_000_000, out var events);
        c.OnDnsAnswer(new DnsAnswer("example.com", "5.6.7.8"));

        c.OnUpload(new UploadSample("OneDrive", "5.6.7.8", 11_000_000));
        c.Flush(T);

        Assert.Empty(events.PeekBatch(10));
    }

    [Fact]
    public void No_limit_no_counting()
    {
        var c = Uploads(0, out var events);
        c.OnUpload(new UploadSample("chrome", "93.184.216.34", 11_000_000));
        c.Flush(T);

        Assert.Empty(events.PeekBatch(10));
    }
}

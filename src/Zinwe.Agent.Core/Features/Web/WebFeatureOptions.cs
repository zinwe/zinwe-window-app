namespace Zinwe.Agent.Core.Features.Web;

/// <summary>
/// Bound from config section <c>Agent:Features:web</c> (defaults live here, so no appsettings edit is required).
/// Covers both halves of the feature: foreground-usage sampling and website blocking.
/// </summary>
public sealed class WebFeatureOptions
{
    /// <summary>Config section this binds from.</summary>
    public const string Section = "Agent:Features:web";

    /// <summary>Master switch for the whole feature (sampler + blocking).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>How often the foreground app is sampled, in seconds. Each sample attributes this many seconds.</summary>
    public int SampleIntervalSeconds { get; set; } = 15;

    /// <summary>
    /// Collect the foreground usage metric at all. When off, nothing about which app is in use is recorded.
    /// </summary>
    public bool CollectForegroundUsage { get; set; } = true;

    /// <summary>
    /// Record internet usage per DOMAIN (host only, e.g. "youtube.com" — never the full URL/query) while a
    /// browser is in the foreground. Read from the browser's address bar via UI Automation; only works when the
    /// agent runs in the user's session. Collected alongside foreground usage.
    /// </summary>
    public bool CollectWebDomains { get; set; } = true;

    /// <summary>
    /// OFF BY DEFAULT (consent-first). When off we record only the process name (e.g. "chrome"); counts and
    /// seconds. When on, the foreground window TITLE is also captured on the sample — potentially sensitive
    /// (document names, page titles), so it stays opt-in. No metric stores the title today; this gates whether
    /// it is captured for the future detail path.
    /// </summary>
    public bool IncludeDetails { get; set; }

    /// <summary>Apply the blocklist on start (dry-run aware) and report blocked attempts.</summary>
    public bool BlockingEnabled { get; set; } = true;

    /// <summary>
    /// Seed the blocklist with the small built-in sample (reserved .test names), for a backend too old to have real
    /// lists. A policy with a <see cref="ListVersion"/> turns it off.
    /// </summary>
    public bool UseBuiltInSampleList { get; set; } = true;

    /// <summary>
    /// With the built-in sample: when non-empty, only these category names are enabled; empty = all of them. With the
    /// backend's lists: exactly the lists the owner switched on, so an empty list means none. Category membership is
    /// what "illegal/blocked" means here — a maintained list, never a content judgement made on the device.
    /// </summary>
    public List<string> Categories { get; set; } = [];

    /// <summary>Extra admin-supplied domains to block, recorded under the "custom" category.</summary>
    public List<string> CustomDomains { get; set; } = [];

    /// <summary>Sites never blocked by a list, subdomains included. Wins over everything else on this PC.</summary>
    public List<string> AllowDomains { get; set; } = [];

    /// <summary>
    /// The set of sites the policy names ("" = none). Null = no policy from a backend with lists: the sites then come
    /// from the built-in sample rather than <see cref="WebBlockSet"/>.
    /// </summary>
    public string? ListVersion { get; set; }
}

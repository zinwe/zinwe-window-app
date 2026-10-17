namespace Zinwe.Agent.Core.Features.Web;

/// <summary>One domain on the blocklist and the category it belongs to.</summary>
/// <param name="Domain">A normalized host, e.g. "malware-sample.test" (no scheme, path, port or leading "www.").</param>
/// <param name="Category">The maintained category it came from, e.g. "malware", "phishing", "custom".</param>
public sealed record BlockRule(string Domain, string Category);

/// <summary>
/// The set of domains to block, grouped by category.
///
/// IMPORTANT (design note, not a content filter): a site is "blocked"/"illegal" purely because its domain is a
/// member of a maintained category list. The agent makes no judgement about a page's content; it only matches
/// hostnames. Real category feeds arrive from the backend later; for the base app we ship a tiny, clearly-labelled
/// sample list using reserved <c>.test</c> domains so a demo never accidentally blocks a real site.
/// </summary>
public sealed class Blocklist
{
    /// <summary>
    /// Built-in placeholder categories. The hostnames use the reserved <c>.test</c> TLD (RFC 2606) on purpose:
    /// they resolve nowhere, so enabling blocking on a real machine has no visible effect until real feeds exist.
    /// </summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> BuiltInSample { get; } =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["malware"] = ["malware-sample.test"],
            ["phishing"] = ["phishing-sample.test"],
            ["adult"] = ["adult-sample.test"],
            ["gambling"] = ["gambling-sample.test"],
        };

    public Blocklist(IEnumerable<BlockRule> rules)
    {
        var byDomain = new Dictionary<string, BlockRule>(StringComparer.OrdinalIgnoreCase);
        foreach (var rule in rules)
        {
            var domain = NormalizeDomain(rule.Domain);
            if (domain is null || byDomain.ContainsKey(domain))
            {
                continue;
            }
            byDomain[domain] = rule with { Domain = domain };
        }

        _byDomain = byDomain;
        Rules = byDomain.Values.OrderBy(r => r.Domain, StringComparer.Ordinal).ToList();
        Categories = Rules.Select(r => r.Category).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(c => c, StringComparer.Ordinal).ToList();

        // Hostnames the hosts-file sink lists: each domain plus its "www." variant (both, deduped, stable order).
        var hosts = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rule in Rules)
        {
            foreach (var host in new[] { rule.Domain, "www." + rule.Domain })
            {
                if (seen.Add(host))
                {
                    hosts.Add(host);
                }
            }
        }
        Hostnames = hosts;
    }

    public IReadOnlyList<BlockRule> Rules { get; }

    /// <summary>Distinct category names present, for the "blocklist applied" event summary.</summary>
    public IReadOnlyList<string> Categories { get; }

    /// <summary>Every hostname to sink in the hosts file (apex + www), deduped.</summary>
    public IReadOnlyList<string> Hostnames { get; }

    public bool IsEmpty => Rules.Count == 0;

    private readonly Dictionary<string, BlockRule> _byDomain;

    /// <summary>Nothing blocked: applying it clears whatever the agent added before.</summary>
    public static Blocklist Empty { get; } = new([]);

    /// <summary>The rule that blocks <paramref name="domain"/> (it or its "www." form), or null.</summary>
    public BlockRule? Find(string? domain) =>
        NormalizeDomain(domain) is { } host && _byDomain.TryGetValue(host, out var rule) ? rule : null;

    /// <summary>Builds the active blocklist from options: enabled built-in categories plus any custom domains.</summary>
    public static Blocklist FromOptions(WebFeatureOptions options)
    {
        var rules = new List<BlockRule>();

        if (options.UseBuiltInSampleList)
        {
            var wanted = options.Categories.Count == 0
                ? null
                : new HashSet<string>(options.Categories, StringComparer.OrdinalIgnoreCase);
            foreach (var (category, domains) in BuiltInSample)
            {
                if (wanted is not null && !wanted.Contains(category))
                {
                    continue;
                }
                rules.AddRange(domains.Select(d => new BlockRule(d, category)));
            }
        }

        rules.AddRange(options.CustomDomains.Select(d => new BlockRule(d, "custom")));
        return new Blocklist(rules);
    }

    /// <summary>
    /// Reduces admin input to a bare host: lowercases, strips a scheme, any path/query, a port and a leading
    /// "www.". Returns null for anything that is not a plausible hostname (empty, whitespace, control chars).
    /// </summary>
    public static string? NormalizeDomain(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var host = value.Trim().ToLowerInvariant();

        var scheme = host.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0)
        {
            host = host[(scheme + 3)..];
        }

        var slash = host.IndexOf('/', StringComparison.Ordinal);
        if (slash >= 0)
        {
            host = host[..slash];
        }

        // Drop any userinfo and port.
        var at = host.LastIndexOf('@');
        if (at >= 0)
        {
            host = host[(at + 1)..];
        }
        var colon = host.IndexOf(':', StringComparison.Ordinal);
        if (colon >= 0)
        {
            host = host[..colon];
        }

        if (host.StartsWith("www.", StringComparison.Ordinal))
        {
            host = host[4..];
        }

        host = host.Trim('.');

        if (host.Length == 0 || host.Length > 253 || host.Any(c => c <= ' ' || c is '/' or '\\' or '*' or '?' or '#'))
        {
            return null;
        }

        // A bare host must have at least one dot (a label.tld); reject single tokens like "localhost" input noise.
        return host.Contains('.', StringComparison.Ordinal) ? host : null;
    }
}

namespace Zinwe.Agent.Core.Features.Web;

/// <summary>
/// Tells the member, on their own widget, that a website didn't open because of their company's rule, so a
/// blocked site never looks like a broken internet connection. The service writes these as widget notices (the
/// tray toasts them); Core and tests use the no-op. Must never throw.
/// </summary>
public interface IWebBlockNotices
{
    /// <summary>A non-empty block list is in force on this PC (posted once per distinct list).</summary>
    void BlocklistInForce(Blocklist list);

    /// <summary>The browser in front is showing a blocked site.</summary>
    void SiteBlocked(string domain, string? category);
}

public sealed class NullWebBlockNotices : IWebBlockNotices
{
    public void BlocklistInForce(Blocklist list)
    {
    }

    public void SiteBlocked(string domain, string? category)
    {
    }
}

/// <summary>The member-facing words for website blocking. Pure, so it's unit-tested.</summary>
public static class WebBlockWording
{
    /// <summary>"gambling" → "gambling sites"; "custom" → "sites your company added".</summary>
    public static string CategoryLabel(string? category) => (category ?? "").Trim().ToLowerInvariant() switch
    {
        "malware" => "harmful sites",
        "phishing" => "fake login (phishing) sites",
        "adult" => "adult sites",
        "gambling" => "gambling sites",
        "social" or "social_media" => "social media",
        "custom" or "" => "sites your company added",
        var other => other.Replace('_', ' ') + " sites",
    };

    public static (string Title, string Body) InForce(Blocklist list)
    {
        var kinds = list.Categories.Select(CategoryLabel).Distinct(StringComparer.Ordinal).ToList();
        var what = kinds.Count switch
        {
            0 => "some websites",
            1 => kinds[0],
            _ => string.Join(", ", kinds.Take(kinds.Count - 1)) + " and " + kinds[^1],
        };
        return ("Your company blocks some websites",
            $"Zinwe blocks {what} on this PC — your company's rule. If one of them doesn't open, that's why: "
            + "your internet is fine.");
    }

    public static (string Title, string Body) Blocked(string domain, string? category) =>
        ($"Zinwe blocked {domain} — your company's rule",
         $"This website is on your company's block list ({CategoryLabel(category)}), so it won't open on this PC. "
         + "Your internet is fine and other sites still work. If you need it for work, message your admin.");

    /// <summary>The same negative id for the same list, so re-applying it never adds a second note.</summary>
    public static long StableId(Blocklist list)
    {
        unchecked
        {
            var hash = 1469598103934665603L; // FNV-1a over the sorted domains
            foreach (var rule in list.Rules)
            {
                foreach (var c in rule.Domain + "|" + rule.Category + ";")
                {
                    hash = (hash ^ c) * 1099511628211L;
                }
            }
            return -(Math.Abs(hash % 1_000_000_000_000L) + 1_000_000_000_000L);
        }
    }
}

/// <summary>At most one "Zinwe blocked {site}" note per site in <see cref="Window"/>. Thread-safe.</summary>
public sealed class BlockedSiteThrottle(TimeProvider? clock = null)
{
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(10);

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly Dictionary<string, DateTimeOffset> _last = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _lock = new();

    public bool ShouldNotify(string domain)
    {
        var now = _clock.GetUtcNow();
        lock (_lock)
        {
            if (_last.TryGetValue(domain, out var at) && now - at < Window && now >= at)
            {
                return false;
            }
            _last[domain] = now;
            return true;
        }
    }
}

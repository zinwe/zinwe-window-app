using System.Collections.Concurrent;

namespace Zinwe.Agent.Core.Features.Web;

/// <summary>
/// What the tray saw in front of the member over a short period: the app (process name), the website when a
/// browser was in front (domain only, never a URL), and for how long. Sent by the tray, which runs in the member's
/// session — the LocalSystem service runs in Session 0 and can't see the desktop, so it can't read these itself.
/// </summary>
/// <param name="App">Process name, e.g. "chrome", "winword" (no ".exe").</param>
/// <param name="Domain">The browser tab's domain, e.g. "youtube.com"; null when no browser was in front.</param>
/// <param name="Seconds">Seconds credited (the sum of the sample intervals).</param>
/// <param name="Samples">How many samples were taken (each one counts as one "visit"/"use" tick).</param>
public sealed record ForegroundReport(string App, string? Domain, int Seconds, int Samples);

/// <summary>
/// Hands the tray's <see cref="ForegroundReport"/>s from the pipe server (singleton) to the web feature (rebuilt
/// with each policy), which records them under the owner's current settings. Bounded and thread-safe; never throws.
/// </summary>
public sealed class ForegroundInbox(TimeProvider? clock = null)
{
    private const int MaxQueued = 500;
    private const int MaxAppLength = 128;
    private const int MaxDomainLength = 253;
    private const int MaxSecondsPerReport = 600;

    private readonly ConcurrentQueue<ForegroundReport> _queue = new();
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private long _lastPostTicks;

    /// <summary>When the tray last reported (null: never in this service run).</summary>
    public DateTimeOffset? LastPostAt
    {
        get
        {
            var ticks = Interlocked.Read(ref _lastPostTicks);
            return ticks == 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero);
        }
    }

    /// <summary>Did the tray report within <paramref name="window"/>? (Then the service doesn't sample on its own.)</summary>
    public bool TrayActive(TimeSpan window) => LastPostAt is { } at && _clock.GetUtcNow() - at <= window;

    /// <summary>Queue the tray's reports, dropping malformed ones. Returns how many were accepted.</summary>
    public int Post(IEnumerable<ForegroundReport>? reports)
    {
        Interlocked.Exchange(ref _lastPostTicks, _clock.GetUtcNow().UtcTicks);
        var accepted = 0;
        foreach (var r in reports ?? [])
        {
            if (r is null || string.IsNullOrWhiteSpace(r.App) || r.App.Length > MaxAppLength
                || r.Seconds <= 0 || r.Seconds > MaxSecondsPerReport || r.Samples <= 0)
            {
                continue;
            }
            var domain = string.IsNullOrWhiteSpace(r.Domain) || r.Domain.Length > MaxDomainLength ? null : r.Domain.Trim().ToLowerInvariant();
            _queue.Enqueue(r with { App = r.App.Trim(), Domain = domain });
            accepted++;
            while (_queue.Count > MaxQueued && _queue.TryDequeue(out _))
            {
                // bounded: the oldest go first if nobody drains (e.g. usage collection is off)
            }
        }
        return accepted;
    }

    /// <summary>Everything queued since the last drain.</summary>
    public IReadOnlyList<ForegroundReport> Drain()
    {
        var items = new List<ForegroundReport>();
        while (_queue.TryDequeue(out var r))
        {
            items.Add(r);
        }
        return items;
    }
}

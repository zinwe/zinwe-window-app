using Microsoft.Extensions.Logging;

namespace Zinwe.Agent.Core.Features.Web;

/// <summary>
/// The "web" feature module: on start it applies the website blocklist (dry-run aware) and begins sampling the
/// foreground app on an interval; it also subscribes to the blocked-attempt source. On stop it cancels the
/// sampler and releases the attempt source. Never throws out of Start/Stop (the host logs and carries on).
/// </summary>
public sealed class WebFeatureModule(
    WebProtection protection,
    ForegroundUsageSampler sampler,
    BrowserDomainSampler domains,
    IBlockedAttemptSource attempts,
    Blocklist blocklist,
    WebFeatureOptions options,
    ILogger<WebFeatureModule> logger,
    IWebBlockNotices? notices = null,
    BlockedSiteThrottle? throttle = null) : IFeatureModule
{
    private readonly IWebBlockNotices _notices = notices ?? new NullWebBlockNotices();
    private readonly BlockedSiteThrottle _throttle = throttle ?? new BlockedSiteThrottle();
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public string Name => "web";

    public Task StartAsync(CancellationToken ct)
    {
        if (!options.Enabled || !options.BlockingEnabled)
        {
            // Blocking is off now (owner switched it off, the member declined monitoring, or the PC was removed):
            // take away any block list an earlier policy left in the hosts file. A no-op when there is none.
            protection.Apply(Blocklist.Empty, report: false);
        }
        if (!options.Enabled)
        {
            logger.LogInformation("Web feature disabled by config");
            return Task.CompletedTask;
        }

        try
        {
            if (options.BlockingEnabled)
            {
                protection.Apply(blocklist, report: true);
                attempts.Attempted += OnAttempted;
                attempts.Start();
                if (!blocklist.IsEmpty)
                {
                    _notices.BlocklistInForce(blocklist);
                }
            }

            if (options.CollectForegroundUsage)
            {
                _cts = new CancellationTokenSource();
                _loop = Task.Run(() => SampleLoopAsync(_cts.Token), CancellationToken.None);
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogError(e, "Web feature failed to start cleanly; continuing with whatever started");
        }
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken ct)
    {
        try
        {
            attempts.Attempted -= OnAttempted;
        }
        catch (Exception e)
        {
            logger.LogError(e, "Error detaching the blocked-attempt source");
        }

        if (_cts is not null)
        {
            await _cts.CancelAsync();
        }

        if (_loop is not null)
        {
            try
            {
                await _loop.WaitAsync(ct);
            }
            catch (Exception e) when (e is OperationCanceledException or TimeoutException)
            {
                // shutting down
            }
        }

        _cts?.Dispose();
        _cts = null;

        try
        {
            attempts.Dispose();
        }
        catch (Exception e)
        {
            logger.LogError(e, "Error disposing the blocked-attempt source");
        }
    }

    private void OnAttempted(object? sender, BlockedAttempt e)
    {
        try
        {
            protection.OnBlockedAttempt(e.Domain, e.Category);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Handling a blocked attempt failed");
        }
    }

    /// <summary>The browser in front is on a blocked site: tell the member it's the company's rule, not the internet.</summary>
    internal void NoticeIfBlocked(string? domain)
    {
        if (!options.BlockingEnabled || domain is null || blocklist.Find(domain) is not { } rule)
        {
            return;
        }
        if (_throttle.ShouldNotify(rule.Domain))
        {
            _notices.SiteBlocked(rule.Domain, rule.Category);
        }
    }

    private async Task SampleLoopAsync(CancellationToken ct)
    {
        var seconds = Math.Max(1, options.SampleIntervalSeconds);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(seconds));
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                // One foreground reading per tick feeds both metrics: app time always, and — when a browser is
                // in front — that tab's domain. The domain read (UI Automation) is skipped for non-browsers.
                var sample = sampler.Collect(seconds);
                if (sample is not null && options.CollectWebDomains && BrowserApps.IsBrowser(sample.ProcessName))
                {
                    NoticeIfBlocked(domains.Collect(seconds));
                }
            }
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
    }
}

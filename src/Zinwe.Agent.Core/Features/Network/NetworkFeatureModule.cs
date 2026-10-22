using Microsoft.Extensions.Logging;

namespace Zinwe.Agent.Core.Features.Network;

/// <summary>
/// The "network" feature: subscribes to the trace source (DNS answers + per-connection send bytes), feeds them to
/// <see cref="NetworkUploadCollector"/> to attribute upload volume per destination domain, and flushes to metrics
/// on a timer. OFF unless enabled (the ETW source needs the LocalSystem service). Never throws out of Start/Stop.
/// </summary>
public sealed class NetworkFeatureModule(
    INetworkTraceSource source,
    NetworkUploadCollector collector,
    NetworkUsageCollector usage,
    NetworkFeatureOptions options,
    ILogger<NetworkFeatureModule> logger) : IFeatureModule
{
    private CancellationTokenSource? _cts;
    private Task? _flushLoop;

    public string Name => "network";

    public Task StartAsync(CancellationToken ct)
    {
        if (!options.Enabled)
        {
            logger.LogInformation("network (data used + upload monitoring) is off: Online protection isn't on for this PC");
            return Task.CompletedTask;
        }
        try
        {
            source.DnsAnswered += OnDns;
            source.BytesSent += OnUpload;
            source.Traffic += OnTraffic;
            source.Start();
            _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _flushLoop = Task.Run(() => FlushLoopAsync(_cts.Token), CancellationToken.None);
            logger.LogInformation("network measuring data used per app and site, and uploads by destination (sizes only)");
        }
        catch (Exception e)
        {
            logger.LogError(e, "network feature failed to start; continuing without upload monitoring");
        }
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken ct)
    {
        try
        {
            source.DnsAnswered -= OnDns;
            source.BytesSent -= OnUpload;
            source.Traffic -= OnTraffic;
        }
        catch (Exception e)
        {
            logger.LogError(e, "Error detaching the network trace source");
        }

        if (_cts is not null)
        {
            await _cts.CancelAsync();
        }
        if (_flushLoop is not null)
        {
            try
            {
                await _flushLoop.WaitAsync(ct);
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
            source.Dispose();
            collector.Flush(); // don't lose the last window
            usage.Flush();
        }
        catch (Exception e)
        {
            logger.LogError(e, "network feature did not stop cleanly");
        }
    }

    private void OnDns(object? sender, DnsAnswer answer)
    {
        collector.OnDnsAnswer(answer);
        usage.OnDnsAnswer(answer);
    }

    private void OnTraffic(object? sender, TrafficSample sample) => usage.OnTraffic(sample);

    private void OnUpload(object? sender, UploadSample sample) => collector.OnUpload(sample);

    private async Task FlushLoopAsync(CancellationToken ct)
    {
        var period = TimeSpan.FromSeconds(Math.Max(1, options.FlushSeconds));
        using var timer = new PeriodicTimer(period);
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                collector.Flush();
                usage.Flush();
            }
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
    }
}

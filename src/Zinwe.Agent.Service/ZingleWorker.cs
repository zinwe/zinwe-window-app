using Zinwe.Agent.Core.Zingles;

namespace Zinwe.Agent.Service;

/// <summary>
/// Keeps the special-day logo (<see cref="ZingleSync"/>) current: a check a few seconds after start-up, then about
/// every hour; while the backend can't be reached, quick retries first (<see cref="ZingleSync.RetryAfter"/>: the
/// network is often not up yet when the service starts with Windows), then every 5 minutes; at once after the PC
/// wakes from sleep (the wall clock jumped past the tick) and when a tray starts (<see cref="ZingleSync.OnStatusAsked"/>).
/// Every tick also gives a member who just signed in their copy of the files. The endpoint is public, so this runs
/// whether or not the PC is connected to a company.
/// </summary>
internal sealed partial class ZingleWorker(ZingleSync zingles, ILogger<ZingleWorker> logger) : BackgroundService
{
    public static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(60);
    private static readonly TimeSpan StartDelay = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(30);
    // A tick that took this much longer than its wall-clock time means the PC slept (or the clock moved).
    private static readonly TimeSpan WakeGap = TimeSpan.FromMinutes(3);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(StartDelay, stoppingToken); // let the host finish starting; well inside 10 s
        var next = DateTimeOffset.MinValue;
        var last = DateTimeOffset.UtcNow;
        var failures = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            var wait = Tick;
            try
            {
                var now = DateTimeOffset.UtcNow;
                if (now - last > Tick + WakeGap || now < last)
                {
                    LogWoke();
                    next = DateTimeOffset.MinValue; // after a sleep: today's Zingle may have changed
                }
                last = now;
                if (now >= next)
                {
                    if (await zingles.CheckAsync(stoppingToken))
                    {
                        failures = 0;
                        next = now + CheckInterval;
                    }
                    else
                    {
                        next = now + ZingleSync.RetryAfter(++failures);
                    }
                }
                else
                {
                    zingles.RefreshWidget();
                }
                var untilNext = next - DateTimeOffset.UtcNow;
                if (untilNext < wait)
                {
                    wait = untilNext < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : untilNext;
                }
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // CheckAsync handles network/file faults itself; anything else must not take the agent down.
                LogFailed(e.Message);
                next = DateTimeOffset.UtcNow + ZingleSync.RetryAfter(++failures);
            }
            if (await zingles.WaitAsync(wait, stoppingToken))
            {
                LogTrayStarted();
                next = DateTimeOffset.MinValue; // a tray just started: ask now
                last = DateTimeOffset.UtcNow;
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "The PC woke up (or the clock moved): checking the Zingle now")]
    private partial void LogWoke();

    [LoggerMessage(Level = LogLevel.Debug, Message = "A tray started: checking the Zingle now")]
    private partial void LogTrayStarted();

    [LoggerMessage(Level = LogLevel.Warning, Message = "Zingle check failed; retrying soon: {Error}")]
    private partial void LogFailed(string error);
}

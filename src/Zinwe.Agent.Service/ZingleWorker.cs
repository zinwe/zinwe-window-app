using Zinwe.Agent.Core.Zingles;

namespace Zinwe.Agent.Service;

/// <summary>
/// Keeps the special-day logo (<see cref="ZingleSync"/>) current: a check at start-up, then about every hour, sooner
/// (every 5 minutes) while the backend can't be reached, and at once after the PC wakes from sleep (the wall clock
/// jumped past the one-minute tick). Every tick also gives a member who just signed in their copy of the files. The
/// endpoint is public, so this runs whether or not the PC is connected to a company.
/// </summary>
internal sealed partial class ZingleWorker(ZingleSync zingles, ILogger<ZingleWorker> logger) : BackgroundService
{
    public static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(60);
    public static readonly TimeSpan RetryInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan Tick = TimeSpan.FromMinutes(1);
    // A tick that took this much longer than a minute of wall clock means the PC slept (or the clock moved).
    private static readonly TimeSpan WakeGap = TimeSpan.FromMinutes(3);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var next = DateTimeOffset.MinValue;
        var last = DateTimeOffset.UtcNow;
        while (!stoppingToken.IsCancellationRequested)
        {
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
                    next = now + (await zingles.CheckAsync(stoppingToken) ? CheckInterval : RetryInterval);
                }
                else
                {
                    zingles.RefreshWidget();
                }
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // CheckAsync handles network/file faults itself; anything else must not take the agent down.
                LogFailed(e.Message);
                next = DateTimeOffset.UtcNow + RetryInterval;
            }
            await Task.Delay(Tick, stoppingToken);
        }
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "The PC woke up (or the clock moved): checking the Zingle now")]
    private partial void LogWoke();

    [LoggerMessage(Level = LogLevel.Warning, Message = "Zingle check failed; retrying in 5 minutes: {Error}")]
    private partial void LogFailed(string error);
}

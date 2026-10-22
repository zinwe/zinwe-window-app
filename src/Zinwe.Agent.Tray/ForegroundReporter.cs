using System.Runtime.InteropServices;
using System.Windows.Automation;
using Zinwe.Agent.Core.Features.Web;
using Zinwe.Agent.Core.Ipc;

namespace Zinwe.Agent.Tray;

/// <summary>
/// Reads what is in front of the member — the app, and the website's domain when a browser is in front — and
/// sends it to the service, which records it as app time and website usage under the owner's settings. This has to
/// run here, in the member's session: the service runs as LocalSystem in Session 0 and can't see the desktop.
///
/// Every 15 seconds one reading; a batch goes to the service each minute (kept and resent if the service is
/// unreachable). Nothing is recorded while the PC is idle (no keyboard or mouse for 5 minutes) or locked, so app
/// time means time actually in use. Only process names and domains leave this class — never window titles or URLs.
/// </summary>
internal sealed class ForegroundReporter : IDisposable
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(15);
    private const int TicksPerBatch = 4;
    private static readonly TimeSpan IdleAfter = TimeSpan.FromMinutes(5);
    private const int MaxPending = 200;

    // Windows' own lock/sign-in screens are not "use" of the PC.
    private static readonly HashSet<string> NotUse = new(StringComparer.OrdinalIgnoreCase)
    {
        "LockApp", "LogonUI", "ScreenClippingHost", "Idle",
    };

    private static readonly Condition EditCondition =
        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit);

    private readonly Win32ForegroundWindowSource _windows = new(new WebFeatureOptions { IncludeDetails = false });
    private readonly Dictionary<(string App, string? Domain), (int Seconds, int Samples)> _pending = new();
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;

    public void Start() => _loop = Task.Run(() => RunAsync(_cts.Token));

    private async Task RunAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(Interval);
        var ticks = 0;
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                SampleOnce();
                if (++ticks % TicksPerBatch == 0)
                {
                    await FlushAsync(ct);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // closing
        }
    }

    private void SampleOnce()
    {
        try
        {
            if (IdleFor() >= IdleAfter)
            {
                return;
            }
            var sample = _windows.Sample();
            if (sample is null || string.IsNullOrWhiteSpace(sample.ProcessName) || NotUse.Contains(sample.ProcessName))
            {
                return;
            }
            var domain = BrowserApps.IsBrowser(sample.ProcessName) ? OmniboxDomain.Parse(ReadAddressBar()) : null;
            var key = (sample.ProcessName, domain);
            var (seconds, samples) = _pending.GetValueOrDefault(key);
            _pending[key] = (seconds + (int)Interval.TotalSeconds, samples + 1);
        }
        catch (Exception e) when (e is InvalidOperationException or ArgumentException or COMException or UnauthorizedAccessException)
        {
            // one missed reading; try again next tick
        }
    }

    private async Task FlushAsync(CancellationToken ct)
    {
        if (_pending.Count == 0)
        {
            return;
        }
        var batch = _pending.Select(p => new ForegroundReport(p.Key.App, p.Key.Domain, p.Value.Seconds, p.Value.Samples)).ToList();
        var ack = await AgentPipeClient.ReportUsageAsync(batch, TimeSpan.FromSeconds(3), ct);
        if (ack?.Accepted == true)
        {
            _pending.Clear();
        }
        else if (_pending.Count > MaxPending)
        {
            _pending.Clear(); // the service has been unreachable for a long time: don't grow without limit
        }
    }

    /// <summary>The foreground browser's address-bar text via UI Automation (read-only); null when it can't be read.</summary>
    private static string? ReadAddressBar()
    {
        try
        {
            var hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero)
            {
                return null;
            }
            var edit = AutomationElement.FromHandle(hwnd)?.FindFirst(TreeScope.Descendants, EditCondition);
            if (edit is not null && edit.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern) && pattern is ValuePattern value)
            {
                var text = value.Current.Value;
                return string.IsNullOrWhiteSpace(text) ? null : text;
            }
            return null;
        }
        catch (Exception e) when (e is ElementNotAvailableException or COMException or InvalidOperationException
                                    or TimeoutException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>How long since the member last used the keyboard or mouse.</summary>
    private static TimeSpan IdleFor()
    {
        var info = new LastInputInfo { Size = (uint)Marshal.SizeOf<LastInputInfo>() };
        if (!GetLastInputInfo(ref info))
        {
            return TimeSpan.Zero;
        }
        var idleMs = unchecked((uint)Environment.TickCount - info.Time);
        return TimeSpan.FromMilliseconds(idleMs);
    }

    public void Dispose()
    {
        _cts.Cancel();
        try
        {
            _loop?.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
            // closing
        }
        _cts.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo
    {
        public uint Size;
        public uint Time;
    }

#pragma warning disable SYSLIB1054
    [DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LastInputInfo info);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
#pragma warning restore SYSLIB1054
}

namespace Zinwe.Agent.Core.Work;

/// <summary>Where the member's consent stands on this PC.</summary>
public enum ConsentState
{
    /// <summary>Not answered yet (or the notice changed since they agreed): nothing is recorded; the widget asks.</summary>
    Pending,

    /// <summary>Agreed to the current notice: recording may run.</summary>
    Given,

    /// <summary>"I don't agree" / withdrawn: nothing is recorded; work hours and chat still work.</summary>
    Declined,
}

/// <summary>
/// "Has the member agreed to monitoring on this PC?" Until they press "I agree" on the widget's consent
/// screen (for the notice in force — see <see cref="ConsentNotice"/>), the agent records nothing about
/// their activity: <see cref="Events.EventQueue"/> and <see cref="Metrics.MetricsStore"/> drop new rows.
/// A member who declines is never recorded; the backend also sends that PC a policy with monitoring off.
/// The answer is cached briefly so the hot recording paths don't read the file every time. Thread-safe.
/// </summary>
public sealed class ConsentGate
{
    /// <summary>How long an answer is reused before the consent file is read again.</summary>
    public static readonly TimeSpan RecheckInterval = TimeSpan.FromSeconds(5);

    private readonly ConsentReader _reader;
    private readonly TimeProvider _clock;
    private readonly Func<string> _noticeVersion;
    private readonly Lock _lock = new();
    private ConsentRecord? _record;
    private DateTimeOffset _readAt = DateTimeOffset.MinValue;

    /// <param name="noticeVersion">
    /// The notice version in force (from the policy: <see cref="ConsentNotice.VersionFor"/>). Default: the full
    /// notice, which only an agreement to the full notice covers.
    /// </param>
    public ConsentGate(ConsentReader reader, TimeProvider? clock = null, Func<string>? noticeVersion = null)
    {
        _reader = reader;
        _clock = clock ?? TimeProvider.System;
        _noticeVersion = noticeVersion ?? (() => ConsentNotice.FullVersion);
    }

    /// <summary>The notice version in force right now.</summary>
    public string NoticeVersion => _noticeVersion();

    /// <summary>The member's recorded answer (any version), or null.</summary>
    public ConsentRecord? Current()
    {
        var now = _clock.GetUtcNow();
        lock (_lock)
        {
            if (now - _readAt >= RecheckInterval || now < _readAt)
            {
                _record = _reader.Read();
                _readAt = now;
            }
            return _record;
        }
    }

    /// <summary>True when the member agreed to a notice that covers the one in force: recording may run.</summary>
    public bool IsGranted() => State() == ConsentState.Given;

    public ConsentState State() => ConsentNotice.StateOf(Current(), NoticeVersion);

    /// <summary>
    /// What the heartbeat reports: "given" with the version agreed to, "declined" with the version declined, or
    /// null while the member hasn't answered the notice in force (the backend keeps it as pending).
    /// </summary>
    public ConsentReport? ForHeartbeat()
    {
        var record = Current();
        return State() switch
        {
            ConsentState.Given => new ConsentReport("given", record!.Version, record.AcceptedAt),
            ConsentState.Declined => new ConsentReport("declined", record!.Version, record.AcceptedAt),
            _ => null,
        };
    }

    /// <summary>Changes whenever the answer or the notice changes (the heartbeat goes out early then).</summary>
    public string Fingerprint() => $"{State()}|{Current()?.Version}|{NoticeVersion}";

    /// <summary>Forget the cached answer (the next check reads the file).</summary>
    public void Refresh()
    {
        lock (_lock)
        {
            _readAt = DateTimeOffset.MinValue;
        }
    }
}

/// <summary>The member's answer as the heartbeat carries it.</summary>
/// <param name="State">"given" | "declined".</param>
/// <param name="Version">The notice version answered.</param>
/// <param name="At">When.</param>
public sealed record ConsentReport(string State, string Version, DateTimeOffset At)
{
    public bool IsGiven => State == "given";
}

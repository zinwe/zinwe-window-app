using Zinwe.Agent.Core.Ipc;
using Zinwe.Agent.Tray.Resources;
using Zinwe.Agent.Tray.Widget;

namespace Zinwe.Agent.Tray;

public enum StatusTone { Ok, Warn, Bad }

/// <summary>Plain-language wording for each agent state -- employees should never see raw state names.</summary>
public static class StatusText
{
    /// <summary>The tray tooltip / toast: a headline and a sentence on what it means or what to do.</summary>
    public static (string Headline, string Detail, StatusTone Tone) Describe(AgentStatus? status) => status?.State switch
    {
        null => (Strings.StatusNotRunning, Strings.StatusNotRunningDetail, StatusTone.Bad),
        AgentState.Connected => (Line(status), Strings.StatusConnectedDetail, StatusTone.Ok),
        AgentState.Offline => (Strings.StatusOfflineShort, Strings.StatusOfflineDetail, StatusTone.Warn),
        AgentState.Enrolling => (Strings.StatusConnecting, Strings.StatusConnectingDetail, StatusTone.Warn),
        AgentState.NotEnrolled => (Strings.StatusNotConnectedYet, status.Message ?? Strings.StatusNotConnectedYetDetail, StatusTone.Warn),
        AgentState.Rejected when status.Removed => (Line(status),
            Strings.Format(Strings.StatusRemovedDetail, ConsentText.Employer(status.CompanyName, sentenceStart: true)), StatusTone.Warn),
        AgentState.Rejected => (Strings.StatusNotConnected, status.Message ?? Strings.ConnectRefusedFallback, StatusTone.Bad),
        AgentState.UpdateRequired => (Strings.StatusUpdateShort, Strings.StatusUpdateDetail, StatusTone.Warn),
        _ => (Strings.StatusNotConnected, "", StatusTone.Warn),
    };

    /// <summary>
    /// The always-visible line under the widget's logo: who this PC is connected to, or what is wrong, so the
    /// member knows their Log in reached their employer (not only in a hover tooltip).
    /// </summary>
    public static string Line(AgentStatus? status) => status?.State switch
    {
        null => Strings.StatusNotRunning,
        AgentState.Connected => string.IsNullOrWhiteSpace(status.CompanyName)
            ? Strings.StatusConnected
            : Strings.Format(Strings.StatusConnectedTo, status.CompanyName.Trim()),
        AgentState.Offline => Strings.StatusOffline,
        AgentState.Enrolling => Strings.StatusConnecting,
        AgentState.NotEnrolled => Strings.StatusNotConnectedYet,
        AgentState.Rejected when status.Removed => string.IsNullOrWhiteSpace(status.CompanyName)
            ? Strings.StatusRemoved
            : Strings.Format(Strings.StatusRemovedFrom, status.CompanyName.Trim()),
        AgentState.Rejected => Strings.StatusNotConnected,
        AgentState.UpdateRequired => Strings.StatusUpdate,
        _ => Strings.StatusNotConnected,
    };

    /// <summary>The colour of the status line's dot.</summary>
    public static StatusTone Tone(AgentStatus? status) => status?.State switch
    {
        AgentState.Connected => StatusTone.Ok,
        null => StatusTone.Bad,
        AgentState.Rejected when !status.Removed => StatusTone.Bad,
        _ => StatusTone.Warn,
    };

    /// <summary>One or two words (support / logs).</summary>
    public static string Short(AgentStatus? status) => status?.State switch
    {
        null => "Not running",
        AgentState.Connected => "Protected",
        AgentState.Offline => "Offline",
        AgentState.Enrolling => "Connecting",
        AgentState.NotEnrolled => "Not set up",
        AgentState.Rejected => status.Removed ? "Removed" : "Not connected",
        AgentState.UpdateRequired => "Update needed",
        _ => "Unknown",
    };

    public static string LastSeen(DateTimeOffset? at)
    {
        if (at is null)
        {
            return "Never";
        }
        var ago = DateTimeOffset.UtcNow - at.Value;
        return ago.TotalSeconds < 60 ? "Just now"
            : ago.TotalMinutes < 60 ? $"{(int)ago.TotalMinutes} min ago"
            : at.Value.ToLocalTime().ToString("d MMM, h:mm tt");
    }
}

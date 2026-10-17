using Zinwe.Agent.Core.Ipc;
using Zinwe.Agent.Core.Work;

namespace Zinwe.Agent.Tray.Widget;

/// <summary>The widget's main view.</summary>
public enum WidgetScreen
{
    /// <summary>Nothing but the "Zinwe isn't running" card (the PC was never set up for this person).</summary>
    None,

    /// <summary>"Connect this PC" (email + Member ID), with the "removed" explanation above it when that's why.</summary>
    Connect,

    /// <summary>The consent notice with "I agree" / "I don't agree".</summary>
    Consent,

    /// <summary>The day (log in / break / log out), notices and the chat.</summary>
    Main,
}

/// <summary>What the widget shows for a status and the member's consent. Pure, so it's unit-tested.</summary>
public sealed record WidgetLayout(WidgetScreen Screen, bool NotRunning, bool Removed, bool DeclinedBanner)
{
    public static WidgetLayout From(AgentStatus? status, ConsentState consent)
    {
        if (status is null)
        {
            // The service is down. Someone who answered the notice before keeps their (local) work clock.
            return new WidgetLayout(consent == ConsentState.Pending ? WidgetScreen.None : WidgetScreen.Main,
                NotRunning: true, Removed: false, DeclinedBanner: consent == ConsentState.Declined);
        }
        return status.State switch
        {
            AgentState.NotEnrolled or AgentState.Enrolling or AgentState.Rejected =>
                new WidgetLayout(WidgetScreen.Connect, false, status.Removed, false),
            _ when consent == ConsentState.Pending => new WidgetLayout(WidgetScreen.Consent, false, false, false),
            _ => new WidgetLayout(WidgetScreen.Main, false, false, consent == ConsentState.Declined),
        };
    }

    /// <summary>True for an enrolled PC (connected, offline or update-required).</summary>
    public static bool IsEnrolled(AgentStatus? status) =>
        status?.State is AgentState.Connected or AgentState.Offline or AgentState.UpdateRequired;
}

/// <summary>Why the tray opens the widget by itself (once per sign-in each), so nobody hunts for a hidden icon.</summary>
public enum AutoOpenReason
{
    None,

    /// <summary>Not connected yet (fresh install, or the Connect card was closed).</summary>
    Connect,

    /// <summary>Connected, but the member hasn't answered the notice (closed it, or a silent install).</summary>
    Consent,

    /// <summary>A work day that hasn't been started yet ("Good morning — Log in").</summary>
    StartDay,
}

public static class AutoOpen
{
    public static AutoOpenReason Decide(AgentStatus? status, ConsentState consent, WorkState today, bool workHoursOn)
    {
        if (status?.State == AgentState.NotEnrolled)
        {
            return AutoOpenReason.Connect;
        }
        if (!WidgetLayout.IsEnrolled(status))
        {
            return AutoOpenReason.None; // removed / refused / not running: no pop-up (a toast says so on the change)
        }
        if (consent == ConsentState.Pending)
        {
            return AutoOpenReason.Consent;
        }
        return workHoursOn && today == WorkState.NotStarted ? AutoOpenReason.StartDay : AutoOpenReason.None;
    }
}

/// <summary>Keeping the widget on the screen it's on, whatever its size (DIPs). Pure, so it's unit-tested.</summary>
public static class WindowFit
{
    public const double Margin = 8;

    /// <summary>The tallest the widget may be on a work area <paramref name="areaHeight"/> tall.</summary>
    public static double MaxHeight(double areaHeight) => Math.Max(240, areaHeight - 2 * Margin);

    /// <summary>The window's new top so all of it is visible (moved up when it grew past the bottom).</summary>
    public static double Top(double top, double height, double areaTop, double areaBottom)
    {
        if (top + height > areaBottom - Margin)
        {
            top = areaBottom - Margin - height;
        }
        return Math.Max(areaTop + Margin, top);
    }

    /// <summary>Centred vertically in the work area (used when the view changes, e.g. Connect → consent).</summary>
    public static double CenteredTop(double height, double areaTop, double areaBottom) =>
        Top(areaTop + Math.Max(0, (areaBottom - areaTop - height) / 2), height, areaTop, areaBottom);
}

using Zinwe.Agent.Core.Ipc;
using Zinwe.Agent.Core.Work;
using Zinwe.Agent.Tray.Resources;

namespace Zinwe.Agent.Tray.Widget;

/// <summary>
/// The widget's ⋯ menu: which version this PC runs (and whether an update waits), Send feedback, what Zinwe
/// records on this PC, the policies, and how to hide the widget. Pure, so it's unit-tested.
/// </summary>
/// <param name="Version">"Zinwe 1.1.2".</param>
/// <param name="VersionDetail">"Up to date" / "Version 1.2.0 is ready to install"; null when the service isn't
/// answering (then we can't say).</param>
/// <param name="UpdateWaiting">A newer release is offered: the version line takes the member to the update card.</param>
/// <param name="FeedbackEnabled">Feedback lives on the main view (connected PCs).</param>
/// <param name="RecordsEnabled">The notice can be re-read once the PC is connected and the member answered it.</param>
public sealed record MoreMenu(string Version, string? VersionDetail, bool UpdateWaiting, bool FeedbackEnabled, bool RecordsEnabled)
{
    public static MoreMenu From(AgentStatus? status, string trayVersion, WidgetScreen screen, ConsentState consent)
    {
        var version = Strings.Format(Strings.MenuVersion, status?.AgentVersion ?? trayVersion);
        var update = status?.UpdateVersion;
        var detail = status is null ? null
            : update is not null ? Strings.Format(Strings.MenuUpdateReady, update)
            : Strings.MenuUpToDate;
        return new MoreMenu(
            version,
            detail,
            UpdateWaiting: update is not null,
            FeedbackEnabled: screen == WidgetScreen.Main,
            RecordsEnabled: WidgetLayout.IsEnrolled(status) && consent != ConsentState.Pending);
    }
}

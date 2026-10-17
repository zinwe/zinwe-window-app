using Zinwe.Agent.Core.Ipc;
using Zinwe.Agent.Tray.Resources;

namespace Zinwe.Agent.Tray.Widget;

/// <summary>
/// What the widget's update card shows, from the service's status. Pure, so it's unit-tested.
/// <list type="bullet">
/// <item>Required release: an orange card at the top. The work day is NOT paused: Log in, breaks and Log out keep
///       working and the times are saved; the card just asks for the one-click update.</item>
/// <item>Optional release: a quiet card.</item>
/// <item>The button runs the one-click update and then reports its progress. When Zinwe hasn't published an
///       installer yet, the card says there's nothing to do; a download page is offered only when there is one.</item>
/// </list>
/// </summary>
public sealed record UpdateView(
    bool Show,
    bool Required,
    string Title,
    string Body,
    string? Notes,
    string Button,
    bool ButtonVisible,
    bool ButtonEnabled,
    bool Busy,
    string? Progress,
    bool ProgressIsError,
    bool ShowManual)
{
    public static readonly UpdateView Hidden = new(false, false, "", "", null, "", false, false, false, null, false, false);

    public static UpdateView From(AgentStatus? status)
    {
        if (status is null)
        {
            return Hidden;
        }
        var required = status.State == AgentState.UpdateRequired;
        if (!required && status.UpdateVersion is null)
        {
            return Hidden;
        }

        var version = string.IsNullOrWhiteSpace(status.UpdateVersion)
            ? Strings.UpdateNewVersion
            : Strings.Format(Strings.UpdateVersion, status.UpdateVersion);
        var title = required ? Strings.UpdateRequiredTitle : Strings.UpdateOptionalTitle;
        var installable = status.UpdateInstallable;
        var body = !installable && required
            ? Strings.UpdatePreparing
            : Strings.Format(required ? Strings.UpdateRequiredBody : Strings.UpdateOptionalBody, version);
        var notes = string.IsNullOrWhiteSpace(status.UpdateNotes) ? null : status.UpdateNotes.Trim();
        var hasPage = !string.IsNullOrWhiteSpace(status.DownloadUrl);

        return status.UpdateStage switch
        {
            UpdateStages.Downloading => new(true, required, title, body, notes, Strings.UpdateDownloading, true, false, true,
                status.UpdateMessage ?? Strings.UpdateDownloadingDetail, false, false),
            UpdateStages.Installing => new(true, required, title, body, notes, Strings.UpdateInstalling, true, false, true,
                status.UpdateMessage ?? Strings.UpdateInstallingDetail, false, false),
            UpdateStages.Ready => new(true, required, title, body, notes, Strings.UpdateDownloaded, true, false, false,
                status.UpdateMessage, false, false),
            UpdateStages.Failed => new(true, required, title, body, notes, Strings.UpdateTryAgain, installable, installable, false,
                status.UpdateMessage ?? Strings.UpdateFailed, true, hasPage),
            _ => new(true, required, title, body, notes, Strings.UpdateNow, installable, installable, false,
                null, false, !installable && hasPage),
        };
    }
}

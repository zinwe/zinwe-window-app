using System.IO;

namespace Zinwe.Agent.Tray.Widget;

/// <summary>
/// Per-user local file locations for the widget. Everything lives under
/// <c>%LOCALAPPDATA%\Zinwe</c> so each signed-in user keeps their own timing and notifications,
/// and nothing here is machine-wide or touched by the LocalSystem service directly.
/// </summary>
public static class WidgetPaths
{
    /// <summary>%LOCALAPPDATA%\Zinwe</summary>
    public static string Directory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        Zinwe.Agent.Core.Messages.LocalWidgetFolder.WidgetFolderName);

    /// <summary>Current-day work-timing snapshot (JSON object).</summary>
    public static string WorkTiming => Path.Combine(Directory, "work-timing.json");

    /// <summary>Append-only human-readable log of start/break/resume/end events (one JSON object per line).</summary>
    public static string WorkTimingLog => Path.Combine(Directory, "work-timing-log.jsonl");

    /// <summary>
    /// Admin -> employee notifications. THIS IS THE SEAM the service writes to in a later milestone
    /// (see <see cref="NotificationStore"/> for the schema). For the base app the widget reads it and,
    /// in DEBUG, can self-seed it.
    /// </summary>
    public static string Notifications => Path.Combine(Directory, "notifications.json");

    /// <summary>The member's consent to monitoring (version + when), written by the consent screen.</summary>
    public static string Consent => Path.Combine(Directory, "consent.json");

    public static void EnsureDirectory()
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
        }
        catch
        {
            // Best effort: a missing dir surfaces later as an empty read, which the UI renders cleanly.
        }
    }
}

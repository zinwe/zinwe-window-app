using System.Runtime.InteropServices;
using Zinwe.Agent.Core;
using Zinwe.Agent.Core.Backend;

namespace Zinwe.Agent.Tray.Widget;

/// <summary>
/// F-13 · "Report an issue" from the Zinwe window: what goes to the Zinwe team (not the member's company). Pure, so it's
/// unit-tested: the member's words, where they were in the window, the agent's version and Windows, and, when they ticked
/// "Attach a screenshot of this window", the window as a PNG.
/// </summary>
public static class IssueReport
{
    /// <summary>The window's screen in plain words: "Tasks", "Me › Settings".</summary>
    public static string Screen(Tab tab, Push push)
    {
        var name = tab.ToString();
        return push switch
        {
            Push.None => name,
            Push.Time => name + " › My time",
            Push.Privacy => name + " › Policies & terms",
            _ => name + " › " + push,
        };
    }

    /// <summary>
    /// The request the agent posts. The description is trimmed and capped at the backend's limit; a screenshot over
    /// <see cref="MemberIssueRequest.MaxScreenshotBytes"/> is left out rather than refused with the report.
    /// </summary>
    public static MemberIssueRequest Build(string description, string screen, byte[]? png, string? appVersion = null, string? os = null)
    {
        var text = description.Trim();
        if (text.Length > MemberIssueRequest.MaxDescriptionLength)
        {
            text = text[..MemberIssueRequest.MaxDescriptionLength];
        }
        var shot = png is { Length: > 0 } bytes && bytes.Length <= MemberIssueRequest.MaxScreenshotBytes ? Convert.ToBase64String(bytes) : null;
        return new MemberIssueRequest(text, MemberIssueRequest.WindowPage,
            new MemberIssueContext(appVersion ?? AgentInfo.Version, os ?? RuntimeInformation.OSDescription, screen), shot);
    }
}

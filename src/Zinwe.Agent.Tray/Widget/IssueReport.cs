using System.Runtime.InteropServices;
using Zinwe.Agent.Core;
using Zinwe.Agent.Core.Backend;

namespace Zinwe.Agent.Tray.Widget;

/// <summary>
/// F-13 · "Report an issue" from the Zinwe window: what goes to the Zinwe team (not the member's company). Pure, so it's
/// unit-tested: the member's words, where they were in the window, the agent's version and Windows, and the screenshot
/// they kept: the window as a PNG (added for them), or a PNG or JPG they picked.
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

    /// <summary>The scales a screenshot is tried at, largest first, until its PNG fits the backend's 5 MB.</summary>
    public static IReadOnlyList<double> ShotScales { get; } = [1, 0.75, 0.5, 0.35, 0.25];

    /// <summary>
    /// The window's PNG at the largest of <see cref="ShotScales"/> that fits <see cref="MemberIssueRequest.MaxScreenshotBytes"/>
    /// (<paramref name="encode"/> draws it at a scale); null when none fits or it can't be drawn.
    /// </summary>
    public static byte[]? Fit(Func<double, byte[]?> encode)
    {
        foreach (var scale in ShotScales)
        {
            if (encode(scale) is { Length: > 0 } png && png.Length <= MemberIssueRequest.MaxScreenshotBytes)
            {
                return png;
            }
        }
        return null;
    }

    /// <summary>A picked screenshot may be at most this big ("Windows app next": 2 MB; the backend takes up to 5).</summary>
    public const int MaxPickedBytes = 2 * 1024 * 1024;

    /// <summary>The OpenFileDialog filter for a screenshot: the two kinds the backend takes.</summary>
    public const string PickFilter = "Screenshots (PNG or JPG)|*.png;*.jpg;*.jpeg";

    /// <summary>
    /// A screenshot the member picked from the PC: null when it can go (a PNG or JPG by its first bytes, at most
    /// <see cref="MaxPickedBytes"/>), else what to tell them. The backend refuses a report whose picture is neither, so
    /// the window checks first.
    /// </summary>
    public static string? CheckPicked(byte[] bytes, string name)
    {
        if (bytes.Length > MaxPickedBytes)
        {
            return TooBig(name);
        }
        var png = bytes.Length >= 8 && bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
        var jpg = bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF;
        return png || jpg ? null : "That file isn’t a PNG or JPG image. Try another one.";
    }

    /// <summary>What a picked screenshot over <see cref="MaxPickedBytes"/> says.</summary>
    public static string TooBig(string name) => $"“{name}” is over 2 MB. Pick a smaller image.";

    /// <summary>"340 KB", "1.4 MB": a picture's size on its thumbnail.</summary>
    public static string Size(long bytes) =>
        bytes > 1024 * 1024 ? (bytes / 1048576.0).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " MB"
        : Math.Max(1, (int)Math.Round(bytes / 1024.0)) + " KB";

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

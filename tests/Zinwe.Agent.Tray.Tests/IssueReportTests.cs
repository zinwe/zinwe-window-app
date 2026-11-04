using Zinwe.Agent.Core.Backend;
using Zinwe.Agent.Tray.Widget;

namespace Zinwe.Agent.Tray.Tests;

/// <summary>F-13 · "Report an issue" from the Zinwe window: what the member's report carries to the Zinwe team.</summary>
public sealed class IssueReportTests
{
    [Fact]
    public void A_report_says_where_in_the_window_it_came_from()
    {
        var r = IssueReport.Build("  The tick went back  ", IssueReport.Screen(Tab.Tasks, Push.None), null, "1.3.8", "Windows 11");

        Assert.Equal("The tick went back", r.Description);
        Assert.Equal("Zinwe window", r.Page);
        Assert.Equal(new MemberIssueContext("1.3.8", "Windows 11", "Tasks"), r.Context);
        Assert.Null(r.ScreenshotB64);
        Assert.Equal("Me › Settings", IssueReport.Screen(Tab.Me, Push.Settings));
        Assert.Equal("Me › Policies & terms", IssueReport.Screen(Tab.Me, Push.Privacy));
    }

    [Fact]
    public void The_screenshot_goes_as_base64_and_one_over_the_limit_is_left_out()
    {
        byte[] png = [0x89, 0x50, 0x4E, 0x47];
        Assert.Equal(Convert.ToBase64String(png), IssueReport.Build("x", "Tasks", png).ScreenshotB64);

        var huge = new byte[MemberIssueRequest.MaxScreenshotBytes + 1];
        Assert.Null(IssueReport.Build("x", "Tasks", huge).ScreenshotB64);
    }

    [Fact]
    public void A_long_description_is_cut_to_the_backends_limit()
    {
        Assert.Equal(MemberIssueRequest.MaxDescriptionLength, IssueReport.Build(new string('a', 5000), "Chat", null).Description.Length);
    }
}

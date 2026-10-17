using Zinwe.Agent.Core.Work;
using Zinwe.Agent.Tray.Resources;

namespace Zinwe.Agent.Tray.Widget;

/// <summary>The consent notice as the widget shows it. Pure, so it's unit-tested.</summary>
public sealed record ConsentNoticeView(
    string Title,
    string Intro,
    string IntroReview,
    string SeesHeading,
    IReadOnlyList<string> Sees,
    string SeesMore,
    string NeverHeading,
    IReadOnlyList<string> Never,
    string DeclineHeading,
    string DeclineBody,
    string SmallPrint);

/// <summary>
/// Builds the notice from what is actually switched on (<see cref="MonitoringScope"/>) and who the employer is:
/// the company name from the backend when it sends one, else "your employer". Also names the owner ("message Ravi").
/// </summary>
public static class ConsentText
{
    /// <summary>"Kulkarni &amp; Associates", or "your employer" (mid-sentence) / "Your employer" (sentence start).</summary>
    public static string Employer(string? company, bool sentenceStart = false) =>
        string.IsNullOrWhiteSpace(company)
            ? sentenceStart ? Strings.YourEmployerStart : Strings.YourEmployer
            : company.Trim();

    /// <summary>"Ravi Kulkarni", or "your admin" / "Your admin".</summary>
    public static string Owner(string? owner, bool sentenceStart = false) =>
        string.IsNullOrWhiteSpace(owner)
            ? sentenceStart ? Strings.YourAdminStart : Strings.YourAdmin
            : owner.Trim();

    /// <param name="chatOn">The company's plan includes the chat (declining keeps it).</param>
    public static ConsentNoticeView Build(MonitoringScope scope, string? company, string? owner, bool chatOn = true)
    {
        var employer = Employer(company);
        var employerStart = Employer(company, sentenceStart: true);

        var sees = new List<string>();
        if (scope.AppsAndWebsites)
        {
            sees.Add(Strings.ConsentSeesApps);
            sees.Add(Strings.ConsentSeesWebsites);
        }
        if (scope.Transfers)
        {
            sees.Add(Strings.ConsentSeesTransfers);
        }
        if (scope.TransferDetails)
        {
            sees.Add(Strings.Format(Strings.ConsentSeesDetails, employer));
        }
        if (scope.RemoteAndInstalls)
        {
            sees.Add(Strings.ConsentSeesRemote);
        }
        if (scope.WorkHours)
        {
            sees.Add(Strings.ConsentSeesWork);
        }
        if (sees.Count == 0)
        {
            sees.Add(Strings.ConsentSeesNothing);
        }

        var decline = Strings.Format(Strings.ConsentDeclineBody, employerStart);
        if (scope.WorkHours)
        {
            decline += " " + Strings.Format(Strings.ConsentDeclineWork, Owner(owner));
        }
        else if (chatOn)
        {
            decline += " " + Strings.Format(Strings.ConsentDeclineChat, Owner(owner));
        }

        return new ConsentNoticeView(
            Strings.ConsentTitle,
            Strings.Format(Strings.ConsentIntro, employerStart, employer),
            Strings.Format(Strings.ConsentIntroReview, employerStart),
            Strings.Format(Strings.ConsentSeesHeading, employer),
            sees,
            Strings.Format(Strings.ConsentSeesMore, employer),
            Strings.ConsentNeverHeading,
            [Strings.ConsentNeverKeys, Strings.ConsentNeverScreen, Strings.ConsentNeverContents, Strings.ConsentNeverPersonal],
            Strings.ConsentDeclineHeading,
            decline,
            Strings.Format(Strings.ConsentSmallPrint, employer));
    }

    /// <summary>"17 Oct 2026, 9:30 AM" for "You agreed on …".</summary>
    public static string When(DateTimeOffset at) => at.ToLocalTime().ToString("d MMM yyyy, h:mm tt");
}

using Zinwe.Agent.Core.Policy;

namespace Zinwe.Agent.Core.Work;

/// <summary>
/// What Zinwe can record on this PC under the policy in force: exactly the parts of the consent notice that are
/// switched on. The widget lists only these, and the notice version is derived from them, so turning something
/// new on asks the member again (turning things off never does).
/// </summary>
/// <param name="AppsAndWebsites">Which apps are used and websites visited, for how long, and how much internet data
/// each uses (the web module; data used since v1.1).</param>
/// <param name="Transfers">Copies to USB drives, shared folders and email attachments: sizes and counts.</param>
/// <param name="TransferDetails">The owner's "include file names" switch: file names and email subjects too.</param>
/// <param name="RemoteAndInstalls">Remote-access tools and newly installed software.</param>
/// <param name="WorkHours">Log in / breaks (with the reason picked) / log out, sent when the member presses them.</param>
public sealed record MonitoringScope(
    bool AppsAndWebsites,
    bool Transfers,
    bool TransferDetails,
    bool RemoteAndInstalls,
    bool WorkHours)
{
    /// <summary>Everything the notice can list. Used when the policy is not known yet.</summary>
    public static readonly MonitoringScope Everything = new(true, true, true, true, true);

    /// <summary>True when anything at all is listed.</summary>
    public bool Any => AppsAndWebsites || Transfers || TransferDetails || RemoteAndInstalls || WorkHours;

    /// <summary>
    /// The scope of <paramref name="policy"/>. No policy yet = <see cref="Everything"/> (the member is told the
    /// most that could be recorded). A feature block an older backend leaves out runs on its local defaults,
    /// which are on, so it counts as on; file-name details are off unless the owner switched them on.
    /// </summary>
    public static MonitoringScope From(AgentPolicy? policy)
    {
        if (policy is null)
        {
            return Everything;
        }
        var transfers = policy.DataTransfer?.Enabled ?? true;
        return new MonitoringScope(
            AppsAndWebsites: policy.Web?.Enabled ?? true,
            Transfers: transfers,
            TransferDetails: transfers && (policy.DataTransfer?.IncludeDetails ?? false),
            RemoteAndInstalls: policy.RemoteSoftware?.Enabled ?? true,
            WorkHours: PlanModules.Allows(policy.Entitlements, PlanModules.WorkTime));
    }
}

/// <summary>
/// The consent notice's version: <see cref="BaseVersion"/> (the wording) plus one letter per thing switched on,
/// e.g. "2026-10-22.atrw". At most 16 characters (the backend keeps 20).
/// <para>An agreement covers the current notice when the wording is the same and everything switched on now was
/// switched on when they agreed (<see cref="Covers"/>). So a new wording, or the owner turning on something new
/// (like file names), asks again; switching something off doesn't.</para>
/// </summary>
public static class ConsentNotice
{
    /// <summary>The notice wording. Bump it only when the text of the notice changes.</summary>
    // 2026-10-17: names the employer, lists only what is switched on (file names / email subjects when the owner
    // enables details), and offers "I don't agree".
    // 2026-10-22 (v1.1, FR-18): apps and websites now also say how much internet data each uses, and websites how
    // long they're open. New wording, so everyone is asked once more.
    public const string BaseVersion = "2026-10-22";

    /// <summary>The version of the full notice (everything listed): covers any policy with this wording.</summary>
    public const string FullVersion = BaseVersion + "." + AllFlags;

    private const string AllFlags = "atdrw";

    public static string VersionFor(MonitoringScope scope)
    {
        var flags = string.Concat(
            scope.AppsAndWebsites ? "a" : "",
            scope.Transfers ? "t" : "",
            scope.TransferDetails ? "d" : "",
            scope.RemoteAndInstalls ? "r" : "",
            scope.WorkHours ? "w" : "");
        return BaseVersion + "." + (flags.Length == 0 ? "0" : flags);
    }

    /// <summary>True when an agreement to <paramref name="agreed"/> covers the notice <paramref name="current"/>.</summary>
    public static bool Covers(string? agreed, string? current)
    {
        if (string.IsNullOrWhiteSpace(agreed) || string.IsNullOrWhiteSpace(current))
        {
            return false;
        }
        var (agreedBase, agreedFlags) = Split(agreed.Trim());
        var (currentBase, currentFlags) = Split(current.Trim());
        if (!string.Equals(agreedBase, currentBase, StringComparison.Ordinal))
        {
            return false;
        }
        return currentFlags.All(f => f == '0' || agreedFlags.Contains(f, StringComparison.Ordinal));
    }

    /// <summary>Where <paramref name="record"/> leaves the member for the notice <paramref name="noticeVersion"/>.</summary>
    public static ConsentState StateOf(ConsentRecord? record, string? noticeVersion)
    {
        if (record is { IsDeclined: true })
        {
            return ConsentState.Declined;
        }
        return record is not null && Covers(record.Version, noticeVersion ?? FullVersion)
            ? ConsentState.Given
            : ConsentState.Pending;
    }

    private static (string Base, string Flags) Split(string version)
    {
        var dot = version.IndexOf('.', StringComparison.Ordinal);
        return dot < 0 ? (version, "") : (version[..dot], version[(dot + 1)..]);
    }
}

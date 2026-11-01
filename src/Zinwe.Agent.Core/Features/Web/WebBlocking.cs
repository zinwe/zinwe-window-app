using System.Text;
using Microsoft.Extensions.Logging;

namespace Zinwe.Agent.Core.Features.Web;

/// <summary>Applies (or clears) the website blocklist on this PC. Idempotent; throws only on real I/O failure.</summary>
public interface IWebBlocker
{
    /// <summary>True when nothing on the PC is changed (test mode); events carry this as "dry_run".</summary>
    bool IsDryRun { get; }

    /// <summary>Make the PC's blocking match <paramref name="list"/> (empty list = clear everything we added).</summary>
    void Apply(Blocklist list);
}

/// <summary>
/// Writes/clears the agent's managed block in the system hosts file. Abstracted (like <c>IRegistryWriter</c>) so
/// tests never touch the real file and the only implementation that mutates the machine lives in the service.
/// </summary>
public interface IHostsFileWriter
{
    /// <summary>Replace the managed block so exactly <paramref name="hostnames"/> are sinkholed. Idempotent.</summary>
    void SetBlockedDomains(IReadOnlyList<string> hostnames);

    /// <summary>Remove the managed block entirely, leaving the rest of the hosts file untouched.</summary>
    void ClearBlockedDomains();
}

/// <summary>
/// Pure text transform for the hosts file: insert/replace/remove the agent's own fenced block and never touch a
/// line outside it. Lives in Core so it is fully unit-tested without a real file.
/// </summary>
public static class HostsBlock
{
    public const string Begin = "# BEGIN ZINWE WEB BLOCK (managed by Zinwe Agent - do not edit)";
    public const string End = "# END ZINWE WEB BLOCK";

    /// <summary>Where blocked hosts are pointed. 0.0.0.0 is a dead address, so no connection is attempted.</summary>
    public const string Sink = "0.0.0.0";

    /// <summary>
    /// Names per line. Windows reads at most nine on one line. Grouping them keeps a list of thousands to a fraction of
    /// the lines, and the DNS Client service has to re-read every line each time the file changes.
    /// </summary>
    public const int HostsPerLine = 9;

    /// <summary>
    /// Returns <paramref name="existing"/> with the managed block set to <paramref name="hostnames"/>, or removed
    /// when the list is empty. Any previous managed block is replaced; everything outside the fences is preserved.
    /// </summary>
    public static string ApplyToHosts(string existing, IReadOnlyList<string> hostnames)
    {
        var newline = existing.Contains("\r\n", StringComparison.Ordinal) || !existing.Contains('\n') ? "\r\n" : "\n";
        var kept = StripManagedBlock(existing).TrimEnd('\r', '\n').Replace("\n", newline, StringComparison.Ordinal);

        if (hostnames.Count == 0)
        {
            return kept.Length == 0 ? "" : kept + newline;
        }

        var block = new List<string> { Begin };
        block.AddRange(hostnames.Chunk(HostsPerLine).Select(group => $"{Sink} {string.Join(' ', group)}"));
        block.Add(End);

        var body = kept.Length == 0 ? "" : kept + newline + newline;
        return body + string.Join(newline, block) + newline;
    }

    /// <summary>The hosts file with any agent-managed block (including malformed/unterminated) removed.</summary>
    public static string StripManagedBlock(string existing)
    {
        var lines = existing.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var result = new List<string>(lines.Length);
        var inside = false;
        foreach (var line in lines)
        {
            if (!inside && line.Trim() == Begin)
            {
                inside = true;
                continue;
            }
            if (inside)
            {
                if (line.Trim() == End)
                {
                    inside = false;
                }
                continue;
            }
            result.Add(line);
        }
        return string.Join("\n", result);
    }
}

/// <summary>
/// Replaces a file in one step, so a reader (the DNS Client service, or a crash mid-way) only ever sees the whole old
/// file or the whole new one, and the original's permissions stay. Takes a few tries: the DNS Client and antivirus hold
/// the hosts file open for moments at a time. Throws <see cref="IOException"/> or
/// <see cref="UnauthorizedAccessException"/> if it stays locked; nothing is left behind.
/// </summary>
public static class AtomicFile
{
    private const int Attempts = 5;

    public static void Replace(string path, string content, Encoding encoding, TimeSpan? retryDelay = null)
    {
        var temp = path + ".zinwe.tmp";
        File.WriteAllText(temp, content, encoding);
        try
        {
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    if (File.Exists(path))
                    {
                        File.Replace(temp, path, destinationBackupFileName: null, ignoreMetadataErrors: true);
                    }
                    else
                    {
                        File.Move(temp, path);
                    }
                    return;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException && attempt < Attempts)
                {
                    Thread.Sleep(retryDelay ?? TimeSpan.FromMilliseconds(300));
                }
            }
        }
        finally
        {
            File.Delete(temp); // already gone after a success
        }
    }
}

/// <summary>The real blocker: drives the hosts file. Only built by <see cref="WebBlockerSelector"/> as LocalSystem.</summary>
public sealed class HostsFileWebBlocker(IHostsFileWriter writer, ILogger<HostsFileWebBlocker> logger) : IWebBlocker
{
    public bool IsDryRun => false;

    public void Apply(Blocklist list)
    {
        if (list.IsEmpty)
        {
            writer.ClearBlockedDomains();
            logger.LogInformation("Website blocklist cleared");
            return;
        }
        writer.SetBlockedDomains(list.Hostnames);
        logger.LogInformation("Website blocklist applied: {Count} host(s) across {Categories}",
            list.Hostnames.Count, string.Join(", ", list.Categories));
    }
}

/// <summary>Test mode: logs exactly what it would sinkhole and changes nothing.</summary>
public sealed class DryRunWebBlocker(ILogger<DryRunWebBlocker> logger) : IWebBlocker
{
    public bool IsDryRun => true;

    public void Apply(Blocklist list)
    {
        if (list.IsEmpty)
        {
            logger.LogInformation("[dry run] would clear the website blocklist");
            return;
        }
        // The first few only: a real list is thousands of names, and the log is not the place for them.
        logger.LogInformation("[dry run] would sinkhole {Count} host(s) to {Sink} in the hosts file, starting: {Hosts}",
            list.Hostnames.Count, HostsBlock.Sink, string.Join(", ", list.Hostnames.Take(5)));
    }
}

/// <summary>
/// SAFETY RULE (identical to <c>UsbEnforcerSelector</c>): the hosts-file blocker is used only when the process is
/// LocalSystem AND Agent:Enforcement:DryRun is false. A developer's <c>dotnet run</c>, a test, or a misconfigured
/// box always gets the dry-run blocker, so nothing short of the installed service can edit the hosts file.
/// </summary>
public static class WebBlockerSelector
{
    public static IWebBlocker Select(
        bool runningAsSystem,
        bool dryRunConfigured,
        Func<IWebBlocker> createRealBlocker,
        ILoggerFactory loggers)
    {
        var logger = loggers.CreateLogger(typeof(WebBlockerSelector));
        if (dryRunConfigured)
        {
            logger.LogWarning("Agent:Enforcement:DryRun is on: website blocking will be logged, not applied.");
            return new DryRunWebBlocker(loggers.CreateLogger<DryRunWebBlocker>());
        }
        if (!runningAsSystem)
        {
            logger.LogWarning("Not running as LocalSystem: website blocking will be logged, not applied (test mode).");
            return new DryRunWebBlocker(loggers.CreateLogger<DryRunWebBlocker>());
        }
        return createRealBlocker();
    }
}

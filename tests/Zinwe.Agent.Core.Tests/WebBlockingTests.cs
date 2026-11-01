using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Zinwe.Agent.Core.Events;
using Zinwe.Agent.Core.Features.Web;

namespace Zinwe.Agent.Core.Tests;

/// <summary>Records hosts-file writes instead of making them. Tests never touch the real hosts file.</summary>
internal sealed class FakeHostsFileWriter : IHostsFileWriter
{
    public List<string>? LastSet { get; private set; }
    public int Cleared { get; private set; }
    public int Sets { get; private set; }

    public void SetBlockedDomains(IReadOnlyList<string> hostnames)
    {
        Sets++;
        LastSet = [.. hostnames];
    }

    public void ClearBlockedDomains() => Cleared++;
}

internal sealed class ThrowingWebBlocker(bool dryRun = false) : IWebBlocker
{
    public bool IsDryRun => dryRun;

    public void Apply(Blocklist list) => throw new IOException("hosts file is locked");
}

public sealed class WebBlockingTests
{
    private const string Begin = "# BEGIN ZINWE WEB BLOCK (managed by Zinwe Agent - do not edit)";
    private const string End = "# END ZINWE WEB BLOCK";

    // ---- HostsBlock (pure text transform) ----

    [Fact]
    public void Apply_adds_a_fenced_block_to_an_empty_hosts_file()
    {
        var result = HostsBlock.ApplyToHosts("", ["a.test", "www.a.test"]);

        Assert.Contains(Begin, result, StringComparison.Ordinal);
        Assert.Contains("0.0.0.0 a.test www.a.test", result, StringComparison.Ordinal); // names share a line, up to nine
        Assert.Contains(End, result, StringComparison.Ordinal);
    }

    [Fact]
    public void Apply_preserves_existing_lines_outside_the_block()
    {
        const string existing = "127.0.0.1 localhost\r\n::1 localhost\r\n";

        var result = HostsBlock.ApplyToHosts(existing, ["a.test"]);

        Assert.Contains("127.0.0.1 localhost", result, StringComparison.Ordinal);
        Assert.Contains("::1 localhost", result, StringComparison.Ordinal);
        Assert.Contains("0.0.0.0 a.test", result, StringComparison.Ordinal);
    }

    [Fact]
    public void Apply_replaces_a_previous_managed_block_and_is_idempotent()
    {
        const string existing = "127.0.0.1 localhost\r\n";
        var once = HostsBlock.ApplyToHosts(existing, ["a.test"]);
        var twice = HostsBlock.ApplyToHosts(once, ["b.test"]);

        Assert.DoesNotContain("a.test", twice, StringComparison.Ordinal);
        Assert.Contains("0.0.0.0 b.test", twice, StringComparison.Ordinal);
        // Exactly one managed block, however many times we rewrite.
        Assert.Equal(1, CountOccurrences(twice, Begin));

        var again = HostsBlock.ApplyToHosts(twice, ["b.test"]);
        Assert.Equal(again, twice);
    }

    [Fact]
    public void Apply_with_an_empty_list_removes_the_managed_block_only()
    {
        const string existing = "127.0.0.1 localhost\r\n";
        var blocked = HostsBlock.ApplyToHosts(existing, ["a.test"]);

        var cleared = HostsBlock.ApplyToHosts(blocked, []);

        Assert.DoesNotContain(Begin, cleared, StringComparison.Ordinal);
        Assert.DoesNotContain("a.test", cleared, StringComparison.Ordinal);
        Assert.Contains("127.0.0.1 localhost", cleared, StringComparison.Ordinal);
    }

    [Fact]
    public void Strip_removes_an_unterminated_managed_block()
    {
        var mangled = $"127.0.0.1 localhost\n{Begin}\n0.0.0.0 a.test\n"; // no END line
        var stripped = HostsBlock.StripManagedBlock(mangled);

        Assert.DoesNotContain("a.test", stripped, StringComparison.Ordinal);
        Assert.Contains("127.0.0.1 localhost", stripped, StringComparison.Ordinal);
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var i = 0;
        while ((i = haystack.IndexOf(needle, i, StringComparison.Ordinal)) >= 0)
        {
            count++;
            i += needle.Length;
        }
        return count;
    }

    // ---- Selector safety (same rule as USB) ----

    [Theory]
    [InlineData(false, false)] // normal user, dry run off → still dry run
    [InlineData(false, true)]
    [InlineData(true, true)]   // system but dry run on → dry run
    public void Real_blocker_is_never_built_unless_system_and_dry_run_off(bool isSystem, bool dryRun)
    {
        var built = false;
        var loggers = new ListLoggerFactory();

        var blocker = WebBlockerSelector.Select(isSystem, dryRun, () =>
        {
            built = true;
            return new HostsFileWebBlocker(new FakeHostsFileWriter(), NullLogger<HostsFileWebBlocker>.Instance);
        }, loggers);

        Assert.IsType<DryRunWebBlocker>(blocker);
        Assert.True(blocker.IsDryRun);
        Assert.False(built);
        Assert.Contains(loggers.Entries, e => e.Level == LogLevel.Warning);
    }

    [Fact]
    public void Real_blocker_is_used_only_as_system_with_dry_run_off()
    {
        var real = new HostsFileWebBlocker(new FakeHostsFileWriter(), NullLogger<HostsFileWebBlocker>.Instance);

        var blocker = WebBlockerSelector.Select(runningAsSystem: true, dryRunConfigured: false, () => real, new ListLoggerFactory());

        Assert.Same(real, blocker);
        Assert.False(blocker.IsDryRun);
    }

    // ---- HostsFileWebBlocker / DryRunWebBlocker ----

    [Fact]
    public void Hosts_blocker_sets_every_hostname_when_the_list_is_not_empty()
    {
        var writer = new FakeHostsFileWriter();
        var blocker = new HostsFileWebBlocker(writer, NullLogger<HostsFileWebBlocker>.Instance);
        var list = new Blocklist([new BlockRule("a.test", "custom")]);

        blocker.Apply(list);

        Assert.Equal(1, writer.Sets);
        Assert.Equal(0, writer.Cleared);
        Assert.Equal(["a.test", "www.a.test"], writer.LastSet);
    }

    [Fact]
    public void Hosts_blocker_clears_when_the_list_is_empty()
    {
        var writer = new FakeHostsFileWriter();
        var blocker = new HostsFileWebBlocker(writer, NullLogger<HostsFileWebBlocker>.Instance);

        blocker.Apply(new Blocklist([]));

        Assert.Equal(1, writer.Cleared);
        Assert.Equal(0, writer.Sets);
    }

    [Fact]
    public void Dry_run_blocker_changes_nothing_and_logs()
    {
        var loggers = new ListLoggerFactory();
        var blocker = new DryRunWebBlocker(loggers.CreateLogger<DryRunWebBlocker>());

        blocker.Apply(new Blocklist([new BlockRule("a.test", "custom")]));

        Assert.True(blocker.IsDryRun);
        Assert.Contains(loggers.Entries, e => e.Message.Contains("dry run", StringComparison.OrdinalIgnoreCase));
    }

    // ---- WebProtection (events) ----

    [Fact]
    public void Apply_records_a_blocklist_applied_event_with_dry_run_flag()
    {
        using var dir = new TempDir();
        var events = new EventQueue(dir.Path);
        var protection = new WebProtection(new DryRunWebBlocker(NullLogger<DryRunWebBlocker>.Instance), events, NullLogger<WebProtection>.Instance);
        var list = new Blocklist([new BlockRule("a.test", "malware")]);

        var ok = protection.Apply(list, report: true);

        Assert.True(ok);
        Assert.Same(list, protection.Applied);
        var evt = Assert.Single(events.PeekBatch(10));
        Assert.Equal(WebEventTypes.BlocklistApplied, evt.Type);
        Assert.Equal(EventSeverity.Info, evt.Severity);
        var data = (JsonObject)JsonNode.Parse(evt.DataJson)!;
        Assert.Equal(1, (int)data["blocked_count"]!);
        Assert.True((bool)data["dry_run"]!);
    }

    [Fact]
    public void Apply_with_report_false_writes_no_event()
    {
        using var dir = new TempDir();
        var events = new EventQueue(dir.Path);
        var protection = new WebProtection(new DryRunWebBlocker(NullLogger<DryRunWebBlocker>.Instance), events, NullLogger<WebProtection>.Instance);

        protection.Apply(new Blocklist([new BlockRule("a.test", "malware")]), report: false);

        Assert.Equal(0, events.Count());
    }

    [Fact]
    public void Apply_returns_false_and_stays_unapplied_when_the_blocker_throws()
    {
        using var dir = new TempDir();
        var events = new EventQueue(dir.Path);
        var protection = new WebProtection(new ThrowingWebBlocker(), events, NullLogger<WebProtection>.Instance);

        var ok = protection.Apply(new Blocklist([new BlockRule("a.test", "malware")]), report: true);

        Assert.False(ok);
        Assert.Null(protection.Applied);
        Assert.Equal(0, events.Count());
    }

    [Fact]
    public void Blocked_attempt_records_a_warning_event_with_domain_and_category()
    {
        using var dir = new TempDir();
        var events = new EventQueue(dir.Path);
        var protection = new WebProtection(new DryRunWebBlocker(NullLogger<DryRunWebBlocker>.Instance), events, NullLogger<WebProtection>.Instance);

        protection.OnBlockedAttempt("Phishing-Sample.Test", "phishing");

        var evt = Assert.Single(events.PeekBatch(10));
        Assert.Equal(WebEventTypes.BlockedAttempt, evt.Type);
        Assert.Equal(EventSeverity.Warning, evt.Severity);
        var data = (JsonObject)JsonNode.Parse(evt.DataJson)!;
        Assert.Equal("phishing-sample.test", (string)data["domain"]!); // normalized
        Assert.Equal("phishing", (string)data["category"]!);
        Assert.True((bool)data["dry_run"]!);
    }
}

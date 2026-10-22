using Zinwe.Agent.Core.Policy;
using Zinwe.Agent.Core.Work;

namespace Zinwe.Agent.Core.Tests;

public sealed class ConsentNoticeTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "zinwe-notice-" + Guid.NewGuid().ToString("N"));

    private static AgentPolicy Policy(bool web = true, bool transfers = true, bool details = false, bool remote = true,
        IReadOnlyList<string>? entitlements = null) => new(
        1,
        new UsbPolicy(UsbMode.Allow),
        new WebPolicy(web, WebMode.Monitor, [], []),
        new DataTransferPolicy(transfers, 1, 1, 1, details),
        new RemoteSoftwarePolicy(remote, false, [], []),
        Entitlements: entitlements);

    [Fact]
    public void No_policy_yet_lists_everything()
    {
        Assert.Equal(MonitoringScope.Everything, MonitoringScope.From(null));
        Assert.Equal(ConsentNotice.FullVersion, ConsentNotice.VersionFor(MonitoringScope.From(null)));
    }

    [Fact]
    public void The_scope_follows_what_the_owner_switched_on()
    {
        var scope = MonitoringScope.From(Policy(web: false, details: true, remote: false, entitlements: ["usb_control"]));

        Assert.Equal(new MonitoringScope(false, true, true, false, false), scope);
        Assert.Equal("2026-10-22.td", ConsentNotice.VersionFor(scope));
    }

    [Fact]
    public void An_older_backend_without_feature_blocks_counts_them_as_on_but_details_off()
    {
        var scope = MonitoringScope.From(new AgentPolicy(3, new UsbPolicy(UsbMode.Block)));

        Assert.Equal(new MonitoringScope(true, true, false, true, true), scope);
    }

    [Fact]
    public void File_name_details_need_transfers_on()
    {
        Assert.False(MonitoringScope.From(Policy(transfers: false, details: true)).TransferDetails);
    }

    [Fact]
    public void Nothing_switched_on_still_has_a_version_within_the_backend_limit()
    {
        var version = ConsentNotice.VersionFor(new MonitoringScope(false, false, false, false, false));
        Assert.Equal("2026-10-22.0", version);
        Assert.True(ConsentNotice.FullVersion.Length <= 20);
    }

    [Theory]
    [InlineData("2026-10-17.atdrw", "2026-10-17.atrw", true)]   // owner switched details off: no new question
    [InlineData("2026-10-17.atrw", "2026-10-17.atrw", true)]
    [InlineData("2026-10-17.atrw", "2026-10-17.atdrw", false)]  // owner switched file names on: ask again
    [InlineData("2026-10-17.w", "2026-10-17.aw", false)]
    [InlineData("2026-10-17.0", "2026-10-17.0", true)]
    [InlineData("2026-10-15", "2026-10-17.atrw", false)]        // the old wording: ask again
    [InlineData("2026-10-17.atdrw", "2026-10-22.atrw", false)]  // v1.1 (FR-18): data used is new, ask again
    [InlineData(null, "2026-10-17.atrw", false)]
    public void An_agreement_covers_the_same_wording_with_nothing_new_switched_on(string? agreed, string current, bool covers)
    {
        Assert.Equal(covers, ConsentNotice.Covers(agreed, current));
    }

    [Fact]
    public void State_of_an_answer()
    {
        var now = DateTimeOffset.Now;
        Assert.Equal(ConsentState.Pending, ConsentNotice.StateOf(null, "2026-10-17.w"));
        Assert.Equal(ConsentState.Given, ConsentNotice.StateOf(new ConsentRecord("2026-10-17.aw", now), "2026-10-17.w"));
        Assert.Equal(ConsentState.Pending, ConsentNotice.StateOf(new ConsentRecord("2026-10-17.w", now), "2026-10-17.aw"));
        Assert.Equal(ConsentState.Declined,
            ConsentNotice.StateOf(new ConsentRecord("2026-10-17.w", now, ConsentChoice.Declined), "2026-10-17.aw"));
    }

    // ---- The service's gate ----

    private void Write(string json)
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "consent.json"), json);
    }

    [Fact]
    public void A_decline_is_read_and_never_grants()
    {
        Write("""{"Version":"2026-10-17.atdrw","State":"declined","DecidedAt":"2026-10-17T09:30:00+05:30"}""");
        var gate = new ConsentGate(new ConsentReader(_dir));

        Assert.False(gate.IsGranted());
        Assert.Equal(ConsentState.Declined, gate.State());
        Assert.True(gate.Current()!.IsDeclined);
        var report = gate.ForHeartbeat();
        Assert.Equal(("declined", "2026-10-17.atdrw"), (report!.State, report.Version));
        Assert.False(report.IsGiven);
    }

    [Fact]
    public void An_agreement_is_reported_as_given()
    {
        Write("""{"Version":"2026-10-22.atdrw","AcceptedAt":"2026-10-22T09:30:00+05:30","State":"given","DecidedAt":"2026-10-22T09:30:00+05:30"}""");
        var gate = new ConsentGate(new ConsentReader(_dir));

        Assert.True(gate.IsGranted());
        var report = gate.ForHeartbeat();
        Assert.Equal(("given", "2026-10-22.atdrw"), (report!.State, report.Version));
    }

    [Fact]
    public void Turning_on_something_new_pauses_recording_until_the_member_agrees_again()
    {
        Write("""{"Version":"2026-10-17.atrw","AcceptedAt":"2026-10-17T09:30:00+05:30"}""");
        var notice = "2026-10-17.atrw";
        var gate = new ConsentGate(new ConsentReader(_dir), noticeVersion: () => notice);
        Assert.True(gate.IsGranted());

        notice = "2026-10-17.atdrw"; // the owner switched on file names
        Assert.False(gate.IsGranted());
        Assert.Equal(ConsentState.Pending, gate.State());
        Assert.Null(gate.ForHeartbeat()); // not answered for this notice: nothing reported (backend keeps pending)
    }

    [Fact]
    public void The_fingerprint_changes_with_the_answer()
    {
        var gate = new ConsentGate(new ConsentReader(_dir));
        var before = gate.Fingerprint();
        Write("""{"Version":"2026-10-17.atdrw","State":"declined","DecidedAt":"2026-10-17T09:30:00+05:30"}""");
        gate.Refresh();
        Assert.NotEqual(before, gate.Fingerprint());
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }
}

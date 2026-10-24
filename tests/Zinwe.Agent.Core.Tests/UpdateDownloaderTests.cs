using System.Net;
using System.Security.Cryptography;
using Zinwe.Agent.Core.Backend;
using Zinwe.Agent.Core.Ipc;
using Zinwe.Agent.Core.Update;

namespace Zinwe.Agent.Core.Tests;

/// <summary>The one-click update only ever installs the exact file the Zinwe team published.</summary>
public sealed class UpdateDownloaderTests : IDisposable
{
    private static readonly byte[] Msi = [.. Enumerable.Range(0, 50_000).Select(i => (byte)(i % 251))];
    private static readonly string MsiHash = Convert.ToHexStringLower(SHA256.HashData(Msi));

    private readonly TempDir _dir = new();
    private readonly FakeBackend _backend = new();

    public UpdateDownloaderTests() =>
        _backend.Respond = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Msi) };

    private static AgentUpdate Offer(string? url = "/api/devices/releases/7/download/", string? sha = null, long? size = null) =>
        new("0.3.0", "Faster chat", true, url, sha ?? MsiHash, size ?? Msi.Length);

    private readonly FakeSignature _signature = new();

    private Task<string> Download(AgentUpdate offer) =>
        new UpdateDownloader(_backend.Client(), _signature).DownloadAsync(offer, "tok", _dir.Path, CancellationToken.None);

    /// <summary>Stands in for the Authenticode check (that one is tested on real signed files in InstallerSignatureTests).</summary>
    private sealed class FakeSignature : IInstallerSignatureCheck
    {
        public string? Refuse { get; set; }
        public List<string> Checked { get; } = [];

        public void Verify(string path)
        {
            Checked.Add(path);
            if (Refuse is not null)
            {
                throw new UpdateException("This update isn't signed by Zinwe, so it wasn't installed. Please tell your admin.", Refuse);
            }
        }
    }

    [Fact]
    public async Task The_signature_is_checked_on_the_final_msi_after_the_hash()
    {
        var path = await Download(Offer());
        Assert.Equal([path], _signature.Checked);
    }

    [Fact]
    public async Task An_installer_not_signed_by_zinwe_is_refused_and_deleted()
    {
        _signature.Refuse = "Signature check failed (0x800B0100): the installer has no digital signature.";
        var e = await Assert.ThrowsAsync<UpdateException>(() => Download(Offer()));
        Assert.Contains("isn't signed by Zinwe", e.Message);
        Assert.Contains("0x800B0100", e.Detail);
        Assert.Empty(Directory.GetFiles(_dir.Path)); // the refused .msi is not left for anyone to run
    }

    [Fact]
    public async Task A_bad_hash_is_refused_before_the_signature_is_looked_at()
    {
        await Assert.ThrowsAsync<UpdateException>(() => Download(Offer(sha: new string('a', 64))));
        Assert.Empty(_signature.Checked);
    }

    [Fact]
    public async Task A_matching_installer_is_saved_and_fetched_with_the_device_token()
    {
        var path = await Download(Offer());

        Assert.Equal(Msi, await File.ReadAllBytesAsync(path));
        Assert.EndsWith("ZinweAgent-0.3.0.msi", path);
        var request = Assert.Single(_backend.Requests);
        Assert.Equal("/api/devices/releases/7/download/", request.Path);
        Assert.Equal(("Device", "tok"), (request.AuthScheme, request.AuthParameter));
    }

    [Fact]
    public async Task A_tampered_download_is_rejected_and_deleted()
    {
        var e = await Assert.ThrowsAsync<UpdateException>(() => Download(Offer(sha: new string('a', 64))));
        Assert.Contains("safety check", e.Message);
        Assert.Empty(Directory.GetFiles(_dir.Path)); // neither the .msi nor the .part is left behind
    }

    [Fact]
    public async Task A_short_download_is_rejected()
    {
        var e = await Assert.ThrowsAsync<UpdateException>(() => Download(Offer(size: Msi.Length + 1)));
        Assert.Contains("didn't download completely", e.Message);
    }

    [Fact]
    public async Task An_external_link_never_gets_the_device_token()
    {
        await Download(Offer(url: "https://cdn.zinwe.io/ZinweAgent-0.3.0.msi"));
        var request = Assert.Single(_backend.Requests);
        Assert.Null(request.AuthScheme);
    }

    [Fact]
    public async Task Plain_http_links_and_missing_installers_are_refused()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => Download(Offer(url: "http://cdn.zinwe.io/a.msi")));
        var none = await Assert.ThrowsAsync<UpdateException>(() => Download(Offer(url: "")));
        Assert.Contains("hasn't published an installer", none.Message);
    }

    [Fact]
    public async Task A_failed_download_says_so_in_plain_words()
    {
        _backend.Respond = _ => new HttpResponseMessage(HttpStatusCode.InternalServerError);
        var e = await Assert.ThrowsAsync<UpdateException>(() => Download(Offer()));
        Assert.Contains("Couldn't download the update", e.Message);
    }

    [Fact]
    public async Task The_heartbeat_reads_the_offered_release()
    {
        _backend.Respond = _ => FakeBackend.Json(HttpStatusCode.OK, $$$"""
            {"device_id":1,"server_time":"2026-10-15T09:00:00Z","next_heartbeat_seconds":60,"policy_version":3,
             "update_required":true,"download_url":"https://zinwe.io/download",
             "update":{"version":"0.3.0","notes":"Faster chat","required":true,
                       "download_url":"/api/devices/releases/7/download/","sha256":"{{{MsiHash}}}","size":{{{Msi.Length}}} } }
            """);
        var beat = await _backend.Client().HeartbeatAsync("tok",
            new HeartbeatRequest("pc", "win", "0.2.0", "working", 0, 0), CancellationToken.None);

        Assert.True(beat.UpdateRequired);
        Assert.Equal(new AgentUpdate("0.3.0", "Faster chat", true, "/api/devices/releases/7/download/", MsiHash, Msi.Length), beat.Update);
    }

    [Fact]
    public void The_tray_reads_update_progress_from_the_status()
    {
        var status = new AgentStatus(AgentState.UpdateRequired, "0.2.0", "pc", 1, null, null,
            UpdateVersion: "0.3.0", UpdateNotes: "Faster chat", UpdateStage: UpdateStages.Installing,
            UpdateMessage: "Installing", UpdateInstallable: true);
        var back = PipeProtocol.DeserializeStatus(PipeProtocol.Serialize(status));
        Assert.Equal(status, back);
    }

    public void Dispose() => _dir.Dispose();
}

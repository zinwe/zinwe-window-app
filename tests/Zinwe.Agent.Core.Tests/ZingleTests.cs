using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Zinwe.Agent.Core.Backend;
using Zinwe.Agent.Core.Ipc;
using Zinwe.Agent.Core.Messages;
using Zinwe.Agent.Core.Zingles;

namespace Zinwe.Agent.Core.Tests;

/// <summary>
/// The special-day logo (Zingle): reading <c>GET api/auth/zingle/?for=app</c>, the cache keyed by id + <c>?v=</c>,
/// old files deleted, offline keeping what is cached, and <c>null</c> taking the app back to the normal logo.
/// </summary>
public sealed class ZingleTests : IDisposable
{
    private const string Endpoint = "/api/auth/zingle/";
    private const string Still = "/api/auth/zingle/1/still/?v=1793785141";
    private const string Animated = "/api/auth/zingle/1/animated/?v=1793785141";

    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, .. Enumerable.Repeat((byte)1, 64)];
    private static readonly byte[] Gif = [.. "GIF89a"u8, .. Enumerable.Repeat((byte)2, 64)];
    private static readonly byte[] Webp = [.. "RIFF"u8, 0, 0, 0, 0, .. "WEBP"u8, .. Enumerable.Repeat((byte)3, 64)];

    private readonly TempDir _dir = new();
    private readonly FakeBackend _backend = new();

    public void Dispose() => _dir.Dispose();

    private string Widget => Path.Combine(_dir.Path, "widget");
    private string Data => Path.Combine(_dir.Path, "agent");
    private string Cache => Path.Combine(Data, "zingles");

    private static string Body(long id = 1, string still = Still, string? animated = Animated, string alt = "Zinwe, lit up for Diwali") =>
        JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["id"] = id, ["animated_url"] = animated, ["still_url"] = still, ["alt"] = alt, ["places"] = "both", ["app"] = true,
            ["updated_at"] = "2026-11-04T09:39:00Z",
        });

    private static HttpResponseMessage File(byte[] bytes) =>
        new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };

    /// <summary>The backend: the endpoint answers <paramref name="body"/>, the files their bytes (by path).</summary>
    private void Serve(string body, byte[]? still = null, byte[]? animated = null) => _backend.Respond = r =>
        r.Path == Endpoint ? FakeBackend.Json(HttpStatusCode.OK, body)
        : r.Path.EndsWith("/still/", StringComparison.Ordinal) ? File(still ?? Png)
        : r.Path.EndsWith("/animated/", StringComparison.Ordinal) ? File(animated ?? Gif)
        : new HttpResponseMessage(HttpStatusCode.NotFound);

    private ZingleSync Sync() => new(_backend.Client(), new LocalWidgetFolder(Widget), NullLogger<ZingleSync>.Instance, Data);

    private static string[] Files(string folder) =>
        Directory.Exists(folder) ? [.. Directory.EnumerateFiles(folder).Select(f => Path.GetFileName(f)!).Order()] : [];

    // ---- Reading the answer ----------------------------------------------------------------------

    [Fact]
    public void Null_means_no_zingle_and_an_object_reads_every_field()
    {
        Assert.Null(ZingleJson.Parse("null"));

        var z = ZingleJson.Parse(Body())!;
        Assert.Equal(1, z.Id);
        Assert.Equal(Still, z.StillUrl);
        Assert.Equal(Animated, z.AnimatedUrl);
        Assert.Equal("Zinwe, lit up for Diwali", z.Alt);
        Assert.Equal("both", z.Places);
        Assert.Equal(new DateTimeOffset(2026, 11, 4, 9, 39, 0, TimeSpan.Zero), z.UpdatedAt);
    }

    [Fact]
    public void A_zingle_for_the_website_only_is_none_and_a_missing_alt_says_Zinwe()
    {
        Assert.Null(ZingleJson.Parse("""{"id":1,"still_url":"/s/?v=1","app":false}"""));
        var z = ZingleJson.Parse("""{"id":2,"still_url":"/s/?v=1","alt":"  "}""")!;
        Assert.Equal(Zingle.DefaultAlt, z.Alt);
        Assert.Null(z.AnimatedUrl); // a still-only Zingle
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("\"zingle\"")]
    [InlineData("""{"still_url":"/s/"}""")]
    [InlineData("""{"id":0,"still_url":"/s/"}""")]
    [InlineData("""{"id":1}""")]
    [InlineData("""{"id":1,"still_url":"http://cdn.example.com/s.png"}""")]
    [InlineData("""{"id":1,"still_url":"//cdn.example.com/s.png"}""")]
    [InlineData("<html>proxy</html>")]
    public void Anything_else_is_unreadable_so_the_cached_one_stays(string body) =>
        Assert.ThrowsAny<JsonException>(() => ZingleJson.Parse(body));

    [Fact]
    public void Only_https_or_backend_paths_are_fetched_an_http_animation_is_dropped()
    {
        var z = ZingleJson.Parse("""{"id":1,"still_url":"https://cdn.zinwe.in/s.png","animated_url":"http://cdn.zinwe.in/a.gif"}""")!;
        Assert.Equal("https://cdn.zinwe.in/s.png", z.StillUrl);
        Assert.Null(z.AnimatedUrl);
    }

    // ---- Cache keys and file types ---------------------------------------------------------------

    [Fact]
    public void Files_are_keyed_by_id_and_the_v_on_the_url()
    {
        var z = ZingleJson.Parse(Body())!;
        Assert.Equal("zingle-1-1793785141-still", z.StillFileStem);
        Assert.Equal("zingle-1-1793785141-animated", z.AnimatedFileStem);
        Assert.Equal("zingle-7-abc_1-still.png", ZingleFiles.FileName(7, "/x/?a=1&v=abc_1", "still", ".png"));

        // No v, or one that isn't safe in a file name: a short hash of the URL (stable, and new when the URL changes).
        var hashed = ZingleFiles.Version("/x/still/");
        Assert.StartsWith("h", hashed);
        Assert.Equal(hashed, ZingleFiles.Version("/x/still/"));
        Assert.NotEqual(hashed, ZingleFiles.Version("/x/still2/"));
        Assert.StartsWith("h", ZingleFiles.Version("/x/?v=..%5C..%5Cevil"));
    }

    [Fact]
    public void The_type_comes_from_the_bytes()
    {
        Assert.Equal(".png", ZingleFiles.Sniff(Png));
        Assert.Equal(".gif", ZingleFiles.Sniff(Gif));
        Assert.Equal(".webp", ZingleFiles.Sniff(Webp));
        Assert.Null(ZingleFiles.Sniff("<html>"u8));
        Assert.Null(ZingleFiles.Sniff([]));
    }

    // ---- The endpoint ----------------------------------------------------------------------------

    [Fact]
    public async Task The_endpoint_is_asked_for_the_app_without_the_device_token()
    {
        Serve("null");
        Assert.Null(await _backend.Client().GetZingleAsync(CancellationToken.None));
        var seen = Assert.Single(_backend.Requests);
        Assert.Equal((HttpMethod.Get, Endpoint, (string?)null), (seen.Method, seen.Path, seen.AuthScheme));
        Assert.Equal("api/auth/zingle/?for=app", BackendClient.ZinglePath);
    }

    [Fact]
    public async Task A_backend_without_zingles_is_none_and_a_server_error_throws_to_keep_the_cache()
    {
        _backend.Respond = _ => new HttpResponseMessage(HttpStatusCode.NotFound);
        Assert.Null(await _backend.Client().GetZingleAsync(CancellationToken.None));

        _backend.Respond = _ => new HttpResponseMessage(HttpStatusCode.BadGateway);
        await Assert.ThrowsAsync<HttpRequestException>(() => _backend.Client().GetZingleAsync(CancellationToken.None));

        _backend.Respond = _ => FakeBackend.Json(HttpStatusCode.OK, "<html>");
        await Assert.ThrowsAsync<HttpRequestException>(() => _backend.Client().GetZingleAsync(CancellationToken.None));
    }

    // ---- Sync: download once, copy for the tray, clean up ----------------------------------------

    [Fact]
    public async Task A_live_zingle_is_downloaded_once_and_copied_into_the_widget_folder()
    {
        Serve(Body());
        var sync = Sync();
        Assert.Null(sync.Status); // nothing yet: the normal logo

        Assert.True(await sync.CheckAsync(CancellationToken.None));
        var status = sync.Status!;
        Assert.Equal(1, status.Id);
        Assert.Equal("Zinwe, lit up for Diwali", status.Alt);
        Assert.Equal(Path.Combine(Widget, "zingle-1-1793785141-still.png"), status.StillFile);
        Assert.Equal(Path.Combine(Widget, "zingle-1-1793785141-animated.gif"), status.AnimatedFile);
        Assert.Equal(Png, System.IO.File.ReadAllBytes(status.StillFile));
        Assert.Equal(["zingle-1-1793785141-animated.gif", "zingle-1-1793785141-still.png"], Files(Cache));
        Assert.Equal(3, _backend.Requests.Count);

        // The next hourly check: the same id + v, so nothing is downloaded again.
        Assert.True(await sync.CheckAsync(CancellationToken.None));
        Assert.Equal(4, _backend.Requests.Count);
        Assert.Equal(Endpoint, _backend.Requests[^1].Path);
        Assert.Equal(status, sync.Status);
    }

    [Fact]
    public async Task A_changed_file_is_a_new_name_and_the_old_files_are_deleted()
    {
        Serve(Body());
        var sync = Sync();
        await sync.CheckAsync(CancellationToken.None);

        Serve(Body(still: "/api/auth/zingle/1/still/?v=2", animated: "/api/auth/zingle/1/animated/?v=2"), animated: Webp);
        await sync.CheckAsync(CancellationToken.None);

        Assert.Equal(["zingle-1-2-animated.webp", "zingle-1-2-still.png"], Files(Cache));
        Assert.Equal(["zingle-1-2-animated.webp", "zingle-1-2-still.png"], Files(Widget));
        Assert.EndsWith("zingle-1-2-animated.webp", sync.Status!.AnimatedFile); // the tray shows the still for a WebP
    }

    [Fact]
    public async Task Null_clears_the_cache_and_the_copies_so_the_normal_logo_shows()
    {
        Serve(Body());
        var sync = Sync();
        await sync.CheckAsync(CancellationToken.None);
        Assert.True(System.IO.File.Exists(Path.Combine(Data, "zingle.json")));

        Serve("null");
        Assert.True(await sync.CheckAsync(CancellationToken.None));

        Assert.Null(sync.Status);
        Assert.Null(sync.Current);
        Assert.Empty(Files(Cache));
        Assert.Empty(Files(Widget));
        Assert.False(System.IO.File.Exists(Path.Combine(Data, "zingle.json")));
        Assert.Null(Sync().Status); // and stays gone after a restart
    }

    [Fact]
    public async Task Offline_the_cached_zingle_stays_and_shows_after_a_restart_without_downloading()
    {
        Serve(Body());
        await Sync().CheckAsync(CancellationToken.None);
        var before = _backend.Requests.Count;

        _backend.Respond = _ => throw new HttpRequestException("no network");
        var restarted = Sync();
        Assert.Equal(1, restarted.Status?.Id); // straight from zingle.json + the cache
        Assert.Equal(before, _backend.Requests.Count);

        Assert.False(await restarted.CheckAsync(CancellationToken.None)); // couldn't ask: retry sooner
        Assert.Equal(1, restarted.Status?.Id);
        Assert.Equal(2, Files(Cache).Length);
    }

    [Fact]
    public async Task A_member_who_signs_in_later_gets_their_copies_on_the_next_tick()
    {
        Serve(Body());
        var sync = Sync();
        await sync.CheckAsync(CancellationToken.None);
        Directory.Delete(Widget, recursive: true); // another member's session: an empty widget folder

        sync.RefreshWidget();

        Assert.Equal(["zingle-1-1793785141-animated.gif", "zingle-1-1793785141-still.png"], Files(Widget));
        Assert.True(System.IO.File.Exists(sync.Status!.StillFile));
    }

    [Fact]
    public async Task A_still_that_is_not_a_png_or_is_gone_means_the_normal_logo()
    {
        Serve(Body(), still: Gif);
        var sync = Sync();
        await sync.CheckAsync(CancellationToken.None);
        Assert.Null(sync.Status);

        _backend.Respond = r => r.Path == Endpoint ? FakeBackend.Json(HttpStatusCode.OK, Body())
            : new HttpResponseMessage(HttpStatusCode.NotFound);
        await sync.CheckAsync(CancellationToken.None);
        Assert.Null(sync.Status);
        Assert.Empty(Files(Widget));
    }

    [Fact]
    public async Task A_missing_animation_still_shows_the_still()
    {
        _backend.Respond = r => r.Path == Endpoint ? FakeBackend.Json(HttpStatusCode.OK, Body())
            : r.Path.EndsWith("/still/", StringComparison.Ordinal) ? File(Png)
            : new HttpResponseMessage(HttpStatusCode.NotFound);
        var sync = Sync();
        await sync.CheckAsync(CancellationToken.None);
        Assert.NotNull(sync.Status!.StillFile);
        Assert.Null(sync.Status.AnimatedFile);
    }

    [Fact]
    public void The_status_carries_the_zingle_and_an_older_service_reads_as_none()
    {
        var status = new AgentStatus(AgentState.Connected, "1.3.8", "PC-01", 7, null, null,
            Zingle: new ZingleStatus(1, "Diwali", @"C:\w\zingle-1-1-still.png", @"C:\w\zingle-1-1-animated.gif"));
        var json = PipeProtocol.Serialize(status);
        Assert.Contains("\"zingle\":{", json);
        Assert.Equal(status.Zingle, PipeProtocol.DeserializeStatus(json)!.Zingle);

        var older = PipeProtocol.Serialize(new AgentStatus(AgentState.Connected, "1.3.7", "PC-01", 7, null, null));
        Assert.Null(PipeProtocol.DeserializeStatus(older)!.Zingle);
    }
}

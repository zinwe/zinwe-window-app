using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Zinwe.Agent.Core.Backend;
using Zinwe.Agent.Core.Ipc;
using Zinwe.Agent.Core.Messages;
using Zinwe.Agent.Core.Pictures;

namespace Zinwe.Agent.Core.Tests;

/// <summary>
/// FR-44 · team pictures on the device API: the client calls (fake HTTP), the agent's sync (heartbeat in, the tray's
/// choice out through the outbox with retry, photo download + copies), older backends (no field / 404) and the pipe
/// and outbox staying compatible both ways.
/// </summary>
public sealed class PicturesApiTests : IDisposable
{
    private const string PicturePath = "/api/devices/me/picture/";
    private const string PhotoUrl = "http://127.0.0.1:8040/api/devices/pictures/abc.jpg";

    private readonly TempDir _dir = new();
    private readonly FakeBackend _backend = new();
    private DateTimeOffset _now = new(2026, 10, 30, 10, 0, 0, TimeSpan.Zero);

    public void Dispose() => _dir.Dispose();

    private string Widget => Path.Combine(_dir.Path, "widget");
    private string Data => Path.Combine(_dir.Path, "agent");

    /// <summary>A tiny JPEG-looking file (the agent checks the SOI marker; the server re-encodes anyway).</summary>
    private static byte[] Jpeg(byte fill = 7) => [0xFF, 0xD8, 0xFF, 0xE0, .. Enumerable.Repeat(fill, 200), 0xFF, 0xD9];

    private static HttpResponseMessage Bytes(byte[] body) =>
        new(HttpStatusCode.OK) { Content = new ByteArrayContent(body) { Headers = { ContentType = new("image/jpeg") } } };

    private static JsonElement Element(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private PictureSync Sync(MessageOutbox? outbox = null) =>
        new(_backend.Client(), outbox ?? new MessageOutbox(Widget), new LocalWidgetFolder(Widget),
            NullLogger<PictureSync>.Instance, Data, () => _now);

    // ---- Reading the picture object --------------------------------------------------------------

    [Fact]
    public void The_picture_field_tells_missing_from_none_and_reads_both_kinds()
    {
        Assert.Null(MemberPictures.Read(default, out var present));
        Assert.False(present); // an older backend: keep what we have

        Assert.Null(MemberPictures.Read(Element("null"), out present));
        Assert.True(present); // no picture: initials

        Assert.Equal(new MemberPicture("avatar", Key: "a07"), MemberPictures.Read(Element("""{"kind":"avatar","key":"a07"}"""), out _));
        var photo = MemberPictures.Read(Element($$"""{"kind":"photo","url":"{{PhotoUrl}}","updated_at":"2026-10-30T18:00:00Z"}"""), out _)!;
        Assert.True(photo.IsPhoto);
        Assert.Equal((PhotoUrl, new DateTimeOffset(2026, 10, 30, 18, 0, 0, TimeSpan.Zero)), (photo.Url, photo.UpdatedAt));

        // Shapes this agent doesn't know are "no picture", never an error.
        Assert.Null(MemberPictures.Read(Element("""{"kind":"hologram","key":"x"}"""), out _));
        Assert.Null(MemberPictures.Read(Element("""{"kind":"avatar","key":"../../etc"}"""), out _));
        Assert.Null(MemberPictures.Read(Element("""{"kind":"photo"}"""), out _));
        Assert.Null(MemberPictures.Read(Element("\"a07\""), out _));
    }

    [Fact]
    public void A_heartbeat_reply_carries_the_picture_and_an_older_one_simply_does_not()
    {
        var newer = JsonSerializer.Deserialize("""
            {"device_id": 7, "server_time": "2026-10-30T10:00:00Z", "next_heartbeat_seconds": 60, "policy_version": 3,
             "picture": {"kind": "avatar", "key": "a03"}}
            """, BackendJsonContext.Default.HeartbeatResponse)!;
        Assert.Equal("a03", MemberPictures.Read(newer.Picture, out var present)?.Key);
        Assert.True(present);

        var none = JsonSerializer.Deserialize("""
            {"device_id": 7, "server_time": "2026-10-30T10:00:00Z", "next_heartbeat_seconds": 60, "policy_version": 3, "picture": null}
            """, BackendJsonContext.Default.HeartbeatResponse)!;
        Assert.Null(MemberPictures.Read(none.Picture, out present));
        Assert.True(present);

        var older = JsonSerializer.Deserialize("""
            {"device_id": 7, "server_time": "2026-10-30T10:00:00Z", "next_heartbeat_seconds": 60, "policy_version": 3}
            """, BackendJsonContext.Default.HeartbeatResponse)!;
        Assert.Null(MemberPictures.Read(older.Picture, out present));
        Assert.False(present);
    }

    // ---- BackendClient ---------------------------------------------------------------------------

    [Fact]
    public async Task SetAvatar_puts_a_buffered_json_body_with_the_device_token()
    {
        _backend.Respond = _ => FakeBackend.Json(HttpStatusCode.OK, """{"picture": {"kind": "avatar", "key": "a07"}}""");

        var result = await _backend.Client().SetAvatarAsync("tok", "a07", CancellationToken.None);

        Assert.Equal((PictureOutcome.Saved, "a07"), (result.Outcome, result.Picture?.Key));
        var seen = _backend.Requests.Single();
        Assert.Equal(("PUT", PicturePath, "Device", "tok"), (seen.Method.Method, seen.Path, seen.AuthScheme, seen.AuthParameter));
        Assert.Equal("""{"kind":"avatar","key":"a07"}""", seen.Body);
        Assert.Equal(seen.Body.Length, seen.ContentLength); // not chunked (WSGI reads it empty)

        // The bare picture object reads too.
        _backend.Respond = _ => FakeBackend.Json(HttpStatusCode.OK, """{"kind": "avatar", "key": "a02"}""");
        Assert.Equal("a02", (await _backend.Client().SetAvatarAsync("tok", "a02", CancellationToken.None)).Picture?.Key);
    }

    [Fact]
    public async Task SetPhoto_sends_the_jpeg_as_a_multipart_file_with_a_content_length()
    {
        _backend.Respond = _ => FakeBackend.Json(HttpStatusCode.OK, $$$"""{"picture": {"kind": "photo", "url": "{{{PhotoUrl}}}"}}""");
        var jpeg = Jpeg();

        var result = await _backend.Client().SetPhotoAsync("tok", jpeg, CancellationToken.None);

        Assert.Equal((PictureOutcome.Saved, PhotoUrl), (result.Outcome, result.Picture?.Url));
        var seen = _backend.Requests.Single();
        Assert.Equal(("PUT", PicturePath, "multipart/form-data"), (seen.Method.Method, seen.Path, seen.ContentType));
        Assert.Equal(seen.Bytes!.Length, seen.ContentLength);
        Assert.Contains("name=file", seen.Body);
        Assert.Contains("filename=picture.jpg", seen.Body);
        Assert.Contains("Content-Type: image/jpeg", seen.Body);
        Assert.True(seen.Bytes!.AsSpan().IndexOf(jpeg) >= 0); // the file goes up byte for byte
    }

    [Fact]
    public async Task RemovePicture_deletes_and_a_204_means_back_to_initials()
    {
        _backend.Respond = _ => new HttpResponseMessage(HttpStatusCode.NoContent);

        var result = await _backend.Client().RemovePictureAsync("tok", CancellationToken.None);

        Assert.Equal((PictureOutcome.Saved, (MemberPicture?)null), (result.Outcome, result.Picture));
        Assert.Equal(("DELETE", PicturePath), (_backend.Requests.Single().Method.Method, _backend.Requests.Single().Path));
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.MethodNotAllowed)]
    public async Task An_older_backend_answers_404_and_that_means_pictures_are_off(HttpStatusCode code)
    {
        _backend.Respond = _ => FakeBackend.Json(code, "{\"detail\": \"Not found.\"}");

        Assert.Equal(PictureOutcome.Unsupported, (await _backend.Client().SetAvatarAsync("tok", "a01", CancellationToken.None)).Outcome);
        Assert.Equal(PictureOutcome.Unsupported, (await _backend.Client().RemovePictureAsync("tok", CancellationToken.None)).Outcome);
    }

    [Fact]
    public async Task A_refusal_is_final_and_keeps_the_backends_code()
    {
        _backend.Respond = _ => FakeBackend.Json(HttpStatusCode.BadRequest, """{"code": "too_large", "detail": "The photo is over 150 KB."}""");

        var result = await _backend.Client().SetPhotoAsync("tok", Jpeg(), CancellationToken.None);

        Assert.Equal((PictureOutcome.Refused, "too_large", "The photo is over 150 KB."), (result.Outcome, result.Code, result.Detail));
    }

    [Theory]
    [InlineData((HttpStatusCode)429)]
    [InlineData(HttpStatusCode.BadGateway)]
    public async Task Throttling_and_server_errors_throw_so_the_change_is_retried(HttpStatusCode code)
    {
        _backend.Respond = _ => new HttpResponseMessage(code);

        await Assert.ThrowsAsync<HttpRequestException>(() => _backend.Client().SetAvatarAsync("tok", "a01", CancellationToken.None));
    }

    [Fact]
    public async Task A_photo_downloads_without_the_device_token_and_only_if_it_is_a_jpeg()
    {
        var jpeg = Jpeg();
        _backend.Respond = _ => Bytes(jpeg);
        Assert.Equal(jpeg, await _backend.Client().DownloadPictureAsync(PhotoUrl, CancellationToken.None));
        Assert.Equal(("/api/devices/pictures/abc.jpg", (string?)null), (_backend.Requests[0].Path, _backend.Requests[0].AuthScheme));

        // A path resolves against the backend - still without the token (the URL is signed).
        await _backend.Client().DownloadPictureAsync("/api/devices/pictures/def.jpg", CancellationToken.None);
        Assert.Equal(("127.0.0.1", (string?)null), (_backend.Requests[1].Host, _backend.Requests[1].AuthScheme));

        _backend.Respond = _ => Bytes(Encoding.UTF8.GetBytes("<html>not a picture</html>"));
        Assert.Null(await _backend.Client().DownloadPictureAsync(PhotoUrl, CancellationToken.None));

        _backend.Respond = _ => Bytes([0xFF, 0xD8, 0xFF, .. new byte[BackendClient.MaxPictureDownloadBytes]]);
        Assert.Null(await _backend.Client().DownloadPictureAsync(PhotoUrl, CancellationToken.None)); // too big for a 256 px JPEG

        _backend.Respond = _ => new HttpResponseMessage(HttpStatusCode.NotFound);
        Assert.Null(await _backend.Client().DownloadPictureAsync(PhotoUrl, CancellationToken.None)); // removed meanwhile

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _backend.Client().DownloadPictureAsync("http://example.com/p.jpg", CancellationToken.None)); // https only off-box
    }

    // ---- PictureSync -------------------------------------------------------------------------------

    [Fact]
    public void The_heartbeat_sets_the_picture_and_the_version_moves_only_on_change()
    {
        var sync = Sync();
        Assert.Equal(PictureBackend.Unknown, sync.Status.Backend);
        Assert.False(sync.Status.CanChange); // nothing known yet: no control

        sync.OnHeartbeat(Element("""{"kind":"avatar","key":"a05"}"""));
        var first = sync.Status;
        Assert.Equal((PictureBackend.On, "avatar", "a05", false), (first.Backend, first.Kind, first.Key, first.Pending));
        Assert.True(first.CanChange);

        sync.OnHeartbeat(Element("""{"kind":"avatar","key":"a05"}"""));
        Assert.Equal(first.Version, sync.Status.Version);

        sync.OnHeartbeat(Element("null")); // the owner removed it in the dashboard
        Assert.Equal((PictureBackend.On, (string?)null), (sync.Status.Backend, sync.Status.Kind));
        Assert.True(sync.Status.Version > first.Version);
    }

    [Fact]
    public void A_heartbeat_without_the_field_is_an_older_backend_and_the_tray_gets_no_control()
    {
        var sync = Sync();

        sync.OnHeartbeat(default);

        Assert.Equal(PictureBackend.Off, sync.Status.Backend);
        Assert.False(sync.Status.CanChange);
        Assert.False(sync.Request(new PictureChoice(PictureChoice.Avatar, "a01")).Accepted);
        Assert.Null(new MessageOutbox(Widget).PendingPicture());

        sync.OnHeartbeat(Element("null")); // the backend was upgraded
        Assert.True(sync.Status.CanChange);
    }

    [Fact]
    public async Task A_choice_shows_at_once_waits_in_the_outbox_and_is_sent_on_the_next_round()
    {
        var outbox = new MessageOutbox(Widget);
        var sync = Sync(outbox);
        sync.OnHeartbeat(Element("null"));

        var ack = sync.Request(new PictureChoice(PictureChoice.Avatar, "a09"));

        Assert.True(ack.Accepted);
        Assert.Equal(("avatar", "a09", true), (sync.Status.Kind, sync.Status.Key, sync.Status.Pending));
        Assert.Equal(("picture", "avatar", "a09"), (outbox.PendingPicture()!.Kind, outbox.PendingPicture()!.Action, outbox.PendingPicture()!.Body));
        Assert.True(sync.HasWork);

        _backend.Respond = _ => FakeBackend.Json(HttpStatusCode.OK, """{"picture": {"kind": "avatar", "key": "a09"}}""");
        await sync.SyncAsync("tok", CancellationToken.None);

        Assert.Equal("""{"kind":"avatar","key":"a09"}""", _backend.Requests.Single().Body);
        Assert.Null(outbox.PendingPicture());
        Assert.Equal(("avatar", "a09", false), (sync.Status.Kind, sync.Status.Key, sync.Status.Pending));
        Assert.False(sync.HasWork);
    }

    [Fact]
    public async Task Offline_the_choice_stays_queued_backs_off_and_goes_out_later()
    {
        var outbox = new MessageOutbox(Widget);
        var sync = Sync(outbox);
        sync.OnHeartbeat(Element("null"));
        sync.Request(new PictureChoice(PictureChoice.Remove));
        _backend.Respond = _ => throw new HttpRequestException("offline");

        await sync.SyncAsync("tok", CancellationToken.None);

        Assert.NotNull(outbox.PendingPicture());
        Assert.True(sync.Status.Pending);
        Assert.False(sync.HasWork); // backing off: the chat loop isn't woken every 2 s
        await sync.SyncAsync("tok", CancellationToken.None); // the regular 15 s round, still inside the backoff
        Assert.Single(_backend.Requests);

        _now += PictureSync.FirstRetry;
        Assert.True(sync.HasWork);
        _backend.Respond = _ => new HttpResponseMessage(HttpStatusCode.NoContent);
        await sync.SyncAsync("tok", CancellationToken.None);
        Assert.Equal("DELETE", _backend.Requests[^1].Method.Method);
        Assert.Null(outbox.PendingPicture());
        Assert.False(sync.Status.Pending);
    }

    [Fact]
    public async Task A_refused_choice_is_dropped_and_the_real_picture_shows_again()
    {
        var outbox = new MessageOutbox(Widget);
        var sync = Sync(outbox);
        sync.OnHeartbeat(Element("""{"kind":"avatar","key":"a01"}"""));
        sync.Request(new PictureChoice(PictureChoice.Avatar, "a99"));
        Assert.Equal("a99", sync.Status.Key);
        _backend.Respond = _ => FakeBackend.Json(HttpStatusCode.BadRequest, """{"code": "unknown_avatar", "detail": "No such avatar."}""");

        await sync.SyncAsync("tok", CancellationToken.None);

        Assert.Null(outbox.PendingPicture());
        Assert.Equal(("a01", false), (sync.Status.Key, sync.Status.Pending));
    }

    [Fact]
    public async Task A_404_from_an_older_backend_turns_pictures_off_and_drops_the_choice()
    {
        var outbox = new MessageOutbox(Widget);
        var sync = Sync(outbox);
        sync.Request(new PictureChoice(PictureChoice.Avatar, "a02")); // backend not known yet (no heartbeat)
        _backend.Respond = _ => new HttpResponseMessage(HttpStatusCode.NotFound);

        await sync.SyncAsync("tok", CancellationToken.None);

        Assert.Null(outbox.PendingPicture());
        Assert.Equal((PictureBackend.Off, (string?)null, false), (sync.Status.Backend, sync.Status.Kind, sync.Status.CanChange));
    }

    [Fact]
    public void The_newest_choice_wins_and_bad_ones_are_refused_before_queueing()
    {
        var outbox = new MessageOutbox(Widget);
        var sync = Sync(outbox);
        sync.OnHeartbeat(Element("null"));

        sync.Request(new PictureChoice(PictureChoice.Avatar, "a01"));
        sync.Request(new PictureChoice(PictureChoice.Avatar, "a02"));

        Assert.Equal("a02", Assert.Single(outbox.All()).Body);
        Assert.False(sync.Request(new PictureChoice(PictureChoice.Avatar, "A B")).Accepted);
        Assert.False(sync.Request(new PictureChoice(PictureChoice.Photo, Jpeg: Convert.ToBase64String("not a jpeg"u8.ToArray()))).Accepted);
        Assert.False(sync.Request(new PictureChoice(PictureChoice.Photo,
            Jpeg: Convert.ToBase64String([0xFF, 0xD8, 0xFF, .. new byte[PictureChoice.MaxJpegBytes]]))).Accepted);
        Assert.False(sync.Request(new PictureChoice("hologram")).Accepted);
        Assert.False(sync.Request(null).Accepted);
        Assert.Equal("a02", Assert.Single(outbox.All()).Body);
    }

    [Fact]
    public async Task A_photo_is_uploaded_shown_from_its_copy_then_from_the_downloaded_one()
    {
        var outbox = new MessageOutbox(Widget);
        var sync = Sync(outbox);
        sync.OnHeartbeat(Element("null"));
        var mine = Jpeg(1);

        Assert.True(sync.Request(new PictureChoice(PictureChoice.Photo, Jpeg: Convert.ToBase64String(mine))).Accepted);

        // Shown at once, from a copy in the member's widget folder (the tray can't read the service's data folder).
        Assert.Equal(("photo", true), (sync.Status.Kind, sync.Status.Pending));
        Assert.StartsWith(Widget, sync.Status.File);
        Assert.Equal(mine, File.ReadAllBytes(sync.Status.File!));

        var stored = Jpeg(2); // the server re-encodes
        _backend.Respond = r => r.Method == HttpMethod.Put
            ? FakeBackend.Json(HttpStatusCode.OK, $$$"""{"picture": {"kind": "photo", "url": "{{{PhotoUrl}}}"}}""")
            : Bytes(stored);
        await sync.SyncAsync("tok", CancellationToken.None);

        Assert.Equal(["PUT", "GET"], _backend.Requests.Select(r => r.Method.Method));
        Assert.Null(_backend.Requests[1].AuthScheme);
        Assert.False(sync.Status.Pending);
        Assert.Equal(stored, File.ReadAllBytes(sync.Status.File!));
        Assert.Single(Directory.GetFiles(Widget, PictureSync.WidgetFilePrefix + "*")); // the pending copy is gone
        Assert.Single(Directory.GetFiles(Path.Combine(Data, "pictures")));
    }

    [Fact]
    public async Task The_owner_changing_the_photo_downloads_the_new_url_once_and_deletes_the_old_files()
    {
        var sync = Sync();
        var first = Jpeg(3);
        var second = Jpeg(4);
        _backend.Respond = r => Bytes(r.Path.EndsWith("abc.jpg", StringComparison.Ordinal) ? first : second);

        sync.OnHeartbeat(Element($$"""{"kind":"photo","url":"{{PhotoUrl}}"}"""));
        Assert.True(sync.HasWork); // wakes the chat loop to fetch it
        await sync.SyncAsync("tok", CancellationToken.None);
        var shown = sync.Status;
        Assert.Equal(first, File.ReadAllBytes(shown.File!));

        await sync.SyncAsync("tok", CancellationToken.None);
        Assert.Single(_backend.Requests); // cached: not downloaded again
        Assert.Equal(shown.Version, sync.Status.Version);

        sync.OnHeartbeat(Element("""{"kind":"photo","url":"http://127.0.0.1:8040/api/devices/pictures/xyz.jpg"}"""));
        await sync.SyncAsync("tok", CancellationToken.None);

        Assert.Equal(second, File.ReadAllBytes(sync.Status.File!));
        Assert.NotEqual(shown.File, sync.Status.File); // a new name, so the tray never shows a stale image
        Assert.True(sync.Status.Version > shown.Version);
        Assert.Single(Directory.GetFiles(Widget, PictureSync.WidgetFilePrefix + "*"));
        Assert.Single(Directory.GetFiles(Path.Combine(Data, "pictures")));
    }

    [Fact]
    public async Task The_picture_and_a_waiting_choice_survive_a_restart_and_Clear_forgets_both()
    {
        var outbox = new MessageOutbox(Widget);
        var sync = Sync(outbox);
        sync.OnHeartbeat(Element("""{"kind":"avatar","key":"a11"}"""));
        sync.Request(new PictureChoice(PictureChoice.Photo, Jpeg: Convert.ToBase64String(Jpeg(5))));

        var restarted = Sync(outbox); // offline after a restart
        Assert.Equal((PictureBackend.On, "avatar", "a11"), (restarted.Status.Backend, restarted.Status.Kind, restarted.Status.Key));
        _backend.Respond = _ => throw new HttpRequestException("offline");
        await restarted.SyncAsync("tok", CancellationToken.None);
        Assert.Equal(("photo", true), (restarted.Status.Kind, restarted.Status.Pending)); // the waiting photo, from the outbox
        Assert.True(File.Exists(restarted.Status.File));

        restarted.Clear(); // removed / another member
        Assert.Equal((PictureBackend.Unknown, (string?)null, false), (restarted.Status.Backend, restarted.Status.Kind, restarted.Status.Pending));
        Assert.Null(outbox.PendingPicture());
        Assert.Empty(Directory.GetFiles(Widget, PictureSync.WidgetFilePrefix + "*"));
        Assert.Equal(PictureBackend.Unknown, Sync(outbox).Status.Backend);
    }

    // ---- Outbox compatibility ------------------------------------------------------------------------

    [Fact]
    public void Picture_changes_wait_in_the_outbox_but_are_never_chat_lines_and_keep_their_own_pace()
    {
        var outbox = new MessageOutbox(Widget);
        outbox.ReplacePicture(PictureChoice.Avatar, "a01");

        Assert.Empty(outbox.Pending());
        Assert.Empty(outbox.PendingFeedback());
        Assert.Empty(outbox.PendingTaskActions());
        Assert.False(outbox.HasPendingOtherThanPicture);
        var sync = new MessageSync(_backend.Client(), new NotificationWriter(Widget), outbox, NullLogger<MessageSync>.Instance);
        Assert.False(sync.HasPendingReplies); // a waiting picture doesn't wake the chat loop every 2 s

        outbox.Append("hello");
        Assert.True(sync.HasPendingReplies);
        Assert.Equal(["hello"], outbox.Pending().Select(p => p.Body));
    }

    [Fact]
    public void An_outbox_written_by_an_older_build_still_reads_next_to_a_picture()
    {
        Directory.CreateDirectory(Widget);
        File.WriteAllText(Path.Combine(Widget, "messages-outbox.json"), """
            {"Items": [{"Id": "a", "Body": "hi", "CreatedAtUtc": "2026-10-30T09:00:00Z", "Kind": "reply", "Rating": null},
                       {"Id": "t", "Body": "", "CreatedAtUtc": "2026-10-30T09:01:00Z", "Kind": "task_action", "TaskId": 4, "Action": "start"},
                       {"Id": "p", "Body": "a04", "CreatedAtUtc": "2026-10-30T09:02:00Z", "Kind": "picture", "Action": "avatar"}]}
            """);
        var outbox = new MessageOutbox(Widget);

        Assert.Equal(["a"], outbox.Pending().Select(p => p.Id));
        Assert.Equal(["t"], outbox.PendingTaskActions().Select(p => p.Id));
        Assert.Equal("p", outbox.PendingPicture()!.Id);

        outbox.ReplacePicture(PictureChoice.Remove, ""); // replaces only the picture item
        Assert.Equal(3, outbox.All().Count);
        Assert.Equal(PictureChoice.Remove, outbox.PendingPicture()!.Action);
    }

    // ---- The pipe ------------------------------------------------------------------------------------

    [Fact]
    public async Task The_picture_request_round_trips_on_the_pipe_with_a_photo_in_it()
    {
        var photo = Convert.ToBase64String([0xFF, 0xD8, 0xFF, .. new byte[60 * 1024]]); // the tray's largest normal photo
        var line = PipeProtocol.Serialize(new PipeProtocol.Request(PipeProtocol.RequestTypes.Picture,
            Picture: new PictureChoice(PictureChoice.Photo, Jpeg: photo)));
        Assert.True(line.Length > 32_768); // a 1.3.1 service reads at most 32 KB: it closes the pipe (= not supported)

        using var reader = new StreamReader(new MemoryStream(Encoding.UTF8.GetBytes(line + "\n")));
        var request = await PipeProtocol.ReadRequestAsync(reader, CancellationToken.None);

        Assert.Equal((PipeProtocol.RequestTypes.Picture, PictureChoice.Photo, photo),
            (request!.Type, request.Picture!.Action, request.Picture.Jpeg));
        // Other requests don't carry it (and services without pictures ignore the field).
        Assert.Null(PipeProtocol.DeserializeRequest(PipeProtocol.Serialize(new PipeProtocol.Request(PipeProtocol.RequestTypes.Status)))!.Picture);
    }

    [Fact]
    public void The_status_carries_the_picture_and_an_older_service_simply_does_not()
    {
        var newer = new AgentStatus(AgentState.Connected, "1.3.2", "PC-01", 7, null, null, TasksVersion: 1,
            Picture: new PictureStatus(PictureBackend.On, "photo", null, @"C:\Users\m\AppData\Local\Zinwe\member-picture-1.jpg", 5));
        var back = PipeProtocol.DeserializeStatus(PipeProtocol.Serialize(newer))!;
        Assert.Equal(newer.Picture, back.Picture);
        Assert.True(back.Picture!.CanChange);

        var older = PipeProtocol.DeserializeStatus(
            """{"state":"Connected","agentVersion":"1.3.1","hostname":"PC-01","deviceId":7,"lastHeartbeatAt":null,"message":null,"tasksVersion":2}""");
        Assert.Null(older!.Picture); // the tray hides Change picture

        // A picture object with fields from the future still reads.
        var future = PipeProtocol.DeserializeStatus(
            """{"state":"Connected","agentVersion":"9","hostname":"PC","deviceId":7,"lastHeartbeatAt":null,"message":null,"picture":{"backend":"on","kind":"avatar","key":"a01","version":3,"frame":"gold"}}""");
        Assert.Equal(("a01", 3), (future!.Picture!.Key, future.Picture.Version));
    }

    [Fact]
    public void A_service_without_pictures_answers_not_supported()
    {
        // 1.3.1's answer to a request type it doesn't know; the tray shows it as "couldn't change your picture".
        var ack = PipeProtocol.DeserializeAck(PipeProtocol.Serialize(PipeProtocol.NotSupported))!;
        Assert.False(ack.Accepted);
        Assert.Equal(PipeProtocol.NotSupportedMessage, ack.Message);
    }

    [Fact]
    public async Task Client_returns_null_for_a_picture_when_the_service_is_not_running()
    {
        Assert.Null(await AgentPipeClient.ExchangeAsync("Zinwe.Agent.test-" + Guid.NewGuid().ToString("N"),
            new PipeProtocol.Request(PipeProtocol.RequestTypes.Picture, Picture: new PictureChoice(PictureChoice.Remove)),
            TimeSpan.FromMilliseconds(200), CancellationToken.None));
    }
}

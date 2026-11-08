using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Zinwe.Agent.Core.Backend;
using Zinwe.Agent.Core.Chat;
using Zinwe.Agent.Core.Ipc;
using Zinwe.Agent.Core.Messages;

namespace Zinwe.Agent.Core.Tests;

/// <summary>
/// v1.5 · file sharing in chat on the agent: the rules and words (size, risky, names, the Downloads clash and the Mark of the
/// Web), the FileJSON contract (null / present / expired, read leniently), the chat_file outbox item, and the service's send
/// (upload → line with file_id → copy deleted; back-off; final refusals; a re-upload when the server lost it) and fetch
/// (into Downloads\Zinwe; an image preview) against a fake backend.
/// </summary>
public sealed class ChatFilesTests : IDisposable
{
    private const string FileId = "6f1c2a8e-1d4b-4c55-9a77-0b1f3c2d4e5f";

    private const string FileJson = """
        {"id": "6f1c2a8e-1d4b-4c55-9a77-0b1f3c2d4e5f", "name": "Quote-Lumen.pdf", "size": 2202009, "mime": "application/pdf",
         "kind": "file", "caption": "Final version", "risky": false, "expires_at": "2026-11-16T10:20:00Z", "expired": false,
         "url": "https://dev.backend.zinwe.io/api/devices/chat/files/6f1c2a8e-1d4b-4c55-9a77-0b1f3c2d4e5f/"}
        """;

    private readonly TempDir _dir = new();
    private readonly FakeBackend _backend = new();
    private DateTimeOffset _now = new(2026, 11, 9, 10, 0, 0, TimeSpan.Zero);

    public void Dispose() => _dir.Dispose();

    private string Widget => Path.Combine(_dir.Path, "widget");
    private string Downloads => Path.Combine(_dir.Path, "downloads");
    private string Data => Path.Combine(_dir.Path, "data");

    private MessageOutbox Outbox() => new(Widget);

    private ChatFileTransfer Transfer() => new(new HttpClient(_backend) { BaseAddress = new Uri("http://127.0.0.1:8000/") });

    private ChatFileSync Sync(MessageOutbox? outbox = null) => new(_backend.Client(), Transfer(), outbox ?? Outbox(),
        new LocalWidgetFolder(Widget), new LocalDownloadsFolder(Downloads), NullLogger<ChatFileSync>.Instance, Data, () => _now);

    /// <summary>The tray's copy of a picked file in outbox-files\&lt;guid&gt;\&lt;name&gt;.</summary>
    private string Copy(string name = "Quote-Lumen.pdf", int bytes = 2048)
    {
        var folder = Path.Combine(Widget, ChatFiles.OutboxFolder, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, name);
        File.WriteAllBytes(path, Enumerable.Repeat((byte)'x', bytes).ToArray());
        return path;
    }

    private int Calls(string method, string path) => _backend.Requests.Count(r => r.Method.Method == method && r.Path == path);

    private static HttpResponseMessage Info(bool allowed = true) =>
        FakeBackend.Json(HttpStatusCode.OK, $$"""{"allowed": {{(allowed ? "true" : "false")}}, "max_bytes": 26214400, "keep_days": 7}""");

    // ---- Words and rules ------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(1023, "1023 B")]
    [InlineData(1024, "1 KB")]
    [InlineData(348_160, "340 KB")]
    [InlineData(2_202_009, "2.1 MB")]
    [InlineData(1024 * 1024, "1 MB")]
    [InlineData(26_214_400, "25 MB")]
    public void Sizes_read_like_the_server_writes_them(long bytes, string text) => Assert.Equal(text, ChatFiles.FormatSize(bytes));

    [Theory]
    [InlineData("setup.exe", true)]
    [InlineData("SETUP.EXE", true)]
    [InlineData("tool.ps1", true)]
    [InlineData("invoice.pdf.js", true)]
    [InlineData("shortcut.lnk", true)]
    [InlineData("app.msix", true)]
    [InlineData("run.bat. ", true)] // Windows drops trailing dots and spaces: still a batch file
    [InlineData("report.pdf", false)]
    [InlineData("exe", false)]
    [InlineData("logo.svg", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Program_files_are_risky(string? name, bool risky) => Assert.Equal(risky, ChatFiles.IsRisky(name));

    [Fact]
    public void A_file_the_server_calls_risky_is_risky_whatever_its_name()
    {
        Assert.True(new ChatFile(FileId, "notes.txt", Risky: true).IsRisky);
        Assert.True(new ChatFile(FileId, "x.EXE").IsRisky);
        Assert.False(new ChatFile(FileId, "x.png").IsRisky);
    }

    [Fact]
    public void A_taken_name_in_downloads_gets_a_number_like_explorer_gives_it()
    {
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"C:\D\Zinwe\report.pdf", @"C:\D\Zinwe\report (2).pdf" };
        Assert.Equal(@"C:\D\Zinwe\report (3).pdf", ChatFiles.FreeName(@"C:\D\Zinwe", "report.pdf", taken.Contains));
        Assert.Equal(@"C:\D\Zinwe\other.pdf", ChatFiles.FreeName(@"C:\D\Zinwe", "other.pdf", taken.Contains));
        Assert.Equal(@"C:\D\Zinwe\README (2)", ChatFiles.FreeName(@"C:\D\Zinwe", "README", new HashSet<string> { @"C:\D\Zinwe\README" }.Contains));
    }

    [Theory]
    [InlineData(@"..\..\Windows\evil.dll", "evil.dll")]
    [InlineData("a/b/c.txt", "c.txt")]
    [InlineData("what?.pdf", "what_.pdf")]
    [InlineData("CON.txt", "_CON.txt")]
    [InlineData("nul", "_nul")]
    [InlineData("name. . ", "name")]
    [InlineData("", "file")]
    [InlineData("..", "file")]
    public void Names_are_made_safe_before_a_file_is_made(string name, string safe) => Assert.Equal(safe, ChatFiles.SafeName(name));

    [Fact]
    public void A_very_long_name_keeps_its_extension()
    {
        var safe = ChatFiles.SafeName(new string('a', 300) + ".docx");
        Assert.Equal(200, safe.Length);
        Assert.EndsWith(".docx", safe, StringComparison.Ordinal);
    }

    [Fact]
    public void A_saved_download_never_overwrites_and_is_marked_as_from_the_internet()
    {
        var folder = Path.Combine(Downloads, "Zinwe");
        var first = ChatFiles.SaveNew(folder, "report.pdf", new MemoryStream(Encoding.UTF8.GetBytes("one")));
        var second = ChatFiles.SaveNew(folder, "report.pdf", new MemoryStream(Encoding.UTF8.GetBytes("two")));

        Assert.Equal(Path.Combine(folder, "report.pdf"), first);
        Assert.Equal(Path.Combine(folder, "report (2).pdf"), second);
        Assert.Equal("one", File.ReadAllText(first));
        Assert.Equal("two", File.ReadAllText(second));
        Assert.Contains("ZoneId=3", File.ReadAllText(second + ":Zone.Identifier"), StringComparison.Ordinal);
    }

    [Fact]
    public void Kept_until_is_the_local_day_and_short_month()
    {
        var at = new DateTimeOffset(2026, 11, 16, 12, 0, 0, TimeSpan.Zero);
        Assert.Equal("Kept until " + at.ToLocalTime().ToString("d MMM", System.Globalization.CultureInfo.InvariantCulture), ChatFiles.KeptUntil(at));
        Assert.Equal("This is a program file. Open it only if you trust Priya.", ChatFiles.RiskyText("Priya"));
    }

    [Theory]
    [InlineData("📎 Quote-Lumen.pdf (2.1 MB)\nFinal version", "Quote-Lumen.pdf")]
    [InlineData("📎 notes (draft).txt (340 KB)", "notes (draft).txt")]
    [InlineData("Hello", null)]
    public void The_fallback_text_is_read_back_for_the_list_preview(string body, string? name) => Assert.Equal(name, ChatFiles.FallbackName(body));

    [Fact]
    public void The_list_preview_of_a_file_line_is_the_clip_and_its_name()
    {
        var withFile = new TeamMessage(1, Body: "📎 Quote-Lumen.pdf (2.1 MB)", File: new ChatFile(FileId, "Quote-Lumen.pdf"));
        Assert.Equal("📎 Quote-Lumen.pdf", ChatFiles.PreviewLine(withFile));
        Assert.Equal("📎 a.png", ChatFiles.PreviewLine(new TeamMessage(2, Body: "📎 a.png (12 KB)\nlook")));
        Assert.Equal("Hi", ChatFiles.PreviewLine(new TeamMessage(3, Body: "Hi")));
    }

    [Fact]
    public void Images_are_known_by_their_bytes_and_svg_is_not_one()
    {
        Assert.Equal(".png", ChatFiles.SniffImage([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0]));
        Assert.Equal(".jpg", ChatFiles.SniffImage([0xFF, 0xD8, 0xFF, 0xE0]));
        Assert.Equal(".gif", ChatFiles.SniffImage("GIF89a.."u8));
        Assert.Equal(".webp", ChatFiles.SniffImage("RIFF\0\0\0\0WEBPVP8 "u8));
        Assert.Null(ChatFiles.SniffImage("<svg xmlns='http://www.w3.org/2000/svg'/>"u8));
    }

    // ---- The JSON contract ----------------------------------------------------------------------------------------

    [Fact]
    public void A_line_reads_file_null_present_and_expired()
    {
        var lines = ChatJson.Messages($$"""
            [{"id": 1, "body": "Hi", "file": null, "at": "2026-11-09T09:00:00Z"},
             {"id": 2, "body": "📎 Quote-Lumen.pdf (2.1 MB)\nFinal version", "file": {{FileJson}}, "at": "2026-11-09T09:01:00Z"},
             {"id": 3, "body": "📎 old.zip (3 MB)", "at": "2026-11-09T09:02:00Z",
              "file": {"id": "{{FileId}}", "name": "old.zip", "size": 3145728, "kind": "file", "risky": false, "expired": true, "url": null} },
             {"id": 4, "body": "odd", "file": "not an object", "at": "2026-11-09T09:03:00Z"}]
            """);

        Assert.Equal(4, lines.Count);
        Assert.Null(lines[0].File);
        var f = lines[1].File!;
        Assert.Equal(FileId, f.Id);
        Assert.Equal("Quote-Lumen.pdf", f.Name);
        Assert.Equal(2_202_009, f.Size);
        Assert.Equal("Final version", f.Caption);
        Assert.False(f.IsImage);
        Assert.Equal(new DateTimeOffset(2026, 11, 16, 10, 20, 0, TimeSpan.Zero), f.ExpiresAt);
        Assert.True(f.AvailableAt(_now));
        Assert.False(f.AvailableAt(new DateTimeOffset(2026, 11, 17, 0, 0, 0, TimeSpan.Zero))); // past its keep date
        var old = lines[2].File!;
        Assert.True(old.Expired);
        Assert.Null(old.Url);
        Assert.False(old.AvailableAt(_now));
        Assert.Null(lines[3].File); // an odd shape is "no file", the line still shows
    }

    [Fact]
    public async Task The_admin_chat_reads_the_file_on_a_line_and_an_older_backend_without_it()
    {
        _backend.Respond = _ => FakeBackend.Json(HttpStatusCode.OK, $$"""
            [{"id": 7, "title": "", "body": "📎 Quote-Lumen.pdf (2.1 MB)", "severity": "info", "created_at": "2026-11-09T09:00:00Z",
              "sender": "member", "file": {{FileJson}}},
             {"id": 8, "title": "Hi", "body": "Hello", "severity": "info", "created_at": "2026-11-09T09:01:00Z"},
             {"id": 9, "title": "", "body": "x", "severity": "info", "created_at": "2026-11-09T09:02:00Z", "file": 12}]
            """);
        var messages = await _backend.Client().GetMessagesAsync("tok", CancellationToken.None);

        Assert.Equal("Quote-Lumen.pdf", messages[0].File!.Name);
        Assert.Null(messages[1].File);
        Assert.Null(messages[2].File);
    }

    [Fact]
    public void A_file_survives_the_pipe_and_the_notifications_file()
    {
        var line = ChatJson.Messages($$"""[{"id": 2, "body": "x", "file": {{FileJson}}}]""")[0];
        var reply = new ChatReply(true, TaskBackend.On, Thread: new ChatThread("conv:3", [line]));
        var back = PipeProtocol.DeserializeChat(PipeProtocol.Serialize(reply))!;
        Assert.Equal(line.File, back.Thread!.Messages![0].File);

        var writer = new NotificationWriter(Widget);
        var msg = new AgentMessage(7, "", "📎 Quote-Lumen.pdf (2.1 MB)", "info", _now, "member", File: line.File);
        writer.MergeChat([msg]);
        var json = File.ReadAllText(writer.FilePath);
        Assert.Contains("\"expires_at\"", json, StringComparison.Ordinal);
        Assert.Contains("Quote-Lumen.pdf", json, StringComparison.Ordinal);

        // Later the file expired: the line already there takes the new state.
        writer.MergeChat([msg with { File = line.File! with { Expired = true, Url = null } }]);
        Assert.Contains("\"expired\": true", File.ReadAllText(writer.FilePath), StringComparison.Ordinal);
    }

    [Fact]
    public void Files_info_reads_on_off_and_nonsense()
    {
        Assert.Equal(new ChatFilesInfo(true, 26_214_400, 7), ChatFiles.InfoFromJson("""{"allowed": true, "max_bytes": 26214400, "keep_days": 7}"""));
        Assert.False(ChatFiles.InfoFromJson("""{"allowed": false}""")!.Allowed);
        Assert.Null(ChatFiles.InfoFromJson("[]"));
        Assert.Null(ChatFiles.InfoFromJson("<html>"));
    }

    [Fact]
    public void A_files_reply_crosses_the_pipe_and_an_older_service_reads_as_not_supported()
    {
        var reply = new ChatFilesReply(true, new ChatFilesInfo(true), [new FileState(FileId, FileState.Done, @"C:\Users\x\Downloads\Zinwe\a.pdf")], [], 3);
        Assert.Equal(reply.Downloads![0], AgentPipeClient.ParseFiles(PipeProtocol.Serialize(reply))!.Downloads![0]);
        Assert.Null(AgentPipeClient.ParseFiles(PipeProtocol.Serialize(PipeProtocol.NotSupported)));
        Assert.Null(AgentPipeClient.ParseFiles(null));
    }

    // ---- The outbox item ------------------------------------------------------------------------------------------

    [Fact]
    public void A_chat_file_item_keeps_its_copy_name_size_and_caption_and_only_the_files_sync_sends_it()
    {
        var outbox = Outbox();
        var path = Copy();
        var item = outbox.AppendChatFile("conv:3", path, "Quote-Lumen.pdf", 2048, "  Final version ")!;

        var back = Assert.Single(outbox.PendingFiles());
        Assert.Equal(item.Id, back.Id);
        Assert.Equal(OutboxReply.ChatFileKind, back.Kind);
        Assert.True(back.IsChatFile);
        Assert.Equal("conv:3", back.ChatKey);
        Assert.Equal(path, back.FilePath);
        Assert.Equal("Quote-Lumen.pdf", back.FileName);
        Assert.Equal(2048, back.FileSize);
        Assert.Equal("Final version", back.Body);

        // Not an admin reply, not a team chat item, and it doesn't make the chat loop spin while it uploads.
        Assert.Empty(outbox.Pending());
        Assert.Empty(outbox.PendingChat());
        Assert.False(outbox.HasPendingOtherThanPicture);

        Assert.True(outbox.Update(item.Id, i => i with { FileId = FileId, UploadedAt = _now }));
        Assert.Equal(FileId, outbox.PendingFiles()[0].FileId);
        Assert.False(outbox.Update("nope", i => i));
    }

    [Fact]
    public void An_older_outbox_file_without_the_new_fields_still_reads()
    {
        Directory.CreateDirectory(Widget);
        File.WriteAllText(Path.Combine(Widget, "messages-outbox.json"),
            """{"Items": [{"Id": "a1", "Body": "hi", "CreatedAtUtc": "2026-11-09T09:00:00Z"}]}""");
        var outbox = Outbox();
        Assert.Single(outbox.Pending());
        Assert.Empty(outbox.PendingFiles());
    }

    // ---- Sending ---------------------------------------------------------------------------------------------------

    private void ServeSend(Func<SeenRequest, HttpResponseMessage?>? more = null) => _backend.Respond = r =>
    {
        if (more?.Invoke(r) is { } custom)
        {
            return custom;
        }
        return (r.Method.Method, r.Path) switch
        {
            ("GET", "/api/devices/chat/files/") => Info(),
            ("POST", "/api/devices/chat/files/") => FakeBackend.Json(HttpStatusCode.Created, FileJson),
            ("POST", "/api/devices/chat/direct/") => FakeBackend.Json(HttpStatusCode.Created, """{"id": 12, "kind": "direct", "name": "Priya"}"""),
            ("POST", _) => FakeBackend.Json(HttpStatusCode.Created, "{}"),
            _ => FakeBackend.Json(HttpStatusCode.NotFound, "{}"),
        };
    };

    [Fact]
    public async Task A_file_is_uploaded_then_posted_with_its_id_and_its_copy_goes()
    {
        ServeSend();
        var outbox = Outbox();
        var path = Copy();
        outbox.AppendChatFile("conv:3", path, "Quote-Lumen.pdf", 2048, "Final version");
        var sync = Sync(outbox);

        await sync.SyncSendAsync("tok", CancellationToken.None);

        var upload = _backend.Requests.Single(r => r.Method == HttpMethod.Post && r.Path == "/api/devices/chat/files/");
        Assert.Equal("multipart/form-data", upload.ContentType);
        Assert.Contains("name=file", upload.Body, StringComparison.Ordinal);
        Assert.Contains("Quote-Lumen.pdf", upload.Body, StringComparison.Ordinal);
        Assert.NotNull(upload.ContentLength); // never chunked: WSGI would read it as empty
        Assert.Equal("Device", upload.AuthScheme);

        var line = _backend.Requests.Single(r => r.Path == "/api/devices/chat/3/messages/");
        using var body = JsonDocument.Parse(line.Body);
        Assert.Equal(FileId, body.RootElement.GetProperty("file_id").GetString());
        Assert.Equal("Final version", body.RootElement.GetProperty("body").GetString());

        Assert.Empty(outbox.PendingFiles());
        Assert.False(Directory.Exists(Path.GetDirectoryName(path)));
        Assert.True(sync.Snapshot.Info!.Allowed);
    }

    [Fact]
    public async Task A_file_to_the_admin_goes_to_messages_and_the_admin_chat_is_pulled_after()
    {
        ServeSend();
        var outbox = Outbox();
        outbox.AppendChatFile(OutboxReply.AdminKey, Copy(), "Quote-Lumen.pdf", 2048, "");
        var sync = Sync(outbox);

        await sync.SyncSendAsync("tok", CancellationToken.None);

        var line = _backend.Requests.Single(r => r.Method == HttpMethod.Post && r.Path == "/api/devices/messages/");
        Assert.Contains($"\"file_id\":\"{FileId}\"", line.Body, StringComparison.Ordinal);
        Assert.Contains("\"body\":\"\"", line.Body, StringComparison.Ordinal); // a blank caption is fine with a file
        Assert.True(sync.TakeAdminPosted());
        Assert.False(sync.TakeAdminPosted());
    }

    [Fact]
    public async Task A_file_to_a_teammate_not_written_to_yet_starts_the_direct_chat_first()
    {
        ServeSend();
        var outbox = Outbox();
        outbox.AppendChatFile(ChatKeys.Direct(4), Copy(), "Quote-Lumen.pdf", 2048, "for you");
        await Sync(outbox).SyncSendAsync("tok", CancellationToken.None);

        Assert.Equal(1, Calls("POST", "/api/devices/chat/direct/"));
        Assert.Equal(1, Calls("POST", "/api/devices/chat/12/messages/"));
        Assert.Empty(outbox.PendingFiles());
    }

    [Fact]
    public async Task A_network_error_backs_off_and_the_upload_is_never_sent_twice()
    {
        var lineFails = true;
        ServeSend(r => r.Path == "/api/devices/chat/3/messages/" && lineFails ? new HttpResponseMessage(HttpStatusCode.BadGateway) : null);
        var outbox = Outbox();
        outbox.AppendChatFile("conv:3", Copy(), "Quote-Lumen.pdf", 2048, "x");
        var sync = Sync(outbox);

        await sync.SyncSendAsync("tok", CancellationToken.None);
        Assert.Equal(FileId, outbox.PendingFiles()[0].FileId); // uploaded and kept
        Assert.False(outbox.PendingFiles()[0].Refused);

        // Within the back-off nothing is tried again.
        _now += TimeSpan.FromSeconds(5);
        await sync.SyncSendAsync("tok", CancellationToken.None);
        Assert.Equal(1, Calls("POST", "/api/devices/chat/3/messages/"));

        lineFails = false;
        _now += ChatFileSync.FirstRetry;
        await sync.SyncSendAsync("tok", CancellationToken.None);
        Assert.Equal(1, Calls("POST", "/api/devices/chat/files/")); // the stored id was used: no second upload
        Assert.Equal(2, Calls("POST", "/api/devices/chat/3/messages/"));
        Assert.Empty(outbox.PendingFiles());
    }

    [Fact]
    public async Task A_refused_upload_is_final_and_says_why()
    {
        ServeSend(r => r.Method == HttpMethod.Post && r.Path == "/api/devices/chat/files/"
            ? FakeBackend.Json(HttpStatusCode.BadRequest, """{"file": ["Too many files waiting. Send or remove some first."]}""") : null);
        var outbox = Outbox();
        var path = Copy();
        outbox.AppendChatFile("conv:3", path, "Quote-Lumen.pdf", 2048, "x");
        var sync = Sync(outbox);
        await sync.SyncSendAsync("tok", CancellationToken.None);

        var item = Assert.Single(outbox.PendingFiles());
        Assert.True(item.Refused);
        Assert.Equal("Too many files waiting. Send or remove some first.", item.Error);
        Assert.True(File.Exists(path)); // kept for Retry
        Assert.Equal(0, Calls("POST", "/api/devices/chat/3/messages/"));

        // Not tried again until the member presses Retry.
        _now += TimeSpan.FromMinutes(10);
        await sync.SyncSendAsync("tok", CancellationToken.None);
        Assert.Equal(1, Calls("POST", "/api/devices/chat/files/"));
        outbox.Retry(item.Id);
        await sync.SyncSendAsync("tok", CancellationToken.None);
        Assert.Equal(2, Calls("POST", "/api/devices/chat/files/"));
    }

    [Fact]
    public async Task Sharing_turned_off_hides_attach_and_fails_the_file_with_the_admins_words()
    {
        ServeSend(r => r.Method == HttpMethod.Post && r.Path == "/api/devices/chat/files/"
            ? FakeBackend.Json(HttpStatusCode.Forbidden, """{"detail": "Your admin has turned off file sharing."}""") : null);
        var outbox = Outbox();
        outbox.AppendChatFile("conv:3", Copy(), "a.pdf", 2048, "");
        var sync = Sync(outbox);
        await sync.SyncSendAsync("tok", CancellationToken.None);

        Assert.Equal(ChatFiles.SharingOffText, outbox.PendingFiles()[0].Error);
        Assert.False(sync.Snapshot.Info!.Allowed);
    }

    [Fact]
    public async Task An_upload_the_server_lost_is_uploaded_again_once()
    {
        var refusals = 0;
        ServeSend(r => r.Path == "/api/devices/chat/3/messages/" && refusals++ < 5
            ? FakeBackend.Json(HttpStatusCode.BadRequest, """{"file_id": ["That file wasn't found. Attach it again."]}""") : null);
        var outbox = Outbox();
        outbox.AppendChatFile("conv:3", Copy(), "a.pdf", 2048, "");
        var sync = Sync(outbox);

        await sync.SyncSendAsync("tok", CancellationToken.None); // upload, refused line: the id is dropped
        Assert.Null(outbox.PendingFiles()[0].FileId);
        Assert.False(outbox.PendingFiles()[0].Refused);
        await sync.SyncSendAsync("tok", CancellationToken.None); // upload again, refused again: now final
        Assert.Equal(2, Calls("POST", "/api/devices/chat/files/"));
        Assert.True(outbox.PendingFiles()[0].Refused);
        Assert.Equal("That file wasn't found. Attach it again.", outbox.PendingFiles()[0].Error);
    }

    [Fact]
    public async Task An_upload_older_than_its_hour_is_sent_again_before_the_line()
    {
        ServeSend();
        var outbox = Outbox();
        var item = outbox.AppendChatFile("conv:3", Copy(), "a.pdf", 2048, "")!;
        outbox.Update(item.Id, i => i with { FileId = "11111111-1111-1111-1111-111111111111", UploadedAt = _now - TimeSpan.FromMinutes(55) });
        await Sync(outbox).SyncSendAsync("tok", CancellationToken.None);

        Assert.Equal(1, Calls("POST", "/api/devices/chat/files/"));
        Assert.Contains(FileId, _backend.Requests.Single(r => r.Path == "/api/devices/chat/3/messages/").Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_path_outside_the_outbox_folder_is_never_read()
    {
        ServeSend();
        Directory.CreateDirectory(_dir.Path);
        var secret = Path.Combine(_dir.Path, "secret.txt");
        File.WriteAllText(secret, "do not send");
        var outbox = Outbox();
        outbox.AppendChatFile("conv:3", secret, "secret.txt", 11, "");
        outbox.AppendChatFile("conv:3", Path.Combine(Widget, ChatFiles.OutboxFolder, "..", "..", "secret.txt"), "secret.txt", 11, "");
        await Sync(outbox).SyncSendAsync("tok", CancellationToken.None);

        Assert.Equal(0, Calls("POST", "/api/devices/chat/files/"));
        Assert.All(outbox.PendingFiles(), i => Assert.True(i.Refused));
    }

    [Fact]
    public async Task A_copy_over_25_MB_is_refused_without_an_upload()
    {
        ServeSend();
        var outbox = Outbox();
        var path = Copy();
        using (var f = new FileStream(path, FileMode.Open))
        {
            f.SetLength(ChatFiles.MaxBytes + 1);
        }
        outbox.AppendChatFile("conv:3", path, "big.bin", ChatFiles.MaxBytes + 1, "");
        await Sync(outbox).SyncSendAsync("tok", CancellationToken.None);

        Assert.Equal(0, Calls("POST", "/api/devices/chat/files/"));
        Assert.Equal(ChatFiles.TooBigText, outbox.PendingFiles()[0].Error);
    }

    [Fact]
    public async Task A_backend_without_files_turns_them_off_and_old_routes_mean_off()
    {
        _backend.Respond = _ => FakeBackend.Json(HttpStatusCode.NotFound, """{"detail": "Not found."}""");
        var outbox = Outbox();
        outbox.AppendChatFile("admin", Copy(), "a.pdf", 2048, "");
        var sync = Sync(outbox);
        await sync.SyncSendAsync("tok", CancellationToken.None);

        Assert.Null(sync.Snapshot.Info);
        Assert.Equal(ChatFiles.SharingOffText, outbox.PendingFiles()[0].Error);

        // 405 on the info as well.
        _backend.Respond = _ => new HttpResponseMessage(HttpStatusCode.MethodNotAllowed);
        Assert.Null(await _backend.Client().GetChatFilesInfoAsync("tok", CancellationToken.None));
    }

    // ---- Fetching --------------------------------------------------------------------------------------------------

    private static HttpResponseMessage FileReply(byte[] bytes, string? name, string type = "application/octet-stream")
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue(type);
        if (name is not null)
        {
            content.Headers.ContentDisposition = new ContentDispositionHeaderValue("attachment") { FileNameStar = name };
        }
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    [Fact]
    public async Task A_download_lands_in_downloads_zinwe_with_a_number_when_taken_and_the_mark_of_the_web()
    {
        _backend.Respond = r => r.Path == $"/api/devices/chat/files/{FileId}/" ? FileReply("%PDF-1.7"u8.ToArray(), "Quote-Lumen.pdf") : Info();
        Directory.CreateDirectory(Path.Combine(Downloads, "Zinwe"));
        File.WriteAllText(Path.Combine(Downloads, "Zinwe", "Quote-Lumen.pdf"), "an older one");
        var sync = Sync();

        var asked = sync.Ask(new FileAsk(FileAsk.Download, FileId.ToUpperInvariant(), "ignored.pdf"));
        Assert.Equal(FileState.Working, Assert.Single(asked.Downloads!).State);
        Assert.True(sync.FetchDue);
        await sync.SyncFetchAsync("tok", CancellationToken.None);

        var done = Assert.Single(sync.Snapshot.Downloads!);
        Assert.Equal(FileState.Done, done.State);
        Assert.Equal(Path.Combine(Downloads, "Zinwe", "Quote-Lumen (2).pdf"), done.Path);
        Assert.Equal("%PDF-1.7", File.ReadAllText(done.Path!));
        Assert.Contains("ZoneId=3", File.ReadAllText(done.Path + ":Zone.Identifier"), StringComparison.Ordinal);
        Assert.Equal("an older one", File.ReadAllText(Path.Combine(Downloads, "Zinwe", "Quote-Lumen.pdf")));
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(Data, "chatfiles"))); // the service's own copy is gone
        Assert.DoesNotContain(_backend.Requests, r => r.Host != "127.0.0.1"); // the token only went to the backend
    }

    [Fact]
    public async Task An_expired_file_says_so_and_nonsense_ids_are_ignored()
    {
        _backend.Respond = _ => FakeBackend.Json(HttpStatusCode.Gone, """{"detail": "This file is no longer available. Files are kept for 7 days."}""");
        var sync = Sync();
        sync.Ask(new FileAsk(FileAsk.Download, "../../etc/passwd"));
        sync.Ask(new FileAsk("delete", FileId));
        Assert.False(sync.FetchDue);

        sync.Ask(new FileAsk(FileAsk.Download, FileId, "a.pdf"));
        await sync.SyncFetchAsync("tok", CancellationToken.None);
        var state = Assert.Single(sync.Snapshot.Downloads!);
        Assert.Equal(FileState.Gone, state.State);
        Assert.Equal(ChatFiles.ExpiredText, state.Error);
    }

    [Fact]
    public async Task A_download_cut_by_the_network_can_be_asked_again()
    {
        _backend.Respond = _ => throw new HttpRequestException("offline");
        var sync = Sync();
        sync.Ask(new FileAsk(FileAsk.Download, FileId, "a.pdf"));
        await sync.SyncFetchAsync("tok", CancellationToken.None);
        Assert.Equal(ChatFiles.DownloadFailedText, sync.Snapshot.Downloads![0].Error);

        sync.Ask(new FileAsk(FileAsk.Download, FileId, "a.pdf"));
        Assert.Equal(FileState.Working, sync.Snapshot.Downloads![0].State);
    }

    [Fact]
    public async Task An_image_preview_is_fetched_inline_once_and_only_a_real_image_is_kept()
    {
        byte[] png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3];
        const string svgId = "aaaaaaaa-1d4b-4c55-9a77-0b1f3c2d4e5f";
        _backend.Respond = r => r.Path.StartsWith("/api/devices/chat/files/aaaaaaaa", StringComparison.Ordinal)
            ? FileReply("<svg/>"u8.ToArray(), null, "image/svg+xml")
            : FileReply(png, null, "image/png");
        var sync = Sync();
        sync.Ask(new FileAsk(FileAsk.Preview, FileId, "photo.png", 11));
        sync.Ask(new FileAsk(FileAsk.Preview, svgId, "x.svg", 6));
        sync.Ask(new FileAsk(FileAsk.Preview, "bbbbbbbb-1d4b-4c55-9a77-0b1f3c2d4e5f", "huge.png", ChatFiles.PreviewMaxBytes + 1)); // too big: not asked
        await sync.SyncFetchAsync("tok", CancellationToken.None);

        var previews = sync.Snapshot.Previews!;
        Assert.Equal(2, previews.Count);
        var ok = previews.Single(p => p.Id == FileId);
        Assert.Equal(FileState.Done, ok.State);
        Assert.Equal(Path.Combine(Widget, ChatFiles.PreviewFolder, FileId + ".png"), ok.Path);
        Assert.Equal(png, File.ReadAllBytes(ok.Path!));
        Assert.Equal(FileState.Failed, previews.Single(p => p.Id == svgId).State);
        Assert.Contains(_backend.Requests, r => r.Path == $"/api/devices/chat/files/{FileId}/");

        // Asked again: nothing new is fetched.
        var calls = _backend.Requests.Count;
        sync.Ask(new FileAsk(FileAsk.Preview, FileId, "photo.png", 11));
        Assert.False(sync.FetchDue);
        Assert.Equal(calls, _backend.Requests.Count);
    }

    [Fact]
    public void Clear_forgets_the_member_and_their_waiting_files()
    {
        var outbox = Outbox();
        outbox.AppendChatFile("conv:3", Copy(), "a.pdf", 2048, "");
        var sync = Sync(outbox);
        sync.Ask(new FileAsk(FileAsk.Download, FileId, "a.pdf"));
        sync.Clear();

        Assert.Empty(outbox.PendingFiles());
        Assert.Empty(sync.Snapshot.Downloads!);
        Assert.False(sync.FetchDue);
    }
}

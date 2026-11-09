using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Zinwe.Agent.Core.Backend;
using Zinwe.Agent.Core.Chat;
using Zinwe.Agent.Core.Ipc;
using Zinwe.Agent.Core.Messages;

namespace Zinwe.Agent.Core.Tests;

/// <summary>
/// v1.5 round 2 · file sharing in chat on the agent, files staying on the sender's PC: the rules and words (sizes in GB, the
/// plan limit, 10 at a time, names, the clash, kinds, the batch grid), the contract (FileJSON round 2 and round 1, info,
/// tickets, relay jobs), the chat_file outbox batch, and the service's send (register → line), hold (hash → PATCH, the
/// holder check, stop, 30 days), serve (relay long-poll, offset streaming) and receive (ticket → Range resume → sha256 →
/// Zone.Identifier, waiting for the sender, cancel, name clash, Save as) against a fake backend and relay.
/// </summary>
public sealed class ChatFilesTests : IDisposable
{
    private const string FileId = "6f1c2a8e-1d4b-4c55-9a77-0b1f3c2d4e5f";
    private const string Sid = "S-1-5-21-1000";
    private const string Relay = "https://dev.backend.zinwe.io/relay/";

    private static string FileJson(string id = FileId, string name = "Quote-Lumen.pdf", long size = 2_202_009, string state = "ok",
        bool available = true, string kind = "document", string extra = "") => $$"""
        {"id": "{{id}}", "name": "{{name}}", "size": {{size}}, "mime": "application/pdf", "kind": "{{kind}}", "caption": "Final version",
         "risky": false, "batch_id": null, "width": null, "height": null, "duration_s": null, "thumb_url": null,
         "sender": {"name": "Priya Nair", "first_name": "Priya"}, "mine": false, "available": {{(available ? "true" : "false")}},
         "state": "{{state}}", "created_at": "2026-11-09T10:00:00Z", "expires_at": "2026-12-09T10:00:00Z", "expired": false{{extra}}}
        """;

    private const string LegacyJson = """
        {"id": "6f1c2a8e-1d4b-4c55-9a77-0b1f3c2d4e5f", "name": "Quote-Lumen.pdf", "size": 2202009, "mime": "application/pdf",
         "kind": "file", "caption": "Final version", "risky": false, "expires_at": "2026-11-16T10:20:00Z", "expired": false,
         "url": "https://dev.backend.zinwe.io/api/devices/chat/files/6f1c2a8e-1d4b-4c55-9a77-0b1f3c2d4e5f/"}
        """;

    private readonly TempDir _dir = new();
    private readonly Net _net = new();
    private readonly List<TimeSpan> _delays = [];
    private DateTimeOffset _now = new(2026, 11, 9, 10, 0, 0, TimeSpan.Zero);

    public void Dispose() => _dir.Dispose();

    private string Widget => Path.Combine(_dir.Path, "widget");
    private string Data => Path.Combine(_dir.Path, "data");
    private string Saves => Path.Combine(_dir.Path, "saves");
    private string SharedRoot => Path.Combine(_dir.Path, "programdata-shared");

    private MessageOutbox Outbox() => new(Widget);

    private ChatFileSync Sync(MessageOutbox? outbox = null, string? sid = Sid) => new(
        new BackendClient(new HttpClient(_net) { BaseAddress = new Uri("http://127.0.0.1:8000/") }),
        new ChatFileTransfer(new HttpClient(_net) { BaseAddress = new Uri("http://127.0.0.1:8000/") }),
        outbox ?? Outbox(), new LocalWidgetFolder(Widget), new LocalUserFiles(sid), NullLogger<ChatFileSync>.Instance, Data, () => _now,
        (t, _) =>
        {
            _delays.Add(t);
            return Task.CompletedTask;
        }, SharedRoot);

    /// <summary>A file the member picked, where they keep it.</summary>
    private string Picked(string name = "Quote-Lumen.pdf", int bytes = 2048, byte fill = (byte)'x')
    {
        var folder = Path.Combine(_dir.Path, "mine");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, name);
        File.WriteAllBytes(path, Enumerable.Range(0, bytes).Select(i => (byte)(fill + (i % 7))).ToArray());
        return path;
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Info(bool allowed = true, long max = 2L * 1024 * 1024 * 1024) =>
        Json(HttpStatusCode.OK, $$"""{"allowed": {{(allowed ? "true" : "false")}}, "max_bytes": {{max}}, "keep_days": 30}""");

    private static HttpResponseMessage ServeTicket(string ticket = "srv") => Json(HttpStatusCode.OK, $$"""
        {"ticket": "{{ticket}}", "wait_url": "{{Relay}}wait?t={{ticket}}", "push_base": "{{Relay}}push/", "expires_at": "2026-11-10T10:00:00Z"}
        """);

    private static HttpResponseMessage Ticket(string id = FileId, string extra = "") => Json(HttpStatusCode.OK, $$"""
        {"url": "{{Relay}}file/{{id}}?t=get", "inline_url": "{{Relay}}file/{{id}}?t=get&inline=1", "status_url": "{{Relay}}status/{{id}}?t=get",
         "expires_at": "2026-11-09T10:15:00Z"{{extra}}}
        """);

    /// <summary>A round-2 backend: info, serve ticket, register (201 with a new id each time), lines, PATCH, stop.</summary>
    private void Backend(Func<Seen, HttpResponseMessage?>? more = null)
    {
        var n = 0;
        _net.Respond = r =>
        {
            if (more?.Invoke(r) is { } custom)
            {
                return custom;
            }
            return (r.Method.Method, r.Path) switch
            {
                ("GET", "/api/devices/chat/files/") => Info(),
                ("GET", "/api/devices/chat/files/serve-ticket/") => ServeTicket(),
                ("POST", "/api/devices/chat/files/") => Json(HttpStatusCode.Created,
                    FileJson(id: n++ == 0 ? FileId : $"{n:00000000}-1d4b-4c55-9a77-0b1f3c2d4e5f")),
                ("POST", "/api/devices/chat/direct/") => Json(HttpStatusCode.Created, """{"id": 12, "kind": "direct", "name": "Priya"}"""),
                ("PATCH", _) => Json(HttpStatusCode.OK, "{}"),
                ("POST", _) => Json(HttpStatusCode.Created, "{}"),
                _ => Json(HttpStatusCode.NotFound, "{}"),
            };
        };
    }

    private OutboxReply Queue(MessageOutbox outbox, string path, string key = "conv:3", string caption = "Final version", string? batch = null,
        bool copied = false) =>
        outbox.AppendChatFiles([new OutboxReply("", caption, _now, ChatKey: key, FilePath: path, FileName: Path.GetFileName(path),
            FileSize: new FileInfo(path).Length, BatchId: batch, Mime: ChatFiles.MimeOf(path), ThumbB64: "/9j/4AAQ", Width: 640, Height: 480,
            Copied: copied)])![0];

    // ---- Words and rules ------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(1023, "1023 B")]
    [InlineData(348_160, "340 KB")]
    [InlineData(2_202_009, "2.1 MB")]
    [InlineData(26_214_400, "25 MB")]
    [InlineData(1_503_238_553, "1.4 GB")]
    [InlineData(2L * 1024 * 1024 * 1024, "2 GB")]
    [InlineData(5L * 1024 * 1024 * 1024, "5 GB")]
    public void Sizes_read_like_the_server_writes_them_in_gb_too(long bytes, string text) => Assert.Equal(text, ChatFiles.FormatSize(bytes));

    [Fact]
    public void The_plan_limit_says_its_own_number_and_the_other_words_are_exact()
    {
        Assert.Equal("Files can be up to 1 GB on your plan.", ChatFiles.TooBigText(1024L * 1024 * 1024));
        Assert.Equal("Files can be up to 5 GB on your plan.", ChatFiles.TooBigText(5120L * 1024 * 1024));
        Assert.Equal("Files can be up to 1.5 GB on your plan.", ChatFiles.TooBigText(1536L * 1024 * 1024));
        Assert.Equal("You can send up to 10 files at a time.", ChatFiles.TooManyText);
        Assert.Equal("Waiting for Priya's PC…", ChatFiles.WaitingText("Priya"));
        Assert.Equal("Not shared any more.", ChatFiles.NotSharedText);
        Assert.Equal("Kept on your PC. Others download it from here while your PC is on.", ChatFiles.OwnFileText);
        Assert.Equal("Document", ChatFiles.AttachDocument);
        Assert.Equal("Photos & videos", ChatFiles.AttachMedia);
        Assert.Equal("Save as…", ChatFiles.SaveAsText);
        Assert.Equal("Save files to", ChatFiles.SaveToText);
        Assert.Equal("Ask where to save each file", ChatFiles.AskWhereText);
        Assert.Equal("Stop sharing", ChatFiles.StopText);
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData(0.0, "")]
    [InlineData(7.2, "0:07")]
    [InlineData(83.0, "1:23")]
    [InlineData(3723.0, "1:02:03")]
    public void A_video_length_reads_like_a_player_shows_it(double? seconds, string text) => Assert.Equal(text, ChatFiles.FormatDuration(seconds));

    [Theory]
    [InlineData("setup.exe", true)]
    [InlineData("tool.ps1", true)]
    [InlineData("invoice.pdf.js", true)]
    [InlineData("run.bat. ", true)]
    [InlineData("report.pdf", false)]
    [InlineData(null, false)]
    public void Program_files_are_risky(string? name, bool risky) => Assert.Equal(risky, ChatFiles.IsRisky(name));

    [Theory]
    [InlineData("photo.JPG", "image")]
    [InlineData("clip.mov", "video")]
    [InlineData("song.mp3", "audio")]
    [InlineData("quote.pdf", "document")]
    [InlineData("data.xlsx", "document")]
    [InlineData("logo.svg", "file")]
    [InlineData("pack.zip", "file")]
    [InlineData("photo.avif", "image")]
    [InlineData("setup.exe", "file")]
    public void The_kind_follows_the_extension_and_svg_is_never_an_image(string name, string kind) => Assert.Equal(kind, ChatFiles.KindOf(name));

    [Fact]
    public void The_attach_menu_filters_documents_and_photos_and_videos()
    {
        var docs = ChatFiles.PickerFilter(media: false);
        foreach (var ext in new[] { "*.pdf", "*.docx", "*.xlsx", "*.pptx", "*.txt", "*.csv", "*.rtf", "*.odt", "*.zip", "*.rar", "*.7z" })
        {
            Assert.Contains(ext, docs, StringComparison.Ordinal);
        }
        Assert.EndsWith("All files|*.*", docs, StringComparison.Ordinal);
        var media = ChatFiles.PickerFilter(media: true);
        foreach (var ext in new[] { "*.jpg", "*.png", "*.webp", "*.mp4", "*.mov", "*.avi", "*.mkv", "*.webm" })
        {
            Assert.Contains(ext, media, StringComparison.Ordinal);
        }
        Assert.DoesNotContain("*.pdf", media, StringComparison.Ordinal);
    }

    [Fact]
    public void The_limits_refuse_each_file_over_the_plan_and_any_past_ten()
    {
        var gb = 1024L * 1024 * 1024;
        var picked = Enumerable.Range(1, 12).Select(i => ($"f{i}.pdf", 1000L)).ToList();
        picked[2] = ("big.mp4", 2 * gb + 1);
        picked[3] = ("empty.txt", 0);
        var (ok, refusals) = ChatFiles.CheckPicked(picked, already: 0, maxBytes: 2 * gb);
        Assert.Equal(10, ok.Count);
        Assert.DoesNotContain(ok, p => p.Path is "big.mp4" or "empty.txt");
        Assert.Equal(["Files can be up to 2 GB on your plan.", ChatFiles.EmptyText], refusals);

        // 8 already waiting: only 2 more fit, and it says so once.
        (ok, refusals) = ChatFiles.CheckPicked([("a", 1), ("b", 1), ("c", 1), ("d", 1)], already: 8, maxBytes: gb);
        Assert.Equal(["a", "b"], ok.Select(p => p.Path));
        Assert.Equal([ChatFiles.TooManyText], refusals);

        // Exactly the limit is fine.
        Assert.Single(ChatFiles.CheckPicked([("x", 2 * gb)], 0, 2 * gb).Ok);
    }

    [Fact]
    public void A_taken_name_gets_a_number_like_explorer_gives_it_and_a_part_sits_next_to_it()
    {
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"C:\d\report.pdf", @"C:\d\report (2).pdf" };
        Assert.Equal(@"C:\d\report (3).pdf", ChatFiles.FreeName(@"C:\d", "report.pdf", taken.Contains));
        Assert.Equal(@"C:\d\notes.txt", ChatFiles.FreeName(@"C:\d", "notes.txt", taken.Contains));
        Assert.Equal(@"C:\d\_CON.txt", ChatFiles.FreeName(@"C:\d", "CON.txt", taken.Contains));
        Assert.Equal(@"C:\d\report.pdf.6f1c2a8e.part", ChatFiles.PartPath(FileId, "report.pdf", @"C:\d", null));
        Assert.Equal(@"E:\x\mine.pdf.part", ChatFiles.PartPath(FileId, "report.pdf", null, @"E:\x\mine.pdf"));
        Assert.Equal(@"C:\d\y.pdf.6f1c2a8e.part", ChatFiles.PartPath(FileId, @"..\x/y.pdf", @"C:\d", null)); // no folders from a name
    }

    [Fact]
    public void Photos_and_videos_sent_together_group_into_one_grid_with_plus_n_on_the_fourth()
    {
        ChatFile Media(int i, string? batch, string kind = "image") => new($"{i:00000000}-1d4b-4c55-9a77-0b1f3c2d4e5f", $"p{i}.jpg", 10, Kind: kind, BatchId: batch);
        var lines = new (ChatFile? File, string Who)[]
        {
            (null, "priya"), (Media(1, "b1"), "priya"), (Media(2, "b1", "video"), "priya"), (Media(3, "b1"), "priya"),
            (Media(4, "b1"), "anil"), // another sender: not in Priya's grid
            (new ChatFile("x", "q.pdf", 5, Kind: "document", BatchId: "b2"), "anil"), (Media(5, "b2"), "anil"), // a document breaks a grid
            (Media(6, null), "anil"), (Media(7, null), "anil"), // no batch: alone
        };
        var groups = ChatFileBatches.Group(lines, l => l.File, l => l.Who);
        Assert.Equal([1, 3, 1, 1, 1, 1, 1], groups.Select(g => g.Count));
        Assert.Equal((4, 0), ChatFileBatches.Grid(4));
        Assert.Equal((4, 3), ChatFileBatches.Grid(7));
        Assert.Equal((2, 0), ChatFileBatches.Grid(2));
    }

    // ---- The contract -----------------------------------------------------------------------------------------------

    [Fact]
    public void A_round_2_file_reads_every_field_and_its_state()
    {
        var f = ChatFiles.FromJson(FileJson(kind: "video", extra: """, "batch_id": "b-1", "width": 1920, "height": 1080, "duration_s": 83.4, "thumb_url": "https://x/thumb/" """.Replace("\"batch_id\": null, ", "")))!;
        Assert.Equal(FileId, f.Id);
        Assert.True(f.IsVideo);
        Assert.True(f.IsMedia);
        Assert.Equal("Priya", f.FirstName());
        Assert.Equal("Priya Nair", f.SenderName);
        Assert.False(f.Mine);
        Assert.Equal("ok", f.State);
        Assert.False(f.IsLegacy);
        Assert.True(f.AvailableAt(_now));
        Assert.Null(f.Url);
        Assert.Equal(new DateTimeOffset(2026, 11, 9, 10, 0, 0, TimeSpan.Zero), f.CreatedAt);

        // A duplicate key: the reader takes the last one. Read each field from a clean object instead.
        var clean = ChatFiles.FromJson("""
            {"id": "6f1c2a8e-1d4b-4c55-9a77-0b1f3c2d4e5f", "name": "clip.mp4", "size": 10, "kind": "video", "batch_id": "b-1", "width": 1920,
             "height": 1080, "duration_s": 83.4, "thumb_url": "https://x/thumb/", "sender": {"name": "Priya Nair"}, "mine": true,
             "available": true, "state": "ok"}
            """)!;
        Assert.Equal("b-1", clean.BatchId);
        Assert.Equal(1920, clean.Width);
        Assert.Equal(1080, clean.Height);
        Assert.Equal(83.4, clean.DurationS);
        Assert.Equal("https://x/thumb/", clean.ThumbUrl);
        Assert.Equal("Priya", clean.FirstName()); // from the name when no first_name
        Assert.True(clean.Mine);

        foreach (var (state, available) in new[] { ("stopped", true), ("gone", true), ("old", true), ("ok", false) })
        {
            Assert.False(ChatFiles.FromJson(FileJson(state: state, available: available))!.AvailableAt(_now), state);
        }
    }

    [Fact]
    public void A_round_1_file_still_reads_as_legacy_with_its_url_and_keep_date()
    {
        var f = ChatFiles.FromJson(LegacyJson)!;
        Assert.True(f.IsLegacy);
        Assert.True(f.AvailableAt(_now));
        Assert.False(f.AvailableAt(new DateTimeOffset(2026, 11, 17, 0, 0, 0, TimeSpan.Zero)));
        Assert.False((f with { Expired = true, Url = null }).AvailableAt(_now));
        Assert.Equal("the sender", f.FirstName());
        Assert.Equal("Priya", f.FirstName("Priya Nair"));
    }

    [Fact]
    public void A_line_reads_file_null_present_and_nonsense()
    {
        var lines = ChatJson.Messages($$"""
            [{"id": 1, "body": "Hi", "file": null},
             {"id": 2, "body": "📎 Quote-Lumen.pdf (2.1 MB)\nFinal version", "file": {{FileJson()}}},
             {"id": 4, "body": "odd", "file": "not an object"},
             {"id": 5, "body": "odd", "file": {"name": "no id" } }]
            """);
        Assert.Null(lines[0].File);
        Assert.Equal("Quote-Lumen.pdf", lines[1].File!.Name);
        Assert.Null(lines[2].File);
        Assert.Null(lines[3].File);
        Assert.Equal("📎 Quote-Lumen.pdf", ChatFiles.PreviewLine(lines[1]));
        Assert.Equal("Quote-Lumen.pdf", ChatFiles.FallbackName("📎 Quote-Lumen.pdf (1.4 GB)\nFinal"));
    }

    [Fact]
    public async Task The_admin_chat_reads_the_file_on_a_line_and_an_older_backend_without_it()
    {
        _net.Respond = _ => Json(HttpStatusCode.OK, $$"""
            [{"id": 7, "title": "", "body": "📎 Quote-Lumen.pdf (2.1 MB)", "severity": "info", "created_at": "2026-11-09T09:00:00Z",
              "sender": "member", "file": {{FileJson()}}},
             {"id": 8, "title": "Hi", "body": "Hello", "severity": "info", "created_at": "2026-11-09T09:01:00Z"}]
            """);
        var messages = await new BackendClient(new HttpClient(_net) { BaseAddress = new Uri("http://127.0.0.1:8000/") })
            .GetMessagesAsync("tok", CancellationToken.None);
        Assert.Equal("Priya", messages[0].File!.SenderFirst);
        Assert.Null(messages[1].File);
    }

    [Fact]
    public void A_file_survives_the_pipe_and_the_notifications_file_with_the_new_fields()
    {
        var file = new ChatFile(FileId, "clip.mp4", 10, "video/mp4", "video", "Look", BatchId: "b-1", Width: 640, Height: 360, DurationS: 7.5,
            ThumbUrl: "https://x/t/", SenderName: "Priya Nair", SenderFirst: "Priya", Mine: false, Available: true, State: "ok", CreatedAt: _now);
        var line = new TeamMessage(2, "member", 9, "Priya Nair", "x", File: file);
        var back = PipeProtocol.DeserializeChat(PipeProtocol.Serialize(new ChatReply(true, TaskBackend.On, Thread: new ChatThread("conv:3", [line]))))!;
        Assert.Equal(file, back.Thread!.Messages![0].File);

        var writer = new NotificationWriter(Widget);
        var msg = new AgentMessage(7, "", "📎 clip.mp4 (10 B)", "info", _now, "member", File: file);
        writer.MergeChat([msg]);
        // Later its sender stopped sharing it: the line already there takes the new state.
        writer.MergeChat([msg with { File = file with { State = "stopped" } }]);
        Assert.Contains("\"state\": \"stopped\"", File.ReadAllText(writer.FilePath).Replace("\":\"", "\": \""), StringComparison.Ordinal);
    }

    [Fact]
    public void Files_info_reads_the_plan_limit_and_tickets_and_jobs_read_strictly()
    {
        Assert.Equal(new ChatFilesInfo(true, 3L * 1024 * 1024 * 1024, 30),
            ChatFiles.InfoFromJson("""{"allowed": true, "max_bytes": 3221225472, "keep_days": 30}"""));
        Assert.Equal(ChatFiles.DefaultMaxBytes, ChatFiles.InfoFromJson("""{"allowed": true}""")!.MaxBytes);
        Assert.False(new ChatFilesInfo(true).CanSend); // no relay known: no attach button
        Assert.Null(ChatFiles.InfoFromJson("<html>"));

        var serve = ChatFiles.ServeTicketFromJson("""{"ticket": "a b", "wait_url": "https://x/relay/wait?t=a", "push_base": "https://x/relay/push/", "expires_at": "2026-11-10T10:00:00Z"}""", _now)!;
        Assert.Equal("https://x/relay/push/j%2F1?t=a%20b", serve.PushUrl("j/1").AbsoluteUri);
        Assert.True(serve.FreshAt(_now));
        Assert.False(serve.FreshAt(new DateTimeOffset(2026, 11, 10, 9, 30, 0, TimeSpan.Zero)));
        Assert.Null(ChatFiles.ServeTicketFromJson("""{"ticket": "a", "wait_url": "http://evil.example/wait", "push_base": "https://x/"}""", _now));
        Assert.NotNull(ChatFiles.ServeTicketFromJson("""{"ticket": "a", "wait_url": "http://127.0.0.1:9000/relay/wait", "push_base": "http://127.0.0.1:9000/relay/push/"}""", _now));

        var ticket = ChatFiles.TicketFromJson("""{"url": "https://x/relay/file/1?t=g", "status_url": "https://x/relay/status/1?t=g", "sha256": "ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789"}""", _now)!;
        Assert.Equal("abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789", ticket.Sha256);
        Assert.Equal(_now.AddMinutes(15), ticket.ExpiresAt);
        Assert.Null(ChatFiles.TicketFromJson("""{"url": "file:///C:/x"}""", _now));

        Assert.Equal(new RelayJob("j1", FileId, 1024), ChatFiles.JobFromJson($$"""{"job": "j1", "file_id": "{{FileId.ToUpperInvariant()}}", "offset": 1024}"""));
        Assert.Null(ChatFiles.JobFromJson("""{"job": "j1", "file_id": "../etc", "offset": 0}"""));
        Assert.Null(ChatFiles.JobFromJson("{}"));
    }

    [Fact]
    public void A_files_reply_crosses_the_pipe_and_an_older_service_reads_as_not_supported()
    {
        var reply = new ChatFilesReply(true, new ChatFilesInfo(true, Relay: true),
            [new FileState(FileId, FileState.Waiting, Received: 10, Total: 100, WaitingFor: "Priya")], [], 3,
            [new SharedFile(FileId, "a.pdf", 100, "conv:3", _now)]);
        var back = AgentPipeClient.ParseFiles(PipeProtocol.Serialize(reply))!;
        Assert.Equal(reply.Downloads![0], back.Downloads![0]);
        Assert.Equal(10, back.Downloads[0].Percent);
        Assert.True(back.Info!.CanSend);
        Assert.Equal(reply.Shared![0], back.Shared![0]);
        Assert.Null(AgentPipeClient.ParseFiles(PipeProtocol.Serialize(PipeProtocol.NotSupported)));
        var ask = new FileAsk(FileAsk.Download, FileId, "a.pdf", 100, Folder: @"C:\Users\x\Downloads\Zinwe", SenderFirst: "Priya");
        var request = PipeProtocol.DeserializeRequest(PipeProtocol.Serialize(new PipeProtocol.Request(PipeProtocol.RequestTypes.Files, File: ask)))!;
        Assert.Equal(ask, request.File);
    }

    // ---- The outbox batch -------------------------------------------------------------------------------------------

    [Fact]
    public void A_batch_is_queued_in_one_write_with_its_fields_and_only_the_files_sync_sends_it()
    {
        var outbox = Outbox();
        var items = outbox.AppendChatFiles(
        [
            new OutboxReply("x", "", _now, ChatKey: "conv:3", FilePath: @"C:\a.jpg", FileName: "a.jpg", FileSize: 5, BatchId: "b", ThumbB64: "AA==", Width: 4, Height: 3),
            new OutboxReply("x", "Look", _now, ChatKey: "conv:3", FilePath: @"\\nas\v.mp4", FileName: "v.mp4", FileSize: 9, BatchId: "b", DurationS: 7, Copied: true),
        ])!;
        Assert.Equal(2, items.Select(i => i.Id).Distinct().Count());
        var back = outbox.PendingFiles();
        Assert.Equal(["a.jpg", "v.mp4"], back.Select(i => i.FileName)); // in order
        Assert.All(back, i => Assert.Equal(OutboxReply.ChatFileKind, i.Kind));
        Assert.Equal(("b", "AA==", 4, 3), (back[0].BatchId, back[0].ThumbB64, back[0].Width, back[0].Height));
        Assert.Equal(("Look", 7d, true), (back[1].Body, back[1].DurationS!.Value, back[1].Copied));
        Assert.Empty(outbox.Pending());
        Assert.Empty(outbox.PendingChat());
        Assert.False(outbox.HasPendingOtherThanPicture);
    }

    [Fact]
    public void An_older_outbox_file_without_the_new_fields_still_reads()
    {
        Directory.CreateDirectory(Widget);
        File.WriteAllText(Path.Combine(Widget, "messages-outbox.json"),
            """{"Items": [{"Id": "a1", "Body": "hi", "CreatedAtUtc": "2026-11-09T09:00:00Z"}, {"Id": "f1", "Body": "", "CreatedAtUtc": "2026-11-09T09:00:00Z", "Kind": "chat_file", "FilePath": "C:\\x.pdf"}]}""");
        var outbox = Outbox();
        Assert.Single(outbox.Pending());
        Assert.Null(Assert.Single(outbox.PendingFiles()).BatchId);
    }

    // ---- Sending: register, then the line ---------------------------------------------------------------------------

    [Fact]
    public async Task A_file_is_registered_with_its_path_and_preview_then_posted_with_its_id_and_held()
    {
        Backend();
        var outbox = Outbox();
        var path = Picked();
        Queue(outbox, path, batch: "b-1");
        var sync = Sync(outbox);
        await sync.SyncSendAsync("tok", CancellationToken.None);

        var register = _net.Seen.Single(r => r is { Method.Method: "POST", Path: "/api/devices/chat/files/" });
        using (var doc = JsonDocument.Parse(register.Body))
        {
            var e = doc.RootElement;
            Assert.Equal("Quote-Lumen.pdf", e.GetProperty("name").GetString());
            Assert.Equal(2048, e.GetProperty("size").GetInt64());
            Assert.Equal("application/pdf", e.GetProperty("mime").GetString());
            Assert.Equal(path, e.GetProperty("local_ref").GetString());
            Assert.Equal("/9j/4AAQ", e.GetProperty("thumb_b64").GetString());
            Assert.Equal(640, e.GetProperty("width").GetInt32());
            Assert.Equal("b-1", e.GetProperty("batch_id").GetString());
            Assert.False(e.TryGetProperty("sha256", out _)); // it follows by PATCH
        }
        Assert.Equal("Device", register.Auth?.Scheme);
        var line = _net.Seen.Single(r => r is { Method.Method: "POST", Path: "/api/devices/chat/3/messages/" });
        Assert.Contains($"\"file_id\":\"{FileId}\"", line.Body, StringComparison.Ordinal);
        Assert.Contains("\"body\":\"Final version\"", line.Body, StringComparison.Ordinal);
        Assert.Empty(outbox.PendingFiles());

        var held = Assert.Single(sync.Store.All());
        Assert.Equal((FileId, path, 2048L, "conv:3", Sid), (held.Id, held.LocalRef, held.Size, held.ChatKey, held.UserSid));
        Assert.Equal(File.GetLastWriteTimeUtc(path), held.LastWriteUtc);
        Assert.Equal([new SharedFile(FileId, "Quote-Lumen.pdf", 2048, "conv:3", _now)], sync.Snapshot.Shared);
        Assert.True(File.Exists(path)); // the member's file stays where it is
    }

    [Fact]
    public async Task A_batch_posts_one_line_each_in_order_to_the_admin_and_the_admin_chat_is_pulled_after()
    {
        Backend();
        var outbox = Outbox();
        Queue(outbox, Picked("a.jpg"), OutboxReply.AdminKey, "", "b");
        Queue(outbox, Picked("b.jpg"), OutboxReply.AdminKey, "", "b");
        Queue(outbox, Picked("c.jpg"), OutboxReply.AdminKey, "All three", "b");
        var sync = Sync(outbox);
        await sync.SyncSendAsync("tok", CancellationToken.None);

        var lines = _net.Seen.Where(r => r is { Method.Method: "POST", Path: "/api/devices/messages/" }).ToList();
        Assert.Equal(3, lines.Count);
        Assert.Contains("\"body\":\"\"", lines[0].Body, StringComparison.Ordinal);
        Assert.Contains("\"body\":\"All three\"", lines[2].Body, StringComparison.Ordinal);
        Assert.Equal(3, _net.Seen.Count(r => r is { Method.Method: "POST", Path: "/api/devices/chat/files/" }));
        Assert.Equal(3, sync.Store.All().Count);
        Assert.True(sync.TakeAdminPosted());
        Assert.False(sync.TakeAdminPosted());
    }

    [Fact]
    public async Task A_file_over_the_plan_limit_is_refused_with_the_plans_words_and_never_registered()
    {
        Backend(r => r.Path == "/api/devices/chat/files/" && r.Method == HttpMethod.Get ? Info(max: 1024) : null);
        var outbox = Outbox();
        Queue(outbox, Picked(bytes: 2048));
        await Sync(outbox).SyncSendAsync("tok", CancellationToken.None);
        var item = Assert.Single(outbox.PendingFiles());
        Assert.True(item.Refused);
        Assert.Equal("Files can be up to 0 GB on your plan.", item.Error);
        Assert.DoesNotContain(_net.Seen, r => r is { Method.Method: "POST", Path: "/api/devices/chat/files/" });
    }

    [Fact]
    public async Task A_network_error_backs_off_and_a_file_is_never_registered_twice()
    {
        var down = true;
        Backend(r => r.Path.EndsWith("/messages/", StringComparison.Ordinal) && down ? throw new HttpRequestException("offline") : null);
        var outbox = Outbox();
        Queue(outbox, Picked());
        var sync = Sync(outbox);
        await sync.SyncSendAsync("tok", CancellationToken.None);
        Assert.Equal(FileId, outbox.PendingFiles()[0].FileId); // registered, the line didn't go
        Assert.False(sync.SendDue && outbox.PendingFiles().Any(i => i.FileId is null));

        down = false;
        _now = _now.AddMinutes(1);
        await sync.SyncSendAsync("tok", CancellationToken.None);
        Assert.Empty(outbox.PendingFiles());
        Assert.Equal(1, _net.Seen.Count(r => r is { Method.Method: "POST", Path: "/api/devices/chat/files/" }));
    }

    [Fact]
    public async Task A_round_1_backend_without_the_relay_hides_attach_and_older_ones_turn_files_off()
    {
        Backend(r => r.Path == "/api/devices/chat/files/serve-ticket/" ? Json(HttpStatusCode.NotFound, "{}") : null);
        var outbox = Outbox();
        Queue(outbox, Picked());
        var sync = Sync(outbox);
        await sync.SyncSendAsync("tok", CancellationToken.None);
        Assert.False(sync.Snapshot.Info!.CanSend);
        Assert.True(sync.Snapshot.Info.Allowed);
        Assert.False(outbox.PendingFiles()[0].Refused); // it waits; nothing was sent to a backend that can't hold it
        Assert.DoesNotContain(_net.Seen, r => r is { Method.Method: "POST", Path: "/api/devices/chat/files/" });

        _net.Respond = _ => Json(HttpStatusCode.NotFound, "{}"); // 1.4: no files at all
        _now = _now.AddMinutes(6);
        await sync.SyncSendAsync("tok", CancellationToken.None);
        Assert.Null(sync.Snapshot.Info);
    }

    [Fact]
    public async Task A_refused_registration_is_final_and_sharing_off_says_so()
    {
        Backend(r => r is { Method.Method: "POST", Path: "/api/devices/chat/files/" }
            ? Json(HttpStatusCode.Forbidden, """{"detail": "Your admin has turned off file sharing."}""") : null);
        var outbox = Outbox();
        Queue(outbox, Picked());
        var sync = Sync(outbox);
        await sync.SyncSendAsync("tok", CancellationToken.None);
        var item = outbox.PendingFiles()[0];
        Assert.True(item.Refused);
        Assert.Equal(ChatFiles.SharingOffText, item.Error);
        Assert.False(sync.Snapshot.Info!.Allowed);
        Assert.Empty(sync.Store.All());
    }

    [Fact]
    public async Task A_file_moved_before_it_was_sent_fails_with_the_words()
    {
        Backend();
        var outbox = Outbox();
        var path = Picked();
        Queue(outbox, path);
        File.Delete(path);
        await Sync(outbox).SyncSendAsync("tok", CancellationToken.None);
        Assert.Equal(ChatFiles.MissingText, outbox.PendingFiles()[0].Error);
    }

    // ---- Holding: the hash, the holder check, stop, 30 days ----------------------------------------------------------

    private async Task<(ChatFileSync Sync, string Path)> HeldAsync(string name = "Quote-Lumen.pdf", int bytes = 4096, bool copied = false,
        string? sid = Sid)
    {
        Backend();
        var outbox = Outbox();
        var path = Picked(name, bytes);
        Queue(outbox, path, copied: copied);
        var sync = Sync(outbox, sid);
        await sync.SyncSendAsync("tok", CancellationToken.None);
        return (sync, path);
    }

    [Fact]
    public async Task The_sha256_is_made_in_the_background_then_patched_once()
    {
        var (sync, path) = await HeldAsync();
        await sync.IdleAsync();
        var expected = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
        Assert.Equal(expected, sync.Store.Get(FileId)!.Sha256);

        await sync.SyncSendAsync("tok", CancellationToken.None);
        var patch = _net.Seen.Single(r => r is { Method.Method: "PATCH" });
        Assert.Equal($"/api/devices/chat/files/{FileId}/", patch.Path);
        Assert.Equal($$"""{"sha256":"{{expected}}"}""", patch.Body);
        await sync.SyncSendAsync("tok", CancellationToken.None);
        Assert.Single(_net.Seen, r => r is { Method.Method: "PATCH" });
    }

    [Fact]
    public async Task The_holder_check_serves_only_its_own_unchanged_files_and_reports_a_changed_one_as_gone()
    {
        var (sync, path) = await HeldAsync();
        var ticket = ChatFiles.ServeTicketFromJson((await ServeTicket().Content.ReadAsStringAsync()), _now)!;

        // A file this PC doesn't hold: nothing is pushed.
        await sync.ServeJobAsync(new RelayJob("j0", "aaaaaaaa-1d4b-4c55-9a77-0b1f3c2d4e5f", 0), ticket, CancellationToken.None);
        Assert.DoesNotContain(_net.Seen, r => r.Method == HttpMethod.Put);

        // Another member signed in: not served, and not "gone" either.
        var other = new LocalUserFiles("S-1-5-21-2000");
        Assert.Null(new ChatFileSyncProbe(sync, other).Open(FileId, out var changed));
        Assert.False(changed);

        // The member changed the file: PATCH gone, nothing pushed, and it leaves My shared files.
        File.AppendAllText(path, "edited");
        await sync.ServeJobAsync(new RelayJob("j1", FileId, 0), ticket, CancellationToken.None);
        Assert.DoesNotContain(_net.Seen, r => r.Method == HttpMethod.Put);
        Assert.Equal(ChatFile.StateGone, sync.Store.Get(FileId)!.State);
        Assert.Empty(sync.Snapshot.Shared!);
        await sync.SyncSendAsync("tok", CancellationToken.None);
        Assert.Contains(_net.Seen, r => r is { Method.Method: "PATCH" } && r.Body == """{"gone":true}""");
        Assert.Null(sync.Store.Get(FileId));
    }

    [Fact]
    public async Task A_job_is_pushed_from_its_offset_with_the_rest_of_the_file_and_no_device_token()
    {
        var (sync, path) = await HeldAsync(bytes: 10_000);
        Backend(r => r.Method == HttpMethod.Put ? new HttpResponseMessage(HttpStatusCode.OK) : null);
        var ticket = ChatFiles.ServeTicketFromJson((await ServeTicket("s t").Content.ReadAsStringAsync()), _now)!;
        await sync.ServeJobAsync(new RelayJob("job-9", FileId, 4_000), ticket, CancellationToken.None);

        var put = _net.Seen.Single(r => r.Method == HttpMethod.Put);
        Assert.Equal("https://dev.backend.zinwe.io/relay/push/job-9?t=s%20t", put.Uri.AbsoluteUri);
        Assert.Null(put.Auth); // the ticket is the key; the device token never goes to the relay
        Assert.Equal(6_000, put.ContentLength);
        Assert.Equal(File.ReadAllBytes(path)[4_000..], put.Bytes);
    }

    [Fact]
    public async Task A_receiver_that_goes_away_ends_the_push()
    {
        var transfer = new ChatFileTransfer(new HttpClient(_net) { BaseAddress = new Uri("http://127.0.0.1:8000/") });
        foreach (var code in new[] { 499, 410 })
        {
            _net.Respond = _ => new HttpResponseMessage((HttpStatusCode)code);
            Assert.Equal(PushOutcome.ReceiverGone,
                await transfer.PushAsync(new Uri(Relay + "push/j"), new MemoryStream(new byte[100]), 100, CancellationToken.None));
        }
        _net.Respond = _ => throw new HttpRequestException("connection reset");
        Assert.Equal(PushOutcome.ReceiverGone, await transfer.PushAsync(new Uri(Relay + "push/j"), new MemoryStream(new byte[100]), 100, CancellationToken.None));
        _net.Respond = _ => new HttpResponseMessage(HttpStatusCode.Forbidden);
        Assert.Equal(PushOutcome.Refused, await transfer.PushAsync(new Uri(Relay + "push/j"), new MemoryStream(new byte[100]), 100, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            transfer.PushAsync(new Uri("http://evil.example/push/j"), new MemoryStream(new byte[1]), 1, CancellationToken.None));
    }

    [Fact]
    public async Task The_serve_loop_long_polls_while_it_holds_files_and_pushes_a_job()
    {
        var (sync, _) = await HeldAsync(bytes: 300);
        var waits = 0;
        Backend(r => r.Path == "/relay/wait" ? waits++ == 0 ? new HttpResponseMessage(HttpStatusCode.NoContent)
                : Json(HttpStatusCode.OK, $$"""{"job": "j1", "file_id": "{{FileId}}", "offset": 100}""")
            : r.Method == HttpMethod.Put ? new HttpResponseMessage(HttpStatusCode.OK) : null);

        Assert.Equal(0, await sync.ServeOnceAsync("tok", 0, CancellationToken.None)); // 204: nobody waits
        Assert.DoesNotContain(_net.Seen, r => r.Method == HttpMethod.Put);
        await sync.ServeOnceAsync("tok", 0, CancellationToken.None);
        await sync.IdleAsync();
        Assert.Equal(200, _net.Seen.Single(r => r.Method == HttpMethod.Put).ContentLength);
        var wait = _net.Seen.First(r => r.Path == "/relay/wait");
        Assert.Equal("t=srv", wait.Uri.Query.TrimStart('?'));
        Assert.Null(wait.Auth);

        // A relay that refuses the ticket: a new one is asked for next time.
        Backend(r => r.Path == "/relay/wait" ? new HttpResponseMessage(HttpStatusCode.Forbidden) : null);
        _net.Seen.Clear();
        await sync.ServeOnceAsync("tok", 0, CancellationToken.None);
        await sync.ServeOnceAsync("tok", 0, CancellationToken.None);
        Assert.Single(_net.Seen, r => r.Path == "/api/devices/chat/files/serve-ticket/");
    }

    [Fact]
    public async Task The_serve_loop_rests_when_nothing_is_held()
    {
        Backend();
        var sync = Sync();
        await sync.SyncSendAsync("tok", CancellationToken.None);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        try
        {
            Assert.Equal(0, await sync.ServeOnceAsync("tok", 0, cts.Token)); // woken by the info, or rests till cancelled
        }
        catch (OperationCanceledException)
        {
            // rested until the end
        }
        Assert.DoesNotContain(_net.Seen, r => r.Path == "/relay/wait");
    }

    [Fact]
    public async Task Stop_sharing_tells_the_server_and_deletes_only_our_copy()
    {
        Directory.CreateDirectory(Path.Combine(SharedRoot, "c1"));
        var copy = Path.Combine(SharedRoot, "c1", "nas.pdf");
        File.WriteAllText(copy, "from the NAS");
        Backend();
        var outbox = Outbox();
        Queue(outbox, copy, copied: true);
        var sync = Sync(outbox);
        await sync.SyncSendAsync("tok", CancellationToken.None);

        sync.Ask(new FileAsk(FileAsk.Stop, FileId));
        Assert.Equal(ChatFile.StateStopped, Assert.Single(sync.Snapshot.Shared!).State); // "Stopping…"
        Assert.True(sync.SendDue);
        await sync.SyncSendAsync("tok", CancellationToken.None);
        Assert.Contains(_net.Seen, r => r is { Method.Method: "POST" } && r.Path == $"/api/devices/chat/files/{FileId}/stop/");
        Assert.Null(sync.Store.Get(FileId));
        Assert.False(Directory.Exists(Path.Combine(SharedRoot, "c1")));

        // Never anything outside a copies root.
        var mine = Picked();
        Assert.False(SharedCopies.DeleteCopy(mine, [SharedRoot]));
        Assert.True(File.Exists(mine));
    }

    [Fact]
    public async Task My_shared_files_comes_from_the_servers_list_with_chat_names_and_a_file_it_dropped_is_let_go()
    {
        var (sync, _) = await HeldAsync();
        var other = "bbbbbbbb-1d4b-4c55-9a77-0b1f3c2d4e5f";
        var listed = true;
        Backend(r => r.Path == "/api/devices/chat/files/shared/" ? Json(HttpStatusCode.OK, $$"""
            [{{(listed ? FileJson(extra: """, "holder": "d2", "local_ref": "C:\\x", "sha256": "", "chat": {"kind": "group", "id": 3, "name": "Design"}""") + "," : "")}}
             {{FileJson(id: other, name: "From my laptop.pdf", extra: """, "chat": {"kind": "admin", "id": 1, "name": "Ananya Rao"}""")}}]
            """) : null);
        _now = _now.AddMinutes(6);
        await sync.SyncSendAsync("tok", CancellationToken.None);
        var shared = sync.Snapshot.Shared!;
        Assert.Equal(2, shared.Count);
        Assert.Equal(("conv:3", "Design"), (shared.Single(f => f.Id == FileId).ChatKey, shared.Single(f => f.Id == FileId).ChatName));
        Assert.Equal((OutboxReply.AdminKey, "Ananya Rao"), (shared.Single(f => f.Id == other).ChatKey, shared.Single(f => f.Id == other).ChatName));

        // Stopped from the web: the server's list no longer has it, so this PC stops holding it (after its first hour).
        listed = false;
        _now = _now.AddHours(1);
        await sync.SyncSendAsync("tok", CancellationToken.None);
        Assert.Null(sync.Store.Get(FileId));
        Assert.Equal([other], sync.Snapshot.Shared!.Select(f => f.Id));
    }

    [Fact]
    public async Task A_file_leaves_the_list_after_30_days()
    {
        var (sync, _) = await HeldAsync();
        _now = _now.AddDays(31);
        await sync.SyncSendAsync("tok", CancellationToken.None);
        Assert.Empty(sync.Store.All());
    }

    [Theory]
    [InlineData(@"\\nas\team\plan.pdf", null, true)]
    [InlineData(@"Z:\plan.pdf", DriveType.Network, true)]
    [InlineData(@"E:\plan.pdf", DriveType.Removable, true)]
    [InlineData(@"C:\Users\x\plan.pdf", DriveType.Fixed, false)]
    [InlineData(@"\\?\C:\plan.pdf", DriveType.Fixed, false)]
    public void Files_on_network_and_removable_drives_are_copied_first(string path, DriveType? type, bool copy) =>
        Assert.Equal(copy, SharedCopies.NeedsCopy(path, _ => type));

    // ---- Receiving ---------------------------------------------------------------------------------------------------

    private byte[] Content(int bytes = 5000) => Enumerable.Range(0, bytes).Select(i => (byte)(i * 31)).ToArray();

    /// <summary>A round-2 backend and relay serving <paramref name="body"/>, with Range.</summary>
    private void Serving(byte[] body, Func<Seen, HttpResponseMessage?>? more = null, bool online = true)
    {
        Backend(r =>
        {
            if (more?.Invoke(r) is { } custom)
            {
                return custom;
            }
            if (r.Path == $"/api/devices/chat/files/{FileId}/ticket/")
            {
                return Ticket();
            }
            if (r.Path == $"/relay/status/{FileId}")
            {
                return Json(HttpStatusCode.OK, online ? """{"online": true}""" : """{"online": false}""");
            }
            if (r.Path == $"/relay/file/{FileId}")
            {
                return Ranged(body, r.Range);
            }
            return null;
        });
    }

    private static HttpResponseMessage Ranged(byte[] body, long? from)
    {
        if (from is not { } start)
        {
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };
        }
        var part = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(body[(int)start..]) };
        part.Content.Headers.ContentRange = new ContentRangeHeaderValue(start, body.Length - 1, body.Length);
        return part;
    }

    private async Task<ChatFileSync> DownloadAsync(FileAsk ask, ChatFileSync? sync = null)
    {
        sync ??= Sync();
        sync.Ask(ask);
        await sync.SyncFetchAsync("tok", CancellationToken.None);
        await sync.IdleAsync();
        return sync;
    }

    private FileAsk Ask(string name = "Quote-Lumen.pdf", long size = 5000, string? target = null, string? sha = null) =>
        new(FileAsk.Download, FileId, name, size, target is null ? Saves : null, target, Sha256: sha, SenderFirst: "Priya");

    [Fact]
    public async Task A_download_comes_through_the_relay_into_the_folder_marked_from_the_internet()
    {
        var body = Content();
        Serving(body);
        var sync = await DownloadAsync(Ask());

        var state = Assert.Single(sync.Snapshot.Downloads!);
        Assert.Equal(FileState.Done, state.State);
        Assert.Equal(Path.Combine(Saves, "Quote-Lumen.pdf"), state.Path);
        Assert.Equal(body, File.ReadAllBytes(state.Path!));
        Assert.Equal(ChatFiles.ZoneIdentifier, File.ReadAllText(state.Path + ":Zone.Identifier"));
        Assert.Empty(Directory.GetFiles(Saves, "*.part"));
        var get = _net.Seen.Single(r => r.Path == $"/relay/file/{FileId}");
        Assert.Null(get.Auth); // the get ticket is the key
        Assert.Null(get.Range);
        Assert.Equal("Device", _net.Seen.Single(r => r.Path.EndsWith("/ticket/", StringComparison.Ordinal)).Auth?.Scheme);
    }

    [Fact]
    public async Task A_cut_download_resumes_from_its_part_with_a_range()
    {
        var body = Content(8000);
        Directory.CreateDirectory(Saves);
        File.WriteAllBytes(ChatFiles.PartPath(FileId, "Quote-Lumen.pdf", Saves, null), body[..3000]);
        Serving(body);
        var sync = await DownloadAsync(Ask(size: 8000));

        Assert.Equal(3000, _net.Seen.Single(r => r.Path == $"/relay/file/{FileId}").Range);
        Assert.Equal(body, File.ReadAllBytes(sync.Snapshot.Downloads![0].Path!));
    }

    [Fact]
    public async Task A_relay_that_ignores_the_range_starts_the_part_over()
    {
        var body = Content(4000);
        Directory.CreateDirectory(Saves);
        File.WriteAllBytes(ChatFiles.PartPath(FileId, "Quote-Lumen.pdf", Saves, null), new byte[1500]); // junk from before
        Serving(body, r => r.Path == $"/relay/file/{FileId}" ? Ranged(body, null) : null);
        var sync = await DownloadAsync(Ask(size: 4000));
        Assert.Equal(body, File.ReadAllBytes(sync.Snapshot.Downloads![0].Path!));
    }

    [Fact]
    public async Task A_stream_that_stops_short_goes_on_from_where_it_got_to()
    {
        var body = Content(6000);
        var first = true;
        Serving(body, r =>
        {
            if (r.Path != $"/relay/file/{FileId}")
            {
                return null;
            }
            if (first)
            {
                first = false;
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body[..2500]) }; // the sender slept
            }
            return Ranged(body, r.Range);
        });
        var sync = await DownloadAsync(Ask(size: 6000));
        Assert.Equal(2500, _net.Seen.Last(r => r.Path == $"/relay/file/{FileId}").Range);
        Assert.Equal(FileState.Done, sync.Snapshot.Downloads![0].State);
        Assert.Equal(body, File.ReadAllBytes(sync.Snapshot.Downloads[0].Path!));
    }

    [Fact]
    public async Task A_taken_name_is_saved_with_a_number_and_save_as_goes_exactly_where_chosen()
    {
        var body = Content(100);
        Serving(body);
        Directory.CreateDirectory(Saves);
        File.WriteAllText(Path.Combine(Saves, "Quote-Lumen.pdf"), "someone else's");
        var sync = await DownloadAsync(Ask(size: 100));
        Assert.Equal(Path.Combine(Saves, "Quote-Lumen (2).pdf"), sync.Snapshot.Downloads![0].Path);
        Assert.Equal("someone else's", File.ReadAllText(Path.Combine(Saves, "Quote-Lumen.pdf")));

        // Save as…: the member chose the path (and agreed to replace it).
        var chosen = Path.Combine(_dir.Path, "elsewhere", "My quote.pdf");
        Directory.CreateDirectory(Path.GetDirectoryName(chosen)!);
        File.WriteAllText(chosen, "old");
        sync = await DownloadAsync(Ask(size: 100, target: chosen), sync);
        Assert.Equal(chosen, sync.Snapshot.Downloads![0].Path);
        Assert.Equal(body, File.ReadAllBytes(chosen));
        Assert.False(File.Exists(chosen + ".part"));
    }

    [Fact]
    public async Task The_sender_offline_shows_waiting_with_their_name_and_tries_again_every_30_seconds()
    {
        var body = Content(100);
        var online = 0;
        Serving(body, r => r.Path == $"/relay/status/{FileId}"
            ? Json(HttpStatusCode.OK, online++ < 2 ? """{"online": false}""" : """{"online": true}""") : null);
        var sync = Sync();
        var states = new List<string>();
        sync.Ask(Ask(size: 100));
        await sync.SyncFetchAsync("tok", CancellationToken.None);
        await sync.IdleAsync();
        Assert.Equal([ChatFiles.RetryEvery, ChatFiles.RetryEvery], _delays);
        Assert.Equal(FileState.Done, sync.Snapshot.Downloads![0].State);

        // A 503 from the relay is "offline" too: Waiting for Priya's PC…
        var calls = 0;
        _delays.Clear();
        Serving(body, r => r.Path == $"/relay/file/{FileId}" && calls++ == 0
            ? Json(HttpStatusCode.ServiceUnavailable, """{"detail": "Priya's PC is offline. The download starts when it's back."}""") : null);
        var waiting = new TaskCompletionSource();
        var gate = new TaskCompletionSource();
        var slow = new ChatFileSync(new BackendClient(new HttpClient(_net) { BaseAddress = new Uri("http://127.0.0.1:8000/") }),
            new ChatFileTransfer(new HttpClient(_net) { BaseAddress = new Uri("http://127.0.0.1:8000/") }), Outbox(), new LocalWidgetFolder(Widget),
            new LocalUserFiles(Sid), NullLogger<ChatFileSync>.Instance, Data, () => _now, async (_, _) =>
            {
                waiting.TrySetResult();
                await gate.Task;
            }, SharedRoot);
        slow.Ask(Ask(size: 100) with { Id = FileId });
        await slow.SyncFetchAsync("tok", CancellationToken.None);
        await waiting.Task;
        var w = slow.Snapshot.Downloads![0];
        Assert.Equal((FileState.Waiting, "Priya"), (w.State, w.WaitingFor));
        Assert.True(w.Busy);
        gate.SetResult();
        await slow.IdleAsync();
        Assert.Equal(FileState.Done, slow.Snapshot.Downloads![0].State);
    }

    [Fact]
    public async Task A_file_no_longer_shared_says_so_and_a_relative_or_nonsense_ask_is_ignored()
    {
        Serving(Content(), r => r.Path.EndsWith("/ticket/", StringComparison.Ordinal)
            ? Json(HttpStatusCode.Gone, """{"detail": "This file isn't shared any more."}""") : null);
        var sync = await DownloadAsync(Ask());
        var state = sync.Snapshot.Downloads![0];
        Assert.Equal((FileState.Gone, ChatFiles.NotSharedText), (state.State, state.Error));

        var fresh = Sync();
        fresh.Ask(new FileAsk(FileAsk.Download, FileId, "a.pdf", 1, Folder: @"..\..\Windows"));
        fresh.Ask(new FileAsk(FileAsk.Download, "../../etc", "a.pdf", 1, Folder: Saves));
        fresh.Ask(new FileAsk(FileAsk.Download, FileId, "a.pdf", 1, Folder: Saves, Target: Path.Combine(Saves, "x.pdf")));
        Assert.Empty(fresh.Snapshot.Downloads!);
        Assert.False(fresh.FetchDue);
    }

    [Fact]
    public async Task The_sha256_is_checked_when_known_and_a_mismatch_is_thrown_away()
    {
        var body = Content(300);
        var good = Convert.ToHexStringLower(SHA256.HashData(body));
        Serving(body);
        var sync = await DownloadAsync(Ask(size: 300, sha: good));
        Assert.Equal(FileState.Done, sync.Snapshot.Downloads![0].State);

        Serving(body, r => r.Path.EndsWith("/ticket/", StringComparison.Ordinal) ? Ticket(extra: $$""", "sha256": "{{new string('0', 64)}}" """) : null);
        sync = await DownloadAsync(Ask(name: "other.pdf", size: 300));
        var state = sync.Snapshot.Downloads![0];
        Assert.Equal((FileState.Failed, ChatFiles.DownloadFailedText), (state.State, state.Error));
        Assert.False(File.Exists(Path.Combine(Saves, "other.pdf")));
        Assert.Empty(Directory.GetFiles(Saves, "*.part"));
    }

    [Fact]
    public async Task Cancel_stops_a_download_and_throws_its_part_away()
    {
        var body = Content(100);
        var gate = new TaskCompletionSource();
        Serving(body, online: false);
        var waiting = new TaskCompletionSource();
        var sync = new ChatFileSync(new BackendClient(new HttpClient(_net) { BaseAddress = new Uri("http://127.0.0.1:8000/") }),
            new ChatFileTransfer(new HttpClient(_net) { BaseAddress = new Uri("http://127.0.0.1:8000/") }), Outbox(), new LocalWidgetFolder(Widget),
            new LocalUserFiles(Sid), NullLogger<ChatFileSync>.Instance, Data, () => _now, async (t, ct) =>
            {
                waiting.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct);
            }, SharedRoot);
        Directory.CreateDirectory(Saves);
        var part = ChatFiles.PartPath(FileId, "Quote-Lumen.pdf", Saves, null);
        File.WriteAllBytes(part, body[..10]);
        sync.Ask(Ask(size: 100));
        await sync.SyncFetchAsync("tok", CancellationToken.None);
        await waiting.Task;
        sync.Ask(new FileAsk(FileAsk.Cancel, FileId));
        await sync.IdleAsync();
        Assert.Equal(FileState.Cancelled, sync.Snapshot.Downloads![0].State);
        Assert.False(File.Exists(part));
    }

    [Fact]
    public async Task A_round_1_file_downloads_from_the_server_copy_with_the_device_token()
    {
        var body = Content(700);
        _net.Respond = r => r.Path == $"/api/devices/chat/files/{FileId}/"
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) }
            : Json(HttpStatusCode.NotFound, "{}");
        var sync = await DownloadAsync(Ask(size: 700) with { Legacy = true });
        Assert.Equal(body, File.ReadAllBytes(sync.Snapshot.Downloads![0].Path!));
        Assert.Equal("Device", _net.Seen.Single().Auth?.Scheme);
    }

    [Fact]
    public async Task A_thumbnail_comes_from_thumb_into_the_widget_folder_and_only_a_real_image_is_kept()
    {
        var png = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0 };
        _net.Respond = r => r.Path switch
        {
            $"/api/devices/chat/files/{FileId}/thumb/" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(png) },
            _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<svg/>") },
        };
        var sync = Sync();
        sync.Ask(new FileAsk(FileAsk.Preview, FileId));
        sync.Ask(new FileAsk(FileAsk.Preview, "aaaaaaaa-1d4b-4c55-9a77-0b1f3c2d4e5f"));
        await sync.SyncFetchAsync("tok", CancellationToken.None);
        var states = sync.Snapshot.Previews!;
        var ok = states.Single(s => s.Id == FileId);
        Assert.Equal(Path.Combine(Widget, ChatFiles.PreviewFolder, FileId + ".png"), ok.Path);
        Assert.Equal(FileState.Failed, states.Single(s => s.Id != FileId).State);
    }

    [Fact]
    public async Task A_relay_body_bigger_than_the_file_is_refused()
    {
        var transfer = new ChatFileTransfer(new HttpClient(_net) { BaseAddress = new Uri("http://127.0.0.1:8000/") });
        _net.Respond = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[5000]) };
        using var target = new MemoryStream();
        var result = await transfer.FetchAsync(new Uri(Relay + "file/x?t=g"), target, 1000, null, CancellationToken.None);
        Assert.Equal(RelayFetchOutcome.Refused, result.Outcome);
    }

    [Fact]
    public void Clear_forgets_the_member_and_their_waiting_files()
    {
        var outbox = Outbox();
        Queue(outbox, Picked());
        var sync = Sync(outbox);
        sync.Store.Add(new SharedEntry(FileId, "a.pdf", 1, @"C:\a.pdf", DateTime.UtcNow, "conv:3", _now));
        sync.Ask(Ask());
        sync.Clear();
        Assert.Empty(outbox.PendingFiles());
        Assert.Empty(sync.Store.All());
        Assert.Empty(sync.Snapshot.Downloads!);
        Assert.False(sync.FetchDue);
    }

    // ---- Test doubles ------------------------------------------------------------------------------------------------

    /// <summary>Opens a held file as another member would see it (the holder check's "who is signed in").</summary>
    private sealed class ChatFileSyncProbe(ChatFileSync sync, IUserFiles user)
    {
        public FileStream? Open(string id, out bool changed)
        {
            var e = sync.Store.Get(id)!;
            var me = user.CurrentUser();
            changed = false;
            return e.UserSid is not null && e.UserSid != me ? null : sync.OpenHeld(e, out changed);
        }
    }

    internal sealed record Seen(HttpMethod Method, Uri Uri, string Body, long? ContentLength, AuthenticationHeaderValue? Auth, long? Range,
        byte[] Bytes)
    {
        public string Path => Uri.AbsolutePath;
    }

    /// <summary>The backend and the relay in one: every request recorded (with its headers), <see cref="Respond"/> answers.</summary>
    internal sealed class Net : HttpMessageHandler
    {
        public List<Seen> Seen { get; } = [];

        public Func<Seen, HttpResponseMessage> Respond { get; set; } = _ => throw new InvalidOperationException("No call was expected.");

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var bytes = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(ct);
            var seen = new Seen(request.Method, request.RequestUri!, Encoding.UTF8.GetString(bytes), request.Content?.Headers.ContentLength,
                request.Headers.Authorization, request.Headers.Range?.Ranges.FirstOrDefault()?.From, bytes);
            lock (Seen)
            {
                Seen.Add(seen);
            }
            return Respond(seen);
        }
    }
}

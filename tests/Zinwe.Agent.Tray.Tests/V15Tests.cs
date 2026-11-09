using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Zinwe.Agent.Core.Backend;
using Zinwe.Agent.Core.Chat;
using Zinwe.Agent.Core.Ipc;
using Zinwe.Agent.Core.Messages;
using Zinwe.Agent.Core.Policy;
using Zinwe.Agent.Tray.Views;
using Zinwe.Agent.Tray.Widget;

namespace Zinwe.Agent.Tray.Tests;

/// <summary>
/// v1.5 round 2 · file sharing in chat on the tray (files stay on the sender's PC): the attach limits (the plan's GB, 10 at a
/// time), the send tray with Windows' thumbnails and a video's length, Send (one outbox item per file, one batch id, the
/// caption on the last, the member's own path), copies of network files, the received cards (document, photo/video tile,
/// own file, not shared any more, risky), Download ▾ / Save as… and the Save files to / Ask where settings, the ring and
/// "Waiting for Priya's PC…" with ✕, the batch grid, the photo viewer, My shared files, and older services and backends.
/// Everything runs in a temp folder: the member's real outbox, settings and Downloads are never touched.
/// </summary>
[Collection(WpfCollection.Name)]
public sealed class V15Tests : IDisposable
{
    private const long Me = 7;
    private const string FileId = "6f1c2a8e-1d4b-4c55-9a77-0b1f3c2d4e5f";
    private static readonly DateTimeOffset Now = DateTimeOffset.Now;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "zinwe-v15-" + Guid.NewGuid().ToString("N"));
    private readonly List<FileAsk?> _asked = [];

    public V15Tests()
    {
        Directory.CreateDirectory(_dir);
        ChatPrefs.PathOverride = Path.Combine(_dir, "chat-prefs.json");
        TrayModel.DownloadsOverride = Path.Combine(_dir, "Downloads");
    }

    public void Dispose()
    {
        ChatPrefs.PathOverride = null;
        TrayModel.DownloadsOverride = null;
        try
        {
            Directory.Delete(_dir, true);
        }
        catch (IOException)
        {
        }
    }

    private string Widget => Path.Combine(_dir, "widget");

    private static ChatFile Pdf(string name = "Quote-Lumen.pdf", string state = "ok", bool mine = false, string id = FileId) =>
        new(id, name, 2_202_009, "application/pdf", ChatFile.DocumentKind, "Final version", ExpiresAt: Now.AddDays(30),
            SenderName: "Priya Nair", SenderFirst: "Priya", Mine: mine, Available: state == "ok", State: state, CreatedAt: Now);

    private static ChatFile Media(int i, string kind = ChatFile.ImageKind, string? batch = null, double? duration = null) =>
        new($"{i:00000000}-1d4b-4c55-9a77-0b1f3c2d4e5f", kind == ChatFile.VideoKind ? $"clip{i}.mp4" : $"photo{i}.jpg", 120_000,
            kind == ChatFile.VideoKind ? "video/mp4" : "image/jpeg", kind, null, BatchId: batch, Width: 1600, Height: 1200, DurationS: duration,
            SenderName: "Priya Nair", SenderFirst: "Priya", Mine: false, Available: true, State: "ok", CreatedAt: Now);

    private static AgentStatus Status(int? filesVersion = 1) => new(AgentState.Connected, "1.5.0", "PC", 1, Now, null,
        Entitlements: [PlanModules.WorkTime, PlanModules.TeamChat], CompanyName: "Skytechno", OwnerName: "Ananya Rao", TasksVersion: 1,
        TodosVersion: 1, ChatVersion: 1, FilesVersion: filesVersion);

    private static ChatFilesReply On(bool allowed = true, bool relay = true, long max = 2L * 1024 * 1024 * 1024,
        IReadOnlyList<FileState>? downloads = null, IReadOnlyList<SharedFile>? shared = null, int version = 1) =>
        new(true, new ChatFilesInfo(allowed, max, 30, relay), downloads ?? [], [], version, shared ?? []);

    private static TeamConversation Group(TeamMessage? last = null) => new(3, TeamConversation.GroupKind, "Design",
        Members: [new(Me, "Vishal Shakya"), new(9, "Priya Nair")], Last: last, LastAt: last?.At);

    private static TeamMessage FileLine(ChatFile file, long id = 41, long by = 9, string name = "Priya Nair") =>
        new(id, "member", by, name, "📎 " + file.Name + " (2.1 MB)\nFinal version", At: Now.AddMinutes(-5), File: file);

    private static ChatReply Thread(params TeamMessage[] lines) =>
        new(true, TaskBackend.On, [Group(lines[^1])], [], new Core.Chat.ChatThread("conv:3", lines), Now, 2);

    private sealed class FakeMedia(byte[]? thumb) : IShellMedia
    {
        public MediaInfo Describe(string path) =>
            ChatFiles.KindOf(path) == ChatFile.VideoKind ? new MediaInfo(thumb, 1920, 1080, 83) : new MediaInfo(thumb, 640, 480);
    }

    private void OnSta(Action<TrayModel> body)
    {
        Exception? error = null;
        var notes = new NotificationStore(Path.Combine(_dir, "notifications.json"));
        if (Application.Current is null)
        {
            var maker = new Thread(() =>
            {
                Application.ResourceAssembly ??= typeof(App).Assembly;
                new App().InitializeComponent();
            });
            maker.SetApartmentState(ApartmentState.STA);
            maker.Start();
            maker.Join();
        }
        var thread = new Thread(() =>
        {
            try
            {
                SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext());
                Application.ResourceAssembly ??= typeof(App).Assembly;
                var app = Application.Current as App ?? new App();
                app.InitializeComponent();
                using var watcher = new NotificationWatcher(notes, TimeSpan.FromMinutes(5));
                using var model = new TrayModel(notes, watcher, () => Task.FromResult<string?>(null));
                body(model);
            }
            catch (Exception e)
            {
                error = e;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null)
        {
            throw new Xunit.Sdk.XunitException(error.ToString());
        }
    }

    private void Seed(TrayModel model, ChatReply? chat = null, ChatFilesReply? files = null, AgentStatus? status = null, TraySettings? settings = null,
        byte[]? thumb = null)
    {
        model.UseForPreview(status ?? Status(), new TasksReply(true, TaskBackend.On, [], Now, 1),
            chat ?? new ChatReply(true, TaskBackend.On, [Group()], [], null, Now, 1),
            new TodosReply(true, TaskBackend.On, [], Me, Now, 1), settings ?? new TraySettings(), inApp: true, local: new LocalTaskSource(false, _dir));
        model.UseFilesForTests(Widget, ask =>
        {
            _asked.Add(ask);
            return Task.FromResult<ChatFilesReply?>(null);
        }, files ?? On(), new FakeMedia(thumb), Path.Combine(_dir, "programdata-shared"));
    }

    private string Picked(string name = "Quote-Lumen.pdf", long bytes = 4096)
    {
        var folder = Path.Combine(_dir, "picked");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, name);
        using var f = new FileStream(path, FileMode.Create);
        f.SetLength(bytes); // sparse enough: a 2 GB test file costs nothing
        return path;
    }

    private static void Pump(Task? task)
    {
        var frame = new System.Windows.Threading.DispatcherFrame();
        _ = (task ?? Task.CompletedTask).ContinueWith(_ => frame.Continue = false, TaskScheduler.Default);
        System.Windows.Threading.Dispatcher.PushFrame(frame);
        System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Background);
    }

    private static byte[] Jpeg()
    {
        var bitmap = new RenderTargetBitmap(1000, 800, 96, 96, PixelFormats.Pbgra32);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new LinearGradientBrush(Colors.Orange, Colors.Navy, 45), null, new Rect(0, 0, 1000, 800));
        }
        bitmap.Render(visual);
        bitmap.Freeze();
        return ShellMedia.EncodeThumb(bitmap)!;
    }

    private TeamThreadView ThreadView(TrayModel model)
    {
        model.OpenConv("conv:3");
        var view = new TeamThreadView { Model = model, Width = 380, Height = 640 };
        view.Render();
        Layout(view);
        return view;
    }

    // ---- Attach ------------------------------------------------------------------------------------------------------

    [Fact]
    public void Picked_files_over_the_plan_or_past_ten_are_refused_and_the_rest_wait_in_the_send_tray()
    {
        OnSta(model =>
        {
            Seed(model, files: On(max: 1024L * 1024 * 1024));
            Assert.True(model.FilesOn);
            Assert.Equal(1024L * 1024 * 1024, model.FileMaxBytes);

            Assert.False(model.Attach("conv:3", [Picked("big.mov", 1024L * 1024 * 1024 + 1)]));
            Assert.Equal("Files can be up to 1 GB on your plan.", model.Snack?.Text);
            Assert.Empty(model.Attached("conv:3"));

            var twelve = Enumerable.Range(1, 12).Select(i => Picked($"f{i}.pdf", 100)).ToList();
            Assert.True(model.Attach("conv:3", twelve));
            Assert.Equal(ChatFiles.TooManyText, model.Snack?.Text);
            Assert.Equal(10, model.Attached("conv:3").Count);
            Assert.False(model.Attach("conv:3", [Picked("one-more.pdf", 100)]));

            var first = model.Attached("conv:3")[0];
            model.RemoveAttach("conv:3", first);
            Assert.Equal(9, model.Attached("conv:3").Count);
            Assert.DoesNotContain(first, model.Attached("conv:3"));
            model.RemoveAttach("conv:3");
            Assert.Empty(model.Attached("conv:3"));
        });
    }

    [Fact]
    public void The_send_tray_shows_windows_thumbnails_and_a_videos_length_with_add_more()
    {
        OnSta(model =>
        {
            var jpeg = Jpeg();
            Assert.True(jpeg.Length <= ChatFiles.ThumbMaxBytes);
            var decoded = BitmapDecoder.Create(new MemoryStream(jpeg), BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
            Assert.Equal(ChatFiles.ThumbMaxPixels, Math.Max(decoded.PixelWidth, decoded.PixelHeight));

            Seed(model, thumb: jpeg);
            model.Attach("conv:3", [Picked("site.mp4", 5000), Picked("plan.pdf", 300)]);
            Pump(model.LastAttachDescribed);
            var drafts = model.Attached("conv:3");
            Assert.All(drafts, d => Assert.True(d.Described));
            Assert.Equal((ChatFile.VideoKind, 83d, 1920), (drafts[0].Kind, drafts[0].DurationS!.Value, drafts[0].Width!.Value));
            Assert.Equal(jpeg, drafts[1].Thumb);

            var view = ThreadView(model);
            var texts = Texts(view).Select(t => t.Text).ToList();
            Assert.Contains("site.mp4", texts);
            Assert.Contains("1:23", texts);
            Assert.Contains("Add more", texts);
            Assert.Contains(Descendants<Button>(view), b => Name(b) == "Remove plan.pdf");
            Assert.Contains(Descendants<Button>(view), b => Name(b) == ChatFiles.AttachTooltip);
        });
    }

    [Fact]
    public void The_attach_menu_has_document_and_photos_and_videos()
    {
        OnSta(model =>
        {
            Seed(model);
            Assert.Equal([(ChatFiles.AttachDocument, false), (ChatFiles.AttachMedia, true)], FileCard.AttachChoices.Select(c => (c.Label, c.Media)));
        });
    }

    // ---- Send --------------------------------------------------------------------------------------------------------

    [Fact]
    public void Send_queues_one_item_per_file_with_one_batch_the_caption_on_the_last_and_the_members_own_path()
    {
        OnSta(model =>
        {
            Seed(model, thumb: Jpeg());
            var a = Picked("a.jpg", 1000);
            var b = Picked("b.mp4", 2000);
            model.Attach("conv:3", [a, b]);
            Pump(model.LastAttachDescribed);
            Assert.True(model.SendFiles("conv:3", "  From the site "));
            Assert.Empty(model.Attached("conv:3"));
            Assert.Equal(2, model.PendingFileItems("conv:3").Count); // "Sending…" while it is queued
            Pump(model.LastFileQueued);

            var items = new MessageOutbox(Widget).PendingFiles();
            Assert.Equal([a, b], items.Select(i => i.FilePath)); // no copy: the file stays where the member keeps it
            Assert.Single(items.Select(i => i.BatchId).Distinct());
            Assert.NotNull(items[0].BatchId);
            Assert.Equal(["", "From the site"], items.Select(i => i.Body));
            Assert.Equal(("image/jpeg", "video/mp4"), (items[0].Mime, items[1].Mime));
            Assert.NotNull(items[0].ThumbB64);
            Assert.Equal(83d, items[1].DurationS);
            Assert.All(items, i => Assert.False(i.Copied));
            Assert.True(File.Exists(a));

            model.OpenConv("conv:3");
            var lines = model.TeamThread.Where(l => l.Upload is not null).ToList();
            Assert.Equal(2, lines.Count);
            Assert.All(lines, l => Assert.True(l.Sending));

            // A single file has no batch.
            model.Attach("conv:3", [Picked("c.pdf", 10)]);
            model.SendFiles("conv:3", "");
            Pump(model.LastFileQueued);
            Assert.Null(new MessageOutbox(Widget).PendingFiles().Single(i => i.FileName == "c.pdf").BatchId);
        });
    }

    [Fact]
    public void A_failed_file_retries_or_goes_with_our_copy_and_the_admin_thread_shows_it()
    {
        OnSta(model =>
        {
            Seed(model);
            var root = Path.Combine(_dir, "programdata-shared");
            var copy = TrayModel.CopyForSharing(Picked("nas.pdf", 50), "nas.pdf", root, Path.Combine(Widget, ChatFiles.FallbackCopiesFolder));
            Assert.StartsWith(root + "\\", copy, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(50, new FileInfo(copy).Length);
            // Only SYSTEM, Administrators and this member can read the copy's folder.
            var sddl = new DirectoryInfo(Path.GetDirectoryName(copy)!).GetAccessControl().GetSecurityDescriptorSddlForm(System.Security.AccessControl.AccessControlSections.Access);
            Assert.Contains("SY", sddl, StringComparison.Ordinal);
            Assert.Contains(System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value, sddl, StringComparison.Ordinal);
            Assert.DoesNotContain(";;;BU)", sddl, StringComparison.Ordinal);

            var outbox = new MessageOutbox(Widget);
            var item = outbox.AppendChatFiles([new OutboxReply("", "", Now, ChatKey: OutboxReply.AdminKey, FilePath: copy, FileName: "nas.pdf", FileSize: 50, Copied: true)])![0];
            var row = model.Thread.Single(r => r.Upload is not null);
            Assert.True(row.Pending);
            Assert.Equal("📎 nas.pdf", row.Snippet);

            outbox.Update(item.Id, i => i with { Refused = true, Error = "Files can be up to 1 GB on your plan." });
            row = model.Thread.Single(r => r.Upload is not null);
            Assert.True(row.Failed);
            Assert.Equal("Files can be up to 1 GB on your plan.", row.Meta);
            model.RetryFile(row.Upload!);
            Assert.False(new MessageOutbox(Widget).PendingFiles()[0].Refused);
            model.RemoveFile(row.Upload!);
            Assert.Empty(new MessageOutbox(Widget).PendingFiles());
            Assert.False(Directory.Exists(Path.GetDirectoryName(copy)));
        });
    }

    [Fact]
    public void Sharing_off_a_round_1_backend_or_an_older_service_hides_attach_and_refuses_to_send()
    {
        OnSta(model =>
        {
            Seed(model, files: On(allowed: false));
            Assert.False(model.FilesOn);
            Seed(model, files: On(relay: false)); // round 1: no relay, files can't stay on this PC
            Assert.False(model.FilesOn);
            var view = ThreadView(model);
            Assert.DoesNotContain(Descendants<Button>(view), b => Name(b) == ChatFiles.AttachTooltip);

            Seed(model, status: Status(filesVersion: null), files: null);
            Assert.False(model.FilesOn);
            Assert.False(model.FilesSupported);
            model.Attach("conv:3", [Picked()]);
            Assert.False(model.SendFiles("conv:3", "x"));
            Assert.Empty(new MessageOutbox(Widget).PendingFiles());
        });
    }

    // ---- Receive -----------------------------------------------------------------------------------------------------

    [Fact]
    public void A_document_shows_its_card_the_caption_and_download_with_save_as_not_the_fallback_text()
    {
        OnSta(model =>
        {
            Seed(model, Thread(FileLine(Pdf())));
            var view = ThreadView(model);
            var texts = Texts(view).Select(t => t.Text).ToList();
            Assert.Contains("Quote-Lumen.pdf", texts);
            Assert.Contains("2.1 MB · PDF", texts);
            Assert.Contains(Descendants<Button>(view), b => Name(b) == "Download Quote-Lumen.pdf");
            Assert.Contains(Descendants<Button>(view), b => Name(b) == "More ways to download Quote-Lumen.pdf");
            Assert.DoesNotContain(texts, t => t.StartsWith("📎", StringComparison.Ordinal));
            Assert.Contains(Descendants<TextBlock>(view), t => string.Concat(t.Inlines.OfType<System.Windows.Documents.Run>().Select(r => r.Text)) == "Final version");
            Assert.Equal("Priya: 📎 Quote-Lumen.pdf", model.ChatRows.Single(r => r.Key == "conv:3").Line);
        });
    }

    [Fact]
    public void Stopped_gone_or_old_files_say_not_shared_any_more_and_own_files_say_where_they_are_kept()
    {
        OnSta(model =>
        {
            foreach (var state in new[] { "stopped", "gone", "old" })
            {
                Seed(model, Thread(FileLine(Pdf(state: state))));
                var view = ThreadView(model);
                Assert.Contains(Texts(view), t => t.Text == ChatFiles.NotSharedText);
                Assert.DoesNotContain(Descendants<Button>(view), b => Name(b).StartsWith("Download", StringComparison.Ordinal));
            }

            var mine = FileLine(Pdf(mine: true), by: Me, name: "Vishal Shakya") with { Mine = true };
            Seed(model, Thread(mine));
            var own = ThreadView(model);
            Assert.Contains(Texts(own), t => t.Text == ChatFiles.OwnFileText);
            Assert.DoesNotContain(Descendants<Button>(own), b => Name(b).StartsWith("Download", StringComparison.Ordinal));
        });
    }

    [Fact]
    public void A_program_file_warns_and_asks_once_more_then_downloads_into_save_files_to()
    {
        OnSta(model =>
        {
            var file = Pdf(name: "setup.exe");
            Seed(model, Thread(FileLine(file)));
            Assert.Contains(Texts(ThreadView(model)), t => t.Text == "This is a program file. Open it only if you trust Priya.");

            model.Download(file, "Priya");
            Assert.Equal("This is a program file. Open it only if you trust Priya.", model.Snack?.Text);
            Assert.Empty(_asked);
            model.Snack!.Actions.Single(a => a.Label == "Download").Run();
            var ask = Assert.Single(_asked)!;
            Assert.Equal((FileAsk.Download, FileId, "setup.exe", "Priya"), (ask.Action, ask.Id, ask.Name, ask.SenderFirst));
            Assert.Equal(TrayModel.DownloadsFolder, ask.Folder); // Downloads\Zinwe by default
            Assert.Null(ask.Target);
        });
    }

    [Fact]
    public void Save_files_to_and_ask_where_to_save_each_file_and_save_as_choose_where_it_goes()
    {
        OnSta(model =>
        {
            Seed(model);
            var picked = new List<(string Name, string Folder)>();
            string? answer = null;
            model.SaveAsPicker = (name, folder) =>
            {
                picked.Add((name, folder));
                return answer;
            };
            var mine = Path.Combine(_dir, "My files");
            model.SetSaveFolder(mine);
            Assert.Equal(mine, model.SaveFolder);
            Assert.True(File.Exists(Path.Combine(Widget, "tray-settings.json"))); // the test's own settings file
            model.Download(Pdf(), "Priya");
            Assert.Equal(mine, _asked[^1]!.Folder);

            // Save as…: the dialog opens in Save files to; cancelling asks nothing.
            model.Download(Pdf(), "Priya", saveAs: true);
            Assert.Equal(("Quote-Lumen.pdf", mine), picked[^1]);
            Assert.Single(_asked);
            answer = Path.Combine(_dir, "elsewhere", "Quote.pdf");
            model.Download(Pdf(), "Priya", saveAs: true);
            Assert.Equal((answer, (string?)null), (_asked[^1]!.Target, _asked[^1]!.Folder));

            // Ask where to save each file: every Download asks.
            model.SetAskWhere(true);
            model.Download(Pdf(), "Priya");
            Assert.Equal(3, picked.Count);
            model.SetSaveFolder(null);
            Assert.Equal(TrayModel.DownloadsFolder, model.SaveFolder);
            Assert.Equal("Downloads › Zinwe", TrayModel.FolderLabel(model.SaveFolder));
        });
    }

    [Fact]
    public void The_ring_shows_the_percent_waiting_shows_the_senders_name_and_cancel_asks_the_service()
    {
        OnSta(model =>
        {
            var file = Pdf();
            Seed(model, Thread(FileLine(file)), On(downloads: [new FileState(FileId, FileState.Working, Received: 1_101_005, Total: 2_202_009)]));
            var view = ThreadView(model);
            Assert.Contains(Texts(view), t => t.Text == "50%");
            Assert.Contains(Descendants<Button>(view), b => Name(b) == "Cancel download of Quote-Lumen.pdf");
            Assert.True(model.FilesBusy);

            Seed(model, Thread(FileLine(file)), On(downloads: [new FileState(FileId, FileState.Waiting, WaitingFor: "Priya")], version: 2));
            view = ThreadView(model);
            Assert.Contains(Texts(view), t => t.Text == "Waiting for Priya's PC…");
            model.CancelDownload(file);
            Assert.Equal((FileAsk.Cancel, FileId), (_asked[^1]!.Action, _asked[^1]!.Id));

            var ring = (Grid)FileCard.Ring(42, 48);
            Assert.Contains(Descendants<TextBlock>(ring), t => t.Text == "42%");
        });
    }

    [Fact]
    public void A_downloaded_file_offers_open_and_show_in_folder_only_where_this_tray_asked()
    {
        OnSta(model =>
        {
            var file = Pdf();
            Seed(model, Thread(FileLine(file)));
            model.Download(file, "Priya");
            var saved = Path.Combine(TrayModel.DownloadsFolder, "Quote-Lumen (2).pdf");
            Directory.CreateDirectory(TrayModel.DownloadsFolder);
            File.WriteAllText(saved, "%PDF");
            model.TakeFiles(On(downloads: [new FileState(FileId, FileState.Done, saved)], version: 2));
            Assert.Equal(saved, model.DownloadedPath(file));
            var view = ThreadView(model);
            Assert.Contains(Descendants<Button>(view), b => Name(b) == "Open Quote-Lumen.pdf");
            Assert.Contains(Descendants<Button>(view), b => Name(b) == "Show Quote-Lumen.pdf in its folder");

            // A path anywhere else (the service is another process), or for a file this tray never asked for, is never opened.
            Assert.False(model.IsOurDownload(FileId, @"C:\Windows\System32\cmd.exe"));
            Assert.False(model.IsOurDownload(FileId, Path.Combine(TrayModel.DownloadsFolder, "..", "evil.exe")));
            Assert.False(model.IsOurDownload("aaaaaaaa-1d4b-4c55-9a77-0b1f3c2d4e5f", saved));
            model.TakeFiles(On(downloads: [new FileState(FileId, FileState.Done, @"C:\Windows\notepad.exe")], version: 3));
            Assert.Null(model.DownloadedPath(file));
        });
    }

    [Fact]
    public void A_video_tile_shows_its_length_and_a_downloaded_photo_opens_in_the_viewer()
    {
        OnSta(model =>
        {
            var video = Media(1, ChatFile.VideoKind, duration: 83);
            var photo = Media(2);
            Seed(model, Thread(FileLine(video, 41), FileLine(photo, 42)));
            var view = ThreadView(model);
            Assert.Contains(Texts(view), t => t.Text == "1:23");
            Assert.Contains(Descendants<Button>(view), b => Name(b) == "Download photo2.jpg");

            string? shown = null;
            model.ViewerRequested += (path, _) => shown = path;
            model.OpenMedia(photo, "Priya", mine: false); // not here yet: download it, then the viewer
            Assert.Equal(FileAsk.Download, _asked[^1]!.Action);
            var saved = Path.Combine(TrayModel.DownloadsFolder, "photo2.jpg");
            Directory.CreateDirectory(TrayModel.DownloadsFolder);
            File.WriteAllBytes(saved, Jpeg());
            model.TakeFiles(On(downloads: [new FileState(photo.Id, FileState.Working, Total: 10)], version: 2));
            model.TakeFiles(On(downloads: [new FileState(photo.Id, FileState.Done, saved)], version: 3));
            Assert.Equal(saved, shown);
        });
    }

    [Fact]
    public void Photos_and_videos_sent_together_show_as_one_grid_with_plus_n_on_the_fourth()
    {
        OnSta(model =>
        {
            var lines = Enumerable.Range(1, 6).Select(i => FileLine(Media(i, i == 2 ? ChatFile.VideoKind : ChatFile.ImageKind, "b-1") with { Caption = i == 6 ? "Site" : null }, 40 + i)).ToArray();
            Seed(model, Thread(lines));
            model.OpenConv("conv:3");
            var batched = TrayModel.Batched(model.TeamThread);
            var line = Assert.Single(batched);
            Assert.Equal(6, line.Batch!.Count);
            Assert.Equal(46, line.Message.Id); // the last line carries the caption

            var view = ThreadView(model);
            Assert.Contains(Texts(view), t => t.Text == "+2");
            Assert.Contains(Texts(view), t => t.Text.StartsWith("6 files", StringComparison.Ordinal));
            Assert.Contains(Descendants<Button>(view), b => Name(b) == "Download all 6");
            Assert.Contains(Descendants<TextBlock>(view), t => string.Concat(t.Inlines.OfType<System.Windows.Documents.Run>().Select(r => r.Text)) == "Site");
        });
    }

    [Fact]
    public void A_thumbnail_is_asked_for_once_and_only_a_path_in_the_widget_folder_is_used()
    {
        OnSta(model =>
        {
            var photo = Media(1) with { ThumbUrl = "https://x/thumb/" };
            Seed(model);
            Assert.Null(model.ThumbFor(photo));
            Assert.Null(model.ThumbFor(photo));
            Assert.Null(model.ThumbFor(Media(2))); // no preview on the server
            var ask = Assert.Single(_asked)!;
            Assert.Equal((FileAsk.Preview, photo.Id, false), (ask.Action, ask.Id, ask.Legacy));

            var folder = Path.Combine(Widget, ChatFiles.PreviewFolder);
            Directory.CreateDirectory(folder);
            var jpg = Path.Combine(folder, photo.Id + ".jpg");
            File.WriteAllBytes(jpg, Jpeg());
            model.TakeFiles(new ChatFilesReply(true, new ChatFilesInfo(true, Relay: true), [], [new FileState(photo.Id, FileState.Done, jpg)], 2));
            Assert.Equal(jpg, model.ThumbFor(photo));
            model.TakeFiles(new ChatFilesReply(true, new ChatFilesInfo(true, Relay: true), [], [new FileState(photo.Id, FileState.Done, @"C:\x.jpg")], 3));
            Assert.Null(model.ThumbFor(photo));
        });
    }

    [Fact]
    public void My_shared_files_lists_the_chat_and_stop_sharing_asks_the_service()
    {
        OnSta(model =>
        {
            var shared = new SharedFile(FileId, "Site visit.mp4", 734_003_200, "conv:3", Now, ChatName: "Design");
            Seed(model, files: On(shared: [shared]));
            Assert.Equal([shared], model.SharedFiles);
            var row = PushView.SharedRow(model, shared);
            Layout(row, 380, 80);
            Assert.Contains(Texts(row), t => t.Text.StartsWith("Design · ", StringComparison.Ordinal) && t.Text.EndsWith("700 MB", StringComparison.Ordinal));
            var stop = Descendants<Button>(row).Single(b => Name(b) == "Stop sharing: Site visit.mp4");
            stop.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Assert.Equal((FileAsk.Stop, FileId), (_asked[^1]!.Action, _asked[^1]!.Id));

            model.Open(Push.SharedFiles);
            Assert.Equal("Me › My shared files", IssueReport.Screen(Tab.Me, Push.SharedFiles));
            Assert.Equal("Design", model.ChatName("conv:3"));
        });
    }

    [Fact]
    public void Settings_files_shows_save_files_to_and_ask_where()
    {
        OnSta(model =>
        {
            Seed(model);
            model.Open(Push.Settings);
            var view = new PushView { Model = model, Width = 380, Height = 900 };
            view.Render();
            Layout(view);
            var texts = Texts(view).Select(t => t.Text).ToList();
            Assert.Contains(ChatFiles.SaveToText, texts);
            Assert.Contains("Downloads › Zinwe", texts);
            Assert.Contains(ChatFiles.AskWhereText, texts);
            Assert.Contains(ChatFiles.MySharedFilesText, texts);
        });
    }

    [Fact]
    public void The_admin_chat_draws_a_file_line_with_its_caption_and_groups_a_batch()
    {
        OnSta(model =>
        {
            var notes = new NotificationStore(Path.Combine(_dir, "notifications.json"));
            notes.Save(new NotificationFile(
            [
                new("301", Now.AddMinutes(-3), "info", "File", "📎 Quote-Lumen.pdf (2.1 MB)\nFinal version", Read: true, Kind: "chat", File: Pdf()),
                new("302", Now.AddMinutes(-2), "info", "", "📎 photo1.jpg", Read: true, Kind: "chat", File: Media(1, batch: "b")),
                new("303", Now.AddMinutes(-1), "info", "", "📎 photo2.jpg", Read: true, Kind: "chat", File: Media(2, batch: "b") with { Caption = "Two" }),
            ]));
            Seed(model);
            var rows = Zinwe.Agent.Tray.Widget.ChatThread.Build(notes.Load().Items, [], [], [], "Ananya", Now).Where(r => r.Kind == ThreadKind.Text).ToList();
            Assert.Equal(2, rows.Count);
            Assert.Equal(("Final version", Pdf()), (rows[0].Text, rows[0].File));
            Assert.Equal(2, rows[1].Batch!.Count);
            Assert.Equal("Two", rows[1].Text);
            Assert.Equal("📎 Quote-Lumen.pdf", ChatFeed.Preview(notes.Load().Items.Single(i => i.Id == "301")));
        });
    }

    [Fact]
    public void An_older_backend_without_files_shows_the_fallback_text_as_today()
    {
        OnSta(model =>
        {
            var old = new TeamMessage(41, "member", 9, "Priya Nair", "📎 Quote-Lumen.pdf (2.1 MB)\nFinal version", At: Now.AddMinutes(-5));
            Seed(model, Thread(old), new ChatFilesReply(true, null, [], [], 1));
            Assert.False(model.FilesOn);
            var view = ThreadView(model);
            Assert.Contains(Descendants<TextBlock>(view), t => string.Concat(t.Inlines.OfType<System.Windows.Documents.Run>().Select(r => r.Text)).StartsWith("📎 Quote-Lumen.pdf (2.1 MB)", StringComparison.Ordinal));
            Assert.DoesNotContain(Descendants<Button>(view), b => Name(b).StartsWith("Download", StringComparison.Ordinal));
            Assert.Equal("Priya: 📎 Quote-Lumen.pdf", model.ChatRows.Single(r => r.Key == "conv:3").Line);
        });
    }

    [Fact]
    public void A_round_1_file_still_downloads_from_the_server_copy()
    {
        OnSta(model =>
        {
            var legacy = new ChatFile(FileId, "old.pdf", 1000, "application/pdf", ChatFile.FileKind, null, false, Now.AddDays(7), false,
                "https://dev.backend.zinwe.io/api/devices/chat/files/" + FileId + "/");
            Seed(model, Thread(FileLine(legacy)));
            Assert.Contains(Descendants<Button>(ThreadView(model)), b => Name(b) == "Download old.pdf");
            model.Download(legacy, "Priya");
            Assert.True(_asked[^1]!.Legacy);
        });
    }

    [Fact]
    public void A_file_line_can_be_deleted_but_not_edited()
    {
        OnSta(model =>
        {
            var mine = FileLine(Pdf(mine: true), by: Me, name: "Vishal Shakya") with { At = Now.AddMinutes(-1), Mine = true };
            Seed(model, new ChatReply(true, TaskBackend.On, [Group(mine)], [], new Core.Chat.ChatThread("conv:3", [mine]), Now, 2, Gone: []));
            model.OpenConv("conv:3");
            model.StartEdit(mine);
            Assert.Null(model.TeamEditing);
            Assert.Equal("A file can’t be edited. Delete it and send it again.", model.Snack?.Text);
            var menu = new ContextMenu();
            TeamThreadView.FillLineMenu(menu, model, model.TeamThread.Single());
            var labels = menu.Items.OfType<MenuItem>().Select(i => (string)i.Header).ToList();
            Assert.DoesNotContain("Edit", labels);
            Assert.Contains("Delete for everyone", labels);
        });
    }

    [Fact]
    public void File_cards_render_in_dark_mode_too()
    {
        OnSta(model =>
        {
            var app = (App)Application.Current;
            Seed(model, Thread(FileLine(Pdf(name: "tool.ps1"))));
            try
            {
                foreach (var (k, hex) in HighContrastTheme.Dark)
                {
                    var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
                    brush.Freeze();
                    app.Resources[k] = brush;
                }
                var view = ThreadView(model);
                var name = Texts(view).Single(t => t.Text == "tool.ps1");
                Assert.Equal(((SolidColorBrush)app.Resources["Z.Ink"]).Color, ((SolidColorBrush)name.Foreground).Color);
            }
            finally
            {
                foreach (var (k, _) in HighContrastTheme.Dark)
                {
                    app.Resources.Remove(k);
                }
            }
        });
    }

    // ---- Helpers ---------------------------------------------------------------------------------------------------

    private static void Layout(FrameworkElement el, double? w = null, double? h = null)
    {
        var size = new Size(w ?? el.Width, h ?? el.Height);
        el.Measure(size);
        el.Arrange(new Rect(size));
        el.UpdateLayout();
    }

    private static string Name(DependencyObject d) => System.Windows.Automation.AutomationProperties.GetName(d);

    private static IEnumerable<TextBlock> Texts(DependencyObject root) => Descendants<TextBlock>(root);

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T hit)
            {
                yield return hit;
            }
            foreach (var deeper in Descendants<T>(child))
            {
                yield return deeper;
            }
        }
    }
}

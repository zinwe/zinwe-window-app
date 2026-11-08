using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Zinwe.Agent.Core.Backend;
using Zinwe.Agent.Core.Chat;
using Zinwe.Agent.Core.Ipc;
using Zinwe.Agent.Core.Messages;
using Zinwe.Agent.Core.Policy;
using Zinwe.Agent.Tray.Views;
using Zinwe.Agent.Tray.Widget;

namespace Zinwe.Agent.Tray.Tests;

/// <summary>
/// v1.5 · file sharing in chat on the tray: attach (the chip, refused over 25 MB at once), send (the copy into the widget
/// folder's outbox-files and the chat_file outbox item, "Sending…" / Failed with Retry), the file bubbles (card, kept line,
/// expired, risky, thumbnail-less image, caption instead of the fallback body), downloads (risky asks once more; only
/// Downloads\Zinwe is opened), the "📎 name" list preview, and an older service or backend (no attach button, the fallback
/// text as before). Everything runs in a temp folder: the member's real outbox, chat and Downloads are never touched.
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

    private static ChatFile Pdf(bool expired = false, string name = "Quote-Lumen.pdf", bool risky = false) =>
        new(FileId, name, 2_202_009, "application/pdf", ChatFile.FileKind, "Final version", risky, Now.AddDays(7), expired,
            expired ? null : "https://dev.backend.zinwe.io/api/devices/chat/files/" + FileId + "/");

    private static AgentStatus Status(int? filesVersion = 1) => new(AgentState.Connected, "1.5.0", "PC", 1, Now, null,
        Entitlements: [PlanModules.WorkTime, PlanModules.TeamChat], CompanyName: "Skytechno", OwnerName: "Ananya Rao", TasksVersion: 1,
        TodosVersion: 1, ChatVersion: 1, FilesVersion: filesVersion);

    private static ChatFilesReply On(bool allowed = true) => new(true, new ChatFilesInfo(allowed), [], [], 1);

    private static TeamConversation Group(TeamMessage? last = null) => new(3, TeamConversation.GroupKind, "Design",
        Members: [new(Me, "Vishal Shakya"), new(9, "Priya Nair")], Last: last, LastAt: last?.At);

    private static TeamMessage FileLine(ChatFile file, long by = 9, string name = "Priya Nair") =>
        new(41, "member", by, name, "📎 " + file.Name + " (2.1 MB)\nFinal version", At: Now.AddMinutes(-5), File: file);

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

    private void Seed(TrayModel model, ChatReply? chat = null, ChatFilesReply? files = null, AgentStatus? status = null)
    {
        model.UseForPreview(status ?? Status(), new TasksReply(true, TaskBackend.On, [], Now, 1),
            chat ?? new ChatReply(true, TaskBackend.On, [Group()], [], null, Now, 1),
            new TodosReply(true, TaskBackend.On, [], Me, Now, 1), new TraySettings(), inApp: true, local: new LocalTaskSource(false, _dir));
        model.UseFilesForTests(Widget, ask =>
        {
            _asked.Add(ask);
            return Task.FromResult<ChatFilesReply?>(null);
        }, files ?? On());
    }

    private string Picked(string name = "Quote-Lumen.pdf", long bytes = 4096)
    {
        var folder = Path.Combine(_dir, "picked");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, name);
        using var f = new FileStream(path, FileMode.Create);
        f.SetLength(bytes); // sparse enough: a 25 MB test file costs nothing
        return path;
    }

    private static void Pump(Task? task)
    {
        // The copy runs on the thread pool and comes back to this STA thread's dispatcher.
        var frame = new System.Windows.Threading.DispatcherFrame();
        _ = (task ?? Task.CompletedTask).ContinueWith(_ => frame.Continue = false, TaskScheduler.Default);
        System.Windows.Threading.Dispatcher.PushFrame(frame);
    }

    // ---- Attach and send -------------------------------------------------------------------------------------------

    [Fact]
    public void A_file_over_25_MB_is_refused_at_once_and_an_ok_one_waits_as_a_chip()
    {
        OnSta(model =>
        {
            Seed(model);
            Assert.True(model.FilesOn);
            Assert.False(model.Attach("conv:3", Picked("big.zip", ChatFiles.MaxBytes + 1)));
            Assert.Equal(ChatFiles.TooBigText, model.Snack?.Text);
            Assert.Null(model.AttachFor("conv:3"));

            Assert.False(model.Attach("conv:3", Picked("empty.txt", 0)));
            Assert.Equal(ChatFiles.EmptyText, model.Snack?.Text);

            Assert.True(model.Attach("conv:3", Picked("exactly.bin", ChatFiles.MaxBytes)));
            var draft = model.AttachFor("conv:3")!;
            Assert.Equal("exactly.bin", draft.Name);
            Assert.Equal(ChatFiles.MaxBytes, draft.Size);
            model.RemoveAttach("conv:3");
            Assert.Null(model.AttachFor("conv:3"));
        });
    }

    [Fact]
    public void Sending_copies_the_file_into_outbox_files_and_queues_a_chat_file_item_with_the_caption()
    {
        OnSta(model =>
        {
            Seed(model);
            var picked = Picked();
            model.Attach("conv:3", picked);
            Assert.True(model.SendFile("conv:3", "  Final version "));
            Assert.Null(model.AttachFor("conv:3"));
            Assert.Single(model.PendingFileItems("conv:3")); // "Sending…" while it is still being copied
            Pump(model.LastFileQueued);

            var item = Assert.Single(new MessageOutbox(Widget).PendingFiles());
            Assert.Equal("conv:3", item.ChatKey);
            Assert.Equal("Final version", item.Body);
            Assert.Equal("Quote-Lumen.pdf", item.FileName);
            Assert.Equal(4096, item.FileSize);
            var copy = item.FilePath!;
            Assert.StartsWith(Path.Combine(Widget, ChatFiles.OutboxFolder) + "\\", copy, StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(copy));
            Assert.True(File.Exists(picked)); // the member's own file is left alone

            // The thread shows it as "Sending…" with its name.
            model.OpenConv("conv:3");
            var line = model.TeamThread.Single(l => l.Upload is not null);
            Assert.True(line.Sending);
            Assert.Equal("Final version", line.Message.Body);

            // Failed (the service refused it): Retry puts it back; Delete takes it and its copy away.
            new MessageOutbox(Widget).Update(item.Id, i => i with { Refused = true, Error = "Too many files waiting. Send or remove some first." });
            var failed = model.TeamThread.Single(l => l.Upload is not null);
            Assert.False(failed.Sending);
            model.RetryFile(failed.Upload!);
            Assert.False(new MessageOutbox(Widget).PendingFiles()[0].Refused);
            model.RemoveFile(failed.Upload!);
            Assert.Empty(new MessageOutbox(Widget).PendingFiles());
            Assert.False(Directory.Exists(Path.GetDirectoryName(copy)));
        });
    }

    [Fact]
    public void A_file_to_the_admin_shows_in_the_admin_thread_and_fails_with_the_words()
    {
        OnSta(model =>
        {
            Seed(model);
            model.Attach(OutboxReply.AdminKey, Picked("notes.txt", 1200));
            Assert.True(model.SendFile(OutboxReply.AdminKey, ""));
            Pump(model.LastFileQueued);
            var row = model.Thread.Single(r => r.Upload is not null);
            Assert.True(row.Pending);
            Assert.Equal("Sending…", row.Meta);
            Assert.Equal("📎 notes.txt", row.Snippet);

            var outbox = new MessageOutbox(Widget);
            outbox.Update(outbox.PendingFiles()[0].Id, i => i with { Refused = true, Error = null });
            row = model.Thread.Single(r => r.Upload is not null);
            Assert.True(row.Failed);
            Assert.Equal(ChatFiles.FailedText, row.Meta); // "Couldn't send. Try again."
        });
    }

    [Fact]
    public void Sharing_off_or_an_older_service_hides_attach_and_refuses_to_send()
    {
        OnSta(model =>
        {
            Seed(model, files: On(allowed: false));
            Assert.False(model.FilesOn);

            Seed(model, status: Status(filesVersion: null), files: null);
            Assert.False(model.FilesOn);
            model.Attach("conv:3", Picked());
            Assert.False(model.SendFile("conv:3", "x"));
            Assert.Empty(new MessageOutbox(Widget).PendingFiles());

            // The composer of a team thread has no paperclip then.
            model.OpenConv("conv:3");
            model.RemoveAttach("conv:3");
            var view = new TeamThreadView { Model = model, Width = 380, Height = 560 };
            view.Render();
            Layout(view);
            Assert.DoesNotContain(Descendants<Button>(view), b => Name(b) == ChatFiles.AttachTooltip);

            Seed(model);
            view.Render();
            Layout(view);
            Assert.Contains(Descendants<Button>(view), b => Name(b) == ChatFiles.AttachTooltip);
        });
    }

    // ---- Receive ---------------------------------------------------------------------------------------------------

    [Fact]
    public void A_file_line_shows_the_card_the_kept_line_and_the_caption_not_the_fallback_text()
    {
        OnSta(model =>
        {
            var line = FileLine(Pdf());
            Seed(model, new ChatReply(true, TaskBackend.On, [Group(line)], [], new Core.Chat.ChatThread("conv:3", [line]), Now, 2));
            model.OpenConv("conv:3");
            var view = new TeamThreadView { Model = model, Width = 380, Height = 560 };
            view.Render();
            Layout(view);
            var texts = Texts(view).Select(t => t.Text).ToList();
            Assert.Contains("Quote-Lumen.pdf", texts);
            Assert.Contains("2.1 MB · PDF", texts);
            Assert.Contains(ChatFiles.KeptUntil(Pdf().ExpiresAt!.Value), texts);
            Assert.Contains(Descendants<Button>(view), b => Name(b) == "Download Quote-Lumen.pdf");
            Assert.DoesNotContain(texts, t => t.StartsWith("📎", StringComparison.Ordinal)); // the fallback body is not shown
            Assert.Contains(Descendants<TextBlock>(view), t => string.Concat(t.Inlines.OfType<System.Windows.Documents.Run>().Select(r => r.Text)) == "Final version");

            // The chat list says "📎 name".
            Assert.Equal("Priya: 📎 Quote-Lumen.pdf", model.ChatRows.Single(r => r.Key == "conv:3").Line);
        });
    }

    [Fact]
    public void An_expired_file_is_a_grey_card_that_says_so()
    {
        OnSta(model =>
        {
            var line = FileLine(Pdf(expired: true));
            Seed(model, new ChatReply(true, TaskBackend.On, [Group(line)], [], new Core.Chat.ChatThread("conv:3", [line]), Now, 2));
            model.OpenConv("conv:3");
            var view = new TeamThreadView { Model = model, Width = 380, Height = 560 };
            view.Render();
            Layout(view);
            Assert.Contains(Texts(view), t => t.Text == ChatFiles.ExpiredText);
            Assert.DoesNotContain(Descendants<Button>(view), b => Name(b).StartsWith("Download", StringComparison.Ordinal));
        });
    }

    [Fact]
    public void A_program_file_warns_and_asks_once_more_before_it_downloads()
    {
        OnSta(model =>
        {
            var file = Pdf(name: "setup.exe");
            var line = FileLine(file);
            Seed(model, new ChatReply(true, TaskBackend.On, [Group(line)], [], new Core.Chat.ChatThread("conv:3", [line]), Now, 2));
            model.OpenConv("conv:3");
            var view = new TeamThreadView { Model = model, Width = 380, Height = 560 };
            view.Render();
            Layout(view);
            Assert.Contains(Texts(view), t => t.Text == "This is a program file. Open it only if you trust Priya.");

            model.Download(file, "Priya");
            Assert.Equal("This is a program file. Open it only if you trust Priya.", model.Snack?.Text);
            Assert.Empty(_asked); // nothing asked of the service yet
            model.Snack!.Actions.Single(a => a.Label == "Download").Run();
            var ask = Assert.Single(_asked)!;
            Assert.Equal(FileAsk.Download, ask.Action);
            Assert.Equal(FileId, ask.Id);
        });
    }

    [Fact]
    public void A_downloaded_file_offers_open_and_show_in_folder_only_from_downloads_zinwe()
    {
        OnSta(model =>
        {
            var file = Pdf();
            var saved = Path.Combine(TrayModel.DownloadsFolder, "Quote-Lumen (2).pdf");
            Directory.CreateDirectory(TrayModel.DownloadsFolder);
            File.WriteAllText(saved, "%PDF");
            var line = FileLine(file);
            Seed(model, new ChatReply(true, TaskBackend.On, [Group(line)], [], new Core.Chat.ChatThread("conv:3", [line]), Now, 2),
                new ChatFilesReply(true, new ChatFilesInfo(true), [new Core.Chat.FileState(FileId, Core.Chat.FileState.Done, saved)], [], 2));
            Assert.Equal(saved, model.DownloadedPath(file));
            model.OpenConv("conv:3");
            var view = new TeamThreadView { Model = model, Width = 380, Height = 560 };
            view.Render();
            Layout(view);
            Assert.Contains(Descendants<Button>(view), b => Name(b) == "Open Quote-Lumen.pdf");
            Assert.Contains(Descendants<Button>(view), b => Name(b) == "Show Quote-Lumen.pdf in its folder");

            // A path anywhere else (the service is another process) is never opened.
            Assert.False(TrayModel.IsOurDownload(@"C:\Windows\System32\cmd.exe"));
            Assert.False(TrayModel.IsOurDownload(Path.Combine(TrayModel.DownloadsFolder, "..", "evil.exe")));
            Assert.True(TrayModel.IsOurDownload(saved));
            Seed(model, files: new ChatFilesReply(true, new ChatFilesInfo(true), [new Core.Chat.FileState(FileId, Core.Chat.FileState.Done, @"C:\Windows\notepad.exe")], [], 3));
            Assert.Null(model.DownloadedPath(file));
        });
    }

    [Fact]
    public void An_image_asks_the_service_for_its_preview_once_and_a_big_one_never()
    {
        OnSta(model =>
        {
            var photo = new ChatFile(FileId, "photo.png", 120_000, "image/png", ChatFile.ImageKind, null, false, Now.AddDays(7), false, "u");
            var big = photo with { Id = "aaaaaaaa-1d4b-4c55-9a77-0b1f3c2d4e5f", Size = ChatFiles.PreviewMaxBytes + 1 };
            Seed(model);
            Assert.Null(model.PreviewFor(photo));
            Assert.Null(model.PreviewFor(photo));
            Assert.Null(model.PreviewFor(big));
            var ask = Assert.Single(_asked)!;
            Assert.Equal(FileAsk.Preview, ask.Action);
            Assert.Equal(FileId, ask.Id);

            // Once fetched (into the widget folder's chat-previews), the path is used; one anywhere else is not.
            var folder = Path.Combine(Widget, ChatFiles.PreviewFolder);
            Directory.CreateDirectory(folder);
            var png = Path.Combine(folder, FileId + ".png");
            File.WriteAllBytes(png, [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
            Seed(model, files: new ChatFilesReply(true, new ChatFilesInfo(true), [], [new Core.Chat.FileState(FileId, Core.Chat.FileState.Done, png)], 2));
            Assert.Equal(png, model.PreviewFor(photo));
            Seed(model, files: new ChatFilesReply(true, new ChatFilesInfo(true), [], [new Core.Chat.FileState(FileId, Core.Chat.FileState.Done, @"C:\x.png")], 3));
            Assert.Null(model.PreviewFor(photo));
        });
    }

    [Fact]
    public void The_admin_chat_draws_a_file_line_with_its_caption_and_previews_it_as_a_clip()
    {
        OnSta(model =>
        {
            var notes = new NotificationStore(Path.Combine(_dir, "notifications.json"));
            notes.Save(new NotificationFile(
            [
                new("301", Now.AddMinutes(-3), "info", "File", "📎 Quote-Lumen.pdf (2.1 MB)\nFinal version", Read: true, Kind: "chat", File: Pdf()),
            ]));
            Seed(model);
            var rows = Zinwe.Agent.Tray.Widget.ChatThread.Build(notes.Load().Items, [], [], [], "Ananya", Now);
            var row = rows.Single(r => r.Kind == ThreadKind.Text);
            Assert.Equal(Pdf(), row.File);
            Assert.Equal("Final version", row.Text); // the caption, not "📎 … (2.1 MB)" nor the title
            Assert.Equal("📎 Quote-Lumen.pdf", ChatFeed.Preview(notes.Load().Items[0]));
        });
    }

    [Fact]
    public void An_older_backend_without_files_shows_the_fallback_text_as_today()
    {
        OnSta(model =>
        {
            var old = new TeamMessage(41, "member", 9, "Priya Nair", "📎 Quote-Lumen.pdf (2.1 MB)\nFinal version", At: Now.AddMinutes(-5));
            Seed(model, new ChatReply(true, TaskBackend.On, [Group(old)], [], new Core.Chat.ChatThread("conv:3", [old]), Now, 2), files: new ChatFilesReply(true, null, [], [], 1));
            Assert.False(model.FilesOn);
            model.OpenConv("conv:3");
            var view = new TeamThreadView { Model = model, Width = 380, Height = 560 };
            view.Render();
            Layout(view);
            Assert.Contains(Descendants<TextBlock>(view), t => string.Concat(t.Inlines.OfType<System.Windows.Documents.Run>().Select(r => r.Text)).StartsWith("📎 Quote-Lumen.pdf (2.1 MB)", StringComparison.Ordinal));
            Assert.DoesNotContain(Descendants<Button>(view), b => Name(b).StartsWith("Download", StringComparison.Ordinal));
            Assert.Equal("Priya: 📎 Quote-Lumen.pdf", model.ChatRows.Single(r => r.Key == "conv:3").Line);
        });
    }

    [Fact]
    public void A_file_line_can_be_deleted_but_not_edited()
    {
        OnSta(model =>
        {
            var mine = FileLine(Pdf(), by: Me, name: "Vishal Shakya") with { At = Now.AddMinutes(-1), Mine = true };
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
            var line = FileLine(Pdf(name: "tool.ps1"));
            Seed(model, new ChatReply(true, TaskBackend.On, [Group(line)], [], new Core.Chat.ChatThread("conv:3", [line]), Now, 2));
            model.OpenConv("conv:3");
            try
            {
                foreach (var (k, hex) in HighContrastTheme.Dark)
                {
                    var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
                    brush.Freeze();
                    app.Resources[k] = brush;
                }
                var view = new TeamThreadView { Model = model, Width = 380, Height = 560 };
                view.Render();
                Layout(view);
                var name = Texts(view).Single(t => t.Text == "tool.ps1");
                // The card's words take the theme's ink, not a fixed colour.
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

    private static void Layout(FrameworkElement el)
    {
        el.Measure(new Size(el.Width, el.Height));
        el.Arrange(new Rect(0, 0, el.Width, el.Height));
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

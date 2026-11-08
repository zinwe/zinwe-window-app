using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Zinwe.Agent.Core.Backend;
using Zinwe.Agent.Core.Chat;
using Zinwe.Agent.Core.Ipc;
using Zinwe.Agent.Core.Work;
using Zinwe.Agent.Tray.Views;
using Zinwe.Agent.Tray.Widget;

namespace Zinwe.Agent.Tray.Tests;

/// <summary>
/// v1.4.1 on the member's window: the server's pins and mutes win, a deleted group closes with a note, @mentions are
/// highlighted (and offered in the composer), own lines can be edited or deleted for 15 minutes, refusals show once, and
/// "Ask for a day off" with the recent asks on Home. Nothing here writes to the outbox: the member's real file stays clean.
/// ZINWE_V14_SHOTS=&lt;folder&gt; also saves PNGs of the new views.
/// </summary>
[Collection(WpfCollection.Name)]
public sealed class V141Tests : IDisposable
{
    private const long Priya = 4, Karan = 5, Emily = 6, Fatima = 9;
    private static readonly DateTimeOffset Now = DateTimeOffset.Now;
    private readonly string _prefsFile = Path.Combine(Path.GetTempPath(), "zinwe-v141-prefs-" + Guid.NewGuid().ToString("N") + ".json");

    public V141Tests() => ChatPrefs.PathOverride = _prefsFile;

    public void Dispose()
    {
        ChatPrefs.PathOverride = null;
        try
        {
            File.Delete(_prefsFile);
        }
        catch (IOException)
        {
        }
    }

    // ---- Pins and mutes: the server is the truth -----------------------------------------------------

    private static TeamConversation Conv(long id, bool pinned = false, bool muted = false, double hoursAgo = 1) =>
        new(id, Name: "C" + id, Pinned: pinned, Muted: muted, LastAt: Now.AddHours(-hoursAgo));

    [Fact]
    public void The_servers_pins_and_mutes_win_unless_a_newer_choice_waits_to_be_sent()
    {
        var local = new ChatPrefs(["conv:3", "direct:9"], ["conv:5", "admin"], ["conv:3"], ["conv:3", "conv:5"]);
        var listed = new[] { Conv(3), Conv(5, pinned: true), Conv(7, muted: true) };

        var next = ChatPrefs.Reconcile(local, listed, new HashSet<string>(), new HashSet<string>());
        Assert.Equal(["direct:9", "conv:5"], next.Pins); // unpinned elsewhere; pinned on another PC; a teammate row stays local
        Assert.Equal(["admin", "conv:7"], next.Muted); // unmuted on the web; the admin chat's mute stays local
        Assert.Equal(["conv:3"], next.Marked);

        // A pin still in the outbox wins over the list that hasn't seen it yet.
        var waiting = ChatPrefs.Reconcile(local, listed, new HashSet<string> { "conv:3" }, new HashSet<string> { "conv:5" });
        Assert.Contains("conv:3", waiting.Pins);
        Assert.Contains("conv:5", waiting.Muted);

        // Nothing new: the same instance (no file write).
        Assert.Same(next, ChatPrefs.Reconcile(next, listed, new HashSet<string>(), new HashSet<string>()));
    }

    [Fact]
    public void A_deleted_group_is_forgotten_and_pins_stay_at_three()
    {
        var local = new ChatPrefs(["conv:2", "conv:3", "conv:4"], ["conv:2"], ["conv:2"], ["conv:2"]);
        var listed = new[] { Conv(3, pinned: true), Conv(4, pinned: true), Conv(5, pinned: true), Conv(6, pinned: true, hoursAgo: 5) };
        var next = ChatPrefs.Reconcile(local, listed, new HashSet<string>(), new HashSet<string>());
        Assert.DoesNotContain("conv:2", next.Pins.Concat(next.Muted).Concat(next.Marked).Concat(next.Known));
        Assert.Equal(3, next.Pins.Count);
        Assert.Equal(["conv:3", "conv:4", "conv:5"], next.Pins);
        Assert.Equal(new ChatPrefs([], [], [], []).Forget("conv:1").Pins, []);
    }

    // ---- The model --------------------------------------------------------------------------------------

    private static void OnSta(Action<TrayModel> body)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                Application.ResourceAssembly ??= typeof(App).Assembly;
                var app = Application.Current as App ?? new App();
                app.InitializeComponent();
                var store = new NotificationStore();
                using var watcher = new NotificationWatcher(store, TimeSpan.FromMinutes(5));
                using var model = new TrayModel(store, watcher, () => Task.FromResult<string?>(null));
                Seed(model);
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
        Assert.Null(error?.ToString());
    }

    private static readonly string ConvsJson = $$$"""
        [{"id": 3, "kind": "group", "name": "Design", "unread": 2, "pinned": true,
          "members": [{"member_id": 6, "name": "Emily Carter", "role": "designer"}, {"member_id": 9, "name": "Fatima Sheikh", "role": "designer"},
                      {"member_id": 4, "name": "Priya Nair", "role": "designer"}],
          "last": {"text": "@Priya can you send the hero files before 5?", "by": "Emily", "at": "{{{Now.AddMinutes(-3):O}}}"}},
         {"id": 5, "kind": "group", "name": "Development", "unread": 0, "muted": true,
          "members": [{"member_id": 5, "name": "Karan Malhotra"}, {"member_id": 4, "name": "Priya Nair"}]},
         {"id": 7, "kind": "direct", "with": {"member_id": 5, "name": "Karan Malhotra"}, "unread": 0}]
        """;

    private static readonly string LinesJson = $$$"""
        [{"id": 91, "by": {"kind": "member", "member_id": 6, "name": "Emily Carter"}, "body": "Hero options are in Figma. @Ananya Rao which one?", "at": "{{{Now.AddHours(-3):O}}}"},
         {"id": 92, "by": {"kind": "member", "member_id": 4, "name": "Priya Nair"}, "body": "Option B for me.", "at": "{{{Now.AddHours(-2.9):O}}}", "mine": true, "edited": true},
         {"id": 93, "by": {"kind": "member", "member_id": 9, "name": "Fatima Sheikh"}, "body": "", "deleted": true, "at": "{{{Now.AddMinutes(-30):O}}}"},
         {"id": 94, "by": {"kind": "member", "member_id": 4, "name": "Priya Nair"}, "body": "Cutting the images now, @Emily.", "at": "{{{Now.AddMinutes(-6):O}}}", "mine": true, "seen_by": 2},
         {"id": 95, "by": {"kind": "member", "member_id": 6, "name": "Emily Carter"}, "body": "@Priya can you send the hero files before 5?", "at": "{{{Now.AddMinutes(-3):O}}}"}]
        """;

    private static ChatReply Reply(IReadOnlyList<long>? gone = null, IReadOnlyList<LeaveAsk>? leave = null, IReadOnlyList<ChatNotice>? notices = null,
        int version = 1, string? convs = null) =>
        new(true, TaskBackend.On, ChatJson.Conversations(convs ?? ConvsJson),
            [new(Karan, "Karan Malhotra", "developer", "Working"), new(Emily, "Emily Carter", "designer", "Working"), new(Fatima, "Fatima Sheikh", "designer")],
            new Zinwe.Agent.Core.Chat.ChatThread("conv:3", ChatJson.Messages(LinesJson)), Now, version, gone ?? [], leave, notices);

    private static readonly LeaveAsk[] Asks =
    [
        new(4, DateOnly.FromDateTime(Now.LocalDateTime.AddDays(4)).ToString("yyyy-MM-dd"), Half: false, Status: "requested", Note: "Family function"),
        new(3, DateOnly.FromDateTime(Now.LocalDateTime.AddDays(1)).ToString("yyyy-MM-dd"), Half: true, Status: "approved"),
        new(2, DateOnly.FromDateTime(Now.LocalDateTime.AddDays(-6)).ToString("yyyy-MM-dd"), Status: "declined", Note: "Launch week"),
    ];

    private static void Seed(TrayModel model, ChatReply? chat = null, WorkRules? rules = null)
    {
        var status = new AgentStatus(AgentState.Connected, "1.4.1", "PRIYA-PC", 1, Now, null, OwnerName: "Ananya Rao", CompanyName: "Kitecrest Digital",
            TasksVersion: 1, TodosVersion: 1, ChatVersion: 1, WorkRules: rules ?? new WorkRules(BreakAllowedMin: 75));
        model.UseForPreview(status, new TasksReply(true, TaskBackend.On, [], Now, 1), chat ?? Reply(leave: Asks),
            new TodosReply(true, TaskBackend.On, [], Priya, Now, 1));
    }

    [Fact]
    public void Mentions_of_you_are_highlighted_and_flagged_on_the_list_and_home()
    {
        OnSta(model =>
        {
            model.OpenConv("conv:3");
            var lines = model.TeamThread;
            Assert.True(lines.Single(l => l.Message.Id == 95).MentionsMe);
            Assert.False(lines.Single(l => l.Message.Id == 91).MentionsMe); // names Ananya
            Assert.False(lines.Single(l => l.Message.Id == 94).MentionsMe); // your own line
            model.BackToChatList();
            Assert.True(model.ChatRows.Single(r => r.Key == "conv:3").Mention);
            Assert.False(model.ChatRows.Single(r => r.Key == "conv:5").Mention);
            Assert.Contains(model.NeedsYou, n => n.Title == "Emily mentioned you in Design");
        });
    }

    [Fact]
    public void The_composer_offers_the_groups_people_and_the_admin_but_not_you()
    {
        OnSta(model =>
        {
            model.OpenConv("conv:3");
            Assert.Equal(["Emily Carter", "Fatima Sheikh", "Ananya Rao"], model.MentionPeople.Select(p => p.Name));
            Assert.Contains("Priya Nair", model.MentionNames);
            Assert.Equal("Priya Nair", model.MyMentionName);
            model.OpenConv("conv:7");
            Assert.Empty(model.MentionPeople); // a direct chat has no @ list
        });
    }

    [Fact]
    public void Own_lines_can_be_changed_for_15_minutes_and_only_with_a_141_service()
    {
        OnSta(model =>
        {
            model.OpenConv("conv:3");
            var lines = model.TeamThread;
            Assert.True(lines.Single(l => l.Message.Id == 94).CanChange);
            Assert.False(lines.Single(l => l.Message.Id == 92).CanChange); // 3 hours ago
            Assert.False(lines.Single(l => l.Message.Id == 95).CanChange); // Emily's

            model.StartEdit(lines.Single(l => l.Message.Id == 94).Message);
            Assert.Equal(94, model.TeamEditing?.Id);
            Assert.True(model.SaveEdit(" Cutting the images now, @Emily. ")); // unchanged: nothing to send
            Assert.Null(model.TeamEditing);
            model.StartEdit(lines.Single(l => l.Message.Id == 94).Message);
            Assert.True(model.Escape());
            Assert.Null(model.TeamEditing);

            model.StartEdit(lines.Single(l => l.Message.Id == 92).Message);
            Assert.Null(model.TeamEditing);
            Assert.Equal(ChatEdits.TooLate, model.Snack?.Text);
            model.AskDelete(lines.Single(l => l.Message.Id == 94).Message);
            Assert.Equal(94, model.DeleteAsk?.Id);
            Assert.True(model.Escape());
            Assert.Null(model.DeleteAsk);

            // A 1.4.0 service (no Gone list) would drop an edit: no actions then.
            model.TakeChat(Reply(version: 2) with { Gone = null });
            Assert.False(model.ChatEditsOn);
            Assert.DoesNotContain(model.TeamThread, l => l.CanChange);
        });
    }

    [Fact]
    public void A_deleted_group_closes_with_a_note_and_its_choices_go()
    {
        OnSta(model =>
        {
            model.OpenConv("conv:3");
            model.TakeChat(Reply(version: 2)); // the list: Design pinned on the server
            Assert.True(model.ChatRows.Single(r => r.Key == "conv:3").Pinned);
            model.TakeChat(Reply(gone: [3], version: 3, convs: """[{"id": 5, "kind": "group", "name": "Development", "muted": true}]"""));
            Assert.Null(model.ChatOpen);
            Assert.Equal("This group was deleted.", model.Snack?.Text);
            Assert.DoesNotContain(model.ChatRows, r => r.Key == "conv:3");
            Assert.DoesNotContain("conv:3", ChatPrefs.Load().Pins);
            Assert.True(model.ChatRows.Single(r => r.Key == "conv:5").Muted); // the server's mute
            model.CloseSnack();
            model.OpenConv("conv:3"); // an old notice
            Assert.Null(model.ChatOpen);
            Assert.Equal("This group was deleted.", model.Snack?.Text);
        });
    }

    [Fact]
    public void A_refusal_shows_once()
    {
        OnSta(model =>
        {
            var notice = new ChatNotice("n1", "conv:3", "edit", ChatEdits.TooLate, Now);
            model.TakeChat(Reply(notices: [notice], version: 2));
            Assert.Equal(ChatEdits.TooLate, model.Snack?.Text);
            model.CloseSnack();
            model.TakeChat(Reply(notices: [notice], version: 3));
            Assert.Null(model.Snack);
        });
    }

    [Fact]
    public void Days_off_list_waiting_approved_declined_and_a_day_over_30_days_back_is_refused()
    {
        OnSta(model =>
        {
            Assert.True(model.LeaveOn);
            var rows = model.LeaveRows;
            Assert.Equal(["Approved", "Waiting", "Declined"], rows.Select(r => r.Status));
            Assert.StartsWith("Tomorrow · Half day", rows[0].Label, StringComparison.Ordinal);
            var today = DateOnly.FromDateTime(Now.LocalDateTime);
            Assert.Equal(LeaveDays.PastDay, model.AskLeave(today.AddDays(-31), false, ""));
            Assert.Equal("Pick a day.", model.AskLeave(null, false, ""));
            model.OpenLeave();
            Assert.Equal(Sheet.Leave, model.Sheet);
            model.CloseSheet();

            model.TakeChat(Reply(version: 2) with { Leave = null }); // a backend (or service) without leave
            Assert.False(model.LeaveOn);
            model.OpenLeave();
            Assert.Equal(Sheet.None, model.Sheet);
        });
    }

    [Fact]
    public void Home_says_when_today_is_a_holiday_or_leave()
    {
        OnSta(model =>
        {
            Seed(model, rules: new WorkRules(HolidayToday: "Diwali", WorkingToday: false));
            Assert.Equal("Today is a holiday: Diwali. Enjoy the day.", model.TodayNote);
            Seed(model, rules: new WorkRules(LeaveToday: "half"));
            Assert.Equal("You have half the day off today.", model.TodayNote);
            Seed(model, rules: new WorkRules());
            Assert.Null(model.TodayNote);
        });
    }

    // ---- The views ---------------------------------------------------------------------------------------

    [Fact]
    public void The_141_views_build_in_light_and_dark()
    {
        OnSta(model =>
        {
            var app = (App)Application.Current;
            var shots = Environment.GetEnvironmentVariable("ZINWE_V14_SHOTS");
            Seed(model, rules: new WorkRules(BreakAllowedMin: 75, HolidayToday: null, LeaveToday: "half"));
            foreach (var dark in new[] { false, true })
            {
                foreach (var (k, hex) in HighContrastTheme.Dark)
                {
                    if (dark)
                    {
                        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
                        brush.Freeze(); // the App is shared with the other tests' threads
                        app.Resources[k] = brush;
                    }
                    else
                    {
                        app.Resources.Remove(k);
                    }
                }
                var suffix = dark ? "_dark" : "";

                model.OpenConv("conv:3");
                var thread = new TeamThreadView { Model = model, Width = 380, Height = 640 };
                thread.Render();
                Shot(thread, shots, "141_thread_mentions" + suffix);

                model.StartEdit(model.TeamThread.Single(l => l.Message.Id == 94).Message);
                thread.Render();
                Shot(thread, shots, "141_thread_editing" + suffix);
                model.CancelEdit();

                model.AskDelete(model.TeamThread.Single(l => l.Message.Id == 94).Message);
                thread.Render();
                Shot(thread, shots, "141_thread_delete" + suffix);
                model.CancelDelete();

                model.BackToChatList();
                var list = new ChatListView { Model = model, Width = 380, Height = 420 };
                list.Render();
                Shot(list, shots, "141_chatlist" + suffix);

                var home = new HomeView { Model = model, Width = 380, Height = 900 };
                home.Render();
                Shot(home, shots, "141_home" + suffix);

                model.OpenLeave();
                var sheet = new SheetView { Model = model, Width = 380, Height = 640 };
                sheet.Render();
                Shot(sheet, shots, "141_leave_sheet" + suffix);
                model.CloseSheet();
            }
            foreach (var k in HighContrastTheme.Dark.Keys)
            {
                app.Resources.Remove(k);
            }
        });
    }

    [Fact]
    public void Mention_names_are_drawn_in_a_stronger_style()
    {
        OnSta(_ =>
        {
            var t = TeamThreadView.BodyText("Thanks @Priya Nair and @emily!", ["Priya Nair", "Emily Carter"]);
            var runs = t.Inlines.OfType<System.Windows.Documents.Run>().ToList();
            Assert.Equal(["Thanks ", "@Priya Nair", " and ", "@emily", "!"], runs.Select(r => r.Text));
            Assert.Equal(FontWeights.Bold, runs[1].FontWeight);
            Assert.Equal(FontWeights.Bold, runs[3].FontWeight);
        });
    }

    private static void Shot(FrameworkElement el, string? dir, string name)
    {
        var host = new Border { Child = el, Width = el.Width, Height = el.Height };
        host.SetResourceReference(Border.BackgroundProperty, "Z.Bg");
        host.Measure(new Size(el.Width, el.Height));
        host.Arrange(new Rect(0, 0, el.Width, el.Height));
        host.UpdateLayout();
        if (!string.IsNullOrEmpty(dir))
        {
            var bmp = new RenderTargetBitmap((int)el.Width, (int)el.Height, 96, 96, PixelFormats.Pbgra32);
            bmp.Render(host);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(bmp));
            Directory.CreateDirectory(dir);
            using var f = File.Create(Path.Combine(dir, name + ".png"));
            enc.Save(f);
        }
        host.Child = null;
    }
}

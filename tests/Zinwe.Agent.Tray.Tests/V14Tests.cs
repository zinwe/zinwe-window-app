using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Zinwe.Agent.Core.Backend;
using Zinwe.Agent.Core.Chat;
using Zinwe.Agent.Core.Ipc;
using Zinwe.Agent.Core.Messages;
using Zinwe.Agent.Core.Tasks;
using Zinwe.Agent.Tray.Views;
using Zinwe.Agent.Tray.Widget;

namespace Zinwe.Agent.Tray.Tests;

/// <summary>
/// v1.4 (member side of team tasks + team chat): the optimistic overlay of the new actions, the chips, and the
/// code-built views (task card, member task panel on each tab, chat list, group thread) building with the app's
/// resources in light and dark. Set ZINWE_V14_SHOTS to a folder to also get PNGs of each view.
/// </summary>
[Collection(WpfCollection.Name)]
public sealed class V14Tests
{
    private const long Priya = 4, Karan = 5, Emily = 6, Fatima = 9;
    private static readonly DateTimeOffset Now = DateTimeOffset.Now;

    private static DeviceTask Lumen(params bool[] done) => new(
        6, "Launch the Lumen landing page", Status: "progress", InOrder: true, Mode: "all", GroupId: 3, Priority: "high",
        People: [new(Priya, "Priya Nair", "progress"), new(Karan, "Karan Malhotra", "progress"), new(Emily, "Emily Carter", "progress")],
        Steps:
        [
            new(1, "Write the copy", done.ElementAtOrDefault(0), Priya, done.ElementAtOrDefault(0) ? Priya : null, Now.AddHours(-3)),
            new(2, "Pick the images", done.ElementAtOrDefault(1), Priya),
            new(3, "Build the page", done.ElementAtOrDefault(2), Karan),
            new(4, "Check on phones", false, Emily),
        ],
        DueAt: Now.Date.AddHours(18), CreatedAt: Now.AddDays(-2),
        Chat: new(3, 1, new("Great. Let’s aim for 6.", "Ananya", Now.AddHours(-1))),
        Activity: [new(Now.AddDays(-2), "owner", "created"), new(Now.AddHours(-3), "member", "ticked", "Write the copy", Priya, "Priya Nair")]);

    private static OutboxReply Action(string action, long? step = null) =>
        new(Guid.NewGuid().ToString("N"), "", DateTimeOffset.UtcNow, OutboxReply.TaskActionKind, TaskId: 6, Action: action, StepId: step);

    // ---- The overlay ---------------------------------------------------------------------------------

    [Fact]
    public void A_tick_shows_at_once_and_the_last_one_waits_for_the_OK()
    {
        OwnerTasks.MeId = Priya;
        var t = OwnerTasks.Apply(Lumen(true), Action(TaskActions.Tick, 2));
        Assert.True(t.Steps![1].Done);
        Assert.Equal(Priya, t.Steps[1].DoneById);
        Assert.Equal("progress", t.Status);
        var all = Lumen(true, true, true) with { Steps = [.. Lumen(true, true, true).Steps!.Select(s => s with { Done = s.Id != 4 })] };
        Assert.Equal("review", OwnerTasks.Apply(all, Action(TaskActions.Tick, 4)).Status);
    }

    [Fact]
    public void Starting_an_any_one_task_takes_it()
    {
        OwnerTasks.MeId = Priya;
        var t = new DeviceTask(24, "Fix the printer", Status: "sent", Mode: "any",
            People: [new(Priya, "Priya Nair", "sent"), new(Emily, "Emily Carter", "sent")]);
        var started = OwnerTasks.Apply(t, Action(TaskActions.Start) with { TaskId = 24 });
        Assert.Equal("progress", started.Status);
        Assert.Equal([Priya], started.People!.Select(p => p.MemberId));
    }

    [Fact]
    public void Cant_do_and_finish_change_the_members_line()
    {
        OwnerTasks.MeId = Priya;
        var solo = new DeviceTask(3, "Call them", Status: "progress", People: [new(Priya, "Priya Nair", "progress"), new(Emily, "Emily Carter", "progress")]);
        var cant = OwnerTasks.Apply(solo, Action(TaskActions.Cantdo) with { Body = "On leave", TaskId = 3 });
        Assert.Equal(("cantdo", "On leave", "cantdo"), (cant.Status, cant.CantdoNote, cant.People![0].State));
        var finished = OwnerTasks.Apply(solo, Action(TaskActions.Finish) with { TaskId = 3 });
        Assert.Equal("progress", finished.Status); // Emily still has to finish hers
        Assert.Equal("done", finished.People![0].State);
        Assert.True(OwnerTasks.Reflected(finished, Action(TaskActions.Finish) with { TaskId = 3 }));
    }

    [Fact]
    public void Cantdo_reads_as_cant_for_the_older_screens()
    {
        Assert.Equal(MemberTaskStatus.Cant, OwnerTasks.StatusOf("cantdo").Status);
    }

    // ---- The views ---------------------------------------------------------------------------------------

    [Fact]
    public void The_v14_views_build_in_light_and_dark()
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
                var shots = Environment.GetEnvironmentVariable("ZINWE_V14_SHOTS");
                foreach (var dark in new[] { false, true })
                {
                    // The dark tokens straight onto the app (HighContrastTheme.Attach would also re-render windows other tests made).
                    foreach (var (k, hex) in HighContrastTheme.Dark)
                    {
                        if (dark)
                        {
                            app.Resources[k] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
                        }
                        else
                        {
                            app.Resources.Remove(k);
                        }
                    }
                    var suffix = dark ? "_dark" : "";
                    var items = model.TaskItems;
                    Assert.Contains(items, i => i.Task.Id == 6);
                    var tasks = new TasksView { Model = model, Width = 380, Height = 720 };
                    tasks.Render();
                    Shot(tasks, shots, "tasks" + suffix);
                    var lumen = items.First(i => i.Task.Id == 6);
                    foreach (var tab in new[] { "overview", "chat", "activity" })
                    {
                        model.SetPanelTab(tab);
                        Shot(Sized(TaskPanel.Build(lumen, model, arriving: false)), shots, "panel_" + tab + suffix);
                    }
                    model.SetPanelTab("overview");
                    var turn = items.First(i => i.Task.Id == 8);
                    Shot(Sized(TaskPanel.Build(turn, model, arriving: false)), shots, "panel_turn" + suffix);
                    Shot(Sized(TaskCardV2.Build(lumen, model, thread: true), 340, 300), shots, "card_thread" + suffix);
                    var list = new ChatListView { Model = model, Width = 380, Height = 640 };
                    list.Render();
                    Shot(list, shots, "chatlist" + suffix);
                    model.OpenConv("conv:3");
                    var threadView = new TeamThreadView { Model = model, Width = 380, Height = 640 };
                    threadView.Render();
                    Shot(threadView, shots, "thread" + suffix);
                    model.BackToChatList();
                }
                foreach (var k in HighContrastTheme.Dark.Keys)
                {
                    app.Resources.Remove(k);
                }
                Assert.Equal(2, model.ChatGroupCount);
                Assert.Equal(2, model.ChatBadge); // Design + Karan have unread lines
                Assert.Equal("You’ve used 1 h 10 min of your 1 h 15 min break time today.",
                    Zinwe.Agent.Core.Work.BreakLimit.Note(model.Status?.WorkRules, TimeSpan.FromMinutes(70)));
                Assert.Equal(8, model.FocusItem?.Task.Id); // your turn first
            }
            catch (Exception e)
            {
                error = e;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        Assert.Null(error);
    }

    private static void Seed(TrayModel model)
    {
        var status = new AgentStatus(AgentState.Connected, "1.4.0", "PRIYA-PC", 1, Now, null, OwnerName: "Ananya Rao", CompanyName: "Kitecrest Digital",
            TasksVersion: 1, TodosVersion: 1, ChatVersion: 1, WorkRules: new WorkRules(BreakAllowedMin: 75));
        var turn = new DeviceTask(8, "Send the Northwind proposal", Status: "progress", InOrder: true,
            People: [new(Karan, "Karan Malhotra", "done"), new(Priya, "Priya Nair", "progress")],
            Steps: [new(1, "Finalise the pricing table", true, Karan, Karan, Now.AddHours(-2)), new(2, "Design the cover and layout", false, Priya),
                new(3, "Send it to Northwind", false, Priya)],
            DueAt: Now.Date.AddDays(2).AddHours(12), CreatedAt: Now.AddDays(-1), Chat: new(1, 0, new("Pricing is final.", "Karan", Now.AddHours(-2))));
        var fresh = new DeviceTask(11, "Write the case study for Northwind", Status: "sent", People: [new(Priya, "Priya Nair", "sent")],
            DueAt: Now.Date.AddDays(1).AddHours(18), CreatedAt: Now.AddHours(-1));
        var any = new DeviceTask(24, "Fix the 2nd floor printer", Status: "sent", Mode: "any", GroupId: 3,
            People: [new(Priya, "Priya Nair", "sent"), new(Emily, "Emily Carter", "sent"), new(Fatima, "Fatima Sheikh", "sent")],
            DueAt: Now.Date.AddHours(17.5), CreatedAt: Now.AddMinutes(-20));
        var back = new DeviceTask(21, "Update the pricing page banner", Status: "progress", ReopenNote: "Use the new orange, not the old red.",
            People: [new(Priya, "Priya Nair", "progress")], DueAt: Now.Date.AddHours(18), Chat: new(2, 0, null),
            Activity: [new(Now.AddMinutes(-40), "owner", "reopened", "Use the new orange, not the old red.")]);
        var review = new DeviceTask(12, "Resize the team photos for the website", Status: "review", People: [new(Priya, "Priya Nair", "done")],
            DueAt: Now.Date.AddHours(17));
        var tasks = new TasksReply(true, TaskBackend.On, [Lumen(true, true), turn, fresh, any, back, review], Now, 1);
        var convs = ChatJson.Conversations($$$"""
            [{"id": 3, "kind": "group", "name": "Design", "unread": 2,
              "members": [{"member_id": 1, "name": "Ananya Rao"}, {"member_id": 6, "name": "Emily Carter"}, {"member_id": 9, "name": "Fatima Sheikh"}, {"member_id": 4, "name": "Priya Nair"}],
              "last_message": {"id": 95, "by": {"kind": "owner", "name": "Ananya Rao"}, "body": "", "task_id": 24, "at": "{{{Now.AddMinutes(-12):O}}}"}},
             {"id": 9, "kind": "group", "name": "Everyone at Kitecrest", "announce": true,
              "last_message": {"id": 70, "by": {"kind": "member", "member_id": 4, "name": "Priya Nair"}, "body": "Happy Diwali! 🪔", "at": "{{{Now.AddDays(-1):O}}}", "mine": true}},
             {"id": 7, "kind": "direct", "with": {"member_id": 5, "name": "Karan Malhotra"}, "unread": 1,
              "last_message": {"id": 81, "by": {"kind": "member", "member_id": 5, "name": "Karan Malhotra"}, "body": "Northwind pricing is final too, the proposal is yours now", "at": "{{{Now.AddHours(-2):O}}}"}},
             {"id": 8, "kind": "direct", "with": {"member_id": 6, "name": "Emily Carter"},
              "last_message": {"id": 82, "by": {"kind": "member", "member_id": 6, "name": "Emily Carter"}, "body": "Thursday it is 👍", "at": "{{{Now.AddHours(-2.2):O}}}"}}]
            """);
        var messages = ChatJson.Messages($$$"""
            [{"id": 90, "by": {"kind": "owner", "name": "Ananya Rao"}, "body": "", "task_id": 6, "at": "{{{Now.AddDays(-2):O}}}"},
             {"id": 91, "by": {"kind": "member", "member_id": 6, "name": "Emily Carter"}, "body": "Hero options are in Figma, three versions. @Ananya which one do you like for Lumen?", "at": "{{{Now.AddHours(-4):O}}}"},
             {"id": 92, "by": {"kind": "owner", "name": "Ananya Rao"}, "body": "Option B. Bigger product shot, shorter headline.", "at": "{{{Now.AddHours(-3.9):O}}}",
              "reply_to": {"id": 91, "by": {"name": "Emily Carter"}, "body": "Hero options are in Figma, three versions."}},
             {"id": 93, "by": {"kind": "member", "member_id": 4, "name": "Priya Nair"}, "body": "On it. Cutting the images now.", "at": "{{{Now.AddHours(-3.8):O}}}", "seen_by": 3},
             {"id": 94, "by": {"kind": "member", "member_id": 9, "name": "Fatima Sheikh"}, "body": "The printer on the 2nd floor is out of toner. I’m ordering more today.", "at": "{{{Now.AddMinutes(-20):O}}}"},
             {"id": 95, "by": {"kind": "owner", "name": "Ananya Rao"}, "body": "", "task_id": 24, "at": "{{{Now.AddMinutes(-12):O}}}"}]
            """);
        var people = new List<TeamMember> { new(Karan, "Karan Malhotra", "developer", "Working"), new(Emily, "Emily Carter", "designer", "Working"), new(Fatima, "Fatima Sheikh", "designer", "On a break") };
        var chat = new ChatReply(true, TaskBackend.On, convs, people, new Zinwe.Agent.Core.Chat.ChatThread("conv:3", messages), Now, 1);
        model.UseForPreview(status, tasks, chat, new TodosReply(true, TaskBackend.On, [], Priya, Now, 1));
    }

    private static FrameworkElement Sized(FrameworkElement el, double w = 380, double h = 720)
    {
        el.Width = w;
        el.Height = h;
        return el;
    }

    private static void Shot(FrameworkElement el, string? dir, string name)
    {
        var host = new Border { Child = el, Width = el.Width, Height = el.Height };
        host.SetResourceReference(Border.BackgroundProperty, "Z.Bg");
        host.Measure(new Size(el.Width, el.Height));
        host.Arrange(new Rect(0, 0, el.Width, el.Height));
        host.UpdateLayout();
        if (string.IsNullOrEmpty(dir))
        {
            return;
        }
        var bmp = new RenderTargetBitmap((int)el.Width, (int)el.Height, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(host);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        Directory.CreateDirectory(dir);
        using var f = File.Create(Path.Combine(dir, name + ".png"));
        enc.Save(f);
        host.Child = null;
    }
}

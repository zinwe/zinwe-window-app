using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Zinwe.Agent.Core.Backend;
using Zinwe.Agent.Core.Chat;
using Zinwe.Agent.Core.Ipc;
using Zinwe.Agent.Core.Policy;
using Zinwe.Agent.Tray.Views;
using Zinwe.Agent.Tray.Widget;

namespace Zinwe.Agent.Tray.Tests;

/// <summary>
/// v1.4.2 · what the owner saw on a real PC (agent 1.4.1): a small company where the member is in no group and has no
/// teammate yet. With team_chat on the plan the Chat tab is still the v1.4 list (the admin row first, a calm note), the
/// admin thread wraps a notice line and ends above the quick replies, and the Tasks chips fit the window. The admin chat
/// comes from a temp notifications file, so the member's real one is never read or marked.
/// ZINWE_V14_SHOTS=&lt;folder&gt; also saves full-window PNGs of those screens, light and dark.
/// </summary>
[Collection(WpfCollection.Name)]
public sealed class V142Tests : IDisposable
{
    private const long Me = 7;
    private static readonly DateTimeOffset Now = DateTimeOffset.Now;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "zinwe-v142-" + Guid.NewGuid().ToString("N"));

    public V142Tests()
    {
        Directory.CreateDirectory(_dir);
        ChatPrefs.PathOverride = Path.Combine(_dir, "chat-prefs.json");
    }

    public void Dispose()
    {
        ChatPrefs.PathOverride = null;
        try
        {
            Directory.Delete(_dir, true);
        }
        catch (IOException)
        {
        }
    }

    private static readonly string[] Plan = [PlanModules.WorkTime, PlanModules.TeamChat];

    private static AgentStatus Status(IReadOnlyList<string>? plan) => new(AgentState.Connected, "1.4.2", "VISHAL-PC", 1, Now, null,
        Entitlements: plan, CompanyName: "Skytechno", OwnerName: "vishal Shakaya", TasksVersion: 1, TodosVersion: 1, ChatVersion: 1);

    private static ChatReply Empty(string backend = TaskBackend.On) => new(true, backend, [], [], null, Now, 1);

    private static DeviceTask Call => new(31, "call dheeraj", Status: "progress", People: [new(Me, "Vishal Shakya", "progress")],
        DueAt: Now.Date.AddHours(23).AddMinutes(30), CreatedAt: Now.AddDays(-1),
        Chat: new(2, 2, new("Yo", "Vishal Shakya", Now.AddMinutes(-5))));

    private static IEnumerable<DeviceTask> Done(int n) => Enumerable.Range(0, n).Select(i => new DeviceTask(100 + i, "Old task " + i, Status: "approved",
        People: [new(Me, "Vishal Shakya", "done")], DueAt: Now.Date.AddDays(-2 - i), CreatedAt: Now.AddDays(-5 - i)));

    /// <summary>The admin chat: the task's line, a questioned lunch break (a notice line), and two lines today.</summary>
    private NotificationStore Notes()
    {
        var y = Now.AddDays(-1);
        var store = new NotificationStore(Path.Combine(_dir, "notifications.json"));
        store.Save(new NotificationFile(
        [
            new("201", y.AddMinutes(-30), "info", "About your Lunch break at 11:07 PM",
                "not approved, please keep lunch to 30 minutes and tell me before you go next time", Read: true, Kind: "chat"),
            new("202", y, "info", "New task", "call dheeraj", Read: true, Kind: "chat", TaskId: 31),
            new("203", Now.AddMinutes(-4), "info", "", "Done bro please", Read: true, Kind: "chat"),
            new("204", Now.AddMinutes(-3), "info", "", "Yo", Read: true, Kind: "chat"),
        ]));
        return store;
    }

    private void OnSta(Action<TrayModel> body, NotificationStore? store = null)
    {
        Exception? error = null;
        var notes = store ?? Notes();
        if (Application.Current is null)
        {
            // Run alone, the App is made on a thread of its own, as when other tests ran first: this test pumps its
            // dispatcher, and the App's own start-up work must not run (and shut it down) in that pump.
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

    private void Seed(TrayModel model, IReadOnlyList<string>? plan = null, ChatReply? chat = null, IEnumerable<DeviceTask>? tasks = null) =>
        model.UseForPreview(Status(plan ?? Plan), new TasksReply(true, TaskBackend.On, [.. tasks ?? [Call, .. Done(10)]], Now, 1), chat ?? Empty(),
            new TodosReply(true, TaskBackend.On, [], Me, Now, 1), new TraySettings { ChatPattern = "geek" }, inApp: true,
            local: new LocalTaskSource(false, _dir));

    // ---- Bug 1: the Chat tab with team chat on the plan and nobody to write to yet ---------------------------

    [Fact]
    public void Team_chat_on_the_plan_shows_the_list_even_with_no_groups_or_teammates()
    {
        OnSta(model =>
        {
            Seed(model);
            Assert.True(model.TeamChatOn);
            Assert.True(model.ChatAlone);
            model.Go(Tab.Chat);
            Assert.Null(model.ChatOpen); // the list, not the 1.3 admin-only screen
            var row = Assert.Single(model.ChatRows);
            Assert.Equal("admin", row.Key);
            Assert.Equal("Yo", row.Line);

            // A notice about an admin message opens the admin thread (with Back), as Home's Needs you does.
            model.Route("chat");
            Assert.Equal("admin", model.ChatOpen);
            model.Back();
            Assert.Null(model.ChatOpen);

            // A plan without team chat keeps the 1.3 admin chat, whatever the lists say.
            Seed(model, plan: [PlanModules.WorkTime]);
            Assert.False(model.TeamChatOn);
            Assert.False(model.ChatAlone);
            Seed(model, plan: [PlanModules.WorkTime], chat: Empty() with { People = [new(9, "Fatima Sheikh")] });
            Assert.False(model.TeamChatOn);

            // A backend without team chat (404 on the list) keeps it too.
            Seed(model, chat: Empty(TaskBackend.Off));
            Assert.False(model.TeamChatOn);

            // A backend that sends no plan list: the lists decide, as in 1.4.1.
            model.UseForPreview(Status(null), null, Empty(), null, null, inApp: true);
            Assert.False(model.TeamChatOn);
            model.UseForPreview(Status(null), null, Empty() with { People = [new(9, "Fatima Sheikh")] }, null, null, inApp: true);
            Assert.True(model.TeamChatOn);
            Assert.False(model.ChatAlone);
        });
    }

    [Fact]
    public void The_empty_list_says_why_it_is_empty()
    {
        OnSta(model =>
        {
            Seed(model);
            model.Go(Tab.Chat);
            var list = new ChatListView { Model = model, Width = 380, Height = 560 };
            list.Render();
            Layout(list);
            var note = Texts(list).Single(t => t.Text.StartsWith("No groups yet", StringComparison.Ordinal));
            Assert.Equal(Visibility.Visible, note.Visibility);
            model.SetChatFilter(ChatChip.Groups);
            list.Render();
            Layout(list);
            Assert.Equal(Visibility.Collapsed, note.Visibility); // the Groups chip has its own "You're not in a group yet."
        });
    }

    // ---- Bug 4: the Tasks chips fit -------------------------------------------------------------------

    [Fact]
    public void Four_task_chips_with_two_digit_counts_fit_the_default_window()
    {
        OnSta(model =>
        {
            // The widest case: All, Today, Upcoming and Done all at two digits.
            var today = Enumerable.Range(0, 12).Select(i => Call with { Id = 300 + i, Chat = null });
            var later = Enumerable.Range(0, 11).Select(i => Call with { Id = 400 + i, Chat = null, DueAt = Now.Date.AddDays(3 + i) });
            Seed(model, tasks: [.. today, .. later, .. Done(10)]);
            Assert.Equal(10, model.ChipCount(TaskChip.Done));
            Assert.True(model.ChipCount(TaskChip.Today) >= 10);
            Assert.True(model.ChipCount(TaskChip.Upcoming) >= 10);
            var view = new TasksView { Model = model, Width = 380 - 2, Height = 600 }; // the window less its 1 px border
            view.Render();
            Layout(view);
            var chips = (FrameworkElement)view.FindName("Chips");
            var buttons = Descendants<Button>(chips).Where(b => b.Visibility == Visibility.Visible).ToList();
            Assert.Equal(4, buttons.Count);
            foreach (var b in buttons)
            {
                var box = b.TransformToAncestor(view).TransformBounds(new Rect(b.RenderSize));
                Assert.True(box.Left >= 0 && box.Right <= view.Width - 12, $"{AutomationName(b)} runs to {box.Right:0.#} of {view.Width}");
            }
            // Bigger text: the row scrolls sideways instead of clipping or wrapping a chip.
            var scroller = Assert.IsType<ScrollViewer>(VisualTreeHelper.GetParent(chips) is ScrollViewer s ? s : FindAncestor<ScrollViewer>(chips));
            Assert.NotEqual(ScrollBarVisibility.Disabled, scroller.HorizontalScrollBarVisibility);
            Assert.Equal(ScrollBarVisibility.Disabled, scroller.VerticalScrollBarVisibility); // one line: never wraps
            if (Environment.GetEnvironmentVariable("ZINWE_V14_SHOTS") is { Length: > 0 } dir)
            {
                var bmp = new RenderTargetBitmap((int)view.Width, 260, 96, 96, PixelFormats.Pbgra32);
                var visual = new DrawingVisual();
                using (var dc = visual.RenderOpen())
                {
                    dc.DrawRectangle((Brush)Application.Current.FindResource("Z.Bg"), null, new Rect(0, 0, view.Width, 260));
                    dc.DrawRectangle(new VisualBrush(view) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top,
                        Viewbox = new Rect(0, 0, view.Width, 260), ViewboxUnits = BrushMappingMode.Absolute }, null, new Rect(0, 0, view.Width, 260));
                }
                bmp.Render(visual);
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(bmp));
                Directory.CreateDirectory(dir);
                using var f = File.Create(Path.Combine(dir, "142_tasks_chips_two_digits.png"));
                enc.Save(f);
            }
        });
    }

    // ---- The owner's screens, full window, light and dark ---------------------------------------------

    [Fact]
    public void The_owners_screens_render_whole_at_the_window_size()
    {
        OnSta(model =>
        {
            var app = (App)Application.Current;
            var shots = Environment.GetEnvironmentVariable("ZINWE_V14_SHOTS");
            Seed(model);
            var window = new WidgetWindow(model);
            try
            {
                foreach (var dark in new[] { false, true })
                {
                    foreach (var (k, hex) in HighContrastTheme.Dark)
                    {
                        if (dark)
                        {
                            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
                            brush.Freeze();
                            app.Resources[k] = brush;
                        }
                        else
                        {
                            app.Resources.Remove(k);
                        }
                    }
                    HighContrastTheme.SetDark(dark); // the chat background pattern follows it
                    var suffix = dark ? "_dark" : "";

                    model.Go(Tab.Chat);
                    Save(model, shots, "142_chatlist_empty" + suffix);
                    var list = Descendants<ChatListView>((DependencyObject)window.Content).Single();
                    Assert.Equal(Visibility.Visible, list.Visibility);

                    model.OpenConv("admin");
                    var chat = Descendants<ChatView>((DependencyObject)window.Content).Single();
                    Assert.Equal(Visibility.Visible, chat.Visibility);
                    Save(model, shots, "142_admin_thread" + suffix);
                    AssertThreadEdges(chat);

                    // The quick replies folding in after the list reached its end (the owner's cut "Yo"): still at the end.
                    var quick = (FrameworkElement)chat.FindName("QuickHost");
                    var scroller = (ScrollViewer)chat.FindName("Scroller");
                    quick.BeginAnimation(FrameworkElement.HeightProperty, null);
                    quick.Height = 0;
                    chat.UpdateLayout();
                    scroller.ScrollToEnd();
                    Pump();
                    quick.Height = 52;
                    chat.UpdateLayout();
                    Pump();
                    AssertThreadEdges(chat);

                    // The notice line and the task card, further up.
                    var log = (FrameworkElement)chat.FindName("Log");
                    var notice = Texts(log).Single(t => Words(t).StartsWith("About your Lunch break", StringComparison.Ordinal));
                    scroller.ScrollToVerticalOffset(notice.TransformToAncestor(log).Transform(new Point(0, 0)).Y - 40);
                    Save(model, shots, "142_admin_thread_card" + suffix);

                    model.Go(Tab.Tasks);
                    Save(model, shots, "142_tasks" + suffix);
                }
            }
            finally
            {
                window.Close();
                HighContrastTheme.SetDark(false);
                foreach (var k in HighContrastTheme.Dark.Keys)
                {
                    app.Resources.Remove(k);
                }
            }
        });
    }

    /// <summary>The admin thread scrolled to the end: the last line shows whole above the quick replies, avatars sit inside
    /// the window, and the notice line wraps instead of being cut.</summary>
    private static void AssertThreadEdges(ChatView chat)
    {
        var scroller = (ScrollViewer)chat.FindName("Scroller");
        var log = (Panel)chat.FindName("Log");
        Assert.True(scroller.VerticalOffset >= scroller.ScrollableHeight - 1, $"not at the end: {scroller.VerticalOffset} of {scroller.ScrollableHeight}");
        var yo = Texts(log).Last(t => Words(t) == "Yo");
        var box = yo.TransformToAncestor(scroller).TransformBounds(new Rect(yo.RenderSize));
        Assert.True(box.Bottom <= scroller.ViewportHeight, $"the last line ends at {box.Bottom:0.#}, the list at {scroller.ViewportHeight:0.#}");
        foreach (var face in Texts(log).Where(t => Words(t) == "VS"))
        {
            var f = face.TransformToAncestor(chat).TransformBounds(new Rect(face.RenderSize));
            Assert.True(f.Left >= 8, $"an avatar starts at {f.Left:0.#}");
        }
        var notice = Texts(log).Single(t => Words(t).StartsWith("About your Lunch break", StringComparison.Ordinal));
        Assert.Equal(TextWrapping.Wrap, notice.TextWrapping);
        Assert.True(notice.ActualHeight > notice.FontSize * 2, "the notice line wraps onto a second line");
        var n = notice.TransformToAncestor(chat).TransformBounds(new Rect(notice.RenderSize));
        Assert.True(n.Left >= 12 && n.Right <= chat.ActualWidth - 12, $"the notice line runs {n.Left:0.#}..{n.Right:0.#}");
    }

    // ---- helpers ------------------------------------------------------------------------------------------

    private static void Save(TrayModel model, string? dir, string name)
    {
        Pump();
        var png = model.CaptureWindow!();
        Pump();
        png = model.CaptureWindow!(); // after the deferred scroll-to-end
        Assert.NotNull(png);
        if (string.IsNullOrEmpty(dir))
        {
            return;
        }
        // The capture leaves out the window's own background (Z.Bg): put it back, as on screen.
        var frame = BitmapFrame.Create(new MemoryStream(png!), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle((Brush)Application.Current.FindResource("Z.Bg"), null, new Rect(0, 0, frame.Width, frame.Height));
            dc.DrawImage(frame, new Rect(0, 0, frame.Width, frame.Height));
        }
        var bmp = new RenderTargetBitmap(frame.PixelWidth, frame.PixelHeight, frame.DpiX, frame.DpiY, PixelFormats.Pbgra32);
        bmp.Render(visual);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        Directory.CreateDirectory(dir);
        using var f = File.Create(Path.Combine(dir, name + ".png"));
        enc.Save(f);
    }

    /// <summary>The words a TextBlock shows, whether set as Text or as runs (links, @mentions).</summary>
    private static string Words(TextBlock t) => t.Inlines.Count == 0 ? t.Text
        : string.Concat(t.Inlines.Select(i => i is System.Windows.Documents.Run r ? r.Text
            : i is System.Windows.Documents.Span sp ? string.Concat(sp.Inlines.OfType<System.Windows.Documents.Run>().Select(x => x.Text)) : ""));

    private static void Pump()
    {
        for (var i = 0; i < 3; i++)
        {
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
        }
    }

    private static void Layout(FrameworkElement el)
    {
        el.Measure(new Size(el.Width, el.Height));
        el.Arrange(new Rect(0, 0, el.Width, el.Height));
        el.UpdateLayout();
    }

    private static string AutomationName(DependencyObject d) => System.Windows.Automation.AutomationProperties.GetName(d);

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

    private static T? FindAncestor<T>(DependencyObject d) where T : DependencyObject
    {
        for (var x = VisualTreeHelper.GetParent(d); x is not null; x = VisualTreeHelper.GetParent(x))
        {
            if (x is T t)
            {
                return t;
            }
        }
        return null;
    }
}

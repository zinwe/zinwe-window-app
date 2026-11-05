using System.IO;
using System.Text.Json;
using Zinwe.Agent.Tray.Widget;

namespace Zinwe.Agent.Tray.Tests;

/// <summary>
/// Agent 1.3.11, the owner's "Windows app next" proposals: the quick replies and their starred row, the chat
/// backgrounds, a task's ⋯ menu, the live clock's split time, and (on the WPF thread) the small menus closing (Esc,
/// a sheet, another tab or filter) before anything else.
/// </summary>
public sealed class WinAppNextTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "zinwe-next-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch
        {
            // best effort
        }
    }

    // ---- Quick replies ---------------------------------------------------------------------------------

    [Fact]
    public void The_row_starts_with_three_replies_and_keeps_only_known_ones_once_each()
    {
        Assert.Equal(["On it", "Thanks!", "Running 10 min late"], QuickReplies.Favourites(new TraySettings()));
        Assert.Empty(QuickReplies.Favourites(new TraySettings { QuickFavourites = [] })); // the member unstarred them all
        Assert.Equal(["Can we talk?", "On it"],
            QuickReplies.Favourites(new TraySettings { QuickFavourites = ["Can we talk?", "Not a reply", "Can we talk?", "On it"] }));
        Assert.Equal(3, QuickReplies.Favourites(new TraySettings { QuickFavourites = [.. QuickReplies.All] }).Count);
        Assert.Equal(8, QuickReplies.All.Count);
        Assert.All(QuickReplies.Defaults, d => Assert.Contains(d, QuickReplies.All));
    }

    [Fact]
    public void A_star_adds_to_the_row_until_it_holds_three_and_a_second_press_takes_it_out()
    {
        IReadOnlyList<string> row = ["On it", "Thanks!"];

        row = QuickReplies.Toggle(row, "Can we talk?", out var note);
        Assert.Null(note);
        Assert.Equal(["On it", "Thanks!", "Can we talk?"], row);

        var full = QuickReplies.Toggle(row, "Give me 5 minutes", out note);
        Assert.Equal(QuickReplies.RowFull, note); // the row is full: nothing changes, the list says why
        Assert.Equal(row, full);

        row = QuickReplies.Toggle(row, "Thanks!", out note);
        Assert.Null(note);
        Assert.Equal(["On it", "Can we talk?"], row);
    }

    [Fact]
    public void Stars_and_the_chat_background_are_kept_on_this_pc_and_older_settings_files_still_load()
    {
        Directory.CreateDirectory(_dir);
        // A tray-settings.json from 1.3.10: no stars, no background.
        File.WriteAllText(Path.Combine(_dir, "tray-settings.json"), JsonSerializer.Serialize(new { Dark = true, TextLarge = true }));
        var store = new TraySettingsStore(_dir);
        var old = store.Load();
        Assert.True(old.Dark);
        Assert.Null(old.QuickFavourites);
        Assert.Equal(QuickReplies.Defaults, QuickReplies.Favourites(old));
        Assert.Equal(ChatPatterns.Default, ChatPatterns.Of(old));

        store.Save(old with { QuickFavourites = ["Can you call me?"], ChatPattern = "geek" });
        var back = new TraySettingsStore(_dir).Load();
        Assert.Equal(["Can you call me?"], QuickReplies.Favourites(back));
        Assert.Equal("geek", ChatPatterns.Of(back));
        Assert.True(back.Dark); // nothing else changed
    }

    [Fact]
    public void The_chat_background_is_one_of_five_and_an_unknown_one_reads_as_the_default()
    {
        Assert.Equal(["calm", "focus", "geek", "money", "plain"], ChatPatterns.All.Select(p => p.Key));
        Assert.Equal("calm", ChatPatterns.Of(new TraySettings()));
        Assert.Equal("calm", ChatPatterns.Of(new TraySettings { ChatPattern = "neon" }));
        Assert.Equal("plain", ChatPatterns.Of(new TraySettings { ChatPattern = "plain" }));
    }

    // ---- A task's ⋯ menu ---------------------------------------------------------------------------------

    private static MemberTask Own(MemberTaskStatus status = MemberTaskStatus.New) =>
        new() { Id = 1, Title = "Tidy the shared Drive folder", Mine = true, Status = status, SentAt = DateTimeOffset.Now };

    private static MemberTask Owners(MemberTaskStatus status = MemberTaskStatus.New) =>
        new() { Id = 2, Title = "Send the Sharma invoice", Mine = false, RemoteId = 12, Status = status, SentAt = DateTimeOffset.Now };

    private static IEnumerable<string> Labels(MemberTask t, IReadOnlySet<int>? holding = null) =>
        RowMenu.Items(t, "Rahul", holding).Select(i => i.Label);

    [Fact]
    public void An_open_task_menu_starts_with_mark_done_then_the_details_cards_own_actions()
    {
        Assert.Equal(["Mark done", "Open details", "Edit", "Move to tomorrow", "Delete"], Labels(Own()));
        Assert.Equal(["Mark done · send to Rahul", "Open details", "Ask Rahul", "Add a note", "Can’t do it"], Labels(Owners()));
        Assert.Equal(["Mark done · send to Rahul", "Open details", "Ask Rahul", "Add a note", "Can’t do it"], Labels(Owners(MemberTaskStatus.Progress)));

        var own = RowMenu.Items(Own(), "Rahul");
        Assert.True(own[0].Bold);
        Assert.Equal(RowMenuAction.MarkDone, own[0].Action);
        Assert.Equal((RowMenuAction.Delete, true), (own[^1].Action, own[^1].Danger));
        Assert.Equal((RowMenuAction.Cant, true), (RowMenu.Items(Owners(), "Rahul")[^1].Action, RowMenu.Items(Owners(), "Rahul")[^1].Danger));
    }

    [Fact]
    public void Done_is_final_in_the_menu_too_and_a_cancelled_task_only_opens_its_details()
    {
        Assert.Equal(["Add a note", "Open details"], Labels(Own(MemberTaskStatus.Done)));
        Assert.Equal(["Add a note", "Open details"], Labels(Owners(MemberTaskStatus.Review)));
        Assert.Equal(["Add a note", "Open details"], Labels(Own(), new HashSet<int> { 1 })); // just ticked, still in place
        Assert.Equal(["Open details"], Labels(Owners(MemberTaskStatus.Cancelled)));
    }

    // ---- The live clock ------------------------------------------------------------------------------------

    [Fact]
    public void The_time_splits_either_side_of_its_colon()
    {
        Assert.Equal(("3", "42"), DayLog.SplitHm("3:42"));
        Assert.Equal(("12", "05"), DayLog.SplitHm("12:05"));
        Assert.Equal(("Start day", ""), DayLog.SplitHm("Start day"));
    }
}

[Collection(WpfCollection.Name)]
public sealed class WinAppNextMenuTests
{
    private static void OnSta(Action<TrayModel> run)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                System.Windows.Application.ResourceAssembly ??= typeof(App).Assembly;
                var app = System.Windows.Application.Current as App ?? new App();
                app.InitializeComponent();
                var store = new NotificationStore();
                using var watcher = new NotificationWatcher(store, TimeSpan.FromMinutes(5));
                using var model = new TrayModel(store, watcher, () => Task.FromResult<string?>(null));
                run(model);
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

    [Fact]
    public void The_small_menus_close_first_on_esc_and_when_the_screen_changes()
    {
        OnSta(model =>
        {
            model.Go(Tab.Tasks);
            model.ToggleQuick();
            Assert.True(model.QuickOpen);
            model.ToggleEmoji(); // one list over the composer at a time
            Assert.True(model.EmojiOpen);
            Assert.False(model.QuickOpen);

            model.OpenSheet(Sheet.Feedback);
            Assert.False(model.MenuOpen); // a sheet closes them

            model.CloseSheet();
            model.ToggleRowMenu(5);
            Assert.Equal(5, model.RowMenu);
            model.ToggleRowMenu(5);
            Assert.Null(model.RowMenu); // ⋯ again closes it
            model.ToggleRowMenu(5);
            model.SetView(TaskView.Done);
            Assert.Null(model.RowMenu); // another filter closes it

            model.ToggleChatMenu();
            model.OpenSheet(Sheet.Feedback);
            model.ToggleEmoji(); // a menu over a sheet (the sheet stays)
            Assert.True(model.Escape());
            Assert.False(model.MenuOpen);
            Assert.Equal(Sheet.Feedback, model.Sheet); // Esc closed the menu, not the sheet under it
            Assert.True(model.Escape());
            Assert.Equal(Sheet.None, model.Sheet);

            model.ToggleRowMenu(3);
            model.Go(Tab.Me);
            Assert.False(model.MenuOpen);
        });
    }
}

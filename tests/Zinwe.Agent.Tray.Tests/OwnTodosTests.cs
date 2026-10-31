using System.IO;
using System.Text.Json;
using Zinwe.Agent.Core.Backend;
using Zinwe.Agent.Tray.Widget;

namespace Zinwe.Agent.Tray.Tests;

/// <summary>
/// The member's own to-dos between this PC and the server: the merge rule, deletes, a new PC, a second member on the
/// same PC, and the promise that to-dos made before sharing existed stay here until the member edits one.
/// </summary>
public sealed class OwnTodosTests : IDisposable
{
    private const long Asha = 7;
    private static readonly TimeSpan Offset = TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 10, 30));
    private static readonly DateTimeOffset Now = new(2026, 10, 30, 11, 0, 0, Offset); // Friday 11 AM local
    private static readonly DateOnly Today = new(2026, 10, 30);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "zinwe-own-todos-" + Guid.NewGuid().ToString("N"));

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

    private static DateTimeOffset At(int minutes) => Now.AddMinutes(minutes);

    /// <summary>A to-do from before this update: tasks.json has no id for it.</summary>
    private static MemberTask Legacy(int id = 1, string title = "Private thing") =>
        new() { Id = id, Title = title, Mine = true, Due = "Today", DueKey = DueKey.Today, Status = MemberTaskStatus.New, SentAt = At(-500) };

    private static MemberTask Shared(string clientId = "aaaaaaaa", int id = 2, int updated = 0, int? synced = 0, long? member = Asha) =>
        new()
        {
            Id = id, Title = "Shared thing", Mine = true, ClientId = clientId, UpdatedAt = At(updated),
            SyncedAt = synced is { } s ? At(s) : null, SyncedFor = member, Due = "Today", DueKey = DueKey.Today, SentAt = At(-100),
        };

    private static DeviceTodo Remote(string clientId = "aaaaaaaa", string title = "From the server", int updated = 0, bool deleted = false,
        string status = DeviceTodo.Open, DateOnly? due = null, params DeviceTodoStep[] steps) =>
        new(clientId, title, due, steps, status, At(-200), At(updated), deleted);

    // ---- The merge rule ----------------------------------------------------------------------------

    [Fact]
    public void A_newer_edit_from_the_server_wins_and_keeps_this_pcs_row()
    {
        var local = Shared() with { Notes = [new TaskNote("You", "A note here")] };

        var t = Assert.Single(OwnTodos.Merge([local], [Remote(updated: 5, title: "Renamed")], Asha, Now));

        Assert.Equal((2, "Renamed", At(5), At(5)), (t.Id, t.Title, t.UpdatedAt!.Value, t.SyncedAt!.Value));
        Assert.Single(t.Notes); // notes stay on this PC
        Assert.False(OwnTodos.Dirty(t));
    }

    [Fact]
    public void A_newer_change_here_wins_and_stays_unsent()
    {
        var local = Shared(updated: 5, synced: 0);

        var t = Assert.Single(OwnTodos.Merge([local], [Remote(updated: 1)], Asha, Now));

        Assert.Equal("Shared thing", t.Title);
        Assert.True(OwnTodos.Dirty(t));
    }

    [Fact]
    public void The_server_echoing_our_change_confirms_it()
    {
        var local = Shared(updated: 5, synced: 0) with { Queued = true };

        var t = Assert.Single(OwnTodos.Merge([local], [Remote(updated: 5)], Asha, Now));

        Assert.Equal("Shared thing", t.Title); // same time: nothing to take
        Assert.False(OwnTodos.Dirty(t));
        Assert.False(t.Queued);
    }

    [Fact]
    public void Merging_twice_changes_nothing()
    {
        var once = OwnTodos.Merge([Shared(), Legacy()], [Remote(updated: 5), Remote("bbbbbbbb", "New here", 1)], Asha, Now);

        var twice = OwnTodos.Merge(once, [Remote(updated: 5), Remote("bbbbbbbb", "New here", 1)], Asha, Now);

        Assert.True(twice.SequenceEqual(once));
    }

    [Fact]
    public void Done_on_another_pc_arrives_and_reopening_there_arrives_too()
    {
        var done = Assert.Single(OwnTodos.Merge([Shared()], [Remote(updated: 1, status: DeviceTodo.Done)], Asha, Now));
        Assert.Equal(MemberTaskStatus.Done, done.Status);

        var open = Assert.Single(OwnTodos.Merge([done], [Remote(updated: 2)], Asha, Now));
        Assert.Equal(MemberTaskStatus.New, open.Status);
    }

    [Fact]
    public void A_task_in_progress_here_keeps_its_lane_when_only_the_name_changed_elsewhere()
    {
        var doing = Shared() with { Status = MemberTaskStatus.Progress };

        Assert.Equal(MemberTaskStatus.Progress, OwnTodos.Merge([doing], [Remote(updated: 1)], Asha, Now)[0].Status);
    }

    // ---- Deletes ---------------------------------------------------------------------------------------

    [Fact]
    public void A_delete_on_another_pc_removes_an_older_copy_here()
    {
        Assert.Empty(OwnTodos.Merge([Shared(updated: 1, synced: 1)], [Remote(updated: 2, deleted: true)], Asha, Now));
    }

    [Fact]
    public void A_delete_wins_a_tie_with_an_edit_here()
    {
        Assert.Empty(OwnTodos.Merge([Shared(updated: 2, synced: 1)], [Remote(updated: 2, deleted: true)], Asha, Now));
    }

    [Fact]
    public void An_edit_here_after_the_delete_survives_and_goes_up_to_bring_it_back()
    {
        var local = Shared(updated: 5, synced: 1);

        var t = Assert.Single(OwnTodos.Merge([local], [Remote(updated: 2, deleted: true)], Asha, Now));

        Assert.True(OwnTodos.Dirty(t));
        Assert.Equal([t], OwnTodos.Unsent([t], new Dictionary<string, DateTimeOffset>(), Asha));
    }

    [Fact]
    public void A_to_do_deleted_here_does_not_come_back_with_the_next_pull_and_leaves_once_confirmed()
    {
        var gone = Shared(updated: 5, synced: 0) with { DeletedAt = At(5) };

        var kept = Assert.Single(OwnTodos.Merge([gone], [Remote(updated: 0)], Asha, Now)); // the server hasn't heard yet

        Assert.NotNull(kept.DeletedAt);
        Assert.Empty(OwnTodos.Merge([kept], [Remote(updated: 5, deleted: true)], Asha, Now));
    }

    [Fact]
    public void An_edit_elsewhere_after_our_delete_brings_it_back_here()
    {
        var gone = Shared(updated: 5, synced: 0) with { DeletedAt = At(5) };

        var t = Assert.Single(OwnTodos.Merge([gone], [Remote(updated: 6, title: "Still needed")], Asha, Now));

        Assert.Null(t.DeletedAt);
        Assert.Equal("Still needed", t.Title);
    }

    [Fact]
    public void A_delete_this_pc_never_saw_adds_nothing()
    {
        Assert.Empty(OwnTodos.Merge([], [Remote(deleted: true)], Asha, Now));
    }

    [Fact]
    public void The_task_list_hides_a_waiting_delete_but_keeps_it_on_disk()
    {
        var source = new LocalTaskSource(samples: false, directory: _dir);
        source.Save(Shared() with { DeletedAt = At(1) });
        source.Save(Legacy(id: 3));

        var reopened = new LocalTaskSource(samples: false, directory: _dir);

        Assert.Equal([3], reopened.Load().Select(t => t.Id));
        Assert.Equal(2, reopened.LoadAll().Count);
        Assert.Equal(4, reopened.NextId());
    }

    // ---- A new PC -------------------------------------------------------------------------------------

    [Fact]
    public void A_new_pc_gets_the_members_to_dos_and_nothing_to_send()
    {
        var rows = OwnTodos.Merge([],
        [
            Remote("aaaaaaaa", "Due today", due: Today),
            Remote("bbbbbbbb", "Due tomorrow", due: Today.AddDays(1)),
            Remote("cccccccc", "Due next week", due: Today.AddDays(6)),
            Remote("dddddddd", "Late", due: Today.AddDays(-2)),
            Remote("eeeeeeee", "No date", steps: new DeviceTodoStep("One", true)),
            Remote("ffffffff", "Finished", status: DeviceTodo.Done),
        ], Asha, Now);

        Assert.Equal([1, 2, 3, 4, 5, 6], rows.Select(t => t.Id));
        Assert.All(rows, t => Assert.True(t.Mine));
        Assert.All(rows, t => Assert.False(OwnTodos.Dirty(t)));
        Assert.All(rows, t => Assert.Equal(Asha, t.SyncedFor));
        Assert.Equal(
            [DueKey.Today, DueKey.Tomorrow, DueKey.Later, DueKey.Overdue, DueKey.Later, DueKey.Later],
            rows.Select(t => t.DueKey));
        Assert.Equal(("Today", "Tomorrow"), (rows[0].Due, rows[1].Due));
        Assert.Equal(Today.AddDays(6).ToString("ddd d MMM"), rows[2].Due);
        Assert.True(TaskBoard.IsLate(rows[3], Now));
        Assert.Equal("", rows[4].Due);
        Assert.Equal(MemberTaskStatus.Progress, rows[4].Status); // a ticked step means it's under way
        Assert.Equal(MemberTaskStatus.Done, rows[5].Status);
        Assert.Empty(OwnTodos.Unsent(rows, new Dictionary<string, DateTimeOffset>(), Asha));
        Assert.All(rows, t => Assert.Equal(TaskBoard.SharedTodoLine, TaskBoard.OwnLine(t, true)));
    }

    [Fact]
    public void Pulled_to_dos_get_ids_past_what_is_here_so_nothing_collides()
    {
        var rows = OwnTodos.Merge([Legacy(id: 9)], [Remote()], Asha, Now);

        Assert.Equal([9, 10], rows.Select(t => t.Id));
    }

    // ---- What goes up -----------------------------------------------------------------------------------

    [Fact]
    public void A_shared_to_do_goes_up_whole_without_notes_or_details()
    {
        var t = Shared(updated: 3, synced: 0) with
        {
            DueOn = Today, Details = "Private details", Notes = [new TaskNote("You", "Private note")],
            Steps = [new TaskStep("One", true), new TaskStep("Two", false)], Status = MemberTaskStatus.Done,
        };

        var wire = OwnTodos.ToWire(t);

        Assert.Equal(("aaaaaaaa", "Shared thing", Today, DeviceTodo.Done, At(3), false),
            (wire.ClientId, wire.Title, wire.DueDate!.Value, wire.Status, wire.UpdatedAt, wire.Deleted));
        Assert.Equal([("One", true), ("Two", false)], wire.Steps!.Select(s => (s.Title, s.Done)));
        var json = JsonSerializer.Serialize(wire);
        Assert.DoesNotContain("Private", json);
    }

    [Fact]
    public void A_picked_date_wins_over_the_day_today_meant()
    {
        var t = Shared() with { DueDate = Today.AddDays(3), DueOn = Today };

        Assert.Equal(Today.AddDays(3), OwnTodos.ToWire(t).DueDate);
    }

    [Fact]
    public void An_open_lane_goes_up_as_open_and_a_waiting_delete_as_a_delete()
    {
        Assert.Equal(DeviceTodo.Open, OwnTodos.ToWire(Shared() with { Status = MemberTaskStatus.Progress }).Status);
        Assert.True(OwnTodos.ToWire(Shared() with { DeletedAt = At(1) }).Deleted);
    }

    [Fact]
    public void Steps_are_cut_to_what_the_backend_takes()
    {
        var t = Shared() with { Steps = [.. Enumerable.Range(0, 60).Select(i => new TaskStep("s" + i, false))] };

        Assert.Equal(OwnTodos.MaxSteps, OwnTodos.ToWire(t).Steps!.Count);
    }

    [Fact]
    public void A_stamp_is_whole_milliseconds_and_always_later()
    {
        var raw = new DateTimeOffset(2026, 10, 30, 9, 0, 0, TimeSpan.Zero).AddTicks(1_234_567);

        var at = OwnTodos.Stamp(raw);

        Assert.Equal(0, at.Ticks % TimeSpan.TicksPerMillisecond);
        Assert.Equal(TimeSpan.Zero, at.Offset);
        Assert.Equal(at.AddMilliseconds(1), OwnTodos.Stamp(raw, at));
        Assert.Equal(at.AddDays(1).AddMilliseconds(1), OwnTodos.Stamp(raw, at.AddDays(1))); // the clock stepped back
    }

    [Fact]
    public void Unsent_is_each_change_once_a_run_so_a_refused_one_is_not_sent_for_ever()
    {
        var waiting = Shared(updated: 3, synced: 0);
        var handed = new Dictionary<string, DateTimeOffset>();

        Assert.Equal([waiting], OwnTodos.Unsent([waiting], handed, Asha));

        handed["aaaaaaaa"] = At(3);
        Assert.Empty(OwnTodos.Unsent([waiting], handed, Asha));

        var edited = waiting with { UpdatedAt = At(4) };
        Assert.Equal([edited], OwnTodos.Unsent([edited], handed, Asha));
    }

    [Fact]
    public void A_to_do_queued_offline_before_the_first_list_goes_up_and_is_adopted()
    {
        var offline = Shared(updated: 3, synced: null, member: null);

        var t = Assert.Single(OwnTodos.Merge([offline], [], Asha, Now));

        Assert.Equal(Asha, t.SyncedFor);
        Assert.Equal([t], OwnTodos.Unsent([t], new Dictionary<string, DateTimeOffset>(), Asha));
    }

    // ---- To-dos from before this update stay on this PC ------------------------------------------------

    [Fact]
    public void A_tasks_file_from_before_this_update_reads_as_local_only()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "tasks.json"), """
            [{"Id": 1, "Title": "Private thing", "Due": "Today", "DueKey": "Today", "Mine": true, "Status": "New",
              "SentAt": "2026-10-01T09:00:00+05:30"}]
            """);

        var t = Assert.Single(new LocalTaskSource(samples: false, directory: _dir).Load());

        Assert.Null(t.ClientId);
        Assert.False(OwnTodos.Dirty(t));
        Assert.Empty(OwnTodos.Unsent([t], new Dictionary<string, DateTimeOffset>(), Asha));
        Assert.Equal("Your own to-do · kept on this PC only", TaskBoard.OwnLine(t, sharingOn: true));
    }

    [Fact]
    public void The_merge_never_touches_a_legacy_to_do_nor_sends_it()
    {
        var legacy = Legacy();

        var rows = OwnTodos.Merge([legacy], [Remote()], Asha, Now);

        Assert.Same(legacy, rows[0]);
        Assert.Single(OwnTodos.Merge([legacy], [], Asha, Now));
        Assert.Empty(OwnTodos.Unsent(rows.Where(t => t.ClientId is null), new Dictionary<string, DateTimeOffset>(), Asha));
    }

    [Fact]
    public void Ticking_finishing_or_reopening_a_legacy_to_do_keeps_it_on_this_pc()
    {
        var legacy = Legacy() with { Steps = [new TaskStep("One", false)] };

        foreach (var t in new[] { TaskBoard.MarkDone(legacy), TaskBoard.ToggleStep(legacy, 0), TaskBoard.Reopen(TaskBoard.MarkDone(legacy)) })
        {
            Assert.Null(t.ClientId);
            Assert.Empty(OwnTodos.Unsent([t], new Dictionary<string, DateTimeOffset>(), Asha));
            Assert.Equal(TaskBoard.LocalTodoLine, TaskBoard.OwnLine(t, sharingOn: true));
        }
    }

    [Fact]
    public void Only_a_to_do_with_an_id_can_go_up()
    {
        Assert.Throws<ArgumentException>(() => OwnTodos.ToWire(Legacy()));
    }

    [Fact]
    public void Editing_a_legacy_to_do_shares_it()
    {
        var old = Legacy();

        var edited = OwnTodos.AfterEdit(old, old with { Title = "New name" }, sharingOn: true, Asha);

        Assert.NotNull(edited.ClientId);
        Assert.Equal(Asha, edited.SyncedFor);
        Assert.Equal(TaskBoard.SharedTodoLine, TaskBoard.OwnLine(edited, sharingOn: true));
    }

    [Theory]
    [InlineData("date")]
    [InlineData("steps")]
    public void A_new_date_or_a_new_step_is_an_edit_too(string what)
    {
        var old = Legacy();
        var changed = what == "date" ? old with { DueKey = DueKey.Tomorrow } : old with { Steps = [new TaskStep("One", false)] };

        Assert.NotNull(OwnTodos.AfterEdit(old, changed, sharingOn: true, Asha).ClientId);
    }

    [Fact]
    public void Saving_a_legacy_to_do_without_a_change_keeps_it_on_this_pc()
    {
        var old = Legacy() with { Steps = [new TaskStep("One", true)] };

        var same = old with { Steps = [new TaskStep("One", true)] };

        Assert.Null(OwnTodos.AfterEdit(old, same, sharingOn: true, Asha).ClientId);
    }

    [Fact]
    public void Editing_while_sharing_is_off_keeps_it_on_this_pc()
    {
        var old = Legacy();

        var edited = OwnTodos.AfterEdit(old, old with { Title = "New name" }, sharingOn: false, Asha);

        Assert.Null(edited.ClientId);
        Assert.Equal(TaskBoard.LocalTodoLine, TaskBoard.OwnLine(edited, sharingOn: false));
    }

    [Fact]
    public void Edits_of_an_already_shared_to_do_keep_its_id()
    {
        var old = Shared();

        Assert.Equal("aaaaaaaa", OwnTodos.AfterEdit(old, old with { Title = "New" }, sharingOn: true, Asha).ClientId);
    }

    // ---- What the member reads --------------------------------------------------------------------------

    [Fact]
    public void The_wording_says_who_can_see_it()
    {
        Assert.Equal("Just for you and your admin: your admin can see it, and it follows you to a new PC.", TaskBoard.FormHint(null, sharingOn: true));
        Assert.Equal("Just for you: it stays on this PC, under My to-dos.", TaskBoard.FormHint(null, sharingOn: false));
        Assert.Equal("Your own to-do · your admin can see this", TaskBoard.OwnLine(Shared(), sharingOn: true));
        Assert.Equal("Your own to-do · kept on this PC only", TaskBoard.OwnLine(Legacy(), sharingOn: true));
    }

    [Fact]
    public void Before_a_legacy_edit_the_sheet_says_what_saving_does()
    {
        Assert.Contains("your admin can see it", TaskBoard.FormHint(Legacy(), sharingOn: true));
        Assert.Equal("Change the name, date or steps.", TaskBoard.FormHint(Legacy(), sharingOn: false));
        Assert.Equal("Change the name, date or steps.", TaskBoard.FormHint(Shared(), sharingOn: true));
    }

    [Fact]
    public void A_to_do_that_reached_the_server_still_says_so_when_sharing_goes_off()
    {
        Assert.Equal(TaskBoard.SharedTodoLine, TaskBoard.OwnLine(Shared(), sharingOn: false));
        Assert.Equal(TaskBoard.LocalTodoLine, TaskBoard.OwnLine(Shared(synced: null), sharingOn: false));
    }

    [Fact]
    public void Only_a_change_that_really_goes_somewhere_says_it_is_waiting()
    {
        Assert.False(TaskBoard.WaitsToSend(Legacy() with { Queued = true }));
        Assert.True(TaskBoard.WaitsToSend(Shared() with { Queued = true }));
        Assert.True(TaskBoard.WaitsToSend(new MemberTask { Queued = true, RemoteId = 4 }));
    }

    // ---- Someone else on this PC --------------------------------------------------------------------------

    [Fact]
    public void Another_members_shared_to_dos_leave_and_their_unsent_ones_stay_here_only()
    {
        const long ben = 8;
        var clean = Shared("aaaaaaaa", 1);
        var unsent = Shared("bbbbbbbb", 2, updated: 3);
        var deleted = Shared("cccccccc", 3, updated: 3) with { DeletedAt = At(3) };
        var legacy = Legacy(4);

        var rows = OwnTodos.Merge([clean, unsent, deleted, legacy], [Remote("dddddddd", "Ben's")], ben, Now);

        Assert.Equal([2, 4, 5], rows.Select(t => t.Id));
        Assert.Null(rows[0].ClientId); // never sent as Ben's
        Assert.False(OwnTodos.Dirty(rows[0]));
        Assert.Equal(ben, rows[2].SyncedFor);
        Assert.Empty(OwnTodos.Unsent(rows, new Dictionary<string, DateTimeOffset>(), ben));
    }

    [Fact]
    public void Nothing_of_the_old_members_goes_up_while_the_new_list_is_not_in()
    {
        var unsent = Shared(updated: 3);

        Assert.Empty(OwnTodos.Unsent([unsent], new Dictionary<string, DateTimeOffset>(), member: null));
        Assert.Empty(OwnTodos.Unsent([unsent], new Dictionary<string, DateTimeOffset>(), member: 8));
    }
}

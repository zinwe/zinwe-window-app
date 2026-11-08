using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows.Threading;
using Zinwe.Agent.Core.Backend;
using Zinwe.Agent.Core.Chat;
using Zinwe.Agent.Core.Ipc;
using Zinwe.Agent.Core.Messages;
using Zinwe.Agent.Core.Tasks;
using Zinwe.Agent.Core.Work;
using Zinwe.Agent.Tray.Widget;

namespace Zinwe.Agent.Tray;

/// <summary>The Tasks tab's chips (v1.4): All · Today · Upcoming · Done, each with its count.</summary>
public enum TaskChip { All, Today, Upcoming, Done }

/// <summary>The chat list's chips (v1.4): All · Unread · Groups.</summary>
public enum ChatChip { All, Unread, Groups }

/// <summary>
/// One task on the v1.4 Tasks tab, Home and in chat: an owner's (team) task with the member's queued actions applied,
/// or the member's own to-do, both as a <see cref="DeviceTask"/> so one engine (<see cref="TeamTaskEngine"/>) words them.
/// <paramref name="Key"/> is the tray's task id (own to-dos 1, 2, 3 …; an owner's task -remote id).
/// </summary>
public sealed record TaskItem(int Key, DeviceTask Task, TaskContext Ctx, MemberTask? Local)
{
    public bool Own => Ctx.Own;
    public long? RemoteId => Own ? null : Task.Id;
    public string ChatKey => ChatKeys.Task(Task.Id);
    public Happening Happen => TeamTaskEngine.Happen(Task, Ctx);
}

/// <summary>One line of a task chat as the panel draws it.</summary>
public sealed record TaskChatLine(string Who, string Initials, bool Mine, bool Owner, string Text, DateTimeOffset At, bool Sending);

/// <summary>One row of the v1.4 chat list: the admin chat, a group, a direct chat, or a teammate not written to yet.</summary>
public sealed record ChatRowView(string Key, string Name, string Initials, bool IsGroup, bool IsAdmin, string Line, bool LineMuted,
    string Time, int Unread, bool Marked, bool Pinned, bool Muted, string? Status, DateTimeOffset At, long? WithMemberId);

/// <summary>A line of a group / direct thread as the view draws it.</summary>
public sealed record TeamLine(TeamMessage Message, bool Mine, bool Sending, string Who, string Initials);

public sealed partial class TrayModel
{
    // ======================================================================================================
    // v1.4 · team tasks (member side)
    // ======================================================================================================

    /// <summary>
    /// This member's id on the server: from the to-do list, else a task's <c>me</c>. Null on a 1.3 backend that never
    /// said (the engine then works it out per task).
    /// </summary>
    public long? MeId => _todos?.MemberId ?? _remote?.Tasks?.Select(t => t.Me?.MemberId).FirstOrDefault(x => x is not null);

    /// <summary>The member's name as the backend has it on a task (for their initials), else the profile's.</summary>
    public string MyName =>
        (MeId is { } me ? _remote?.Tasks?.SelectMany(t => t.People ?? []).FirstOrDefault(p => p.MemberId == me && p.Name.Length > 0)?.Name : null)
        ?? Profile.DisplayName;

    private TaskContext Ctx(DeviceTask? t, bool own) => new(MeId, OwnerFirst, OwnerFirstStart, Now,
        t?.GroupId is { } g ? _chat?.Conversations?.FirstOrDefault(c => c.Id == g)?.Name : null, own);

    /// <summary>Every task the v1.4 Tasks tab lists: the member's own to-dos and the owner's tasks (team or 1.3).</summary>
    public IReadOnlyList<TaskItem> TaskItems
    {
        get
        {
            OwnerTasks.MeId = MeId; // the overlay and the history name teammates by it
            var list = new List<TaskItem>();
            foreach (var t in Tasks.Load())
            {
                if (t.Mine)
                {
                    var d = OwnToDevice(t);
                    list.Add(new TaskItem(t.Id, d, Ctx(d, own: true), t));
                }
                else if (!RemoteOn && t.DeletedAt is null)
                {
                    var d = SampleToDevice(t);
                    list.Add(new TaskItem(t.Id, d, Ctx(d, own: false), t));
                }
            }
            if (RemoteOn)
            {
                foreach (var d in OwnerTasks.Overlay(_remote!.Tasks ?? [], _actions))
                {
                    list.Add(new TaskItem(OwnerTasks.IdOf(d.Id), d, Ctx(d, own: false), null));
                }
            }
            return list;
        }
    }

    public TaskItem? Item(int key) => TaskItems.FirstOrDefault(i => i.Key == key);

    /// <summary>The task an admin chat card or a task chat line points at (an owner's task, by its server id).</summary>
    public TaskItem? ItemByRemote(long remoteId) => TaskItems.FirstOrDefault(i => i.RemoteId == remoteId);

    /// <summary>An own to-do as the engine reads it: one person (you), every step yours, done = "approved".</summary>
    public static DeviceTask OwnToDevice(MemberTask t)
    {
        var (day, at) = TaskBoard.When(t);
        DateTimeOffset? due = at ?? (day is { } d ? new DateTimeOffset(d.ToDateTime(TimeOnly.MinValue), TimeZoneInfo.Local.GetUtcOffset(d.ToDateTime(TimeOnly.MinValue))) : null);
        var status = t.Status == MemberTaskStatus.Cancelled ? "cancelled" : TaskBoard.Ticked(t) ? "approved" : "progress";
        return new DeviceTask(t.Id, t.Title, t.Details, due, t.High ? "high" : "normal",
            [.. t.Steps.Select((s, i) => new DeviceTaskStep(i + 1, s.Label, s.Done))], status, CreatedAt: t.SentAt,
            Activity: [.. t.Activity.Select(a => new DeviceTaskActivity(a.At, "member", "note", a.Text))]);
    }

    /// <summary>A sample owner's task (Debug, no backend) as the engine reads it.</summary>
    private static DeviceTask SampleToDevice(MemberTask t)
    {
        var (day, at) = TaskBoard.When(t);
        DateTimeOffset? due = at ?? (day is { } d ? new DateTimeOffset(d.ToDateTime(TimeOnly.MinValue), TimeZoneInfo.Local.GetUtcOffset(d.ToDateTime(TimeOnly.MinValue))) : null);
        var status = t.Status switch
        {
            MemberTaskStatus.New => t.IsNew ? "sent" : "seen",
            MemberTaskStatus.Confirmed => "seen",
            MemberTaskStatus.Progress => "progress",
            MemberTaskStatus.Help => "help",
            MemberTaskStatus.Cant => "cantdo",
            MemberTaskStatus.Review or MemberTaskStatus.Done => "review",
            MemberTaskStatus.Approved => "approved",
            _ => "cancelled",
        };
        return new DeviceTask(t.Id, t.Title, t.Details, due, t.High ? "high" : "normal",
            [.. t.Steps.Select((s, i) => new DeviceTaskStep(i + 1, s.Label, s.Done))], status, CreatedAt: t.SentAt);
    }

    public TaskChip Chip { get; private set; } = TaskChip.All;
    public string TaskQuery { get; private set; } = "";

    public void SetChip(TaskChip chip)
    {
        Chip = chip;
        Raise();
    }

    public void SetTaskQuery(string q)
    {
        if (q == TaskQuery)
        {
            return;
        }
        TaskQuery = q;
        Raise();
    }

    /// <summary>The chip's rule (engine_win.js inView): Done = finished; else Today = due by today (no date counts), Upcoming = later.</summary>
    public static bool InChip(TaskItem i, TaskChip chip, DateTimeOffset now)
    {
        var bucket = TeamTaskEngine.Bucket(i.Task, i.Ctx);
        if (chip == TaskChip.Done)
        {
            return bucket == TaskBucket.Done;
        }
        if (bucket == TaskBucket.Done)
        {
            return false;
        }
        var d = TeamTaskEngine.DayOf(i.Task, now);
        return chip switch { TaskChip.Today => d <= 0, TaskChip.Upcoming => d > 0, _ => true };
    }

    public int ChipCount(TaskChip chip) => TaskItems.Count(i => InChip(i, chip, Now));

    /// <summary>The Tasks tab's sections: Needs you · Waiting (or Done), each sorted by when it's due.</summary>
    public IReadOnlyList<(string Name, string Sub, IReadOnlyList<TaskItem> Items)> TaskSections
    {
        get
        {
            var now = Now;
            var q = TaskQuery.Trim();
            var shown = TaskItems.Where(i => InChip(i, Chip, now) && (q.Length == 0 || i.Task.Title.Contains(q, StringComparison.CurrentCultureIgnoreCase)))
                .OrderBy(i => TeamTaskEngine.Rank(i.Task, now)).ThenBy(i => i.Key).ToList();
            var defs = Chip == TaskChip.Done
                ? new[] { (TaskBucket.Done, "Done", OwnerFirstStart + " gave the OK") }
                : [(TaskBucket.Needs, "Needs you", "Your turn, new, or sent back"), (TaskBucket.Wait, "Waiting", "On a teammate or on " + OwnerFirst)];
            var sections = new List<(string, string, IReadOnlyList<TaskItem>)>();
            foreach (var (bucket, name, sub) in defs)
            {
                var rows = shown.Where(i => TeamTaskEngine.Bucket(i.Task, i.Ctx) == bucket).ToList();
                if (rows.Count > 0)
                {
                    sections.Add((name, sub, rows));
                }
            }
            return sections;
        }
    }

    /// <summary>Tasks waiting on the member (not their own to-dos): the Home focus and Needs you rows.</summary>
    public IReadOnlyList<TaskItem> NeedsYouTasks =>
        [.. TaskItems.Where(i => !i.Own && TeamTaskEngine.Bucket(i.Task, i.Ctx) == TaskBucket.Needs)
            .OrderBy(i => TeamTaskEngine.Rank(i.Task, Now)).ThenBy(i => i.Key)];

    /// <summary>Home's focus (engine_win.js): your turn to tick first, then a sent-back task, then one in progress, else the next new one.</summary>
    public TaskItem? FocusItem
    {
        get
        {
            var open = NeedsYouTasks;
            string Label(TaskItem i) => i.Happen.Buttons.FirstOrDefault()?.Label ?? "";
            return open.FirstOrDefault(i => Label(i).StartsWith("Tick", StringComparison.Ordinal))
                ?? open.FirstOrDefault(i => Label(i).StartsWith("Start again", StringComparison.Ordinal))
                ?? open.FirstOrDefault(i => TeamTaskEngine.StatusOf(i.Task) == "progress")
                ?? open.FirstOrDefault();
        }
    }

    // ---- The member task panel (Overview · Chat · Activity) -----------------------------------------------

    /// <summary>The open panel's tab: "overview", "chat" or "activity".</summary>
    public string PanelTab { get; private set; } = "overview";

    /// <summary>The inline note card in What's happening: null, "help" or "cantdo".</summary>
    public string? PanelCard { get; private set; }

    public bool PanelMenu { get; private set; }

    /// <summary>The task whose panel is open (the details card's slot, <see cref="DetailId"/>).</summary>
    public TaskItem? PanelItem => DetailId is { } id ? Item(id) : null;

    /// <summary>Open a task's panel on a tab (a card, a notice, Home, a chat card's "Open task chat").</summary>
    public void OpenPanel(int key, string tab = "overview")
    {
        if (Tab != Tab.Tasks && Tab != Tab.Chat)
        {
            Tab = Tab.Tasks;
            Push = Push.None;
        }
        PanelTab = tab;
        PanelCard = null;
        PanelMenu = false;
        OpenDetail(key);
        if (Item(key) is { Own: false } item && TeamTaskEngine.StatusOf(item.Task) == "sent")
        {
            ActRemote(item, TaskActions.Seen);
        }
        if (tab == "chat")
        {
            _ = RefreshChatAsync();
        }
    }

    /// <summary>A notice or Home row about a task: Tasks, with its panel open.</summary>
    public void OpenTaskPanel(int key)
    {
        if (Item(key) is { } item)
        {
            Chip = TeamTaskEngine.Bucket(item.Task, item.Ctx) == TaskBucket.Done ? TaskChip.Done : TaskChip.All;
        }
        Go(Tab.Tasks);
        OpenPanel(key);
    }

    public void SetPanelTab(string tab)
    {
        PanelTab = tab;
        PanelMenu = false;
        Raise();
        if (tab == "chat")
        {
            _ = RefreshChatAsync();
        }
    }

    public void TogglePanelMenu()
    {
        PanelMenu = !PanelMenu;
        Raise();
    }

    public void StartPanelCard(string? kind)
    {
        PanelCard = kind;
        PanelMenu = false;
        if (kind is not null)
        {
            PanelTab = "overview";
        }
        Raise();
    }

    /// <summary>Need help / Can't do it, sent from the inline card: the task waits with the owner, who answers in the task chat.</summary>
    public void SubmitPanelCard(TaskItem item, string text)
    {
        var note = string.IsNullOrWhiteSpace(text) ? "No reason given" : text.Trim();
        if (PanelCard == "help")
        {
            ActRemote(item, TaskActions.Help, note);
            ShowSnack($"Asked {OwnerFirst}. The answer lands in the task chat.", [("OK", CloseSnack)]);
        }
        else if (PanelCard == "cantdo")
        {
            ActRemote(item, TaskActions.Cantdo, note);
            ShowSnack($"Told {OwnerFirst} you can’t do it.", [("OK", CloseSnack)]);
        }
        PanelCard = null;
        Raise();
    }

    /// <summary>A button of What's happening / a card / Home: start, tick step N, finish.</summary>
    public void RunTaskAct(TaskItem item, string act)
    {
        if (act.StartsWith("tick:", StringComparison.Ordinal) && int.TryParse(act[5..], NumberStyles.None, CultureInfo.InvariantCulture, out var index))
        {
            TickStep(item, index);
            return;
        }
        if (item.Own && item.Local is { } own)
        {
            if (act == "finish")
            {
                DoneTask(own);
            }
            return;
        }
        switch (act)
        {
            case "start":
                var took = TeamTaskEngine.IsAny(item.Task) && TeamTaskEngine.Team(item.Task, item.Ctx);
                var others = TeamTaskEngine.People(item.Task, item.Ctx).Where(p => p.MemberId != TeamTaskEngine.Me(item.Task, item.Ctx))
                    .Select(p => p.Name.Split(' ')[0]).ToList();
                ActRemote(item, TaskActions.Start);
                if (took && others.Count > 0)
                {
                    ShowSnack("It’s yours. " + TeamTaskEngine.List(others) + " no longer see it.", [("OK", CloseSnack)]);
                }
                break;
            case "finish":
                ActRemote(item, TaskActions.Finish);
                ShowSnack(TeamTaskEngine.Team(item.Task, item.Ctx) ? "Your part is done." : $"Done. {OwnerFirstStart} gives the OK next.", [("OK", CloseSnack)]);
                break;
        }
        Raise();
    }

    /// <summary>Tick a step (only the member's own or anyone's, in order when the task says so).</summary>
    public void TickStep(TaskItem item, int index)
    {
        var rows = TeamTaskEngine.StepRows(item.Task, item.Ctx);
        if (index < 0 || index >= rows.Count)
        {
            return;
        }
        var row = rows[index];
        if (!row.CanTick)
        {
            ShowSnack(row.Lock.Length > 0 ? row.Lock + "." : TeamTaskEngine.WhoseStep(row), [("OK", CloseSnack)]);
            return;
        }
        if (item.Own && item.Local is { } own)
        {
            var toggled = TaskBoard.ToggleStep(own, index);
            if (ReferenceEquals(toggled, own))
            {
                return;
            }
            SaveOwn(toggled with { Queued = Offline || own.Queued });
            if (TaskBoard.AllStepsDone(toggled))
            {
                DoneTask(toggled); // the last step of an own to-do: done
                return;
            }
            Raise();
            return;
        }
        ActRemote(item, TaskActions.Tick, stepId: row.Id);
        var after = OwnerTasks.Overlay([item.Task], _actions.Where(a => a.TaskId == item.Task.Id)).FirstOrDefault();
        if (after is not null && TeamTaskEngine.StatusOf(after) == "review")
        {
            ShowSnack($"All done. Waiting for {OwnerFirst}’s OK.", [("OK", CloseSnack)]);
        }
        Raise();
    }

    /// <summary>Queue a member action on an owner's task in the words this backend takes (v1.4, or 1.3 for an older one).</summary>
    private void ActRemote(TaskItem item, string action, string? note = null, long? stepId = null)
    {
        if (item.RemoteId is not { } id)
        {
            return;
        }
        var (a, n, s, done) = TaskActions.ForBackend(action, note, stepId, TeamTaskEngine.IsTeamShape(item.Task));
        try
        {
            var queued = _outbox.AppendTaskAction(id, a, n, s, done);
            _actions.Add(queued);
            PendingInOutbox.Add(id);
        }
        catch (Exception)
        {
            // the file is busy: the member can press again
        }
    }

    /// <summary>Own to-do ⋯: Move to tomorrow, Edit, Delete.</summary>
    public void OwnMenu(TaskItem item, string what)
    {
        PanelMenu = false;
        if (item.Local is not { } t)
        {
            return;
        }
        switch (what)
        {
            case "tomorrow":
                CloseDetail();
                MoveToTomorrow(t);
                break;
            case "edit":
                CloseDetail();
                OpenNewTask(t);
                break;
            case "delete":
                CloseDetail();
                DeleteOwn(t);
                break;
        }
        Raise();
    }

    /// <summary>The task chat: the service's thread when there is one, else the task's notes (a 1.3 backend); queued lines last.</summary>
    public IReadOnlyList<TaskChatLine> TaskChat(TaskItem item)
    {
        var lines = new List<TaskChatLine>();
        var me = MeId;
        if (!item.Own && _chat?.Thread is { Supported: true } thread && thread.Key == item.ChatKey)
        {
            foreach (var m in thread.Messages ?? [])
            {
                var mine = m.Mine ?? (me is not null && m.ByMemberId == me && !m.ByOwner);
                var who = mine ? "You" : m.ByOwner ? OwnerFirstStart : First(m.ByName);
                lines.Add(new TaskChatLine(who, mine ? Initials(MyName) : TeamTaskEngine.Initials(m.ByOwner ? Status?.OwnerName ?? m.ByName : m.ByName),
                    mine, m.ByOwner, m.Deleted ? "This message was deleted" : m.Body, m.At, false));
            }
        }
        else
        {
            foreach (var a in (item.Task.Activity ?? []).Where(a => !string.IsNullOrWhiteSpace(a.Note)
                && (a.Action ?? "").ToLowerInvariant() is "note" or "help" or "cantdo" or "reopen" or "reopened" or "reply" or "approved" or "approve"))
            {
                var mine = string.Equals(a.By, "member", StringComparison.OrdinalIgnoreCase) && (a.MemberId is null || a.MemberId == me);
                if (item.Own && (a.Action ?? "") == "note" && a.Note is { } n && (n.StartsWith("You ", StringComparison.Ordinal) || n == "You added it"))
                {
                    continue; // an own to-do's history line, not words
                }
                var who = mine ? "You" : string.Equals(a.By, "owner", StringComparison.OrdinalIgnoreCase) ? OwnerFirstStart : First(a.Name) is { Length: > 0 } f ? f : "A teammate";
                lines.Add(new TaskChatLine(who, mine ? Initials(MyName) : TeamTaskEngine.Initials(who == OwnerFirstStart ? Status?.OwnerName : a.Name),
                    mine, !mine && who == OwnerFirstStart, a.Note!.Trim(), a.At, false));
            }
        }
        foreach (var p in PendingChat(item.ChatKey))
        {
            if (lines.Any(l => l.Mine && l.Text == p.Body && l.At >= p.CreatedAtUtc.AddMinutes(-2)))
            {
                continue;
            }
            lines.Add(new TaskChatLine("You", Initials(MyName), true, false, p.Body, p.CreatedAtUtc, true));
        }
        return [.. lines.OrderBy(l => l.At)];
    }

    /// <summary>"Message everyone on this task…": into the task chat (on a 1.3 backend the agent posts it as a note).</summary>
    public void SendTaskChat(TaskItem item, string text)
    {
        var body = (text ?? "").Trim();
        if (body.Length == 0 || item.Own)
        {
            return;
        }
        if (Status?.ChatVersion is not null)
        {
            QueueChat(OutboxReply.ChatSend, item.ChatKey, body);
        }
        else
        {
            ActRemote(item, TaskActions.Note, body);
        }
        PanelTab = PanelTab == "overview" ? PanelTab : "chat";
        Raise();
    }

    /// <summary>The task chat's numbers for a card: lines, unread, and the last line ("Ananya: Great").</summary>
    public (int Count, int Unread, string? LastWho, string? LastText, DateTimeOffset? LastAt) TaskChatSummary(TaskItem item)
    {
        if (item.Own)
        {
            return (0, 0, null, null, null);
        }
        var reading = PanelItem?.Key == item.Key && PanelTab == "chat";
        if (item.Task.Chat is { } c)
        {
            var who = c.Last?.By is { Length: > 0 } by ? (string.Equals(First(by), First(MyName), StringComparison.OrdinalIgnoreCase) ? "You" : First(by)) : null;
            return (c.Count, reading ? 0 : c.Unread, who, c.Last?.Text, c.Last?.At);
        }
        var notes = TaskChat(item);
        var last = notes.LastOrDefault();
        return (notes.Count, 0, last?.Who, last?.Text, last?.At);
    }

    private static string First(string? name) => string.IsNullOrWhiteSpace(name) ? "" : name.Trim().Split(' ')[0];

    private static string Initials(string? name) => TeamTaskEngine.Initials(name);

    // ======================================================================================================
    // v1.4 · team chat
    // ======================================================================================================

    private ChatReply? _chat;
    private readonly DispatcherTimer _chatTimer = new() { Interval = TimeSpan.FromSeconds(15) };
    private bool _chatTimerOn;
    private readonly Dictionary<long, int> _unreadSeen = [];
    private ChatPrefs _prefs = ChatPrefs.Load();

    /// <summary>The window can be seen (the open thread is pulled every 3 s only then).</summary>
    public bool WindowVisible { get; set; }

    /// <summary>True when the service and the backend both have team chat: the Chat tab is a list, as on the phone.</summary>
    public bool TeamChatOn => Status?.ChatVersion is not null && _chat is { Backend: TaskBackend.On };

    /// <summary>The open thread on Chat: null = the list, "admin" = the admin chat, "conv:N" / "direct:M" = a team thread.</summary>
    public string? ChatOpen { get; private set; }

    public ChatChip ChatFilter { get; private set; } = ChatChip.All;
    public string ChatQuery { get; private set; } = "";

    /// <summary>The list row whose ⋯ menu is open.</summary>
    public string? ConvMenu { get; private set; }

    /// <summary>The line the member is answering in a team thread.</summary>
    public TeamMessage? TeamReplyTo { get; private set; }

    /// <summary>The report card over the thread: the thread, the message (null = the person), and who.</summary>
    public (string Key, TeamMessage? Message, string Who)? Report { get; private set; }

    public IReadOnlyList<TeamConversation> Conversations => _chat?.Conversations ?? [];

    private void StartChatPolling()
    {
        if (_chatTimerOn)
        {
            return;
        }
        _chatTimerOn = true;
        _chatTimer.Tick += async (_, _) => await RefreshChatAsync();
        _chatTimer.Start();
    }

    /// <summary>The thread the member is reading now (pulled every 3 s): a team thread or an open task chat.</summary>
    public string? ChatFocus =>
        !WindowVisible || Stage != Stage.App ? null
        : Tab == Tab.Chat && Push == Push.None && ChatKeys.Parse(ChatOpen) is not null && DetailId is null ? ChatOpen
        : PanelItem is { Own: false } p && PanelTab == "chat" ? p.ChatKey
        : null;

    /// <summary>Ask the service for the chat (focus included); 3 s while a thread is open, 15 s otherwise.</summary>
    public async Task RefreshChatAsync()
    {
        StartChatPolling();
        if (Status?.ChatVersion is null)
        {
            if (Status is not null && _chat is not null)
            {
                _chat = null; // an older service
                Raise();
            }
            return;
        }
        var focus = ChatFocus;
        _chatTimer.Interval = TimeSpan.FromSeconds(focus is null ? 15 : 3);
        if (await AgentPipeClient.GetChatAsync(focus, TimeSpan.FromSeconds(3)) is not { } reply)
        {
            return;
        }
        var changed = reply.Version != _chat?.Version || _chat is null;
        _chat = reply;
        ResolveDirect();
        AnnounceChat(reply);
        if (changed)
        {
            Raise();
        }
    }

    /// <summary>A direct chat started from a teammate's row: once the list has it, the thread moves to its real key.</summary>
    private void ResolveDirect()
    {
        if (ChatKeys.Parse(ChatOpen) is ("direct", var member)
            && Conversations.FirstOrDefault(c => !c.IsGroup && c.WithMemberId == member) is { } conv)
        {
            ChatOpen = conv.Key;
        }
    }

    /// <summary>A new line in a group or direct chat the member isn't reading: a notice (and a toast while Zinwe is away).</summary>
    private void AnnounceChat(ChatReply reply)
    {
        foreach (var c in reply.Conversations ?? [])
        {
            var before = _unreadSeen.GetValueOrDefault(c.Id, -1);
            _unreadSeen[c.Id] = c.Unread;
            if (before < 0 || c.Unread <= before || ChatFocus == c.Key || _prefs.Muted.Contains(c.Key) || Stage != Stage.App)
            {
                continue;
            }
            var who = c.Last is { } l ? (l.ByOwner ? OwnerFirstStart : First(l.ByName)) : "";
            var text = c.Last is { } last ? (last.TaskId is not null ? "Task: " + TaskTitle(last.TaskId) : last.Body) : "";
            Notify(c.IsGroup ? "New message in " + c.Name : "New message from " + c.Name,
                (c.IsGroup && who.Length > 0 ? who + ": " : "") + text, "chat:" + c.Key, "Open", "chat:" + c.Key + ":" + c.Last?.Id);
        }
    }

    private string TaskTitle(long? remoteId) => remoteId is { } id && ItemByRemote(id) is { } i ? i.Task.Title : "a task";

    /// <summary>The Chat tab's badge: how many chats have something unread (admin chat included).</summary>
    public int ChatBadge
    {
        get
        {
            if (!TeamChatOn)
            {
                return Unread.Chat;
            }
            var admin = ChatFeed.UnreadCount(Notes.Items) > 0 ? 1 : 0;
            return admin + Conversations.Count(c => (c.Unread > 0 || _prefs.Marked.Contains(c.Key)) && ChatFocus != c.Key);
        }
    }

    public void OpenConv(string key)
    {
        Tab = Tab.Chat;
        Push = Push.None;
        DetailId = null;
        ChatOpen = key;
        ConvMenu = null;
        TeamReplyTo = null;
        Report = null;
        if (key == "admin")
        {
            ReadChat();
        }
        _prefs = _prefs.Unmark(key);
        History.MarkRead(e => e.Go == "chat:" + key);
        Raise();
        _ = RefreshChatAsync();
    }

    /// <summary>A teammate's row on a task: their direct chat (started on the first line when there is none yet).</summary>
    public void OpenDirect(long memberId)
    {
        if (!TeamChatOn)
        {
            return;
        }
        var key = Conversations.FirstOrDefault(c => !c.IsGroup && c.WithMemberId == memberId)?.Key ?? ChatKeys.Direct(memberId);
        CloseDetail();
        Go(Tab.Chat);
        OpenConv(key);
    }

    /// <summary>Back from a thread to the list.</summary>
    public void BackToChatList()
    {
        ChatOpen = null;
        TeamReplyTo = null;
        Report = null;
        ConvMenu = null;
        Raise();
    }

    public void SetChatFilter(ChatChip chip)
    {
        ChatFilter = chip;
        Raise();
    }

    public void SetChatQuery(string q)
    {
        if (q == ChatQuery)
        {
            return;
        }
        ChatQuery = q;
        Raise();
    }

    public void ToggleConvMenu(string? key)
    {
        ConvMenu = ConvMenu == key ? null : key;
        Raise();
    }

    /// <summary>The chat list: the admin chat pinned on top, then pins, then the newest first; teammates not written to last.</summary>
    public IReadOnlyList<ChatRowView> ChatRows
    {
        get
        {
            var rows = new List<ChatRowView>();
            var now = Now;
            // The admin chat
            var admin = Notes.Items.Where(i => i.IsChat && i.TaskId is null).MaxBy(i => i.CreatedAtUtc);
            var adminLine = admin is null ? "Say hi to " + OwnerFirst : (admin.IsFromAdmin ? "" : "You: ") + ChatFeed.Preview(admin, OwnerFirst);
            rows.Add(new ChatRowView("admin", OwnerNameStart, TeamTaskEngine.Initials(Status?.OwnerName ?? "A"), false, true, adminLine, admin is null,
                admin is null ? "" : When(admin.CreatedAtUtc, now), ChatFeed.UnreadCount(Notes.Items), _prefs.Marked.Contains("admin"), true,
                _prefs.Muted.Contains("admin"), null, admin?.CreatedAtUtc ?? DateTimeOffset.MinValue, null));
            var me = MeId;
            foreach (var c in Conversations)
            {
                var last = c.Last;
                string line;
                var muted = false;
                if (last is null)
                {
                    line = c.IsGroup ? "No messages yet" : "Say hi to " + First(c.Name);
                    muted = true;
                }
                else if (last.TaskId is not null)
                {
                    line = "Task: " + TaskTitle(last.TaskId);
                }
                else if (last.Deleted)
                {
                    line = "This message was deleted";
                    muted = true;
                }
                else
                {
                    var mine = last.Mine ?? (me is not null && last.ByMemberId == me && !last.ByOwner);
                    line = (mine ? "You: " : c.IsGroup ? (last.ByOwner ? OwnerFirstStart : First(last.ByName)) + ": " : "") + last.Body;
                }
                var at = c.LastAt ?? (last?.At is { } la && la != default ? la : (DateTimeOffset?)null);
                var person = c.WithMemberId is { } wid ? _chat?.People?.FirstOrDefault(p => p.MemberId == wid) : null;
                rows.Add(new ChatRowView(c.Key, c.Name, TeamTaskEngine.Initials(c.Name), c.IsGroup, false, line, muted,
                    at is { } x ? When(x, now) : "", ChatFocus == c.Key ? 0 : c.Unread, _prefs.Marked.Contains(c.Key), _prefs.Pins.Contains(c.Key),
                    _prefs.Muted.Contains(c.Key), person?.Status, at ?? DateTimeOffset.MinValue, c.WithMemberId));
            }
            // Teammates one can message but hasn't yet
            foreach (var p in _chat?.People ?? [])
            {
                if (p.MemberId == me || Conversations.Any(c => !c.IsGroup && c.WithMemberId == p.MemberId))
                {
                    continue;
                }
                var key = ChatKeys.Direct(p.MemberId);
                rows.Add(new ChatRowView(key, p.Name, TeamTaskEngine.Initials(p.Name), false, false, "Say hi to " + First(p.Name), true, "", 0,
                    false, _prefs.Pins.Contains(key), false, p.Status, DateTimeOffset.MinValue, p.MemberId));
            }
            var q = ChatQuery.Trim();
            bool Hit(ChatRowView r)
            {
                if (q.Length > 0 && !r.Name.Contains(q, StringComparison.CurrentCultureIgnoreCase)
                    && !(Conversations.FirstOrDefault(c => c.Key == r.Key)?.Members ?? []).Any(m => m.Name.Contains(q, StringComparison.CurrentCultureIgnoreCase)))
                {
                    return false;
                }
                return ChatFilter switch
                {
                    ChatChip.Unread => r.Unread > 0 || r.Marked,
                    ChatChip.Groups => r.IsGroup,
                    _ => true,
                };
            }
            return [.. rows.Where(Hit)
                .OrderBy(r => r.IsAdmin ? -1 : r.Pinned ? _prefs.Pins.IndexOf(r.Key) : 99)
                .ThenByDescending(r => r.At)
                .ThenBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase)];
        }
    }

    public int ChatUnreadRows => ChatRows.Count(r => r.Unread > 0 || r.Marked);

    public int ChatGroupCount => Conversations.Count(c => c.IsGroup);

    private static string When(DateTimeOffset at, DateTimeOffset now)
    {
        var d = DateOnly.FromDateTime(at.ToLocalTime().DateTime).DayNumber - DateOnly.FromDateTime(now.ToLocalTime().DateTime).DayNumber;
        return d == 0 ? TeamTaskEngine.Clock(at) : d == -1 ? "Yesterday" : TeamTaskEngine.DayLabel(at);
    }

    public void TogglePin(string key)
    {
        ConvMenu = null;
        if (_prefs.Pins.Contains(key))
        {
            _prefs = _prefs with { Pins = [.. _prefs.Pins.Where(p => p != key)] };
        }
        else if (_prefs.Pins.Count >= 3)
        {
            ShowSnack("You can pin up to 3 chats.", [("OK", CloseSnack)]);
            return;
        }
        else
        {
            _prefs = _prefs with { Pins = [.. _prefs.Pins, key] };
        }
        _prefs.Save();
        Raise();
    }

    public void ToggleMute(string key)
    {
        ConvMenu = null;
        _prefs = _prefs with { Muted = _prefs.Muted.Contains(key) ? [.. _prefs.Muted.Where(p => p != key)] : [.. _prefs.Muted, key] };
        _prefs.Save();
        Raise();
    }

    /// <summary>Mark as unread (a dot until opened) / Mark as read.</summary>
    public void ToggleUnread(ChatRowView row)
    {
        ConvMenu = null;
        if (row.Unread > 0 || row.Marked)
        {
            _prefs = _prefs.Unmark(row.Key);
            if (row.IsAdmin)
            {
                ReadChat();
            }
        }
        else
        {
            _prefs = _prefs with { Marked = [.. _prefs.Marked, row.Key] };
        }
        _prefs.Save();
        Raise();
    }

    public TeamConversation? OpenConversation => ChatKeys.Parse(ChatOpen) is ("conv", var id) ? Conversations.FirstOrDefault(c => c.Id == id) : null;

    /// <summary>The header of an open team thread: its name and the line under it.</summary>
    public (string Title, string Line) ThreadHeader
    {
        get
        {
            if (OpenConversation is { } c)
            {
                if (c.IsGroup)
                {
                    var names = (c.Members ?? []).Where(m => m.MemberId != MeId).Select(m => First(m.Name)).Where(n => n.Length > 0).ToList();
                    return (c.Name, c.Announce ? $"Only {OwnerFirst} posts here" : names.Count == 0 ? "Group" : names.Count > 3
                        ? string.Join(", ", names.Take(3)) + " and " + (names.Count - 3 + 1) + " more" : TeamTaskEngine.List([.. names, "you"]));
                }
                var p = c.WithMemberId is { } w ? _chat?.People?.FirstOrDefault(x => x.MemberId == w) : null;
                return (c.Name, p?.Status ?? p?.Role ?? "Teammate");
            }
            if (ChatKeys.Parse(ChatOpen) is ("direct", var member) && _chat?.People?.FirstOrDefault(x => x.MemberId == member) is { } person)
            {
                return (person.Name, person.Status ?? person.Role ?? "Teammate");
            }
            return ("Chat", "");
        }
    }

    /// <summary>The open team thread's lines (oldest first), with the member's queued ones at the end ("Sending…").</summary>
    public IReadOnlyList<TeamLine> TeamThread
    {
        get
        {
            var key = ChatOpen;
            var lines = new List<TeamLine>();
            var me = MeId;
            if (_chat?.Thread is { } thread && thread.Key == key)
            {
                foreach (var m in thread.Messages ?? [])
                {
                    var mine = m.Mine ?? (me is not null && m.ByMemberId == me && !m.ByOwner);
                    var who = mine ? "You" : m.ByOwner ? OwnerFirstStart : First(m.ByName);
                    lines.Add(new TeamLine(m, mine, false, who, TeamTaskEngine.Initials(m.ByOwner ? Status?.OwnerName ?? m.ByName : m.ByName)));
                }
            }
            if (key is not null)
            {
                foreach (var p in PendingChat(key))
                {
                    if (lines.Any(l => l.Mine && l.Message.Body == p.Body && l.Message.At >= p.CreatedAtUtc.AddMinutes(-2)))
                    {
                        continue;
                    }
                    lines.Add(new TeamLine(new TeamMessage(0, Body: p.Body, At: p.CreatedAtUtc, ReplyToId: p.MessageId, Mine: true), true, true, "You", ""));
                }
            }
            return lines;
        }
    }

    /// <summary>Whether the member may post in the open thread, and what the composer says instead when not.</summary>
    public (bool CanPost, string Note) ThreadPosting =>
        OpenConversation is { } c
            ? c.Announce ? (false, $"Only {OwnerFirst} posts here. You can read it.") : c.ReadOnly ? (false, "You can read this chat. You can’t post here any more.") : (true, "")
            : (true, "");

    public void StartTeamReply(TeamMessage m)
    {
        TeamReplyTo = m;
        Raise();
        FocusRequested?.Invoke("team-composer");
    }

    public void CancelTeamReply()
    {
        TeamReplyTo = null;
        Raise();
    }

    /// <summary>Send a line in the open team thread (offline: it waits in the outbox and goes when back).</summary>
    public bool SendTeam(string raw)
    {
        var body = (raw ?? "").Trim();
        if (body.Length == 0 || ChatOpen is not { } key || !ThreadPosting.CanPost)
        {
            return false;
        }
        QueueChat(OutboxReply.ChatSend, key, body, TeamReplyTo?.Id is > 0 ? TeamReplyTo.Id : null);
        TeamReplyTo = null;
        Raise();
        _ = RefreshChatAsync();
        return true;
    }

    /// <summary>"Reply to this task…" on a card: a line in that task's chat.</summary>
    public void ReplyOnCard(TaskItem item, string text)
    {
        if (item.Own || string.IsNullOrWhiteSpace(text))
        {
            return;
        }
        SendTaskChat(item, text);
        ShowSnack("Sent to the task chat.", [("Open", () => OpenPanel(item.Key, "chat")), ("OK", CloseSnack)]);
    }

    public void OpenReport(string key, TeamMessage? message)
    {
        ConvMenu = null;
        var who = message is not null ? First(message.ByName)
            : Conversations.FirstOrDefault(c => c.Key == key) is { IsGroup: false } c ? First(c.Name) : "";
        Report = (key, message, who.Length > 0 ? who : "this person");
        Raise();
    }

    public void CloseReport()
    {
        Report = null;
        Raise();
    }

    /// <summary>The hint on the report card (prototype wording): who gets what, and that nothing else goes.</summary>
    public string ReportHint => Report is not { } r ? ""
        : (r.Message is not null ? $"{OwnerFirstStart}, your admin, gets this message and your note."
            : $"{OwnerFirstStart}, your admin, gets your last 5 messages with {r.Who} and your note.")
          + $" Nothing else from your chats. {r.Who} is told a report was made.";

    public void SendReport(string note)
    {
        if (Report is not { } r)
        {
            return;
        }
        var key = ChatKeys.Parse(r.Key) is ("direct", var member) && Conversations.FirstOrDefault(c => !c.IsGroup && c.WithMemberId == member) is { } conv
            ? conv.Key : r.Key;
        QueueChat(OutboxReply.ChatReport, key, string.IsNullOrWhiteSpace(note) ? "Reported from the Zinwe app" : note.Trim(), r.Message?.Id);
        Report = null;
        ShowSnack($"Reported to {OwnerFirst}. Only " + (r.Message is not null ? "that message" : $"your last 5 messages with {r.Who}") + " went with it.",
            [("OK", CloseSnack)]);
    }

    private void QueueChat(string action, string key, string body, long? messageId = null, long? memberId = null)
    {
        if (Status?.ChatVersion is null)
        {
            ShowSnack("Zinwe isn’t running, so this can’t be sent yet. Try again in a moment.", [("OK", CloseSnack)]);
            return;
        }
        try
        {
            _outbox.AppendChat(action, key, body, messageId, memberId);
        }
        catch (Exception)
        {
            ShowSnack("That didn’t save. Try again.", [("OK", CloseSnack)]);
        }
    }

    private IReadOnlyList<OutboxReply> PendingChat(string key)
    {
        try
        {
            return [.. _outbox.PendingChat().Where(i => i.Action == OutboxReply.ChatSend && i.ChatKey == key)];
        }
        catch (Exception)
        {
            return [];
        }
    }

    /// <summary>Tests and previews: the service's answers without a service (status, tasks, chat).</summary>
    internal void UseForPreview(AgentStatus? status, TasksReply? tasks, ChatReply? chat, TodosReply? todos = null)
    {
        Status = status;
        StatusKnown = true;
        _remote = tasks;
        _remoteVersion = tasks?.Version;
        _chat = chat;
        _todos = todos;
        _todosVersion = todos?.Version;
    }

    // ---- Break limit (work_rules) ----------------------------------------------------------------------------

    /// <summary>"You've used 1 h 10 min of your 1 h 15 min break time today.", or null.</summary>
    public string? BreakNote => WorkHoursOn ? BreakLimit.Note(Status?.WorkRules, Timer.LiveBreak(Now)) : null;
}

/// <summary>
/// The member's own chat list choices, kept on this PC (chat-prefs.json): up to 3 pins, muted chats, chats marked
/// unread. The device API has no endpoint for them, so they never leave the PC.
/// </summary>
public sealed record ChatPrefs(List<string> Pins, List<string> Muted, List<string> Marked)
{
    private static string PathOf() => System.IO.Path.Combine(WidgetPaths.Directory, "chat-prefs.json");

    public ChatPrefs Unmark(string key) => Marked.Contains(key) ? Save(this with { Marked = [.. Marked.Where(m => m != key)] }) : this;

    public static ChatPrefs Load()
    {
        try
        {
            var path = PathOf();
            if (File.Exists(path) && JsonSerializer.Deserialize<ChatPrefs>(File.ReadAllText(path)) is { } p)
            {
                return new ChatPrefs(p.Pins ?? [], p.Muted ?? [], p.Marked ?? []);
            }
        }
        catch
        {
            // unreadable: start clean
        }
        return new ChatPrefs([], [], []);
    }

    public void Save() => Save(this);

    private static ChatPrefs Save(ChatPrefs p)
    {
        try
        {
            var path = PathOf();
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(p));
        }
        catch
        {
            // best effort
        }
        return p;
    }
}

using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows.Threading;
using Zinwe.Agent.Core;
using Zinwe.Agent.Core.Ipc;
using Zinwe.Agent.Core.Messages;
using Zinwe.Agent.Core.Policy;
using Zinwe.Agent.Core.Work;
using Zinwe.Agent.Tray.Resources;
using Zinwe.Agent.Tray.Widget;

namespace Zinwe.Agent.Tray;

/// <summary>Which full-window view shows (before the tabbed app, or instead of it).</summary>
public enum Stage { NotRunning, Connect, Consent, Welcome, Removed, App }

public enum Tab { Home, Chat, Tasks, Me }

/// <summary>Screens pushed over a tab, with a Back arrow.</summary>
public enum Push { None, Time, Privacy, Settings, Notifications, Help }

/// <summary>Bottom sheets.</summary>
public enum Sheet { None, Break, Out, Ask, Cant, Note, NewTask, Photo, Feedback }

/// <summary>The dark bar at the bottom ("Done: …  Add a note · Undo").</summary>
public sealed record Snack(string Text, IReadOnlyList<(string Label, Action Run)> Actions, DateTimeOffset Born);

/// <summary>The "Add a task for yourself" / "Edit your task" form.</summary>
public sealed record NewTaskForm(string Title = "", string Due = "today", DateOnly? Date = null, IReadOnlyList<string>? Steps = null, int? EditId = null)
{
    public IReadOnlyList<string> StepList => Steps ?? [];
}

/// <summary>
/// Everything the tray app v2 shows and does, in one place: the service's status (named pipe), consent, the work
/// day, the chat, tasks (seam), the notification history and settings. Lives as long as the tray (not the window),
/// so reminders and the unread dot keep working while Zinwe is minimized. Views render from it on
/// <see cref="Changed"/> and call its methods; it never touches WPF elements.
/// </summary>
public sealed class TrayModel : IDisposable
{
    private readonly NotificationStore _noteStore;
    private readonly NotificationWatcher _watcher;
    private readonly Func<Task<string?>> _startUpdate;
    private readonly WorkTimerStore _timerStore = new();
    private readonly ConsentStore _consent = new();
    private readonly MessageOutbox _outbox = new();
    private readonly TraySettingsStore _settingsStore = new();
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private readonly DateTimeOffset _startedAt = DateTimeOffset.Now;
    private int _secondsSinceSave;

    // Connect: when the member last pressed Connect, and the status message showing just before.
    private DateTimeOffset _connectSubmittedAt = DateTimeOffset.MinValue;
    private string? _messageBeforeSubmit;

    // End day is committed (saved + logged for the agent) when its Undo goes away, so Undo leaves no trace.
    private (string Before, DateTimeOffset At)? _pendingEnd;

    public TrayModel(NotificationStore noteStore, NotificationWatcher watcher, Func<Task<string?>> startUpdate)
    {
        _noteStore = noteStore;
        _watcher = watcher;
        _startUpdate = startUpdate;
        Tasks = new LocalTaskSource(TraySeams.UseSamples);
        Settings = _settingsStore.Load();
        Timer = _timerStore.Load(DateTimeOffset.Now);
        Notes = _noteStore.Load();
        Log = DayLog.Read();
        Profile = MemberProfile.Load();
        History.DropBefore(DateTime.Today);
        if (Tasks.IsSample && !History.Local.Any(e => e.Go.StartsWith("task:", StringComparison.Ordinal)))
        {
            // The sample owner "sent" the new sample tasks: their notices feed the Tasks badge like real ones will.
            foreach (var t in Tasks.Load().Where(t => t.IsNew))
            {
                History.Add("New task from your admin", $"{t.Title} · due {t.Due}", "task:" + t.Id, t.SentAt);
            }
        }
        HighContrastTheme.SetDark(Settings.Dark);

        _watcher.Refreshed += file =>
        {
            Notes = file;
            Raise();
        };
        Tasks.Changed += Raise;
        _tick.Tick += (_, _) => OnTick();
        _statusTimer.Tick += async (_, _) => await RefreshStatusAsync();
    }

    public void Start()
    {
        _tick.Start();
        _statusTimer.Start();
        _ = RefreshStatusAsync();
    }

    /// <summary>Something visible changed: re-render.</summary>
    public event Action? Changed;

    /// <summary>Every second (clock text only).</summary>
    public event Action? Ticked;

    /// <summary>A notice to pop up as a Windows toast (the tray decides whether the window is in front).</summary>
    public event Action<NoticeEntry, string>? Toast;

    /// <summary>The window should come to the front (a toast's action, a reminder).</summary>
    public event Action? OpenRequested;

    /// <summary>A view should move keyboard focus (e.g. "composer" after Reply).</summary>
    public event Action<string>? FocusRequested;

    // ---- State -----------------------------------------------------------------

    public AgentStatus? Status { get; private set; }
    public bool StatusKnown { get; private set; }
    public WorkTimerState Timer { get; private set; }
    public NotificationFile Notes { get; private set; }
    public IReadOnlyList<DayLog.Entry> Log { get; private set; }
    public TraySettings Settings { get; private set; }
    public MemberProfile Profile { get; private set; }
    public ITaskSource Tasks { get; }
    public NoticeHistory History { get; } = new();
    public ReplyQuotes Quotes { get; } = new();

    public Tab Tab { get; private set; } = Tab.Home;
    public Push Push { get; private set; } = Push.None;
    public Sheet Sheet { get; private set; } = Sheet.None;
    public bool WorkMenuOpen { get; private set; }
    public bool Welcome { get; private set; }
    public bool Reconnecting { get; private set; }
    public string PrivacyTab { get; private set; } = "pc";
    public bool ConfirmWithdraw { get; private set; }

    public string ConnectMessage { get; private set; } = "";
    public bool ConnectError { get; private set; }
    public bool ConnectBusy { get; private set; }
    public string? UpdateRefused { get; private set; }

    // Work sheet
    public string? Reason { get; private set; }
    public string ReasonNote { get; private set; } = "";

    // Tasks
    public TaskFilter Filter { get; private set; } = TaskFilter.None;
    public int? OpenCard { get; private set; }
    public int? FocusId { get; private set; }
    public int? SelTask { get; private set; }
    public string TaskReason { get; private set; } = "";
    public NewTaskForm Form { get; private set; } = new();

    // Chat
    public Quote? ReplyTo { get; private set; }
    public string Draft { get; set; } = "";
    public string? PhotoStatus { get; private set; }
    public string? PhotoError { get; private set; }

    public Snack? Snack { get; private set; }

    public DateTimeOffset Now => DateTimeOffset.Now;

    public ConsentState Consent => _consent.State(Status?.NoticeVersion);
    public ConsentRecord? ConsentRecord => _consent.Load();
    public bool WorkHoursOn => PlanModules.Allows(Status?.Entitlements, PlanModules.WorkTime);
    public bool ChatOn => PlanModules.Allows(Status?.Entitlements, PlanModules.TeamChat);
    public bool Offline => Status?.State == AgentState.Offline;
    public string OwnerName => string.IsNullOrWhiteSpace(Status?.OwnerName) ? "your admin" : Status.OwnerName.Trim();
    /// <summary>The owner's full name, or "Your admin" (labels, sentence starts).</summary>
    public string OwnerNameStart => string.IsNullOrWhiteSpace(Status?.OwnerName) ? "Your admin" : Status.OwnerName.Trim();
    public string OwnerFirst => Avatars.First(Status?.OwnerName) is { Length: > 0 } f ? f : "your admin";
    /// <summary>"Ananya" for a sentence start ("Ananya can see it"), "Your admin" when the name isn't known.</summary>
    public string OwnerFirstStart => Avatars.First(Status?.OwnerName) is { Length: > 0 } f ? f : "Your admin";
    public string Company => ConsentText.Employer(Status?.CompanyName);
    public string CompanyStart => ConsentText.Employer(Status?.CompanyName, sentenceStart: true);
    public IReadOnlyList<MemberTask> TaskList => Tasks.Load();
    public IReadOnlyList<OutboxReply> PendingReplies => _outbox.Pending();
    public UpdateView Update => UpdateView.From(Status);

    public Stage Stage
    {
        get
        {
            var layout = WidgetLayout.From(Status, Consent);
            return layout.Screen switch
            {
                WidgetScreen.None => Stage.NotRunning,
                WidgetScreen.Connect => layout.Removed && !Reconnecting ? Stage.Removed : Stage.Connect,
                WidgetScreen.Consent => Stage.Consent,
                _ => Welcome ? Stage.Welcome : Stage.App,
            };
        }
    }

    public bool NotRunning => Status is null && StatusKnown;

    /// <summary>Notification history, newest first: the tray's own notices + chat messages + protection notices.</summary>
    public IReadOnlyList<NoticeEntry> NoticeList => History.Merge(Notes.Items, OwnerName);

    public UnreadCounts Unread => UnreadCounts.Of(NoticeList);

    public bool Paused => Settings.Paused(Now);

    // ---- The clock -----------------------------------------------------------------

    private void OnTick()
    {
        var now = Now;
        if (Timer.RollOverIfNewDay(now))
        {
            _timerStore.Save(Timer);
            Log = DayLog.Read();
            History.DropBefore(DateTime.Today);
            Raise();
        }
        if (++_secondsSinceSave >= 30)
        {
            _secondsSinceSave = 0;
            if (_pendingEnd is null)
            {
                _timerStore.Save(Timer); // a crash loses at most ~30 s of the running segment
            }
            CheckReminders();
        }
        if (Snack is { } snack && now - snack.Born > TimeSpan.FromSeconds(9))
        {
            CloseSnack();
        }
        Ticked?.Invoke();
    }

    private void CheckReminders()
    {
        if (Stage != Stage.App)
        {
            return;
        }
        var due = Reminders.Due(Settings, Consent == ConsentState.Given, WorkHoursOn, Timer, TaskList, Now, Now - _startedAt);
        if (due.Count == 0)
        {
            return;
        }
        SaveSettings(Reminders.Remember(Settings, due, Now));
        foreach (var r in due)
        {
            Notify(r.Title, r.Body, r.Go, r.Action);
        }
    }

    /// <summary>A notice: always a line in the history, a toast while Zinwe is minimized (unless paused / switched off).</summary>
    public NoticeEntry Notify(string title, string body, string go, string action = "Open", string? key = null)
    {
        var entry = History.Add(title, body, go, Now, key);
        if (Settings.NotifyOn && !Paused)
        {
            Toast?.Invoke(entry, action);
        }
        Raise();
        return entry;
    }

    /// <summary>True when the tray may pop a toast now (Settings › Pop-up notifications, Pause for 1 hour).</summary>
    public bool ToastsAllowed => Settings.NotifyOn && !Paused;

    public void Raise() => Changed?.Invoke();

    // ---- Status ---------------------------------------------------------------------

    public async Task RefreshStatusAsync()
    {
        Status = await AgentPipeClient.GetStatusAsync(TimeSpan.FromSeconds(3));
        StatusKnown = true;
        if (Status?.Removed == true)
        {
            _consent.Clear(); // removed from this company: a new one asks afresh
        }
        _statusTimer.Interval = TimeSpan.FromSeconds(Update.Busy ? 1 : 5);
        if (Update.Show is false)
        {
            UpdateRefused = null;
        }
        UpdateConnectMessage();
        Raise();
    }

    // ---- Navigation -----------------------------------------------------------------

    public void Go(Tab tab)
    {
        Tab = tab;
        Push = Push.None;
        Sheet = Sheet.None;
        WorkMenuOpen = false;
        if (tab == Tab.Chat)
        {
            ReadChat();
        }
        Raise();
    }

    public void Open(Push push)
    {
        Push = push;
        Sheet = Sheet.None;
        WorkMenuOpen = false;
        if (push == Push.Privacy)
        {
            PrivacyTab = "pc";
            ConfirmWithdraw = false;
        }
        Raise();
    }

    public void Back()
    {
        Push = Push.None;
        Sheet = Sheet.None;
        Raise();
    }

    public void SetWorkMenu(bool open)
    {
        WorkMenuOpen = open;
        Raise();
    }

    /// <summary>Esc: close the menu, the sheet, the pushed screen; returns false when there was nothing to close.</summary>
    public bool Escape()
    {
        if (WorkMenuOpen)
        {
            SetWorkMenu(false);
            return true;
        }
        if (Sheet != Sheet.None)
        {
            CloseSheet();
            return true;
        }
        if (Push != Push.None)
        {
            Back();
            return true;
        }
        return false;
    }

    /// <summary>Where a notice or reminder leads (CompNotify table): messages → Chat, tasks → Tasks, the rest → Home.</summary>
    public void Route(string go)
    {
        switch (go)
        {
            case "chat":
                Go(Tab.Chat);
                break;
            case "startday":
                Go(Tab.Home);
                if (Timer.CanStart)
                {
                    StartDay();
                }
                break;
            case "endbreak":
                Go(Tab.Home);
                if (Timer.State == WorkState.OnBreak)
                {
                    EndBreak();
                }
                break;
            case "endday":
                Go(Tab.Home);
                if (Timer.State == WorkState.Working)
                {
                    AskOut();
                }
                break;
            case "notifications":
                Go(Tab.Me);
                Open(Push.Notifications);
                break;
            default:
                if (go.StartsWith("task:", StringComparison.Ordinal) && int.TryParse(go[5..], out var id))
                {
                    OpenTask(id);
                }
                else if (go != "open")
                {
                    Go(Tab.Home);
                }
                break;
        }
        OpenRequested?.Invoke();
    }

    public void OpenNotice(NoticeEntry entry)
    {
        MarkRead(entry);
        Route(entry.Go);
    }

    private void MarkRead(NoticeEntry entry)
    {
        if (!entry.FromFile)
        {
            History.MarkRead(e => e.Id == entry.Id);
        }
        else if (entry.Go == "chat")
        {
            Notes = _noteStore.MarkChatRead();
        }
        else
        {
            Notes = _noteStore.MarkNoticesRead();
        }
    }

    public void MarkAllRead()
    {
        History.MarkRead(_ => true);
        _noteStore.MarkChatRead();
        Notes = _noteStore.MarkNoticesRead();
        Raise();
    }

    /// <summary>Opening Chat (or the window on Chat) reads the owner's messages; the agent tells the backend.</summary>
    public void ReadChat()
    {
        if (ChatFeed.UnreadCount(Notes.Items) > 0)
        {
            Notes = _noteStore.MarkChatRead();
        }
        History.MarkRead(e => e.Go == "chat");
    }

    /// <summary>The window came to the front.</summary>
    public void OnShown()
    {
        if (Tab == Tab.Chat && Push == Push.None)
        {
            ReadChat();
        }
        Raise();
    }

    /// <summary>The window was tucked away: close what was open, keep the day safe.</summary>
    public void OnHidden()
    {
        Sheet = Sheet.None;
        WorkMenuOpen = false;
        CommitPendingEnd();
        _timerStore.Save(Timer);
    }

    // ---- Connect --------------------------------------------------------------------

    public async Task ConnectAsync(string emailText, string memberIdText, Action<string> tidyMemberId)
    {
        var (email, memberId, problem) = Zinwe.Agent.Core.Backend.MemberIdInput.Validate(emailText, memberIdText);
        if (problem is not null)
        {
            SetConnectMessage(problem, error: true);
            return;
        }
        tidyMemberId(memberId);
        _messageBeforeSubmit = Status?.Message;
        _connectSubmittedAt = DateTimeOffset.UtcNow;
        ConnectBusy = true;
        SetConnectMessage(Strings.Connecting, error: false);

        var ack = await AgentPipeClient.SubmitEnrollmentAsync(email, memberId, TimeSpan.FromSeconds(5));
        if (ack is null || !ack.Accepted)
        {
            ConnectBusy = false;
            SetConnectMessage(ack?.Message ?? Strings.ConnectNotRunning, error: true);
            return;
        }
        Profile = Profile with { Email = email };
        Profile.Save();
        Reconnecting = false;
        await RefreshStatusAsync();
    }

    public void ClearConnectError()
    {
        if (!ConnectBusy && ConnectMessage.Length > 0)
        {
            SetConnectMessage("", error: false);
        }
    }

    private void SetConnectMessage(string text, bool error)
    {
        ConnectMessage = text;
        ConnectError = error;
        Raise();
    }

    /// <summary>The Connect screen's message for the status (the same rules as the v1 widget).</summary>
    private void UpdateConnectMessage()
    {
        if (Status is not { } status)
        {
            return;
        }
        var sinceSubmit = DateTimeOffset.UtcNow - _connectSubmittedAt;
        switch (status.State)
        {
            case AgentState.Enrolling:
                ConnectBusy = true;
                (ConnectMessage, ConnectError) = (sinceSubmit > TimeSpan.FromSeconds(25) ? Strings.ConnectStillTrying : Strings.Connecting, false);
                break;
            case AgentState.Rejected:
                ConnectBusy = false;
                if (sinceSubmit < TimeSpan.FromSeconds(3) && status.Message == _messageBeforeSubmit)
                {
                    (ConnectMessage, ConnectError) = (Strings.Connecting, false);
                }
                else if (status.Removed && _connectSubmittedAt == DateTimeOffset.MinValue)
                {
                    (ConnectMessage, ConnectError) = ("", false);
                }
                else
                {
                    (ConnectMessage, ConnectError) = (status.Message ?? Strings.ConnectRefusedFallback, true);
                }
                break;
            case AgentState.NotEnrolled:
                ConnectBusy = false;
                if (status.Message is not null && sinceSubmit >= TimeSpan.FromSeconds(1) && _connectSubmittedAt != DateTimeOffset.MinValue)
                {
                    (ConnectMessage, ConnectError) = (status.Message, true);
                }
                else if (sinceSubmit < TimeSpan.FromSeconds(6))
                {
                    (ConnectMessage, ConnectError) = (Strings.Connecting, false);
                }
                break;
            default:
                ConnectBusy = false;
                break;
        }
    }

    public void Reconnect()
    {
        Reconnecting = true;
        _connectSubmittedAt = DateTimeOffset.MinValue;
        ConnectMessage = "";
        Raise();
    }

    // ---- Consent ------------------------------------------------------------------------

    /// <summary>What the notice lists: after a decline, everything that can be on; else exactly what is on now.</summary>
    public MonitoringScope NoticeScope =>
        Consent == ConsentState.Declined || string.Equals(Status?.ServerConsentState, "declined", StringComparison.OrdinalIgnoreCase)
            ? MonitoringScope.Everything
            : Status?.Monitoring ?? MonitoringScope.Everything;

    public ConsentNoticeView Notice => ConsentText.Build(NoticeScope, Status?.CompanyName, Status?.OwnerName, ChatOn);

    public void Agree()
    {
        var firstAnswer = Stage == Stage.Consent;
        _consent.Accept(ConsentNotice.VersionFor(NoticeScope), Now);
        ConfirmWithdraw = false;
        Welcome = firstAnswer || Welcome;
        AnsweredConsent();
    }

    public void Decline()
    {
        _consent.Decline(Status?.NoticeVersion ?? ConsentNotice.FullVersion, Now);
        Welcome = true;
        AnsweredConsent();
    }

    /// <summary>Agree from Me › Policies & terms (no welcome screen).</summary>
    public void AgreeNow()
    {
        _consent.Accept(ConsentNotice.VersionFor(NoticeScope), Now);
        AnsweredConsent();
    }

    public void AskWithdraw(bool confirm)
    {
        ConfirmWithdraw = confirm;
        Raise();
    }

    public void Withdraw()
    {
        _consent.Decline(Status?.NoticeVersion ?? ConsentNotice.FullVersion, Now);
        ConfirmWithdraw = false;
        Raise();
    }

    /// <summary>"Not now": Zinwe minimizes; a sticky notice asks again.</summary>
    public void ConsentNotNow()
    {
        Notify("Zinwe has a question for you", "Please read what Zinwe records on this PC, then choose I agree or I don’t agree.",
            "open", "Open Zinwe", key: "consent");
    }

    private void AnsweredConsent()
    {
        History.MarkRead(e => e.Key == "consent");
        Raise();
    }

    public void StartApp()
    {
        Welcome = false;
        Go(Tab.Home);
    }

    public void SetPrivacyTab(string tab)
    {
        PrivacyTab = tab;
        Raise();
    }

    // ---- The work day ---------------------------------------------------------------------

    public bool HadLunch => DayLog.Today(Log, Now).Any(e => e.Label.Contains("Lunch", StringComparison.Ordinal));

    public IReadOnlyList<TimelineEvent> Timeline => DayLog.Today(Log, Now);

    public int OpenTasks => TaskList.Count(t => !TaskBoard.Finished(t));

    public void StartDay()
    {
        CommitPendingEnd();
        Transition((t, now) => t.Start(now), "start");
    }

    public void AskBreak()
    {
        OpenWorkSheet(Sheet.Break, WorkPrompt.Break.Likely(Now, HadLunch));
    }

    public void AskOut()
    {
        OpenWorkSheet(Sheet.Out, WorkPrompt.LogOut.Likely(Now, HadLunch));
    }

    private void OpenWorkSheet(Sheet sheet, string? likely)
    {
        Sheet = sheet;
        Reason = likely;
        ReasonNote = "";
        WorkMenuOpen = false;
        Raise();
        OpenRequested?.Invoke();
    }

    public WorkPrompt? Prompt => Sheet switch { Sheet.Break => WorkPrompt.Break, Sheet.Out => WorkPrompt.LogOut, _ => null };

    public void PickReason(string reason)
    {
        Reason = reason;
        Raise();
    }

    public void SetReasonNote(string note) => ReasonNote = note;

    public string? ReasonMissing => Prompt?.Missing(Reason, ReasonNote);

    public void ConfirmReason()
    {
        if (Prompt is not { } prompt || prompt.Missing(Reason, ReasonNote) is not null)
        {
            return;
        }
        var (reason, note) = (Reason, ReasonNote);
        Sheet = Sheet.None;
        if (prompt == WorkPrompt.Break)
        {
            Transition((t, now) => t.ToggleBreak(now, reason, note), "break");
            return;
        }
        // End day: shown at once, committed when the Undo goes away.
        CommitPendingEnd();
        var now = Now;
        var before = JsonSerializer.Serialize(Timer);
        Timer.RollOverIfNewDay(now);
        if (!Timer.End(now, reason, note))
        {
            Raise();
            return;
        }
        var worked = DayLog.HoursMinutes(Timer.LiveWorked(now));
        ShowSnack($"Day ended at {now.ToLocalTime():h:mm tt} · {worked} worked.", [("Undo", UndoEndDay)]);
        _pendingEnd = (before, now); // after ShowSnack, which commits any earlier pending end
        Raise();
    }

    private void UndoEndDay()
    {
        if (_pendingEnd is { } pending && JsonSerializer.Deserialize<WorkTimerState>(pending.Before) is { } before)
        {
            Timer = before;
            _pendingEnd = null;
        }
        Snack = null;
        Raise();
    }

    private void CommitPendingEnd()
    {
        if (_pendingEnd is not { } pending)
        {
            return;
        }
        _pendingEnd = null;
        _timerStore.Save(Timer);
        _timerStore.AppendLog("end", Timer, pending.At);
        Log = DayLog.Read();
    }

    public void EndBreak() => Transition((t, now) => t.ToggleBreak(now), "resume");

    // One timestamp for the change: the state's LastTransitionAtUtc and the log line the agent sends must match exactly.
    private void Transition(Func<WorkTimerState, DateTimeOffset, bool> action, string logAction)
    {
        CommitPendingEnd();
        var now = Now;
        Timer.RollOverIfNewDay(now);
        WorkMenuOpen = false;
        if (action(Timer, now))
        {
            _timerStore.Save(Timer);
            _timerStore.AppendLog(logAction, Timer, now);
            Log = DayLog.Read();
        }
        Raise();
    }

    /// <summary>What's running now: the break while on one, else the time worked today.</summary>
    public TimeSpan Running => WorkSummary.Hero(Timer, Now);

    public TimeSpan Worked => Timer.LiveWorked(Now);

    public IReadOnlyList<DayHours> Week => DayLog.LastWorkingDays(Log, Now, Worked);

    // ---- Tasks -------------------------------------------------------------------------------

    public MemberTask? Selected => SelTask is { } id ? TaskList.FirstOrDefault(t => t.Id == id) : null;

    public void OpenTask(int id)
    {
        Filter = TaskFilter.None;
        OpenCard = id;
        ReadTask(id);
        Go(Tab.Tasks);
    }

    public void ToggleCard(int id)
    {
        OpenCard = OpenCard == id ? null : id;
        ReadTask(id);
        Raise();
    }

    /// <summary>Opening a task reads its notices too, so the Tasks badge and the history agree.</summary>
    private void ReadTask(int id)
    {
        History.MarkRead(e => e.Go == "task:" + id);
        if (TaskList.FirstOrDefault(t => t.Id == id) is { IsNew: true } t)
        {
            Tasks.Save(t with { IsNew = false });
        }
    }

    public void SetFilter(TaskFilter filter)
    {
        Filter = filter;
        Raise();
    }

    private void Change(MemberTask t, Func<MemberTask, MemberTask> fn)
    {
        var updated = fn(t) with { IsNew = false, Queued = Offline || t.Queued };
        Tasks.Save(updated);
        History.MarkRead(e => e.Go == "task:" + t.Id);
        Sheet = Sheet.None;
        TaskReason = "";
    }

    public void StartTask(MemberTask t)
    {
        Change(t, TaskBoard.Start);
        FocusId = t.Id;
        Raise();
    }

    public void DoneTask(MemberTask t)
    {
        var before = t;
        Change(t, TaskBoard.MarkDone);
        if (FocusId == t.Id)
        {
            FocusId = null;
        }
        var text = (t.NeedsOk ? $"Sent to {OwnerFirst} to check: " : "Done: ") + t.Title;
        ShowSnack(text,
        [
            ("Add a note", () => OpenNote(t.Id)),
            ("Undo", () =>
            {
                Tasks.Save(before);
                FocusId = before.Status == MemberTaskStatus.Progress ? before.Id : FocusId;
                Snack = null;
                Raise();
            }),
        ]);
    }

    public void ReopenTask(MemberTask t)
    {
        Change(t, TaskBoard.Reopen);
        Raise();
    }

    public void ToggleStep(MemberTask t, int index)
    {
        var updated = TaskBoard.ToggleStep(t, index) with { Queued = Offline || t.Queued };
        Tasks.Save(updated);
        if (t.Status is MemberTaskStatus.New or MemberTaskStatus.Confirmed)
        {
            FocusId = t.Id;
        }
        if (TaskBoard.AllStepsDone(updated) && !TaskBoard.Finished(updated) && updated.Status != MemberTaskStatus.Review)
        {
            ShowSnack($"All steps ticked. Mark “{t.Title}” done?",
                [("Mark done", () => DoneTask(updated)), ("Not yet", CloseSnack)]);
            return;
        }
        Raise();
    }

    public void CarryOn(MemberTask t)
    {
        Change(t, TaskBoard.CarryOn);
        FocusId = t.Id;
        Raise();
    }

    public void AskAbout(MemberTask t)
    {
        SelTask = t.Id;
        TaskReason = "";
        Snack = null;
        Sheet = Sheet.Ask;
        Raise();
    }

    public void PickTaskReason(string reason)
    {
        TaskReason = TaskReason == reason ? "" : reason;
        Raise();
    }

    public void ToCant()
    {
        TaskReason = "";
        Sheet = Sheet.Cant;
        Raise();
    }

    public void BackToAsk()
    {
        TaskReason = "";
        Sheet = Sheet.Ask;
        Raise();
    }

    public void SendAsk(string note)
    {
        if (Selected is not { } t)
        {
            return;
        }
        var q = TaskBoard.Join(TaskReason, note);
        Change(t, x => TaskBoard.Ask(x, TaskReason, note));
        SendTaskMessage($"About “{t.Title}”: {q}");
        Raise();
    }

    public void SendCant(string note)
    {
        if (Selected is not { } t)
        {
            return;
        }
        var q = TaskBoard.Join(TaskReason, note);
        Change(t, x => TaskBoard.Cant(x, TaskReason, note));
        if (FocusId == t.Id)
        {
            FocusId = null;
        }
        SendTaskMessage($"I can’t do “{t.Title}”: {q}");
        ShowSnack($"Sent to {OwnerFirst}. They’ll reply in Chat.", [("OK", CloseSnack)]);
    }

    /// <summary>
    /// A task question goes to the owner as a chat line. With the sample tasks nothing is sent (the owner has no such
    /// task), so the real chat never carries made-up tasks.
    /// </summary>
    private void SendTaskMessage(string text)
    {
        if (!Tasks.IsSample)
        {
            TryAppendOutbox(text);
        }
    }

    public void OpenNote(int id)
    {
        SelTask = id;
        Snack = null;
        Sheet = Sheet.Note;
        Raise();
    }

    public void SaveNote(string text)
    {
        if (Selected is not { } t || string.IsNullOrWhiteSpace(text))
        {
            return;
        }
        Tasks.Save(TaskBoard.AddNote(t, text));
        Sheet = Sheet.None;
        ShowSnack($"Note saved on “{t.Title}”.", [("OK", CloseSnack)]);
    }

    public void OpenNewTask(MemberTask? edit = null)
    {
        Form = edit is null
            ? new NewTaskForm()
            : new NewTaskForm(edit.Title, edit.DueDate is not null ? "date" : edit.DueKey switch
            {
                DueKey.Tomorrow => "tomorrow",
                DueKey.Later => "later",
                _ => "today",
            }, edit.DueDate, [.. edit.Steps.Select(s => s.Label)], edit.Id);
        Snack = null;
        Sheet = Sheet.NewTask;
        Raise();
    }

    /// <summary>Typing in the form: keep it without re-rendering (the text box already shows it).</summary>
    public void SetFormQuiet(NewTaskForm form) => Form = form;

    public void SetForm(NewTaskForm form)
    {
        Form = form;
        Raise();
    }

    /// <summary>"Add task" / "Save changes". Returns false when the title or date is missing.</summary>
    public bool SaveForm()
    {
        var f = Form;
        var today = DateOnly.FromDateTime(Now.LocalDateTime);
        if (string.IsNullOrWhiteSpace(f.Title) || (f.Due == "date" && (f.Date is null || f.Date < today)))
        {
            return false;
        }
        var (due, key, date) = f.Due switch
        {
            "tomorrow" => ("Tomorrow", DueKey.Tomorrow, (DateOnly?)null),
            "later" => (NextFriday(today).ToString("ddd d MMM"), DueKey.Later, NextFriday(today)),
            "date" => DateDue(f.Date!.Value, today),
            _ => ("Today", DueKey.Today, (DateOnly?)null),
        };
        var title = f.Title.Trim();
        if (f.EditId is { } id && TaskList.FirstOrDefault(t => t.Id == id) is { } old)
        {
            Tasks.Save(old with
            {
                Title = title, Due = due, DueKey = key, DueDate = date, Queued = Offline || old.Queued,
                Steps = [.. f.StepList.Select(l => new TaskStep(l, old.Steps.FirstOrDefault(s => s.Label == l)?.Done ?? false))],
            });
            OpenCard = id;
            Sheet = Sheet.None;
            ShowSnack("Saved: " + title, [("OK", CloseSnack)]);
            return true;
        }
        var next = Tasks.NextId();
        Tasks.Save(new MemberTask
        {
            Id = next, Title = title, Due = due, DueKey = key, DueDate = date, Mine = true, Status = MemberTaskStatus.New,
            Queued = Offline, SentAt = Now, Steps = [.. f.StepList.Select(l => new TaskStep(l, false))],
        });
        Filter = TaskFilter.None;
        OpenCard = next;
        Sheet = Sheet.None;
        ShowSnack("Added: " + title, [("OK", CloseSnack)]);
        return true;
    }

    private static DateOnly NextFriday(DateOnly today)
    {
        var add = ((int)DayOfWeek.Friday - (int)today.DayOfWeek + 7) % 7;
        return today.AddDays(add == 0 ? 7 : add);
    }

    private static (string, DueKey, DateOnly?) DateDue(DateOnly date, DateOnly today)
    {
        var days = date.DayNumber - today.DayNumber;
        return days switch
        {
            0 => ("Today", DueKey.Today, date),
            1 => ("Tomorrow", DueKey.Tomorrow, date),
            _ => (date.ToString("ddd d MMM"), DueKey.Later, date),
        };
    }

    public void DeleteOwn(MemberTask t)
    {
        var at = TaskList.ToList().FindIndex(x => x.Id == t.Id);
        Tasks.Remove(t.Id);
        OpenCard = null;
        ShowSnack("Deleted: " + t.Title, [("Undo", () =>
        {
            Tasks.Insert(at, t);
            Snack = null;
            Raise();
        })]);
    }

    /// <summary>Home's "Needs you": new tasks, unread messages, tasks due today (without the focus card's task).</summary>
    public IReadOnlyList<(string Kind, string Title, string Sub, Action Run)> NeedsYou
    {
        get
        {
            var list = new List<(string, string, string, Action)>();
            var tasks = TaskList;
            var (focus, _) = TaskBoard.Focus(tasks, FocusId, Now);
            foreach (var t in tasks.Where(t => t.IsNew && t.Id != focus?.Id))
            {
                var id = t.Id;
                list.Add(("task", "New task: " + t.Title, $"From {OwnerFirst} · due {t.Due}", () => OpenTask(id)));
            }
            var unreadChat = ChatFeed.UnreadCount(Notes.Items);
            if (unreadChat > 0)
            {
                var last = Notes.Items.Where(i => i.IsChat && i.IsFromAdmin).MaxBy(i => i.CreatedAtUtc);
                list.Add(("chat", $"{unreadChat} new message{(unreadChat == 1 ? "" : "s")} from {OwnerFirst}",
                    last is null ? "" : ChatFeed.Preview(last, OwnerFirst), () => Go(Tab.Chat)));
            }
            foreach (var t in tasks.Where(t => t.Id != focus?.Id && !t.IsNew && !TaskBoard.Finished(t)
                && t.Status != MemberTaskStatus.Review && t.DueKey is DueKey.Today or DueKey.Overdue))
            {
                var id = t.Id;
                list.Add(("due", t.Title, $"Due {t.Due} · {TaskBoard.LaneLabel(t, OwnerFirst)}", () => OpenTask(id)));
            }
            return list.Take(5).ToList();
        }
    }

    // ---- Chat -------------------------------------------------------------------------------

    public IReadOnlyList<ThreadRow> Thread =>
        ChatThread.Build(Notes.Items, PendingReplies, TaskList, Quotes.Items, OwnerFirstStart, Now, Offline);

    public void StartReply(ThreadRow row)
    {
        ReplyTo = new Quote(row.Mine ? "me" : "them", row.Snippet, row.Key);
        Raise();
        FocusRequested?.Invoke("composer");
    }

    public void CancelReply()
    {
        ReplyTo = null;
        Raise();
    }

    public string? SendError { get; private set; }

    public bool Send(string raw)
    {
        var text = (raw ?? "").Trim();
        if (text.Length == 0)
        {
            return false;
        }
        if (!TryAppendOutbox(text))
        {
            SendError = "Couldn’t send right now. Please try again.";
            Raise();
            return false;
        }
        if (ReplyTo is { } quote)
        {
            Quotes.Add(text, quote with { Who = quote.Who == "me" ? "You" : OwnerNameStart }, DateTimeOffset.UtcNow);
        }
        ReplyTo = null;
        Draft = "";
        SendError = null;
        Raise();
        return true;
    }

    private bool TryAppendOutbox(string text)
    {
        try
        {
            _outbox.Append(text);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    // ---- Me, settings, photo, feedback ---------------------------------------------------------

    public void SaveSettings(TraySettings settings)
    {
        Settings = settings;
        _settingsStore.Save(settings);
        HighContrastTheme.SetDark(settings.Dark);
        Raise();
    }

    public void TogglePause() =>
        SaveSettings(Settings with { PausedUntil = Paused ? null : Now.AddHours(1) });

    public void OpenSheet(Sheet sheet)
    {
        Sheet = sheet;
        Snack = null;
        PhotoError = null;
        PhotoStatus = null;
        WorkMenuOpen = false;
        Raise();
    }

    public void CloseSheet()
    {
        Sheet = Sheet.None;
        Raise();
    }

    public void PickAvatar(string? avatar)
    {
        PhotoError = null;
        PhotoStatus = null;
        SaveSettings(Settings with { Avatar = avatar });
    }

    /// <summary>SEAM 4 · Profile photo: shrunk to 256 × 256 px and kept on this PC (avatar.jpg); not uploaded yet.</summary>
    public void UsePhoto(string file)
    {
        try
        {
            var ext = Path.GetExtension(file).ToLowerInvariant();
            if (ext is not (".jpg" or ".jpeg" or ".png" or ".webp"))
            {
                PhotoError = "That file isn’t a JPG, PNG or WebP photo. Try another one.";
                Raise();
                return;
            }
            var before = new FileInfo(file).Length;
            var after = PhotoShrink.To256(file, Path.Combine(WidgetPaths.Directory, "avatar.jpg"));
            PhotoError = null;
            PhotoStatus = $"New photo saved · {Size(before)} → {Size(after)}";
            SaveSettings(Settings with { Avatar = "photo:" + DateTimeOffset.UtcNow.ToUnixTimeSeconds() });
        }
        catch (Exception)
        {
            PhotoError = "That photo couldn’t be opened. Try another one.";
            Raise();
        }

        static string Size(long b) => b >= 1_048_576 ? $"{b / 1_048_576.0:0.0} MB" : $"{Math.Max(1, b / 1024)} KB";
    }

    public static string AvatarFile => Path.Combine(WidgetPaths.Directory, "avatar.jpg");

    // ---- Update ---------------------------------------------------------------------------------

    public async Task StartUpdateAsync()
    {
        UpdateRefused = await _startUpdate();
        await RefreshStatusAsync();
    }

    public static void OpenLink(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception)
        {
            // No browser / blocked: nothing else to do.
        }
    }

    // ---- Snack ------------------------------------------------------------------------------------

    public void ShowSnack(string text, IReadOnlyList<(string, Action)> actions)
    {
        CommitIfSnackIsEndDay();
        Snack = new Snack(text, actions, Now);
        Raise();
    }

    public void CloseSnack()
    {
        CommitIfSnackIsEndDay();
        Snack = null;
        Raise();
    }

    private void CommitIfSnackIsEndDay()
    {
        if (_pendingEnd is not null)
        {
            CommitPendingEnd();
        }
    }

    public void Dispose()
    {
        _tick.Stop();
        _statusTimer.Stop();
        CommitPendingEnd();
        _timerStore.Save(Timer);
    }
}

/// <summary>
/// SEAM 3 · Who the member is. The pipe status has no member name yet, so the tray keeps the email typed on Connect
/// (profile.json) and shows a name made from it ("emily.carter@…" → "Emily Carter"). Later: the member's name on the
/// status (an optional trailing AgentStatus field, so older trays keep reading it).
/// </summary>
public sealed record MemberProfile(string? Email = null)
{
    private static string FilePath => Path.Combine(WidgetPaths.Directory, "profile.json");

    public string DisplayName
    {
        get
        {
            var local = (Email ?? "").Split('@')[0];
            var parts = local.Split(['.', '_', '-'], StringSplitOptions.RemoveEmptyEntries);
            return parts.Length == 0 ? "You" : string.Join(' ', parts.Select(p => char.ToUpperInvariant(p[0]) + p[1..]));
        }
    }

    public static MemberProfile Load()
    {
        try
        {
            return File.Exists(FilePath) ? JsonSerializer.Deserialize<MemberProfile>(File.ReadAllText(FilePath)) ?? new() : new();
        }
        catch
        {
            return new();
        }
    }

    public void Save()
    {
        try
        {
            WidgetPaths.EnsureDirectory();
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this));
        }
        catch
        {
            // best effort
        }
    }
}

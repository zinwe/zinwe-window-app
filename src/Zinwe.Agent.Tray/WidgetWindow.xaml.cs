using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Zinwe.Agent.Core;
using Zinwe.Agent.Core.Ipc;
using Zinwe.Agent.Core.Policy;
using Zinwe.Agent.Core.Work;
using Zinwe.Agent.Tray.Resources;
using Zinwe.Agent.Tray.Widget;
using Forms = System.Windows.Forms;

namespace Zinwe.Agent.Tray;

/// <summary>
/// The member's Zinwe in one compact view: who the PC is connected to (always visible), their day (log in / take a
/// break / log out with today's time), then the chat with their admin (<see cref="ChatPanel"/>) and notes from
/// Zinwe's own protection. Before that: Connect (email + Member ID), then the consent notice with a real choice
/// ("I agree" / "I don't agree"). "What Zinwe records on this PC" re-opens the notice any time (and withdraws).
/// The window always fits the screen it is on. Minimizing hides it; the agent keeps running.
/// </summary>
public partial class WidgetWindow : Window
{
    private enum ConsentMode
    {
        /// <summary>Not answered yet: I agree / I don't agree.</summary>
        Decide,
        /// <summary>Declined earlier, now reading the full notice to agree: I agree / Not now.</summary>
        AgreeLater,
        /// <summary>Agreed or declined, re-reading what is recorded.</summary>
        Review,
        /// <summary>"Withdraw my agreement" pressed: confirm.</summary>
        WithdrawConfirm,
    }

    private readonly WorkTimerStore _timerStore = new();
    private readonly ConsentStore _consent = new();
    private readonly NotificationStore _noteStore;
    private readonly NotificationWatcher _watcher;
    // "Update now": asks the service to install the offered release; returns why it couldn't, or null.
    private readonly Func<Task<string?>> _startUpdate;
    private string? _updateRefused;

    private WorkTimerState _timer;

    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromSeconds(5) };

    private int _secondsSinceSave;
    private int _lastSpokenMinute = -1;

    private AgentStatus? _status;
    private bool _statusKnown;
    private string? _updateDownloadUrl;

    // The consent notice re-opened from "What Zinwe records" (null = not open).
    private ConsentMode? _review;
    private bool _welcome;
    private WidgetScreen? _lastScreen;

    // Connect: when the member last pressed Connect, and the status message showing just before.
    private DateTimeOffset _connectSubmittedAt = DateTimeOffset.MinValue;
    private string? _messageBeforeSubmit;

    // The open input view (break / log out), or null.
    private WorkPrompt? _prompt;
    private string? _promptReason;

    /// <summary>Set by the tray on real shutdown so Closing stops hiding and actually closes.</summary>
    public bool AllowClose { get; set; }

    /// <summary>The member is looking at the open chat (the tray skips toasts for chat lines while this is true).</summary>
    public bool IsReading => IsVisible && IsActive && WindowState != WindowState.Minimized
        && ConnectedContent.Visibility == Visibility.Visible && Chat.IsOpen;

    public WidgetWindow(NotificationStore noteStore, NotificationWatcher watcher, Func<Task<string?>> startUpdate)
    {
        _noteStore = noteStore;
        _watcher = watcher;
        _startUpdate = startUpdate;
        InitializeComponent();
        ApplyStaticText();
        // Never taller than the screen (refined to the actual monitor once shown).
        MaxHeight = WindowFit.MaxHeight(SystemParameters.WorkArea.Height);

        _timer = _timerStore.Load(DateTimeOffset.Now);
        Logo.ToolTip = $"Zinwe {AgentInfo.Version}";

        _tick.Tick += (_, _) => OnTick();
        _statusTimer.Tick += async (_, _) => await RefreshStatusAsync();

        _watcher.Refreshed += OnNotificationsRefreshed;
        Activated += (_, _) =>
        {
            var file = _noteStore.Load();
            MarkNoticesReadIfShown(file);
            Chat.Refresh(); // coming back to the window reads the admin's new messages
        };

        // Whenever the window grows (Connect → consent, the chat opening), keep all of it on the screen.
        SizeChanged += (_, _) => KeepOnScreen();

        Loaded += async (_, _) =>
        {
            Chat.Attach(_noteStore, _watcher, () => IsReading);
            KeepOnScreen();
            RenderTimer();
            RenderNotifications(_watcher.Poll());
            await RefreshStatusAsync();
            _tick.Start();
            _statusTimer.Start();
        };

        Closing += OnClosing;
    }

    /// <summary>The XAML labels that come from Strings.resx (so another language needs no XAML change).</summary>
    private void ApplyStaticText()
    {
        ConnectIntro.Text = Strings.ConnectIntro;
        EmailLabel.Text = Strings.ConnectEmailLabel;
        EmailPlaceholder.Text = Strings.ConnectEmailPlaceholder;
        MemberIdLabel.Text = Strings.ConnectIdLabel;
        MemberIdPlaceholder.Text = Strings.ConnectIdPlaceholder;
        ConnectHint.Text = Strings.ConnectHint;
        ConnectButton.Content = Strings.ConnectButton;
        AutomationProperties.SetHelpText(EmailInput, Strings.ConnectEmailPlaceholder);
        AutomationProperties.SetHelpText(MemberIdInput, Strings.ConnectHint);
        NotRunningTitle.Text = Strings.NotRunningTitle;
        NotRunningRetry.Content = Strings.NotRunningRetry;
        DeclinedTitle.Text = Strings.DeclinedBannerTitle;
        DeclinedBody.Text = Strings.DeclinedBannerBody;
        DeclinedAgreeButton.Content = Strings.DeclinedBannerButton;
        WelcomeTitle.Text = Strings.WelcomeTitle;
        WelcomeDismiss.Content = Strings.WelcomeDismiss;
        RemovedBody.Text = Strings.RemovedBody;
        RecordsButton.Content = Strings.RecordsLink;
    }

    // ---- Work: log in / break / log out --------------------------------------

    private void OnLogIn(object sender, RoutedEventArgs e) => Transition((t, now) => t.Start(now), "start");

    private void OnBreak(object sender, RoutedEventArgs e)
    {
        // "Take a break" while working; "I'm back" while on a break.
        if (_timer.State == WorkState.OnBreak)
        {
            Transition((t, now) => t.ToggleBreak(now), "resume");
            return;
        }
        OpenPrompt(WorkPrompt.Break);
    }

    private void OnLogOut(object sender, RoutedEventArgs e) => OpenPrompt(WorkPrompt.LogOut);

    // One timestamp for the change: the state's LastTransitionAtUtc and the change-log line the agent sends must
    // match exactly (the backend recognises a change it already has by its time).
    private void Transition(Func<WorkTimerState, DateTimeOffset, bool> action, string logAction)
    {
        var now = DateTimeOffset.Now;
        _timer.RollOverIfNewDay(now);
        if (!action(_timer, now))
        {
            return;
        }
        _timerStore.Save(_timer);
        _timerStore.AppendLog(logAction, _timer, now);
        RenderTimer();
    }

    // ---- The one input view (break / log out) ---------------------------------

    private void OpenPrompt(WorkPrompt prompt)
    {
        _prompt = prompt;
        _promptReason = null;
        PromptTitle.Text = prompt.Title;
        PromptSubtitle.Text = prompt.Subtitle;
        PromptConfirm.Content = prompt.ConfirmLabel;
        PromptNote.Text = "";

        ReasonChips.Children.Clear();
        foreach (var reason in prompt.Reasons)
        {
            var chip = new RadioButton
            {
                Content = reason,
                GroupName = "reason",
                Style = (Style)FindResource("Zinwe.Chip"),
            };
            chip.Checked += (_, _) =>
            {
                _promptReason = reason;
                UpdatePrompt();
                if (_prompt?.NoteRequired(reason) == true)
                {
                    PromptNote.Focus(); // "Other": go straight to the words
                }
            };
            ReasonChips.Children.Add(chip);
        }

        ActionsPanel.Visibility = Visibility.Collapsed;
        PromptPanel.Visibility = Visibility.Visible;
        UpdatePrompt();
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () => (ReasonChips.Children[0] as UIElement)?.Focus());
    }

    private void ClosePrompt()
    {
        _prompt = null;
        _promptReason = null;
        PromptPanel.Visibility = Visibility.Collapsed;
        ActionsPanel.Visibility = Visibility.Visible;
    }

    private void UpdatePrompt()
    {
        if (_prompt is null)
        {
            return;
        }
        PromptNotePlaceholder.Text = _prompt.NotePlaceholder(_promptReason);
        PromptNotePlaceholder.Visibility = PromptNote.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        var missing = _prompt.Missing(_promptReason, PromptNote.Text);
        PromptConfirm.IsEnabled = missing is null;
        PromptHint.Text = missing ?? (_prompt == WorkPrompt.LogOut
            ? "Your time stops when you log out. You can log in again any time today."
            : "Your work time pauses until you tap I'm back.");
    }

    private void OnPromptNoteChanged(object sender, TextChangedEventArgs e) => UpdatePrompt();

    private void OnPromptCancel(object sender, RoutedEventArgs e) => ClosePrompt();

    private void OnPromptKeyDown(object sender, KeyEventArgs e)
    {
        // Ctrl+Enter confirms from the note box (plain Enter adds a line).
        if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Control) != 0 && PromptConfirm.IsEnabled)
        {
            e.Handled = true;
            OnPromptConfirm(sender, e);
        }
    }

    private void OnPromptConfirm(object sender, RoutedEventArgs e)
    {
        if (_prompt is null || _prompt.Missing(_promptReason, PromptNote.Text) is not null)
        {
            return;
        }
        var (reason, note) = (_promptReason, PromptNote.Text);
        if (_prompt == WorkPrompt.Break)
        {
            Transition((t, now) => t.ToggleBreak(now, reason, note), "break");
        }
        else
        {
            Transition((t, now) => t.End(now, reason, note), "end");
        }
        ClosePrompt();
        RenderTimer();
    }

    // ---- Rendering the day ------------------------------------------------------

    private void OnTick()
    {
        var now = DateTimeOffset.Now;
        if (_timer.RollOverIfNewDay(now))
        {
            _timerStore.Save(_timer);
        }
        RenderTimer();

        // Checkpoint periodically so a crash loses at most ~30s of the running segment.
        if (++_secondsSinceSave >= 30)
        {
            _secondsSinceSave = 0;
            _timerStore.Save(_timer);
        }
    }

    private void RenderTimer()
    {
        var now = DateTimeOffset.Now;
        Greeting.Text = WorkSummary.Greeting(now);
        // The big number is whatever is running now: the break while on one, else time worked today.
        var hero = WorkSummary.Hero(_timer, now);
        WorkedTime.Text = WorkSummary.Clock(hero);
        WorkedTime.Foreground = (Brush)FindResource(_timer.State == WorkState.OnBreak ? "Zinwe.Warn" : "Zinwe.Ink");
        // Screen readers: "Work time today: 2 h 5 min" (updated once a minute, not every second).
        if ((int)hero.TotalMinutes != _lastSpokenMinute)
        {
            _lastSpokenMinute = (int)hero.TotalMinutes;
            AutomationProperties.SetName(WorkedTime, Strings.Format(
                _timer.State == WorkState.OnBreak ? Strings.BreakTimeNow : Strings.WorkTimeToday, SpokenDuration(hero)));
        }
        StatusLine.Text = WorkSummary.Status(_timer);

        var secondary = WorkSummary.Secondary(_timer, now);
        BreakLine.Text = secondary ?? "";
        BreakLine.Visibility = secondary is null ? Visibility.Collapsed : Visibility.Visible;

        WorkPillText.Text = WorkSummary.Pill(_timer.State);
        var (pillBg, pillFg) = _timer.State switch
        {
            WorkState.Working => ("Zinwe.OkTint", "Zinwe.Ok"),
            WorkState.OnBreak => ("Zinwe.WarnTint", "Zinwe.Warn"),
            _ => ("Zinwe.Paper", "Zinwe.Muted"),
        };
        WorkPill.Background = (Brush)FindResource(pillBg);
        WorkPillText.Foreground = (Brush)FindResource(pillFg);

        // Only the buttons that make sense right now. They keep working while an update is pending or the PC
        // is offline: the times are saved locally and sent later, so the work clock is never trapped.
        var clockedIn = _timer.State is WorkState.Working or WorkState.OnBreak;
        LogInButton.Visibility = clockedIn ? Visibility.Collapsed : Visibility.Visible;
        WorkingActions.Visibility = clockedIn ? Visibility.Visible : Visibility.Collapsed;
        LogInButton.Content = _timer.State == WorkState.Ended ? "Log in again" : "Log in";

        var onBreak = _timer.State == WorkState.OnBreak;
        BreakButton.Content = onBreak ? "I'm back" : "Take a break";
        BreakButton.Style = (Style)FindResource(onBreak ? "Zinwe.PrimaryButton" : "Zinwe.OutlineButton");

        // A prompt that no longer applies (day rolled over) closes itself.
        if (_prompt is not null && (!clockedIn || (_prompt == WorkPrompt.Break && onBreak)))
        {
            ClosePrompt();
        }
    }

    private static string SpokenDuration(TimeSpan t) => t.TotalHours >= 1
        ? Strings.Format(Strings.DurationHoursMinutes, (int)t.TotalHours, t.Minutes)
        : Strings.Format(Strings.DurationMinutes, (int)t.TotalMinutes);

    // ---- Messages + notices -----------------------------------------------------

    private void OnNotificationsRefreshed(NotificationFile file) => RenderNotifications(file);

    private void RenderNotifications(NotificationFile file)
    {
        // Notes from Zinwe's own protection (the chat renders itself): shown only when there are some.
        var notices = file.Items.Where(i => !i.IsChat).ToList();
        NoticeList.ItemsSource = notices.Select(NotificationRow.From).ToList();
        NoticesCard.Visibility = notices.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        MarkNoticesReadIfShown(file);
    }

    /// <summary>Notices count as read once the employee has the widget in front of them.</summary>
    private void MarkNoticesReadIfShown(NotificationFile file)
    {
        if (IsVisible && IsActive && file.Items.Any(i => !i.IsChat && !i.Read))
        {
            _noteStore.MarkNoticesRead();
        }
    }

    /// <summary>"Open chat" (tray menu, a message toast): the chat is right here, so go to its end and the composer.</summary>
    public void FocusChat() => Chat.Activate();

    private void OnClear(object sender, RoutedEventArgs e) => RenderNotifications(_noteStore.ClearNotices());

    // ---- Fitting the screen -----------------------------------------------------

    /// <summary>The work area (DIPs) of the monitor the widget is on; the primary one before it is shown.</summary>
    private Rect WorkArea()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle != IntPtr.Zero && PresentationSource.FromVisual(this)?.CompositionTarget is { } target)
        {
            var area = Forms.Screen.FromHandle(handle).WorkingArea;
            var toDips = target.TransformFromDevice;
            return new Rect(toDips.Transform(new Point(area.Left, area.Top)), toDips.Transform(new Point(area.Right, area.Bottom)));
        }
        return SystemParameters.WorkArea;
    }

    /// <summary>Cap the height to the screen and move the window up if its bottom went off it.</summary>
    private void KeepOnScreen() => Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
    {
        var area = WorkArea();
        MaxHeight = WindowFit.MaxHeight(area.Height);
        FitChat();
        var top = WindowFit.Top(Top, ActualHeight, area.Top, area.Bottom);
        if (!double.IsNaN(top) && Math.Abs(top - Top) > 0.5)
        {
            Top = top;
        }
    });

    // Title bar + borders + the root margin: everything around the content (measured once, on the small first view).
    private double _chrome = double.NaN;

    /// <summary>On a short screen the open chat's conversation gets shorter, so the composer stays visible.</summary>
    private void FitChat()
    {
        if (Content is not FrameworkElement root || root.ActualHeight <= 0)
        {
            return;
        }
        if (double.IsNaN(_chrome))
        {
            _chrome = Math.Max(0, ActualHeight - root.ActualHeight);
        }
        if (!Chat.IsOpen || ConnectedContent.Visibility != Visibility.Visible)
        {
            return;
        }
        root.Measure(new Size(root.ActualWidth, double.PositiveInfinity));
        var overflow = root.DesiredSize.Height + _chrome - MaxHeight;
        var height = Math.Clamp(Chat.ConversationHeight - overflow, ChatPanel.MinConversationHeight, ChatPanel.MaxConversationHeight);
        if (Math.Abs(height - Chat.ConversationHeight) > 1)
        {
            Chat.ConversationHeight = height;
        }
    }

    /// <summary>A different view (Connect → consent → the day): centre it again on its screen.</summary>
    private void Recenter() => Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
    {
        var area = WorkArea();
        MaxHeight = WindowFit.MaxHeight(area.Height);
        Top = WindowFit.CenteredTop(ActualHeight, area.Top, area.Bottom);
    });

    // ---- Status → which view -----------------------------------------------------

    private async Task RefreshStatusAsync()
    {
        _status = await AgentPipeClient.GetStatusAsync(TimeSpan.FromSeconds(3));
        _statusKnown = true;
        Render();
    }

    private async void OnRetryStatus(object sender, RoutedEventArgs e)
    {
        NotRunningRetry.IsEnabled = false;
        await RefreshStatusAsync();
        NotRunningRetry.IsEnabled = true;
    }

    private void Render()
    {
        var status = _status;
        if (status?.Removed == true)
        {
            _consent.Clear(); // removed from this company: a new one asks afresh
            _review = null;
        }
        var consent = _consent.State(status?.NoticeVersion);
        var layout = WidgetLayout.From(status, consent);
        var screen = layout.Screen;
        if (screen == WidgetScreen.Consent)
        {
            _review = null; // a pending answer always shows the choice itself
        }
        else if (_review is not null && screen == WidgetScreen.Main)
        {
            screen = WidgetScreen.Consent;
        }

        ConnectScroller.Visibility = screen == WidgetScreen.Connect ? Visibility.Visible : Visibility.Collapsed;
        ConsentPanel.Visibility = screen == WidgetScreen.Consent ? Visibility.Visible : Visibility.Collapsed;
        ConnectedContent.Visibility = screen == WidgetScreen.Main ? Visibility.Visible : Visibility.Collapsed;
        NotRunningPanel.Visibility = layout.NotRunning && _statusKnown ? Visibility.Visible : Visibility.Collapsed;
        NotRunningBody.Text = Strings.Format(Strings.NotRunningBody, ConsentText.Owner(status?.OwnerName));
        DeclinedPanel.Visibility = layout.DeclinedBanner && screen == WidgetScreen.Main ? Visibility.Visible : Visibility.Collapsed;
        WelcomePanel.Visibility = _welcome && screen == WidgetScreen.Main ? Visibility.Visible : Visibility.Collapsed;

        RenderStatusLine(status, consent);
        Chat.SetConnection(status);
        Chat.OwnerName = status?.OwnerName;
        RenderPlan(status?.Entitlements);
        _updateDownloadUrl = status?.DownloadUrl;
        RenderUpdate(status);
        RenderConnect(status, layout.Removed);
        if (screen == WidgetScreen.Consent)
        {
            RenderConsent(status, consent);
        }
        if (screen == WidgetScreen.Main)
        {
            RenderTimer();
        }

        if (_lastScreen is not null && _lastScreen != screen)
        {
            Recenter();
        }
        _lastScreen = screen;
    }

    private void RenderStatusLine(AgentStatus? status, ConsentState consent)
    {
        var line = StatusText.Line(status);
        if (WidgetLayout.IsEnrolled(status) && consent == ConsentState.Declined)
        {
            line += " · " + Strings.StatusMonitoringOff;
        }
        StatusLineText.Text = line;
        StatusDot.Fill = (Brush)FindResource(StatusText.Tone(status) switch
        {
            StatusTone.Ok => "Zinwe.Ok",
            StatusTone.Bad => "Zinwe.Bad",
            _ => "Zinwe.Warn",
        });
        var (_, detail, _) = StatusText.Describe(status);
        AutomationProperties.SetName(StatusChip, line);
        AutomationProperties.SetHelpText(StatusChip, detail);
        StatusChip.ToolTip = $"{detail}{Environment.NewLine}Zinwe {status?.AgentVersion ?? AgentInfo.Version}";
    }

    /// <summary>
    /// The company's plan decides what the member sees: no work hours → no log in / break / log out; no chat → no
    /// chat bar. With neither, one quiet "This PC is protected" card. Unknown plan (older backend) → everything.
    /// </summary>
    private void RenderPlan(IReadOnlyList<string>? entitlements)
    {
        var workOn = PlanModules.Allows(entitlements, PlanModules.WorkTime);
        var chatOn = PlanModules.Allows(entitlements, PlanModules.TeamChat);
        WorkCard.Visibility = workOn ? Visibility.Visible : Visibility.Collapsed;
        Chat.Visibility = chatOn ? Visibility.Visible : Visibility.Collapsed;
        ProtectedCard.Visibility = workOn || chatOn ? Visibility.Collapsed : Visibility.Visible;
        WelcomeBody.Text = workOn ? Strings.WelcomeBodyWork : Strings.WelcomeBodyNoWork;
        if (!workOn && _prompt is not null)
        {
            ClosePrompt();
        }
    }

    /// <summary>
    /// The update card: required (orange) or optional (quiet), with "Update now" and its progress. The work day
    /// carries on either way. Status is polled every second while an update runs, so progress feels live.
    /// </summary>
    private void RenderUpdate(AgentStatus? status)
    {
        var view = UpdateView.From(status);
        UpdatePanel.Visibility = view.Show ? Visibility.Visible : Visibility.Collapsed;
        _statusTimer.Interval = TimeSpan.FromSeconds(view.Busy ? 1 : 5);
        if (!view.Show)
        {
            _updateRefused = null;
            return;
        }

        UpdatePanel.Background = (Brush)FindResource(view.Required ? "Zinwe.OrangeTint" : "Zinwe.White");
        UpdatePanel.BorderBrush = (Brush)FindResource(view.Required ? "Zinwe.OrangeStrong" : "Zinwe.Line");
        UpdateIcon.Background = (Brush)FindResource(view.Required ? "Zinwe.White" : "Zinwe.OrangeTint");
        UpdateTitle.Text = view.Title;
        UpdateBody.Text = view.Body;
        UpdateBody.Foreground = (Brush)FindResource(view.Required ? "Zinwe.OrangeInk" : "Zinwe.Muted");
        UpdateNotes.Text = view.Notes is null ? "" : Strings.Format(Strings.UpdateWhatsNew, view.Notes);
        UpdateNotes.Visibility = view.Notes is null ? Visibility.Collapsed : Visibility.Visible;

        UpdateButton.Content = view.Button;
        UpdateButton.Visibility = view.ButtonVisible ? Visibility.Visible : Visibility.Collapsed;
        UpdateButton.IsEnabled = view.ButtonEnabled;
        ManualDownloadButton.Content = Strings.UpdateManual;
        ManualDownloadButton.Visibility = view.ShowManual ? Visibility.Visible : Visibility.Collapsed;

        // The service's progress, or (before it took the request) why it refused.
        var progress = _updateRefused ?? view.Progress;
        UpdateProgress.Text = progress ?? "";
        UpdateProgress.Visibility = progress is null ? Visibility.Collapsed : Visibility.Visible;
        UpdateProgress.Foreground = (Brush)FindResource(view.ProgressIsError || _updateRefused is not null ? "Zinwe.Bad" : "Zinwe.Muted");
    }

    private async void OnUpdateNow(object sender, RoutedEventArgs e)
    {
        UpdateButton.IsEnabled = false;
        _updateRefused = await _startUpdate();
        await RefreshStatusAsync();
    }

    /// <summary>Open the update's download page in the default browser (only offered when there is one).</summary>
    private void OnDownloadUpdate(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_updateDownloadUrl))
        {
            return;
        }
        OpenLink(_updateDownloadUrl);
    }

    private static void OpenLink(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception)
        {
            // No browser / blocked: nothing else to do; the card's text already explains.
        }
    }

    // ---- Connect this PC ---------------------------------------------------

    private void RenderConnect(AgentStatus? status, bool removed)
    {
        RemovedPanel.Visibility = removed ? Visibility.Visible : Visibility.Collapsed;
        RemovedTitle.Text = Strings.Format(Strings.RemovedTitle, ConsentText.Employer(status?.CompanyName, sentenceStart: true));
        ConnectTitle.Text = removed ? Strings.RemovedConnectTitle : Strings.ConnectTitle;
        if (status is null)
        {
            return;
        }

        var sinceSubmit = DateTimeOffset.UtcNow - _connectSubmittedAt;
        switch (status.State)
        {
            case AgentState.Enrolling:
                ConnectButton.IsEnabled = false;
                ShowConnectMessage(sinceSubmit > TimeSpan.FromSeconds(25) ? Strings.ConnectStillTrying : Strings.Connecting, error: false);
                break;
            case AgentState.Rejected:
                ConnectButton.IsEnabled = true;
                if (sinceSubmit < TimeSpan.FromSeconds(3) && status.Message == _messageBeforeSubmit)
                {
                    // Still the answer to the previous try: keep "Connecting…" until this one's result arrives.
                    ShowConnectMessage(Strings.Connecting, error: false);
                }
                else if (removed && _connectSubmittedAt == DateTimeOffset.MinValue)
                {
                    ShowConnectMessage("", error: false); // the card above explains; nothing was typed yet
                }
                else
                {
                    ShowConnectMessage(status.Message ?? Strings.ConnectRefusedFallback, error: true);
                }
                break;
            case AgentState.NotEnrolled:
                ConnectButton.IsEnabled = true;
                if (status.Message is not null && sinceSubmit >= TimeSpan.FromSeconds(1))
                {
                    // Can't reach Zinwe / server busy: say so, and let the member try again (it also retries itself).
                    ShowConnectMessage(status.Message, error: true);
                }
                else if (sinceSubmit < TimeSpan.FromSeconds(6))
                {
                    ShowConnectMessage(Strings.Connecting, error: false);
                }
                break;
        }
    }

    private void OnConnectInputChanged(object sender, TextChangedEventArgs e)
    {
        EmailPlaceholder.Visibility = EmailInput.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        MemberIdPlaceholder.Visibility = MemberIdInput.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        // Fixing the input clears the old error, so it never looks like the new attempt failed too.
        if (ConnectButton.IsEnabled)
        {
            ShowConnectMessage("", error: false);
        }
    }

    private async void OnConnect(object sender, RoutedEventArgs e)
    {
        var (email, memberId, problem) = Zinwe.Agent.Core.Backend.MemberIdInput.Validate(EmailInput.Text, MemberIdInput.Text);
        if (problem is not null)
        {
            ShowConnectMessage(problem, error: true);
            (email.Length == 0 ? EmailInput : MemberIdInput).Focus();
            return;
        }
        MemberIdInput.Text = memberId; // show the tidied ID (ZM-XXXX-XXXX)

        _messageBeforeSubmit = _status?.Message;
        _connectSubmittedAt = DateTimeOffset.UtcNow;
        ConnectButton.IsEnabled = false;
        ShowConnectMessage(Strings.Connecting, error: false);

        var ack = await AgentPipeClient.SubmitEnrollmentAsync(email, memberId, TimeSpan.FromSeconds(5));
        if (ack is null)
        {
            ShowConnectMessage(Strings.ConnectNotRunning, error: true);
            ConnectButton.IsEnabled = true;
            return;
        }
        if (!ack.Accepted)
        {
            ShowConnectMessage(ack.Message, error: true);
            ConnectButton.IsEnabled = true;
            return;
        }
        // Accepted: the service is enrolling. The status poll flips us to the notice (or shows why it was refused).
        await RefreshStatusAsync();
    }

    private void ShowConnectMessage(string message, bool error)
    {
        ConnectMessage.Text = message;
        ConnectMessage.Foreground = (Brush)FindResource(error ? "Zinwe.Bad" : "Zinwe.Muted");
        ConnectMessage.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
    }

    // ---- Consent (DPDP): a real choice, re-readable, withdrawable -------------------

    /// <summary>"What Zinwe records on this PC" (tray menu, widget link): the notice, with the member's answer.</summary>
    public void ShowRecords()
    {
        if (WidgetLayout.IsEnrolled(_status) && _consent.State(_status?.NoticeVersion) != ConsentState.Pending)
        {
            _review = ConsentMode.Review;
            Render();
            ConsentPrimary.Focus();
        }
    }

    private void OnShowRecords(object sender, RoutedEventArgs e) => ShowRecords();

    private void OnReadAndAgree(object sender, RoutedEventArgs e)
    {
        _review = ConsentMode.AgreeLater;
        Render();
        ConsentPrimary.Focus();
    }

    private void OnWelcomeDismiss(object sender, RoutedEventArgs e)
    {
        _welcome = false;
        Render();
    }

    /// <summary>
    /// What the notice lists. After a decline the backend sends this PC a policy with everything off, so agreeing
    /// from there is to the full notice (everything that can be on); otherwise exactly what is switched on now.
    /// </summary>
    private MonitoringScope NoticeScope(ConsentState consent, ConsentMode mode) =>
        consent == ConsentState.Declined || mode == ConsentMode.AgreeLater
            || string.Equals(_status?.ServerConsentState, "declined", StringComparison.OrdinalIgnoreCase)
            ? MonitoringScope.Everything
            : _status?.Monitoring ?? MonitoringScope.Everything;

    private ConsentMode CurrentConsentMode(ConsentState consent) =>
        consent == ConsentState.Pending ? ConsentMode.Decide : _review ?? ConsentMode.Review;

    private void RenderConsent(AgentStatus? status, ConsentState consent)
    {
        var mode = CurrentConsentMode(consent);
        var chatOn = PlanModules.Allows(status?.Entitlements, PlanModules.TeamChat);
        var view = ConsentText.Build(NoticeScope(consent, mode), status?.CompanyName, status?.OwnerName, chatOn);
        ConsentTitle.Text = mode is ConsentMode.Review or ConsentMode.WithdrawConfirm ? Strings.RecordsTitle : view.Title;
        var reviewing = mode is ConsentMode.Review or ConsentMode.WithdrawConfirm;
        BuildConsentBody(view, reviewing ? view.IntroReview : view.Intro,
            showDecline: mode != ConsentMode.Review || consent != ConsentState.Declined);

        var record = _consent.Load();
        var employer = ConsentText.Employer(status?.CompanyName);
        (string footer, string primary, string secondary) = mode switch
        {
            ConsentMode.Decide => (Strings.ConsentDecideFooter, Strings.ConsentAgree, Strings.ConsentDecline),
            ConsentMode.AgreeLater => (Strings.ConsentDecideFooter, Strings.ConsentAgree, Strings.ConsentNotNow),
            ConsentMode.WithdrawConfirm => (Strings.Format(Strings.ConsentWithdrawConfirm, employer),
                Strings.ConsentWithdrawNo, Strings.ConsentWithdrawYes),
            _ when consent == ConsentState.Declined => (
                Strings.Format(Strings.ConsentDeclinedOn, ConsentText.When(record?.AcceptedAt ?? DateTimeOffset.Now)),
                Strings.ConsentAgreeNow, Strings.ConsentClose),
            _ => (Strings.Format(Strings.ConsentAgreedOn, ConsentText.When(record?.AcceptedAt ?? DateTimeOffset.Now)),
                Strings.ConsentClose, Strings.ConsentWithdraw),
        };
        ConsentFooterText.Text = footer;
        ConsentPrimary.Content = primary;
        ConsentSecondary.Content = secondary;
        AutomationProperties.SetHelpText(ConsentPrimary, footer);
    }

    private void BuildConsentBody(ConsentNoticeView view, string intro, bool showDecline)
    {
        ConsentBody.Children.Clear();
        Add(new TextBlock { Text = intro, Foreground = Muted, Margin = new Thickness(0, 0, 0, 2) });
        Heading(view.SeesHeading);
        Bullets(view.Sees);
        Add(new TextBlock { Text = view.SeesMore, Foreground = Muted, FontSize = 12, Margin = new Thickness(0, 4, 0, 0) });
        Heading(view.NeverHeading);
        Bullets(view.Never);
        if (showDecline)
        {
            Heading(view.DeclineHeading);
            Add(new TextBlock { Text = view.DeclineBody });
        }
        Add(new TextBlock { Text = view.SmallPrint, Foreground = Muted, FontSize = 12, Margin = new Thickness(0, 12, 0, 0) });

        var links = new WrapPanel { Margin = new Thickness(-10, 6, 0, 0) };
        links.Children.Add(LinkButton(Strings.ConsentPrivacyPolicy, "privacy"));
        links.Children.Add(LinkButton(Strings.ConsentAcceptableUse, "acceptable-use"));
        ConsentBody.Children.Add(links);
        ConsentScroller.ScrollToTop();

        void Heading(string text) => Add(new TextBlock
        {
            Text = text, FontSize = 13, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 14, 0, 4),
        });

        void Bullets(IEnumerable<string> items)
        {
            foreach (var item in items)
            {
                // A hanging bullet: wrapped lines line up with the text, not under the dot.
                var row = new Grid { Margin = new Thickness(0, 1, 0, 1) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
                row.ColumnDefinitions.Add(new ColumnDefinition());
                row.Children.Add(new TextBlock { Text = "•", FontSize = 12.5, LineHeight = 19 });
                var text = new TextBlock { Text = item, FontSize = 12.5, LineHeight = 19, TextWrapping = TextWrapping.Wrap };
                Grid.SetColumn(text, 1);
                row.Children.Add(text);
                ConsentBody.Children.Add(row);
            }
        }
    }

    private Brush Muted => (Brush)FindResource("Zinwe.Muted");

    private void Add(TextBlock block)
    {
        block.TextWrapping = TextWrapping.Wrap;
        if (block.ReadLocalValue(TextBlock.FontSizeProperty) == DependencyProperty.UnsetValue)
        {
            block.FontSize = 12.5;
        }
        block.LineHeight = 19;
        ConsentBody.Children.Add(block);
    }

    private Button LinkButton(string text, string slug)
    {
        var button = new Button { Content = text, Tag = slug, Style = (Style)FindResource("Zinwe.HeaderButton") };
        button.Click += OnOpenPolicy;
        return button;
    }

    private void OnConsentPrimary(object sender, RoutedEventArgs e)
    {
        var consent = _consent.State(_status?.NoticeVersion);
        var mode = CurrentConsentMode(consent);
        switch (mode)
        {
            case ConsentMode.Decide:
            case ConsentMode.AgreeLater:
            case ConsentMode.Review when consent == ConsentState.Declined:
                Agree(consent, mode);
                break;
            case ConsentMode.WithdrawConfirm:
                _review = ConsentMode.Review; // "Keep my agreement"
                break;
            default:
                _review = null; // "Close"
                break;
        }
        Render();
    }

    private void OnConsentSecondary(object sender, RoutedEventArgs e)
    {
        var consent = _consent.State(_status?.NoticeVersion);
        switch (CurrentConsentMode(consent))
        {
            case ConsentMode.Decide:
                // "I don't agree": nothing is recorded; the day and the chat stay available.
                _consent.Decline(_status?.NoticeVersion ?? ConsentNotice.FullVersion, DateTimeOffset.Now);
                _review = null;
                break;
            case ConsentMode.Review when consent == ConsentState.Given:
                _review = ConsentMode.WithdrawConfirm;
                break;
            case ConsentMode.WithdrawConfirm:
                _consent.Decline(_status?.NoticeVersion ?? ConsentNotice.FullVersion, DateTimeOffset.Now);
                _review = null;
                break;
            default:
                _review = null; // "Not now" / "Close"
                break;
        }
        Render();
    }

    private void Agree(ConsentState consent, ConsentMode mode)
    {
        // The version agreed to is the notice shown: the full one after a decline, else exactly what is on now.
        var version = ConsentNotice.VersionFor(NoticeScope(consent, mode));
        _consent.Accept(version, DateTimeOffset.Now);
        _review = null;
        _welcome = true;
    }

    private void OnOpenPolicy(object sender, RoutedEventArgs e)
    {
        var slug = (sender as FrameworkElement)?.Tag as string ?? "privacy";
        OpenLink($"https://zinwe.io/legal/{slug}");
    }

    // ---- Window lifetime ---------------------------------------------------

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (AllowClose)
        {
            _tick.Stop();
            _statusTimer.Stop();
            _watcher.Refreshed -= OnNotificationsRefreshed;
            Chat.Detach();
            _timerStore.Save(_timer);
            return;
        }
        // Employees can't quit the widget; the X just tucks it away. Protection keeps running.
        e.Cancel = true;
        HideToBackground();
    }

    private void HideToBackground()
    {
        _timerStore.Save(_timer);
        _review = null;
        Hide();
    }
}

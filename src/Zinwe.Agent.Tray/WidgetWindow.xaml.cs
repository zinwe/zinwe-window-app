using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Zinwe.Agent.Core;
using Zinwe.Agent.Core.Ipc;
using Zinwe.Agent.Core.Policy;
using Zinwe.Agent.Tray.Widget;

namespace Zinwe.Agent.Tray;

/// <summary>
/// The member's Zinwe in one compact view: their day (log in / take a break / log out with today's time) on top,
/// then the chat with their admin (<see cref="ChatPanel"/>: a bar that opens the conversation when clicked), and
/// notes from Zinwe's own protection as a slim strip when there are some. "Take a break" and "Log out" share one input view (<see cref="WorkPrompt"/>):
/// a required reason, plus a note. Minimizing hides it; the agent keeps running.
/// </summary>
public partial class WidgetWindow : Window
{
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

    // Forced upgrade (M12): when the backend requires a newer agent, work is paused and the
    // employee is prompted to install the update (from this link) until a newer agent reports in.
    private bool _updateRequired;
    private string? _updateDownloadUrl;

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

        Loaded += async (_, _) =>
        {
            Chat.Attach(_noteStore, _watcher, () => IsReading);
            Chat.OpenChanged += KeepOnScreen;
            RenderTimer();
            RenderNotifications(_watcher.Poll());
            await RefreshStatusAsync();
            _tick.Start();
            _statusTimer.Start();
        };

        Closing += OnClosing;
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
        WorkedTime.Text = WorkSummary.Clock(WorkSummary.Hero(_timer, now));
        WorkedTime.Foreground = (Brush)FindResource(_timer.State == WorkState.OnBreak ? "Zinwe.Warn" : "Zinwe.Ink");
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

        // Only the buttons that make sense right now.
        var clockedIn = _timer.State is WorkState.Working or WorkState.OnBreak;
        LogInButton.Visibility = clockedIn ? Visibility.Collapsed : Visibility.Visible;
        WorkingActions.Visibility = clockedIn ? Visibility.Visible : Visibility.Collapsed;
        LogInButton.Content = _timer.State == WorkState.Ended ? "Log in again" : "Log in";

        var onBreak = _timer.State == WorkState.OnBreak;
        BreakButton.Content = onBreak ? "I'm back" : "Take a break";
        BreakButton.Style = (Style)FindResource(onBreak ? "Zinwe.PrimaryButton" : "Zinwe.OutlineButton");

        // Work is paused while an update is required.
        LogInButton.IsEnabled = !_updateRequired;
        BreakButton.IsEnabled = !_updateRequired;
        LogOutButton.IsEnabled = !_updateRequired;

        // A prompt that no longer applies (day rolled over, update required) closes itself.
        if (_prompt is not null && (_updateRequired || !clockedIn || (_prompt == WorkPrompt.Break && onBreak)))
        {
            ClosePrompt();
        }
    }

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

    /// <summary>Opening the chat makes the window taller: nudge it up so its bottom stays on the screen.</summary>
    private void KeepOnScreen() => Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
    {
        var area = SystemParameters.WorkArea;
        if (Top + ActualHeight > area.Bottom)
        {
            Top = Math.Max(area.Top, area.Bottom - ActualHeight - 8);
        }
    });

    /// <summary>"Open chat" (tray menu, a message toast): the chat is right here, so go to its end and the composer.</summary>
    public void FocusChat() => Chat.Activate();

    private void OnClear(object sender, RoutedEventArgs e) => RenderNotifications(_noteStore.ClearNotices());

    // ---- Protection status (same source as the tray tooltip) ------------------

    private async Task RefreshStatusAsync()
    {
        var status = await AgentPipeClient.GetStatusAsync(TimeSpan.FromSeconds(3));
        // No status chip in the header: the logo's tooltip carries the protection status and version.
        var (headline, detail, _) = StatusText.Describe(status);
        Logo.ToolTip = $"{headline}. {detail}{Environment.NewLine}Zinwe {status?.AgentVersion ?? AgentInfo.Version}";

        RenderConnectState(status);
    }

    /// <summary>Show the Connect form until the PC is enrolled; show the normal widget once it is.</summary>
    private void RenderConnectState(AgentStatus? status)
    {
        var state = status?.State;
        // An enrolled PC (connected, offline, or update-required) shows the normal widget body.
        var enrolled = state is AgentState.Connected or AgentState.Offline or AgentState.UpdateRequired;
        var needsConnect = state is AgentState.NotEnrolled or AgentState.Rejected or AgentState.Enrolling;

        // A connected PC asks for the member's consent first (once per policy version).
        var needsConsent = enrolled && !_consent.HasConsented();
        ConsentPanel.Visibility = needsConsent ? Visibility.Visible : Visibility.Collapsed;
        ConnectedContent.Visibility = enrolled && !needsConsent ? Visibility.Visible : Visibility.Collapsed;
        ConnectPanel.Visibility = needsConnect ? Visibility.Visible : Visibility.Collapsed;
        SetupScroller.Visibility = needsConnect || needsConsent ? Visibility.Visible : Visibility.Collapsed;
        Chat.SetConnection(status);
        RenderPlan(status?.Entitlements);

        _updateRequired = state == AgentState.UpdateRequired;
        _updateDownloadUrl = status?.DownloadUrl;
        RenderUpdate(status);
        // Re-gate the work buttons now that the pause flag may have changed.
        if (enrolled)
        {
            RenderTimer();
        }

        switch (state)
        {
            case AgentState.Enrolling:
                ConnectButton.IsEnabled = false;
                ShowConnectMessage("Connecting…", error: false);
                break;
            case AgentState.Rejected:
                ConnectButton.IsEnabled = true;
                ShowConnectMessage(status?.Message ?? "That didn't work — check the ID and try again.", error: true);
                break;
        }
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
        if (!workOn && _prompt is not null)
        {
            ClosePrompt();
        }
    }

    /// <summary>
    /// The update card: required (orange, work paused) or optional (quiet), with "Update now" and its progress.
    /// Status is polled every second while an update runs, so progress feels live.
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
        UpdateNotes.Text = view.Notes is null ? "" : $"What's new: {view.Notes}";
        UpdateNotes.Visibility = view.Notes is null ? Visibility.Collapsed : Visibility.Visible;

        UpdateButton.Content = view.Button;
        UpdateButton.Visibility = view.ButtonVisible ? Visibility.Visible : Visibility.Collapsed;
        UpdateButton.IsEnabled = view.ButtonEnabled;
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

    /// <summary>Open the update download in the default browser; the employee installs it manually.</summary>
    private void OnDownloadUpdate(object sender, RoutedEventArgs e)
    {
        var url = string.IsNullOrWhiteSpace(_updateDownloadUrl) ? "https://zinwe.io/download" : _updateDownloadUrl;
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception)
        {
            // No browser / blocked: leave the link visible in the panel text; nothing else to do.
        }
    }

    // ---- Connect this PC ---------------------------------------------------

    private async void OnConnect(object sender, RoutedEventArgs e)
    {
        var email = EmailInput.Text.Trim();
        var memberId = MemberIdInput.Text.Trim();
        if (email.Length == 0 || memberId.Length == 0)
        {
            ShowConnectMessage("Enter both your work email and your Member ID.", error: true);
            return;
        }

        ConnectButton.IsEnabled = false;
        ShowConnectMessage("Connecting…", error: false);

        var ack = await AgentPipeClient.SubmitEnrollmentAsync(email, memberId, TimeSpan.FromSeconds(5));
        if (ack is null)
        {
            ShowConnectMessage("Zinwe isn't running yet. Please try again in a moment.", error: true);
            ConnectButton.IsEnabled = true;
            return;
        }
        if (!ack.Accepted)
        {
            ShowConnectMessage(ack.Message, error: true);
            ConnectButton.IsEnabled = true;
            return;
        }
        // Accepted: the service is enrolling. The status poll flips us to Connected (or back to the form on a bad id).
        await RefreshStatusAsync();
    }

    private void ShowConnectMessage(string message, bool error)
    {
        ConnectMessage.Text = message;
        ConnectMessage.Foreground = (Brush)FindResource(error ? "Zinwe.Bad" : "Zinwe.Muted");
        ConnectMessage.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
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
        Hide();
    }

    // ---- Consent (DPDP): the member's own agreement, once per policy version ----------------

    private void OnConsentCheckChanged(object sender, RoutedEventArgs e) =>
        ConsentAgree.IsEnabled = ConsentCheck.IsChecked == true;

    private void OnConsentAgree(object sender, RoutedEventArgs e)
    {
        if (ConsentCheck.IsChecked != true)
        {
            return;
        }
        // The service reads this file and reports the agreement on its next heartbeat.
        _consent.Accept(DateTimeOffset.Now);
        ConsentPanel.Visibility = Visibility.Collapsed;
        SetupScroller.Visibility = Visibility.Collapsed;
        ConnectedContent.Visibility = Visibility.Visible;
        RenderTimer();
    }

    private void OnOpenPolicy(object sender, RoutedEventArgs e)
    {
        var slug = (sender as FrameworkElement)?.Tag as string ?? "privacy";
        try
        {
            Process.Start(new ProcessStartInfo($"https://zinwe.io/legal/{slug}") { UseShellExecute = true });
        }
        catch
        {
            // No default browser: nothing to do; the text above already explains the essentials.
        }
    }
}

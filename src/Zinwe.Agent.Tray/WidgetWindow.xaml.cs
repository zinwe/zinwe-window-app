using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Zinwe.Agent.Core.Ipc;
using Zinwe.Agent.Core.Work;
using Zinwe.Agent.Tray.Resources;
using Zinwe.Agent.Tray.Widget;
using Forms = System.Windows.Forms;

namespace Zinwe.Agent.Tray;

/// <summary>
/// Zinwe for Windows, tray app v2: a phone-width window above the taskbar, bottom right, that minimizes back to the
/// tray icon. The shell (title bar, header with the work pill, the work strip, the focus card, the tab bar, the snack
/// bar) is here; each screen is a view in Views/ rendering from the shared <see cref="TrayModel"/>.
/// </summary>
public partial class WidgetWindow : Window
{
    /// <summary>The board's panel: 380 × 720, never taller than the work area minus 16 px.</summary>
    public const double PanelWidth = 380;
    public const double PanelHeight = 720;

    private readonly TrayModel _model;
    private int _lastMinute = -1;

    private readonly Views.OnboardView Onboard = new() { Visibility = Visibility.Collapsed };
    private readonly Views.HomeView Home = new();
    private readonly Views.ChatView ChatView = new() { Visibility = Visibility.Collapsed };
    private readonly Views.TasksView TasksView = new() { Visibility = Visibility.Collapsed };
    private readonly Views.MeView MeView = new() { Visibility = Visibility.Collapsed };
    private readonly Views.PushView PushView = new() { Visibility = Visibility.Collapsed };
    private readonly Views.SheetView SheetHost = new() { Visibility = Visibility.Collapsed };

    /// <summary>Set by the tray on real shutdown so Closing stops hiding and actually closes.</summary>
    public bool AllowClose { get; set; }

    public WidgetWindow(TrayModel model)
    {
        _model = model;
        InitializeComponent();
        OnboardHost.Content = Onboard;
        foreach (var view in new UIElement[] { Home, ChatView, TasksView, MeView, PushView })
        {
            ScreensHost.Children.Add(view);
        }
        SheetSlot.Content = SheetHost;
        Onboard.Model = model;
        Home.Model = model;
        ChatView.Model = model;
        TasksView.Model = model;
        MeView.Model = model;
        PushView.Model = model;
        SheetHost.Model = model;
        foreach (var badge in new[] { BadgeChat, BadgeTasks, BadgeMe })
        {
            badge.Child = new TextBlock { FontSize = 11, FontWeight = FontWeights.ExtraBold, HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center };
            ((TextBlock)badge.Child).SetResourceReference(TextBlock.ForegroundProperty, "Z.OnOrange");
        }
        WorkMenu.CustomPopupPlacementCallback = (popup, target, _) =>
            [new CustomPopupPlacement(new Point(target.Width - popup.Width + 8, target.Height + 2), PopupPrimaryAxis.Horizontal)];

        _model.Changed += Render;
        _model.Ticked += OnTick;
        _model.FocusRequested += what =>
        {
            if (what == "composer")
            {
                ChatView.FocusComposer();
            }
        };
        HighContrastTheme.Changed += Render;

        SourceInitialized += (_, _) =>
        {
            RoundCorners();
            PlaceAboveTaskbar();
        };
        Loaded += (_, _) => Render();
        Activated += (_, _) => _model.OnShown();
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible)
            {
                PlaceAboveTaskbar();
            }
        };
        Closing += OnClosing;
    }

    /// <summary>The member is looking at the open chat (the tray skips message toasts while this is true).</summary>
    public bool IsReading => IsVisible && IsActive && WindowState != WindowState.Minimized
        && _model.Stage == Stage.App && _model.Tab == Tab.Chat && _model.Push == Push.None;

    /// <summary>Set by the tray once Ctrl+Alt+Z is registered (null when another app holds it).</summary>
    public string? Shortcut
    {
        get => _shortcut;
        set
        {
            _shortcut = value;
            MeView.ShortcutOn = value is not null;
        }
    }

    private string? _shortcut;

    // ---- Render -----------------------------------------------------------------------

    private bool _rendering;

    private void Render()
    {
        if (_rendering)
        {
            return;
        }
        _rendering = true;
        try
        {
            RenderAll();
        }
        finally
        {
            _rendering = false;
        }
    }

    private void RenderAll()
    {
        var m = _model;
        var focusName = Keyboard.FocusedElement is DependencyObject d && IsAncestorOf(d) ? AutomationProperties.GetName(d) : null;
        var stage = m.Stage;
        var inApp = stage == Stage.App;
        Onboard.Visibility = inApp ? Visibility.Collapsed : Visibility.Visible;
        AppArea.Visibility = inApp ? Visibility.Visible : Visibility.Collapsed;
        RenderHeader(m, stage);
        if (!inApp)
        {
            Onboard.Render();
            return;
        }

        var onHome = m.Tab == Tab.Home && m.Push == Push.None;
        Home.Visibility = Show(onHome);
        ChatView.Visibility = Show(m.Tab == Tab.Chat && m.Push == Push.None);
        TasksView.Visibility = Show(m.Tab == Tab.Tasks && m.Push == Push.None);
        MeView.Visibility = Show(m.Tab == Tab.Me && m.Push == Push.None);
        PushView.Visibility = Show(m.Push != Push.None);
        if (onHome)
        {
            Home.Render();
        }
        if (ChatView.IsVisible || m.Tab == Tab.Chat)
        {
            ChatView.Render();
        }
        if (m.Tab == Tab.Tasks)
        {
            TasksView.Render();
        }
        if (m.Tab == Tab.Me && m.Push == Push.None)
        {
            MeView.Render();
        }
        if (m.Push != Push.None)
        {
            PushView.Render();
        }
        SheetHost.Render();

        RenderStrip(m, onHome);
        RenderBanners(m);
        RenderFocus(m, onHome);
        RenderTabs(m);
        RenderSnack(m);
        TextScale.ScaleX = TextScale.ScaleY = m.Settings.TextLarge ? 1.12 : 1;
        RestoreFocus(focusName);
    }

    /// <summary>Lists are rebuilt on each change: put keyboard focus back on the control with the same name.</summary>
    private void RestoreFocus(string? name)
    {
        if (string.IsNullOrEmpty(name) || Keyboard.FocusedElement is DependencyObject now && IsAncestorOf(now)
            && AutomationProperties.GetName(now) == name)
        {
            return;
        }
        Dispatcher.BeginInvoke(() =>
        {
            if (Keyboard.FocusedElement is DependencyObject cur && IsAncestorOf(cur) && cur is FrameworkElement { IsVisible: true })
            {
                return;
            }
            if (Find(Root, name) is { } target)
            {
                target.Focus();
            }
        }, DispatcherPriority.Loaded);
    }

    private static FrameworkElement? Find(DependencyObject root, string name)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is FrameworkElement { IsVisible: true, Focusable: true } fe && AutomationProperties.GetName(fe) == name)
            {
                return fe;
            }
            if (Find(child, name) is { } hit)
            {
                return hit;
            }
        }
        return null;
    }

    private static Visibility Show(bool on) => on ? Visibility.Visible : Visibility.Collapsed;

    private string? _headerPicture;

    private void RenderHeader(TrayModel m, Stage stage)
    {
        var inApp = stage == Stage.App;
        var pushed = inApp && m.Push != Push.None;
        BackButton.Visibility = Show(pushed);
        // FR-44: the member's picture in place of the mark (no picture, or not set up: the official mark, as before).
        var picture = inApp ? m.MyPicture : PictureView.None;
        HeaderMark.Visibility = Show(!pushed && !picture.HasPicture);
        HeaderPicture.Visibility = Show(!pushed && picture.HasPicture);
        if (picture.HasPicture && picture.Stamp != _headerPicture)
        {
            _headerPicture = picture.Stamp;
            HeaderPicture.Content = Views.Ui.Avatar(picture, "", 28);
            AutomationProperties.SetName(HeaderPicture, picture.Describe());
        }
        ScreenTitle.Text = !inApp ? "Zinwe" : pushed ? m.Push switch
        {
            Push.Time => "My time",
            Push.Privacy => "Policies & terms",
            Push.Settings => "Settings",
            Push.Notifications => "Notifications",
            _ => "Help",
        } : m.Tab.ToString();
        // The wordmark says "Zinwe"; a dev copy adds its instance name next to it.
        TitleText.Text = Zinwe.Agent.Core.AgentInstance.IsDefault ? "" : $"({Zinwe.Agent.Core.AgentInstance.Name})";

        // The line under the title says something useful only: setup state, offline, the date on Home, who the chat is with.
        (string Text, string? Dot) line = stage switch
        {
            Stage.Removed => ($"Removed from {m.Company}", "Z.Muted"),
            Stage.Connect => ("Not connected yet", "Z.Muted"),
            Stage.Consent => ("Connected · waiting for your answer", "Z.Ok"),
            Stage.NotRunning => (Strings.StatusNotRunning, "Z.RedText"),
            Stage.Welcome => (StatusText.Line(m.Status), "Z.Ok"),
            _ when m.NotRunning => (Strings.StatusNotRunning, "Z.RedText"),
            _ when m.Offline => ("Offline · saved on this PC", "Z.Warn"),
            _ when m.Tab == Tab.Home && !pushed => (m.Now.ToLocalTime().ToString("dddd, d MMMM"), null),
            _ when m.Tab == Tab.Chat && !pushed => (m.Status?.OwnerName is { Length: > 0 } ? $"{m.OwnerNameStart} · your admin" : "Messages with your admin", null),
            _ when m.Consent == ConsentState.Declined => ("Monitoring is off on this PC", "Z.Warn"),
            _ when m.Status?.State == AgentState.UpdateRequired => (Strings.StatusUpdate, "Z.Warn"),
            _ => ("", null),
        };
        StatusRow.Visibility = Show(line.Text.Length > 0);
        StatusLine.Text = line.Text;
        StatusDot.Visibility = Show(line.Dot is not null);
        if (line.Dot is { } dot)
        {
            StatusDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, dot);
        }
        AutomationProperties.SetName(StatusLine, line.Text);

        // The work pill: everywhere but Home (Home has the full strip).
        var showMini = inApp && m.WorkHoursOn && !(m.Tab == Tab.Home && !pushed);
        MiniPill.Visibility = Show(showMini);
        if (showMini)
        {
            RenderMini(m);
        }
        else if (WorkMenu.IsOpen)
        {
            WorkMenu.IsOpen = false;
        }
    }

    private void RenderMini(TrayModel m)
    {
        var state = m.Timer.State;
        var clocked = state is WorkState.Working or WorkState.OnBreak;
        MiniText.Text = state switch
        {
            WorkState.NotStarted => "Start day",
            WorkState.Ended => "Start again",
            _ => DayLog.Hm(m.Running),
        };
        MiniText.FontFamily = clocked ? (FontFamily)FindResource("Z.Mono") : (FontFamily)FindResource("Zinwe.Font");
        MiniChevron.Visibility = Show(clocked);
        var (bg, border, fg, dot) = state switch
        {
            WorkState.OnBreak => ("Z.Surface", "Z.Line2", "Z.Ink", "Z.Warn"),
            WorkState.Working => ("Z.Surface", "Z.Line2", "Z.Ink", "Z.Orange"),
            _ => ("Z.Inv", "Z.Inv", "Z.OnInv", "Z.Muted"),
        };
        MiniPill.SetResourceReference(BackgroundProperty, bg);
        MiniPill.SetResourceReference(BorderBrushProperty, border);
        MiniText.SetResourceReference(TextBlock.ForegroundProperty, fg);
        MiniDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, dot);
        AutomationProperties.SetName(MiniPill, state switch
        {
            WorkState.Working => $"Working, {DayLog.HoursMinutes(m.Worked)}. Open work menu",
            WorkState.OnBreak => $"On break, {DayLog.HoursMinutes(m.Running)}. Open work menu",
            WorkState.Ended => "Day ended. Start again",
            _ => "Not in. Start day",
        });

        MenuBreak.Visibility = Show(state == WorkState.Working);
        MenuEnd.Visibility = Show(state == WorkState.Working);
        MenuEndBreak.Visibility = Show(state == WorkState.OnBreak);
        var (label, pbg, pfg) = Pill(state);
        MenuPill.SetResourceReference(Border.BackgroundProperty, pbg);
        MenuPillText.SetResourceReference(TextBlock.ForegroundProperty, pfg);
        MenuPillText.Text = label;
        MenuSince.Text = state == WorkState.OnBreak
            ? (m.Timer.BreakReason is { Length: > 0 } r ? r + " break" : "On a break")
            : m.Timeline.FirstOrDefault(e => e.Kind == TimelineKind.In) is { } first ? "Started " + first.Time : "";
        if (WorkMenu.IsOpen != m.WorkMenuOpen && clocked)
        {
            WorkMenu.IsOpen = m.WorkMenuOpen;
            if (m.WorkMenuOpen)
            {
                Dispatcher.BeginInvoke(() => (MenuBreak.IsVisible ? MenuBreak : MenuEndBreak).Focus(), DispatcherPriority.Input);
            }
        }
    }

    private static (string Label, string Bg, string Fg) Pill(WorkState state) => state switch
    {
        WorkState.Working => ("Working", "Z.OrangeTint", "Z.OrangeInk"),
        WorkState.OnBreak => ("On break", "Z.WarnTint", "Z.Warn"),
        WorkState.Ended => ("Day ended", "Z.Fill", "Z.Muted"),
        _ => ("Not in", "Z.Fill", "Z.Muted"),
    };

    private void RenderStrip(TrayModel m, bool onHome)
    {
        Strip.Visibility = Show(onHome && m.WorkHoursOn);
        var state = m.Timer.State;
        var (label, bg, fg) = Pill(state);
        StripPillText.Text = label;
        StripPill.SetResourceReference(Border.BackgroundProperty, bg);
        StripPillText.SetResourceReference(TextBlock.ForegroundProperty, fg);
        Strip.SetResourceReference(Border.BackgroundProperty, "Z.Surface");
        Strip.SetResourceReference(Border.BorderBrushProperty, "Z.Line");
        BigTime.Text = DayLog.Hm(m.Running);
        AutomationProperties.SetName(BigTime, (state == WorkState.OnBreak ? "Break time now: " : "Work time today: ") + DayLog.HoursMinutes(m.Running));
        StartDayButton.Visibility = Show(state == WorkState.NotStarted);
        BreakButton.Visibility = Show(state == WorkState.Working);
        EndDayButton.Visibility = Show(state == WorkState.Working);
        EndBreakButton.Visibility = Show(state == WorkState.OnBreak);
        StartAgainButton.Visibility = Show(state == WorkState.Ended);
    }

    private void RenderBanners(TrayModel m)
    {
        NotRunningBanner.Visibility = Show(m.NotRunning);
        NotRunningTitle.Text = Strings.NotRunningTitle;
        NotRunningBody.Text = Strings.Format(Strings.NotRunningBody, m.OwnerName);
        OfflineBanner.Visibility = Show(m.Offline);
        var queued = m.TaskList.Count(TaskBoard.WaitsToSend);
        OfflineText.Text = "Offline. Your times" + (queued > 0 ? $" and {queued} task update{(queued == 1 ? "" : "s")}" : "")
            + " are saved on this PC and sent when you’re back.";
    }

    private MemberTask? _focus;
    private bool _focusNow;

    private void RenderFocus(TrayModel m, bool onHome)
    {
        (_focus, _focusNow) = TaskBoard.Focus(m.TaskList, m.FocusId, m.Now);
        FocusCard.Visibility = Show(onHome && _focus is not null);
        if (_focus is not { } t)
        {
            return;
        }
        FocusKicker.Text = _focusNow ? "Now" : "Up next";
        FocusKicker.SetResourceReference(TextBlock.ForegroundProperty, "Z.Muted");
        var tone = TaskBoard.DueToneOf(t, m.Now);
        FocusDue.Text = TaskBoard.DueLabel(t, m.Now);
        FocusDue.FontWeight = tone == DueTone.Late ? FontWeights.Bold : tone == DueTone.Soon ? FontWeights.SemiBold : FontWeights.Normal;
        FocusDue.SetResourceReference(TextBlock.ForegroundProperty, tone == DueTone.Late ? "Z.RedText" : tone == DueTone.Soon ? "Z.StDoing" : "Z.Muted");
        FocusTitle.Content = t.Title;
        AutomationProperties.SetName(FocusTitle, "Open " + t.Title);
        FocusCta.Content = _focusNow ? "Mark done" : TaskBoard.NeedsConfirm(t) ? "Confirm & start" : "Start";
        FocusCta.Style = (Style)FindResource("Z.BtnSm.Pri");
        AutomationProperties.SetName(FocusCta, FocusCta.Content + ": " + t.Title);
        FocusCard.SetResourceReference(Border.BackgroundProperty, "Z.Surface");
        FocusCard.SetResourceReference(Border.BorderBrushProperty, "Z.Line");
        FocusSteps.Visibility = Show(t.Steps.Count > 0);
        if (t.Steps.Count > 0)
        {
            var done = t.Steps.Count(s => s.Done);
            FocusStepsText.Text = $"{done}/{t.Steps.Count} steps";
            FocusBar.Width = Math.Max(0, (FocusCard.ActualWidth > 0 ? FocusCard.ActualWidth - 110 : 220) * done / t.Steps.Count);
        }
    }

    private void RenderTabs(TrayModel m)
    {
        TabHome.IsChecked = m.Tab == Tab.Home;
        TabChat.IsChecked = m.Tab == Tab.Chat;
        TabTasks.IsChecked = m.Tab == Tab.Tasks;
        TabMe.IsChecked = m.Tab == Tab.Me;
        var u = m.Unread;
        Badge(BadgeChat, TabChat, "Chat", u.Chat);
        Badge(BadgeTasks, TabTasks, "Tasks", u.Tasks);
        Badge(BadgeMe, TabMe, "Me", u.Other);
        AutomationProperties.SetName(TabHome, "Home");
    }

    private static void Badge(Border badge, RadioButton tab, string name, int count)
    {
        badge.Visibility = Show(count > 0);
        ((TextBlock)badge.Child).Text = count > 9 ? "9+" : count.ToString();
        AutomationProperties.SetName(tab, count > 0 ? $"{name}, {count} new" : name);
    }

    private void RenderSnack(TrayModel m)
    {
        SnackBar.Visibility = Show(m.Snack is not null);
        if (m.Snack is not { } snack)
        {
            return;
        }
        SnackText.Text = snack.Text;
        SnackActions.Children.Clear();
        foreach (var (label, run) in snack.Actions)
        {
            var b = new Button
            {
                Content = label, Style = (Style)FindResource("Z.IconBtn"), Padding = new Thickness(4, 0, 4, 0), MinHeight = 44,
                FontSize = 12.5, FontWeight = FontWeights.Bold, Margin = new Thickness(6, 0, 0, 0),
            };
            b.SetResourceReference(ForegroundProperty, "Z.SnackAction");
            b.Click += (_, _) => run();
            SnackActions.Children.Add(b);
        }
    }

    private void OnTick()
    {
        if (_model.Stage != Stage.App)
        {
            return;
        }
        var minute = (int)_model.Running.TotalMinutes;
        if (minute == _lastMinute)
        {
            return;
        }
        // Hours and minutes only: the clock text changes once a minute (no ticking seconds in the corner of the eye).
        _lastMinute = minute;
        Render();
    }

    // ---- Placement -----------------------------------------------------------------------

    /// <summary>Bottom right, above the taskbar, on the screen with the taskbar's work area; never taller than it.</summary>
    private void PlaceAboveTaskbar()
    {
        var area = WorkArea();
        Height = Math.Min(PanelHeight, Math.Max(400, area.Height - 16));
        Width = PanelWidth;
        Left = area.Right - Width - 12;
        Top = area.Bottom - Height - 10;
    }

    private Rect WorkArea()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle != IntPtr.Zero && PresentationSource.FromVisual(this)?.CompositionTarget is { } target)
        {
            var area = Forms.Screen.PrimaryScreen?.WorkingArea ?? Forms.Screen.FromHandle(handle).WorkingArea;
            var toDips = target.TransformFromDevice;
            return new Rect(toDips.Transform(new Point(area.Left, area.Top)), toDips.Transform(new Point(area.Right, area.Bottom)));
        }
        return SystemParameters.WorkArea;
    }

    /// <summary>Windows 11 rounds the corners of a borderless window when asked (no effect on Windows 10).</summary>
    private void RoundCorners()
    {
        try
        {
            var handle = new WindowInteropHelper(this).Handle;
            var round = 2; // DWMWCP_ROUND
            _ = DwmSetWindowAttribute(handle, 33 /* DWMWA_WINDOW_CORNER_PREFERENCE */, ref round, sizeof(int));
        }
        catch (Exception)
        {
            // Older Windows: square corners.
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    // ---- Input -----------------------------------------------------------------------------

    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || e.Handled)
        {
            return;
        }
        e.Handled = true;
        if (!_model.Escape())
        {
            TuckAway();
        }
    }

    private void OnTab(object sender, RoutedEventArgs e)
    {
        if (_rendering)
        {
            return;
        }
        var tab = sender == TabChat ? Tab.Chat : sender == TabTasks ? Tab.Tasks : sender == TabMe ? Tab.Me : Tab.Home;
        _model.Go(tab);
    }

    private void OnBack(object sender, RoutedEventArgs e) => _model.Back();

    private void OnTuck(object sender, RoutedEventArgs e) => TuckAway();

    private void OnStartDay(object sender, RoutedEventArgs e) => _model.StartDay();

    private void OnAskBreak(object sender, RoutedEventArgs e) => _model.AskBreak();

    private void OnAskOut(object sender, RoutedEventArgs e) => _model.AskOut();

    private void OnEndBreak(object sender, RoutedEventArgs e) => _model.EndBreak();

    private async void OnRetry(object sender, RoutedEventArgs e)
    {
        RetryButton.IsEnabled = false;
        await _model.RefreshStatusAsync();
        RetryButton.IsEnabled = true;
    }

    private void OnMiniPill(object sender, RoutedEventArgs e)
    {
        switch (_model.Timer.State)
        {
            case WorkState.Working or WorkState.OnBreak:
                _model.SetWorkMenu(!_model.WorkMenuOpen);
                break;
            default:
                _model.StartDay();
                break;
        }
    }

    private void OnWorkMenuClosed(object? sender, EventArgs e)
    {
        if (_model.WorkMenuOpen)
        {
            _model.SetWorkMenu(false);
        }
    }

    private void OnWorkMenuKey(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Escape or Key.Tab)
        {
            e.Handled = e.Key == Key.Escape;
            _model.SetWorkMenu(false);
            MiniPill.Focus();
        }
    }

    private void OnMenuBreak(object sender, RoutedEventArgs e) => _model.AskBreak();

    private void OnMenuEndBreak(object sender, RoutedEventArgs e) => _model.EndBreak();

    private void OnMenuEnd(object sender, RoutedEventArgs e) => _model.AskOut();

    private void OnMenuTime(object sender, RoutedEventArgs e)
    {
        _model.SetWorkMenu(false);
        _model.Open(Push.Time);
    }

    private void OnFocusOpen(object sender, RoutedEventArgs e)
    {
        if (_focus is { } t)
        {
            _model.OpenTask(t.Id);
        }
    }

    private void OnFocusRun(object sender, RoutedEventArgs e)
    {
        if (_focus is not { } t)
        {
            return;
        }
        if (_focusNow)
        {
            _model.DoneTask(t);
        }
        else
        {
            _model.StartTask(t);
        }
    }

    // ---- Entry points used by the tray icon ---------------------------------------------------

    /// <summary>"Open chat" (tray menu, a message toast).</summary>
    public void FocusChat()
    {
        _model.Go(Tab.Chat);
        ChatView.FocusComposer();
    }

    /// <summary>"What Zinwe records on this PC" (tray menu): Me › Policies &amp; terms.</summary>
    public void ShowRecords()
    {
        if (_model.Stage == Stage.App)
        {
            _model.Go(Tab.Me);
            _model.Open(Push.Privacy);
        }
    }

    /// <summary>FR-14: Send feedback (tray menu).</summary>
    public void ShowFeedback()
    {
        if (_model.Stage == Stage.App)
        {
            _model.Go(Tab.Me);
            _model.OpenSheet(Sheet.Feedback);
        }
    }

    // ---- Window lifetime -----------------------------------------------------------------------

    /// <summary>Hide the window (the title bar buttons, Esc, Ctrl+Alt+Z). Zinwe keeps running in the taskbar.</summary>
    public void TuckAway()
    {
        _model.OnHidden();
        if (!SystemParameters.MinimizeAnimation || WindowState == WindowState.Minimized)
        {
            Hide();
            return;
        }
        // FR-13: Windows' own minimize animation first, then hidden and set back to normal size for next time.
        WindowState = WindowState.Minimized;
        var after = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(280) };
        after.Tick += (_, _) =>
        {
            after.Stop();
            if (WindowState != WindowState.Minimized)
            {
                return;
            }
            Hide();
            WindowState = WindowState.Normal;
        };
        after.Start();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (AllowClose)
        {
            _model.Changed -= Render;
            _model.Ticked -= OnTick;
            HighContrastTheme.Changed -= Render;
            return;
        }
        // Employees can't quit the widget; closing just tucks it away. Protection keeps running.
        e.Cancel = true;
        TuckAway();
    }
}

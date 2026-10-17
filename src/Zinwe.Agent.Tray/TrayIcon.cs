using System.Runtime.InteropServices;
using System.Windows.Threading;
using Zinwe.Agent.Core.Ipc;
using Zinwe.Agent.Core.Policy;
using Zinwe.Agent.Core.Work;
using Zinwe.Agent.Tray.Resources;
using Zinwe.Agent.Tray.Widget;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace Zinwe.Agent.Tray;

/// <summary>
/// Notification-area icon: polls the service for status, watches the local notifications store and raises
/// Windows toasts for new admin chat messages and protection notices (even while the widget is hidden), and
/// opens the widget or the chat on demand.
/// </summary>
internal sealed partial class TrayIcon : IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);
    // The agent pulls the chat every ~15s; reading the local file every 5s keeps the toast close behind it.
    private static readonly TimeSpan NotificationPollInterval = TimeSpan.FromSeconds(5);
    private const int ToastTextMax = 200;

    private readonly Forms.NotifyIcon _icon = new();
    private readonly DispatcherTimer _timer = new() { Interval = PollInterval };

    private readonly NotificationStore _noteStore = new();
    private readonly NotificationWatcher _watcher;

    private Drawing.Icon? _glyph;
    private WidgetWindow? _window;
    private AgentState? _lastState;

    // Tooltip parts: the protection headline (status poll) + unread chat count (file poll).
    private string _headline = "Checking…";
    private int _unreadChat;

    // What the last toast was about, so clicking it opens the right window.
    private bool _lastToastWasChat;
    // What the widget already opened itself for this sign-in (see RefreshAsync), and the last nudge toast.
    private readonly HashSet<AutoOpenReason> _autoOpened = [];
    private DateTime _autoOpenDay = DateTime.Today;
    private DateTimeOffset _lastNudge = DateTimeOffset.MinValue;
    private static readonly TimeSpan NudgeInterval = TimeSpan.FromHours(1);
    private readonly ConsentStore _consent = new();
    private readonly WorkTimerStore _timerStore = new();
    private string? _ownerName;

    // One-click update: polled every second once the member pressed "Update now", so the tray closes itself as
    // soon as the installer starts (its files are about to be replaced; the new service reopens it).
    private readonly DispatcherTimer _updateWatch = new() { Interval = TimeSpan.FromSeconds(1) };
    private string? _toastedUpdate;

    public TrayIcon()
    {
        _watcher = new NotificationWatcher(_noteStore, NotificationPollInterval);
    }

    public void Start()
    {
        _glyph = CreateGlyph();
        _icon.Icon = _glyph;
        _icon.Text = "Zinwe";
        // One click opens Zinwe (a double click is two clicks: same result).
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left)
            {
                ShowStatus();
            }
        };
        _icon.BalloonTipClicked += (_, _) =>
        {
            if (_lastToastWasChat)
            {
                ShowChat();
            }
            else
            {
                ShowStatus();
            }
        };

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add(Strings.TrayOpen, null, (_, _) => ShowStatus());
        menu.Items.Add(Strings.TrayOpenChat, null, (_, _) => ShowChat());
        menu.Items.Add(Strings.TrayRecords, null, (_, _) => ShowRecords());
#if DEBUG
        menu.Items.Add("Send test chat message (debug)", null, (_, _) => SeedTestChatMessage());
        menu.Items.Add("Send test notice (debug)", null, (_, _) => SeedTestMessage());
        menu.Items.Add(new Forms.ToolStripSeparator());
        // Employees can't quit the tray in release builds (closing it would not stop protection anyway).
        menu.Items.Add("Exit (debug only)", null, (_, _) => Shutdown());
#endif
        _icon.ContextMenuStrip = menu;
        _icon.Visible = true;

        // Toast for any message that arrives, regardless of whether the widget is open.
        _watcher.NewItems += OnNewNotifications;
        _watcher.Refreshed += OnNotificationsRefreshed;
        _watcher.Start();

        _timer.Tick += async (_, _) => await RefreshAsync();
        _updateWatch.Tick += async (_, _) => await WatchUpdateAsync();
        _timer.Start();
        _ = RefreshAsync();
    }

    private void OnNewNotifications(IReadOnlyList<NotificationItem> items)
    {
        // Protection notices: one toast each (newest first), as before.
        foreach (var item in items.Where(i => !i.IsChat))
        {
            _lastToastWasChat = false;
            var noticeTitle = item.Title.StartsWith("Zinwe", StringComparison.Ordinal) ? item.Title : "Zinwe: " + item.Title;
            _icon.ShowBalloonTip(10_000, noticeTitle, item.Body ?? "", Tone(item));
        }

        // Chat: only the admin's lines (never the employee's own reply coming back), and not while the
        // employee is already looking at the chat. One toast for a burst; clicking it opens the chat.
        var fromAdmin = items.Where(i => i.IsChat && i.IsFromAdmin).ToList(); // newest first
        if (fromAdmin.Count == 0 || _window?.IsReading == true)
        {
            return;
        }
        var latest = fromAdmin[0];
        var owner = ConsentText.Owner(_ownerName);
        var title = fromAdmin.Count == 1
            ? Strings.Format(Strings.ToastNewMessage, owner)
            : Strings.Format(Strings.ToastNewMessages, fromAdmin.Count, owner);
        var text = ChatFeed.Preview(latest, _ownerName);
        if (text.Length > ToastTextMax)
        {
            text = text[..(ToastTextMax - 1)] + "…";
        }
        _lastToastWasChat = true;
        // A break that wasn't approved is a question, not an alarm: an info toast.
        var tone = ChatFeed.IsBreakNotApproved(latest) ? Forms.ToolTipIcon.Info : Tone(latest);
        _icon.ShowBalloonTip(10_000, title, text.Length == 0 ? Strings.ToastOpenChat : text, tone);
    }

    private static Forms.ToolTipIcon Tone(NotificationItem item) => item.Severity?.ToLowerInvariant() switch
    {
        "critical" => Forms.ToolTipIcon.Error,
        "warning" => Forms.ToolTipIcon.Warning,
        _ => Forms.ToolTipIcon.Info,
    };

    private void OnNotificationsRefreshed(NotificationFile file)
    {
        var unread = ChatFeed.UnreadCount(file.Items);
        if (unread != _unreadChat)
        {
            _unreadChat = unread;
            UpdateTooltip();
        }
    }

    private void UpdateTooltip()
    {
        var tip = _unreadChat switch
        {
            0 => $"Zinwe — {_headline}",
            1 => $"Zinwe — {_headline} · 1 unread message",
            _ => $"Zinwe — {_headline} · {_unreadChat} unread messages",
        };
        // NotifyIcon.Text is capped at 127 chars.
        _icon.Text = tip.Length > 127 ? tip[..127] : tip;
    }

    private async Task RefreshAsync()
    {
        var status = await AgentPipeClient.GetStatusAsync(TimeSpan.FromSeconds(3));
        var (headline, detail, _) = StatusText.Describe(status);

        _headline = headline;
        _ownerName = status?.OwnerName ?? _ownerName;
        UpdateTooltip();

        // Only interrupt the user for a change they should know about: removal, or a required update. (A refused
        // Member ID is shown right in the Connect form the member is looking at.)
        var needsAction = status is { State: AgentState.Rejected, Removed: true } or { State: AgentState.UpdateRequired };
        if (needsAction && _lastState is not null && _lastState != status!.State)
        {
            _lastToastWasChat = false;
            _icon.ShowBalloonTip(10_000, "Zinwe: " + headline, detail, Forms.ToolTipIcon.Warning);
            _toastedUpdate = status.UpdateVersion;
        }
        else if (status?.UpdateVersion is { } version && version != _toastedUpdate)
        {
            // A new optional release: tell the member once per version; clicking opens the widget's Update card.
            _toastedUpdate = version;
            _lastToastWasChat = false;
            _icon.ShowBalloonTip(10_000, Strings.Format(Strings.ToastUpdateTitle, version),
                Strings.ToastUpdateBody, Forms.ToolTipIcon.Info);
        }
        OpenIfNeeded(status);

        _lastState = status?.State;
        ExitIfInstalling(status);
    }

    /// <summary>
    /// Open the widget by itself, once per sign-in for each reason, so nobody is left with a hidden icon: not
    /// connected yet, the notice not answered (closed, or a silent install), or a work day not started. While the
    /// notice or the connection is still waiting, a gentle toast repeats every hour.
    /// </summary>
    private void OpenIfNeeded(AgentStatus? status)
    {
        var consent = _consent.State(status?.NoticeVersion);
        var workOn = PlanModules.Allows(status?.Entitlements, PlanModules.WorkTime);
        var today = _timerStore.Load(DateTimeOffset.Now).State;
        var reason = AutoOpen.Decide(status, consent, today, workOn);
        if (_autoOpenDay != DateTime.Today)
        {
            // A PC left signed in overnight: tomorrow morning counts as a new start of the day.
            _autoOpenDay = DateTime.Today;
            _autoOpened.Remove(AutoOpenReason.StartDay);
        }
        if (reason == AutoOpenReason.None || (reason == AutoOpenReason.StartDay && DateTime.Now.Hour < 6))
        {
            return; // nothing waiting (and no "Good morning" in the middle of the night)
        }
        if (_autoOpened.Add(reason))
        {
            ShowStatus();
            if (reason == AutoOpenReason.StartDay)
            {
                Toast(Strings.ToastStartDayTitle, Strings.ToastStartDayBody);
            }
            else if (reason == AutoOpenReason.Consent)
            {
                Toast(Strings.ToastConsentTitle, Strings.ToastConsentBody);
            }
            _lastNudge = DateTimeOffset.UtcNow;
            return;
        }
        if (reason is AutoOpenReason.Consent or AutoOpenReason.Connect && DateTimeOffset.UtcNow - _lastNudge >= NudgeInterval
            && _window?.IsActive != true)
        {
            _lastNudge = DateTimeOffset.UtcNow;
            if (reason == AutoOpenReason.Consent)
            {
                Toast(Strings.ToastConsentTitle, Strings.ToastConsentBody);
            }
            else
            {
                Toast(Strings.ToastConnectTitle, Strings.ToastConnectBody);
            }
        }
    }

    private void Toast(string title, string text)
    {
        _lastToastWasChat = false;
        _icon.ShowBalloonTip(10_000, title, text, Forms.ToolTipIcon.Info);
    }

    /// <summary>The widget's "Update now". Null when the service took it on; otherwise why not, for the widget.</summary>
    public async Task<string?> StartUpdateAsync()
    {
        var ack = await AgentPipeClient.RequestUpdateAsync(TimeSpan.FromSeconds(5));
        if (ack is null)
        {
            return Strings.ConnectNotRunning;
        }
        if (!ack.Accepted)
        {
            return ack.Message;
        }
        _updateWatch.Start();
        return null;
    }

    private async Task WatchUpdateAsync()
    {
        var status = await AgentPipeClient.GetStatusAsync(TimeSpan.FromSeconds(2));
        if (status?.UpdateStage is not (UpdateStages.Downloading or UpdateStages.Installing))
        {
            _updateWatch.Stop(); // finished (ready / failed) or the service went away
        }
        ExitIfInstalling(status);
    }

    /// <summary>The installer is about to replace our files: close (the updated service reopens the tray).</summary>
    private void ExitIfInstalling(AgentStatus? status)
    {
        if (status?.UpdateStage != UpdateStages.Installing)
        {
            return;
        }
        _updateWatch.Stop();
        if (_window is not null)
        {
            _window.AllowClose = true;
        }
        System.Windows.Application.Current.Shutdown();
    }

    /// <summary>Opens the widget at "What Zinwe records on this PC".</summary>
    public void ShowRecords()
    {
        ShowStatus();
        _window?.ShowRecords();
    }

    /// <summary>Opens the widget at its chat (the chat is built into it).</summary>
    public void ShowChat()
    {
        ShowStatus();
        _window?.FocusChat();
    }

    public void ShowStatus()
    {
        if (_window is null)
        {
            _window = new WidgetWindow(_noteStore, _watcher, StartUpdateAsync);
            _window.Closed += (_, _) => _window = null;
            _window.Show();
        }
        else if (!_window.IsVisible)
        {
            _window.Show();
        }
        if (_window.WindowState == System.Windows.WindowState.Minimized)
        {
            _window.WindowState = System.Windows.WindowState.Normal;
        }
        _window.Activate();
    }

#if DEBUG
    private void SeedTestMessage()
    {
        _noteStore.DebugAppend(new NotificationItem(
            Guid.NewGuid().ToString("D"),
            DateTimeOffset.UtcNow,
            "warning",
            "Test alert from admin",
            "This is a locally-seeded test message. The service writes real ones here in a later milestone."));
        _watcher.Poll();
    }

    private void SeedTestChatMessage()
    {
        _noteStore.DebugAppend(new NotificationItem(
            Guid.NewGuid().ToString("D"),
            DateTimeOffset.UtcNow,
            "info",
            "",
            "Hi! This is a locally-seeded chat message. Real ones come from the dashboard's Chat page.",
            Kind: NotificationItem.ChatKind));
        _watcher.Poll();
    }

    private void Shutdown()
    {
        if (_window is not null)
        {
            _window.AllowClose = true;
        }
        System.Windows.Application.Current.Shutdown();
    }
#endif

    /// <summary>
    /// The notification-area icon: the real Zinwe mark (Assets/zinwe-mark.png, already transparent - see
    /// design/make_tray_brand.py), scaled to 32px. Falls back to a drawn orange "Z" if the asset can't be loaded.
    /// </summary>
    private static Drawing.Icon CreateGlyph()
    {
        const int size = 32;
        using var bitmap = new Drawing.Bitmap(size, size);
        using (var g = Drawing.Graphics.FromImage(bitmap))
        {
            g.InterpolationMode = Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.SmoothingMode = Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Drawing.Color.Transparent);
            if (!TryDrawMark(g, size))
            {
                DrawFallbackZ(g, size);
            }
        }
        var handle = bitmap.GetHicon();
        try
        {
            return (Drawing.Icon)Drawing.Icon.FromHandle(handle).Clone(); // Clone owns its handle
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    private static bool TryDrawMark(Drawing.Graphics g, int size)
    {
        try
        {
            var info = System.Windows.Application.GetResourceStream(
                new Uri("pack://application:,,,/Assets/zinwe-mark.png", UriKind.Absolute));
            if (info is null)
            {
                return false;
            }
            using var stream = info.Stream;
            using var mark = new Drawing.Bitmap(stream);
            g.DrawImage(mark, 0, 0, size, size);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static void DrawFallbackZ(Drawing.Graphics g, int size)
    {
        g.TextRenderingHint = Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        using var orange = new Drawing.SolidBrush(Drawing.Color.FromArgb(0xFD, 0x6D, 0x02) /* logo orange */);
        using var font = new Drawing.Font("Segoe UI", 22, Drawing.FontStyle.Bold, Drawing.GraphicsUnit.Pixel);
        using var format = new Drawing.StringFormat { Alignment = Drawing.StringAlignment.Center, LineAlignment = Drawing.StringAlignment.Center };
        g.DrawString("Z", font, orange, new Drawing.RectangleF(0, 0, size, size), format);
    }

    public void Dispose()
    {
        _timer.Stop();
        _updateWatch.Stop();
        _watcher.NewItems -= OnNewNotifications;
        _watcher.Refreshed -= OnNotificationsRefreshed;
        _watcher.Dispose();
        _icon.Visible = false;
        _icon.Dispose();
        _glyph?.Dispose();
        if (_window is not null)
        {
            _window.AllowClose = true;
            _window.Close();
        }
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyIcon(IntPtr handle);
}

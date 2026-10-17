using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Zinwe.Agent.Core.Ipc;
using Zinwe.Agent.Core.Messages;
using Zinwe.Agent.Tray.Widget;

namespace Zinwe.Agent.Tray;

/// <summary>
/// The member's chat with their admin, built into the widget. Closed it's a compact bar (unread count + the latest
/// line); a click opens the conversation and composer below it. Reads the conversation from the shared notifications
/// file (chat lines only) and queues replies in the shared outbox; the agent delivers both ways within seconds.
/// While the widget is in front of the member, the admin's messages count as read (the agent tells the backend).
/// </summary>
public partial class ChatPanel : UserControl
{
    // While the widget is open, re-read the file often so a new message shows almost at once.
    private readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly MessageOutbox _outbox = new();

    private NotificationStore? _store;
    private NotificationWatcher? _watcher;
    private Func<bool> _isReading = () => false;
    private string _signature = "";
    private bool _atBottom = true;

    /// <summary>The conversation is showing (the bar was clicked, or "Open chat" was chosen).</summary>
    public bool IsOpen => Body.Visibility == Visibility.Visible;

    /// <summary>Raised when the chat opens or closes (the window re-measures its height).</summary>
    public event Action? OpenChanged;

    /// <summary>Height of the conversation area: 300 normally, less on a short screen (the widget sets it).</summary>
    public static readonly DependencyProperty ConversationHeightProperty = DependencyProperty.Register(
        nameof(ConversationHeight), typeof(double), typeof(ChatPanel), new PropertyMetadata(MaxConversationHeight));

    public const double MaxConversationHeight = 300;
    public const double MinConversationHeight = 140;

    public double ConversationHeight
    {
        get => (double)GetValue(ConversationHeightProperty);
        set => SetValue(ConversationHeightProperty, value);
    }

    private string? _ownerName;

    /// <summary>The owner's name when the backend sends it ("Message Ravi", "Ravi: …").</summary>
    public string? OwnerName
    {
        get => _ownerName;
        set
        {
            if (value == _ownerName)
            {
                return;
            }
            _ownerName = value;
            _signature = "";
            if (_store is not null)
            {
                Render(_store.Load(), forceScroll: false);
            }
        }
    }

    public ChatPanel()
    {
        InitializeComponent();
        _poll.Tick += (_, _) => _watcher?.Poll();
    }

    /// <summary>Connect to the shared store; <paramref name="isReading"/> says whether the member can see the chat.</summary>
    public void Attach(NotificationStore store, NotificationWatcher watcher, Func<bool> isReading)
    {
        _store = store;
        _watcher = watcher;
        _isReading = isReading;
        _watcher.Refreshed += OnRefreshed;
        Render(_watcher.Poll(), forceScroll: true);
        _poll.Start();
    }

    public void Detach()
    {
        _poll.Stop();
        if (_watcher is not null)
        {
            _watcher.Refreshed -= OnRefreshed;
        }
    }

    /// <summary>"Open chat" (tray menu, a message toast): open it, go to the newest message and the composer.</summary>
    public void Activate()
    {
        SetOpen(true);
        Input.Focus();
    }

    private void OnToggle(object sender, RoutedEventArgs e)
    {
        SetOpen(!IsOpen);
        if (IsOpen)
        {
            Input.Focus();
        }
    }

    private void SetOpen(bool open)
    {
        if (open == IsOpen)
        {
            return;
        }
        Body.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        ChevronTurn.Angle = open ? 180 : 0;
        HeaderButton.ToolTip = open ? "Hide the chat" : "Open the chat";
        if (open && _store is not null)
        {
            _signature = ""; // re-bind + scroll to the newest line
            Render(_store.Load(), forceScroll: true);
        }
        OpenChanged?.Invoke();
    }

    /// <summary>Re-render without moving focus (e.g. the window came back to the front: read the new messages).</summary>
    public void Refresh()
    {
        if (_store is not null)
        {
            Render(_store.Load(), forceScroll: false);
        }
    }

    /// <summary>Called on every status poll: a short note when messages can't flow right now.</summary>
    public void SetConnection(AgentStatus? status)
    {
        var note = status?.State switch
        {
            null => "Zinwe isn't running right now. Your messages are sent once it starts.",
            AgentState.Connected or AgentState.UpdateRequired => null,
            AgentState.Offline => "This PC is offline. Your messages are sent when it reconnects.",
            _ => "This PC isn't connected to Zinwe yet.",
        };
        ConnectionText.Text = note ?? "";
        ConnectionNote.Visibility = note is null ? Visibility.Collapsed : Visibility.Visible;
    }

    // ---- Conversation ------------------------------------------------------

    private void OnRefreshed(NotificationFile file) => Render(file, forceScroll: false);

    private void Render(NotificationFile file, bool forceScroll)
    {
        // The bar: unread count + the latest line (also while closed).
        var unread = ChatFeed.UnreadCount(file.Items);
        UnreadCount.Text = unread > 9 ? "9+" : unread.ToString();
        UnreadBadge.Visibility = unread > 0 ? Visibility.Visible : Visibility.Collapsed;
        var latest = ChatFeed.Latest(file.Items);
        var owner = string.IsNullOrWhiteSpace(_ownerName) ? "Admin" : _ownerName.Trim();
        Preview.Text = latest is null
            ? string.IsNullOrWhiteSpace(_ownerName) ? "Chat with your admin" : $"Chat with {owner}"
            : $"{(latest.IsFromAdmin ? owner : "You")}: {ChatFeed.Preview(latest, _ownerName)}";
        // Screen readers hear the unread count with the bar's name.
        AutomationProperties.SetName(HeaderButton, unread switch
        {
            0 => "Messages",
            1 => "Messages, 1 unread",
            _ => $"Messages, {unread} unread",
        });
        Preview.Foreground = (System.Windows.Media.Brush)FindResource(unread > 0 ? "Zinwe.Ink" : "Zinwe.Muted");
        Preview.FontWeight = unread > 0 ? FontWeights.SemiBold : FontWeights.Normal;

        if (!IsOpen)
        {
            return; // the conversation renders (and counts as read) only while open
        }
        var rows = ChatFeed.Build(file.Items, _outbox.Pending(), owner: _ownerName);

        // Re-bind only when something visible changed, so the list doesn't flicker or lose its scroll position.
        var signature = string.Join("|", rows.Select(r => r.Key + "@" + r.Stamp));
        if (signature != _signature)
        {
            var grew = rows.Count > MessageList.Items.Count;
            _signature = signature;
            MessageList.ItemsSource = rows;
            EmptyState.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            if (forceScroll || (grew && _atBottom))
            {
                ScrollToEnd();
            }
        }
        if (_isReading() && ChatFeed.UnreadCount(file.Items) > 0)
        {
            _store?.MarkChatRead();
        }
    }

    /// <summary>"Message Ravi" under a break that wasn't approved: straight to the composer.</summary>
    private void OnReply(object sender, RoutedEventArgs e) => Input.Focus();

    private void ScrollToEnd() =>
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () => Scroller.ScrollToEnd());

    private void OnScrollChanged(object sender, ScrollChangedEventArgs e) =>
        _atBottom = Scroller.VerticalOffset >= Scroller.ScrollableHeight - 24;

    // ---- Composer ----------------------------------------------------------

    private void OnInputKeyDown(object sender, KeyEventArgs e)
    {
        // Enter sends; Shift+Enter is a new line.
        if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0)
        {
            e.Handled = true;
            Send();
        }
    }

    private void OnInputChanged(object sender, TextChangedEventArgs e)
    {
        Placeholder.Visibility = Input.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        SendButton.IsEnabled = !string.IsNullOrWhiteSpace(Input.Text);
    }

    private void OnSend(object sender, RoutedEventArgs e) => Send();

    private void Send()
    {
        var text = Input.Text.Trim();
        if (text.Length == 0 || _store is null)
        {
            return;
        }
        try
        {
            _outbox.Append(text);
            Input.Clear();
            SendError.Visibility = Visibility.Collapsed;
        }
        catch (Exception)
        {
            SendError.Text = "Couldn't send right now. Please try again.";
            SendError.Visibility = Visibility.Visible;
            return;
        }
        // Show the "Sending…" bubble right away; it turns into the delivered line on the next sync.
        Render(_store.Load(), forceScroll: true);
    }
}

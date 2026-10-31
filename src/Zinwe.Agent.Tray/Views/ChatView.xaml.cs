using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Zinwe.Agent.Core.Ipc;
using Zinwe.Agent.Tray.Widget;

namespace Zinwe.Agent.Tray.Views;

/// <summary>
/// The chat with the owner: the real conversation (notifications file + outbox) with the owner's task cards, quick
/// replies and reply-with-quote. Enter sends, Shift+Enter is a new line, Esc cancels the reply. "Reply" is plain text
/// under a message; a reply the backend refused says "Not sent" with Retry and Delete.
/// </summary>
public partial class ChatView : UserControl
{
    private const double QuickHeight = 44;
    private const double TaskCardWidth = 300;

    private static readonly string[] Quick = ["On it", "Thanks!", "Running 10 min late", "Can we talk?"];
    private string _signature = "";
    private bool _atBottom = true;
    private bool _primed;
    private bool? _quickShown;
    private bool _replyShown;
    private int _unseen;
    private readonly Dictionary<string, FrameworkElement> _rows = [];
    private readonly HashSet<string> _known = [];

    public ChatView()
    {
        InitializeComponent();
        foreach (var q in Quick)
        {
            QuickReplies.Children.Add(Ui.Button(q, "Z.QuickReply", () => Model?.Send(q), "Send: " + q));
        }
    }

    public TrayModel? Model { get; set; }

    public void FocusComposer() => Dispatcher.BeginInvoke(() => Input.Focus(), DispatcherPriority.Input);

    public void Render()
    {
        if (Model is not { } m)
        {
            return;
        }
        var note = m.Status?.State switch
        {
            null => "Zinwe isn't running right now. Your messages are sent once it starts.",
            AgentState.Connected or AgentState.UpdateRequired => null,
            AgentState.Offline => null, // the offline banner above says it
            _ => "This PC isn't connected to Zinwe yet.",
        };
        ConnectionText.Text = note ?? "";
        ConnectionNote.Visibility = note is null ? Visibility.Collapsed : Visibility.Visible;

        Input.Tag = m.Offline ? $"Message {m.OwnerFirst} (sends when you’re back online)" : $"Message {m.OwnerFirst}…";
        AutomationProperties.SetName(Input, "Message " + m.OwnerName);
        ComposerNote.Text = m.ReplyTo is not null ? "Enter to send · Esc cancels the reply" : "Enter to send · Shift+Enter for a new line";
        ReplyBar.Visibility = m.ReplyTo is null ? Visibility.Collapsed : Visibility.Visible;
        if (m.ReplyTo is { } quote)
        {
            ReplyWho.Text = "Replying to " + (quote.Who == "me" ? "yourself" : m.OwnerName);
            ReplyText.Text = quote.Text;
            if (!_replyShown)
            {
                Motion.Arrive(ReplyBar, 0, 6, 1, 160);
            }
        }
        _replyShown = m.ReplyTo is not null;
        if (Input.Text != m.Draft && !Input.IsKeyboardFocused)
        {
            Input.Text = m.Draft;
        }
        SendError.Text = m.SendError ?? "";
        SendError.Visibility = m.SendError is null ? Visibility.Collapsed : Visibility.Visible;

        var rows = m.Thread;
        UpdateQuick(m, rows);
        var signature = string.Join("|", rows.Select(r => r.Key + "@" + r.Meta + "@" + r.Task?.Status + r.Task?.IsNew
            + string.Concat(r.Task?.Steps.Select(s => s.Done ? "1" : "0") ?? []) + r.Task?.Notes.Count + "@" + r.Task?.Activity.Count + r.Task?.Queued))
            + "#" + m.OpenCard + "#" + m.OpenLog + "#" + m.Now.ToLocalTime().Date.ToString("yyyyMMdd") + "#" + m.OwnerName;
        if (signature == _signature)
        {
            return;
        }
        var grew = rows.Count > _rows.Count;
        _signature = signature;
        RenderRows(m, rows);
        EmptyState.Text = $"No messages yet. {m.OwnerFirstStart}’s messages show up here, and you can write to them below.";
        EmptyState.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (grew && _atBottom || _rows.Count == rows.Count && _atBottom)
        {
            Dispatcher.BeginInvoke(() => Scroller.ScrollToEnd(), DispatcherPriority.Background);
        }
        RenderJump();
    }

    /// <summary>Quick replies answer the owner: shown when they spoke last, folded away while the member types or replies.</summary>
    private void UpdateQuick(TrayModel m, IReadOnlyList<ThreadRow> rows)
    {
        var last = rows.Count > 0 ? rows[^1] : null;
        var show = last is { Mine: false, Kind: ThreadKind.Text or ThreadKind.Task } && Input.Text.Length == 0 && m.ReplyTo is null;
        if (show == _quickShown)
        {
            return;
        }
        var first = _quickShown is null;
        _quickShown = show;
        QuickHost.IsEnabled = show;
        if (first)
        {
            QuickHost.Height = show ? QuickHeight : 0;
            QuickHost.Opacity = show ? 1 : 0;
            return;
        }
        Motion.Fold(QuickHost, show ? QuickHeight : 0, show ? 1 : 0);
    }

    private void RenderRows(TrayModel m, IReadOnlyList<ThreadRow> rows)
    {
        Log.Children.Clear();
        _rows.Clear();
        var ownerInitials = Avatars.Initials(m.Status?.OwnerName);

        foreach (var row in rows)
        {
            FrameworkElement el = row.Kind switch
            {
                ThreadKind.Day => DayLine(row.Text),
                ThreadKind.Sys => SysLine(row),
                _ => Message(m, row, ownerInitials),
            };
            el.Margin = new Thickness(0, 0, 0, 12);
            _rows[row.Key] = el;
            Log.Children.Add(el);

            if (_known.Add(row.Key) && row.Kind is ThreadKind.Text or ThreadKind.Task)
            {
                Announce(row, el);
            }
        }
        _primed |= rows.Count > 0;
    }

    /// <summary>What a row does the moment it first appears (never on the first paint, nor for old messages).</summary>
    private void Announce(ThreadRow row, FrameworkElement el)
    {
        if (row is { Mine: true, Pending: true })
        {
            Motion.Arrive(el, 0, 14, 0.96); // rises out of the composer
        }
        else if (!_primed)
        {
            return;
        }
        else if (!row.Mine)
        {
            Motion.Arrive(el, -8, 0);
            if (!_atBottom)
            {
                _unseen++;
            }
        }
    }

    private void RenderJump()
    {
        JumpButton.Visibility = _unseen > 0 ? Visibility.Visible : Visibility.Collapsed;
        JumpText.Text = _unseen == 1 ? "1 new message" : _unseen + " new messages";
    }

    private static FrameworkElement DayLine(string text)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        var left = Rule();
        var right = Rule();
        Grid.SetColumn(right, 2);
        var label = Ui.Text(text, 12, fg: "Z.Muted");
        label.Margin = new Thickness(10, 0, 10, 0);
        Grid.SetColumn(label, 1);
        grid.Children.Add(left);
        grid.Children.Add(label);
        grid.Children.Add(right);
        return grid;

        static Border Rule() => new Border { Height = 1, VerticalAlignment = VerticalAlignment.Center }.Res(Border.BackgroundProperty, "Z.Line");
    }

    private static FrameworkElement SysLine(ThreadRow row)
    {
        var text = Ui.Text(row.Text, 12, FontWeights.Medium, "Z.Muted", wrap: true);
        text.MaxWidth = 300;
        var dot = Ui.Dot(7, row.ToneNo ? "Z.Warn" : "Z.Ok");
        dot.Margin = new Thickness(0, 5, 6, 0);
        dot.VerticalAlignment = VerticalAlignment.Top;
        var line = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        line.Children.Add(dot);
        line.Children.Add(text);
        AutomationProperties.SetName(line, row.Text);
        return line;
    }

    private FrameworkElement Message(TrayModel m, ThreadRow row, string ownerInitials)
    {
        var side = row.Mine ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        var wrap = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = side, Background = Brushes.Transparent };
        if (row.Kind == ThreadKind.Task && row.Task is { } task)
        {
            wrap.Children.Add(new Border { Width = 32 });
            wrap.Children.Add(TaskCard.Build(task, m, m.OpenCard == task.Id, TaskCardWidth));
            return wrap;
        }

        if (!row.Mine)
        {
            FrameworkElement avatar = row.HasAvatar ? Ui.Avatar(null, ownerInitials, 24, "#FDEBDC") : new Border { Width = 24 };
            avatar.VerticalAlignment = VerticalAlignment.Bottom;
            avatar.Margin = new Thickness(0, 0, 8, 22); // sits beside the bubble, not the time under it
            wrap.Children.Add(avatar);
        }
        var column = new StackPanel { HorizontalAlignment = side };
        column.Children.Add(Bubble(row, m));
        column.Children.Add(MetaLine(m, row, wrap));
        wrap.Children.Add(column);
        AutomationProperties.SetName(wrap, $"{(row.Mine ? "You" : m.OwnerFirst)}: {row.Snippet}. {row.Meta}");
        return wrap;
    }

    /// <summary>The line under a bubble: the time and the plain-text Reply, or "Not sent · Retry · Delete".</summary>
    private static FrameworkElement MetaLine(TrayModel m, ThreadRow row, FrameworkElement hoverArea)
    {
        var line = new StackPanel
        {
            Orientation = Orientation.Horizontal, MinHeight = 22, HorizontalAlignment = row.Mine ? HorizontalAlignment.Right : HorizontalAlignment.Left,
        };
        if (row.Failed)
        {
            line.Children.Add(Ui.Text(row.Meta, 12, FontWeights.SemiBold, "Z.RedText").Centered());
            line.Children.Add(Dot());
            line.Children.Add(Ui.Button("Retry", "Z.TextAction", () => m.RetryReply(row), "Retry sending: " + Cut(row.Snippet, 60)));
            line.Children.Add(Dot());
            line.Children.Add(Ui.Button("Delete", "Z.TextAction", () => m.DeleteReply(row), "Delete: " + Cut(row.Snippet, 60)));
            return line;
        }

        var time = Ui.Text(row.Meta, 12, fg: "Z.Muted").Centered();
        if (row is { Pending: true } && row.Meta.StartsWith("Sending", StringComparison.Ordinal))
        {
            Motion.Breathe(time);
        }
        if (row.Pending)
        {
            line.Children.Add(time);
            return line;
        }

        var reply = Ui.Button("Reply", "Z.TextAction", () => m.StartReply(row),
            "Reply to " + (row.Mine ? "your message" : m.OwnerFirst) + ": " + Cut(row.Snippet, 60));
        if (!row.Mine)
        {
            line.Children.Add(time);
            line.Children.Add(Dot());
            line.Children.Add(reply);
            return line;
        }

        // On the member's own messages Reply waits for the mouse or the keyboard, so the thread stays quiet.
        var own = new StackPanel { Orientation = Orientation.Horizontal, Opacity = 0 };
        own.Children.Add(reply);
        own.Children.Add(Dot());
        hoverArea.MouseEnter += (_, _) => own.Opacity = 1;
        hoverArea.MouseLeave += (_, _) => own.Opacity = own.IsKeyboardFocusWithin ? 1 : 0;
        own.GotKeyboardFocus += (_, _) => own.Opacity = 1;
        own.LostKeyboardFocus += (_, _) => own.Opacity = hoverArea.IsMouseOver ? 1 : 0;
        line.Children.Add(own);
        line.Children.Add(time);
        return line;

        static TextBlock Dot() => Ui.Text("·", 12, fg: "Z.Muted").Centered(4);
    }

    private FrameworkElement Bubble(ThreadRow row, TrayModel m)
    {
        var stack = new StackPanel();
        if (row.Importance is { } importance)
        {
            var label = Ui.Text(importance, 12, FontWeights.Bold, importance == "Urgent" ? "Z.RedText" : "Z.WarnText");
            label.Margin = new Thickness(0, 0, 0, 2);
            stack.Children.Add(label);
        }
        if (row.Quote is { } q)
        {
            var qt = new StackPanel();
            qt.Children.Add(Ui.Text(q.Who, 12, FontWeights.SemiBold, "Z.Ink2"));
            var qtext = Ui.Text(q.Text, 12, fg: "Z.Ink2", trim: true);
            qtext.MaxWidth = 240;
            qt.Children.Add(qtext);
            var qb = new Button
            {
                Content = qt, Cursor = Cursors.Hand, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 4),
                Template = QuoteTemplate(), FocusVisualStyle = (Style)FindResource("Z.FocusRing"),
            };
            AutomationProperties.SetName(qb, $"Show the message you replied to, from {(q.Who == "You" ? "you" : q.Who)}: {Cut(q.Text, 60)}");
            qb.Click += (_, _) => JumpTo(q.TargetKey);
            stack.Children.Add(qb);
        }
        var text = new TextBlock { FontSize = 13.5, TextWrapping = TextWrapping.Wrap, LineHeight = 19 };
        ChatLinks.SetText(text, row.Text);
        text.SetResourceReference(TextBlock.ForegroundProperty, "Z.Ink");
        stack.Children.Add(text);

        var bubble = new Border
        {
            MaxWidth = 288, Child = stack, Opacity = row.Pending ? 0.7 : 1, Padding = new Thickness(12, 8, 12, 8),
            CornerRadius = row.Mine ? new CornerRadius(12, 12, 4, 12) : new CornerRadius(12, 12, 12, 4),
            HorizontalAlignment = row.Mine ? HorizontalAlignment.Right : HorizontalAlignment.Left,
        };
        bubble.SetResourceReference(Border.BackgroundProperty, row.Mine ? "Z.OrangeTint" : "Z.Fill");
        return bubble;
    }

    private static ControlTemplate QuoteTemplate()
    {
        var t = new ControlTemplate(typeof(Button));
        var b = new FrameworkElementFactory(typeof(Border));
        b.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        b.AppendChild(new FrameworkElementFactory(typeof(ContentPresenter)));
        t.VisualTree = b;
        return t;
    }

    private void JumpTo(string key)
    {
        if (!_rows.TryGetValue(key, out var el))
        {
            return;
        }
        el.BringIntoView();
        var flash = new Border { CornerRadius = new CornerRadius(12), IsHitTestVisible = false, Background = new SolidColorBrush(Color.FromArgb(0x29, 0xF2, 0x6A, 0x0A)) };
        if (el is Panel panel)
        {
            var host = panel;
            var old = host.Background;
            host.Background = flash.Background;
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                host.Background = old;
            };
            timer.Start();
        }
    }

    private static string Cut(string s, int n) => s.Length > n ? s[..n] : s;

    private void OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        _atBottom = Scroller.VerticalOffset >= Scroller.ScrollableHeight - 24;
        if (_atBottom && _unseen > 0)
        {
            _unseen = 0;
            RenderJump();
        }
    }

    private void OnJump(object sender, RoutedEventArgs e)
    {
        _unseen = 0;
        RenderJump();
        Scroller.ScrollToEnd();
        Input.Focus();
    }

    private void OnInputKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && Model?.ReplyTo is not null)
        {
            e.Handled = true;
            Model.CancelReply();
            return;
        }
        if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0)
        {
            e.Handled = true;
            Send();
        }
    }

    private void OnInputChanged(object sender, TextChangedEventArgs e)
    {
        if (Model is { } m)
        {
            m.Draft = Input.Text;
            UpdateQuick(m, m.Thread);
        }
        var ready = !string.IsNullOrWhiteSpace(Input.Text);
        if (ready && !SendButton.IsEnabled)
        {
            Motion.Pop(SendButton);
        }
        SendButton.IsEnabled = ready;
    }

    private void OnInputFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        var on = Input.IsKeyboardFocused;
        Composer.SetResourceReference(Border.BorderBrushProperty, on ? "Z.Focus" : "Z.Line2");
        Composer.BorderThickness = new Thickness(on ? 2 : 1);
        Composer.Padding = on ? new Thickness(11, 2, 2, 2) : new Thickness(12, 3, 3, 3); // the thicker border must not move the text
        ComposerNote.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnSend(object sender, RoutedEventArgs e) => Send();

    private void Send()
    {
        if (Model?.Send(Input.Text) == true)
        {
            Input.Clear();
            _atBottom = true;
            Motion.Fly(SendGlyph);
            Input.Focus(); // the send button just disabled itself: don't leave the keyboard on it
            Dispatcher.BeginInvoke(() => Scroller.ScrollToEnd(), DispatcherPriority.Background);
        }
    }

    private void OnCancelReply(object sender, RoutedEventArgs e) => Model?.CancelReply();
}

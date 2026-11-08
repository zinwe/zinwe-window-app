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
/// under a message; a reply the backend refused says "Not sent" with Retry and Delete. The composer's ⚡ lists every
/// quick reply (a star puts up to three in the row over the composer), its 🙂 adds an emoji where the member types,
/// and the thread sits on the background picked in Chat options.
/// </summary>
public partial class ChatView : UserControl
{
    private const double QuickHeight = 52; // the starred chips and the thin bar under them when they run wide (F-08)

    private string _signature = "";
    private string _quickRow = "";
    private string _quickList = "";
    private string _background = "";
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
        foreach (var emoji in QuickReplies.Emoji)
        {
            EmojiGrid.Children.Add(EmojiKey(emoji));
        }
        // A click anywhere but the open list (or its button) closes the quick replies or the emoji.
        PreviewMouseDown += (_, e) =>
        {
            if (Model is { } m && (m.QuickOpen || m.EmojiOpen) && e.OriginalSource is DependencyObject src
                && !new FrameworkElement[] { QuickPanel, EmojiPanel, QuickButton, EmojiButton }.Any(p => IsInside(src, p)))
            {
                m.CloseMenu();
            }
        };
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

        Input.Tag = m.Offline ? $"Message {m.OwnerFirst} (sends when you’re back online)"
            : m.ReplyTo is not null ? $"Reply to {m.OwnerFirst} · Esc cancels"
            : m.Settings.TextLarge ? $"Message {m.OwnerFirst}…"
            : "Message · Shift+Enter for a new line";
        AutomationProperties.SetName(Input, "Message " + m.OwnerName);
        AutomationProperties.SetHelpText(Input, "Enter sends, Shift+Enter makes a new line");
        RenderBackground(m);
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
        RenderLists(m);
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

    /// <summary>The mouse wheel moves the quick replies sideways (a row of chips has nothing to scroll up or down).</summary>
    private void OnQuickWheel(object sender, MouseWheelEventArgs e)
    {
        if (QuickScroll.ScrollableWidth <= 0)
        {
            return;
        }
        QuickScroll.ScrollToHorizontalOffset(QuickScroll.HorizontalOffset - e.Delta / 3.0);
        e.Handled = true;
    }

    /// <summary>
    /// The starred quick replies answer the owner: shown when they spoke last, folded away while the member types or
    /// replies (and when nothing is starred).
    /// </summary>
    private void UpdateQuick(TrayModel m, IReadOnlyList<ThreadRow> rows)
    {
        var favourites = m.QuickFavourites;
        var key = string.Join("|", favourites);
        if (key != _quickRow)
        {
            _quickRow = key;
            QuickRow.Children.Clear();
            foreach (var q in favourites)
            {
                QuickRow.Children.Add(Ui.Button(q, "Z.QuickReply", () => Model?.SendQuick(q), "Send: " + q));
            }
        }
        var last = rows.Count > 0 ? rows[^1] : null;
        var show = last is { Mine: false, Kind: ThreadKind.Text or ThreadKind.Task } && Input.Text.Length == 0 && m.ReplyTo is null
            && favourites.Count > 0;
        QuickHint.Visibility = show && !m.QuickOpen ? Visibility.Visible : Visibility.Collapsed;
        QuickGlyph.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, show || m.QuickOpen ? "Z.Ink" : "Z.Muted");
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

    /// <summary>The chat's background (Chat options), redrawn when it or the look (dark, High Contrast) changes.</summary>
    private void RenderBackground(TrayModel m)
    {
        var key = $"{m.ChatPattern}|{HighContrastTheme.IsDark}|{SystemParameters.HighContrast}";
        if (key == _background)
        {
            return;
        }
        _background = key;
        Scroller.Background = ChatPatternBrush.For(m.ChatPattern);
    }

    /// <summary>⚡'s list (a star per reply) and 🙂's emoji, over the composer while open.</summary>
    private void RenderLists(TrayModel m)
    {
        QuickPanel.Visibility = m.QuickOpen ? Visibility.Visible : Visibility.Collapsed;
        EmojiPanel.Visibility = m.EmojiOpen ? Visibility.Visible : Visibility.Collapsed;
        if (m.QuickOpen)
        {
            QuickButton.SetResourceReference(BackgroundProperty, "Z.Fill");
        }
        else
        {
            QuickButton.Background = Brushes.Transparent;
        }
        if (m.EmojiOpen)
        {
            EmojiButton.SetResourceReference(BackgroundProperty, "Z.Fill");
        }
        else
        {
            EmojiButton.Background = Brushes.Transparent;
        }
        EmojiGlyph.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, m.EmojiOpen ? "Z.Ink" : "Z.Muted");
        AutomationProperties.SetItemStatus(QuickButton, m.QuickOpen ? "expanded" : "collapsed");
        AutomationProperties.SetItemStatus(EmojiButton, m.EmojiOpen ? "expanded" : "collapsed");
        QuickNoteText.Text = m.QuickNote ?? "";
        QuickNoteText.Visibility = m.QuickNote is null ? Visibility.Collapsed : Visibility.Visible;

        var favourites = m.QuickFavourites;
        var key = m.QuickOpen + "|" + string.Join("|", favourites);
        if (key == _quickList)
        {
            return;
        }
        var opened = m.QuickOpen && !_quickList.StartsWith("True", StringComparison.Ordinal);
        _quickList = key;
        QuickList.Children.Clear();
        if (!m.QuickOpen)
        {
            return;
        }
        foreach (var q in QuickReplies.All)
        {
            QuickList.Children.Add(QuickLine(q, favourites.Contains(q)));
        }
        if (opened)
        {
            Motion.Arrive(QuickPanel, 0, 8, 1, 140);
        }
    }

    /// <summary>One quick reply in ⚡'s list: its star (into or out of the row), then the reply itself (sends it).</summary>
    private FrameworkElement QuickLine(string reply, bool starred)
    {
        var star = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M12 3.5l2.6 5.3 5.9.9-4.3 4.1 1 5.8L12 16.9l-5.2 2.7 1-5.8-4.3-4.1 5.9-.9z"), Width = 16, Height = 16,
            Stretch = Stretch.Uniform, StrokeThickness = 1.6, StrokeLineJoin = PenLineJoin.Round,
        };
        star.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, starred ? "Z.Orange" : "Z.Muted");
        if (starred)
        {
            star.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "Z.Orange");
        }
        var starButton = Ui.Button(star, "Z.IconBtn", () => Model?.StarQuick(reply), (starred ? "Remove from the row: " : "Add to the row: ") + reply);
        starButton.Width = starButton.Height = starButton.MinWidth = starButton.MinHeight = 34;
        AutomationProperties.SetItemStatus(starButton, starred ? "starred" : "not starred");

        var send = Ui.Button(Ui.Text(reply, 13), "Z.MenuRow", () => Model?.SendQuick(reply), "Send: " + reply);
        send.MinHeight = 34;
        send.Padding = new Thickness(8, 0, 8, 0);
        var line = new DockPanel();
        DockPanel.SetDock(starButton, Dock.Left);
        line.Children.Add(starButton);
        line.Children.Add(send);
        return line;
    }

    /// <summary>One key of 🙂's grid: adds its emoji where the member is typing.</summary>
    private Button EmojiKey(string emoji)
    {
        var face = new TextBlock
        {
            Text = emoji, FontSize = 19, FontFamily = new FontFamily("Segoe UI Emoji"), HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var b = Ui.Button(face, "Z.MenuRow", () => Insert(emoji), emoji);
        b.MinHeight = 36;
        b.Height = 36;
        b.Padding = new Thickness(0);
        b.HorizontalContentAlignment = HorizontalAlignment.Center;
        return b;
    }

    /// <summary>Puts text at the caret (over a selection), and the keyboard back in the message.</summary>
    private void Insert(string text)
    {
        if (Input.Text.Length + text.Length > Input.MaxLength)
        {
            return;
        }
        var at = Math.Clamp(Input.SelectionStart, 0, Input.Text.Length);
        var length = Math.Clamp(Input.SelectionLength, 0, Input.Text.Length - at);
        Input.Text = Input.Text.Remove(at, length).Insert(at, text);
        Input.SelectionStart = at + text.Length;
        Input.SelectionLength = 0;
        Input.Focus();
    }

    private static bool IsInside(DependencyObject node, DependencyObject ancestor)
    {
        for (var d = node; d is not null; d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
        {
            if (d == ancestor)
            {
                return true;
            }
        }
        return false;
    }

    private void OnQuickButton(object sender, RoutedEventArgs e) => Model?.ToggleQuick();

    private void OnEmojiButton(object sender, RoutedEventArgs e) => Model?.ToggleEmoji();

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

    private static FrameworkElement SysLine(ThreadRow row) => NoticeLine(row.Text, row.ToneNo ? "Z.Warn" : "Z.Ok");

    /// <summary>
    /// A centred notice line ("About your Lunch break at 11:07 PM: …") with its tone dot: it wraps onto up to three lines
    /// (then an ellipsis, the whole line in the tooltip), never cut at the window's edge. Also used in team threads.
    /// </summary>
    internal static FrameworkElement NoticeLine(string text, string? dot, double maxWidth = 300)
    {
        var words = Ui.Text(text, 12, FontWeights.Medium, "Z.Muted", wrap: true);
        words.TextAlignment = TextAlignment.Center;
        words.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
        words.MaxHeight = words.LineHeight * 3;
        words.TextTrimming = TextTrimming.CharacterEllipsis;
        words.ToolTip = text;
        var line = new Grid { HorizontalAlignment = HorizontalAlignment.Center, MaxWidth = maxWidth };
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        if (dot is not null)
        {
            var d = Ui.Dot(7, dot);
            d.Margin = new Thickness(0, 5, 6, 0);
            d.VerticalAlignment = VerticalAlignment.Top;
            line.Children.Add(d);
        }
        Grid.SetColumn(words, 1);
        line.Children.Add(words);
        AutomationProperties.SetName(line, text);
        return line;
    }

    private FrameworkElement Message(TrayModel m, ThreadRow row, string ownerInitials)
    {
        var side = row.Mine ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        var wrap = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = side, Background = Brushes.Transparent };
        if (row.Kind == ThreadKind.Task && row.Task is { } task)
        {
            // v1.4: the task card with its thread footer (last reply, replies, Open task chat, reply box), at the left
            // margin and up to 300 px wide like the prototype (v1.4.2: no longer pushed past the window's right edge).
            var item = (task.RemoteId is { } rid ? m.ItemByRemote(rid) : null) ?? m.Item(task.Id);
            return item is not null ? TaskCardV2.Build(item, m, thread: true) : new Border();
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
        column.Children.Add(MetaLine(m, row));
        wrap.Children.Add(column);
        AutomationProperties.SetName(wrap, $"{(row.Mine ? "You" : m.OwnerFirst)}: {row.Snippet}. {row.Meta}");
        return wrap;
    }

    /// <summary>The line under a bubble: the time and the plain-text Reply, or "Not sent · Retry · Delete".</summary>
    private static FrameworkElement MetaLine(TrayModel m, ThreadRow row)
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

        // F-14: Reply shows under every bubble, the member's own too (it used to wait for the mouse there).
        line.Children.Add(reply);
        line.Children.Add(Dot());
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

        var corners = row.Mine ? new CornerRadius(12, 12, 4, 12) : new CornerRadius(12, 12, 12, 4);
        var bubble = new Border
        {
            MaxWidth = 288, Child = stack, Opacity = row.Pending ? 0.7 : 1, Padding = new Thickness(12, 8, 12, 8), CornerRadius = corners,
            HorizontalAlignment = row.Mine ? HorizontalAlignment.Right : HorizontalAlignment.Left,
        };
        if (row.Mine)
        {
            bubble.SetResourceReference(Border.BackgroundProperty, "Z.OrangeTint"); // yours: the light orange, dark text
            return bubble;
        }
        // Theirs: white with a hairline and a soft lift, so it reads on the patterned background.
        bubble.BorderThickness = new Thickness(1);
        bubble.Padding = new Thickness(11, 7, 11, 7);
        bubble.SetResourceReference(Border.BackgroundProperty, "Z.Surface");
        bubble.SetResourceReference(Border.BorderBrushProperty, "Z.Line");
        var lifted = new Grid { HorizontalAlignment = HorizontalAlignment.Left, MaxWidth = 288 };
        lifted.Children.Add(new Border { CornerRadius = corners, Margin = new Thickness(0, 1, 0, -1), Background = Lift, IsHitTestVisible = false });
        lifted.Children.Add(bubble);
        return lifted;
    }

    // The soft shadow under their bubbles (a second shape, not an Effect, so the text keeps its ClearType).
    private static readonly Brush Lift = Frozen(Color.FromArgb(0x14, 0x14, 0x12, 0x10));

    private static SolidColorBrush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
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

    /// <summary>The thread was opened (from the list, a notice, or the Chat tab): it shows the latest lines.</summary>
    public void ShowLatest()
    {
        _atBottom = true;
        _unseen = 0;
        RenderJump();
        Dispatcher.BeginInvoke(() => Scroller.ScrollToEnd(), DispatcherPriority.Loaded);
    }

    private void OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        // v1.4.2: the list got shorter (the quick replies folding in, the reply bar, a taller composer) or longer while
        // the member was at the end: stay at the end, so the last line never hides behind the composer.
        if ((e.ViewportHeightChange != 0 || e.ExtentHeightChange != 0) && _atBottom)
        {
            if (Scroller.VerticalOffset < Scroller.ScrollableHeight)
            {
                Scroller.ScrollToEnd();
            }
            return;
        }
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
        Composer.Padding = new Thickness(on ? 5 : 6); // the thicker border must not move the text
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

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
/// replies and reply-with-quote. Enter sends, Shift+Enter is a new line, Esc cancels the reply.
/// </summary>
public partial class ChatView : UserControl
{
    private static readonly string[] Quick = ["On it", "Thanks!", "Running 10 min late", "Can we talk?"];
    private string _signature = "";
    private bool _atBottom = true;
    private readonly Dictionary<string, FrameworkElement> _rows = [];

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
        }
        if (Input.Text != m.Draft && !Input.IsKeyboardFocused)
        {
            Input.Text = m.Draft;
        }
        SendError.Text = m.SendError ?? "";
        SendError.Visibility = m.SendError is null ? Visibility.Collapsed : Visibility.Visible;

        var rows = m.Thread;
        var signature = string.Join("|", rows.Select(r => r.Key + "@" + r.Meta + "@" + r.Task?.Status + r.Task?.IsNew
            + string.Concat(r.Task?.Steps.Select(s => s.Done ? "1" : "0") ?? []) + r.Task?.Notes.Count + "@" + r.Task?.Activity.Count + r.Task?.Queued))
            + "#" + m.OpenCard + "#" + m.MyPicture.Stamp + "#" + m.Now.ToLocalTime().Date.ToString("yyyyMMdd") + "#" + m.OwnerName;
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
    }

    private void RenderRows(TrayModel m, IReadOnlyList<ThreadRow> rows)
    {
        Log.Children.Clear();
        _rows.Clear();
        var meInitials = Avatars.Initials(m.Profile.DisplayName == "You" ? null : m.Profile.DisplayName);
        var ownerInitials = Avatars.Initials(m.Status?.OwnerName);

        foreach (var row in rows)
        {
            FrameworkElement el = row.Kind switch
            {
                ThreadKind.Day => DayChip(row.Text),
                ThreadKind.Sys => SysLine(row),
                _ => Message(m, row, meInitials, ownerInitials),
            };
            el.Margin = new Thickness(0, row.Kind == ThreadKind.Day ? 6 : 0, 0, 10);
            _rows[row.Key] = el;
            Log.Children.Add(el);
        }
    }

    private static FrameworkElement DayChip(string text)
    {
        var chip = new Border
        {
            CornerRadius = new CornerRadius(12), Padding = new Thickness(12, 4, 12, 4), HorizontalAlignment = HorizontalAlignment.Center,
            Child = Ui.Text(text, 12, FontWeights.SemiBold, "Z.Ink2"),
        };
        chip.SetResourceReference(Border.BackgroundProperty, "Z.Fill");
        return chip;
    }

    private static FrameworkElement SysLine(ThreadRow row)
    {
        var text = Ui.Text(row.Text, 12, FontWeights.SemiBold, row.ToneNo ? "Z.Warn" : "Z.Ok", wrap: true);
        text.TextAlignment = TextAlignment.Center;
        var chip = new Border
        {
            CornerRadius = new CornerRadius(12), Padding = new Thickness(10, 4, 10, 4), HorizontalAlignment = HorizontalAlignment.Center,
            MaxWidth = 320, Child = text,
        };
        chip.SetResourceReference(Border.BackgroundProperty, row.ToneNo ? "Z.WarnTint" : "Z.OkTint");
        AutomationProperties.SetName(chip, row.Text);
        return chip;
    }

    private FrameworkElement Message(TrayModel m, ThreadRow row, string meInitials, string ownerInitials)
    {
        var wrap = new StackPanel { HorizontalAlignment = row.Mine ? HorizontalAlignment.Right : HorizontalAlignment.Left };
        var line = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = row.Mine ? HorizontalAlignment.Right : HorizontalAlignment.Left };

        FrameworkElement avatar = row.HasAvatar
            ? row.Mine ? Ui.Avatar(m.MyPicture, meInitials, 26) : Ui.Avatar(null, ownerInitials, 26, "#FDEBDC")
            : new Border { Width = 26 };
        avatar.VerticalAlignment = VerticalAlignment.Bottom;
        avatar.Margin = new Thickness(row.Mine ? 4 : 0, 0, row.Mine ? 0 : 4, 0);

        FrameworkElement body = row.Kind == ThreadKind.Task && row.Task is { } task
            ? TaskCard.Build(task, m, m.OpenCard == task.Id, 270)
            : Bubble(row, m);

        var reply = new Button
        {
            Style = (Style)FindResource("Z.IconBtn"), Width = 40, Height = 40, Opacity = 0.5, VerticalAlignment = VerticalAlignment.Center,
            Content = Ui.Glyph(Ui.Icon.Reply, 16, "Z.Ink3"), ToolTip = "Reply",
        };
        AutomationProperties.SetName(reply, "Reply to " + (row.Mine ? "your message" : m.OwnerFirst) + ": " + Cut(row.Snippet, 60));
        reply.MouseEnter += (_, _) => reply.Opacity = 1;
        reply.MouseLeave += (_, _) => reply.Opacity = reply.IsKeyboardFocused ? 1 : 0.5;
        reply.GotKeyboardFocus += (_, _) => reply.Opacity = 1;
        reply.LostKeyboardFocus += (_, _) => reply.Opacity = 0.5;
        reply.Click += (_, _) => m.StartReply(row);

        if (row.Mine)
        {
            line.Children.Add(reply);
            line.Children.Add(body);
            line.Children.Add(avatar);
        }
        else
        {
            line.Children.Add(avatar);
            line.Children.Add(body);
            line.Children.Add(reply);
        }
        wrap.Children.Add(line);
        if (row.Meta.Length > 0)
        {
            var meta = Ui.Text(row.Meta, 12, fg: "Z.Muted");
            meta.Margin = row.Mine ? new Thickness(0, 3, 30, 0) : new Thickness(30, 3, 0, 0);
            meta.HorizontalAlignment = row.Mine ? HorizontalAlignment.Right : HorizontalAlignment.Left;
            wrap.Children.Add(meta);
        }
        AutomationProperties.SetName(wrap, $"{(row.Mine ? "You" : m.OwnerFirst)}: {row.Snippet}. {row.Meta}");
        return wrap;
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
            qt.Children.Add(Ui.Text(q.Who, 12, FontWeights.Bold, row.Mine ? "Z.OnOrange" : "Z.Ink"));
            var qtext = Ui.Text(q.Text, 12, fg: row.Mine ? "Z.OnOrange" : "Z.Ink2", trim: true);
            qtext.MaxWidth = 220;
            qt.Children.Add(qtext);
            var qb = new Button
            {
                Content = qt, Cursor = Cursors.Hand, HorizontalAlignment = HorizontalAlignment.Left, MinHeight = 40, Margin = new Thickness(0, 0, 0, 5),
                Template = QuoteTemplate(row.Mine), FocusVisualStyle = (Style)FindResource("Z.FocusRing"),
            };
            AutomationProperties.SetName(qb, $"Show the message you replied to, from {(q.Who == "You" ? "you" : q.Who)}: {Cut(q.Text, 60)}");
            qb.Click += (_, _) => JumpTo(q.TargetKey);
            stack.Children.Add(qb);
        }
        var text = new TextBlock
        {
            FontSize = 13.5, TextWrapping = TextWrapping.Wrap, LineHeight = 19, Margin = row.Quote is null ? new Thickness(0) : new Thickness(8, 0, 8, 0),
        };
        ChatLinks.SetText(text, row.Text);
        text.SetResourceReference(TextBlock.ForegroundProperty, row.Mine ? "Z.OnOrange" : "Z.Ink");
        stack.Children.Add(text);

        var bubble = new Border
        {
            MaxWidth = 260, Child = stack, Opacity = row.Pending ? 0.7 : 1,
            Padding = row.Quote is null ? new Thickness(13, 9, 13, 9) : new Thickness(5, 5, 5, 9),
            CornerRadius = row.Mine ? new CornerRadius(18, 18, 6, 18) : new CornerRadius(18, 18, 18, 6),
            BorderThickness = new Thickness(row.Mine ? 0 : 1),
        };
        bubble.SetResourceReference(Border.BackgroundProperty, row.Mine ? "Z.Orange" : "Z.Surface");
        bubble.SetResourceReference(Border.BorderBrushProperty, "Z.Line");
        return bubble;
    }

    private static ControlTemplate QuoteTemplate(bool mine)
    {
        var t = new ControlTemplate(typeof(Button));
        var b = new FrameworkElementFactory(typeof(Border));
        b.SetValue(Border.CornerRadiusProperty, new CornerRadius(12));
        b.SetValue(Border.PaddingProperty, new Thickness(10, 5, 10, 5));
        if (mine)
        {
            b.SetValue(Border.BackgroundProperty, new SolidColorBrush(Color.FromArgb(0x8C, 0xFF, 0xFF, 0xFF)));
        }
        else
        {
            b.SetResourceReference(Border.BackgroundProperty, "Z.Fill");
        }
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

    private void OnScrollChanged(object sender, ScrollChangedEventArgs e) =>
        _atBottom = Scroller.VerticalOffset >= Scroller.ScrollableHeight - 24;

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
        }
        SendButton.IsEnabled = !string.IsNullOrWhiteSpace(Input.Text);
    }

    private void OnInputFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        Composer.SetResourceReference(Border.BorderBrushProperty, Input.IsKeyboardFocused ? "Z.Focus" : "Z.Line2");
        Composer.BorderThickness = new Thickness(Input.IsKeyboardFocused ? 2 : 1);
    }

    private void OnSend(object sender, RoutedEventArgs e) => Send();

    private void Send()
    {
        if (Model?.Send(Input.Text) == true)
        {
            Input.Clear();
            _atBottom = true;
            Dispatcher.BeginInvoke(() => Scroller.ScrollToEnd(), DispatcherPriority.Background);
        }
    }

    private void OnCancelReply(object sender, RoutedEventArgs e) => Model?.CancelReply();
}

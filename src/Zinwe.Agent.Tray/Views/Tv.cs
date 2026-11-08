using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Zinwe.Agent.Core.Tasks;

namespace Zinwe.Agent.Tray.Views;

/// <summary>
/// v1.4 · small builders shared by the task cards, the member task panel and the chat (the prototype's .tv-* pieces):
/// tone colours, the status pill, the due pill, the sentence with its dot, the journey bar, faces, the chat count and the
/// task buttons. Every colour is a Z.* token (dark mode and High Contrast follow).
/// </summary>
internal static class Tv
{
    public const string IcoChat = "M21 12a8 8 0 0 1-11.5 7.2L4 20l1-4.5A8 8 0 1 1 21 12Z";
    public const string IcoMore = "M5 12h.01M12 12h.01M19 12h.01";
    public const string IcoX = "M6 6l12 12M18 6 6 18";
    public const string IcoSend = "M5 12h14M13 6l6 6-6 6";
    public const string IcoPlus = "M12 5v14M5 12h14";
    public const string IcoGroup = "M16 19v-1a4 4 0 0 0-4-4H7a4 4 0 0 0-4 4v1M9.5 10a3 3 0 1 0 0-6 3 3 0 0 0 0 6zM21 19v-1a4 4 0 0 0-3-3.9M15.5 4.1a3 3 0 0 1 0 5.8";
    public const string IcoPin = "M12 17v5M9 3h6l-1 6 3 3H7l3-3z";
    public const string IcoBack = "m15 18-6-6 6-6";
    public const string IcoReply = "M9 14 4 9l5-5M4 9h10.5a5.5 5.5 0 0 1 0 11H11";
    public const string IcoFlag = "M5 21V4M5 4.5h11l-2.2 4 2.2 4H5";
    public const string IcoClock = "M12 4a8 8 0 1 0 0 16 8 8 0 0 0 0-16zM12 7.5V12l3 2";
    public const string IcoTick = "M20 6 9 17l-5-5";

    /// <summary>The edge / dot colour of a tone.</summary>
    public static string Edge(TaskTone t) => t switch
    {
        TaskTone.Needs => "Z.Inv",
        TaskTone.Work => "Z.Orange",
        TaskTone.Red => "Z.RedText",
        TaskTone.Ok => "Z.Ok",
        _ => "Z.Faint",
    };

    /// <summary>The pill's (background, text) of a tone.</summary>
    public static (string Bg, string Fg) PillKeys(TaskTone t) => t switch
    {
        TaskTone.Needs => ("Z.Inv", "Z.OnInv"),
        TaskTone.Work => ("Z.OrangeTint", "Z.TvWorkInk"),
        TaskTone.Red => ("Z.RedTint", "Z.RedText"),
        TaskTone.Ok => ("Z.OkTint", "Z.Ok"),
        _ => ("Z.Fill", "Z.Ink2"),
    };

    /// <summary>What's happening's (background, border).</summary>
    public static (string Bg, string Line) HapKeys(TaskTone t) => t switch
    {
        TaskTone.Needs => ("Z.TvNeedsBg", "Z.Line2"),
        TaskTone.Work => ("Z.TvWorkBg", "Z.TvWorkLine"),
        TaskTone.Red => ("Z.TvRedBg", "Z.TvRedLine"),
        TaskTone.Ok => ("Z.TvOkBg", "Z.TvOkLine"),
        _ => ("Z.Surface", "Z.Line"),
    };

    /// <summary>
    /// A rounded pill with a dot: the status pill ("New", "In progress"). A fixed height with half of it as the corner
    /// (v1.4.2: a 999 corner made WPF draw an ellipse); <paramref name="compact"/> = the window's task card (.tvw-card:
    /// 20 px, 11 px text).
    /// </summary>
    public static Border Pill(string label, TaskTone tone, double size = 12, bool compact = false)
    {
        var (bg, fg) = PillKeys(tone);
        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var dot = Ui.Dot(compact ? 6 : 7, fg);
        dot.Margin = new Thickness(0, 0, compact ? 5 : 6, 0);
        dot.VerticalAlignment = VerticalAlignment.Center;
        row.Children.Add(dot);
        var t = Ui.Text(label, compact ? 11 : size, FontWeights.SemiBold, fg);
        t.VerticalAlignment = VerticalAlignment.Center;
        row.Children.Add(t);
        return PillBox(row, bg, compact);
    }

    /// <summary>The pill shape: 22 px (20 compact), half the height as the corner, 10 px sides (7 compact).</summary>
    private static Border PillBox(UIElement content, string bg, bool compact, double side = 10)
    {
        var h = compact ? 20 : 22;
        return new Border
        {
            Height = h, CornerRadius = new CornerRadius(h / 2.0), Padding = new Thickness(compact ? 7 : side, 0, compact ? 7 : side, 0), Child = content,
            VerticalAlignment = VerticalAlignment.Center,
        }.Res(Border.BackgroundProperty, bg);
    }

    /// <summary>The due pill: today in the warm tint, late in red, else grey. Empty text = nothing.</summary>
    public static Border? Due(TaskDue due, bool icon = false, bool compact = false)
    {
        if (due.Short.Length == 0)
        {
            return null;
        }
        var (bg, fg) = due.Kind switch { "today" => ("Z.TvTodayBg", "Z.TvWorkInk"), "over" => ("Z.RedTint", "Z.RedText"), _ => ("Z.Fill", "Z.Ink2") };
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        if (icon)
        {
            var g = Ui.Glyph(IcoClock, 13, fg, 2.2);
            g.Margin = new Thickness(0, 0, 6, 0);
            row.Children.Add(g);
        }
        var text = Ui.Text(icon ? due.Chip : due.Short, compact ? 11 : 12, FontWeights.SemiBold, fg);
        text.VerticalAlignment = VerticalAlignment.Center;
        row.Children.Add(text);
        row.VerticalAlignment = VerticalAlignment.Center;
        return PillBox(row, bg, compact);
    }

    public static Border High(bool compact = false)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var g = Ui.Glyph(IcoFlag, compact ? 11 : 12, "Z.RedText", 2.2);
        g.Margin = new Thickness(0, 0, compact ? 4 : 5, 0);
        g.VerticalAlignment = VerticalAlignment.Center;
        row.Children.Add(g);
        var t = Ui.Text("High", compact ? 11 : 12, FontWeights.SemiBold, "Z.RedText");
        t.VerticalAlignment = VerticalAlignment.Center;
        row.Children.Add(t);
        return PillBox(row, "Z.RedTint", compact, side: 9);
    }

    /// <summary>The card's sentence: a tone dot and the words (bold for something new, red when it needs a look).</summary>
    public static FrameworkElement Say(string text, TaskTone tone, bool wrap = true, double size = 12.5)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        var dot = Ui.Dot(7, Edge(tone));
        dot.Margin = new Thickness(0, 0, 7, 0);
        dot.VerticalAlignment = VerticalAlignment.Top;
        dot.Margin = new Thickness(0, Math.Max(0, (Math.Round(size * 1.45) - 7) / 2), 7, 0); // centred on the first line
        grid.Children.Add(dot);
        var fg = tone switch { TaskTone.Red => "Z.RedText", TaskTone.Needs => "Z.Ink", _ => "Z.Ink2" };
        var t = Ui.Text(text, size, tone == TaskTone.Needs ? FontWeights.SemiBold : FontWeights.Normal, fg, wrap: wrap, trim: !wrap);
        if (!wrap)
        {
            t.LineHeight = Math.Round(size * 1.45);
        }
        Grid.SetColumn(t, 1);
        grid.Children.Add(t);
        return grid;
    }

    /// <summary>The five-piece journey bar (Sent · Seen · Working · Done · OK), stretched to its width.</summary>
    public static Grid JourneyBar(IReadOnlyList<JourneyStep> steps, double height = 5)
    {
        var grid = new Grid();
        for (var i = 0; i < steps.Count; i++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            var seg = new Border { Height = height, CornerRadius = new CornerRadius(height / 2), Margin = new Thickness(i == 0 ? 0 : 1.5, 0, i == steps.Count - 1 ? 0 : 1.5, 0) }
                .Res(Border.BackgroundProperty, JourneyKey(steps[i].State));
            Grid.SetColumn(seg, i);
            grid.Children.Add(seg);
        }
        AutomationProperties.SetName(grid, "Journey: " + string.Join(", ", steps.Select(s => s.Label + (s.State is JourneyState.Now ? " (now)" : ""))));
        return grid;
    }

    public static string JourneyKey(JourneyState s) => s switch
    {
        JourneyState.Past => "Z.TvPast",
        JourneyState.Now => "Z.Orange",
        JourneyState.Red => "Z.RedText",
        JourneyState.Ok => "Z.Ok",
        _ => "Z.TvTrack",
    };

    /// <summary>A small initials face (22 px on cards).</summary>
    public static Border Face(string initials, double size = 22, string bg = "Z.Fill", string fg = "Z.Ink2")
    {
        var face = new Border
        {
            Width = size, Height = size, CornerRadius = new CornerRadius(size / 2), BorderThickness = new Thickness(1.5),
            Child = new TextBlock
            {
                Text = initials, FontSize = Math.Max(9, Math.Round(size * 0.4)), FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            },
        }.Res(Border.BackgroundProperty, bg).Res(Border.BorderBrushProperty, "Z.Surface");
        ((TextBlock)face.Child).SetResourceReference(TextBlock.ForegroundProperty, fg);
        return face;
    }

    /// <summary>Up to three overlapping faces and "+N".</summary>
    public static StackPanel Faces(IReadOnlyList<string> initials, double size = 22)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        for (var i = 0; i < Math.Min(3, initials.Count); i++)
        {
            var f = Face(initials[i], size);
            f.Margin = new Thickness(i == 0 ? 0 : -7, 0, 0, 0);
            row.Children.Add(f);
        }
        if (initials.Count > 3)
        {
            var more = Face("+" + (initials.Count - 3), size);
            more.Margin = new Thickness(-7, 0, 0, 0);
            row.Children.Add(more);
        }
        return row;
    }

    /// <summary>A task button (.tv-btn): orange primary with ink text, or white.</summary>
    public static Button Btn(string label, bool primary, Action run, string? aria = null, bool small = false, bool compact = false)
    {
        // compact = the window's task card (.tvw-card .tv-btn: 28 px, 12 px text, 8 px corners).
        var style = compact ? primary ? "Z.TvBtnXs.Pri" : "Z.TvBtnXs"
            : small ? primary ? "Z.TvBtnSm.Pri" : "Z.TvBtnSm" : primary ? "Z.TvBtn.Pri" : "Z.TvBtn";
        var b = new Button { Content = label, Style = (Style)Application.Current.FindResource(style) };
        b.Click += (_, e) =>
        {
            e.Handled = true;
            run();
        };
        AutomationProperties.SetName(b, aria ?? label);
        return b;
    }

    /// <summary>A bare clickable wrapper around <paramref name="content"/>.</summary>
    public static Button Bare(UIElement content, Action run, string aria, string? tip = null)
    {
        var b = new Button { Content = content, Style = (Style)Application.Current.FindResource("Z.TvBare") };
        b.Click += (_, e) =>
        {
            e.Handled = true;
            run();
        };
        AutomationProperties.SetName(b, aria);
        if (tip is not null)
        {
            b.ToolTip = tip;
        }
        return b;
    }

    /// <summary>A round icon button (34 px look, 40 px hit area): chat, ⋯, ×.</summary>
    public static Button Icon(string data, Action run, string aria, string fill = "Transparent", double glyph = 16, string stroke = "Z.Ink2",
        double thickness = 2)
    {
        var disc = new Border { Width = 34, Height = 34, CornerRadius = new CornerRadius(17), Child = Ui.Glyph(data, glyph, stroke, thickness) };
        if (fill != "Transparent")
        {
            disc.SetResourceReference(Border.BackgroundProperty, fill);
        }
        else
        {
            disc.Background = Brushes.Transparent;
        }
        var b = Bare(new Grid { Width = 40, Height = 40, Background = Brushes.Transparent, Children = { disc } }, run, aria, aria);
        b.MouseEnter += (_, _) =>
        {
            if (fill == "Transparent")
            {
                disc.SetResourceReference(Border.BackgroundProperty, "Z.Hover");
            }
        };
        b.MouseLeave += (_, _) =>
        {
            if (fill == "Transparent")
            {
                disc.Background = Brushes.Transparent;
            }
        };
        return b;
    }

    /// <summary>The task chat count on a card: a speech icon and the number, an orange dot while something is unread.</summary>
    public static Button ChatCount(int count, int unread, Action run, bool compact = false)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var icon = new Grid { VerticalAlignment = VerticalAlignment.Center };
        var ink = unread > 0 && compact ? "Z.OrangeInk" : "Z.Ink2";
        icon.Children.Add(Ui.Glyph(IcoChat, 14, ink, 2));
        row.Children.Add(icon);
        if (count > 0)
        {
            var n = Ui.Text(count.ToString(System.Globalization.CultureInfo.InvariantCulture), compact ? 11.5 : 12.5, FontWeights.SemiBold,
                compact ? ink : unread > 0 ? "Z.Ink" : "Z.Ink2");
            n.Margin = new Thickness(compact ? 4 : 5, 0, 0, 0);
            n.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(n);
        }
        // .tv-cc: an unread chat is tinted, with the orange dot on its corner.
        var face = new Border { Padding = new Thickness(compact ? 6 : 8, 0, compact ? 6 : 8, 0), CornerRadius = new CornerRadius(8), Child = row, Background = Brushes.Transparent };
        if (compact)
        {
            face.Height = 24;
            face.VerticalAlignment = VerticalAlignment.Center;
            if (unread > 0)
            {
                face.SetResourceReference(Border.BackgroundProperty, "Z.OrangeTint");
            }
        }
        else
        {
            face.MinHeight = 36;
        }
        var host = new Grid { Background = Brushes.Transparent, MinHeight = compact ? 32 : 36 };
        host.Children.Add(face);
        if (unread > 0)
        {
            var dot = new Border
            {
                Width = 9, Height = 9, CornerRadius = new CornerRadius(4.5), BorderThickness = new Thickness(1.5), HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = compact ? VerticalAlignment.Center : VerticalAlignment.Top, Margin = compact ? new Thickness(0, -16, 1, 0) : new Thickness(0, 6, 4, 0),
                IsHitTestVisible = false,
            }.Res(Border.BackgroundProperty, "Z.Orange").Res(Border.BorderBrushProperty, "Z.Surface");
            host.Children.Add(dot);
        }
        var b = Bare(host, run,
            $"Task chat: {count} {(count == 1 ? "message" : "messages")}" + (unread > 0 ? $", {unread} new" : ""), "Task chat");
        b.VerticalAlignment = VerticalAlignment.Center;
        return b;
    }

    /// <summary>A heading in caps ("PEOPLE · 2", "STEPS · 1 OF 3 DONE · IN ORDER").</summary>
    public static TextBlock Caps(string text)
    {
        var t = Ui.Text(text.ToUpperInvariant(), 11.5, FontWeights.SemiBold, "Z.Muted");
        t.Margin = new Thickness(0, 0, 0, 8);
        return t;
    }

    /// <summary>A rounded text box (task chat composer, notes, the reply box on a card).</summary>
    public static TextBox Field(string placeholder, bool multiLine = false, string? aria = null)
    {
        var box = new TextBox
        {
            Style = (Style)Application.Current.FindResource("Z.TvField"), Tag = placeholder, AcceptsReturn = false,
            TextWrapping = multiLine ? TextWrapping.Wrap : TextWrapping.NoWrap, MaxLength = 2000,
            VerticalScrollBarVisibility = multiLine ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled,
        };
        if (multiLine)
        {
            box.MinHeight = 64;
            box.MaxHeight = 120;
            box.VerticalContentAlignment = VerticalAlignment.Top;
        }
        AutomationProperties.SetName(box, aria ?? placeholder);
        return box;
    }

    /// <summary>Enter sends, Shift+Enter makes a new line.</summary>
    public static void EnterSends(TextBox box, Action<string> send)
    {
        box.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0)
            {
                e.Handled = true;
                var text = box.Text;
                if (!string.IsNullOrWhiteSpace(text))
                {
                    box.Text = "";
                    send(text);
                }
            }
            else if (e.Key == Key.Enter)
            {
                e.Handled = true;
                var at = box.CaretIndex;
                box.Text = box.Text.Insert(at, Environment.NewLine);
                box.CaretIndex = at + Environment.NewLine.Length;
            }
        };
    }

    /// <summary>The round orange send button.</summary>
    public static Button Send(Action run, string aria = "Send", double size = 36)
    {
        var disc = new Border { Width = size, Height = size, CornerRadius = new CornerRadius(size / 2), Child = Ui.Glyph(IcoSend, size * 0.45, "Z.OnOrange", 2.4) }
            .Res(Border.BackgroundProperty, "Z.Orange");
        var b = Bare(disc, run, aria, aria);
        b.VerticalAlignment = VerticalAlignment.Bottom;
        return b;
    }

    /// <summary>
    /// A pill chip with a count (All 7 · Today 5 · Upcoming 2 · Done 1): black when on, outlined when off. Sized like the
    /// prototype's .zw-pchips (32 px, 12.5 px label, 11 px count, 14 px sides), so four chips with two-digit counts fit
    /// the window; the row they sit in (<see cref="ChipRow"/>) scrolls sideways when bigger text makes them wider.
    /// </summary>
    public static Button Chip(string label, int count, bool on, Action pick, bool showZero = false)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var name = Ui.Text(label, 12.5, on ? FontWeights.SemiBold : FontWeights.Normal, on ? "Z.OnInv" : "Z.Ink2");
        name.VerticalAlignment = VerticalAlignment.Center;
        row.Children.Add(name);
        if (count > 0 || showZero)
        {
            var n = Ui.Text(count.ToString(System.Globalization.CultureInfo.InvariantCulture), 11, on ? FontWeights.SemiBold : FontWeights.Normal, on ? "Z.OnInv" : "Z.Muted");
            n.Margin = new Thickness(4, 1, 0, 0);
            n.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(n);
        }
        // A fixed height and half of it as the corner: a true pill (999 made WPF draw an ellipse).
        var pill = new Border { CornerRadius = new CornerRadius(ChipHeight / 2), BorderThickness = new Thickness(1), Padding = new Thickness(14, 0, 14, 0), Height = ChipHeight, MinWidth = 44, Child = row }
            .Res(Border.BackgroundProperty, on ? "Z.Inv" : "Z.Surface").Res(Border.BorderBrushProperty, on ? "Z.Inv" : "Z.Line2");
        row.HorizontalAlignment = HorizontalAlignment.Center;
        var b = Bare(pill, pick, label + (count > 0 ? ", " + count : ""));
        b.MouseEnter += (_, _) =>
        {
            if (!on)
            {
                pill.SetResourceReference(Border.BorderBrushProperty, "Z.Ink2");
            }
        };
        b.MouseLeave += (_, _) =>
        {
            if (!on)
            {
                pill.SetResourceReference(Border.BorderBrushProperty, "Z.Line2");
            }
        };
        b.Margin = new Thickness(0, 0, 6, 0);
        AutomationProperties.SetItemStatus(b, on ? "selected" : "");
        return b;
    }

    public const double ChipHeight = 32;

    /// <summary>
    /// The row the chips sit in: one line that never wraps or clips a chip; when they run wider than the window (bigger
    /// text) it scrolls sideways (the mouse wheel too), without a bar, like the prototype's .zw-pchips.
    /// </summary>
    public static ScrollViewer ChipRow(Panel chips)
    {
        var scroll = new ScrollViewer
        {
            Content = chips, HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Focusable = false, PanningMode = PanningMode.HorizontalOnly,
        };
        scroll.PreviewMouseWheel += (_, e) =>
        {
            if (scroll.ScrollableWidth > 0)
            {
                scroll.ScrollToHorizontalOffset(scroll.HorizontalOffset - e.Delta / 3.0);
                e.Handled = true;
            }
        };
        return scroll;
    }

    /// <summary>A rounded white card with a 3 px tone edge on the left (.tv-card).</summary>
    public static Border EdgeCard(UIElement content, TaskTone tone, double radius = 14)
    {
        var grid = new Grid();
        var edge = new Rectangle { Width = 3, HorizontalAlignment = HorizontalAlignment.Left }.Res(Shape.FillProperty, Edge(tone));
        grid.Children.Add(content);
        grid.Children.Add(edge);
        var card = new Border
        {
            CornerRadius = new CornerRadius(radius), BorderThickness = new Thickness(1), Child = grid, SnapsToDevicePixels = true, ClipToBounds = false,
        }.Res(Border.BackgroundProperty, "Z.Surface").Res(Border.BorderBrushProperty, "Z.Line");
        // Clip the edge to the rounded corners.
        card.SizeChanged += (_, e) => grid.Clip = new RectangleGeometry(new Rect(0, 0, Math.Max(0, e.NewSize.Width - 2), Math.Max(0, e.NewSize.Height - 2)), radius - 1, radius - 1);
        return card;
    }
}


/// <summary>
/// The text boxes of code-built views (a card's reply box, the task chat composer, the panel's note) live across
/// re-renders: the same box is moved into the new tree, so what the member typed, the caret and the focus stay.
/// </summary>
internal static class Inputs
{
    private static readonly Dictionary<string, TextBox> Boxes = new(StringComparer.Ordinal);

    public static TextBox Keep(string key, Func<TextBox> make)
    {
        if (!Boxes.TryGetValue(key, out var box))
        {
            box = make();
            Boxes[key] = box;
        }
        Detach(box);
        return box;
    }

    /// <summary>Forget a box (its card or panel went away for good).</summary>
    public static void Drop(string key) => Boxes.Remove(key);

    public static void Detach(FrameworkElement el)
    {
        switch (el.Parent)
        {
            case Panel p:
                p.Children.Remove(el);
                break;
            case Decorator d:
                d.Child = null;
                break;
            case ContentControl c:
                c.Content = null;
                break;
        }
    }
}

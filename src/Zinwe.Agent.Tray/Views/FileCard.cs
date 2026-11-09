using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Zinwe.Agent.Core.Chat;
using Zinwe.Agent.Core.Messages;

namespace Zinwe.Agent.Tray.Views;

/// <summary>
/// v1.5 · files in chat, drawn the same in the admin chat and in team threads (inside the existing bubbles, with the
/// existing colours, so dark mode and High Contrast follow the theme). Round 2 (files stay on the sender's PC):
/// <list type="bullet">
/// <item>the paperclip opens the attach menu (Document · Photos &amp; videos), each a multi-select picker (up to 10);</item>
/// <item>the send tray above the composer: a tile per file (Windows' thumbnail, a video's length, name, size, ✕) and Add more;</item>
/// <item>a received photo or video is a tile with its thumbnail (a video: a play mark and its length), a document a card with
///   its type icon; Download ▾ → Save as…; while it comes, a simple ring with % and ✕; when the sender's PC is off,
///   "Waiting for Priya's PC…" with a soft pulse; then Open · Show in folder (a photo opens in the viewer);</item>
/// <item>photos and videos sent together: a 2×2 grid, "+N" on the 4th;</item>
/// <item>the member's own file: "Kept on your PC…"; stopped, gone or old: "Not shared any more."</item>
/// </list>
/// </summary>
internal static class FileCard
{
    public const double TileMax = 220;
    public const double TrayTile = 64;

    private const string IcoPlay = "M8 5.5v13l11-6.5z";
    private const string IcoPlus = "M12 5v14M5 12h14";
    private const string IcoDown = "M6 9l6 6 6-6";
    private const string IcoDoc = "M14 3H7a2 2 0 0 0-2 2v14a2 2 0 0 0 2 2h10a2 2 0 0 0 2-2V8zM14 3v5h5M9 13h6M9 17h6";
    private const string IcoPhoto = "M5 4h14a1 1 0 0 1 1 1v14a1 1 0 0 1-1 1H5a1 1 0 0 1-1-1V5a1 1 0 0 1 1-1zM9 10.5a1.5 1.5 0 1 0 0-3 1.5 1.5 0 0 0 0 3zM20 15l-5-5-10 10";

    /// <summary>A stroked 24-unit icon per kind (pdf, doc, sheet, slide, zip, image, video, audio, other).</summary>
    public static string IconData(string kind) => kind switch
    {
        "pdf" or "doc" => IcoDoc,
        "sheet" => "M5 4h14a1 1 0 0 1 1 1v14a1 1 0 0 1-1 1H5a1 1 0 0 1-1-1V5a1 1 0 0 1 1-1zM4 10h16M4 15h16M10 4v16",
        "slide" => "M4 4h16a1 1 0 0 1 1 1v10a1 1 0 0 1-1 1H4a1 1 0 0 1-1-1V5a1 1 0 0 1 1-1zM12 16v4M8 20h8",
        "zip" => "M14 3H7a2 2 0 0 0-2 2v14a2 2 0 0 0 2 2h10a2 2 0 0 0 2-2V8zM14 3v5h5M11 5h1M11 8h1M11 11h1M10.5 14h2v3h-2z",
        "image" => IcoPhoto,
        "video" => "M4 6h10a1 1 0 0 1 1 1v10a1 1 0 0 1-1 1H4a1 1 0 0 1-1-1V7a1 1 0 0 1 1-1zM15 10l6-3v10l-6-3",
        "audio" => "M9 18V5l11-2v13M9 18a3 3 0 1 1-6 0 3 3 0 0 1 6 0zM20 16a3 3 0 1 1-6 0 3 3 0 0 1 6 0z",
        _ => "M14 3H7a2 2 0 0 0-2 2v14a2 2 0 0 0 2 2h10a2 2 0 0 0 2-2V8zM14 3v5h5",
    };

    /// <summary>The type tile at the card's left: the brand tint, a red one for a program file, grey when it is gone.</summary>
    private static Border Tile(string? name, bool risky, bool muted = false, double size = 36) =>
        Ui.Tile(IconData(ChatFiles.IconKind(name)), size, muted ? "Z.Fill" : risky ? "Z.RedTint" : "Z.OrangeTint",
            muted ? "Z.Muted" : risky ? "Z.RedText" : "Z.OrangeInk", 9, size / 2);

    /// <summary>Icon (or the ring while it downloads), name (one line) and "2.1 MB · PDF".</summary>
    private static Grid Head(string name, long size, bool risky, bool muted = false, FrameworkElement? icon = null)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        var tile = icon ?? Tile(name, risky, muted);
        tile.Margin = new Thickness(0, 0, 10, 0);
        tile.VerticalAlignment = VerticalAlignment.Center;
        grid.Children.Add(tile);
        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var title = Ui.Text(name, 13.5, FontWeights.SemiBold, muted ? "Z.Muted" : "Z.Ink", trim: true);
        title.ToolTip = name;
        texts.Children.Add(title);
        if (!muted)
        {
            texts.Children.Add(Ui.Text($"{ChatFiles.FormatSize(size)} · {ChatFiles.TypeLabel(name)}", 12, fg: "Z.Muted"));
        }
        Grid.SetColumn(texts, 1);
        grid.Children.Add(texts);
        return grid;
    }

    // ---- A file on a line ----------------------------------------------------------------------------------------------

    /// <summary>
    /// A file on a line: a photo or video tile, or a document card; then the caution for someone else's program file, the
    /// actions (Download ▾ / the ring / waiting / Open · Show in folder) or, for the member's own, where it is kept.
    /// </summary>
    public static FrameworkElement Line(TrayModel m, ChatFile f, string? senderFirst, bool mine)
    {
        var stack = new StackPanel { MinWidth = 200, MaxWidth = 264 };
        var state = m.DownloadOf(f);
        if (!f.AvailableAt(m.Now) || state is { State: Core.Chat.FileState.Gone })
        {
            stack.Children.Add(NotShared(f.Name));
            AutomationProperties.SetName(stack, $"File {f.Name}. {ChatFiles.NotSharedText}");
            return stack;
        }
        var own = mine && !f.IsLegacy;
        var downloaded = m.DownloadedPath(f);
        var first = f.FirstName(senderFirst);
        if (f.IsMedia)
        {
            stack.Children.Add(MediaTile(m, f, first, mine, state, downloaded, own));
            var caption = Ui.Text($"{f.Name} · {ChatFiles.FormatSize(f.Size)}", 12, fg: "Z.Muted", trim: true);
            caption.ToolTip = f.Name;
            caption.Margin = new Thickness(0, 6, 0, 0);
            stack.Children.Add(caption);
        }
        else
        {
            stack.Children.Add(Head(f.Name, f.Size, f.IsRisky, icon: state is { Busy: true } && !own ? Progress(m, f, state, first, 36) : null));
        }
        if (f.IsRisky && !mine)
        {
            var caution = Ui.Text(ChatFiles.RiskyText(first), 12, FontWeights.SemiBold, "Z.RedText", wrap: true);
            caution.Margin = new Thickness(0, 8, 0, 0);
            stack.Children.Add(caution);
        }
        if (own)
        {
            var kept = Ui.Text(ChatFiles.OwnFileText, 12, fg: "Z.Muted", wrap: true);
            kept.Margin = new Thickness(0, 8, 0, 0);
            stack.Children.Add(kept);
        }
        else
        {
            stack.Children.Add(Actions(m, f, first, mine, state, downloaded));
        }
        AutomationProperties.SetName(stack, $"File {f.Name}, {ChatFiles.FormatSize(f.Size)}."
            + (f.IsVideo && ChatFiles.FormatDuration(f.DurationS) is { Length: > 0 } d ? " Video, " + d + "." : "")
            + (f.IsRisky && !mine ? " " + ChatFiles.RiskyText(first) : "")
            + (own ? " " + ChatFiles.OwnFileText : "")
            + (state is { State: Core.Chat.FileState.Waiting } ? " " + ChatFiles.WaitingText(state.WaitingFor ?? first) : ""));
        return stack;
    }

    /// <summary>Download ▾ (Save as…) · the waiting line · ✕ while it comes · Open · Show in folder · why it failed.</summary>
    private static FrameworkElement Actions(TrayModel m, ChatFile f, string first, bool mine, Core.Chat.FileState? state, string? downloaded)
    {
        var row = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        if (downloaded is not null)
        {
            var open = Tv.Btn("Open", true, () => m.OpenMedia(f, first, mine), "Open " + f.Name, compact: true);
            open.Margin = new Thickness(0, 0, 6, 0);
            row.Children.Add(open);
            row.Children.Add(Tv.Btn("Show in folder", false, () => m.ShowInFolder(f.Id, downloaded), "Show " + f.Name + " in its folder", compact: true));
            return row;
        }
        if (state is { State: Core.Chat.FileState.Waiting })
        {
            var waiting = Ui.Text(ChatFiles.WaitingText(state.WaitingFor ?? first), 12.5, FontWeights.SemiBold, "Z.Ink2", wrap: true);
            waiting.VerticalAlignment = VerticalAlignment.Center;
            waiting.Margin = new Thickness(0, 0, 8, 0);
            Motion.Breathe(waiting);
            row.Children.Add(waiting);
            row.Children.Add(CancelButton(m, f));
            return row;
        }
        if (state is { State: Core.Chat.FileState.Working })
        {
            var busy = Ui.Text(state.Total > 0 ? $"{ChatFiles.FormatSize(state.Received)} of {ChatFiles.FormatSize(state.Total)}" : "Downloading…",
                12, fg: "Z.Muted");
            busy.VerticalAlignment = VerticalAlignment.Center;
            busy.Margin = new Thickness(0, 0, 8, 0);
            row.Children.Add(busy);
            row.Children.Add(CancelButton(m, f));
            return row;
        }
        var stack = new StackPanel();
        if (state is { State: Core.Chat.FileState.Failed })
        {
            var why = Ui.Text(state.Error ?? ChatFiles.DownloadFailedText, 12, fg: "Z.RedText", wrap: true);
            why.Margin = new Thickness(0, 0, 0, 6);
            stack.Children.Add(why);
        }
        stack.Children.Add(Split(m, f, first, mine));
        row.Children.Add(stack);
        return row;
    }

    /// <summary>The split button: Download, and ▾ with Save as….</summary>
    internal static FrameworkElement Split(TrayModel m, ChatFile f, string first, bool mine)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        var download = Tv.Btn("Download", false, () => m.Download(f, first, mine), "Download " + f.Name, compact: true);
        row.Children.Add(download);
        var more = Tv.Btn("▾", false, () => { }, "More ways to download " + f.Name, compact: true);
        more.Margin = new Thickness(2, 0, 0, 0);
        more.Padding = new Thickness(6, 0, 6, 0);
        more.Click += (_, e) =>
        {
            // Made when it opens (the menu belongs to the window's own thread).
            e.Handled = true;
            var menu = new ContextMenu { PlacementTarget = more, Placement = PlacementMode.Bottom };
            var saveAs = new MenuItem { Header = ChatFiles.SaveAsText };
            saveAs.Click += (_, _) => m.Download(f, first, mine, saveAs: true);
            AutomationProperties.SetName(saveAs, ChatFiles.SaveAsText);
            menu.Items.Add(saveAs);
            menu.IsOpen = true;
        };
        row.Children.Add(more);
        return row;
    }

    private static Button CancelButton(TrayModel m, ChatFile f)
    {
        var x = Tv.Icon(Tv.IcoX, () => m.CancelDownload(f), "Cancel download of " + f.Name, "Transparent", 12, "Z.Ink2", 2.4);
        x.VerticalAlignment = VerticalAlignment.Center;
        return x;
    }

    /// <summary>The grey card of a file that isn't shared any more (stopped, gone or old).</summary>
    public static FrameworkElement NotShared(string name)
    {
        var stack = new StackPanel();
        stack.Children.Add(Head(name, 0, false, muted: true));
        var why = Ui.Text(ChatFiles.NotSharedText, 12, fg: "Z.Muted", wrap: true);
        why.Margin = new Thickness(0, 8, 0, 0);
        stack.Children.Add(why);
        return new Border { Child = stack, CornerRadius = new CornerRadius(10), Padding = new Thickness(10, 8, 10, 8) }.Res(Border.BackgroundProperty, "Z.Fill");
    }

    // ---- Photo and video tiles -----------------------------------------------------------------------------------------

    /// <summary>The tile's size: the file's own shape, at most 220 × 220 (4:3 when unknown).</summary>
    internal static Size TileSize(int? width, int? height, double max = TileMax)
    {
        if (width is not { } w || height is not { } h || w <= 0 || h <= 0)
        {
            return new Size(max, Math.Round(max * 0.75));
        }
        var ratio = (double)h / w;
        var tw = ratio > 1 ? max / ratio : max;
        return new Size(Math.Max(96, Math.Round(tw)), Math.Max(72, Math.Round(Math.Min(max, tw * ratio))));
    }

    /// <summary>A photo or video: its thumbnail (or its type icon), a play mark and length on a video, the ring or the pulse over it.</summary>
    private static FrameworkElement MediaTile(TrayModel m, ChatFile f, string first, bool mine, Core.Chat.FileState? state, string? downloaded,
        bool own, Size? size = null, int more = 0)
    {
        var box = size ?? TileSize(f.Width, f.Height);
        var grid = new Grid { Width = box.Width, Height = box.Height, ClipToBounds = true };
        var face = new Border { CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1) }
            .Res(Border.BorderBrushProperty, "Z.Line").Res(Border.BackgroundProperty, "Z.Fill");
        if (m.ThumbFor(f) is { } thumb && Thumb(thumb) is { } bitmap)
        {
            face.Background = new ImageBrush(bitmap) { Stretch = Stretch.UniformToFill };
        }
        else
        {
            face.Child = Ui.Glyph(IconData(f.IsVideo ? "video" : "image"), 28, "Z.Muted", 1.8);
        }
        grid.Children.Add(face);
        if (f.IsVideo && more == 0)
        {
            grid.Children.Add(PlayMark());
            if (ChatFiles.FormatDuration(f.DurationS) is { Length: > 0 } length)
            {
                grid.Children.Add(Badge(length));
            }
        }
        if (more > 0)
        {
            var dim = new Border { CornerRadius = new CornerRadius(8), Background = new SolidColorBrush(Color.FromArgb(0x99, 0x14, 0x12, 0x10)) };
            var plus = new TextBlock { Text = "+" + more, FontSize = 22, FontWeight = FontWeights.Bold, Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            dim.Child = plus;
            grid.Children.Add(dim);
        }
        else if (state is { Busy: true } && !own)
        {
            var veil = new Border { CornerRadius = new CornerRadius(8), Background = new SolidColorBrush(Color.FromArgb(0x66, 0x14, 0x12, 0x10)) };
            var ring = Progress(m, f, state, first, 48);
            ring.HorizontalAlignment = HorizontalAlignment.Center;
            ring.VerticalAlignment = VerticalAlignment.Center;
            veil.Child = ring;
            grid.Children.Add(veil);
        }
        var aria = (downloaded is not null ? "Open " : own ? "" : "Download ") + f.Name;
        if (own)
        {
            AutomationProperties.SetName(grid, f.Name);
            return grid;
        }
        var button = Tv.Bare(grid, () => m.OpenMedia(f, first, mine), aria);
        button.HorizontalAlignment = HorizontalAlignment.Left;
        button.Cursor = Cursors.Hand;
        return button;
    }

    private static FrameworkElement PlayMark()
    {
        var disc = new Border
        {
            Width = 40, Height = 40, CornerRadius = new CornerRadius(20), Background = new SolidColorBrush(Color.FromArgb(0xB3, 0x14, 0x12, 0x10)),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false,
        };
        var path = new System.Windows.Shapes.Path { Data = Geometry.Parse(IcoPlay), Fill = Brushes.White, Stretch = Stretch.Uniform, Width = 14, Height = 16, Margin = new Thickness(3, 0, 0, 0) };
        disc.Child = path;
        return disc;
    }

    /// <summary>The video length at the tile's bottom right ("1:23").</summary>
    private static FrameworkElement Badge(string text) => new Border
    {
        CornerRadius = new CornerRadius(6), Padding = new Thickness(6, 1, 6, 2), Margin = new Thickness(0, 0, 6, 6),
        HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, IsHitTestVisible = false,
        Background = new SolidColorBrush(Color.FromArgb(0xB3, 0x14, 0x12, 0x10)),
        Child = new TextBlock { Text = text, FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White },
    };

    /// <summary>
    /// Photos and videos sent together: a 2×2 grid (one or two rows), "+N" on the 4th for the rest; the actions under it
    /// download them all (Download all) or show where they went.
    /// </summary>
    public static FrameworkElement BatchGrid(TrayModel m, IReadOnlyList<ChatFile> files, string? senderFirst, bool mine)
    {
        var (shown, rest) = ChatFileBatches.Grid(files.Count);
        var stack = new StackPanel { MaxWidth = 264 };
        var grid = new UniformGrid { Columns = 2, Width = TileMax + 4 };
        var cell = new Size(TileMax / 2, TileMax / 2);
        for (var i = 0; i < shown; i++)
        {
            var f = files[i];
            var state = m.DownloadOf(f);
            FrameworkElement tile = !f.AvailableAt(m.Now) || state is { State: Core.Chat.FileState.Gone }
                ? new Border { Width = cell.Width, Height = cell.Height, CornerRadius = new CornerRadius(8),
                    Child = Ui.Glyph(IcoPhoto, 22, "Z.Muted", 1.6) }.Res(Border.BackgroundProperty, "Z.Fill")
                : MediaTile(m, f, f.FirstName(senderFirst), mine, state, m.DownloadedPath(f), mine && !f.IsLegacy, cell, i == shown - 1 ? rest : 0);
            tile.Margin = new Thickness(0, 0, i % 2 == 0 ? 4 : 0, 4);
            grid.Children.Add(tile);
        }
        stack.Children.Add(grid);
        var available = files.Where(f => f.AvailableAt(m.Now)).ToList();
        var own = mine && files.All(f => !f.IsLegacy);
        var line = Ui.Text($"{files.Count} {(files.All(f => f.IsVideo) ? "videos" : files.All(f => f.IsImage) ? "photos" : "files")} · "
            + ChatFiles.FormatSize(files.Sum(f => f.Size)), 12, fg: "Z.Muted");
        line.Margin = new Thickness(0, 2, 0, 0);
        stack.Children.Add(line);
        if (own)
        {
            var kept = Ui.Text(ChatFiles.OwnFileText, 12, fg: "Z.Muted", wrap: true);
            kept.Margin = new Thickness(0, 6, 0, 0);
            stack.Children.Add(kept);
        }
        else if (available.Count == 0)
        {
            var gone = Ui.Text(ChatFiles.NotSharedText, 12, fg: "Z.Muted", wrap: true);
            gone.Margin = new Thickness(0, 6, 0, 0);
            stack.Children.Add(gone);
        }
        else if (available.Any(f => m.DownloadedPath(f) is null && m.DownloadOf(f) is not { Busy: true }))
        {
            var all = Tv.Btn("Download all", false, () =>
            {
                foreach (var f in available.Where(f => m.DownloadedPath(f) is null && m.DownloadOf(f) is not { Busy: true }))
                {
                    m.Download(f, f.FirstName(senderFirst), mine);
                }
            }, $"Download all {available.Count}", compact: true);
            all.Margin = new Thickness(0, 8, 0, 0);
            all.HorizontalAlignment = HorizontalAlignment.Left;
            stack.Children.Add(all);
        }
        AutomationProperties.SetName(stack, $"{files.Count} files sent together: " + string.Join(", ", files.Select(f => f.Name)));
        return stack;
    }

    // ---- The ring --------------------------------------------------------------------------------------------------------

    /// <summary>
    /// The download's look over a tile or in place of the icon: a simple ring with the % inside (spinning while the size isn't
    /// known), or a soft pulse while the sender's PC is off. ✕ (Cancel) sits next to it.
    /// </summary>
    internal static FrameworkElement Progress(TrayModel m, ChatFile f, Core.Chat.FileState state, string first, double size)
    {
        var ring = Ring(state.State == Core.Chat.FileState.Waiting ? null : state.Percent, size, state.Total <= 0);
        if (state.State == Core.Chat.FileState.Waiting)
        {
            Motion.Breathe(ring);
            AutomationProperties.SetName(ring, ChatFiles.WaitingText(state.WaitingFor ?? first));
        }
        else
        {
            AutomationProperties.SetName(ring, $"Downloading {f.Name}, {state.Percent}%");
        }
        return ring;
    }

    /// <summary>A ring of <paramref name="size"/> px: the track, the done part in orange, and "42%" (null: a waiting ring, no number).</summary>
    internal static FrameworkElement Ring(int? percent, double size, bool spin = false)
    {
        var grid = new Grid { Width = size, Height = size };
        var track = new Ellipse { StrokeThickness = 3 };
        track.SetResourceReference(Shape.StrokeProperty, "Z.Line");
        grid.Children.Add(track);
        var done = new System.Windows.Shapes.Path { StrokeThickness = 3, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
        done.SetResourceReference(Shape.StrokeProperty, "Z.Orange");
        done.Data = Arc(size, spin || percent is null ? 25 : Math.Clamp(percent.Value, 0, 100));
        grid.Children.Add(done);
        if (spin || percent is null)
        {
            var turn = new RotateTransform(0, size / 2, size / 2);
            done.RenderTransform = turn;
            if (Motion.On)
            {
                turn.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, 360, TimeSpan.FromSeconds(1.1)) { RepeatBehavior = RepeatBehavior.Forever });
            }
        }
        if (percent is { } p && !spin)
        {
            var text = Ui.Text(p + "%", size >= 44 ? 12 : 10, FontWeights.SemiBold, "Z.Ink");
            text.HorizontalAlignment = HorizontalAlignment.Center;
            text.VerticalAlignment = VerticalAlignment.Center;
            var back = new Border { Width = size - 8, Height = size - 8, CornerRadius = new CornerRadius((size - 8) / 2), Child = text }
                .Res(Border.BackgroundProperty, "Z.Surface");
            grid.Children.Add(back);
        }
        return grid;
    }

    /// <summary>An arc from 12 o'clock, clockwise, over <paramref name="percent"/> of the circle.</summary>
    internal static Geometry Arc(double size, double percent)
    {
        var r = (size - 3) / 2;
        var c = size / 2;
        var p = Math.Clamp(percent, 0.5, 99.9);
        var angle = p / 100 * 2 * Math.PI;
        var start = new Point(c, c - r);
        var end = new Point(c + (r * Math.Sin(angle)), c - (r * Math.Cos(angle)));
        var figure = new PathFigure { StartPoint = start, IsClosed = false };
        figure.Segments.Add(new ArcSegment(end, new Size(r, r), 0, p > 50, SweepDirection.Clockwise, true));
        var geometry = new PathGeometry([figure]);
        geometry.Freeze();
        return geometry;
    }

    // ---- Sending ---------------------------------------------------------------------------------------------------------

    /// <summary>
    /// A file the member is sending: its name and size, then "Sending…" or the reason it failed with Retry · Delete. Without
    /// <paramref name="withStatus"/> only the name and size (the admin chat says the rest under the bubble).
    /// </summary>
    public static FrameworkElement Pending(TrayModel m, OutboxReply item, bool withStatus = true)
    {
        var name = item.FileName ?? "file";
        var stack = new StackPanel { MinWidth = 200, MaxWidth = 264 };
        FrameworkElement? icon = null;
        if (item.ThumbB64 is { Length: > 0 } b64 && ThumbFromBytes(b64) is { } bitmap)
        {
            icon = new Border
            {
                Width = 36, Height = 36, CornerRadius = new CornerRadius(9), Background = new ImageBrush(bitmap) { Stretch = Stretch.UniformToFill },
            };
        }
        stack.Children.Add(Head(name, item.FileSize ?? 0, ChatFiles.IsRisky(name), icon: icon));
        if (!withStatus)
        {
            AutomationProperties.SetName(stack, $"File {name}, {ChatFiles.FormatSize(item.FileSize ?? 0)}");
            return stack;
        }
        if (!item.Refused)
        {
            var sending = Ui.Text("Sending…", 12, FontWeights.SemiBold, "Z.Ink2");
            sending.Margin = new Thickness(0, 8, 0, 0);
            Motion.Breathe(sending);
            stack.Children.Add(sending);
            AutomationProperties.SetName(stack, $"Sending {name}");
            return stack;
        }
        var why = Ui.Text(item.Error ?? ChatFiles.FailedText, 12, FontWeights.SemiBold, "Z.RedText", wrap: true);
        why.Margin = new Thickness(0, 8, 0, 0);
        stack.Children.Add(why);
        var row = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        var retry = Tv.Btn("Retry", true, () => m.RetryFile(item), "Retry sending " + name, compact: true);
        retry.Margin = new Thickness(0, 0, 6, 0);
        row.Children.Add(retry);
        row.Children.Add(Tv.Btn("Delete", false, () => m.RemoveFile(item), "Delete " + name, compact: true));
        stack.Children.Add(row);
        AutomationProperties.SetName(stack, $"{name} wasn’t sent. {item.Error ?? ChatFiles.FailedText}");
        return stack;
    }

    /// <summary>
    /// The send tray above the composer: a tile per picked file (thumbnail or type icon, a video's length, name, size, ✕) and
    /// Add more (while under 10). The typed text goes with the last file as its caption.
    /// </summary>
    public static FrameworkElement SendTray(DependencyObject host, TrayModel m, string key)
    {
        var drafts = m.Attached(key);
        var panel = new WrapPanel { Margin = new Thickness(0, 4, 0, 6) };
        foreach (var d in drafts)
        {
            panel.Children.Add(TrayTileFor(m, key, d));
        }
        if (drafts.Count < ChatFiles.MaxFilesAtOnce)
        {
            var plus = new StackPanel { Width = TrayTile, HorizontalAlignment = HorizontalAlignment.Center };
            var box = new Border
            {
                Width = TrayTile, Height = TrayTile, CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1.5),
                Child = Ui.Glyph(IcoPlus, 20, "Z.Ink2", 2),
            }.Res(Border.BorderBrushProperty, "Z.Line2").Res(Border.BackgroundProperty, "Z.Surface");
            plus.Children.Add(box);
            var label = Ui.Text("Add more", 11, fg: "Z.Ink2");
            label.HorizontalAlignment = HorizontalAlignment.Center;
            label.Margin = new Thickness(0, 4, 0, 0);
            plus.Children.Add(label);
            var add = Tv.Bare(plus, () => OpenMenu(host, m, key, null), "Add more files");
            add.VerticalAlignment = VerticalAlignment.Top;
            panel.Children.Add(add);
        }
        AutomationProperties.SetName(panel, $"{drafts.Count} file{(drafts.Count == 1 ? "" : "s")} to send. Your message goes with them.");
        return panel;
    }

    private static FrameworkElement TrayTileFor(TrayModel m, string key, AttachDraft d)
    {
        var stack = new StackPanel { Width = TrayTile, Margin = new Thickness(0, 0, 8, 4) };
        var grid = new Grid { Width = TrayTile, Height = TrayTile };
        var face = new Border { CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1) }
            .Res(Border.BorderBrushProperty, "Z.Line").Res(Border.BackgroundProperty, d.IsMedia ? "Z.Fill" : "Z.OrangeTint");
        if (d.Thumb is { Length: > 0 } bytes && ThumbFromBytes(bytes) is { } bitmap)
        {
            face.Background = new ImageBrush(bitmap) { Stretch = Stretch.UniformToFill };
        }
        else
        {
            face.Child = Ui.Glyph(IconData(ChatFiles.IconKind(d.Name)), 22, ChatFiles.IsRisky(d.Name) ? "Z.RedText" : "Z.OrangeInk", 2);
        }
        grid.Children.Add(face);
        if (d.Kind == ChatFile.VideoKind && ChatFiles.FormatDuration(d.DurationS) is { Length: > 0 } length)
        {
            var badge = Badge(length);
            badge.Margin = new Thickness(0, 0, 3, 3);
            grid.Children.Add(badge);
        }
        var x = Tv.Icon(Tv.IcoX, () => m.RemoveAttach(key, d), "Remove " + d.Name, "Z.Surface", 10, "Z.Ink2", 2.4);
        x.HorizontalAlignment = HorizontalAlignment.Right;
        x.VerticalAlignment = VerticalAlignment.Top;
        x.Margin = new Thickness(0, -10, -12, 0);
        x.RenderTransform = new ScaleTransform(0.7, 0.7, 40, 0);
        grid.Children.Add(x);
        stack.Children.Add(grid);
        var name = Ui.Text(d.Name, 11, fg: "Z.Ink", trim: true);
        name.ToolTip = d.Name;
        name.Margin = new Thickness(0, 4, 0, 0);
        stack.Children.Add(name);
        stack.Children.Add(Ui.Text(ChatFiles.FormatSize(d.Size), 10.5, fg: "Z.Muted"));
        AutomationProperties.SetName(stack, $"{d.Name}, {ChatFiles.FormatSize(d.Size)}"
            + (d.Kind == ChatFile.VideoKind && ChatFiles.FormatDuration(d.DurationS) is { Length: > 0 } dur ? ", " + dur : ""));
        return stack;
    }

    // ---- Attach ------------------------------------------------------------------------------------------------------------

    /// <summary>The paperclip in a team thread's composer (40 px hit area, tooltip "Attach a file"): opens the attach menu.</summary>
    public static Button AttachButton(DependencyObject host, TrayModel m, string key)
    {
        Button? b = null;
        b = Tv.Icon(Ui.Icon.Clip, () => OpenMenu(host, m, key, b), ChatFiles.AttachTooltip, "Transparent", 18, "Z.Muted", 2);
        b.VerticalAlignment = VerticalAlignment.Bottom;
        return b;
    }

    /// <summary>The attach menu's entries, in order: the words, photos and videos or not, the icon.</summary>
    internal static readonly (string Label, bool Media, string Icon)[] AttachChoices =
        [(ChatFiles.AttachDocument, false, IcoDoc), (ChatFiles.AttachMedia, true, IcoPhoto)];

    /// <summary>The attach menu: Document · Photos &amp; videos, each the Windows picker with multi-select.</summary>
    internal static ContextMenu AttachMenu(DependencyObject host, TrayModel m, string key)
    {
        var menu = new ContextMenu();
        foreach (var (label, media, icon) in AttachChoices)
        {
            var item = new MenuItem { Header = label, Icon = Ui.Glyph(icon, 16, "Z.Ink2", 2) };
            item.Click += (_, _) => PickFor(host, m, key, media);
            AutomationProperties.SetName(item, label);
            menu.Items.Add(item);
        }
        return menu;
    }

    public static void OpenMenu(DependencyObject host, TrayModel m, string key, UIElement? at)
    {
        var menu = AttachMenu(host, m, key);
        menu.PlacementTarget = at ?? host as UIElement;
        menu.Placement = PlacementMode.Top;
        menu.IsOpen = true;
    }

    /// <summary>The Windows file picker for one attach menu entry (multi-select). Empty when the member cancelled.</summary>
    public static IReadOnlyList<string> Pick(DependencyObject from, bool media)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = media ? ChatFiles.AttachMedia : ChatFiles.AttachDocument, CheckFileExists = true, Multiselect = true,
            Filter = ChatFiles.PickerFilter(media),
        };
        return dialog.ShowDialog(Window.GetWindow(from)) == true ? dialog.FileNames : [];
    }

    /// <summary>Picks files for <paramref name="key"/> and hands them to the model (each refused at once when too big).</summary>
    public static void PickFor(DependencyObject from, TrayModel? m, string? key, bool media)
    {
        if (m is null || key is null)
        {
            return;
        }
        if (Pick(from, media) is { Count: > 0 } paths)
        {
            m.Attach(key, paths);
        }
    }

    /// <summary>The Save as… dialog: the chosen path, or null.</summary>
    public static string? SaveAs(Window? owner, string name, string folder)
    {
        var ext = System.IO.Path.GetExtension(name);
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = ChatFiles.SaveAsText.TrimEnd('…'), FileName = name, OverwritePrompt = true, AddExtension = true,
            Filter = ext.Length > 1 ? $"{ext.TrimStart('.').ToUpperInvariant()} file (*{ext})|*{ext}|All files (*.*)|*.*" : "All files (*.*)|*.*",
        };
        if (Directory.Exists(folder))
        {
            dialog.InitialDirectory = folder;
        }
        return (owner is null ? dialog.ShowDialog() : dialog.ShowDialog(owner)) == true ? dialog.FileName : null;
    }

    // ---- Pictures ----------------------------------------------------------------------------------------------------------

    private static readonly Dictionary<string, BitmapSource?> Thumbs = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A thumbnail file decoded once (at twice the tile size, for sharp HiDPI), or null when Windows can't read it.</summary>
    internal static BitmapSource? Thumb(string path)
    {
        if (Thumbs.TryGetValue(path, out var cached))
        {
            return cached;
        }
        BitmapSource? bitmap = null;
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad; // the file is not held open
            image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            image.UriSource = new Uri(path, UriKind.Absolute);
            image.DecodePixelWidth = (int)(TileMax * 2);
            image.EndInit();
            image.Freeze();
            bitmap = image.PixelWidth > 0 && image.PixelHeight > 0 ? image : null;
        }
        catch (Exception e) when (e is IOException or NotSupportedException or UriFormatException or ArgumentException or InvalidOperationException
            or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            bitmap = null; // not decodable here (a WebP without its codec): the tile shows its icon
        }
        if (Thumbs.Count > 80)
        {
            Thumbs.Clear();
        }
        Thumbs[path] = bitmap;
        return bitmap;
    }

    private static BitmapSource? ThumbFromBytes(string b64)
    {
        try
        {
            return ThumbFromBytes(Convert.FromBase64String(b64));
        }
        catch (FormatException)
        {
            return null;
        }
    }

    internal static BitmapSource? ThumbFromBytes(byte[] bytes)
    {
        try
        {
            using var ms = new MemoryStream(bytes);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = ms;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception e) when (e is IOException or NotSupportedException or ArgumentException or InvalidOperationException
            or System.Runtime.InteropServices.COMException or FileFormatException)
        {
            return null;
        }
    }
}

using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Zinwe.Agent.Tray.Widget;

namespace Zinwe.Agent.Tray.Views;

/// <summary>
/// Small builders for the v2 views' dynamic parts (lists, cards, rows). Colours are attached as resource references
/// (Z.* tokens), so dark mode and High Contrast repaint them like the XAML.
/// </summary>
internal static class Ui
{
    /// <summary>The board's icon paths (24 × 24, stroked).</summary>
    public static class Icon
    {
        public const string Home = "M3 11 12 4l9 7v9H5v-9M10 20v-5h4v5";
        public const string Chat = "M21 12a8 8 0 0 1-11.6 7.1L4 20l1-4.6A8 8 0 1 1 21 12z";
        public const string Tasks = "M4 5a1 1 0 0 1 1-1h14a1 1 0 0 1 1 1v14a1 1 0 0 1-1 1H5a1 1 0 0 1-1-1zM8.5 12.5l2.5 2.5 4.5-5";
        public const string Me = "M12 12a4 4 0 1 0 0-8 4 4 0 0 0 0 8zM4 21a8 8 0 0 1 16 0";
        public const string Time = "M12 7v5l3 2M12 3a9 9 0 1 0 0 18 9 9 0 0 0 0-18z";
        public const string Privacy = "M12 3 4 6v6c0 5 3.5 8 8 9 4.5-1 8-4 8-9V6z";
        public const string Feedback = "M4 4h16v12H8l-4 4z";
        public const string Dock = "M5 4h14a2 2 0 0 1 2 2v12a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V6a2 2 0 0 1 2-2zM8 15h8";
        public const string Bell = "M6 8a6 6 0 0 1 12 0c0 7 3 9 3 9H3s3-2 3-9M10.3 21a1.9 1.9 0 0 0 3.4 0";
        public const string Gear = "M12 15a3 3 0 1 0 0-6 3 3 0 0 0 0 6zM19.4 15a1.7 1.7 0 0 0 .3 1.8l.1.1a2 2 0 1 1-2.8 2.8l-.1-.1a1.7 1.7 0 0 0-2.9 1.2V21a2 2 0 1 1-4 0v-.1a1.7 1.7 0 0 0-2.9-1.2l-.1.1a2 2 0 1 1-2.8-2.8l.1-.1a1.7 1.7 0 0 0-1.2-2.9H3a2 2 0 1 1 0-4h.1a1.7 1.7 0 0 0 1.2-2.9l-.1-.1a2 2 0 1 1 2.8-2.8l.1.1a1.7 1.7 0 0 0 2.9-1.2V3a2 2 0 1 1 4 0v.1a1.7 1.7 0 0 0 2.9 1.2l.1-.1a2 2 0 1 1 2.8 2.8l-.1.1a1.7 1.7 0 0 0 1.2 2.9H21a2 2 0 1 1 0 4h-.1a1.7 1.7 0 0 0-1.5 1z";
        public const string Help = "M12 21a9 9 0 1 0 0-18 9 9 0 0 0 0 18zM9.1 9a3 3 0 0 1 5.8 1c0 2-3 3-3 3M12 17h.01";
        public const string Check = "M20 6 9 17l-5-5";
        public const string TaskTick = "M4 4h16v16H4zM8.5 12.5l2.5 2.5 4.5-5";
        public const string Chevron = "m6 9 6 6 6-6";
        public const string Reply = "M9 14 4 9l5-5M4 9h10.5a5.5 5.5 0 0 1 0 11H11";
        public const string Send = "M22 2 11 13M22 2l-7 20-4-9-9-4z";
        public const string Clip = "m21 11-8.6 8.6a5 5 0 0 1-7-7l8.6-8.6a3.3 3.3 0 0 1 4.7 4.7l-8.6 8.6a1.7 1.7 0 0 1-2.4-2.4L15 7.6";
        public const string Calendar = "M8 2v4M16 2v4M3 10h18M5 4h14a2 2 0 0 1 2 2v14a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V6a2 2 0 0 1 2-2z";
        public const string Person = "M12 12a4 4 0 1 0 0-8 4 4 0 0 0 0 8zM4 21a8 8 0 0 1 16 0";
        public const string Camera = "M4 8h3l2-3h6l2 3h3v11H4zM12 9.5a3.5 3.5 0 1 0 0 7 3.5 3.5 0 0 0 0-7z";
        public const string Upload = "M12 16V4M7 9l5-5 5 5M4 16v3a1 1 0 0 0 1 1h14a1 1 0 0 0 1-1v-3";
        public const string Monitor = "M5 4h14a2 2 0 0 1 2 2v8a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V6a2 2 0 0 1 2-2zM8 20h8M12 16v4";
        public const string Removed = "M3 4h18v12H3zM8 20h8M12 16v4M9 8l6 4M15 8l-6 4";
    }

    public static T Res<T>(this T el, DependencyProperty prop, string key) where T : FrameworkElement
    {
        el.SetResourceReference(prop, key);
        return el;
    }

    public static Brush B(string key) => Application.Current?.TryFindResource(key) as Brush ?? Brushes.Gray;

    public static TextBlock Text(string text, double size = 13.5, FontWeight? weight = null, string fg = "Z.Ink",
        bool wrap = false, bool trim = false)
    {
        var t = new TextBlock { Text = text, FontSize = size, FontWeight = weight ?? FontWeights.Normal };
        t.SetResourceReference(TextBlock.ForegroundProperty, fg);
        if (wrap)
        {
            t.TextWrapping = TextWrapping.Wrap;
            t.LineHeight = Math.Round(size * 1.45);
        }
        if (trim)
        {
            t.TextTrimming = TextTrimming.CharacterEllipsis;
        }
        return t;
    }

    /// <summary>Centres a piece of a line on its row, with space either side (the "·" between two words).</summary>
    public static TextBlock Centered(this TextBlock t, double side = 0)
    {
        t.VerticalAlignment = VerticalAlignment.Center;
        t.Margin = new Thickness(side, 0, side, 0);
        return t;
    }

    /// <summary>A stroked 24-unit icon drawn at <paramref name="size"/> px.</summary>
    public static FrameworkElement Glyph(string data, double size, string strokeKey = "Z.Ink3", double thickness = 2)
    {
        var path = new Path
        {
            Data = Geometry.Parse(data),
            StrokeThickness = thickness,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
        };
        path.SetResourceReference(Shape.StrokeProperty, strokeKey);
        var canvas = new Canvas { Width = 24, Height = 24 };
        canvas.Children.Add(path);
        return new Viewbox { Width = size, Height = size, Child = canvas, IsHitTestVisible = false };
    }

    /// <summary>A rounded square tile with an icon (Needs you, Me rows, tips).</summary>
    public static Border Tile(string icon, double size, string bgKey, string fgKey, double radius = 9, double iconSize = 16) =>
        new Border
        {
            Width = size, Height = size, CornerRadius = new CornerRadius(radius),
            Child = Glyph(icon, iconSize, fgKey, 2.1),
        }.Res(Border.BackgroundProperty, bgKey);

    public static Border Card(UIElement child, double radius = 12, Thickness? padding = null) =>
        new Border
        {
            CornerRadius = new CornerRadius(radius), BorderThickness = new Thickness(1), Child = child,
            Padding = padding ?? new Thickness(0), SnapsToDevicePixels = true,
        }.Res(Border.BackgroundProperty, "Z.Surface").Res(Border.BorderBrushProperty, "Z.Line");

    public static Button Button(object content, string style, Action run, string? aria = null)
    {
        var b = new Button { Content = content, Style = (Style)Application.Current.FindResource(style) };
        b.Click += (_, _) => run();
        if (aria is not null)
        {
            AutomationProperties.SetName(b, aria);
        }
        return b;
    }

    /// <summary>A .li row: content stretched, a › at the end.</summary>
    public static Button ListRow(UIElement? lead, string title, string? sub, Action run, string trail = "›", string? aria = null)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        if (lead is not null)
        {
            if (lead is FrameworkElement fe)
            {
                fe.Margin = new Thickness(0, 0, 12, 0);
                fe.VerticalAlignment = VerticalAlignment.Center;
            }
            grid.Children.Add(lead);
        }
        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        texts.Children.Add(Text(title, 13.5, FontWeights.SemiBold, trim: true));
        if (!string.IsNullOrEmpty(sub))
        {
            texts.Children.Add(Text(sub, 12, fg: "Z.Muted", trim: true));
        }
        Grid.SetColumn(texts, 1);
        grid.Children.Add(texts);
        var arrow = Text(trail, 18, fg: "Z.Muted");
        arrow.Margin = new Thickness(8, 0, 0, 0);
        arrow.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(arrow, 2);
        grid.Children.Add(arrow);
        var b = Button(grid, "Z.ListItem", run, aria ?? (string.IsNullOrEmpty(sub) ? title : $"{title}. {sub}"));
        return b;
    }

    /// <summary>The last row of a card's list: no rule under it (it would poke past the card's rounded corners).</summary>
    public static void EndList(Panel list)
    {
        if (list.Children.Count > 0 && list.Children[^1] is Control last)
        {
            last.BorderThickness = new Thickness(0);
        }
    }

    public static Ellipse Dot(double size, string key)
    {
        var e = new Ellipse { Width = size, Height = size, VerticalAlignment = VerticalAlignment.Center };
        e.SetResourceReference(Shape.FillProperty, key);
        return e;
    }

    /// <summary>A pill ("Working", "Doing", NEW).</summary>
    public static Border Pill(string text, string bgKey, string fgKey, double size = 12, FontWeight? weight = null) =>
        new Border
        {
            CornerRadius = new CornerRadius(6), Padding = new Thickness(7, 1, 7, 1), VerticalAlignment = VerticalAlignment.Center,
            Child = Text(text, size, weight ?? FontWeights.SemiBold, fgKey),
        }.Res(Border.BackgroundProperty, bgKey);

    /// <summary>
    /// A member's circle (FR-44): their photo, a library avatar, or - with no picture - initials on a tint, or the
    /// official Zinwe mark when no name is known. A thin ring in the line colour keeps the circle visible on any ground
    /// (it turns into the text colour in High Contrast).
    /// </summary>
    public static FrameworkElement Avatar(PictureView? picture, string initials, double size, string initialsBg = "#F5F5F5")
    {
        if (picture is { IsPhoto: true } && Photo(picture) is { } photo)
        {
            return Ringed(new Ellipse { Width = size, Height = size, Fill = new ImageBrush(photo) { Stretch = Stretch.UniformToFill } }, size);
        }
        if (picture is { IsAvatar: true } && AvatarLibrary.Find(picture.Key) is { } avatar)
        {
            return Ringed(Art(avatar, size), size);
        }
        var bg = (Color)ColorConverter.ConvertFromString(initialsBg);
        if (initials.Length == 0)
        {
            // No name known: the official Zinwe mark, not a drawn "Z".
            var mark = new Image { Width = size * 0.56, Height = size * 0.56, Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            mark.SetResourceReference(Image.SourceProperty, "Z.Mark");
            RenderOptions.SetBitmapScalingMode(mark, BitmapScalingMode.HighQuality);
            return new Border
            {
                Width = size, Height = size, CornerRadius = new CornerRadius(size / 2), Background = new SolidColorBrush(bg), Child = mark,
            };
        }
        return new Border
        {
            Width = size, Height = size, CornerRadius = new CornerRadius(size / 2), Background = new SolidColorBrush(bg),
            Child = new TextBlock
            {
                Text = initials, FontWeight = FontWeights.ExtraBold, FontSize = Math.Max(10, Math.Round(size * 0.36)),
                Foreground = new SolidColorBrush(Color.FromRgb(0x14, 0x12, 0x10)),
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            },
        };
    }

    private static FrameworkElement Ringed(FrameworkElement picture, double size)
    {
        var ring = new Ellipse { Width = size, Height = size, StrokeThickness = size >= 40 ? 1.5 : 1, IsHitTestVisible = false };
        ring.SetResourceReference(Shape.StrokeProperty, "Z.Line");
        var grid = new Grid { Width = size, Height = size };
        grid.Children.Add(picture);
        grid.Children.Add(ring);
        return grid;
    }

    /// <summary>A library avatar: its paths on its tint, clipped to a circle.</summary>
    public static FrameworkElement Art(LibraryAvatar avatar, double size)
    {
        var box = AvatarLibrary.ViewSize;
        var canvas = new Canvas { Width = box, Height = box, Clip = new EllipseGeometry(new Point(box / 2, box / 2), box / 2, box / 2) };
        canvas.Children.Add(new Rectangle { Width = box, Height = box, Fill = Hex(avatar.Bg) });
        foreach (var (data, fill) in avatar.Paths)
        {
            canvas.Children.Add(new Path { Data = PathGeometry(data), Fill = Hex(fill) });
        }
        return new Viewbox { Width = size, Height = size, Child = canvas };
    }

    private static readonly Dictionary<string, Geometry> Shapes = [];

    /// <summary>Parsed once and frozen: the library's paths are drawn on every render.</summary>
    private static Geometry PathGeometry(string data)
    {
        if (!Shapes.TryGetValue(data, out var g))
        {
            g = Geometry.Parse(data);
            g.Freeze();
            Shapes[data] = g;
        }
        return g;
    }

    private static (string Key, BitmapSource Image)? _lastPhoto;

    /// <summary>The photo, loaded once per file (its name changes with the picture) or per fresh JPEG.</summary>
    private static BitmapSource? Photo(PictureView picture)
    {
        var key = picture.Jpeg is { } bytes ? "mem:" + bytes.Length + ":" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes))[..16]
            : "file:" + picture.File;
        if (_lastPhoto is { } last && last.Key == key)
        {
            return last.Image;
        }
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad; // the file isn't kept open: the service may replace it
            if (picture.Jpeg is { } jpeg)
            {
                image.StreamSource = new System.IO.MemoryStream(jpeg); // (IgnoreImageCache throws with a stream)
            }
            else if (System.IO.File.Exists(picture.File))
            {
                image.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
                image.UriSource = new Uri(picture.File!);
            }
            else
            {
                return null;
            }
            image.EndInit();
            image.Freeze();
            _lastPhoto = (key, image);
            return image;
        }
        catch (Exception)
        {
            return null; // unreadable: initials
        }
    }

    public static SolidColorBrush Hex(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }

    /// <summary>A toggle chip (reasons, due) in a single-choice group.</summary>
    public static ToggleButton Chip(string label, bool on, Action pick, string style = "Z.Chip")
    {
        var chip = new ToggleButton { Content = label, IsChecked = on, Style = (Style)Application.Current.FindResource(style) };
        // Checked/Unchecked (not Click): keyboard and UI Automation change IsChecked without a click.
        chip.Checked += (_, _) => pick();
        chip.Unchecked += (_, _) => pick();
        AutomationProperties.SetName(chip, label);
        return chip;
    }
}

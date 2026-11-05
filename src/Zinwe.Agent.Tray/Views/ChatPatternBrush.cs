using System.Windows;
using System.Windows.Media;
using Zinwe.Agent.Tray.Widget;

namespace Zinwe.Agent.Tray.Views;

/// <summary>
/// The chat backgrounds (<see cref="ChatPatterns"/>): a soft off-white (or near-black) ground with a faint line
/// pattern in ink and orange, tiled behind the messages. Drawn as vectors, so it is sharp at every scale and needs no
/// image files. Plain is the window's own ground; High Contrast always gets the plain system ground.
/// </summary>
internal static class ChatPatternBrush
{
    private const double Tile = 120;

    // Each pattern: lines in ink, then lines in orange, on a 120 × 120 tile that repeats seamlessly.
    private static readonly Dictionary<string, (string Ink, string Accent)> Shapes = new()
    {
        ["calm"] = (
            "M0,30 C15,22 25,22 40,30 S65,38 80,30 S105,22 120,30 M0,90 C15,82 25,82 40,90 S65,98 80,90 S105,82 120,90",
            "M52,68 C52,57 60,51 71,51 C71,62 63,68 52,68 Z M52,68 L64,57 M10,118 C10,109 17,104 26,104 C26,113 19,118 10,118 Z M10,118 L20,109"),
        ["focus"] = (
            "M21,30 A9,9 0 1 0 39,30 A9,9 0 1 0 21,30 Z M26,30 A4,4 0 1 0 34,30 A4,4 0 1 0 26,30 Z "
            + "M81,90 A9,9 0 1 0 99,90 A9,9 0 1 0 81,90 Z M86,90 A4,4 0 1 0 94,90 A4,4 0 1 0 86,90 Z",
            "M80,30 L85,35 L95,24 M20,90 L25,95 L35,84"),
        ["geek"] = (
            "M28,22 L20,30 L28,38 M42,22 L50,30 L42,38 M38,19 L32,41 "
            + "M84,78 C80,78 80,81 80,84 L80,88 C80,90 78,90 76,90 C78,90 80,90 80,92 L80,96 C80,99 80,102 84,102 "
            + "M96,78 C100,78 100,81 100,84 L100,88 C100,90 102,90 104,90 C102,90 100,90 100,92 L100,96 C100,99 100,102 96,102",
            "M86,30 H100 M20,90 H34 M20,96 H28"),
        ["money"] = (
            "M20,90 A10,10 0 1 0 40,90 A10,10 0 1 0 20,90 Z M24,90 A6,6 0 1 0 36,90 A6,6 0 1 0 24,90 Z "
            + "M86,96 A7,7 0 1 0 100,96 A7,7 0 1 0 86,96 Z",
            "M80,20 H96 M80,26 H96 M84,20 C92,20 92,32 84,32 H80 L92,44 M24,22 H36 M24,27 H36 M27,22 C33,22 33,31 27,31 H24 L33,40"),
    };

    /// <summary>The brush for a chat background; <paramref name="tile"/> scales the pattern (the menu's small swatches).</summary>
    public static Brush For(string key, double tile = Tile)
    {
        if (SystemParameters.HighContrast || !Shapes.TryGetValue(key, out var shape))
        {
            return Ui.B("Z.Bg");
        }
        var dark = HighContrastTheme.IsDark;
        var ground = dark ? Color.FromRgb(0x12, 0x12, 0x12) : Color.FromRgb(0xF7, 0xF7, 0xF5);
        var ink = dark ? Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x14, 0x14, 0x12, 0x10);
        var accent = dark ? Color.FromArgb(0x29, 0xFF, 0x8A, 0x3D) : Color.FromArgb(0x2E, 0xF2, 0x6A, 0x0A);

        var group = new DrawingGroup();
        group.Children.Add(new GeometryDrawing(new SolidColorBrush(ground), null, new RectangleGeometry(new Rect(0, 0, Tile, Tile))));
        group.Children.Add(Lines(shape.Ink, ink));
        group.Children.Add(Lines(shape.Accent, accent));
        group.ClipGeometry = new RectangleGeometry(new Rect(0, 0, Tile, Tile));
        var brush = new DrawingBrush(group)
        {
            TileMode = TileMode.Tile, Stretch = Stretch.Fill,
            Viewbox = new Rect(0, 0, Tile, Tile), ViewboxUnits = BrushMappingMode.Absolute,
            Viewport = new Rect(0, 0, tile, tile), ViewportUnits = BrushMappingMode.Absolute,
        };
        brush.Freeze();
        return brush;
    }

    private static GeometryDrawing Lines(string data, Color colour)
    {
        var pen = new Pen(new SolidColorBrush(colour), 1.6)
        {
            StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round,
        };
        return new GeometryDrawing(null, pen, Geometry.Parse(data));
    }
}

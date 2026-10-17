using System.Windows;
using System.Windows.Media;

namespace Zinwe.Agent.Tray;

/// <summary>
/// Windows High Contrast: the brand brushes (App.xaml, used everywhere as DynamicResource) are swapped for the
/// system's contrast colours, and back again when it's switched off.
/// </summary>
internal static class HighContrastTheme
{
    private static readonly Dictionary<string, object> Brand = [];

    // Which system colour each brand brush becomes: text and lines → window text, surfaces and tints → window,
    // the orange fills (buttons) → button face, focus rings → highlight.
    private static readonly (string Key, Func<Color> Contrast)[] Map =
    [
        ("Zinwe.White", () => SystemColors.WindowColor),
        ("Zinwe.Paper", () => SystemColors.WindowColor),
        ("Zinwe.Ink", () => SystemColors.WindowTextColor),
        ("Zinwe.Muted", () => SystemColors.WindowTextColor),
        ("Zinwe.Line", () => SystemColors.WindowTextColor),
        ("Zinwe.LineStrong", () => SystemColors.WindowTextColor),
        ("Zinwe.Orange", () => SystemColors.ControlColor),
        ("Zinwe.OrangeDeep", () => SystemColors.ControlColor),
        ("Zinwe.OrangeStrong", () => SystemColors.HighlightColor),
        ("Zinwe.OrangeText", () => SystemColors.WindowTextColor),
        ("Zinwe.OrangeTint", () => SystemColors.WindowColor),
        ("Zinwe.OrangeInk", () => SystemColors.WindowTextColor),
        ("Zinwe.Ok", () => SystemColors.WindowTextColor),
        ("Zinwe.OkTint", () => SystemColors.WindowColor),
        ("Zinwe.Warn", () => SystemColors.WindowTextColor),
        ("Zinwe.WarnTint", () => SystemColors.WindowColor),
        ("Zinwe.Bad", () => SystemColors.WindowTextColor),
        ("Zinwe.BadTint", () => SystemColors.WindowColor),
    ];

    /// <summary>Apply now, and follow the system setting from here on.</summary>
    public static void Attach(ResourceDictionary resources)
    {
        Apply(resources);
        SystemParameters.StaticPropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SystemParameters.HighContrast))
            {
                Application.Current?.Dispatcher.BeginInvoke(() => Apply(resources));
            }
        };
    }

    private static void Apply(ResourceDictionary resources)
    {
        var on = SystemParameters.HighContrast;
        foreach (var (key, contrast) in Map)
        {
            if (!Brand.ContainsKey(key) && resources[key] is { } original)
            {
                Brand[key] = original;
            }
            if (on)
            {
                var brush = new SolidColorBrush(contrast());
                brush.Freeze();
                resources[key] = brush;
            }
            else if (Brand.TryGetValue(key, out var brand))
            {
                resources[key] = brand;
            }
        }
    }
}

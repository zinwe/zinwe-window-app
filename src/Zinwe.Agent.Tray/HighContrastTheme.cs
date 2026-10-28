using System.Windows;
using System.Windows.Media;

namespace Zinwe.Agent.Tray;

/// <summary>
/// The tray's three looks, all by swapping the token brushes of Theme/Tokens.xaml (every view uses them as
/// DynamicResource): the brand light palette, the board's dark palette (Settings › Look › Dark mode), and Windows
/// High Contrast, which always wins and repaints everything with the system's contrast colours.
/// </summary>
internal static class HighContrastTheme
{
    private static ResourceDictionary? _resources;
    private static bool _dark;

    /// <summary>The board's .zdark values. Keys not listed keep their light value (fixed brand fills and tints).</summary>
    internal static readonly IReadOnlyDictionary<string, string> Dark = new Dictionary<string, string>
    {
        ["Z.Bg"] = "#151311", ["Z.Surface"] = "#201D19", ["Z.Surface2"] = "#1B1916", ["Z.Fill"] = "#2C2824",
        ["Z.Line"] = "#3A352F", ["Z.LineSoft"] = "#2E2A25", ["Z.Line2"] = "#4D463E", ["Z.Ink"] = "#F2EEE7",
        ["Z.Ink2"] = "#DCD5C9", ["Z.Ink3"] = "#CFC7BA", ["Z.Muted"] = "#B9B0A2", ["Z.Link"] = "#FF9A55",
        ["Z.Inv"] = "#EDE8DE", ["Z.OnInv"] = "#141210", ["Z.AccentSoft"] = "#2E2117", ["Z.AccentLine"] = "#6B3A14",
        ["Z.WarnSoft"] = "#2B2416", ["Z.WarnLine"] = "#5C4A22", ["Z.Snack"] = "#3A352F", ["Z.TabOn"] = "#3A2414",
        ["Z.Hover"] = "#26221E", ["Z.WarnText"] = "#E3A857", ["Z.StDoing"] = "#FFB27A", ["Z.StWait"] = "#9DBBFF",
        ["Z.StDone"] = "#72D39C", ["Z.RedText"] = "#FF8A75",
        ["Zinwe.White"] = "#201D19", ["Zinwe.Paper"] = "#151311", ["Zinwe.Ink"] = "#F2EEE7", ["Zinwe.Muted"] = "#B9B0A2",
        ["Zinwe.Line"] = "#3A352F", ["Zinwe.LineStrong"] = "#4D463E",
    };

    private enum Hc { Window, WindowText, Control, ControlText, Highlight, HighlightText, Gray }

    // High Contrast: surfaces → Window, text and lines → WindowText, orange fills → button face with button text,
    // ink (dark) fills → Highlight with HighlightText, focus → Highlight.
    private static readonly Dictionary<string, Hc> Contrast = new()
    {
        ["Z.Bg"] = Hc.Window, ["Z.Surface"] = Hc.Window, ["Z.Surface2"] = Hc.Window, ["Z.Fill"] = Hc.Window,
        ["Z.Hover"] = Hc.Window, ["Z.Line"] = Hc.WindowText, ["Z.LineSoft"] = Hc.Gray, ["Z.Line2"] = Hc.WindowText,
        ["Z.Ink"] = Hc.WindowText, ["Z.Ink2"] = Hc.WindowText, ["Z.Ink3"] = Hc.WindowText, ["Z.Muted"] = Hc.WindowText,
        ["Z.Link"] = Hc.WindowText, ["Z.Inv"] = Hc.Highlight, ["Z.OnInv"] = Hc.HighlightText, ["Z.WarnText"] = Hc.WindowText,
        ["Z.RedText"] = Hc.WindowText, ["Z.StDoing"] = Hc.WindowText, ["Z.StWait"] = Hc.WindowText, ["Z.StDone"] = Hc.WindowText,
        ["Z.AccentSoft"] = Hc.Window, ["Z.AccentLine"] = Hc.WindowText, ["Z.WarnSoft"] = Hc.Window, ["Z.WarnLine"] = Hc.WindowText,
        ["Z.Snack"] = Hc.Highlight, ["Z.TabOn"] = Hc.Highlight, ["Z.Orange"] = Hc.Control, ["Z.OnOrange"] = Hc.ControlText,
        ["Z.Focus"] = Hc.Highlight, ["Z.OkTint"] = Hc.Window, ["Z.Ok"] = Hc.WindowText, ["Z.OkFill"] = Hc.Highlight,
        ["Z.WarnTint"] = Hc.Window, ["Z.Warn"] = Hc.WindowText, ["Z.WarnInk"] = Hc.WindowText, ["Z.InfoTint"] = Hc.Window,
        ["Z.Info"] = Hc.WindowText, ["Z.InfoInk"] = Hc.WindowText, ["Z.OrangeTint"] = Hc.Window, ["Z.OrangeInk"] = Hc.WindowText,
        ["Z.SoonTint"] = Hc.Window, ["Z.RedTint"] = Hc.Window, ["Z.RedInk"] = Hc.WindowText, ["Z.Off"] = Hc.Gray,
        ["Z.SnackAction"] = Hc.HighlightText,
        ["Zinwe.White"] = Hc.Window, ["Zinwe.Paper"] = Hc.Window, ["Zinwe.Ink"] = Hc.WindowText, ["Zinwe.Muted"] = Hc.WindowText,
        ["Zinwe.Line"] = Hc.WindowText, ["Zinwe.LineStrong"] = Hc.WindowText, ["Zinwe.Orange"] = Hc.Control,
        ["Zinwe.OrangeDeep"] = Hc.Control, ["Zinwe.OrangeStrong"] = Hc.Highlight, ["Zinwe.OrangeText"] = Hc.WindowText,
        ["Zinwe.OrangeTint"] = Hc.Window, ["Zinwe.OrangeInk"] = Hc.WindowText, ["Zinwe.Ok"] = Hc.WindowText,
        ["Zinwe.OkTint"] = Hc.Window, ["Zinwe.Warn"] = Hc.WindowText, ["Zinwe.WarnTint"] = Hc.Window,
        ["Zinwe.Bad"] = Hc.WindowText, ["Zinwe.BadTint"] = Hc.Window,
    };

    /// <summary>Raised after the palette changed (views that resolved brushes in code re-render).</summary>
    public static event Action? Changed;

    public static bool IsHighContrast => SystemParameters.HighContrast;

    /// <summary>Apply now, and follow the system setting from here on.</summary>
    public static void Attach(ResourceDictionary resources)
    {
        _resources = resources;
        Apply();
        SystemParameters.StaticPropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SystemParameters.HighContrast))
            {
                Application.Current?.Dispatcher.BeginInvoke(Apply);
            }
        };
    }

    /// <summary>Settings › Dark mode.</summary>
    public static void SetDark(bool dark)
    {
        if (dark == _dark)
        {
            return;
        }
        _dark = dark;
        Apply();
    }

    private static void Apply()
    {
        if (_resources is not { } resources)
        {
            return;
        }
        // Start from the light tokens (the merged Tokens.xaml), then lay dark or High Contrast over them as local values.
        foreach (var key in Contrast.Keys)
        {
            resources.Remove(key);
        }
        if (SystemParameters.HighContrast)
        {
            foreach (var (key, role) in Contrast)
            {
                resources[key] = Frozen(SystemColor(role));
            }
        }
        else if (_dark)
        {
            foreach (var (key, hex) in Dark)
            {
                resources[key] = Frozen((Color)ColorConverter.ConvertFromString(hex));
            }
        }
        Changed?.Invoke();
    }

    private static Color SystemColor(Hc role) => role switch
    {
        Hc.Window => SystemColors.WindowColor,
        Hc.WindowText => SystemColors.WindowTextColor,
        Hc.Control => SystemColors.ControlColor,
        Hc.ControlText => SystemColors.ControlTextColor,
        Hc.Highlight => SystemColors.HighlightColor,
        Hc.HighlightText => SystemColors.HighlightTextColor,
        _ => SystemColors.GrayTextColor,
    };

    private static SolidColorBrush Frozen(Color c)
    {
        var brush = new SolidColorBrush(c);
        brush.Freeze();
        return brush;
    }
}

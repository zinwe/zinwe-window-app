using System.Windows;
using System.Windows.Media;

namespace Zinwe.Agent.Tray;

/// <summary>
/// The tray's three looks, all by swapping the token brushes of Theme/Tokens.xaml (every view uses them as
/// DynamicResource): the brand light palette, the black-based dark palette (Settings › Look › Dark mode), and Windows
/// High Contrast, which always wins and repaints everything with the system's contrast colours.
/// </summary>
internal static class HighContrastTheme
{
    private static ResourceDictionary? _resources;
    private static bool _dark;

    /// <summary>
    /// Dark mode (colour rule v1.3.2): black-based neutrals, white text, orange accent. Every key has a dark value,
    /// tints included, so no light chip sits on the black ground. Text on an orange fill stays ink (Z.OnOrange).
    /// </summary>
    internal static readonly IReadOnlyDictionary<string, string> Dark = new Dictionary<string, string>
    {
        // ground, surfaces, lines
        ["Z.Bg"] = "#0E0E0E", ["Z.Surface"] = "#1A1A1A", ["Z.Surface2"] = "#262626", ["Z.Fill"] = "#262626",
        ["Z.Hover"] = "#262626", ["Z.Line"] = "#333333", ["Z.LineSoft"] = "#333333", ["Z.Line2"] = "#4A4A4A",
        // ink
        ["Z.Ink"] = "#F5F5F5", ["Z.Ink2"] = "#CFCFCF", ["Z.Ink3"] = "#CFCFCF", ["Z.Muted"] = "#A3A3A3",
        ["Z.Inv"] = "#F5F5F5", ["Z.OnInv"] = "#141210", ["Z.Link"] = "#FF8A3D",
        // accent (orange) and its soft panels; amber states use the same orange
        ["Z.AccentSoft"] = "#1F150D", ["Z.AccentLine"] = "#5A3315", ["Z.WarnSoft"] = "#1F150D", ["Z.WarnLine"] = "#5A3315",
        ["Z.TabOn"] = "#3A2414", ["Z.OrangeTint"] = "#3A2414", ["Z.OrangeInk"] = "#FF8A3D", ["Z.SoonTint"] = "#3A2414",
        ["Z.WarnTint"] = "#3A2414", ["Z.Warn"] = "#FF8A3D", ["Z.WarnInk"] = "#FF8A3D", ["Z.WarnText"] = "#FF8A3D",
        ["Z.StDoing"] = "#FF8A3D",
        // grey (was blue)
        ["Z.InfoTint"] = "#262626", ["Z.Info"] = "#CFCFCF", ["Z.InfoInk"] = "#F5F5F5", ["Z.StWait"] = "#CFCFCF",
        // status: red for errors, green for working/done
        ["Z.RedTint"] = "#3A1A15", ["Z.RedText"] = "#FF6B57", ["Z.RedInk"] = "#FF6B57",
        ["Z.OkTint"] = "#12301F", ["Z.Ok"] = "#4ADE80", ["Z.StDone"] = "#4ADE80",
        // snackbar and scrim
        ["Z.Snack"] = "#333333", ["Z.Dim"] = "#99000000", ["Z.ProgressTrack"] = "#5A3315",
        // legacy keys (v1 widget, FeedbackPanel, ChatRow / NotificationRow)
        ["Zinwe.White"] = "#1A1A1A", ["Zinwe.Paper"] = "#0E0E0E", ["Zinwe.Ink"] = "#F5F5F5", ["Zinwe.Muted"] = "#A3A3A3",
        ["Zinwe.Line"] = "#333333", ["Zinwe.LineStrong"] = "#4A4A4A", ["Zinwe.OrangeText"] = "#FF8A3D",
        ["Zinwe.OrangeTint"] = "#3A2414", ["Zinwe.OrangeInk"] = "#FF8A3D", ["Zinwe.Ok"] = "#4ADE80", ["Zinwe.OkTint"] = "#12301F",
        ["Zinwe.Warn"] = "#FF8A3D", ["Zinwe.WarnTint"] = "#3A2414", ["Zinwe.Bad"] = "#FF6B57", ["Zinwe.BadTint"] = "#3A1A15",
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
        ["Z.Progress"] = Hc.Highlight, ["Z.ProgressTrack"] = Hc.Window, // same system roles as before (OkFill / Fill)
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
        foreach (var key in Contrast.Keys.Concat(Dark.Keys))
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
        // The official wordmark: dark letters on a light ground, its light version on a dark one.
        var darkGround = SystemParameters.HighContrast ? Luma(SystemColors.WindowColor) < 0.5 : _dark;
        resources.Remove(WordmarkKey);
        if (darkGround && LightWordmark() is { } light)
        {
            resources[WordmarkKey] = light;
        }
        Changed?.Invoke();
    }

    private const string WordmarkKey = "Z.Wordmark";

    private static ImageSource? _lightWordmark;

    private static ImageSource? LightWordmark()
    {
        try
        {
            if (_lightWordmark is null)
            {
                var image = new System.Windows.Media.Imaging.BitmapImage(
                    new Uri("pack://application:,,,/Zinwe.Agent.Tray;component/Assets/zinwe-logo-light.png", UriKind.Absolute));
                image.Freeze();
                _lightWordmark = image;
            }
            return _lightWordmark;
        }
        catch (Exception)
        {
            return null; // the default (dark letters) still shows
        }
    }

    private static double Luma(Color c) => (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255.0;

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

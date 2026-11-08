using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Zinwe.Agent.Core.Chat;
using Zinwe.Agent.Core.Messages;

namespace Zinwe.Agent.Tray.Views;

/// <summary>
/// v1.5 · files in chat, drawn the same in the admin chat and in team threads (inside the existing bubbles, with the
/// existing colours, so dark mode and High Contrast follow the theme): the file card (type icon, name, "2.1 MB · PDF",
/// Download → Open · Show in folder, "Kept until 16 Nov"), an image's thumbnail (max 220 px), the expired card, the risky
/// caution, the "Sending…" / Failed card of a file on its way, the chip above the composer and the paperclip button.
/// </summary>
internal static class FileCard
{
    public const double ThumbMax = 220;

    /// <summary>A stroked 24-unit icon per kind (pdf, doc, sheet, slide, zip, image, video, audio, other).</summary>
    public static string IconData(string kind) => kind switch
    {
        "pdf" or "doc" => "M14 3H7a2 2 0 0 0-2 2v14a2 2 0 0 0 2 2h10a2 2 0 0 0 2-2V8zM14 3v5h5M9 13h6M9 17h6",
        "sheet" => "M5 4h14a1 1 0 0 1 1 1v14a1 1 0 0 1-1 1H5a1 1 0 0 1-1-1V5a1 1 0 0 1 1-1zM4 10h16M4 15h16M10 4v16",
        "slide" => "M4 4h16a1 1 0 0 1 1 1v10a1 1 0 0 1-1 1H4a1 1 0 0 1-1-1V5a1 1 0 0 1 1-1zM12 16v4M8 20h8",
        "zip" => "M14 3H7a2 2 0 0 0-2 2v14a2 2 0 0 0 2 2h10a2 2 0 0 0 2-2V8zM14 3v5h5M11 5h1M11 8h1M11 11h1M10.5 14h2v3h-2z",
        "image" => "M5 4h14a1 1 0 0 1 1 1v14a1 1 0 0 1-1 1H5a1 1 0 0 1-1-1V5a1 1 0 0 1 1-1zM9 10.5a1.5 1.5 0 1 0 0-3 1.5 1.5 0 0 0 0 3zM20 15l-5-5-10 10",
        "video" => "M4 6h10a1 1 0 0 1 1 1v10a1 1 0 0 1-1 1H4a1 1 0 0 1-1-1V7a1 1 0 0 1 1-1zM15 10l6-3v10l-6-3",
        "audio" => "M9 18V5l11-2v13M9 18a3 3 0 1 1-6 0 3 3 0 0 1 6 0zM20 16a3 3 0 1 1-6 0 3 3 0 0 1 6 0z",
        _ => "M14 3H7a2 2 0 0 0-2 2v14a2 2 0 0 0 2 2h10a2 2 0 0 0 2-2V8zM14 3v5h5",
    };

    /// <summary>The type tile at the card's left: the brand tint, a red one for a program file.</summary>
    private static Border Tile(string? name, bool risky, bool muted = false) =>
        Ui.Tile(IconData(ChatFiles.IconKind(name)), 36, muted ? "Z.Fill" : risky ? "Z.RedTint" : "Z.OrangeTint",
            muted ? "Z.Muted" : risky ? "Z.RedText" : "Z.OrangeInk", 9, 18);

    /// <summary>Icon, name (one line) and "2.1 MB · PDF".</summary>
    private static Grid Head(string name, long size, bool risky, bool muted = false)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        var tile = Tile(name, risky, muted);
        tile.Margin = new Thickness(0, 0, 10, 0);
        tile.VerticalAlignment = VerticalAlignment.Center;
        grid.Children.Add(tile);
        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var title = Ui.Text(name, 13.5, FontWeights.SemiBold, muted ? "Z.Muted" : "Z.Ink", trim: true);
        title.ToolTip = name;
        texts.Children.Add(title);
        texts.Children.Add(Ui.Text($"{ChatFiles.FormatSize(size)} · {ChatFiles.TypeLabel(name)}", 12, fg: "Z.Muted"));
        Grid.SetColumn(texts, 1);
        grid.Children.Add(texts);
        return grid;
    }

    /// <summary>
    /// A file on a line: the thumbnail (an image already fetched), the card, the caution for a program file from someone
    /// else, the actions and "Kept until 16 Nov". An expired (or deleted) file is a grey card that says so.
    /// </summary>
    public static FrameworkElement Line(TrayModel m, ChatFile f, string? senderFirst, bool mine)
    {
        var stack = new StackPanel { MinWidth = 200, MaxWidth = 264 };
        if (!f.AvailableAt(m.Now) || m.DownloadOf(f) is { State: Core.Chat.FileState.Gone })
        {
            stack.Children.Add(Expired(f.Name));
            AutomationProperties.SetName(stack, $"File {f.Name}. {ChatFiles.ExpiredText}");
            return stack;
        }
        var risky = f.IsRisky;
        var downloaded = m.DownloadedPath(f);
        if (m.PreviewFor(f) is { } preview && Thumb(preview) is { } bitmap)
        {
            var thumb = Thumbnail(bitmap);
            var open = Tv.Bare(thumb, () =>
            {
                if (downloaded is not null)
                {
                    m.OpenFile(downloaded);
                }
                else
                {
                    m.Download(f, senderFirst, mine);
                }
            }, (downloaded is not null ? "Open " : "Download ") + f.Name);
            open.HorizontalAlignment = HorizontalAlignment.Left;
            open.Margin = new Thickness(0, 2, 0, 8);
            stack.Children.Add(open);
        }
        stack.Children.Add(Head(f.Name, f.Size, risky));
        if (risky && !mine)
        {
            var caution = Ui.Text(ChatFiles.RiskyText(senderFirst), 12, FontWeights.SemiBold, "Z.RedText", wrap: true);
            caution.Margin = new Thickness(0, 8, 0, 0);
            stack.Children.Add(caution);
        }
        stack.Children.Add(Actions(m, f, senderFirst, mine, downloaded));
        if (f.ExpiresAt is { } until)
        {
            var kept = Ui.Text(ChatFiles.KeptUntil(until), 11.5, fg: "Z.Muted");
            kept.Margin = new Thickness(0, 6, 0, 0);
            stack.Children.Add(kept);
        }
        AutomationProperties.SetName(stack, $"File {f.Name}, {ChatFiles.FormatSize(f.Size)}." + (risky && !mine ? " " + ChatFiles.RiskyText(senderFirst) : "")
            + (f.ExpiresAt is { } e ? " " + ChatFiles.KeptUntil(e) + "." : ""));
        return stack;
    }

    /// <summary>Download · Downloading… · Open · Show in folder · the reason it failed.</summary>
    private static FrameworkElement Actions(TrayModel m, ChatFile f, string? senderFirst, bool mine, string? downloaded)
    {
        var row = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        if (downloaded is not null)
        {
            var open = Tv.Btn("Open", true, () => m.OpenFile(downloaded), "Open " + f.Name, compact: true);
            open.Margin = new Thickness(0, 0, 6, 0);
            row.Children.Add(open);
            row.Children.Add(Tv.Btn("Show in folder", false, () => m.ShowInFolder(downloaded), "Show " + f.Name + " in its folder", compact: true));
            return row;
        }
        var state = m.DownloadOf(f);
        if (state is { State: Core.Chat.FileState.Working })
        {
            var busy = Ui.Text("Downloading…", 12.5, FontWeights.SemiBold, "Z.Ink2");
            busy.VerticalAlignment = VerticalAlignment.Center;
            Motion.Breathe(busy);
            row.Children.Add(busy);
            return row;
        }
        if (state is { State: Core.Chat.FileState.Failed })
        {
            var why = Ui.Text(state.Error ?? ChatFiles.DownloadFailedText, 12, fg: "Z.RedText", wrap: true);
            why.Margin = new Thickness(0, 0, 0, 6);
            var stack = new StackPanel();
            stack.Children.Add(why);
            stack.Children.Add(Tv.Btn("Download", false, () => m.Download(f, senderFirst, mine), "Download " + f.Name + " again", compact: true));
            row.Children.Add(stack);
            return row;
        }
        row.Children.Add(Tv.Btn("Download", false, () => m.Download(f, senderFirst, mine), "Download " + f.Name, compact: true));
        return row;
    }

    /// <summary>The grey card of a file that is no longer kept.</summary>
    public static FrameworkElement Expired(string name)
    {
        var stack = new StackPanel();
        stack.Children.Add(WithoutSize(Head(name, 0, false, muted: true)));
        var why = Ui.Text(ChatFiles.ExpiredText, 12, fg: "Z.Muted", wrap: true);
        why.Margin = new Thickness(0, 8, 0, 0);
        stack.Children.Add(why);
        return new Border { Child = stack, CornerRadius = new CornerRadius(10), Padding = new Thickness(10, 8, 10, 8) }.Res(Border.BackgroundProperty, "Z.Fill");

        static Grid WithoutSize(Grid head)
        {
            if (head.Children[1] is StackPanel texts && texts.Children.Count > 1)
            {
                texts.Children.RemoveAt(1); // the size of a file that is gone says nothing
            }
            return head;
        }
    }

    /// <summary>
    /// A file the member is sending: its name and size, then "Sending…" or the reason it failed with Retry · Delete. Without
    /// <paramref name="withStatus"/> only the name and size (the admin chat says the rest under the bubble).
    /// </summary>
    public static FrameworkElement Pending(TrayModel m, OutboxReply item, bool withStatus = true)
    {
        var name = item.FileName ?? "file";
        var stack = new StackPanel { MinWidth = 200, MaxWidth = 264 };
        stack.Children.Add(Head(name, item.FileSize ?? 0, ChatFiles.IsRisky(name)));
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
        {
            var row = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
            var retry = Tv.Btn("Retry", true, () => m.RetryFile(item), "Retry sending " + name, compact: true);
            retry.Margin = new Thickness(0, 0, 6, 0);
            row.Children.Add(retry);
            row.Children.Add(Tv.Btn("Delete", false, () => m.RemoveFile(item), "Delete " + name, compact: true));
            stack.Children.Add(row);
        }
        AutomationProperties.SetName(stack, $"{name} wasn’t sent. {item.Error ?? ChatFiles.FailedText}");
        return stack;
    }

    /// <summary>The picked file above the composer: icon, name, size and ✕ (the typed text becomes its caption).</summary>
    public static FrameworkElement Chip(AttachDraft draft, Action remove)
    {
        var grid = Head(draft.Name, draft.Size, ChatFiles.IsRisky(draft.Name));
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var x = Tv.Icon(Tv.IcoX, remove, "Remove " + draft.Name, "Transparent", 12, "Z.Ink2", 2.4);
        x.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(x, 2);
        grid.Children.Add(x);
        var chip = new Border
        {
            Child = grid, CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1), Padding = new Thickness(8, 4, 2, 4),
            Margin = new Thickness(0, 4, 0, 6),
        }.Res(Border.BackgroundProperty, "Z.Fill").Res(Border.BorderBrushProperty, "Z.Line");
        AutomationProperties.SetName(chip, $"Attached: {draft.Name}, {ChatFiles.FormatSize(draft.Size)}. Your message goes with it.");
        return chip;
    }

    /// <summary>The paperclip in a team thread's composer (40 px hit area, tooltip "Attach a file").</summary>
    public static Button AttachButton(Action pick)
    {
        var b = Tv.Icon(Ui.Icon.Clip, pick, ChatFiles.AttachTooltip, "Transparent", 18, "Z.Muted", 2);
        b.VerticalAlignment = VerticalAlignment.Bottom;
        return b;
    }

    /// <summary>The Windows file picker (one file). Null when the member cancelled.</summary>
    public static string? Pick(DependencyObject from)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = ChatFiles.AttachTooltip, CheckFileExists = true, Multiselect = false, Filter = "All files (*.*)|*.*",
        };
        return dialog.ShowDialog(Window.GetWindow(from)) == true ? dialog.FileName : null;
    }

    /// <summary>Picks a file for <paramref name="key"/> and hands it to the model (refused at once when too big).</summary>
    public static void PickFor(DependencyObject from, TrayModel? m, string? key)
    {
        if (m is null || key is null)
        {
            return;
        }
        if (Pick(from) is { } path)
        {
            m.Attach(key, path);
        }
    }

    // ---- Thumbnails --------------------------------------------------------------------------------------------------

    private static readonly Dictionary<string, BitmapSource?> Thumbs = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The image decoded once (at twice the thumbnail size, for sharp HiDPI), or null when Windows can't read it.</summary>
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
            image.DecodePixelWidth = (int)(ThumbMax * 2);
            image.EndInit();
            image.Freeze();
            bitmap = image.PixelWidth > 0 && image.PixelHeight > 0 ? image : null;
        }
        catch (Exception e) when (e is IOException or NotSupportedException or UriFormatException or ArgumentException or InvalidOperationException
            or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            bitmap = null; // not decodable here (a WebP without its codec): the card shows without a thumbnail
        }
        if (Thumbs.Count > 60)
        {
            Thumbs.Clear();
        }
        Thumbs[path] = bitmap;
        return bitmap;
    }

    /// <summary>The thumbnail: at most 220 × 220, its own shape, rounded 8.</summary>
    private static Border Thumbnail(BitmapSource bitmap)
    {
        var ratio = (double)bitmap.PixelHeight / bitmap.PixelWidth;
        var width = ratio > 1 ? ThumbMax / ratio : ThumbMax;
        var height = Math.Min(ThumbMax, width * ratio);
        var border = new Border
        {
            Width = Math.Max(48, width), Height = Math.Max(48, height), CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1),
            Background = new ImageBrush(bitmap) { Stretch = Stretch.UniformToFill }, Cursor = Cursors.Hand,
        }.Res(Border.BorderBrushProperty, "Z.Line");
        return border;
    }
}

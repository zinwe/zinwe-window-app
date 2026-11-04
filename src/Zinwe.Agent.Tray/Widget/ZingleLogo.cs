using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Zinwe.Agent.Core.Ipc;

namespace Zinwe.Agent.Tray.Widget;

/// <summary>What the title bar shows: the normal wordmark (<see cref="IsNone"/>), or a Zingle file, animated or still.</summary>
/// <param name="File">The Zingle file to show; null = the normal wordmark.</param>
/// <param name="Animated">True when <paramref name="File"/> is a GIF to play.</param>
/// <param name="Alt">The screen reader name.</param>
public sealed record ZingleChoice(string? File, bool Animated, string Alt)
{
    public static readonly ZingleChoice None = new(null, false, "Zinwe");

    public bool IsNone => File is null;

    /// <summary>Changes whenever something else must be shown (the window redraws the logo only then).</summary>
    public string Stamp => $"{File}|{Animated}|{Alt}";
}

/// <summary>
/// The special-day logo (Zingle) in place of the title bar's wordmark. The rules, kept here so they're tested:
/// <list type="bullet">
/// <item>no Zingle on the status (none today, an older service, no service) → the normal wordmark;</item>
/// <item>dark mode or High Contrast → the normal wordmark (the Zingle art is made for the light title bar);</item>
/// <item>animations on and a GIF → the GIF, played; a WebP, or animations off → the still PNG;</item>
/// <item>a file that isn't there → the still, else the normal wordmark.</item>
/// </list>
/// </summary>
public static class ZingleLogo
{
    public static ZingleChoice Choose(ZingleStatus? zingle, bool animationsOn, bool lightGround, Func<string, bool>? fileExists = null)
    {
        fileExists ??= File.Exists;
        if (zingle is null || !lightGround)
        {
            return ZingleChoice.None;
        }
        var alt = string.IsNullOrWhiteSpace(zingle.Alt) ? "Zinwe" : zingle.Alt.Trim();
        if (animationsOn && zingle.AnimatedFile is { } animated && IsGif(animated) && fileExists(animated))
        {
            return new ZingleChoice(animated, true, alt);
        }
        if (!string.IsNullOrEmpty(zingle.StillFile) && fileExists(zingle.StillFile))
        {
            return new ZingleChoice(zingle.StillFile, false, alt);
        }
        return ZingleChoice.None;
    }

    private static bool IsGif(string path) => path.EndsWith(".gif", StringComparison.OrdinalIgnoreCase);

    /// <summary>A still image from a file, read fully (the file isn't held open: the service may replace it). Null on any error.</summary>
    public static BitmapSource? LoadStill(string path)
    {
        try
        {
            using var stream = new MemoryStream(File.ReadAllBytes(path));
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException
            or ArgumentException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            return null;
        }
    }
}

/// <summary>One composed frame of an animated GIF and how long it shows.</summary>
public sealed record GifFrame(BitmapSource Image, TimeSpan Delay);

/// <summary>
/// WPF shows only a GIF's first frame, so the frames are decoded here (<see cref="GifBitmapDecoder"/>) and composed onto
/// the GIF's canvas the way browsers do: each frame at its offset, then disposal 2 clears its area and 3 puts back
/// what was there. Delays come from <c>/grctlext/Delay</c> (hundredths of a second; under 2 → 100 ms, as browsers).
/// </summary>
public static class GifFrames
{
    /// <summary>The most frames / pixels decoded (a Zingle is ~30 frames of 390 × 96).</summary>
    public const int MaxFrames = 300;
    public const int MaxSide = 2048;

    /// <summary>A GIF delay (hundredths of a second) as a time; 0 and 1 mean "as fast as possible" → 100 ms.</summary>
    public static TimeSpan Delay(int hundredths) => TimeSpan.FromMilliseconds(hundredths < 2 ? 100 : hundredths * 10);

    /// <summary>The composed, frozen frames; empty when the bytes aren't a GIF that decodes.</summary>
    public static IReadOnlyList<GifFrame> Decode(byte[] bytes)
    {
        try
        {
            using var stream = new MemoryStream(bytes);
            var decoder = new GifBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            if (decoder.Frames.Count == 0)
            {
                return [];
            }
            var first = decoder.Frames[0];
            var width = Read(decoder.Metadata, "/logscrdesc/Width", first.PixelWidth);
            var height = Read(decoder.Metadata, "/logscrdesc/Height", first.PixelHeight);
            if (width <= 0 || height <= 0 || width > MaxSide || height > MaxSide)
            {
                return [];
            }
            var stride = width * 4;
            var canvas = new byte[stride * height];
            var frames = new List<GifFrame>();
            foreach (var frame in decoder.Frames.Take(MaxFrames))
            {
                var meta = frame.Metadata as BitmapMetadata;
                var left = Read(meta, "/imgdesc/Left", 0);
                var top = Read(meta, "/imgdesc/Top", 0);
                var disposal = Read(meta, "/grctlext/Disposal", 0);
                var delay = Delay(Read(meta, "/grctlext/Delay", 0));

                var previous = disposal == 3 ? (byte[])canvas.Clone() : null;
                var (fw, fh) = (frame.PixelWidth, frame.PixelHeight);
                var pixels = new byte[fw * fh * 4];
                new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0).CopyPixels(pixels, fw * 4, 0);
                for (var y = 0; y < fh; y++)
                {
                    var cy = top + y;
                    if (cy < 0 || cy >= height)
                    {
                        continue;
                    }
                    for (var x = 0; x < fw; x++)
                    {
                        var cx = left + x;
                        var src = (y * fw + x) * 4;
                        if (cx < 0 || cx >= width || pixels[src + 3] == 0)
                        {
                            continue; // transparent in this frame: what was there shows through
                        }
                        Buffer.BlockCopy(pixels, src, canvas, cy * stride + cx * 4, 4);
                    }
                }
                var composed = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, canvas, stride);
                composed.Freeze();
                frames.Add(new GifFrame(composed, delay));

                if (disposal == 2)
                {
                    for (var y = Math.Max(0, top); y < Math.Min(height, top + fh); y++)
                    {
                        var from = y * stride + Math.Max(0, left) * 4;
                        var to = y * stride + Math.Min(width, left + fw) * 4;
                        if (to > from)
                        {
                            Array.Clear(canvas, from, to - from);
                        }
                    }
                }
                else if (previous is not null)
                {
                    canvas = previous;
                }
            }
            return frames;
        }
        catch (Exception e) when (e is IOException or NotSupportedException or ArgumentException or InvalidOperationException
            or FileFormatException or OverflowException or System.Runtime.InteropServices.COMException)
        {
            return [];
        }
    }

    private static int Read(ImageMetadata? metadata, string query, int fallback)
    {
        try
        {
            return metadata is BitmapMetadata meta && meta.ContainsQuery(query) ? Convert.ToInt32(meta.GetQuery(query)) : fallback;
        }
        catch (Exception e) when (e is NotSupportedException or InvalidOperationException or FormatException
            or InvalidCastException or OverflowException or ArgumentException or System.Runtime.InteropServices.COMException)
        {
            return fallback;
        }
    }
}

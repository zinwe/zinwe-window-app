using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Zinwe.Agent.Tray.Widget;

/// <summary>A photo ready to upload: a 256 × 256 JPEG with no metadata.</summary>
/// <param name="SourceBytes">The size of the file the member picked.</param>
public sealed record CompressedPhoto(byte[] Jpeg, int Quality, long SourceBytes, int Width, int Height);

/// <summary>A photo the tray won't use; <see cref="Exception.Message"/> is what the member reads.</summary>
public sealed class PhotoRejectedException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// FR-44 · the member's photo, made small before it leaves the PC (the plan's sizes): a centred square crop scaled to
/// 256 × 256, the EXIF orientation applied, transparency put on white, encoded as JPEG at quality 82, then 70 if that
/// is over 60 KB (and 55 as a last step, so the promise holds for very noisy pictures). A new bitmap is encoded, so no
/// EXIF, GPS or other metadata survives. Sources: jpg, png, bmp or gif (first frame), up to 10 MB.
/// </summary>
public static class PictureCompress
{
    public const int Size = 256;
    public const long MaxSourceBytes = 10L * 1024 * 1024;
    public const int TargetBytes = 60 * 1024;
    public static readonly int[] Qualities = [82, 70, 55];
    public static readonly string[] Extensions = [".jpg", ".jpeg", ".png", ".bmp", ".gif"];

    /// <summary>The OpenFileDialog filter for <see cref="Extensions"/>.</summary>
    public const string DialogFilter = "Photos (*.jpg;*.jpeg;*.png;*.bmp;*.gif)|*.jpg;*.jpeg;*.png;*.bmp;*.gif";

    // Above this many pixels on a side the decoder is asked for a smaller image straight away (fast, little memory).
    private const int DecodeAbove = 768;
    private const int DecodeTo = 512;
    private const int MaxSide = 20_000;

    public static CompressedPhoto FromFile(string path)
    {
        if (!Extensions.Contains(Path.GetExtension(path).ToLowerInvariant()))
        {
            throw new PhotoRejectedException("That file isn’t a JPG, PNG, BMP or GIF photo. Try another one.");
        }
        var info = new FileInfo(path);
        if (!info.Exists || info.Length == 0)
        {
            throw new PhotoRejectedException("That photo couldn’t be opened. Try another one.");
        }
        if (info.Length > MaxSourceBytes)
        {
            throw new PhotoRejectedException("That photo is over 10 MB. Pick a smaller one.");
        }
        return FromBytes(File.ReadAllBytes(path));
    }

    /// <param name="targetBytes">The size to get under (the plan's 60 KB; tests use smaller ones to see the quality steps).</param>
    public static CompressedPhoto FromBytes(byte[] source, int targetBytes = TargetBytes)
    {
        if (source.LongLength > MaxSourceBytes)
        {
            throw new PhotoRejectedException("That photo is over 10 MB. Pick a smaller one.");
        }
        try
        {
            var square = Square256(source);
            byte[]? jpeg = null;
            var quality = 0;
            foreach (var q in Qualities)
            {
                (jpeg, quality) = (Encode(square, q), q);
                if (jpeg.Length <= targetBytes)
                {
                    break;
                }
            }
            if (jpeg!.Length > Zinwe.Agent.Core.Ipc.PictureChoice.MaxJpegBytes)
            {
                throw new PhotoRejectedException("That photo couldn’t be made small enough. Try another one.");
            }
            return new CompressedPhoto(jpeg, quality, source.LongLength, square.PixelWidth, square.PixelHeight);
        }
        catch (Exception e) when (e is NotSupportedException or FileFormatException or ArgumentException or InvalidOperationException
            or OverflowException or IOException or System.Runtime.InteropServices.COMException)
        {
            throw new PhotoRejectedException("That photo couldn’t be opened. Try another one.", e);
        }
    }

    /// <summary>Decode → orientation → centre square → 256 px → opaque Bgr24 on white.</summary>
    private static BitmapSource Square256(byte[] source)
    {
        var frame = BitmapDecoder.Create(new MemoryStream(source), BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.None).Frames[0];
        var (w, h) = (frame.PixelWidth, frame.PixelHeight);
        if (w < 1 || h < 1 || w > MaxSide || h > MaxSide)
        {
            throw new PhotoRejectedException("That photo’s size can’t be used. Try another one.");
        }
        var orientation = Orientation(frame);

        BitmapSource image = frame;
        if (Math.Min(w, h) > DecodeAbove)
        {
            // Let the decoder shrink it (keeps the aspect ratio when only one side is set).
            var smaller = new BitmapImage();
            smaller.BeginInit();
            smaller.StreamSource = new MemoryStream(source);
            smaller.CacheOption = BitmapCacheOption.OnLoad;
            // (No IgnoreImageCache: with a StreamSource it throws inside WPF; a stream is never cached anyway.)
            smaller.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            if (w <= h)
            {
                smaller.DecodePixelWidth = DecodeTo;
            }
            else
            {
                smaller.DecodePixelHeight = DecodeTo;
            }
            smaller.EndInit();
            image = smaller;
        }
        image = Orient(image, orientation);

        var side = Math.Min(image.PixelWidth, image.PixelHeight);
        BitmapSource square = new CroppedBitmap(image, new Int32Rect((image.PixelWidth - side) / 2, (image.PixelHeight - side) / 2, side, side));
        if (side != Size)
        {
            square = new TransformedBitmap(square, new ScaleTransform((double)Size / side, (double)Size / side));
        }

        // Read whatever came out as BGRA, put it on white, and write exactly 256 × 256 BGR (no alpha, no metadata).
        var bgra = new FormatConvertedBitmap(square, PixelFormats.Bgra32, null, 0);
        var (sw, sh) = (bgra.PixelWidth, bgra.PixelHeight);
        var pixels = new byte[sw * sh * 4];
        bgra.CopyPixels(pixels, sw * 4, 0);
        var output = new byte[Size * Size * 3];
        for (var y = 0; y < Size; y++)
        {
            var sy = Math.Min(y, sh - 1);
            for (var x = 0; x < Size; x++)
            {
                var sx = Math.Min(x, sw - 1);
                var i = (sy * sw + sx) * 4;
                var a = pixels[i + 3];
                var o = (y * Size + x) * 3;
                for (var c = 0; c < 3; c++)
                {
                    output[o + c] = (byte)((pixels[i + c] * a + 255 * (255 - a) + 127) / 255);
                }
            }
        }
        var result = BitmapSource.Create(Size, Size, 96, 96, PixelFormats.Bgr24, null, output, Size * 3);
        result.Freeze();
        return result;
    }

    private static byte[] Encode(BitmapSource square, int quality)
    {
        var encoder = new JpegBitmapEncoder { QualityLevel = quality };
        encoder.Frames.Add(BitmapFrame.Create(square));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    /// <summary>EXIF orientation 1-8 (1 when there is none).</summary>
    private static int Orientation(BitmapFrame frame)
    {
        try
        {
            if (frame.Metadata is BitmapMetadata meta && meta.ContainsQuery("System.Photo.Orientation")
                && meta.GetQuery("System.Photo.Orientation") is ushort o and >= 1 and <= 8)
            {
                return o;
            }
        }
        catch (Exception e) when (e is NotSupportedException or InvalidOperationException or ArgumentException
            or System.Runtime.InteropServices.COMException)
        {
            // no readable metadata
        }
        return 1;
    }

    /// <summary>Turn the picture upright as the camera meant it (EXIF 2-8: mirrors and quarter turns).</summary>
    private static BitmapSource Orient(BitmapSource image, int orientation)
    {
        var (angle, mirror) = orientation switch
        {
            2 => (0, true),
            3 => (180, false),
            4 => (180, true),
            5 => (270, true),
            6 => (90, false),
            7 => (90, true),
            8 => (270, false),
            _ => (0, false),
        };
        if (angle == 0 && !mirror)
        {
            return image;
        }
        var transform = new TransformGroup();
        if (mirror)
        {
            transform.Children.Add(new ScaleTransform(-1, 1));
        }
        if (angle != 0)
        {
            transform.Children.Add(new RotateTransform(angle));
        }
        return new TransformedBitmap(image, transform);
    }

    /// <summary>"4.2 MB", "23 KB".</summary>
    public static string Bytes(long b) => b >= 1_048_576 ? $"{b / 1_048_576.0:0.0} MB" : $"{Math.Max(1, (b + 512) / 1024)} KB";
}

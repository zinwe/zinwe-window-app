using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Zinwe.Agent.Tray.Widget;

/// <summary>Any photo → a centred 256 × 256 px JPEG (about 100 KB), the size the board promises.</summary>
public static class PhotoShrink
{
    public const int Size = 256;

    /// <summary>Writes <paramref name="target"/> and returns its size in bytes.</summary>
    public static long To256(string source, string target)
    {
        BitmapSource image;
        using (var stream = File.OpenRead(source))
        {
            var frame = BitmapFrame.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            image = frame;
        }
        // Centre square crop, then scale to 256.
        var side = Math.Min(image.PixelWidth, image.PixelHeight);
        var crop = new CroppedBitmap(image, new System.Windows.Int32Rect((image.PixelWidth - side) / 2, (image.PixelHeight - side) / 2, side, side));
        var scale = (double)Size / side;
        var scaled = new TransformedBitmap(crop, new ScaleTransform(scale, scale));
        var encoder = new JpegBitmapEncoder { QualityLevel = 85 };
        encoder.Frames.Add(BitmapFrame.Create(scaled));
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var tmp = target + ".tmp";
        using (var output = File.Create(tmp))
        {
            encoder.Save(output);
        }
        File.Move(tmp, target, overwrite: true);
        return new FileInfo(target).Length;
    }
}

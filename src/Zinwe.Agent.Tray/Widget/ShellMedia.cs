using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Zinwe.Agent.Core.Chat;

namespace Zinwe.Agent.Tray.Widget;

/// <summary>What the tray learns about a picked file: its thumbnail (a JPEG ≤ 40 KB), its size in pixels and a video's length.</summary>
public sealed record MediaInfo(byte[]? Thumb = null, int? Width = null, int? Height = null, double? DurationS = null);

/// <summary>The seam the tray makes thumbnails through (Windows' shell, or a test's own).</summary>
public interface IShellMedia
{
    MediaInfo Describe(string path);
}

/// <summary>
/// v1.5 round 2 · thumbnails and video lengths from Windows itself, at pick time (BRIEF2 §3): the shell's thumbnail
/// (<c>IShellItemImageFactory</c>, which knows images, videos, PDFs and Office files when their handlers are installed) at
/// most 320 px, as a JPEG of at most 40 KB; an image's pixels from its header; a video's frame size and length from its
/// shell properties. Anything Windows can't say is left out (the tile shows its type icon). Never throws.
/// </summary>
public sealed class ShellMedia : IShellMedia
{
    public MediaInfo Describe(string path)
    {
        try
        {
            var thumb = Thumbnail(path, ChatFiles.ThumbMaxPixels) is { } bitmap ? EncodeThumb(bitmap) : null;
            var kind = ChatFiles.KindOf(path);
            var (w, h) = kind == ChatFile.ImageKind ? ImagePixels(path) : (null, null);
            double? duration = null;
            if (kind == ChatFile.VideoKind)
            {
                duration = Property(path, MediaDuration) is { } ticks and > 0 ? ticks / 10_000_000d : null;
                w = Property(path, VideoWidth) is { } vw and > 0 and < 100_000 ? (int)vw : null;
                h = Property(path, VideoHeight) is { } vh and > 0 and < 100_000 ? (int)vh : null;
            }
            return new MediaInfo(thumb, w, h, duration);
        }
        catch (Exception e) when (e is COMException or IOException or UnauthorizedAccessException or ArgumentException
            or NotSupportedException or InvalidOperationException or FileFormatException or OverflowException)
        {
            return new MediaInfo();
        }
    }

    /// <summary>
    /// A thumbnail as a JPEG within <see cref="ChatFiles.ThumbMaxBytes"/> and <see cref="ChatFiles.ThumbMaxPixels"/>: the
    /// quality steps down, then the size, until it fits (transparent parts on white). Null when it never fits.
    /// </summary>
    public static byte[]? EncodeThumb(BitmapSource source, int maxBytes = ChatFiles.ThumbMaxBytes, int maxPixels = ChatFiles.ThumbMaxPixels)
    {
        var scale = Math.Min(1, (double)maxPixels / Math.Max(source.PixelWidth, source.PixelHeight));
        for (var round = 0; round < 6; round++, scale *= 0.75)
        {
            var w = Math.Max(1, (int)Math.Round(source.PixelWidth * scale));
            var h = Math.Max(1, (int)Math.Round(source.PixelHeight * scale));
            var flat = Flatten(source, w, h);
            foreach (var quality in new[] { 82, 70, 58, 45 })
            {
                var encoder = new JpegBitmapEncoder { QualityLevel = quality };
                encoder.Frames.Add(BitmapFrame.Create(flat));
                using var ms = new MemoryStream();
                encoder.Save(ms);
                if (ms.Length <= maxBytes)
                {
                    return ms.ToArray();
                }
            }
        }
        return null;
    }

    /// <summary>The picture at w × h on white (a JPEG has no transparency).</summary>
    private static BitmapSource Flatten(BitmapSource source, int w, int h)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, w, h));
            dc.DrawImage(source, new Rect(0, 0, w, h));
        }
        var target = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        target.Render(visual);
        target.Freeze();
        return target;
    }

    /// <summary>The shell's thumbnail (never its icon), fitted in <paramref name="size"/>; null when it has none.</summary>
    internal static BitmapSource? Thumbnail(string path, int size)
    {
        var iid = typeof(IShellItemImageFactory).GUID;
        if (SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out var item) != 0 || item is not IShellItemImageFactory factory)
        {
            return null;
        }
        try
        {
            if (factory.GetImage(new NativeSize { cx = size, cy = size }, SiigbfResizeToFit | SiigbfThumbnailOnly, out var hbitmap) != 0
                || hbitmap == IntPtr.Zero)
            {
                return null;
            }
            try
            {
                var bitmap = Imaging.CreateBitmapSourceFromHBitmap(hbitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                bitmap.Freeze();
                return bitmap.PixelWidth > 0 && bitmap.PixelHeight > 0 ? bitmap : null;
            }
            finally
            {
                DeleteObject(hbitmap);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(factory);
        }
    }

    /// <summary>An image's width and height from its header (nothing decoded).</summary>
    private static (int?, int?) ImagePixels(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var frame = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.None).Frames[0];
            return (frame.PixelWidth, frame.PixelHeight);
        }
        catch (Exception e) when (e is IOException or NotSupportedException or FileFormatException or COMException or ArgumentException
            or InvalidOperationException or UnauthorizedAccessException)
        {
            return (null, null);
        }
    }

    // ---- Shell properties (PKEYs from propkey.h) -----------------------------------------------------------------------

    /// <summary>System.Media.Duration: 100 ns units.</summary>
    private static readonly PropertyKey MediaDuration = new(new Guid("64440490-4C8B-11D1-8B70-080036B11A03"), 3);
    /// <summary>System.Video.FrameWidth / FrameHeight.</summary>
    private static readonly PropertyKey VideoWidth = new(new Guid("64440491-4C8B-11D1-8B70-080036B11A03"), 3);
    private static readonly PropertyKey VideoHeight = new(new Guid("64440491-4C8B-11D1-8B70-080036B11A03"), 4);

    /// <summary>A numeric shell property of a file, or null.</summary>
    private static ulong? Property(string path, PropertyKey key)
    {
        var iid = typeof(IPropertyStore).GUID;
        if (SHGetPropertyStoreFromParsingName(path, IntPtr.Zero, GpsDefault, ref iid, out var raw) != 0 || raw is not IPropertyStore store)
        {
            return null;
        }
        var value = new PropVariant();
        try
        {
            if (store.GetValue(ref key, out value) != 0)
            {
                return null;
            }
            return value.vt switch
            {
                VtUi8 or VtI8 => value.number,
                VtUi4 or VtI4 => value.number & 0xFFFFFFFF,
                _ => null,
            };
        }
        finally
        {
            PropVariantClear(ref value);
            Marshal.ReleaseComObject(store);
        }
    }

    private const int SiigbfResizeToFit = 0x00;
    private const int SiigbfThumbnailOnly = 0x08;
    private const int GpsDefault = 0;
    private const ushort VtI4 = 3, VtUi4 = 19, VtI8 = 20, VtUi8 = 21;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeSize
    {
        public int cx;
        public int cy;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct PropertyKey(Guid fmtid, uint pid)
    {
        public Guid fmtid = fmtid;
        public uint pid = pid;
    }

    /// <summary>A PROPVARIANT with a number (the only kinds read here); 24 bytes on x64.</summary>
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PropVariant
    {
        [FieldOffset(0)] public ushort vt;
        [FieldOffset(8)] public ulong number;
    }

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig]
        int GetImage(NativeSize size, int flags, out IntPtr phbm);
    }

    [ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig]
        int GetCount(out uint count);

        [PreserveSig]
        int GetAt(uint index, out PropertyKey key);

        [PreserveSig]
        int GetValue(ref PropertyKey key, out PropVariant value);

        [PreserveSig]
        int SetValue(ref PropertyKey key, ref PropVariant value);

        [PreserveSig]
        int Commit();
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(string path, IntPtr bindContext, ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out object? item);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHGetPropertyStoreFromParsingName(string path, IntPtr bindContext, int flags, ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out object? store);

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropVariant value);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr handle);
}

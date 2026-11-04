using System.IO;
using System.Windows.Media.Imaging;
using Zinwe.Agent.Core.Ipc;
using Zinwe.Agent.Tray.Widget;

namespace Zinwe.Agent.Tray.Tests;

/// <summary>
/// The title bar's special-day logo (Zingle): which file shows (or the normal wordmark), and the GIF frames the window
/// plays (composition, disposal, delays).
/// </summary>
public sealed class ZingleLogoTests
{
    private const string Still = @"C:\w\zingle-1-9-still.png";
    private const string Gif = @"C:\w\zingle-1-9-animated.gif";
    private static readonly ZingleStatus Live = new(1, "Zinwe, lit up for Diwali", Still, Gif);

    private static bool All(string _) => true;

    // ---- What shows --------------------------------------------------------------------------------

    [Fact]
    public void No_zingle_on_the_status_shows_the_normal_wordmark()
    {
        var choice = ZingleLogo.Choose(null, animationsOn: true, lightGround: true, All);
        Assert.True(choice.IsNone);
        Assert.Same(ZingleChoice.None, choice);
        Assert.Equal("Zinwe", choice.Alt);
    }

    [Fact]
    public void A_live_zingle_plays_its_gif_with_its_alt_text()
    {
        var choice = ZingleLogo.Choose(Live, animationsOn: true, lightGround: true, All);
        Assert.Equal(new ZingleChoice(Gif, true, "Zinwe, lit up for Diwali"), choice);
    }

    [Fact]
    public void Animations_off_in_Windows_show_the_still()
    {
        Assert.Equal(new ZingleChoice(Still, false, Live.Alt), ZingleLogo.Choose(Live, animationsOn: false, lightGround: true, All));
    }

    [Fact]
    public void A_webp_or_a_missing_animation_shows_the_still()
    {
        var webp = Live with { AnimatedFile = @"C:\w\zingle-1-9-animated.webp" };
        Assert.Equal(Still, ZingleLogo.Choose(webp, true, true, All).File);
        Assert.Equal(Still, ZingleLogo.Choose(Live with { AnimatedFile = null }, true, true, All).File);
        Assert.Equal(Still, ZingleLogo.Choose(Live, true, true, f => f != Gif).File);
    }

    [Fact]
    public void Dark_mode_high_contrast_or_missing_files_show_the_normal_wordmark()
    {
        Assert.True(ZingleLogo.Choose(Live, true, lightGround: false, All).IsNone);
        Assert.True(ZingleLogo.Choose(Live, false, true, _ => false).IsNone);
        Assert.Equal("Zinwe", ZingleLogo.Choose(Live with { Alt = " " }, true, true, All).Alt);
    }

    [Fact]
    public void A_still_that_cant_be_read_is_null_so_the_wordmark_stays() =>
        Assert.Null(ZingleLogo.LoadStill(Path.Combine(Path.GetTempPath(), "zinwe-no-such-zingle.png")));

    // ---- GIF frames -------------------------------------------------------------------------------

    [Theory]
    [InlineData(0, 100)]
    [InlineData(1, 100)]
    [InlineData(2, 20)]
    [InlineData(8, 80)]
    [InlineData(150, 1500)]
    public void Delays_are_hundredths_with_the_browser_minimum(int hundredths, int ms) =>
        Assert.Equal(TimeSpan.FromMilliseconds(ms), GifFrames.Delay(hundredths));

    [Fact]
    public void Garbage_decodes_to_no_frames() => Assert.Empty(GifFrames.Decode("not a gif"u8.ToArray()));

    [Fact]
    public void Frames_are_composed_on_the_canvas_with_their_delays()
    {
        // 2 × 2 red, then one blue pixel at (1, 1) drawn over it (disposal 1: keep).
        var frames = GifFrames.Decode(TinyGif.Make(disposalFirst: 1));

        Assert.Equal(2, frames.Count);
        Assert.Equal(TimeSpan.FromMilliseconds(50), frames[0].Delay);
        Assert.Equal(TimeSpan.FromMilliseconds(100), frames[1].Delay); // 0 = as fast as possible → 100 ms
        Assert.Equal((2, 2), (frames[1].Image.PixelWidth, frames[1].Image.PixelHeight));
        Assert.Equal(Red, Pixel(frames[0].Image, 1, 1));
        Assert.Equal(Red, Pixel(frames[1].Image, 0, 0)); // the first frame shows through
        Assert.Equal(Blue, Pixel(frames[1].Image, 1, 1));
        Assert.True(frames.All(f => f.Image.IsFrozen));
    }

    [Fact]
    public void Disposal_2_clears_the_frame_before_the_next()
    {
        var frames = GifFrames.Decode(TinyGif.Make(disposalFirst: 2));
        Assert.Equal(0u, Pixel(frames[1].Image, 0, 0) >> 24); // cleared to transparent
        Assert.Equal(Blue, Pixel(frames[1].Image, 1, 1));
    }

    private const uint Red = 0xFFFF0000;
    private const uint Blue = 0xFF0000FF;

    /// <summary>ARGB of one pixel.</summary>
    private static uint Pixel(BitmapSource image, int x, int y)
    {
        var px = new byte[4];
        image.CopyPixels(new System.Windows.Int32Rect(x, y, 1, 1), px, 4, 0);
        return (uint)(px[3] << 24 | px[2] << 16 | px[1] << 8 | px[0]);
    }

    /// <summary>A hand-built two-frame GIF89a (2 × 2, four colours), so the test needs no binary fixture.</summary>
    private static class TinyGif
    {
        public static byte[] Make(int disposalFirst)
        {
            var g = new List<byte>();
            g.AddRange("GIF89a"u8.ToArray());
            g.AddRange([2, 0, 2, 0, 0xF1, 0, 0]); // 2 × 2, global table of 4 colours
            g.AddRange([255, 0, 0, 0, 255, 0, 0, 0, 255, 255, 255, 255]); // red, green, blue, white
            Frame(g, disposalFirst, delay: 5, left: 0, top: 0, width: 2, height: 2, [0, 0, 0, 0]);
            Frame(g, disposal: 1, delay: 0, left: 1, top: 1, width: 1, height: 1, [2]);
            g.Add(0x3B);
            return [.. g];
        }

        private static void Frame(List<byte> g, int disposal, int delay, int left, int top, int width, int height, byte[] pixels)
        {
            g.AddRange([0x21, 0xF9, 4, (byte)(disposal << 2), (byte)delay, 0, 0, 0]);
            g.AddRange([0x2C, (byte)left, 0, (byte)top, 0, (byte)width, 0, (byte)height, 0, 0]);
            g.Add(2); // LZW minimum code size: codes 0-3 colours, 4 clear, 5 end
            var data = Lzw(pixels);
            g.Add((byte)data.Length);
            g.AddRange(data);
            g.Add(0);
        }

        /// <summary>Literal codes only, 3 bits wide: a clear code before every two pixels keeps the width from growing.</summary>
        private static byte[] Lzw(byte[] pixels)
        {
            var codes = new List<int>();
            for (var i = 0; i < pixels.Length; i++)
            {
                if (i % 2 == 0)
                {
                    codes.Add(4);
                }
                codes.Add(pixels[i]);
            }
            codes.Add(5);
            var bytes = new List<byte>();
            int buffer = 0, bits = 0;
            foreach (var code in codes)
            {
                buffer |= code << bits;
                bits += 3;
                while (bits >= 8)
                {
                    bytes.Add((byte)(buffer & 0xFF));
                    buffer >>= 8;
                    bits -= 8;
                }
            }
            if (bits > 0)
            {
                bytes.Add((byte)buffer);
            }
            return [.. bytes];
        }
    }
}

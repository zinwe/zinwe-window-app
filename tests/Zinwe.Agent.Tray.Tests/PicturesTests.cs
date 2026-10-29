using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Zinwe.Agent.Core.Ipc;
using Zinwe.Agent.Tray.Widget;

namespace Zinwe.Agent.Tray.Tests;

/// <summary>
/// FR-44 · team pictures in the tray: the shared avatar library (byte-identical, every path a valid WPF geometry),
/// the photo compression (≤ 60 KB, 256 px, no EXIF, upright, on white), the optimistic pick, and no control without
/// a service + backend that have pictures.
/// </summary>
public sealed class PicturesTests : IDisposable
{
    /// <summary>
    /// SHA-256 of zinwe-frontend/src/dashboard/team/avatars.json as copied (= zinwe-wt/_shared/avatars.json). When the
    /// library changes there: copy the file over Assets/avatars.json unchanged and update this hash.
    /// </summary>
    private const string LibrarySha256 = "59ce414cdc487ec524b64de17dabde717c939b5f3325b9343e8be0d794e7995e";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "zinwe-tray-pic-" + Guid.NewGuid().ToString("N"));

    public PicturesTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // best effort
        }
    }

    private static void OnSta(Action body)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                body();
            }
            catch (Exception e)
            {
                error = e;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null)
        {
            throw new Xunit.Sdk.XunitException(error.ToString());
        }
    }

    // ---- The library ---------------------------------------------------------------------------

    [Fact]
    public void The_embedded_library_is_byte_identical_to_the_shared_one()
    {
        using var stream = typeof(AvatarLibrary).Assembly.GetManifestResourceStream(AvatarLibrary.ResourceName)!;
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        var bytes = copy.ToArray();

        Assert.Equal(LibrarySha256, Convert.ToHexStringLower(SHA256.HashData(bytes)));
        // On a PC with the shared folder (the FR-44 worktrees), compare with it directly too.
        var shared = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, @"..\..\..\..\..\..\_shared\avatars.json"));
        if (File.Exists(shared))
        {
            Assert.Equal(File.ReadAllBytes(shared), bytes);
        }
    }

    [Fact]
    public void The_library_has_sixteen_people_with_stable_keys_and_every_path_parses_as_a_wpf_geometry()
    {
        Assert.Equal(1, AvatarLibrary.Version);
        Assert.Equal(48, AvatarLibrary.ViewSize);
        var all = AvatarLibrary.All;
        Assert.Equal(16, all.Count);
        Assert.Equal(Enumerable.Range(1, 16).Select(i => $"a{i:00}"), all.Select(a => a.Key));
        Assert.Equal(16, all.Select(a => a.Label).Distinct().Count());
        OnSta(() =>
        {
            foreach (var avatar in all)
            {
                Assert.IsType<Color>(ColorConverter.ConvertFromString(avatar.Bg));
                foreach (var (data, fill) in avatar.Paths)
                {
                    Assert.DoesNotMatch("[^MLHVCQSTAZmlhvcqstaz0-9., \\-eE]", data); // the plan's SVG-and-WPF subset
                    var geometry = Geometry.Parse(data);
                    Assert.False(geometry.IsEmpty(), $"{avatar.Key}: an empty path");
                    var bounds = geometry.Bounds;
                    Assert.True(bounds.Left >= -4 && bounds.Top >= -4 && bounds.Right <= 52, $"{avatar.Key}: a path far outside 0 0 48 48");
                    Assert.IsType<Color>(ColorConverter.ConvertFromString(fill));
                }
            }
        });
    }

    [Fact]
    public void The_library_format_tolerates_broken_entries()
    {
        var (version, size, all) = AvatarLibrary.Parse("""
            {"version": 2, "viewBox": "0 0 64 64", "avatars": [
              {"key": "a01", "label": "One", "bg": "#FFFFFF", "paths": [["M0 0H10V10Z", "#000000"]]},
              {"key": "Bad Key", "label": "x", "bg": "#FFFFFF", "paths": [["M0 0H1Z", "#000"]]},
              {"key": "a03", "label": "No paths", "bg": "#FFFFFF", "paths": []}]}
            """);

        Assert.Equal((2, 64.0), (version, size));
        Assert.Equal("a01", Assert.Single(all).Key);
    }

    // ---- Photo compression --------------------------------------------------------------------------

    /// <summary>A photo-like source (gradients, detail, a little noise) encoded the way a camera would, with EXIF.</summary>
    private static byte[] CameraJpeg(int w, int h, ushort orientation = 1, int quality = 95)
    {
        var pixels = new byte[w * h * 3];
        var random = new Random(7);
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var i = (y * w + x) * 3;
                var top = y < h / 4; // a red band along the top edge, to see the orientation
                pixels[i] = (byte)(top ? 30 : (x * 255 / w + random.Next(24)) & 0xFF);
                pixels[i + 1] = (byte)(top ? 30 : (y * 255 / h + random.Next(24)) & 0xFF);
                pixels[i + 2] = (byte)(top ? 230 : ((x + y) % 97 * 2 + random.Next(24)) & 0xFF);
            }
        }
        var source = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgr24, null, pixels, w * 3);
        var meta = new BitmapMetadata("jpg");
        meta.SetQuery("/app1/ifd/{ushort=271}", "ZinweTestCam");      // Make
        meta.SetQuery("/app1/ifd/{ushort=274}", orientation);         // Orientation
        meta.SetQuery("/app1/ifd/gps/{ushort=1}", "N");               // GPS latitude ref
        var encoder = new JpegBitmapEncoder { QualityLevel = quality };
        encoder.Frames.Add(BitmapFrame.Create(source, null, meta, null));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private static Color Pixel(byte[] jpeg, int x, int y)
    {
        var frame = BitmapDecoder.Create(new MemoryStream(jpeg), BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
        var bgr = new FormatConvertedBitmap(frame, PixelFormats.Bgr24, null, 0);
        var p = new byte[3];
        bgr.CopyPixels(new Int32Rect(x, y, 1, 1), p, 3, 0);
        return Color.FromRgb(p[2], p[1], p[0]);
    }

    [Fact]
    public void A_large_camera_photo_becomes_a_256_px_jpeg_under_60_kb_with_no_exif()
    {
        OnSta(() =>
        {
            var source = CameraJpeg(4000, 3000);
            Assert.True(source.Length > 1_000_000, $"the source should be a big photo, was {source.Length} bytes");
            Assert.Contains("ZinweTestCam", Encoding.ASCII.GetString(source));
            var file = Path.Combine(_dir, "camera.JPG");
            File.WriteAllBytes(file, source);

            var photo = PictureCompress.FromFile(file);

            Assert.True(photo.Jpeg.Length <= PictureCompress.TargetBytes, $"{photo.Jpeg.Length} bytes");
            Assert.Equal((256, 256, source.LongLength), (photo.Width, photo.Height, photo.SourceBytes));
            Assert.Equal(82, photo.Quality); // a normal photo makes it at the first quality
            var frame = BitmapDecoder.Create(new MemoryStream(photo.Jpeg), BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
            Assert.Equal((256, 256), (frame.PixelWidth, frame.PixelHeight));
            var text = Encoding.ASCII.GetString(photo.Jpeg);
            Assert.DoesNotContain("Exif", text);
            Assert.DoesNotContain("ZinweTestCam", text);
            Assert.False(frame.Metadata is BitmapMetadata m && m.ContainsQuery("/app1/ifd"), "EXIF survived");
        });
    }

    [Fact]
    public void A_picture_over_the_target_retries_at_lower_qualities()
    {
        OnSta(() =>
        {
            // Pure noise is the worst case for JPEG; against a small target the next qualities are tried in order.
            var w = 256;
            var pixels = new byte[w * w * 3];
            new Random(3).NextBytes(pixels);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(w, w, 96, 96, PixelFormats.Bgr24, null, pixels, w * 3)));
            using var stream = new MemoryStream();
            encoder.Save(stream);

            var source = stream.ToArray();
            var normal = PictureCompress.FromBytes(source);
            Assert.True(normal.Jpeg.Length <= PictureCompress.TargetBytes, $"{normal.Jpeg.Length} bytes at q{normal.Quality}");

            var at82 = normal.Quality == 82 ? normal.Jpeg.Length : PictureCompress.FromBytes(source, int.MaxValue).Jpeg.Length;
            var retried = PictureCompress.FromBytes(source, at82 - 1);
            Assert.Equal(70, retried.Quality); // q82 was over: q70
            Assert.True(retried.Jpeg.Length < at82);
            Assert.Equal(55, PictureCompress.FromBytes(source, 1).Quality); // nothing fits: the last step is used
        });
    }

    [Fact]
    public void The_photo_is_turned_upright_cropped_to_the_centre_and_transparency_goes_on_white()
    {
        OnSta(() =>
        {
            // Orientation 6 = the camera was turned a quarter clockwise: the top band ends up on the right.
            var turned = PictureCompress.FromBytes(CameraJpeg(1200, 900, orientation: 6));
            var right = Pixel(turned.Jpeg, 250, 128);
            var left = Pixel(turned.Jpeg, 5, 128);
            Assert.True(right.R > 180 && right.B < 90, $"right edge {right}");
            Assert.False(left.R > 180 && left.B < 90, $"left edge {left}");

            // A small transparent PNG: scaled up to 256, and see-through pixels are white (not black).
            var w = 100;
            var pixels = new byte[w * 50 * 4]; // all transparent
            var png = new PngBitmapEncoder();
            png.Frames.Add(BitmapFrame.Create(BitmapSource.Create(w, 50, 96, 96, PixelFormats.Bgra32, null, pixels, w * 4)));
            using var stream = new MemoryStream();
            png.Save(stream);
            var small = PictureCompress.FromBytes(stream.ToArray());
            Assert.Equal((256, 256), (small.Width, small.Height));
            var c = Pixel(small.Jpeg, 128, 128);
            Assert.True(c.R > 245 && c.G > 245 && c.B > 245, $"centre {c}");
        });
    }

    [Fact]
    public void Wrong_kinds_too_big_files_and_garbage_are_refused_in_plain_words()
    {
        var gif = Path.Combine(_dir, "a.webp");
        File.WriteAllBytes(gif, [1, 2, 3]);
        Assert.Contains("JPG, PNG, BMP or GIF", Assert.Throws<PhotoRejectedException>(() => PictureCompress.FromFile(gif)).Message);

        var big = Path.Combine(_dir, "big.png");
        using (var f = File.Create(big))
        {
            f.SetLength(PictureCompress.MaxSourceBytes + 1);
        }
        Assert.Contains("10 MB", Assert.Throws<PhotoRejectedException>(() => PictureCompress.FromFile(big)).Message);

        var junk = Path.Combine(_dir, "junk.jpg");
        File.WriteAllBytes(junk, Encoding.UTF8.GetBytes("not a picture at all"));
        OnSta(() => Assert.Contains("couldn’t be opened", Assert.Throws<PhotoRejectedException>(() => PictureCompress.FromFile(junk)).Message));
    }

    // ---- What the tray draws --------------------------------------------------------------------------

    [Fact]
    public void A_pick_shows_at_once_until_the_services_status_moves_on()
    {
        var pick = new PicturePick();
        var before = new PictureStatus(PictureBackend.On, null, null, null, Version: 3);
        Assert.False(pick.Current(before).HasPicture); // initials

        pick.Pick(new PictureView(PictureChoice.Avatar, "a04"), before);
        Assert.Equal("a04", pick.Current(before).Key);
        pick.Observe(before); // same version: the service hasn't answered yet
        Assert.True(pick.Waiting);

        var after = before with { Kind = PictureChoice.Avatar, Key = "a04", Version = 4, Pending = true };
        pick.Observe(after);
        Assert.False(pick.Waiting);
        Assert.Equal(("a04", "Hair bun avatar"), (pick.Current(after).Key, pick.Current(after).Describe()));

        pick.Pick(new PictureView(PictureChoice.Avatar, "a05"), after);
        pick.Drop(); // the service said no
        Assert.Equal("a04", pick.Current(after).Key);
    }

    [Fact]
    public void What_is_drawn_falls_back_to_initials_for_anything_unknown()
    {
        Assert.Same(PictureView.None, PictureView.From(null)); // an older service
        Assert.False(PictureView.From(new PictureStatus(PictureBackend.On, "avatar", "a99")).HasPicture); // a key from a newer library
        Assert.False(PictureView.From(new PictureStatus(PictureBackend.On, "photo")).HasPicture); // not downloaded yet
        Assert.True(PictureView.From(new PictureStatus(PictureBackend.On, "photo", File: @"C:\x.jpg")).IsPhoto);
        Assert.Equal("Your initials", PictureView.None.Describe());
        Assert.False(new PictureStatus().CanChange);
        Assert.False(new PictureStatus(PictureBackend.Off).CanChange);
    }

    [Fact]
    public void Without_a_service_that_has_pictures_the_tray_offers_no_change_and_sends_nothing()
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                var sent = 0;
                var store = new NotificationStore();
                using var watcher = new NotificationWatcher(store, TimeSpan.FromMinutes(5));
                using var model = new TrayModel(store, watcher, () => Task.FromResult<string?>(null),
                    _ => { sent++; return Task.FromResult<PipeProtocol.EnrollAck?>(new(true, "saved")); });

                Assert.False(model.PictureOn); // no status yet (or an older service: no Picture on it)
                model.PickAvatarAsync("a01").GetAwaiter().GetResult();

                Assert.Equal(0, sent);
                Assert.NotNull(model.PhotoError);
                Assert.False(model.MyPicture.HasPicture); // initials / the official mark, as before
            }
            catch (Exception e)
            {
                error = e;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        Assert.Null(error);
    }
}

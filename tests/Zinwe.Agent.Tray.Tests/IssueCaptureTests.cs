using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using Zinwe.Agent.Core.Backend;
using Zinwe.Agent.Tray.Widget;

namespace Zinwe.Agent.Tray.Tests;

/// <summary>
/// F-13 · "Report an issue" attaches the screen the member was on: drawn before the form shows, from the window's own
/// visuals without the bottom sheet (never the Report form, never the desktop), offered ticked, and not offered at
/// all when nothing could be drawn.
/// </summary>
[Collection(WpfCollection.Name)]
public sealed class IssueCaptureTests
{
    private static void OnSta(Action<App, TrayModel> run)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                Application.ResourceAssembly ??= typeof(App).Assembly;
                var app = Application.Current as App ?? new App();
                app.InitializeComponent();
                var store = new NotificationStore();
                using var watcher = new NotificationWatcher(store, TimeSpan.FromMinutes(5));
                using var model = new TrayModel(store, watcher, () => Task.FromResult<string?>(null));
                run(app, model);
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

    [Fact]
    public void The_screen_is_captured_before_the_report_sheet_opens()
    {
        OnSta((_, model) =>
        {
            Sheet? seen = null;
            model.CaptureWindow = () =>
            {
                seen = model.Sheet;
                return [1, 2, 3];
            };
            model.Go(Tab.Tasks);

            model.OpenIssue();

            Assert.Equal(Sheet.None, seen); // drawn while the member's screen still showed, sheet not up yet
            Assert.Equal(Sheet.Issue, model.Sheet);
            Assert.Equal([1, 2, 3], model.IssueShot);
        });
    }

    [Fact]
    public void The_capture_leaves_out_the_bottom_sheet_and_its_dim()
    {
        OnSta((_, model) =>
        {
            model.Go(Tab.Me);
            var window = new WidgetWindow(model); // never shown: the capture lays it out off-screen
            model.CloseSheet();
            var plain = model.CaptureWindow!();
            Assert.NotNull(plain);

            model.OpenSheet(Sheet.Feedback); // a sheet (with its dim) over the screen
            var withSheet = model.CaptureWindow!();

            Assert.NotNull(withSheet);
            Assert.Equal(Pixels(plain!), Pixels(withSheet!)); // the same picture: the sheet layer isn't drawn
            Assert.Equal(Visibility.Visible, ((FrameworkElement)window.FindName("SheetSlot")).Visibility); // and it comes back
            Assert.True(plain!.Length <= MemberIssueRequest.MaxScreenshotBytes);
            Assert.Equal([0x89, (byte)'P', (byte)'N', (byte)'G'], plain[..4]);
        });
    }

    [Fact]
    public void Without_a_screenshot_the_option_is_not_offered_and_with_one_it_is_ticked()
    {
        OnSta((_, model) =>
        {
            var panel = new IssuePanel { Model = model };
            var attach = (System.Windows.Controls.CheckBox)panel.FindName("Attach");

            model.CaptureWindow = () => null; // nothing could be drawn
            model.OpenIssue();
            panel.Reset();
            Assert.Equal(Visibility.Collapsed, attach.Visibility);
            Assert.False(attach.IsChecked == true);

            model.CloseSheet();
            model.CaptureWindow = () => TinyPng();
            model.OpenIssue();
            panel.Reset();
            Assert.Equal(Visibility.Visible, attach.Visibility);
            Assert.True(attach.IsChecked); // ticked by default; the member can untick it
            Assert.Equal(Visibility.Visible, ((FrameworkElement)panel.FindName("ShotFrame")).Visibility);
        });
    }

    [Fact]
    public void A_screenshot_too_big_for_the_report_is_drawn_smaller_and_none_that_fits_is_none()
    {
        var tried = new List<double>();
        var png = IssueReport.Fit(scale =>
        {
            tried.Add(scale);
            return new byte[scale >= 0.75 ? MemberIssueRequest.MaxScreenshotBytes + 1 : 10];
        });
        Assert.Equal(10, png!.Length);
        Assert.Equal([1, 0.75, 0.5], tried);

        Assert.Null(IssueReport.Fit(_ => new byte[MemberIssueRequest.MaxScreenshotBytes + 1]));
        Assert.Null(IssueReport.Fit(_ => null));
    }

    private static byte[] Pixels(byte[] png)
    {
        var frame = BitmapDecoder.Create(new MemoryStream(png), BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
        var stride = frame.PixelWidth * 4;
        var pixels = new byte[stride * frame.PixelHeight];
        new FormatConvertedBitmap(frame, System.Windows.Media.PixelFormats.Bgra32, null, 0).CopyPixels(pixels, stride, 0);
        return pixels;
    }

    private static byte[] TinyPng()
    {
        var bmp = BitmapSource.Create(2, 2, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, new byte[16], 8);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var s = new MemoryStream();
        enc.Save(s);
        return s.ToArray();
    }
}

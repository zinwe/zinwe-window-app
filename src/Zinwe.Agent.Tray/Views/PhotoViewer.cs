using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Zinwe.Agent.Tray.Views;

/// <summary>
/// v1.5 · the simple photo viewer: a downloaded photo, as big as the screen allows, on a dark ground. Esc or a click
/// closes it; Open hands it to the member's own photo app.
/// </summary>
internal sealed class PhotoViewer : Window
{
    public PhotoViewer(string path, string name, Action open)
    {
        Title = name;
        Width = Math.Min(1100, SystemParameters.WorkArea.Width * 0.8);
        Height = Math.Min(800, SystemParameters.WorkArea.Height * 0.85);
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = new SolidColorBrush(Color.FromRgb(0x14, 0x12, 0x10));
        ShowInTaskbar = true;
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition());
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var image = new Image { Stretch = Stretch.Uniform, Margin = new Thickness(16), Source = Load(path) };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        AutomationProperties.SetName(image, name);
        grid.Children.Add(image);
        var bar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 14) };
        var label = new TextBlock { Text = name, Foreground = Brushes.White, Opacity = 0.85, FontSize = 13, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 12, 0), TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 420 };
        bar.Children.Add(label);
        var openButton = Tv.Btn("Open", true, () =>
        {
            open();
            Close();
        }, "Open " + name + " in its app", compact: true);
        bar.Children.Add(openButton);
        var close = Tv.Btn("Close", false, Close, "Close the photo", compact: true);
        close.Margin = new Thickness(6, 0, 0, 0);
        bar.Children.Add(close);
        Grid.SetRow(bar, 1);
        grid.Children.Add(bar);
        Content = grid;
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                Close();
            }
        };
        image.MouseLeftButtonUp += (_, _) => Close();
    }

    private static ImageSource? Load(string path)
    {
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            image.UriSource = new Uri(path, UriKind.Absolute);
            image.DecodePixelWidth = (int)Math.Min(2400, SystemParameters.PrimaryScreenWidth * 1.5);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception e) when (e is IOException or NotSupportedException or UriFormatException or ArgumentException or InvalidOperationException
            or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            return null;
        }
    }
}

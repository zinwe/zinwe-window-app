using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using Zinwe.Agent.Tray.Widget;

namespace Zinwe.Agent.Tray;

/// <summary>
/// F-13: "Report an issue" (the Send feedback pattern). The member writes what went wrong and may send a screenshot: the
/// window as it was when they opened the form (added for them, removable) or a PNG or JPG they pick; the model queues it
/// in the shared outbox and the agent posts it to the Zinwe team.
/// </summary>
public partial class IssuePanel : UserControl
{
    public IssuePanel() => InitializeComponent();

    public TrayModel? Model { get; set; }

    /// <summary>The panel asks to be closed (Cancel / Close).</summary>
    public event EventHandler? Closed;

    // The screenshot the report will carry (null = none), and whether it is the window as it was.
    private byte[]? _shot;
    private bool _shotIsWindow;

    /// <summary>
    /// A fresh, empty form (each time it's opened). With a screenshot of the screen the member was on (taken before
    /// this form showed) it is already added, shown as a thumbnail, and × takes it out; without one (or once taken
    /// out) Add picks a PNG or JPG from the PC.
    /// </summary>
    public void Reset()
    {
        Description.Text = "";
        Error.Visibility = Visibility.Collapsed;
        ShotError.Visibility = Visibility.Collapsed;
        SendButton.IsEnabled = false;
        Form.Visibility = Visibility.Visible;
        Thanks.Visibility = Visibility.Collapsed;
        SetShot(Model?.IssueShot, window: true);
    }

    private void SetShot(byte[]? bytes, bool window)
    {
        var image = bytes is { Length: > 0 } ? Load(bytes) : null;
        _shot = image is null ? null : bytes;
        _shotIsWindow = window && image is not null;
        Shot.Source = image;
        ShotSize.Text = _shot is null ? "" : IssueReport.Size(_shot.Length);
        ShotTile.Visibility = image is null ? Visibility.Collapsed : Visibility.Visible;
        AddShot.Visibility = image is null ? Visibility.Visible : Visibility.Collapsed;
        ShotNote.Text = _shotIsWindow
            ? "We added the screen you were on. Remove it if something private shows. The Zinwe team sees it, not your company."
            : "One PNG or JPG, up to 2 MB. The Zinwe team sees it, not your company.";
    }

    private static BitmapImage? Load(byte[] bytes)
    {
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = 144; // a thumbnail: the bytes sent stay the original
            image.StreamSource = new MemoryStream(bytes);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private void OnTextChanged(object sender, TextChangedEventArgs e) => SendButton.IsEnabled = Description.Text.Trim().Length > 0;

    private void OnRemoveShot(object sender, RoutedEventArgs e)
    {
        SetShot(null, window: false);
        ShotError.Visibility = Visibility.Collapsed;
        AddShot.Focus();
    }

    private void OnAddShot(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = IssueReport.PickFilter, Title = "Add a screenshot" };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true)
        {
            return;
        }
        string? problem;
        byte[] bytes = [];
        try
        {
            var info = new FileInfo(dialog.FileName);
            problem = info.Length > IssueReport.MaxPickedBytes ? IssueReport.TooBig(info.Name) : null;
            if (problem is null)
            {
                bytes = File.ReadAllBytes(dialog.FileName);
                problem = IssueReport.CheckPicked(bytes, info.Name) ?? (Load(bytes) is null ? "That image couldn’t be opened. Try another one." : null);
            }
        }
        catch (Exception)
        {
            problem = "That file couldn’t be read. Try another one.";
        }
        ShotError.Text = problem ?? "";
        ShotError.Visibility = problem is null ? Visibility.Collapsed : Visibility.Visible;
        if (problem is null)
        {
            SetShot(bytes, window: false);
            RemoveShot.Focus();
        }
    }

    private void OnSend(object sender, RoutedEventArgs e)
    {
        if (Model is not { } m)
        {
            return;
        }
        if (m.SendIssue(Description.Text, _shot) is { } problem)
        {
            Error.Text = problem;
            Error.Visibility = Visibility.Visible;
            return;
        }
        Form.Visibility = Visibility.Collapsed;
        Thanks.Visibility = Visibility.Visible;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Closed?.Invoke(this, EventArgs.Empty);
}

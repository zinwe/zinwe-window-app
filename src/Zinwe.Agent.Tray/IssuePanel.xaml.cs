using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;

namespace Zinwe.Agent.Tray;

/// <summary>
/// F-13: "Report an issue" (the Send feedback pattern). The member writes what went wrong and may attach the window as
/// it was when they opened the form; the model queues it in the shared outbox and the agent posts it to the Zinwe team.
/// </summary>
public partial class IssuePanel : UserControl
{
    public IssuePanel() => InitializeComponent();

    public TrayModel? Model { get; set; }

    /// <summary>The panel asks to be closed (Cancel / Close).</summary>
    public event EventHandler? Closed;

    /// <summary>A fresh, empty form (each time it's opened); the screenshot is off until the member ticks it.</summary>
    public void Reset()
    {
        Description.Text = "";
        Attach.IsChecked = false;
        Attach.IsEnabled = Model?.IssueShot is not null;
        Shot.Source = Model?.IssueShot is { } png ? Load(png) : null;
        Error.Visibility = Visibility.Collapsed;
        SendButton.IsEnabled = false;
        Form.Visibility = Visibility.Visible;
        Thanks.Visibility = Visibility.Collapsed;
        OnAttachChanged(this, new RoutedEventArgs());
    }

    private static BitmapImage? Load(byte[] png)
    {
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = new MemoryStream(png);
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

    private void OnAttachChanged(object sender, RoutedEventArgs e)
    {
        var on = Attach.IsChecked == true && Shot.Source is not null;
        ShotFrame.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        ShotNote.Visibility = ShotFrame.Visibility;
    }

    private void OnSend(object sender, RoutedEventArgs e)
    {
        if (Model is not { } m)
        {
            return;
        }
        if (m.SendIssue(Description.Text, Attach.IsChecked == true) is { } problem)
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

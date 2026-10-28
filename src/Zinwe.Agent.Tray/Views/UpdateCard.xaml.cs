using System.Windows;
using System.Windows.Controls;
using Zinwe.Agent.Tray.Resources;

namespace Zinwe.Agent.Tray.Views;

/// <summary>The real one-click update (service status → <see cref="Widget.UpdateView"/>), drawn as the board's card.</summary>
public partial class UpdateCard : UserControl
{
    public UpdateCard() => InitializeComponent();

    public TrayModel? Model { get; set; }

    public void Render()
    {
        if (Model is not { } m)
        {
            return;
        }
        var view = m.Update;
        Visibility = view.Show && m.Stage == Stage.App ? Visibility.Visible : Visibility.Collapsed;
        if (!view.Show)
        {
            return;
        }
        Title.Text = view.Busy ? view.Button : view.Title;
        Body.Text = view.Busy
            ? "You can keep working. Your times are saved."
            : view.Required ? view.Body + " Start day, breaks and End day keep working." : view.Body;
        Notes.Text = view.Notes is null ? "" : Strings.Format(Strings.UpdateWhatsNew, view.Notes);
        Notes.Visibility = view.Notes is null || view.Busy ? Visibility.Collapsed : Visibility.Visible;
        Bar.Visibility = view.Busy ? Visibility.Visible : Visibility.Collapsed;

        var progress = m.UpdateRefused ?? view.Progress;
        Progress.Text = progress ?? "";
        Progress.Visibility = string.IsNullOrEmpty(progress) ? Visibility.Collapsed : Visibility.Visible;
        Progress.SetResourceReference(TextBlock.ForegroundProperty, view.ProgressIsError || m.UpdateRefused is not null ? "Z.RedText" : "Z.Muted");

        UpdateButton.Content = view.Button;
        UpdateButton.Visibility = view.ButtonVisible && !view.Busy ? Visibility.Visible : Visibility.Collapsed;
        UpdateButton.IsEnabled = view.ButtonEnabled;
        ManualButton.Content = Strings.UpdateManual;
        ManualButton.Visibility = view.ShowManual ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void OnUpdate(object sender, RoutedEventArgs e)
    {
        if (Model is { } m)
        {
            UpdateButton.IsEnabled = false;
            await m.StartUpdateAsync();
        }
    }

    private void OnManual(object sender, RoutedEventArgs e)
    {
        if (Model?.Status?.DownloadUrl is { Length: > 0 } url)
        {
            TrayModel.OpenLink(url);
        }
    }
}

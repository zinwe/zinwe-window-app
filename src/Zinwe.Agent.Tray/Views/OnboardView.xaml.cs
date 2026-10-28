using System.Windows;
using System.Windows.Controls;
using Zinwe.Agent.Core.Work;
using Zinwe.Agent.Tray.Resources;
using Zinwe.Agent.Tray.Widget;

namespace Zinwe.Agent.Tray.Views;

/// <summary>Connect → consent → welcome, plus the removed and not-running states (real pipe + consent file).</summary>
public partial class OnboardView : UserControl
{
    private Stage? _shown;

    public OnboardView() => InitializeComponent();

    public TrayModel? Model { get; set; }

    public void Render()
    {
        if (Model is not { } m)
        {
            return;
        }
        var stage = m.Stage;
        ConnectPanel.Visibility = Show(stage == Stage.Connect);
        ConsentPanel.Visibility = Show(stage == Stage.Consent);
        WelcomePanel.Visibility = Show(stage == Stage.Welcome);
        RemovedPanel.Visibility = Show(stage == Stage.Removed);
        NotRunningPanel.Visibility = Show(stage == Stage.NotRunning);

        switch (stage)
        {
            case Stage.Connect:
                ConnectTitle.Text = m.Status?.Removed == true ? "Connect with a new Member ID" : "Connect this PC";
                ConnectButton.IsEnabled = !m.ConnectBusy;
                ConnectButton.Content = m.ConnectBusy
                    ? m.ConnectMessage == Strings.ConnectStillTrying ? "Still trying to reach Zinwe…" : "Connecting…"
                    : "Connect";
                ConnectMessage.Text = m.ConnectMessage;
                ConnectMessage.SetResourceReference(TextBlock.ForegroundProperty, m.ConnectError ? "Z.RedText" : "Z.Muted");
                ConnectMessage.Visibility = Show(m.ConnectMessage.Length > 0 && (m.ConnectError || !m.ConnectBusy));
                break;
            case Stage.Consent:
                RenderConsent(m);
                break;
            case Stage.Welcome:
                RenderWelcome(m);
                break;
            case Stage.Removed:
                RemovedTitle.Text = $"{m.CompanyStart} removed this PC from Zinwe";
                break;
            case Stage.NotRunning:
                NotRunningTitle.Text = Strings.NotRunningTitle;
                NotRunningBody.Text = Strings.Format(Strings.NotRunningBody, m.OwnerName);
                break;
        }
        if (_shown != stage)
        {
            _shown = stage;
            Dispatcher.BeginInvoke(() => FirstFocus(stage), System.Windows.Threading.DispatcherPriority.Loaded);
        }
    }

    private void FirstFocus(Stage stage)
    {
        if (stage == Stage.Connect)
        {
            (EmailInput.Text.Length == 0 ? EmailInput : (UIElement)MemberIdInput).Focus();
        }
        else if (stage == Stage.Consent)
        {
            ConsentScroller.ScrollToTop();
        }
    }

    private void RenderConsent(TrayModel m)
    {
        var view = m.Notice;
        ConsentIntro.Text = view.Intro;
        SeesHeading.Text = view.SeesHeading;
        Bullets(SeesList, view.Sees);
        SeesMore.Text = view.SeesMore;
        NeverHeading.Text = view.NeverHeading;
        Bullets(NeverList, view.Never);
        DeclineHeading.Text = view.DeclineHeading;
        DeclineBody.Text = view.DeclineBody;
        ConsentFooter.Text = "Choosing \"I agree\" is your consent to the Privacy Policy. You can withdraw it later under Me, in Policies & terms.";
    }

    internal static void Bullets(Panel panel, IEnumerable<string> items)
    {
        panel.Children.Clear();
        foreach (var item in items)
        {
            // A hanging bullet: wrapped lines line up with the text, not under the dot.
            var row = new Grid { Margin = new Thickness(0, 0, 0, 4) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.Children.Add(Ui.Text("•", 12.5, fg: "Z.Ink2"));
            var text = Ui.Text(item, 12.5, fg: "Z.Ink2", wrap: true);
            Grid.SetColumn(text, 1);
            row.Children.Add(text);
            panel.Children.Add(row);
        }
    }

    private void RenderWelcome(TrayModel m)
    {
        var declined = m.Consent == ConsentState.Declined;
        WelcomeIcon.SetResourceReference(Border.BackgroundProperty, declined ? "Z.WarnTint" : "Z.OkTint");
        WelcomeTick.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, declined ? "Z.Warn" : "Z.Ok");
        WelcomeTitle.Text = declined ? "You’re connected" : "You’re all set";
        WelcomeBody.Text = declined
            ? "Monitoring is off on this PC: nothing about what you do is recorded. Start day, breaks, tasks and chat still work, and you can agree later under Me, in Policies & terms."
            : "Zinwe lives in your taskbar, bottom right, and starts with Windows. Here is how a day works.";
        Tips.Children.Clear();
        var owner = m.OwnerFirstStart;
        var tips = new List<(string Icon, string Title, string Sub)>();
        if (m.WorkHoursOn)
        {
            tips.Add((Ui.Icon.Time, "Press Start day when you begin work", $"Take a break and End day ask for a reason. {owner} sees it with the time."));
        }
        tips.Add((Ui.Icon.Tasks, $"Tasks from {m.OwnerFirst} land in Tasks", "Start or finish one with a single tap. Messages are in Chat."));
        tips.Add((Ui.Icon.Dock, "Close or minimize it any time", "Zinwe keeps running in the taskbar. Click the Z icon or press Ctrl+Alt+Z to open it."));
        for (var i = 0; i < tips.Count; i++)
        {
            var (icon, title, sub) = tips[i];
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            var tile = Ui.Tile(icon, 32, "Z.OrangeTint", "Z.OrangeInk");
            tile.Margin = new Thickness(0, 0, 12, 0);
            tile.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(tile);
            var texts = new StackPanel();
            texts.Children.Add(Ui.Text(title, 13.5, FontWeights.SemiBold, wrap: true));
            texts.Children.Add(Ui.Text(sub, 12, fg: "Z.Muted", wrap: true));
            Grid.SetColumn(texts, 1);
            row.Children.Add(texts);
            var cell = new Border { Padding = new Thickness(14, 12, 14, 12), BorderThickness = new Thickness(0, 0, 0, i < tips.Count - 1 ? 1 : 0), Child = row };
            cell.SetResourceReference(Border.BorderBrushProperty, "Z.LineSoft");
            Tips.Children.Add(cell);
        }
    }

    private static Visibility Show(bool on) => on ? Visibility.Visible : Visibility.Collapsed;

    private void OnConnectInput(object sender, TextChangedEventArgs e) => Model?.ClearConnectError();

    private async void OnConnect(object sender, RoutedEventArgs e)
    {
        if (Model is { } m)
        {
            await m.ConnectAsync(EmailInput.Text, MemberIdInput.Text, id => MemberIdInput.Text = id);
            if (m.ConnectError)
            {
                (EmailInput.Text.Trim().Length == 0 ? EmailInput : (UIElement)MemberIdInput).Focus();
            }
        }
    }

    private void OnAgree(object sender, RoutedEventArgs e) => Model?.Agree();

    private void OnDecline(object sender, RoutedEventArgs e) => Model?.Decline();

    private void OnNotNow(object sender, RoutedEventArgs e)
    {
        Model?.ConsentNotNow();
        (Window.GetWindow(this) as WidgetWindow)?.TuckAway();
    }

    private void OnStartApp(object sender, RoutedEventArgs e) => Model?.StartApp();

    private void OnReconnect(object sender, RoutedEventArgs e)
    {
        EmailInput.Text = "";
        MemberIdInput.Text = "";
        Model?.Reconnect();
    }

    private async void OnRetry(object sender, RoutedEventArgs e)
    {
        if (Model is { } m)
        {
            RetryButton.IsEnabled = false;
            await m.RefreshStatusAsync();
            RetryButton.IsEnabled = true;
        }
    }

    private void OnPrivacyPolicy(object sender, RoutedEventArgs e) => TrayModel.OpenLink("https://zinwe.io/legal/privacy");
}

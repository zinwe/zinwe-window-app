using System.Windows;
using System.Windows.Controls;
using Zinwe.Agent.Core.Work;
using Zinwe.Agent.Tray.Widget;

namespace Zinwe.Agent.Tray.Views;

/// <summary>Home: what needs the member (new tasks, unread messages, tasks due today) and today's timeline (real log).</summary>
public partial class HomeView : UserControl
{
    private readonly UpdateCard Update = new();

    public HomeView()
    {
        InitializeComponent();
        UpdateHost.Content = Update;
    }

    public TrayModel? Model { get; set; }

    public void Render()
    {
        if (Model is not { } m)
        {
            return;
        }
        Update.Model = m;
        Update.Render();
        DeclinedNote.Visibility = m.Consent == ConsentState.Declined ? Visibility.Visible : Visibility.Collapsed;

        NeedsList.Children.Clear();
        var needs = m.NeedsYou;
        foreach (var (kind, title, sub, run) in needs)
        {
            var (icon, bg, fg) = kind switch
            {
                "task" => (Ui.Icon.Tasks, "Z.OrangeTint", "Z.Orange"),
                "chat" => (Ui.Icon.Chat, "Z.InfoTint", "Z.Info"),
                _ => (Ui.Icon.Time, "Z.SoonTint", "Z.StDoing"),
            };
            NeedsList.Children.Add(Ui.ListRow(Ui.Tile(icon, 34, bg, fg, 10), title, sub, run));
        }
        NeedsCount.Text = needs.Count > 0 ? needs.Count.ToString() : "";
        NoNeeds.Visibility = needs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        TodayCard.Visibility = m.WorkHoursOn ? Visibility.Visible : Visibility.Collapsed;
        RenderTimeline(Timeline, m.Timeline);
        NoTimeline.Visibility = m.Timeline.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ProtectedCard.Visibility = !m.WorkHoursOn && !m.ChatOn ? Visibility.Visible : Visibility.Collapsed;
    }

    internal static void RenderTimeline(Panel panel, IReadOnlyList<TimelineEvent> events)
    {
        panel.Children.Clear();
        foreach (var e in events)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 4) };
            var time = Ui.Text(e.Time, 12, fg: "Z.Muted");
            time.FontFamily = (System.Windows.Media.FontFamily)Application.Current.FindResource("Z.Mono");
            time.Width = 62;
            time.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(time);
            var dot = Ui.Dot(8, e.Kind switch { TimelineKind.Break => "Z.Warn", TimelineKind.Out => "Z.Muted", _ => "Z.Ok" });
            dot.Margin = new Thickness(10, 0, 10, 0);
            row.Children.Add(dot);
            var label = Ui.Text(e.Label, 12.5);
            label.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(label);
            panel.Children.Add(row);
        }
    }

    private void OnReview(object sender, RoutedEventArgs e)
    {
        Model?.Go(Tab.Me);
        Model?.Open(Push.Privacy);
    }

    private void OnMyTime(object sender, RoutedEventArgs e) => Model?.Open(Push.Time);
}

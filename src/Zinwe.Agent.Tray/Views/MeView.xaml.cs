using System.Windows;
using System.Windows.Controls;
using Zinwe.Agent.Core;
using Zinwe.Agent.Core.Work;
using Zinwe.Agent.Tray.Widget;

namespace Zinwe.Agent.Tray.Views;

/// <summary>Me: who and where, the pushed screens, the update card and the version.</summary>
public partial class MeView : UserControl
{
    private readonly UpdateCard Update = new() { Margin = new Thickness(0, 0, 0, 12) };

    public MeView()
    {
        InitializeComponent();
        UpdateHost.Content = Update;
    }

    public TrayModel? Model { get; set; }

    /// <summary>Set by the window when Ctrl+Alt+Z is registered (null when another app holds it).</summary>
    public bool ShortcutOn { get; set; } = true;

    public void Render()
    {
        if (Model is not { } m)
        {
            return;
        }
        var name = m.Profile.DisplayName;
        NameText.Text = name;
        AvatarHost.Content = Ui.Avatar(m.Settings.Avatar, Avatars.Initials(name == "You" ? null : name), 46);
        CompanyRun.Text = m.Status?.CompanyName is { Length: > 0 } c ? c : "Zinwe";
        PcRun.Text = m.Status?.Hostname ?? Environment.MachineName;

        Items.Children.Clear();
        var unread = m.Unread.All;
        var notifSub = m.Paused ? $"Paused until {m.Settings.PausedUntil!.Value.ToLocalTime():h:mm tt}"
            : unread > 0 ? $"{unread} unread · pause for meetings"
            : m.NoticeList.Count > 0 ? $"{m.NoticeList.Count} today · pause for meetings" : "Nothing yet today · pause for meetings";
        var rows = new List<(string Icon, string Label, string Sub, Action Run)>();
        if (m.WorkHoursOn)
        {
            rows.Add((Ui.Icon.Time, "My time", "Worked today: " + DayLog.HoursMinutes(m.Worked), () => m.Open(Push.Time)));
        }
        rows.Add((Ui.Icon.Bell, "Notifications", notifSub, () => m.Open(Push.Notifications)));
        rows.Add((Ui.Icon.Gear, "Settings", "Notifications, dark mode, text size", () => m.Open(Push.Settings)));
        rows.Add((Ui.Icon.Privacy, "Policies & terms", m.Consent == ConsentState.Given
            ? "What’s recorded · your rights · fair use · terms" : "Monitoring is off on this PC · terms", () => m.Open(Push.Privacy)));
        rows.Add((Ui.Icon.Help, "Help", $"Ask {m.OwnerFirst}, guides, or the Zinwe team", () => m.Open(Push.Help)));
        rows.Add((Ui.Icon.Feedback, "Send feedback", "Goes to the Zinwe team, not your employer", () => m.OpenSheet(Sheet.Feedback)));
        foreach (var (icon, label, sub, run) in rows)
        {
            Items.Children.Add(Ui.ListRow(Ui.Tile(icon, 32, "Z.Bg", "Z.Ink3"), label, sub, run));
        }

        Update.Model = m;
        Update.Render();
        ShortcutLine.Visibility = ShortcutOn ? Visibility.Visible : Visibility.Collapsed;
        var version = m.Status?.AgentVersion ?? AgentInfo.Version;
        var state = m.Status?.UpdateVersion is { } v ? (m.Status.State == Zinwe.Agent.Core.Ipc.AgentState.UpdateRequired ? $"{v} is required" : $"{v} is ready")
            : m.Status is null ? "can't check right now" : "up to date";
        VersionLine.Text = $"Zinwe {version} · {state}";
    }

    private void OnPhoto(object sender, RoutedEventArgs e) => Model?.OpenSheet(Sheet.Photo);
}

using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Zinwe.Agent.Core.Work;
using Zinwe.Agent.Tray.Widget;

namespace Zinwe.Agent.Tray.Views;

/// <summary>My time (real work log), Settings (local), Notifications (one shared read state), Help, Policies &amp; terms (real consent).</summary>
public partial class PushView : UserControl
{
    public PushView() => InitializeComponent();

    public TrayModel? Model { get; set; }

    private bool _syncing;

    public void Render()
    {
        if (Model is not { } m)
        {
            return;
        }
        _syncing = true;
        try
        {
            RenderScreens(m);
        }
        finally
        {
            _syncing = false;
        }
    }

    private void RenderScreens(TrayModel m)
    {
        TimePanel.Visibility = Show(m.Push == Push.Time);
        SettingsPanel.Visibility = Show(m.Push == Push.Settings);
        NotifPanel.Visibility = Show(m.Push == Push.Notifications);
        HelpPanel.Visibility = Show(m.Push == Push.Help);
        PrivacyPanel.Visibility = Show(m.Push == Push.Privacy);
        SharedPanel.Visibility = Show(m.Push == Push.SharedFiles);
        switch (m.Push)
        {
            case Push.SharedFiles:
                RenderShared(m);
                break;
            case Push.Time:
                RenderTime(m);
                break;
            case Push.Settings:
                RenderSettings(m);
                break;
            case Push.Notifications:
                RenderNotifications(m);
                break;
            case Push.Help:
                RenderHelp(m);
                break;
            case Push.Privacy:
                RenderPrivacy(m);
                break;
        }
    }

    private static Visibility Show(bool on) => on ? Visibility.Visible : Visibility.Collapsed;

    // ---- My time ----------------------------------------------------------------

    private void RenderTime(TrayModel m)
    {
        var week = m.Week;
        var tiles = new (string Label, string Value)[]
        {
            ("Worked today", DayLog.HoursMinutes(m.Worked)),
            ("Breaks today", m.BreaksToday.ToString()),
            ("Started at", m.DayStarted?.ToLocalTime().ToString("h:mm tt") ?? "—"),
            ("Last 5 days", DayLog.HoursMinutes(TimeSpan.FromHours(week.Sum(d => d.Hours)))),
        };
        TimeTiles.Children.Clear();
        foreach (var (label, value) in tiles)
        {
            var stack = new StackPanel();
            stack.Children.Add(Ui.Text(label, 12, fg: "Z.Ink3"));
            var v = Ui.Text(value, 17, FontWeights.Bold);
            v.FontFamily = (System.Windows.Media.FontFamily)FindResource("Z.Mono");
            stack.Children.Add(v);
            var card = Ui.Card(stack, 14, new Thickness(12));
            card.Margin = new Thickness(4);
            TimeTiles.Children.Add(card);
        }
        HomeView.RenderTimeline(TimeTimeline, m.Timeline);
        TimeEmpty.Visibility = Show(m.Timeline.Count == 0);

        WeekBars.Children.Clear();
        WeekDays.Children.Clear();
        foreach (var d in week)
        {
            var col = new StackPanel { VerticalAlignment = VerticalAlignment.Bottom, HorizontalAlignment = HorizontalAlignment.Center };
            var h = Ui.Text(d.Hours.ToString("0.0"), 12, fg: "Z.Ink3");
            h.HorizontalAlignment = HorizontalAlignment.Center;
            h.FontFamily = (System.Windows.Media.FontFamily)FindResource("Z.Mono");
            col.Children.Add(h);
            var bar = new Border
            {
                Width = 30, Height = Math.Max(2, Math.Round(d.Hours / 9 * 84)), CornerRadius = new CornerRadius(6, 6, 0, 0),
                Margin = new Thickness(0, 4, 0, 0),
            };
            bar.SetResourceReference(Border.BackgroundProperty, d.IsToday ? "Z.Orange" : "Z.Inv");
            col.Children.Add(bar);
            WeekBars.Children.Add(col);
            var day = Ui.Text(d.Day, 12, fg: "Z.Muted");
            day.HorizontalAlignment = HorizontalAlignment.Center;
            WeekDays.Children.Add(day);
        }
        AutomationProperties.SetName(WeekBars, "Hours worked: " + string.Join(", ", week.Select(d => $"{d.Day} {d.Hours:0.0}")));
        TimeNote.Text = $"Worked time comes only from your Start day, Take a break and End day buttons. {m.OwnerFirstStart} also sees \"Active on PC\": time the PC was in use (keyboard or mouse), not what was done.";
    }

    // ---- Settings -------------------------------------------------------------------

    private void RenderSettings(TrayModel m)
    {
        var s = m.Settings;
        NotifyOn.IsChecked = s.NotifyOn;
        NotifySub.Text = $"New tasks, messages and answers from {m.OwnerFirst}";
        DueRemind.IsChecked = s.DueRemind;
        DayRemind.IsChecked = s.DayRemind;
        DarkMode.IsChecked = s.Dark;
        DarkMode.IsEnabled = !HighContrastTheme.IsHighContrast;
        SizeStandard.IsChecked = !s.TextLarge;
        SizeLarge.IsChecked = s.TextLarge;
        RenderFilesSettings(m);
        RunsWithWindows.Text = $"Zinwe starts when you sign in to this PC and stays in the taskbar, bottom right. {m.CompanyStart} manages this, so it can't be switched off here.";
    }

    private void OnToggle(object sender, RoutedEventArgs e)
    {
        if (_syncing || Model is not { } m || sender is not ToggleButton t)
        {
            return;
        }
        var on = t.IsChecked == true;
        m.SaveSettings(t.Name switch
        {
            nameof(NotifyOn) => m.Settings with { NotifyOn = on },
            nameof(DueRemind) => m.Settings with { DueRemind = on },
            nameof(DayRemind) => m.Settings with { DayRemind = on },
            _ => m.Settings with { Dark = on },
        });
    }

    private void OnSize(object sender, RoutedEventArgs e)
    {
        if (!_syncing)
        {
            Model?.SaveSettings(Model.Settings with { TextLarge = SizeLarge.IsChecked == true });
        }
    }

    // ---- Files (v1.5) ----------------------------------------------------------------------

    private string _filesShown = "";

    /// <summary>Settings › Files: Save files to (Change…), Ask where to save each file, and My shared files.</summary>
    private void RenderFilesSettings(TrayModel m)
    {
        FilesSection.Visibility = Show(m.FilesSupported);
        var key = $"{m.SaveFolder}|{m.Settings.AskWhere}|{m.SharedFiles.Count}|{HighContrastTheme.IsDark}";
        if (!m.FilesSupported || key == _filesShown)
        {
            return;
        }
        _filesShown = key;
        FilesSettings.Children.Clear();

        var folder = new Grid { Margin = new Thickness(14, 12, 14, 12) };
        folder.ColumnDefinitions.Add(new ColumnDefinition());
        folder.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
        texts.Children.Add(Ui.Text(Core.Chat.ChatFiles.SaveToText, 13.5, FontWeights.SemiBold));
        var where = Ui.Text(TrayModel.FolderLabel(m.SaveFolder), 12, fg: "Z.Muted", trim: true);
        where.ToolTip = m.SaveFolder;
        texts.Children.Add(where);
        folder.Children.Add(texts);
        var change = Tv.Btn("Change…", false, () => ChooseFolder(m), "Change where files are saved", compact: true);
        change.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(change, 1);
        folder.Children.Add(change);
        AutomationProperties.SetName(folder, $"{Core.Chat.ChatFiles.SaveToText}: {m.SaveFolder}");
        FilesSettings.Children.Add(folder);

        var ask = new ToggleButton { Style = (Style)FindResource("Z.SwitchRow"), IsChecked = m.Settings.AskWhere };
        var askText = new StackPanel();
        askText.Children.Add(new TextBlock { Text = Core.Chat.ChatFiles.AskWhereText, FontWeight = FontWeights.SemiBold });
        askText.Children.Add(Ui.Text("Download asks for a folder and a name each time", 12, fg: "Z.Muted", wrap: true));
        ask.Content = askText;
        AutomationProperties.SetName(ask, Core.Chat.ChatFiles.AskWhereText);
        ask.Checked += (_, _) =>
        {
            if (!_syncing)
            {
                m.SetAskWhere(true);
            }
        };
        ask.Unchecked += (_, _) =>
        {
            if (!_syncing)
            {
                m.SetAskWhere(false);
            }
        };
        FilesSettings.Children.Add(ask);

        var shared = new Grid { Margin = new Thickness(14, 12, 14, 12) };
        shared.ColumnDefinitions.Add(new ColumnDefinition());
        shared.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var sharedTexts = new StackPanel();
        sharedTexts.Children.Add(Ui.Text(Core.Chat.ChatFiles.MySharedFilesText, 13.5, FontWeights.SemiBold));
        sharedTexts.Children.Add(Ui.Text(m.SharedFiles.Count == 0 ? "None right now"
            : $"{m.SharedFiles.Count} file{(m.SharedFiles.Count == 1 ? "" : "s")} others can download from this PC", 12, fg: "Z.Muted", wrap: true));
        shared.Children.Add(sharedTexts);
        var chevron = Ui.Glyph("M9 6l6 6-6 6", 14, "Z.Muted", 2);
        chevron.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(chevron, 1);
        shared.Children.Add(chevron);
        var open = Tv.Bare(shared, () => m.Open(Push.SharedFiles), Core.Chat.ChatFiles.MySharedFilesText);
        open.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        FilesSettings.Children.Add(open);
    }

    private void ChooseFolder(TrayModel m)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = Core.Chat.ChatFiles.SaveToText, Multiselect = false };
        if (System.IO.Directory.Exists(m.SaveFolder))
        {
            dialog.InitialDirectory = m.SaveFolder;
        }
        if (dialog.ShowDialog(Window.GetWindow(this)) == true)
        {
            m.SetSaveFolder(dialog.FolderName);
        }
    }

    /// <summary>My shared files: each with its chat, date, size and Stop sharing.</summary>
    private void RenderShared(TrayModel m)
    {
        SharedList.Children.Clear();
        var note = Ui.Text(Core.Chat.ChatFiles.OwnFileText, 12.5, fg: "Z.Ink2", wrap: true);
        note.Margin = new Thickness(4, 0, 4, 12);
        SharedList.Children.Add(note);
        var files = m.SharedFiles;
        if (files.Count == 0)
        {
            var none = Ui.Text("You aren’t sharing any files right now.", 13, fg: "Z.Muted", wrap: true);
            none.Margin = new Thickness(4, 8, 4, 0);
            SharedList.Children.Add(none);
            return;
        }
        var list = new StackPanel();
        foreach (var f in files)
        {
            list.Children.Add(SharedRow(m, f));
        }
        SharedList.Children.Add(Ui.Card(list));
    }

    internal static FrameworkElement SharedRow(TrayModel m, Core.Chat.SharedFile f)
    {
        var grid = new Grid { Margin = new Thickness(14, 10, 10, 10) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var tile = Ui.Tile(FileCard.IconData(Core.Chat.ChatFiles.IconKind(f.Name)), 32, "Z.OrangeTint", "Z.OrangeInk", 8, 16);
        tile.Margin = new Thickness(0, 0, 10, 0);
        tile.VerticalAlignment = VerticalAlignment.Center;
        grid.Children.Add(tile);
        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var name = Ui.Text(f.Name, 13.5, FontWeights.SemiBold, trim: true);
        name.ToolTip = f.Name;
        texts.Children.Add(name);
        var line = $"{f.ChatName ?? m.ChatName(f.ChatKey)} · {f.CreatedAt.ToLocalTime().ToString("d MMM", System.Globalization.CultureInfo.InvariantCulture)} · {Core.Chat.ChatFiles.FormatSize(f.Size)}";
        texts.Children.Add(Ui.Text(line, 12, fg: "Z.Muted", trim: true));
        Grid.SetColumn(texts, 1);
        grid.Children.Add(texts);
        FrameworkElement action = f.State == Core.Chat.ChatFile.StateStopped
            ? Ui.Text("Stopping…", 12, fg: "Z.Muted")
            : Tv.Btn(Core.Chat.ChatFiles.StopText, false, () => m.StopSharing(f.Id), $"{Core.Chat.ChatFiles.StopText}: {f.Name}", compact: true);
        action.VerticalAlignment = VerticalAlignment.Center;
        action.Margin = new Thickness(8, 0, 0, 0);
        Grid.SetColumn(action, 2);
        grid.Children.Add(action);
        AutomationProperties.SetName(grid, $"{f.Name}, {line}");
        return new Border { Child = grid, BorderThickness = new Thickness(0, 0, 0, 1) }.Res(Border.BorderBrushProperty, "Z.Line");
    }

    // ---- Notifications -----------------------------------------------------------------

    private void RenderNotifications(TrayModel m)
    {
        var paused = m.Paused;
        PauseTitle.Text = paused ? $"Paused until {m.Settings.PausedUntil!.Value.ToLocalTime():h:mm tt}" : "Pause notifications for 1 hour";
        PauseSub.Text = paused ? "Tap to turn them back on now. Missed ones still collect below." : "For meetings or focus time. They still collect below.";
        PauseButton.SetResourceReference(BackgroundProperty, paused ? "Z.WarnSoft" : "Z.Surface");
        PauseButton.SetResourceReference(BorderBrushProperty, paused ? "Z.WarnLine" : "Z.Line");
        AutomationProperties.SetName(PauseButton, PauseTitle.Text);

        var list = m.NoticeList;
        var unread = list.Count(n => !n.Read);
        UnreadRow.Visibility = Show(unread > 0);
        UnreadLine.Text = $"{unread} unread";
        NotifList.Children.Clear();
        foreach (var n in list)
        {
            NotifList.Children.Add(NoticeRow(m, n));
        }
        NoNotifs.Visibility = Show(list.Count == 0);
    }

    private static Button NoticeRow(TrayModel m, NoticeEntry n)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var dot = Ui.Dot(8, n.Read ? "Z.Surface" : "Z.Orange");
        dot.Margin = new Thickness(0, 0, 12, 0);
        grid.Children.Add(dot);
        var texts = new StackPanel();
        texts.Children.Add(Ui.Text(n.Title, 13.5, n.Read ? FontWeights.Medium : FontWeights.Bold, n.Read ? "Z.Ink3" : "Z.Ink", trim: true));
        texts.Children.Add(Ui.Text(n.Body, 12, fg: "Z.Muted", trim: true));
        Grid.SetColumn(texts, 1);
        grid.Children.Add(texts);
        var time = Ui.Text(n.At.ToLocalTime().ToString("h:mm tt"), 12, n.Read ? FontWeights.Normal : FontWeights.SemiBold, n.Read ? "Z.Muted" : "Z.Link");
        time.FontFamily = (System.Windows.Media.FontFamily)Application.Current.FindResource("Z.Mono");
        time.Margin = new Thickness(8, 0, 0, 0);
        time.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(time, 2);
        grid.Children.Add(time);
        return Ui.Button(grid, "Z.ListItem", () => m.OpenNotice(n),
            $"{(n.Read ? "" : "Unread. ")}{n.Title}. {n.Body}. {n.At.ToLocalTime():h:mm tt}");
    }

    private void OnPause(object sender, RoutedEventArgs e) => Model?.TogglePause();

    private void OnMarkAll(object sender, RoutedEventArgs e) => Model?.MarkAllRead();

    // ---- Help ----------------------------------------------------------------------------

    private void RenderHelp(TrayModel m)
    {
        HelpList.Children.Clear();
        HelpList.Children.Add(Ui.ListRow(null, "Ask " + m.OwnerName, "For tasks, your hours, or if Zinwe stops working", () =>
        {
            m.Draft = $"Hi {m.OwnerFirst}, I need help with Zinwe: ";
            m.Go(Tab.Chat);
        }));
        HelpList.Children.Add(Ui.ListRow(null, "Zinwe help centre", "Short guides: starting your day, tasks, breaks",
            () => TrayModel.OpenLink("https://zinwe.io/help"), "↗"));
        HelpList.Children.Add(Ui.ListRow(null, "Write to the Zinwe team", "hello@zinwe.io · read by Zinwe, not your employer",
            () => TrayModel.OpenLink("mailto:hello@zinwe.io"), "↗"));
        Ui.EndList(HelpList);
    }

    // ---- Policies & terms ----------------------------------------------------------------

    private void RenderPrivacy(TrayModel m)
    {
        TabPc.IsChecked = m.PrivacyTab == "pc";
        TabRights.IsChecked = m.PrivacyTab == "rights";
        TabFair.IsChecked = m.PrivacyTab == "fair";
        TabTerms.IsChecked = m.PrivacyTab == "terms";
        var company = m.CompanyStart;
        PolicyBody.Children.Clear();
        switch (m.PrivacyTab)
        {
            case "rights":
                Section("Who controls your data", $"{company} decides what Zinwe records on this PC and who can see it. SKYTECHNO, the company behind Zinwe, runs the service for them.");
                Section("See, correct or delete it", $"Ask {company} ({m.OwnerName}). They can see, correct or delete what Zinwe holds about you. You can withdraw your agreement at any time below.");
                Section("How long it's kept", $"While {company}'s account is active, unless they ask Zinwe to delete it sooner.");
                Section("Your own to-dos", $"The to-dos you add or edit in Zinwe are stored so they follow you to a new PC, and {company}'s admins can read them. To-dos from before this update stay on this PC until you edit one. Deleting a to-do erases its text.");
                Section("Never sold", $"Zinwe never sells your data or uses it for advertising. It is encrypted on the way, and only {company}'s Zinwe admins can see it.");
                Link("Full Privacy Policy", "https://zinwe.io/legal/privacy");
                break;
            case "fair":
                Section("What Zinwe is for", "Protecting company data on work PCs the company owns or manages, with the knowledge of the people who use them.");
                Heading("What your employer may not do with Zinwe");
                List(["Monitor you without telling you", "Monitor your personal devices", "Look at your personal accounts or messages", "Use it to harass or control anyone"]);
                Section("If something feels wrong", "Write to hello@zinwe.io. The Zinwe team reads it, not your employer.");
                Link("Full Acceptable Use Policy", "https://zinwe.io/legal/acceptable-use");
                break;
            case "terms":
                Heading("Terms of Service, in short");
                List([
                    $"{company} holds the Zinwe account and accepted Zinwe’s Terms of Service. You use Zinwe as part of your work there.",
                    $"Zinwe only runs on work PCs. Ask {m.OwnerFirst} if you need it removed.",
                    "Your rights in the Privacy Policy and under the law come first. The terms don’t take them away.",
                    "Zinwe may sometimes be briefly unavailable for updates or fixes. Your times stay saved on this PC until it’s back.",
                    $"{company} can remove this PC at any time. Zinwe then stops recording here.",
                ]);
                Section("Questions about the terms", "Write to hello@zinwe.io. The Zinwe team answers, not your employer.");
                Link("Full Terms of Service", "https://zinwe.io/legal/terms");
                break;
            default:
                var notice = m.Notice;
                Heading(notice.SeesHeading);
                List(notice.Sees);
                var more = Ui.Text(notice.SeesMore, 12, fg: "Z.Muted", wrap: true);
                more.Margin = new Thickness(0, 0, 0, 8);
                PolicyBody.Children.Add(more);
                Heading(notice.NeverHeading);
                List(notice.Never);
                break;
        }

        var record = m.ConsentRecord;
        var agreed = m.Consent == ConsentState.Given;
        ConsentLine.Text = agreed
            ? $"You agreed on {(record?.AcceptedAt ?? m.Now).ToLocalTime():d MMM yyyy}. You can withdraw any time."
            : "You chose not to agree. Monitoring is off on this PC.";
        ConsentButton.Visibility = Show(!m.ConfirmWithdraw);
        ConsentButton.Content = agreed ? "Withdraw" : "I agree now";
        ConsentButton.Style = (Style)FindResource(agreed ? "Z.BtnSm" : "Z.BtnSm.Pri");
        WithdrawConfirm.Visibility = Show(m.ConfirmWithdraw);
        WithdrawText.Text = $"If you withdraw, Zinwe stops recording on this PC and {m.Company} sees that you withdrew. Start day, breaks, tasks and chat keep working.";
    }

    private void Heading(string text)
    {
        var h = Ui.Text(text, 13, FontWeights.Bold, wrap: true);
        h.Margin = new Thickness(0, PolicyBody.Children.Count == 0 ? 0 : 6, 0, 6);
        PolicyBody.Children.Add(h);
    }

    private void Section(string title, string body)
    {
        Heading(title);
        var b = Ui.Text(body, 13, fg: "Z.Ink2", wrap: true);
        b.Margin = new Thickness(0, 0, 0, 6);
        PolicyBody.Children.Add(b);
    }

    private void List(IEnumerable<string> items)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 6) };
        OnboardView.Bullets(panel, items);
        PolicyBody.Children.Add(panel);
    }

    private void Link(string text, string url)
    {
        var link = Ui.Button(text, "Z.LinkBtn", () => TrayModel.OpenLink(url));
        link.MinHeight = 44;
        PolicyBody.Children.Add(link);
    }

    private void OnPrivacyTab(object sender, RoutedEventArgs e)
    {
        if (!_syncing && sender is FrameworkElement { Tag: string tab })
        {
            Model?.SetPrivacyTab(tab);
            PrivacyScroller.ScrollToTop();
        }
    }

    private void OnConsentButton(object sender, RoutedEventArgs e)
    {
        if (Model is not { } m)
        {
            return;
        }
        if (m.Consent == ConsentState.Given)
        {
            m.AskWithdraw(true);
        }
        else
        {
            m.AgreeNow();
        }
    }

    private void OnWithdraw(object sender, RoutedEventArgs e) => Model?.Withdraw();

    private void OnKeep(object sender, RoutedEventArgs e) => Model?.AskWithdraw(false);
}

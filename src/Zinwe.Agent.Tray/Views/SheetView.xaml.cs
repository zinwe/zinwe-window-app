using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Zinwe.Agent.Tray.Widget;

namespace Zinwe.Agent.Tray.Views;

/// <summary>
/// The bottom sheets: reason (Take a break / End day), Ask {owner}, Can't do this, Add a note, Add / edit your task,
/// Profile photo and Send feedback. Focus moves in when one opens and Tab stays inside it.
/// </summary>
public partial class SheetView : UserControl
{
    private Sheet _shown = Sheet.None;
    private bool _syncing;

    private readonly FeedbackPanel Feedback = new();

    public SheetView()
    {
        InitializeComponent();
        FeedbackSlot.Content = Feedback;
        Feedback.Closed += (_, _) => Model?.CloseSheet();
    }

    public TrayModel? Model { get; set; }

    public void Render()
    {
        if (Model is not { } m)
        {
            return;
        }
        var sheet = m.Sheet;
        Visibility = sheet == Sheet.None ? Visibility.Collapsed : Visibility.Visible;
        if (sheet == Sheet.None)
        {
            _shown = Sheet.None;
            return;
        }
        var opened = sheet != _shown;
        _shown = sheet;
        ReasonPanel.Visibility = Show(sheet is Sheet.Break or Sheet.Out);
        AskPanel.Visibility = Show(sheet == Sheet.Ask);
        CantPanel.Visibility = Show(sheet == Sheet.Cant);
        NotePanel.Visibility = Show(sheet == Sheet.Note);
        NewTaskPanel.Visibility = Show(sheet == Sheet.NewTask);
        PhotoPanel.Visibility = Show(sheet == Sheet.Photo);
        Feedback.Visibility = Show(sheet == Sheet.Feedback);
        var owner = m.OwnerFirst;
        var task = m.Selected;

        (Title.Text, Hint.Text) = sheet switch
        {
            Sheet.Break or Sheet.Out => (m.Prompt!.Title, m.Prompt.Hint(m.OpenTasks)),
            Sheet.Ask => ($"Ask {owner}", task is null ? "" : $"About “{task.Title}”. {m.OwnerFirstStart} gets it in Chat."),
            Sheet.Cant => ("Can’t do this task", task is null ? ""
                : $"About “{task.Title}”. Tell {owner} why, in a few words. They can hand it to someone else or reply in Chat."),
            Sheet.Note => ($"Add a note for {owner}", "It stays on the task card."),
            Sheet.NewTask => m.Form.EditId is null ? ("Add a task for yourself", "For your own to-dos. It shows in Tasks.")
                : ("Edit your task", "Change the name, date or steps."),
            Sheet.Photo => ("Profile photo", "Optional. It shows on your profile and next to your messages in Chat."),
            _ => ("Send feedback", ""),
        };
        Hint.Visibility = Show(Hint.Text.Length > 0);
        AutomationProperties.SetName(SheetPanel, Title.Text);

        switch (sheet)
        {
            case Sheet.Break or Sheet.Out:
                RenderReason(m, opened);
                break;
            case Sheet.Ask:
                if (opened)
                {
                    AskNote.Text = "";
                }
                AskNote.Tag = $"A few words for {owner}";
                AutomationProperties.SetName(AskNote, $"Note for {owner}");
                Chips(AskChips, ["Need access", "Need info", "Need more time", "Something else"], m.TaskReason, m.PickTaskReason);
                AskCta.Content = $"Send to {owner}";
                AskCta.IsEnabled = m.TaskReason.Length > 0 || AskNote.Text.Trim().Length > 0;
                break;
            case Sheet.Cant:
                if (opened)
                {
                    CantNote.Text = "";
                }
                AutomationProperties.SetName(CantNote, $"A short reason for {owner}");
                Chips(CantChips, ["Not my area", "No time this week", "Missing access", "Something else"], m.TaskReason, m.PickTaskReason);
                CantCta.Content = $"Tell {owner} I can’t do this";
                CantCta.IsEnabled = m.TaskReason.Length > 0 || CantNote.Text.Trim().Length > 0;
                BackToAsk.Content = $"Back to Ask {owner}";
                break;
            case Sheet.Note:
                if (opened)
                {
                    NoteInput.Text = "";
                }
                NoteCta.IsEnabled = NoteInput.Text.Trim().Length > 0;
                break;
            case Sheet.NewTask:
                RenderForm(m, opened);
                break;
            case Sheet.Photo:
                RenderPhoto(m);
                break;
            case Sheet.Feedback:
                if (opened)
                {
                    Feedback.Reset();
                }
                break;
        }
        if (opened)
        {
            Dispatcher.BeginInvoke(() => SheetPanel.MoveFocus(new TraversalRequest(FocusNavigationDirection.First)), DispatcherPriority.Input);
        }
    }

    private static Visibility Show(bool on) => on ? Visibility.Visible : Visibility.Collapsed;

    private static void Chips(Panel panel, IEnumerable<string> labels, string current, Action<string> pick)
    {
        panel.Children.Clear();
        foreach (var label in labels)
        {
            var chip = Ui.Chip(label, label == current, () => pick(label));
            chip.Margin = new Thickness(0, 0, 6, 0);
            panel.Children.Add(chip);
        }
    }

    // ---- Take a break / End day ----------------------------------------------------

    private void RenderReason(TrayModel m, bool opened)
    {
        var prompt = m.Prompt!;
        if (opened)
        {
            _syncing = true;
            ReasonNote.Text = "";
            _syncing = false;
        }
        ReasonChips.Children.Clear();
        foreach (var reason in prompt.Reasons)
        {
            var chip = new RadioButton
            {
                Content = reason, GroupName = "reason", IsChecked = m.Reason == reason, Style = (Style)FindResource("Zinwe.Chip"),
            };
            chip.Checked += (_, _) =>
            {
                m.PickReason(reason);
                if (prompt.NoteRequired(reason))
                {
                    ReasonNote.Focus();
                }
            };
            ReasonChips.Children.Add(chip);
        }
        ReasonNote.Tag = prompt.NotePlaceholder(m.Reason);
        ReasonCta.Content = prompt.ConfirmLabel;
        UpdateReasonCta(m);
    }

    private void UpdateReasonCta(TrayModel m)
    {
        var missing = m.ReasonMissing;
        ReasonCta.IsEnabled = missing is null;
        ReasonMissing.Text = m.Reason is null ? "" : missing ?? "";
        ReasonMissing.Visibility = m.Reason is not null && missing is not null ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnReasonNote(object sender, TextChangedEventArgs e)
    {
        if (_syncing || Model is not { } m)
        {
            return;
        }
        m.SetReasonNote(ReasonNote.Text);
        UpdateReasonCta(m);
    }

    private void OnReasonKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && ReasonCta.IsEnabled)
        {
            e.Handled = true;
            Model?.ConfirmReason();
        }
    }

    private void OnReasonCta(object sender, RoutedEventArgs e) => Model?.ConfirmReason();

    // ---- Ask / can't / note ---------------------------------------------------------------

    private void OnTaskNote(object sender, TextChangedEventArgs e)
    {
        AskCta.IsEnabled = (Model?.TaskReason.Length ?? 0) > 0 || AskNote.Text.Trim().Length > 0;
        CantCta.IsEnabled = (Model?.TaskReason.Length ?? 0) > 0 || CantNote.Text.Trim().Length > 0;
        NoteCta.IsEnabled = NoteInput.Text.Trim().Length > 0;
    }

    private void OnSendAsk(object sender, RoutedEventArgs e) => Model?.SendAsk(AskNote.Text);

    private void OnToCant(object sender, RoutedEventArgs e)
    {
        CantNote.Text = "";
        Model?.ToCant();
    }

    private void OnSendCant(object sender, RoutedEventArgs e) => Model?.SendCant(CantNote.Text);

    private void OnBackToAsk(object sender, RoutedEventArgs e)
    {
        AskNote.Text = "";
        Model?.BackToAsk();
    }

    private void OnSaveNote(object sender, RoutedEventArgs e) => Model?.SaveNote(NoteInput.Text);

    // ---- Add / edit your task -----------------------------------------------------------

    private void RenderForm(TrayModel m, bool opened)
    {
        var f = m.Form;
        _syncing = true;
        if (opened)
        {
            NtTitle.Text = f.Title;
            NtStep.Text = "";
        }
        NtDate.Visibility = Show(f.Due == "date");
        NtDate.DisplayDateStart = DateTime.Today;
        NtDate.SelectedDate = f.Date?.ToDateTime(TimeOnly.MinValue);
        _syncing = false;

        var today = DateOnly.FromDateTime(DateTime.Today);
        var okDate = f.Date is { } d && d >= today;
        DueChips.Children.Clear();
        foreach (var (key, label) in new[] { ("today", "Today"), ("tomorrow", "Tomorrow"), ("later", "This week"), ("date", "Pick a date") })
        {
            var text = key == "date" && f.Due == "date" && okDate ? f.Date!.Value.ToString("ddd d MMM") : label;
            var chip = Ui.Chip(text, f.Due == key, () => m.SetForm(key == "date"
                ? f with { Due = "date", Date = okDate ? f.Date : Suggest(today) }
                : f with { Due = key }));
            DueChips.Children.Add(chip);
        }
        var bad = f.Due == "date" && !okDate;
        NtDateHint.Visibility = Show(f.Due == "date");
        if (f.Due == "date")
        {
            var days = okDate ? f.Date!.Value.DayNumber - today.DayNumber : 0;
            NtDateHint.Text = bad ? "Pick today or a later date." : days == 0 ? "Due today." : days == 1 ? "Due tomorrow."
                : $"Due {f.Date!.Value:ddd d MMM}, in {days} days.";
            NtDateHint.SetResourceReference(TextBlock.ForegroundProperty, bad ? "Z.RedText" : "Z.Muted");
        }

        NtSteps.Children.Clear();
        for (var i = 0; i < f.StepList.Count; i++)
        {
            var index = i;
            var row = new Grid { Margin = new Thickness(0, 0, 0, 4) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var n = Ui.Text((i + 1).ToString(), 12, fg: "Z.Muted");
            n.Margin = new Thickness(0, 0, 8, 0);
            n.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(n);
            var label = Ui.Text(f.StepList[i], 13);
            label.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(label, 1);
            row.Children.Add(label);
            var remove = Ui.Button("×", "Z.IconBtn", () => m.SetForm(f with { Steps = [.. f.StepList.Where((_, j) => j != index)] }),
                $"Remove step {i + 1}");
            remove.FontSize = 15;
            Grid.SetColumn(remove, 2);
            row.Children.Add(remove);
            var box = new Border { CornerRadius = new CornerRadius(10), Padding = new Thickness(10, 2, 2, 2), Child = row };
            box.SetResourceReference(Border.BackgroundProperty, "Z.Bg");
            NtSteps.Children.Add(box);
        }
        NtCta.Content = f.EditId is null ? "Add task" : "Save changes";
        NtCta.IsEnabled = NtTitle.Text.Trim().Length > 0 && !bad;
        NtNote.Text = $"{m.OwnerFirstStart} can see it, but doesn't need to do anything.";
    }

    /// <summary>About a week out, moved off the weekend to the Monday.</summary>
    private static DateOnly Suggest(DateOnly today)
    {
        var d = today.AddDays(6);
        while (d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
        {
            d = d.AddDays(1);
        }
        return d;
    }

    private void OnFormChanged(object sender, TextChangedEventArgs e)
    {
        if (_syncing || Model is not { } m)
        {
            return;
        }
        m.SetFormQuiet(m.Form with { Title = NtTitle.Text });
        NtCta.IsEnabled = NtTitle.Text.Trim().Length > 0 && !(m.Form.Due == "date" && (m.Form.Date is null || m.Form.Date < DateOnly.FromDateTime(DateTime.Today)));
    }

    private void OnDateChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_syncing || Model is not { } m || NtDate.SelectedDate is not { } d)
        {
            return;
        }
        m.SetForm(m.Form with { Date = DateOnly.FromDateTime(d) });
    }

    private void OnStepChanged(object sender, TextChangedEventArgs e) => NtAddStep.IsEnabled = NtStep.Text.Trim().Length > 0;

    private void OnStepKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            AddStep();
        }
    }

    private void OnAddStep(object sender, RoutedEventArgs e) => AddStep();

    private void AddStep()
    {
        if (Model is not { } m || NtStep.Text.Trim().Length == 0)
        {
            return;
        }
        m.SetForm(m.Form with { Title = NtTitle.Text, Steps = [.. m.Form.StepList, NtStep.Text.Trim()] });
        NtStep.Text = "";
        NtStep.Focus();
    }

    private void OnSaveForm(object sender, RoutedEventArgs e)
    {
        if (Model is { } m)
        {
            m.SetFormQuiet(m.Form with { Title = NtTitle.Text });
            m.SaveForm();
        }
    }

    // ---- Profile photo -------------------------------------------------------------------

    private void RenderPhoto(TrayModel m)
    {
        var name = m.Profile.DisplayName;
        PhotoPreview.Content = Ui.Avatar(m.Settings.Avatar, Avatars.Initials(name == "You" ? null : name), 64);
        PhotoName.Text = m.Settings.Avatar is null ? "Your initials"
            : m.Settings.Avatar.StartsWith("photo", StringComparison.Ordinal) ? "Your photo"
            : (Avatars.Find(m.Settings.Avatar)?.Label ?? "Your") + " avatar";
        PhotoStatus.Text = m.PhotoStatus ?? "";
        PhotoStatus.Visibility = Show(m.PhotoStatus is not null);
        PhotoError.Text = m.PhotoError ?? "";
        PhotoError.Visibility = Show(m.PhotoError is not null);
        RemovePhoto.Visibility = Show(m.Settings.Avatar is not null);
        // SEAM 4: the photo stays on this PC until photo sync exists, so the board's "Your admin can see this photo." waits.
        PhotoFoot.Text = "Kept on this PC for now. Your admin sees it once Zinwe syncs profile photos.";

        AvatarGrid.Children.Clear();
        foreach (var preset in Avatars.Presets)
        {
            var on = m.Settings.Avatar == "preset:" + preset.Key;
            var art = Ui.Art(preset, 36);
            var ring = new Border
            {
                CornerRadius = new CornerRadius(22), Padding = new Thickness(2), BorderThickness = new Thickness(2), Child = art,
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            ring.SetResourceReference(Border.BorderBrushProperty, on ? "Z.Orange" : "Z.Surface");
            var b = new RadioButton
            {
                Content = ring, IsChecked = on, GroupName = "avatar", Cursor = Cursors.Hand, MinHeight = 44,
                Template = PlainTemplate(), FocusVisualStyle = (Style)FindResource("Z.FocusRing"),
            };
            AutomationProperties.SetName(b, preset.Label + " avatar");
            var key = preset.Key;
            b.Checked += (_, _) => m.PickAvatar("preset:" + key);
            AvatarGrid.Children.Add(b);
        }
    }

    private static ControlTemplate PlainTemplate()
    {
        var t = new ControlTemplate(typeof(RadioButton));
        var grid = new FrameworkElementFactory(typeof(Grid));
        grid.SetValue(System.Windows.Controls.Panel.BackgroundProperty, System.Windows.Media.Brushes.Transparent);
        grid.AppendChild(new FrameworkElementFactory(typeof(ContentPresenter)));
        t.VisualTree = grid;
        return t;
    }

    private void OnUpload(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choose a photo",
            Filter = "Photos (*.jpg;*.jpeg;*.png;*.webp)|*.jpg;*.jpeg;*.png;*.webp|All files (*.*)|*.*",
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true)
        {
            Model?.UsePhoto(dialog.FileName);
        }
    }

    private void OnRemovePhoto(object sender, RoutedEventArgs e) => Model?.PickAvatar(null);

    private void OnClose(object sender, RoutedEventArgs e) => Model?.CloseSheet();

    private void OnBackdrop(object sender, MouseButtonEventArgs e) => Model?.CloseSheet();
}

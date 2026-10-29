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
            Sheet.Ask => ($"Ask {owner}", task is null ? "" : $"About “{task.Title}”. {m.OwnerFirstStart} " + (task.RemoteId is null ? "gets it in Chat." : "sees it on the task right away.")),
            Sheet.Cant => ("Can’t do this task", task is null ? ""
                : $"About “{task.Title}”. Tell {owner} why, in a few words. They can hand it to someone else or reply in Chat."),
            Sheet.Note => ($"Add a note for {owner}", "It stays on the task card."),
            Sheet.NewTask => m.Form.EditId is null ? ("Add a to-do", "Just for you: it stays on this PC, under My to-dos.")
                : ("Edit your to-do", "Change the name, date or steps."),
            Sheet.Photo => ("Your picture", "Optional. It shows on your team card, here and next to your messages in Chat."),
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
        NtCta.Content = f.EditId is null ? "Add to-do" : "Save changes";
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

    // ---- Your picture (FR-44) -------------------------------------------------------------

    private string? _gridStamp;

    private void RenderPhoto(TrayModel m)
    {
        var name = m.Profile.DisplayName;
        var initials = Avatars.Initials(name == "You" ? null : name);
        var draft = m.PhotoDraft;
        var current = m.MyPicture;
        var shown = draft is null ? current : new PictureView(Zinwe.Agent.Core.Ipc.PictureChoice.Photo, Jpeg: draft.Jpeg);
        PhotoPreview.Content = Ui.Avatar(shown, initials, 64);
        AutomationProperties.SetName(PhotoPreview, draft is null ? shown.Describe() : "Your new photo, not used yet");
        PhotoName.Text = draft is null ? shown.Describe() : "Your new photo";
        var status = draft is not null
            ? $"{draft.Width} × {draft.Height} · {PictureCompress.Bytes(draft.Jpeg.Length)} (from {PictureCompress.Bytes(draft.SourceBytes)}). Use it?"
            : m.PictureBusy ? "Saving…"
            : m.PhotoStatus ?? (m.Status?.Picture is { Pending: true } ? "Saved on this PC · sends when you’re back online" : null);
        PhotoStatus.Text = status ?? "";
        PhotoStatus.Visibility = Show(status is not null);
        PhotoError.Text = m.PhotoError ?? (m.PictureOn ? "" : "Pictures aren’t available on your company’s Zinwe yet.");
        PhotoError.Visibility = Show(PhotoError.Text.Length > 0);

        var on = m.PictureOn;
        DraftPanel.Visibility = Show(on && draft is not null);
        ChoosePanel.Visibility = Show(on && draft is null);
        LibraryPanel.Visibility = Show(on && draft is null);
        RemovePhoto.Visibility = Show(on && draft is null && current.HasPicture);
        UseDraft.IsEnabled = UploadButton.IsEnabled = RemovePhoto.IsEnabled = !m.PictureBusy;
        PhotoFoot.Text = "Your admin sees this picture on your team card.";

        // The grid is rebuilt only when the choice changes (keeps keyboard focus on the cell the member is on).
        var stamp = (current.IsAvatar ? current.Key : "") + "|" + m.PictureBusy;
        if (stamp == _gridStamp && AvatarGrid.Children.Count > 0)
        {
            return;
        }
        _gridStamp = stamp;
        AvatarGrid.Children.Clear();
        foreach (var avatar in AvatarLibrary.All)
        {
            var chosen = current.IsAvatar && current.Key == avatar.Key;
            var cell = new Grid { MinHeight = 44, Margin = new Thickness(0, 0, 0, 4) };
            var art = Ui.Art(avatar, 38);
            art.HorizontalAlignment = HorizontalAlignment.Center;
            art.VerticalAlignment = VerticalAlignment.Center;
            cell.Children.Add(art);
            var ring = new System.Windows.Shapes.Ellipse
            {
                Width = 44, Height = 44, StrokeThickness = chosen ? 2.5 : 1, HorizontalAlignment = HorizontalAlignment.Center,
            };
            // The board's ring: orange around the chosen one (button face in High Contrast), a hairline otherwise.
            ring.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, chosen ? "Z.Orange" : "Z.LineSoft");
            cell.Children.Add(ring);
            var b = new RadioButton
            {
                Content = cell, IsChecked = chosen, GroupName = "avatar", Cursor = Cursors.Hand, IsEnabled = !m.PictureBusy,
                Template = PlainTemplate(), FocusVisualStyle = (Style)FindResource("Z.FocusRing"), HorizontalAlignment = HorizontalAlignment.Stretch,
                ToolTip = avatar.Label,
                // The app theme's RadioButton has a MinWidth wider than a 44 px cell: without this the cell is cut.
                MinWidth = 0, MinHeight = 44, Padding = new Thickness(0),
            };
            AutomationProperties.SetName(b, avatar.Label + " avatar");
            var key = avatar.Key;
            // Checked (not Click): keyboard and UI Automation select without a click.
            b.Checked += (_, _) =>
            {
                if (Model is { } model && !(model.MyPicture.IsAvatar && model.MyPicture.Key == key))
                {
                    _ = model.PickAvatarAsync(key);
                }
            };
            AvatarGrid.Children.Add(b);
        }
    }

    private static ControlTemplate PlainTemplate()
    {
        var t = new ControlTemplate(typeof(RadioButton));
        var grid = new FrameworkElementFactory(typeof(Grid));
        grid.SetValue(System.Windows.Controls.Panel.BackgroundProperty, System.Windows.Media.Brushes.Transparent);
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(ContentPresenter.ContentProperty, new TemplateBindingExtension(ContentControl.ContentProperty));
        grid.AppendChild(presenter);
        t.VisualTree = grid;
        return t;
    }

    private void OnUpload(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choose a photo",
            Filter = PictureCompress.DialogFilter,
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true)
        {
            Model?.PreparePhoto(dialog.FileName);
            Dispatcher.BeginInvoke(() => UseDraft.Focus(), DispatcherPriority.Input);
        }
    }

    private void OnUseDraft(object sender, RoutedEventArgs e) => _ = Model?.UsePhotoAsync();

    private void OnDiscardDraft(object sender, RoutedEventArgs e) => Model?.DiscardPhoto();

    private void OnRemovePhoto(object sender, RoutedEventArgs e) => _ = Model?.RemovePictureAsync();

    private void OnClose(object sender, RoutedEventArgs e) => Model?.CloseSheet();

    private void OnBackdrop(object sender, MouseButtonEventArgs e) => Model?.CloseSheet();
}

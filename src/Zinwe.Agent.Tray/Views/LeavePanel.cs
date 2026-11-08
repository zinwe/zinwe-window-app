using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Zinwe.Agent.Core.Work;

namespace Zinwe.Agent.Tray.Views;

/// <summary>
/// v1.4.1 · "Ask for a day off" in the bottom sheet: a day (today or later), Full day / Half day, an optional note, and
/// "Send to Ananya". The ask goes through the outbox (offline: when back); the admin's answer shows on Home.
/// </summary>
public sealed class LeavePanel : UserControl
{
    private readonly DatePicker _date = new() { Margin = new Thickness(0, 4, 0, 0), FontSize = 14 };
    private readonly WrapPanel _length = new() { Margin = new Thickness(0, 10, 0, 0) };
    private readonly TextBox _note;
    private readonly TextBlock _error;
    private readonly Button _send;
    private bool _half;

    public LeavePanel()
    {
        _date.SetResourceReference(Control.FontFamilyProperty, "Zinwe.Font");
        AutomationProperties.SetName(_date, "Day");
        _date.SelectedDateChanged += (_, _) => Check();
        _note = new TextBox { Style = (Style)Application.Current.FindResource("Z.Input"), Tag = "A note for your admin (optional)", MaxLength = 280,
            Margin = new Thickness(0, 10, 0, 0) };
        AutomationProperties.SetName(_note, "Note (optional)");
        _error = Ui.Text("", 12, fg: "Z.RedText", wrap: true);
        _error.Margin = new Thickness(4, 6, 0, 0);
        _error.Visibility = Visibility.Collapsed;
        AutomationProperties.SetLiveSetting(_error, AutomationLiveSetting.Polite);
        _send = new Button { Style = (Style)Application.Current.FindResource("Z.Btn.Pri"), Margin = new Thickness(0, 12, 0, 0) };
        _send.Click += (_, _) => Send();

        var stack = new StackPanel();
        stack.Children.Add(Ui.Text("Day", 12.5, FontWeights.SemiBold, "Z.Ink2"));
        stack.Children.Add(_date);
        stack.Children.Add(_length);
        stack.Children.Add(_note);
        stack.Children.Add(_error);
        stack.Children.Add(_send);
        Content = stack;
    }

    public TrayModel? Model { get; set; }

    /// <summary>A fresh form each time the sheet opens: tomorrow, full day, no note.</summary>
    public void Reset()
    {
        _date.DisplayDateStart = DateTime.Today;
        _date.SelectedDate = DateTime.Today.AddDays(1);
        _half = false;
        _note.Text = "";
        _error.Visibility = Visibility.Collapsed;
        Render();
    }

    public void Render()
    {
        _length.Children.Clear();
        foreach (var (label, half) in new[] { ("Full day", false), ("Half day", true) })
        {
            var chip = Ui.Chip(label, _half == half, () =>
            {
                _half = half;
                Render();
            });
            chip.Margin = new Thickness(0, 0, 6, 0);
            _length.Children.Add(chip);
        }
        _send.Content = "Send to " + (Model?.OwnerFirst ?? "your admin");
    }

    private DateOnly? Day => _date.SelectedDate is { } d ? DateOnly.FromDateTime(d) : null;

    private void Check()
    {
        var problem = LeaveDays.Check(Day, DateOnly.FromDateTime(DateTime.Today));
        _error.Text = problem ?? "";
        _error.Visibility = problem is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void Send()
    {
        if (Model?.AskLeave(Day, _half, _note.Text) is { } problem)
        {
            _error.Text = problem;
            _error.Visibility = Visibility.Visible;
        }
    }
}

using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Zinwe.Agent.Core.Messages;

namespace Zinwe.Agent.Tray;

/// <summary>
/// FR-14: the member's feedback on Zinwe. Five plain choices (stored as 2, 4, 6, 8, 10 on the team's 1-10 scale)
/// and an optional note, queued in the shared outbox; the agent posts it to the Zinwe team.
/// </summary>
public partial class FeedbackPanel : UserControl
{
    /// <summary>What each choice means, and its value on the 1-10 scale the dashboard's feedback uses.</summary>
    public static readonly IReadOnlyList<(string Label, int Rating)> Choices =
        [("Poor", 2), ("Okay", 4), ("Good", 6), ("Great", 8), ("Love it", 10)];

    private readonly MessageOutbox _outbox = new();
    private readonly List<Button> _buttons = [];
    private int? _rating;

    /// <summary>The panel asks to be closed (Cancel / Close).</summary>
    public event EventHandler? Closed;

    public FeedbackPanel()
    {
        InitializeComponent();
        Scale.Columns = Choices.Count;
        foreach (var (label, rating) in Choices)
        {
            var button = new Button
            {
                Content = label,
                Height = 30,
                Margin = new Thickness(0, 0, _buttons.Count < Choices.Count - 1 ? 4 : 0, 0),
                Padding = new Thickness(2, 0, 2, 0),
                Style = (Style)FindResource("Zinwe.OutlineButton"),
            };
            AutomationProperties.SetName(button, $"{label} ({rating} out of 10)");
            button.Click += (_, _) => Pick(rating);
            _buttons.Add(button);
            Scale.Children.Add(button);
        }
    }

    /// <summary>A fresh, empty form (each time it's opened).</summary>
    public void Reset()
    {
        _rating = null;
        Message.Text = "";
        SendButton.IsEnabled = false;
        Form.Visibility = Visibility.Visible;
        Thanks.Visibility = Visibility.Collapsed;
        Paint();
    }

    private void Pick(int rating)
    {
        _rating = rating;
        SendButton.IsEnabled = true;
        Paint();
    }

    private void Paint()
    {
        for (var i = 0; i < _buttons.Count; i++)
        {
            var on = Choices[i].Rating == _rating;
            _buttons[i].Style = (Style)FindResource(on ? "Zinwe.PrimaryButton" : "Zinwe.OutlineButton");
        }
    }

    private void OnSend(object sender, RoutedEventArgs e)
    {
        if (_rating is not { } rating)
        {
            return;
        }
        _outbox.AppendFeedback(rating, Message.Text.Trim());
        Form.Visibility = Visibility.Collapsed;
        Thanks.Visibility = Visibility.Visible;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Closed?.Invoke(this, EventArgs.Empty);
}

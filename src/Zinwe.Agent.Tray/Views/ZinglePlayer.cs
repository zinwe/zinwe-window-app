using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using Zinwe.Agent.Core.Ipc;
using Zinwe.Agent.Tray.Widget;

namespace Zinwe.Agent.Tray.Views;

/// <summary>
/// Puts the special-day logo (Zingle) in the title bar's wordmark <see cref="Image"/>: the same height (the art has the
/// wordmark's aspect, so nothing moves), the still PNG or the GIF played frame by frame with a
/// <see cref="DispatcherTimer"/>, looping. The timer runs only while the window is on screen. Any error, no Zingle,
/// dark mode or High Contrast: the normal wordmark (<c>Z.Wordmark</c>) as before.
/// </summary>
internal sealed class ZinglePlayer
{
    private const string WordmarkKey = "Z.Wordmark";

    private readonly Image _image;
    private readonly DispatcherTimer _timer;
    private IReadOnlyList<GifFrame> _frames = [];
    private int _index;
    private bool _onScreen;
    private string _stamp = ZingleChoice.None.Stamp;

    public ZinglePlayer(Image image)
    {
        _image = image;
        _timer = new DispatcherTimer(DispatcherPriority.Render, image.Dispatcher);
        _timer.Tick += (_, _) => Next();
    }

    /// <summary>Show what the status says (cheap when nothing changed: called on every render).</summary>
    public void Apply(ZingleStatus? zingle)
    {
        var choice = ZingleLogo.Choose(zingle, Motion.On, lightGround: !SystemParameters.HighContrast && !HighContrastTheme.IsDark);
        if (choice.Stamp == _stamp)
        {
            return;
        }
        _stamp = choice.Stamp;
        Stop();
        _frames = [];
        if (choice.Animated && choice.File is { } gif && Load(gif) is { Count: > 1 } frames)
        {
            _frames = frames;
            _index = 0;
            Show(frames[0].Image, choice.Alt);
            Resume();
            return;
        }
        var still = choice.Animated ? zingle?.StillFile : choice.File; // a GIF that didn't decode: its still
        if (still is not null && ZingleLogo.LoadStill(still) is { } image)
        {
            Show(image, choice.Alt);
            return;
        }
        ShowWordmark();
    }

    /// <summary>The window was shown, hidden, minimised or restored: play only while it can be seen.</summary>
    public void SetOnScreen(bool onScreen)
    {
        _onScreen = onScreen;
        if (onScreen)
        {
            Resume();
        }
        else
        {
            Stop();
        }
    }

    /// <summary>Windows' animation setting changed: show the right thing on the next <see cref="Apply"/>.</summary>
    public void Invalidate() => _stamp = "";

    private static IReadOnlyList<GifFrame>? Load(string path)
    {
        try
        {
            return GifFrames.Decode(File.ReadAllBytes(path));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private void Show(System.Windows.Media.ImageSource source, string alt)
    {
        _image.Source = source;
        AutomationProperties.SetName(_image, alt);
    }

    private void ShowWordmark()
    {
        _image.SetResourceReference(Image.SourceProperty, WordmarkKey);
        AutomationProperties.SetName(_image, "Zinwe");
    }

    private void Resume()
    {
        if (_onScreen && _frames.Count > 1 && !_timer.IsEnabled)
        {
            _timer.Interval = _frames[_index].Delay;
            _timer.Start();
        }
    }

    private void Stop() => _timer.Stop();

    private void Next()
    {
        if (_frames.Count < 2)
        {
            Stop();
            return;
        }
        _index = (_index + 1) % _frames.Count;
        _image.Source = _frames[_index].Image;
        _timer.Interval = _frames[_index].Delay;
    }
}

using System.Windows;

namespace Zinwe.Agent.Tray;

/// <summary>
/// Starts once per signed-in user (HKLM\...\Run, written by the MSI). No main window at launch:
/// lives in the notification area and opens <see cref="WidgetWindow"/> on demand.
/// </summary>
public partial class App : Application
{
    // Per-session ("Local\") so every signed-in user on a shared PC gets their own tray.
    private const string SingleInstanceMutexName = @"Local\Zinwe.Agent.Tray";
    // A second start ("Zinwe" in Start, the installer's last step) asks the running tray to open the widget.
    private const string ShowSignalName = @"Local\Zinwe.Agent.Tray.Show";

    private Mutex? _singleInstance;
    private EventWaitHandle? _showSignal;
    private RegisteredWaitHandle? _showWait;
    private TrayIcon? _tray;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var show = e.Args.Contains("--show", StringComparer.OrdinalIgnoreCase);
        _singleInstance = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out var isFirst);
        if (!isFirst)
        {
            if (show && EventWaitHandle.TryOpenExisting(ShowSignalName, out var signal))
            {
                using (signal)
                {
                    signal.Set();
                }
            }
            Shutdown();
            return;
        }

        // Windows High Contrast: repaint the brand colours with the system's contrast colours.
        HighContrastTheme.Attach(Resources);

        _tray = new TrayIcon();
        _tray.Start();

        _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ShowSignalName);
        _showWait = ThreadPool.RegisterWaitForSingleObject(_showSignal,
            (_, _) => Dispatcher.BeginInvoke(() => _tray?.ShowStatus()), null, Timeout.Infinite, executeOnlyOnce: false);

        // `Zinwe.Agent.Tray.exe --show` opens the widget immediately (Start-menu shortcut, end of install, support calls).
        if (show)
        {
            _tray.ShowStatus();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _showWait?.Unregister(null);
        _showSignal?.Dispose();
        _tray?.Dispose();
        if (_singleInstance is not null)
        {
            if (_tray is not null)
            {
                _singleInstance.ReleaseMutex();
            }
            _singleInstance.Dispose();
        }
        base.OnExit(e);
    }
}

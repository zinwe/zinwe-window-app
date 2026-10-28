using System.Windows;
using Zinwe.Agent.Tray.Widget;

namespace Zinwe.Agent.Tray.Tests;

/// <summary>
/// Parses the real XAML of both windows with the app's shared resources, so a missing style or resource key
/// (e.g. after moving the button styles to App.xaml) fails here instead of on an employee's PC.
/// The windows are only constructed, never shown or closed (closing the widget would save timing files).
/// </summary>
public sealed class WindowXamlTests
{
    [Fact]
    public void The_widget_with_its_built_in_chat_loads_with_the_app_resources()
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                // pack://application:,,,/Assets/... must resolve against the Tray assembly, not the test host.
                Application.ResourceAssembly ??= typeof(App).Assembly;
                var app = Application.Current as App ?? new App();
                app.InitializeComponent();

                var store = new NotificationStore();
                using var watcher = new NotificationWatcher(store, TimeSpan.FromMinutes(5));
                // The v2 window hosts every view (Views/*), so this parses all of them with the app's styles.
                using var model = new TrayModel(store, watcher, () => Task.FromResult<string?>(null));
                Assert.NotNull(new WidgetWindow(model));
            }
            catch (Exception e)
            {
                error = e;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        Assert.Null(error);
    }
}

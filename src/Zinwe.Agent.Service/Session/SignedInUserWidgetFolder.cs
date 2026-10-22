using System.Security.Principal;
using Microsoft.Extensions.Logging;
using Zinwe.Agent.Core.Messages;
using Zinwe.Agent.Core.Usb;

namespace Zinwe.Agent.Service.Session;

/// <summary>
/// The widget folder of whoever is signed in to this PC: the seam the chat, protection notices, queued replies
/// and work timing all go through.
/// <list type="bullet">
/// <item>Agent run as a console app (dev): the agent IS the signed-in user, so it's simply its own folder.</item>
/// <item>LocalSystem service (installed): the signed-in user's <c>%LOCALAPPDATA%\Zinwe</c>, and every file
/// operation runs impersonating that user. The folder is user-writable; doing the I/O with SYSTEM rights would
/// let a planted junction redirect our write into a protected location.</item>
/// <item>Nobody signed in: <c>whenNobody</c>, nothing touched.</item>
/// </list>
/// The user is resolved on every call, so signing out / switching users is picked up at once.
/// </summary>
internal sealed partial class SignedInUserWidgetFolder(ILogger<SignedInUserWidgetFolder> logger) : IWidgetFolder
{
    private readonly bool _asSystem = UsbEnforcerSelector.IsRunningAsSystem();
    private readonly LocalWidgetFolder _own = new();
    private string? _lastFolder;

    public T Use<T>(Func<string, T> op, T whenNobody)
    {
        if (!_asSystem)
        {
            return _own.Use(op, whenNobody);
        }

        using var token = SignedInUser.OpenToken();
        if (token is null)
        {
            return whenNobody;
        }
        var appData = SignedInUser.LocalAppData(token);
        if (appData is null)
        {
            return whenNobody;
        }
        var folder = Path.Combine(appData, LocalWidgetFolder.WidgetFolderName);
        if (!string.Equals(folder, _lastFolder, StringComparison.OrdinalIgnoreCase))
        {
            _lastFolder = folder;
            LogUserFolder(folder);
        }
        return WindowsIdentity.RunImpersonated(token, () => op(folder));
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Widget folder is now the signed-in user's {Folder}")]
    private partial void LogUserFolder(string folder);
}

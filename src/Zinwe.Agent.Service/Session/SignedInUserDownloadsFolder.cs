using System.Security.Principal;
using Zinwe.Agent.Core.Messages;
using Zinwe.Agent.Core.Usb;

namespace Zinwe.Agent.Service.Session;

/// <summary>
/// v1.5 · the Downloads folder of whoever is signed in to this PC: where a file from the chat is saved (in <c>Zinwe\</c>).
/// As the LocalSystem service, that user's known Downloads folder (or <c>%USERPROFILE%\Downloads</c>), with every file
/// operation run impersonating them, like <see cref="SignedInUserWidgetFolder"/>. Under <c>dotnet run</c>, the developer's own.
/// Nobody signed in: <c>whenNobody</c>, nothing touched.
/// </summary>
internal sealed class SignedInUserDownloadsFolder : IDownloadsFolder
{
    private readonly bool _asSystem = UsbEnforcerSelector.IsRunningAsSystem();
    private readonly LocalDownloadsFolder _own = new();

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
        var downloads = KnownFolders.Downloads(token.DangerousGetHandle());
        if (downloads is null)
        {
            // The profile's own folder when Windows can't name the known folder: %USERPROFILE%\Downloads.
            var local = SignedInUser.LocalAppData(token);
            var profile = local is null ? null : Path.GetDirectoryName(Path.GetDirectoryName(local));
            if (profile is null)
            {
                return whenNobody;
            }
            downloads = Path.Combine(profile, "Downloads");
        }
        return WindowsIdentity.RunImpersonated(token, () => op(downloads));
    }
}

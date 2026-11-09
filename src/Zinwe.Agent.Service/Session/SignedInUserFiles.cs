using System.Security.Principal;
using Zinwe.Agent.Core.Chat;
using Zinwe.Agent.Core.Usb;

namespace Zinwe.Agent.Service.Session;

/// <summary>
/// v1.5 round 2 · file work AS whoever is signed in to this PC: reading the files a member shares (they stay where the
/// member keeps them), and writing downloads where the member chose. As the LocalSystem service every operation runs
/// impersonating that user, like <see cref="SignedInUserWidgetFolder"/>, so a path or a planted junction can never reach
/// more than the member can. Under <c>dotnet run</c>, the developer's own. Nobody signed in: <c>whenNobody</c>, nothing touched.
/// A file handle opened inside keeps working after the impersonation ends (access is checked when it is opened).
/// </summary>
internal sealed class SignedInUserFiles : IUserFiles
{
    private readonly bool _asSystem = UsbEnforcerSelector.IsRunningAsSystem();
    private readonly LocalUserFiles _own = new();

    public T AsUser<T>(Func<T> op, T whenNobody)
    {
        if (!_asSystem)
        {
            return _own.AsUser(op, whenNobody);
        }
        using var token = SignedInUser.OpenToken();
        return token is null ? whenNobody : WindowsIdentity.RunImpersonated(token, op);
    }

    public string? CurrentUser()
    {
        if (!_asSystem)
        {
            return _own.CurrentUser();
        }
        using var token = SignedInUser.OpenToken();
        if (token is null)
        {
            return null;
        }
        using var identity = new WindowsIdentity(token.DangerousGetHandle());
        return identity.User?.Value;
    }
}

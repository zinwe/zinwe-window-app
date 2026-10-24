using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Zinwe.Agent.Core.Ipc;

/// <summary>
/// FIX-015: who is on the other end of the "Zinwe.Agent" pipe.
/// <list type="bullet">
/// <item><b>Service side</b> (<see cref="IsTrustedTray(NamedPipeServerStream, string, out string?)"/>): the client process
///   (GetNamedPipeClientProcessId) must be the Zinwe tray: <c>&lt;install folder&gt;\tray\Zinwe.Agent.Tray.exe</c>, the
///   sibling of the service's own <c>service\</c> folder. Anything else is answered with nothing.</item>
/// <item><b>Tray side</b> (<see cref="IsTrustedService(NamedPipeClientStream, string)"/>): the server process
///   (GetNamedPipeServerProcessId) must be the process the Service Control Manager started for the
///   <c>ZinweAgent</c> service (an ordinary user can't start, replace or impersonate a service), so a program that
///   grabbed the pipe name while the service was stopped is never trusted with an enrolment.</item>
/// <item><b>Dev builds</b> (FR-05): a copy running from a source tree (<c>…\src\Zinwe.Agent.Service\bin\…</c>, i.e.
///   <c>dotnet run</c>) also trusts its partner from the same tree (<c>…\src\Zinwe.Agent.Tray\bin\…</c>). The
///   installed service runs from Program Files, so this rule never applies to it.</item>
/// </list>
/// Honest limit: a program running as the same Windows user can still inject into that user's real tray process;
/// this check stops the simple cases (scripts, look-alike programs, pipe squatting), not a determined local attacker.
/// </summary>
public static class PipePeers
{
    public const string TrayExeName = "Zinwe.Agent.Tray.exe";
    public const string ServiceExeName = "Zinwe.Agent.Service.exe";
    public const string TrayProject = "Zinwe.Agent.Tray";
    public const string ServiceProject = "Zinwe.Agent.Service";

    // ---------------------------------------------------------------- rules (pure, unit-tested)

    /// <summary>
    /// Service side: is <paramref name="clientImage"/> the Zinwe tray this service may serve?
    /// The installed layout's sibling tray, or, for a dev build of the service, the tray built in the same tree.
    /// </summary>
    public static bool IsTrustedTrayPath(string? clientImage, string serviceBaseDirectory)
    {
        if (!TryFull(clientImage, out var client))
        {
            return false;
        }
        if (TryFull(Path.Combine(serviceBaseDirectory, "..", "tray", TrayExeName), out var installed) && SamePath(client, installed))
        {
            return true;
        }
        return IsDevBuildPeer(client, serviceBaseDirectory, ServiceProject, TrayProject, TrayExeName);
    }

    /// <summary>
    /// Tray side: is the server trusted? It is the SCM's process for the installed service (default instance only),
    /// or, for a dev build of the tray, the service built in the same source tree.
    /// </summary>
    public static bool IsTrustedServer(int serverProcessId, int? scmServiceProcessId, string? serverImage, string trayBaseDirectory, bool defaultInstance)
    {
        if (defaultInstance && scmServiceProcessId is > 0 && scmServiceProcessId == serverProcessId)
        {
            return true;
        }
        return IsDevBuildPeer(serverImage, trayBaseDirectory, TrayProject, ServiceProject, ServiceExeName);
    }

    /// <summary>
    /// True when <paramref name="ownBaseDirectory"/> is a build output in a source tree (<c>&lt;root&gt;\src\&lt;ownProject&gt;\bin\…</c>)
    /// and <paramref name="peerImage"/> is <paramref name="peerExe"/> built in the same tree (<c>&lt;root&gt;\src\&lt;peerProject&gt;\bin\…</c>).
    /// </summary>
    public static bool IsDevBuildPeer(string? peerImage, string ownBaseDirectory, string ownProject, string peerProject, string peerExe)
    {
        if (!TryFull(peerImage, out var peer) || !TryFull(ownBaseDirectory, out var own))
        {
            return false;
        }
        own = Path.TrimEndingDirectorySeparator(own) + Path.DirectorySeparatorChar;
        var marker = $"{Path.DirectorySeparatorChar}src{Path.DirectorySeparatorChar}{ownProject}{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}";
        var at = own.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (at <= 0)
        {
            return false;
        }
        var peerBin = own[..at] + $"{Path.DirectorySeparatorChar}src{Path.DirectorySeparatorChar}{peerProject}{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}";
        return peer.StartsWith(peerBin, StringComparison.OrdinalIgnoreCase)
               && string.Equals(Path.GetFileName(peer), peerExe, StringComparison.OrdinalIgnoreCase);
    }

    private static bool SamePath(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static bool TryFull(string? path, out string full)
    {
        full = "";
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }
        try
        {
            full = Path.GetFullPath(path);
            return true;
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    // ---------------------------------------------------------------- live checks

    /// <summary>Service side: the connected client is the Zinwe tray. <paramref name="clientImage"/> is for the log.</summary>
    public static bool IsTrustedTray(NamedPipeServerStream pipe, string serviceBaseDirectory, out string? clientImage)
    {
        clientImage = ClientImage(pipe);
        return IsTrustedTrayPath(clientImage, serviceBaseDirectory);
    }

    /// <summary>The executable of the process on the client end of <paramref name="pipe"/>, or null.</summary>
    public static string? ClientImage(NamedPipeServerStream pipe) =>
        GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var pid) && pid != 0 ? ImagePath((int)pid) : null;

    /// <summary>The executable of the process serving <paramref name="pipe"/>, or null.</summary>
    public static string? ServerImage(NamedPipeClientStream pipe) =>
        GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var pid) && pid != 0 ? ImagePath((int)pid) : null;

    /// <summary>Tray side: the pipe server is the real Zinwe service (see the class notes).</summary>
    public static bool IsTrustedService(NamedPipeClientStream pipe, string trayBaseDirectory)
    {
        if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var pid) || pid == 0)
        {
            return false;
        }
        var scm = AgentInstance.IsDefault ? ServiceProcessId(AgentInfo.ServiceName) : null;
        if (AgentInstance.IsDefault && scm == (int)pid)
        {
            return true; // no need to read the image path
        }
        return IsTrustedServer((int)pid, scm, ImagePath((int)pid), trayBaseDirectory, AgentInstance.IsDefault);
    }

    /// <summary>Full Win32 path of a process's executable, or null (gone, or no access).</summary>
    public static string? ImagePath(int processId)
    {
        const uint ProcessQueryLimitedInformation = 0x1000;
        var process = OpenProcess(ProcessQueryLimitedInformation, false, (uint)processId);
        if (process == IntPtr.Zero)
        {
            return null;
        }
        try
        {
            var buffer = new StringBuilder(1024);
            var size = (uint)buffer.Capacity;
            return QueryFullProcessImageName(process, 0, buffer, ref size) ? buffer.ToString(0, (int)size) : null;
        }
        finally
        {
            CloseHandle(process);
        }
    }

    /// <summary>The process id the Service Control Manager reports for a running service, or null.</summary>
    public static int? ServiceProcessId(string serviceName)
    {
        const uint ScManagerConnect = 0x0001;
        const uint ServiceQueryStatus = 0x0004;
        const int ScStatusProcessInfo = 0;
        var scm = OpenSCManager(null, null, ScManagerConnect);
        if (scm == IntPtr.Zero)
        {
            return null;
        }
        try
        {
            var service = OpenService(scm, serviceName, ServiceQueryStatus);
            if (service == IntPtr.Zero)
            {
                return null;
            }
            try
            {
                var status = new ServiceStatusProcess();
                var size = (uint)Marshal.SizeOf<ServiceStatusProcess>();
                if (!QueryServiceStatusEx(service, ScStatusProcessInfo, ref status, size, out _))
                {
                    return null;
                }
                return status.ProcessId == 0 ? null : (int)status.ProcessId;
            }
            finally
            {
                CloseServiceHandle(service);
            }
        }
        finally
        {
            CloseServiceHandle(scm);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatusProcess
    {
        public uint ServiceType;
        public uint CurrentState;
        public uint ControlsAccepted;
        public uint Win32ExitCode;
        public uint ServiceSpecificExitCode;
        public uint CheckPoint;
        public uint WaitHint;
        public uint ProcessId;
        public uint ServiceFlags;
    }

    // Classic DllImport (not LibraryImport) so no unsafe/csproj change is needed in the shared Core project.
#pragma warning disable SYSLIB1054
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint clientProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint serverProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint processId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "QueryFullProcessImageNameW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder exeName, ref uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "OpenSCManagerW")]
    private static extern IntPtr OpenSCManager(string? machineName, string? databaseName, uint access);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "OpenServiceW")]
    private static extern IntPtr OpenService(IntPtr scManager, string serviceName, uint access);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceStatusEx(IntPtr service, int infoLevel, ref ServiceStatusProcess buffer, uint bufferSize, out uint bytesNeeded);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseServiceHandle(IntPtr handle);
#pragma warning restore SYSLIB1054
}

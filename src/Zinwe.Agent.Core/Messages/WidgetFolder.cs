namespace Zinwe.Agent.Core.Messages;

/// <summary>
/// The signed-in member's widget folder (<c>%LOCALAPPDATA%\Zinwe</c>): where the agent and the tray exchange the
/// chat, protection notices, queued replies and work timing. Every file operation on it goes through
/// <see cref="Use{T}"/>, so the LocalSystem service can run it AS that user (see the service's
/// SignedInUserWidgetFolder): the folder is user-writable, and touching it with SYSTEM rights would let a
/// planted junction redirect the write into a system location.
/// </summary>
public interface IWidgetFolder
{
    /// <summary>
    /// Runs <paramref name="op"/> with the folder path (as the folder's user). Returns <paramref name="whenNobody"/>
    /// without running it when no one is signed in to the PC.
    /// </summary>
    T Use<T>(Func<string, T> op, T whenNobody);
}

/// <summary>The current process user's own folder: the tray, the agent run as a console app (dev), and tests.</summary>
public sealed class LocalWidgetFolder(string? directory = null) : IWidgetFolder
{
    /// <summary>"Zinwe", or "Zinwe-dev" for the local dev run (<see cref="AgentInstance"/>). Declared first: statics initialise in order.</summary>
    public static string WidgetFolderName { get; } = AgentInstance.Suffixed("Zinwe", "-");

    public static string DefaultDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), WidgetFolderName);

    public string Directory { get; } = directory ?? DefaultDirectory;

    public T Use<T>(Func<string, T> op, T whenNobody) => op(Directory);
}

/// <summary>The Windows known folders the agent needs (for a given user's token, or the current user's with zero).</summary>
public static class KnownFolders
{
    private static readonly Guid DownloadsId = new("374DE290-123F-4565-9164-39C4925E467B"); // FOLDERID_Downloads

    /// <summary>That user's Downloads folder, or null when Windows can't say (profile not loaded).</summary>
    public static string? Downloads(IntPtr token)
    {
        var id = DownloadsId;
        var hr = SHGetKnownFolderPath(ref id, 0, token, out var path);
        try
        {
            return hr == 0 ? System.Runtime.InteropServices.Marshal.PtrToStringUni(path) : null;
        }
        finally
        {
            System.Runtime.InteropServices.Marshal.FreeCoTaskMem(path);
        }
    }

    [System.Runtime.InteropServices.DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath(ref Guid folderId, uint flags, IntPtr token, out IntPtr path);
}

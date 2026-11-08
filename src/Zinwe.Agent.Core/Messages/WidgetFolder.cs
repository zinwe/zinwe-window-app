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

/// <summary>
/// v1.5 · the signed-in member's Downloads folder (<c>%USERPROFILE%\Downloads</c>, or where they moved it): where a file from
/// the chat is saved, in <c>Downloads\Zinwe\</c>. Like <see cref="IWidgetFolder"/>, every file operation runs AS that member
/// (the service's SignedInUserDownloadsFolder), so a planted junction can't redirect a SYSTEM write.
/// </summary>
public interface IDownloadsFolder
{
    /// <summary>Runs <paramref name="op"/> with the Downloads path (as its user); <paramref name="whenNobody"/> when nobody is signed in.</summary>
    T Use<T>(Func<string, T> op, T whenNobody);
}

/// <summary>The current process user's own Downloads folder (the dev console agent, tests: a folder of their own).</summary>
public sealed class LocalDownloadsFolder(string? directory = null) : IDownloadsFolder
{
    public string Directory { get; } = directory ?? KnownFolders.Downloads(IntPtr.Zero)
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

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

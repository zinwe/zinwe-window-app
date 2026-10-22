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

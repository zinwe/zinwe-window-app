namespace Zinwe.Agent.Core.Ipc;

/// <summary>Whether the backend has team pictures (FR-44).</summary>
public static class PictureBackend
{
    /// <summary>Not known yet (no heartbeat since the start, or the PC was removed). The tray hides the control.</summary>
    public const string Unknown = "unknown";
    /// <summary>The heartbeat carries <c>picture</c>, or a picture change was stored.</summary>
    public const string On = "on";
    /// <summary>An older backend: the heartbeat has no <c>picture</c>, or <c>me/picture/</c> answered 404. The tray
    /// hides the control until a heartbeat carries the field again.</summary>
    public const string Off = "off";
}

/// <summary>
/// The member's picture as the service holds it (FR-44), on <see cref="AgentStatus.Picture"/>. The tray redraws its
/// avatars only when <see cref="Version"/> changes. Every member has a default; add new ones only as optional trailing
/// parameters (an older tray ignores the whole object, a newer tray reads an older service's status as "no pictures").
/// </summary>
/// <param name="Backend">See <see cref="PictureBackend"/>.</param>
/// <param name="Kind">"avatar" or "photo" (<see cref="Backend.MemberPicture"/>), or null for initials.</param>
/// <param name="Key">The library avatar's key ("a07") when <paramref name="Kind"/> is "avatar".</param>
/// <param name="File">With a photo: the 256 × 256 JPEG in the member's own widget folder (the tray can't read the
/// service's data folder). Null while it isn't downloaded yet, or nobody is signed in.</param>
/// <param name="Version">Goes up on every change of the fields above.</param>
/// <param name="Pending">True while the member's latest choice waits to be sent (offline): the fields above already
/// show it.</param>
public sealed record PictureStatus(
    string Backend = PictureBackend.Unknown,
    string? Kind = null,
    string? Key = null,
    string? File = null,
    int Version = 0,
    bool Pending = false)
{
    /// <summary>The tray offers "Change picture" only when the backend has pictures.</summary>
    public bool CanChange => Backend == PictureBackend.On;
}

/// <summary>
/// A "picture" pipe request's payload (FR-44): <see cref="Action"/> "avatar" (+ <see cref="Key"/>), "photo"
/// (+ <see cref="Jpeg"/>, base64 of the tray's compressed 256 × 256 JPEG) or "remove".
/// </summary>
public sealed record PictureChoice(string Action, string? Key = null, string? Jpeg = null)
{
    public const string Avatar = "avatar";
    public const string Photo = "photo";
    public const string Remove = "remove";

    /// <summary>The largest photo the service takes (the server's own limit is 150 KB).</summary>
    public const int MaxJpegBytes = 150 * 1024;
}

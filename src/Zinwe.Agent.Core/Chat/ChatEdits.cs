namespace Zinwe.Agent.Core.Chat;

/// <summary>
/// v1.4.1 · when a member may still edit or delete one of their own lines: for 15 minutes after sending it (the backend
/// refuses after that, in <see cref="TooLate"/>'s words). Task cards, system lines, deleted lines and lines not stored yet
/// can't be changed.
/// </summary>
public static class ChatEdits
{
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(15);

    public const string TooLate = "You can change a message for 15 minutes after sending it.";

    public const string DeletedText = "This message was deleted";

    public static bool CanChange(TeamMessage message, bool mine, DateTimeOffset now) =>
        mine && message.Id > 0 && !message.Deleted && !message.System && message.TaskId is null && message.At != default
        && now - message.At < Window && now - message.At > -Window;

    /// <summary>How long until the actions go away (zero when they already have).</summary>
    public static TimeSpan Left(TeamMessage message, DateTimeOffset now)
    {
        var left = message.At + Window - now;
        return left > TimeSpan.Zero ? left : TimeSpan.Zero;
    }
}

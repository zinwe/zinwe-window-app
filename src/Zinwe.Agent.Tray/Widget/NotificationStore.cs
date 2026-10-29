using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Zinwe.Agent.Tray.Widget;

/// <summary>One admin -> employee message shown in the widget.</summary>
/// <param name="Id">Stable unique id (the service should generate a UUID).</param>
/// <param name="CreatedAtUtc">When the message was raised.</param>
/// <param name="Severity">info | warning | critical (drives the dot colour; free-form tolerated).</param>
/// <param name="Title">Short headline.</param>
/// <param name="Body">Optional longer text.</param>
/// <param name="Read">Whether the employee has seen it (the widget sets this locally).</param>
public sealed record NotificationItem(
    string Id,
    DateTimeOffset CreatedAtUtc,
    string Severity,
    string Title,
    string? Body = null,
    bool Read = false,
    // "owner" = an admin message/alert; "member" = the employee's own reply (shown as outgoing).
    string Sender = "owner",
    // "chat" = a line of the admin<->employee conversation; "notice" = a note from Zinwe's own protection.
    // Null in files written before chat existed (see IsChat).
    string? Kind = null,
    // FR-20 v2: the chat line the owner's new task created (the server's task id); the chat draws the task card
    // here, and the task's own notice stands for it (no second "new message"). Null on every other line.
    long? TaskId = null)
{
    public const string ChatKind = "chat";

    /// <summary>
    /// Part of the admin↔employee conversation (shown in the Chat window), not a protection notice.
    /// Older files carry no Kind: server messages have positive numeric ids, the agent's own notices negative ones.
    /// </summary>
    [JsonIgnore]
    public bool IsChat => Kind is not null
        ? string.Equals(Kind, ChatKind, StringComparison.OrdinalIgnoreCase)
        : long.TryParse(Id, out var n) && n > 0;

    /// <summary>Written by the admin (not the employee's own reply).</summary>
    [JsonIgnore]
    public bool IsFromAdmin => !string.Equals(Sender, "member", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Root object persisted to <see cref="WidgetPaths.Notifications"/>.</summary>
public sealed record NotificationFile(List<NotificationItem> Items)
{
    public NotificationFile() : this([]) { }
}

/// <summary>
/// Reads and writes the per-user notifications JSON file.
///
/// SEAM (documented contract for the LATER milestone): the service -- when it receives an
/// admin-sent alert -- APPENDS a <see cref="NotificationItem"/> to the "items" array of the signed-in
/// user's <c>%LOCALAPPDATA%\Zinwe\notifications.json</c>. The service must:
///   - generate a fresh unique <c>id</c> per message (the widget de-dupes "new" by id),
///   - write atomically (temp file + move) to avoid a half-written read,
///   - leave <c>read=false</c>; the widget flips it to true once shown.
/// The widget owns marking items read and never deletes the service's entries except on the
/// employee's explicit "Clear" action. No pipe/PipeProtocol change is made for the base app.
/// </summary>
public sealed class NotificationStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    public NotificationFile Load()
    {
        try
        {
            if (File.Exists(WidgetPaths.Notifications))
            {
                var text = File.ReadAllText(WidgetPaths.Notifications);
                var file = JsonSerializer.Deserialize<NotificationFile>(text, Json);
                if (file is not null)
                {
                    file.Items.RemoveAll(i => i is null || string.IsNullOrWhiteSpace(i.Id));
                    // Newest first for display.
                    file.Items.Sort((a, b) => b.CreatedAtUtc.CompareTo(a.CreatedAtUtc));
                    return file;
                }
            }
        }
        catch
        {
            // Unreadable or mid-write: treat as empty; the next poll will pick it up.
        }
        return new NotificationFile();
    }

    public void Save(NotificationFile file)
    {
        try
        {
            WidgetPaths.EnsureDirectory();
            var tmp = WidgetPaths.Notifications + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(file, Json));
            File.Move(tmp, WidgetPaths.Notifications, overwrite: true);
        }
        catch
        {
            // Best effort.
        }
    }

    /// <summary>
    /// Marks the matching items read and persists (only when something changed). Returns the updated file.
    /// The agent reports chat lines as read to the admin only after this flips them.
    /// </summary>
    public NotificationFile MarkRead(Func<NotificationItem, bool> which)
    {
        var file = Load();
        var changed = false;
        for (var i = 0; i < file.Items.Count; i++)
        {
            if (!file.Items[i].Read && which(file.Items[i]))
            {
                file.Items[i] = file.Items[i] with { Read = true };
                changed = true;
            }
        }
        if (changed)
        {
            Save(file);
        }
        return file;
    }

    /// <summary>The employee opened the chat: the admin's messages are read.</summary>
    public NotificationFile MarkChatRead() => MarkRead(i => i.IsChat);

    /// <summary>The member opened an owner's task: the chat line that brought it is read too.</summary>
    public NotificationFile MarkTaskRead(long taskId) => MarkRead(i => i.TaskId == taskId);

    /// <summary>"Mark read" on the widget's Notices list.</summary>
    public NotificationFile MarkNoticesRead() => MarkRead(i => !i.IsChat);

    /// <summary>
    /// Removes the protection notices (employee's explicit "Clear"). The chat is kept: it's the conversation
    /// history, and clearing it would only make the agent re-deliver it as unread.
    /// </summary>
    public NotificationFile ClearNotices()
    {
        var file = Load();
        if (file.Items.RemoveAll(i => !i.IsChat) > 0)
        {
            Save(file);
        }
        return file;
    }

#if DEBUG
    /// <summary>DEBUG-only self-seed so the widget can be exercised before the service writes real alerts.</summary>
    public void DebugAppend(NotificationItem item)
    {
        var file = Load();
        file.Items.Add(item);
        Save(file);
    }
#endif
}

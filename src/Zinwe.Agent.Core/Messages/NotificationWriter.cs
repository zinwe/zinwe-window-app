using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Zinwe.Agent.Core.Backend;

namespace Zinwe.Agent.Core.Messages;

// Mirrors the widget's notification file schema (Zinwe.Agent.Tray.Widget.NotificationStore). The service
// can't reference the tray project, so the shape is duplicated here. Keys are PascalCase; the widget reads
// them case-insensitively. The file lives in the signed-in member's %LOCALAPPDATA%\Zinwe, reached through
// IWidgetFolder (the dev console agent: its own folder; the LocalSystem service: the signed-in user's, as them).
// Kind: "chat" = a line of the owner<->member conversation (shown in the widget's Chat window);
// "notice" = a note from Zinwe's own protection (USB blocked, ...). Null in files written before chat existed.
// TaskId (FR-20 v2): the chat line the owner's new task created; the widget draws the task card there. Left out
// (null) on every other line, so trays that don't know it read the file exactly as before.
// ReadByOwner: the owner has read this line of the member's ("Seen" in the chat). Older trays ignore it.
internal sealed record WidgetNotification(
    string Id, DateTimeOffset CreatedAtUtc, string Severity, string Title, string? Body, bool Read, string Sender = "owner",
    string? Kind = null, long? TaskId = null, bool ReadByOwner = false);

internal sealed class WidgetNotificationFile
{
    public List<WidgetNotification> Items { get; set; } = [];
}

/// <summary>
/// Merges owner→member messages into the widget's notifications.json. New messages are appended with
/// Read=false; messages already present keep their (widget-set) Read flag, so delivery never un-reads them.
/// </summary>
public sealed class NotificationWriter
{
    /// <summary>A line of the owner↔member conversation (from the backend).</summary>
    public const string ChatKind = "chat";

    /// <summary>A note from Zinwe's own protection on this PC (written by <see cref="IMemberNotifier"/>).</summary>
    public const string NoticeKind = "notice";

    private const int MaxItems = 100;

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private const string FileName = "notifications.json";

    private readonly IWidgetFolder _folder;

    /// <summary><paramref name="directory"/> defaults to %LOCALAPPDATA%\Zinwe (the current user's widget folder).</summary>
    public NotificationWriter(string? directory = null) : this(new LocalWidgetFolder(directory))
    {
    }

    /// <summary>Writes into whichever user's folder <paramref name="folder"/> resolves (the service: the signed-in member).</summary>
    public NotificationWriter(IWidgetFolder folder) => _folder = folder;

    /// <summary>The file path right now ("" when nobody is signed in).</summary>
    public string FilePath => _folder.Use(dir => Path.Combine(dir, FileName), "");

    /// <summary>False while nobody is signed in: there is no widget to deliver to.</summary>
    public bool HasSignedInUser => _folder.Use(_ => true, false);

    /// <summary>Adds owner↔member chat lines not already in the file. Returns how many were newly added.</summary>
    public int MergeChat(IReadOnlyList<AgentMessage> messages) => Merge(messages, ChatKind);

    /// <summary>Adds notes from Zinwe's own protection (USB blocked, ...); never shown as chat. Returns how many were added.</summary>
    public int AddNotices(IReadOnlyList<AgentMessage> notices) => Merge(notices, NoticeKind);

    private int Merge(IReadOnlyList<AgentMessage> messages, string kind)
    {
        if (messages.Count == 0)
        {
            return 0;
        }
        return _folder.Use(dir => MergeInto(Path.Combine(dir, FileName), messages, kind), 0);
    }

    private static int MergeInto(string path, IReadOnlyList<AgentMessage> messages, string kind)
    {
        var file = Load(path);
        var seen = new HashSet<string>(file.Items.Select(i => i.Id), StringComparer.Ordinal);

        // A line already here can only change one way: the owner has now read it.
        var readByOwner = messages.Where(m => m.ReadByOwner).Select(m => m.Id.ToString(CultureInfo.InvariantCulture)).ToHashSet(StringComparer.Ordinal);
        var nowRead = false;
        for (var i = 0; i < file.Items.Count; i++)
        {
            if (!file.Items[i].ReadByOwner && readByOwner.Contains(file.Items[i].Id))
            {
                file.Items[i] = file.Items[i] with { ReadByOwner = true };
                nowRead = true;
            }
        }

        var added = 0;
        foreach (var m in messages)
        {
            var id = m.Id.ToString(CultureInfo.InvariantCulture);
            if (seen.Add(id))
            {
                // The member's own replies arrive "already read" (they wrote them); admin lines start unread.
                var isMember = string.Equals(m.Sender, "member", StringComparison.OrdinalIgnoreCase);
                file.Items.Add(new WidgetNotification(id, m.CreatedAt, m.Severity, m.Title, m.Body, Read: isMember, Sender: m.Sender, Kind: kind,
                    TaskId: m.Task is { Id: > 0 } task ? task.Id : null, ReadByOwner: m.ReadByOwner));
                added++;
            }
        }
        if (added > 0)
        {
            // Keep the newest MaxItems so the file can't grow without bound.
            file.Items = [.. file.Items.OrderByDescending(i => i.CreatedAtUtc).Take(MaxItems)];
            Save(path, file);
        }
        else if (nowRead)
        {
            Save(path, file);
        }
        return added;
    }

    /// <summary>
    /// True when every one of <paramref name="messageIds"/> is in the file and the member has opened it in the
    /// widget (Read=true). A missing id counts as unread. Used to report "read" to the backend only once the
    /// member has actually seen the messages, not merely when they reached the PC.
    /// </summary>
    public bool AllReadLocally(IEnumerable<long> messageIds) => _folder.Use(dir =>
    {
        var read = Load(Path.Combine(dir, FileName)).Items
            .Where(i => i.Read)
            .Select(i => i.Id)
            .ToHashSet(StringComparer.Ordinal);
        return messageIds.All(id => read.Contains(id.ToString(CultureInfo.InvariantCulture)));
    }, false);

    private static WidgetNotificationFile Load(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                return JsonSerializer.Deserialize<WidgetNotificationFile>(File.ReadAllText(path), Json) ?? new WidgetNotificationFile();
            }
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            // Unreadable / mid-write: treat as empty; the next sync re-adds.
        }
        return new WidgetNotificationFile();
    }

    private static void Save(string path, WidgetNotificationFile file)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(file, Json));
        File.Move(temp, path, overwrite: true);
    }
}

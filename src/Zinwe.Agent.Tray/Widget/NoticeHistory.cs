using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Zinwe.Agent.Tray.Widget;

/// <summary>
/// One line of Me › Notifications. <see cref="Go"/> is where it leads: "chat", "home", "task:{id}", "startday",
/// "endbreak", "endday", "open". <see cref="Key"/> lets an answer clear it (e.g. "consent", "update:1.3.0").
/// </summary>
public sealed record NoticeEntry(string Id, string Title, string Body, string Go, DateTimeOffset At, bool Read, string? Key = null)
{
    /// <summary>Where the entry came from: the shared notifications file (chat / protection notices) or the tray itself.</summary>
    [JsonIgnore]
    public bool FromFile { get; init; }
}

/// <summary>Unread counts: Chat counts message notices, Tasks counts task notices, Me counts the rest.</summary>
public sealed record UnreadCounts(int Chat, int Tasks, int Other)
{
    public int All => Chat + Tasks + Other;

    public static UnreadCounts Of(IEnumerable<NoticeEntry> entries)
    {
        var unread = entries.Where(e => !e.Read).ToList();
        var chat = unread.Count(e => e.Go == "chat");
        var tasks = unread.Count(e => e.Go.StartsWith("task:", StringComparison.Ordinal));
        return new UnreadCounts(chat, tasks, unread.Count - chat - tasks);
    }
}

/// <summary>
/// The notification history: ONE read state for every counter (the taskbar dot, the tab badges and Me ›
/// Notifications all count the same list). Chat lines and protection notices come from the shared
/// notifications file the service writes (their Read flag is the real one the agent reports); the tray's own
/// notices (task notices, reminders, update offers, the consent question) are kept in notice-history.json.
/// </summary>
public sealed class NoticeHistory(string? directory = null)
{
    public const int Max = 30;

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly string _path = Path.Combine(directory ?? WidgetPaths.Directory, "notice-history.json");
    private List<NoticeEntry>? _local;

    public IReadOnlyList<NoticeEntry> Local => _local ??= Read();

    /// <summary>Adds a tray notice (newest last in the file). Returns it.</summary>
    public NoticeEntry Add(string title, string body, string go, DateTimeOffset at, string? key = null, bool read = false)
    {
        var entry = new NoticeEntry(Guid.NewGuid().ToString("N"), title, body, go, at, read, key);
        var list = Local.ToList();
        list.Add(entry);
        _local = list.Count > Max ? list[^Max..] : list;
        Write();
        return entry;
    }

    /// <summary>Marks the tray's own notices read; returns true when something changed.</summary>
    public bool MarkRead(Func<NoticeEntry, bool> which)
    {
        var changed = false;
        _local = [.. Local.Select(e =>
        {
            if (e.Read || !which(e))
            {
                return e;
            }
            changed = true;
            return e with { Read = true };
        })];
        if (changed)
        {
            Write();
        }
        return changed;
    }

    /// <summary>Forget the tray's own notices of earlier days (the history is "today", like the board).</summary>
    public void DropBefore(DateTime localDay)
    {
        var keep = Local.Where(e => e.At.ToLocalTime().Date >= localDay).ToList();
        if (keep.Count != Local.Count)
        {
            _local = keep;
            Write();
        }
    }

    /// <summary>
    /// Everything for the history list, newest first: the tray's notices plus one line per admin chat message and
    /// per protection notice from the shared file. Pure apart from <see cref="Local"/>.
    /// </summary>
    public IReadOnlyList<NoticeEntry> Merge(IEnumerable<NotificationItem> items, string ownerName) =>
        Merge(Local, items, ownerName);

    public static IReadOnlyList<NoticeEntry> Merge(IEnumerable<NoticeEntry> local, IEnumerable<NotificationItem> items, string ownerName)
    {
        // A chat line that carries an owner's task isn't "a new message": the task's own notice (TaskNotices) is it.
        var fromFile = items
            .Where(i => (!i.IsChat || i.IsFromAdmin) && i.TaskId is null)
            .Select(i => i.IsChat
                ? new NoticeEntry("chat:" + i.Id, "New message from " + ownerName, ChatFeed.Preview(i, ownerName), "chat",
                    i.CreatedAtUtc, i.Read) { FromFile = true }
                : new NoticeEntry("notice:" + i.Id, i.Title, i.Body ?? "", "home", i.CreatedAtUtc, i.Read) { FromFile = true });
        return [.. local.Concat(fromFile).OrderByDescending(e => e.At).Take(Max)];
    }

    private List<NoticeEntry> Read()
    {
        try
        {
            return File.Exists(_path) ? JsonSerializer.Deserialize<List<NoticeEntry>>(File.ReadAllText(_path), Json) ?? [] : [];
        }
        catch
        {
            return [];
        }
    }

    private void Write()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_local ?? [], Json));
            File.Move(tmp, _path, overwrite: true);
        }
        catch
        {
            // Best effort.
        }
    }
}

/// <summary>Settings › Notifications and Look, the profile picture, and which reminders already went today.</summary>
public sealed record TraySettings
{
    public bool NotifyOn { get; init; } = true;
    public bool DueRemind { get; init; } = true;
    public bool DayRemind { get; init; } = true;
    public bool Dark { get; init; }
    public bool TextLarge { get; init; }
    public DateTimeOffset? PausedUntil { get; init; }
    /// <summary>null = initials; "preset:{key}" = a preset avatar; "photo" = the member's own photo (avatar.png).</summary>
    public string? Avatar { get; init; }
    /// <summary>Reminder keys already shown, and the local day they belong to.</summary>
    public string RemindedDay { get; init; } = "";
    public IReadOnlyList<string> Reminded { get; init; } = [];
    public IReadOnlyList<int> Nudged { get; init; } = [];

    public bool Paused(DateTimeOffset now) => PausedUntil is { } until && now < until;
}

public sealed class TraySettingsStore(string? directory = null)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly string _path = Path.Combine(directory ?? WidgetPaths.Directory, "tray-settings.json");

    public TraySettings Load()
    {
        try
        {
            return File.Exists(_path) ? JsonSerializer.Deserialize<TraySettings>(File.ReadAllText(_path), Json) ?? new() : new();
        }
        catch
        {
            return new();
        }
    }

    public void Save(TraySettings settings)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(settings, Json));
            File.Move(tmp, _path, overwrite: true);
        }
        catch
        {
            // Best effort.
        }
    }
}

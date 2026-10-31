using System.IO;
using System.Text.Json;
using Zinwe.Agent.Core.Messages;

namespace Zinwe.Agent.Tray.Widget;

public enum ThreadKind { Day, Text, Sys, Task }

/// <summary>The message a reply quotes ("Replying to …").</summary>
public sealed record Quote(string Who, string Text, string TargetKey);

/// <summary>One row of the v2 chat (WhatsApp style): a date chip, a bubble, a centred system line or a task card.</summary>
public sealed record ThreadRow
{
    public string Key { get; init; } = "";
    public ThreadKind Kind { get; init; }
    public bool Mine { get; init; }
    public string Text { get; init; } = "";
    /// <summary>"10:02 AM", "1:10 PM · Seen", "Sending…", "Not sent".</summary>
    public string Meta { get; init; } = "";
    /// <summary>Last bubble of a run from the owner: their avatar shows next to it (the member's own bubbles have none).</summary>
    public bool HasAvatar { get; init; }
    public Quote? Quote { get; init; }
    public MemberTask? Task { get; init; }
    public bool Pending { get; init; }
    /// <summary>The backend refused this reply: "Not sent", with Retry and Delete.</summary>
    public bool Failed { get; init; }
    /// <summary>The owner has read this line of the member's.</summary>
    public bool Seen { get; init; }
    /// <summary>Sys lines: false = an approval ("ok"), true = a decision against ("no").</summary>
    public bool ToneNo { get; init; }
    /// <summary>An "Important" / "Urgent" admin message.</summary>
    public string? Importance { get; init; }
    public DateTimeOffset At { get; init; }

    /// <summary>The one-line snippet a reply to this row quotes.</summary>
    public string Snippet
    {
        get
        {
            if (Kind == ThreadKind.Task && Task is { } t)
            {
                return "Task: " + t.Title;
            }
            var line = Text.Split('\n').FirstOrDefault() ?? "";
            return line.Length > 140 ? line[..140] : line;
        }
    }
}

/// <summary>
/// Builds the v2 conversation from the real chat (the shared notifications file + the replies still in the outbox),
/// the owner's task cards and the local reply quotes. Pure. A chat line that carries an owner's task (FR-20 v2,
/// <see cref="NotificationItem.TaskId"/>) becomes that task's card, live (its status as the Tasks tab shows it); an
/// owner's task without such a line (samples, or a line older than the kept history) gets a card at the time it was sent.
/// </summary>
public static class ChatThread
{
    public static IReadOnlyList<ThreadRow> Build(
        IEnumerable<NotificationItem> items, IEnumerable<OutboxReply> pending, IEnumerable<MemberTask> tasks,
        IReadOnlyList<ReplyQuotes.Entry> quotes, string ownerFirst, DateTimeOffset now, bool offline = false)
    {
        var rows = new List<ThreadRow>();
        var owners = tasks.Where(t => !t.Mine).ToList();
        var carded = new HashSet<int>();
        foreach (var i in items.Where(i => i.IsChat))
        {
            if (i.TaskId is { } taskId && owners.FirstOrDefault(t => t.RemoteId == taskId) is { } live)
            {
                carded.Add(live.Id);
                rows.Add(new ThreadRow { Key = "task:" + live.Id, Kind = ThreadKind.Task, Task = live, At = i.CreatedAtUtc });
                continue;
            }
            if (i.IsFromAdmin && ChatFeed.IsBreakNotApproved(i))
            {
                var (title, text) = ChatFeed.Content(i, ownerFirst);
                rows.Add(new ThreadRow { Key = i.Id, Kind = ThreadKind.Sys, ToneNo = true, At = i.CreatedAtUtc,
                    Text = string.IsNullOrWhiteSpace(text) ? title : $"{title}: {text}" });
                continue;
            }
            var (head, body) = ChatFeed.Content(i, ownerFirst);
            var mine = !i.IsFromAdmin;
            var severity = (i.Severity ?? "info").ToLowerInvariant();
            rows.Add(new ThreadRow
            {
                Key = i.Id, Kind = ThreadKind.Text, Mine = mine, At = i.CreatedAtUtc,
                Text = head.Length > 0 ? head + "\n" + body : body,
                Meta = i.CreatedAtUtc.ToLocalTime().ToString("h:mm tt"), Seen = mine && i.ReadByOwner,
                Quote = mine ? ReplyQuotes.Find(quotes, body, i.CreatedAtUtc) : null,
                Importance = !mine && severity is "warning" or "critical" ? (severity == "critical" ? "Urgent" : "Important") : null,
            });
        }
        foreach (var t in owners.Where(t => !carded.Contains(t.Id)))
        {
            rows.Add(new ThreadRow { Key = "task:" + t.Id, Kind = ThreadKind.Task, Task = t, At = t.SentAt });
        }
        rows = [.. rows.OrderBy(r => r.At)];
        foreach (var p in pending.OrderBy(p => p.CreatedAtUtc))
        {
            rows.Add(new ThreadRow
            {
                Key = "pending:" + p.Id, Kind = ThreadKind.Text, Mine = true, Pending = !p.Refused, Failed = p.Refused, At = p.CreatedAtUtc, Text = p.Body,
                Meta = p.Refused ? "Not sent" : offline ? p.CreatedAtUtc.ToLocalTime().ToString("h:mm tt") + " · sends when online" : "Sending…",
                Quote = ReplyQuotes.Find(quotes, p.Body, p.CreatedAtUtc),
            });
        }

        // "Seen" belongs to the newest line the owner has read, not to every line above it.
        var seenAt = rows.FindLastIndex(r => r.Seen);

        // Date lines, and the owner's avatar on the last bubble of each run.
        var result = new List<ThreadRow>(rows.Count + 4);
        DateTime? day = null;
        for (var n = 0; n < rows.Count; n++)
        {
            var row = rows[n];
            var rowDay = row.At.ToLocalTime().Date;
            if (rowDay != day)
            {
                day = rowDay;
                result.Add(new ThreadRow { Key = "day:" + rowDay.ToString("yyyy-MM-dd"), Kind = ThreadKind.Day, At = row.At,
                    Text = ChatFeed.DayLabel(rowDay, now.ToLocalTime().Date) });
            }
            var next = n + 1 < rows.Count ? rows[n + 1] : null;
            var sameRun = next is not null && next.At.ToLocalTime().Date == rowDay && next.Mine == row.Mine && next.Kind == ThreadKind.Text;
            result.Add(row with { HasAvatar = row.Kind == ThreadKind.Text && !row.Mine && !sameRun, Meta = n == seenAt ? row.Meta + " · Seen" : row.Meta });
        }
        return result;
    }
}

/// <summary>
/// SEAM 2 · Reply quotes. The message API has no "reply to" yet, so the quote a reply shows is kept on this PC
/// (chat-quotes.json) and matched to the sent line by its text; the text itself goes to the owner as it is.
/// Later: a reply_to field on devices/messages/ (backward compatible: older owners just see the text).
/// </summary>
public sealed class ReplyQuotes(string? directory = null)
{
    public sealed record Entry(string Body, DateTimeOffset At, string Who, string Text, string TargetKey);

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly string _path = Path.Combine(directory ?? WidgetPaths.Directory, "chat-quotes.json");
    private List<Entry>? _items;

    public IReadOnlyList<Entry> Items => _items ??= Read();

    public void Add(string body, Quote quote, DateTimeOffset at)
    {
        var list = Items.Where(e => at - e.At < TimeSpan.FromDays(30)).ToList();
        list.Add(new Entry(body.Trim(), at, quote.Who, quote.Text, quote.TargetKey));
        _items = list;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(list, Json));
        }
        catch
        {
            // Best effort: the reply still goes out, just without the local quote.
        }
    }

    /// <summary>The quote for a member line with this text sent around <paramref name="at"/> (±15 minutes).</summary>
    public static Quote? Find(IReadOnlyList<Entry> entries, string body, DateTimeOffset at)
    {
        var text = body.Trim();
        var hit = entries.LastOrDefault(e => e.Body == text && (at - e.At).Duration() <= TimeSpan.FromMinutes(15));
        return hit is null ? null : new Quote(hit.Who, hit.Text, hit.TargetKey);
    }

    private List<Entry> Read()
    {
        try
        {
            return File.Exists(_path) ? JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(_path), Json) ?? [] : [];
        }
        catch
        {
            return [];
        }
    }
}

/// <summary>Names for avatar circles (the pictures themselves: <see cref="AvatarLibrary"/>, FR-44).</summary>
public static class Avatars
{
    /// <summary>"Emily Carter" → "EC"; "ravi" → "R"; empty → "" (the avatar then shows the official Zinwe mark).</summary>
    public static string Initials(string? name)
    {
        var parts = (name ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length switch
        {
            0 => "",
            1 => parts[0][..1].ToUpperInvariant(),
            _ => (parts[0][..1] + parts[^1][..1]).ToUpperInvariant(),
        };
    }

    /// <summary>"Ananya Rao" → "Ananya".</summary>
    public static string First(string? name) =>
        (name ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries) is { Length: > 0 } p ? p[0] : "";
}

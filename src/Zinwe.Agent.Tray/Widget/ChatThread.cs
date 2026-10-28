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
    /// <summary>"Ananya · 10:02 AM", "You · 1:10 PM", "You · Sending…".</summary>
    public string Meta { get; init; } = "";
    /// <summary>Last bubble of a run from the same person: the avatar shows next to it.</summary>
    public bool HasAvatar { get; init; }
    public Quote? Quote { get; init; }
    public MemberTask? Task { get; init; }
    public bool Pending { get; init; }
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
/// the owner's task cards (task seam) and the local reply quotes. Pure.
/// </summary>
public static class ChatThread
{
    public static IReadOnlyList<ThreadRow> Build(
        IEnumerable<NotificationItem> items, IEnumerable<OutboxReply> pending, IEnumerable<MemberTask> tasks,
        IReadOnlyList<ReplyQuotes.Entry> quotes, string ownerFirst, DateTimeOffset now, bool offline = false)
    {
        var rows = new List<ThreadRow>();
        foreach (var i in items.Where(i => i.IsChat))
        {
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
                Meta = (mine ? "You" : ownerFirst) + " · " + i.CreatedAtUtc.ToLocalTime().ToString("h:mm tt"),
                Quote = mine ? ReplyQuotes.Find(quotes, body, i.CreatedAtUtc) : null,
                Importance = !mine && severity is "warning" or "critical" ? (severity == "critical" ? "Urgent" : "Important") : null,
            });
        }
        foreach (var t in tasks.Where(t => !t.Mine))
        {
            rows.Add(new ThreadRow { Key = "task:" + t.Id, Kind = ThreadKind.Task, Task = t, At = t.SentAt,
                Meta = ownerFirst + " · " + t.SentAt.ToLocalTime().ToString("h:mm tt") });
        }
        rows = [.. rows.OrderBy(r => r.At)];
        foreach (var p in pending.OrderBy(p => p.CreatedAtUtc))
        {
            rows.Add(new ThreadRow
            {
                Key = "pending:" + p.Id, Kind = ThreadKind.Text, Mine = true, Pending = true, At = p.CreatedAtUtc, Text = p.Body,
                Meta = "You · " + (offline ? p.CreatedAtUtc.ToLocalTime().ToString("h:mm tt") + " · sends when online" : "Sending…"),
                Quote = ReplyQuotes.Find(quotes, p.Body, p.CreatedAtUtc),
            });
        }

        // Date chips, and the avatar on the last bubble of each run.
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
            var sameRun = next is not null && next.At.ToLocalTime().Date == rowDay && next.Mine == row.Mine && next.Kind != ThreadKind.Sys
                && row.Kind != ThreadKind.Sys;
            result.Add(row with { HasAvatar = row.Kind != ThreadKind.Sys && !sameRun });
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

/// <summary>The board's preset avatars: up to three shapes on a coloured ground (40 × 40).</summary>
public static class Avatars
{
    public sealed record Preset(string Key, string Label, string Bg, (string Path, string Fill)[] Shapes);

    public static readonly Preset[] Presets =
    [
        new("sun", "Sunrise", "#FDEBDC", [("M20 8a8 8 0 1 1 0 16a8 8 0 1 1 0-16z", "#F26A0A"), ("M0 29c7-5 14-6 20-2s13 4 20-1v14H0z", "#8A3A04")]),
        new("peaks", "Mountains", "#E6EEFB", [("M1 36 15 14l8 11 5-6 11 17z", "#1B4FB0"), ("M15 14l-4 6 4-2 4 3z", "#FFFFFF"), ("M30 6a3.5 3.5 0 1 1 0 7a3.5 3.5 0 1 1 0-7z", "#F26A0A")]),
        new("waves", "Waves", "#E3F4EA", [("M0 16c5-4 10-4 15 0s10 4 15 0 8-3 10-2v6c-2-1-5 0-10 2s-10 4-15 0S5 18 0 22z", "#17663F"), ("M0 27c5-4 10-4 15 0s10 4 15 0 8-3 10-2v6c-2-1-5 0-10 2s-10 4-15 0S5 29 0 33z", "#5FB58A")]),
        new("leaf", "Leaf", "#FBF0D9", [("M9 31C9 17 19 9 32 8c0 13-8 23-23 23z", "#8A5300"), ("M9.6 31.4 8.6 30.4 25 14l1 1z", "#FBF0D9")]),
        new("shapes", "Shapes", "#141210", [("M0 40V18a22 22 0 0 1 22 22z", "#F26A0A"), ("M28 6a6 6 0 1 1 0 12a6 6 0 1 1 0-12z", "#F7F6F2"), ("M24 25h11v11H24z", "#5AB0F0")]),
        new("stripes", "Stripes", "#F4ECFB", [("M0 10 10 0h6L0 16zM0 24 24 0h6L0 30zM0 38 38 0h2v4L4 40H0zM12 40 40 12v6L18 40zM26 40l14-14v6l-8 8z", "#7A3FB0")]),
        new("figure", "Figure", "#FFF1E6", [("M8 40c0-9 5-15 12-15s12 6 12 15z", "#141210"), ("M20 9a6.5 6.5 0 1 1 0 13a6.5 6.5 0 1 1 0-13z", "#A84505")]),
        new("night", "Night", "#22313F", [("M23 8a12 12 0 1 0 10 19A10 10 0 0 1 23 8z", "#F2C04B"), ("M10 9l1.2 2.3 2.3 1.2-2.3 1.2L10 16l-1.2-2.3-2.3-1.2 2.3-1.2z", "#FFFFFF"), ("M31 31l.8 1.5 1.5.8-1.5.8-.8 1.5-.8-1.5-1.5-.8 1.5-.8z", "#FFFFFF")]),
    ];

    public static Preset? Find(string? avatar) =>
        avatar is { } a && a.StartsWith("preset:", StringComparison.Ordinal) ? Presets.FirstOrDefault(p => p.Key == a[7..]) : null;

    /// <summary>"Emily Carter" → "EC"; "ravi" → "R"; empty → "Z".</summary>
    public static string Initials(string? name)
    {
        var parts = (name ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length switch
        {
            0 => "Z",
            1 => parts[0][..1].ToUpperInvariant(),
            _ => (parts[0][..1] + parts[^1][..1]).ToUpperInvariant(),
        };
    }

    /// <summary>"Ananya Rao" → "Ananya".</summary>
    public static string First(string? name) =>
        (name ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries) is { Length: > 0 } p ? p[0] : "";
}

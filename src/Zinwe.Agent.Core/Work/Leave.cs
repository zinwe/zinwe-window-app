using System.Globalization;
using System.Text.Json;
using Zinwe.Agent.Core.Backend;

namespace Zinwe.Agent.Core.Work;

/// <summary>
/// v1.4.1 · one of the member's asks for a day off (api-v14 §3, <c>GET /api/devices/leave/</c>). <paramref name="Date"/> is
/// "yyyy-MM-dd"; <paramref name="Status"/> is "requested", "approved" or "declined". Every member but the id has a default,
/// so it crosses the pipe between an older and a newer tray or service.
/// </summary>
public sealed record LeaveAsk(
    long Id,
    string Date = "",
    bool Half = false,
    string Status = LeaveAsk.Requested,
    string Note = "",
    DateTimeOffset? CreatedAt = null,
    DateTimeOffset? DecidedAt = null)
{
    public const string Requested = "requested";
    public const string Approved = "approved";
    public const string Declined = "declined";

    [System.Text.Json.Serialization.JsonIgnore]
    public DateOnly? Day => DateOnly.TryParseExact(Date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
}

/// <summary>The words and rules of "Ask for a day off" (plain, short, friendly).</summary>
public static class LeaveDays
{
    public const string SentToast = "Sent. Your admin will answer here.";
    public const string PastDay = "Pick a day in the last 30 days or later.";

    /// <summary>How far back a day off can be asked for (a sick day told afterwards). The server takes the same 30 days.</summary>
    public const int DaysBack = 30;

    /// <summary>Waiting · Approved · Declined.</summary>
    public static string StatusLabel(string? status) => (status ?? "").ToLowerInvariant() switch
    {
        LeaveAsk.Approved => "Approved",
        LeaveAsk.Declined => "Declined",
        _ => "Waiting",
    };

    /// <summary>Null when the day can be asked for; else what to tell the member.</summary>
    public static string? Check(DateOnly? day, DateOnly today) => day is not { } d ? "Pick a day." : d < today.AddDays(-DaysBack) ? PastDay : null;

    /// <summary>The body of <c>POST leave/</c> for a day.</summary>
    public static LeaveRequest Request(DateOnly day, bool half, string? note) =>
        new(day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), half, (note ?? "").Trim());

    /// <summary>"Fri, 13 Nov · Full day" for a row of the recent asks.</summary>
    public static string Label(LeaveAsk ask, DateOnly today)
    {
        var when = ask.Day is { } d
            ? d == today ? "Today" : d == today.AddDays(1) ? "Tomorrow" : d == today.AddDays(-1) ? "Yesterday" : d.ToString("ddd, d MMM", CultureInfo.InvariantCulture)
            : ask.Date;
        return when + " · " + (ask.Half ? "Half day" : "Full day");
    }

    /// <summary>The recent asks, the next days first and then the past ones, newest first.</summary>
    public static IReadOnlyList<LeaveAsk> Recent(IEnumerable<LeaveAsk> asks, DateOnly today, int max = 5)
    {
        var all = asks.ToList();
        var coming = all.Where(a => a.Day is not { } d || d >= today).OrderBy(a => a.Day ?? DateOnly.MaxValue);
        var past = all.Where(a => a.Day is { } d && d < today).OrderByDescending(a => a.Day);
        return [.. coming.Concat(past).Take(max)];
    }

    /// <summary>
    /// The heartbeat's note for today on Home: a holiday, approved leave, or not a working day. Null on a normal working day
    /// (or an older backend that sends none of it).
    /// </summary>
    public static string? TodayNote(WorkRules? rules)
    {
        if (rules is null)
        {
            return null;
        }
        if (rules.HolidayToday is { Length: > 0 } holiday)
        {
            return $"Today is a holiday: {holiday}. Enjoy the day.";
        }
        return (rules.LeaveToday ?? "").ToLowerInvariant() switch
        {
            "full" => "You have the day off today. Enjoy it.",
            "half" => "You have half the day off today.",
            _ => rules.WorkingToday == false ? "Today isn’t a working day for you." : null,
        };
    }
}

/// <summary>Reads <c>GET leave/</c> leniently (a list or a <c>{"results": […]}</c> page); odd items are skipped, never thrown.</summary>
public static class LeaveJson
{
    public static IReadOnlyList<LeaveAsk> Asks(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("results", out var r))
            {
                root = r;
            }
            return root.ValueKind == JsonValueKind.Array ? [.. root.EnumerateArray().Select(Ask).OfType<LeaveAsk>()] : [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public static LeaveAsk? Ask(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty("id", out var idEl))
        {
            return null;
        }
        long? id = idEl.ValueKind switch
        {
            JsonValueKind.Number when idEl.TryGetInt64(out var n) => n,
            JsonValueKind.String when long.TryParse(idEl.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) => n,
            _ => null,
        };
        if (id is null)
        {
            return null;
        }
        string? Text(string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        DateTimeOffset? Time(string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.TryGetDateTimeOffset(out var at) ? at : null;
        var half = e.TryGetProperty("half", out var h) && h.ValueKind == JsonValueKind.True;
        return new LeaveAsk(id.Value, Text("date") ?? "", half, (Text("status") ?? LeaveAsk.Requested).ToLowerInvariant(), Text("note") ?? "",
            Time("created_at"), Time("decided_at"));
    }
}

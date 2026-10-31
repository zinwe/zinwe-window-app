using System.Globalization;
using System.Text.Json;
using Zinwe.Agent.Core.Backend;
using Zinwe.Agent.Core.Messages;

namespace Zinwe.Agent.Core.Work;

/// <summary>
/// Every log in / break / back / log out the member made, read from the widget's own change log
/// (<c>%LOCALAPPDATA%\Zinwe\work-timing-log.jsonl</c>, one JSON line per button press with its exact time) and sent
/// on the heartbeat until the backend has them. The heartbeat alone only carries the CURRENT state once a minute,
/// which loses short changes (a 20-second break) and anything done while the PC was offline.
/// <para>
/// "Sent up to" is a timestamp kept in the agent's own data folder (<c>work-log-sent.txt</c>); it only moves after
/// a heartbeat succeeds, and the backend skips re-sent events, so a lost response never duplicates or drops one.
/// </para>
/// </summary>
public sealed class WorkTransitionLog(IWidgetFolder folder, string dataDirectory)
{
    private const string FileName = "work-timing-log.jsonl";
    private const string CursorFile = "work-log-sent.txt";
    private const int MaxPerHeartbeat = 200; // the backend's limit

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    private string CursorPath => Path.Combine(dataDirectory, CursorFile);

    /// <summary>
    /// Changes not yet confirmed by the backend, oldest first. With no marker yet (first run of this agent), only
    /// today's changes are sent: older ones were already reported the old way.
    /// </summary>
    public IReadOnlyList<WorkEvent> Pending(DateTimeOffset now)
    {
        var after = ReadCursor() ?? new DateTimeOffset(now.ToLocalTime().Date, now.ToLocalTime().Offset);
        return folder.Use<IReadOnlyList<WorkEvent>>(dir => ReadAfter(Path.Combine(dir, FileName), after), []);
    }

    /// <summary>The backend has these: move the marker past the newest one.</summary>
    public void Acknowledge(IReadOnlyList<WorkEvent> sent)
    {
        if (sent.Count == 0)
        {
            return;
        }
        var newest = sent.Max(e => e.At);
        try
        {
            Directory.CreateDirectory(dataDirectory);
            File.WriteAllText(CursorPath, newest.ToString("o", CultureInfo.InvariantCulture));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Next heartbeat re-sends them; the backend skips duplicates.
        }
    }

    /// <summary>Cheap change check (the log's size): the agent heartbeats right away when it grows.</summary>
    public long Fingerprint() => folder.Use(dir =>
    {
        var file = new FileInfo(Path.Combine(dir, FileName));
        return file.Exists ? file.Length : 0L;
    }, -1L);

    private DateTimeOffset? ReadCursor()
    {
        try
        {
            return File.Exists(CursorPath) &&
                   DateTimeOffset.TryParse(File.ReadAllText(CursorPath).Trim(), CultureInfo.InvariantCulture,
                       DateTimeStyles.RoundtripKind, out var at)
                ? at
                : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static IReadOnlyList<WorkEvent> ReadAfter(string path, DateTimeOffset after)
    {
        if (!File.Exists(path))
        {
            return [];
        }
        string[] lines;
        try
        {
            // The widget appends while we read: share the file.
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            lines = reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }

        var events = new List<WorkEvent>();
        foreach (var line in lines)
        {
            LogLine? entry;
            try
            {
                entry = JsonSerializer.Deserialize<LogLine>(line, Json);
            }
            catch (JsonException)
            {
                continue; // a half-written last line: picked up next time
            }
            if (entry is null || entry.At <= after || ToWire(entry) is not { } e)
            {
                continue;
            }
            events.Add(e);
        }
        return [.. events.OrderBy(e => e.At).TakeLast(MaxPerHeartbeat)];
    }

    // "carried" and "moved" lines are the backend's own news coming back: never reported as a change.
    private static WorkEvent? ToWire(LogLine l) => l.Action == "carried" ? null : l.State switch
    {
        "Working" => new WorkEvent(l.At, "working"),
        "OnBreak" => new WorkEvent(l.At, "on_break", l.BreakReason, l.BreakNote),
        "Ended" => new WorkEvent(l.At, "ended", l.EndReason, l.EndNote),
        _ => null,
    };

    /// <summary>One line of the widget's log (Zinwe.Agent.Tray WorkTimerStore.WorkLogEntry; extra fields ignored).</summary>
    private sealed class LogLine
    {
        public DateTimeOffset At { get; set; }
        public string Action { get; set; } = "";
        public string State { get; set; } = "";
        public string? BreakReason { get; set; }
        public string? BreakNote { get; set; }
        public string? EndReason { get; set; }
        public string? EndNote { get; set; }
    }
}

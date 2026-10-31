using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Zinwe.Agent.Tray.Widget;

/// <summary>
/// Loads and saves the per-user <see cref="WorkTimerState"/> and appends a plain local audit log of
/// start/break/resume/end actions. All I/O is best-effort: a failed read yields a fresh day, a failed
/// write is swallowed so the widget never crashes on a locked or roaming file.
///
/// LATER MILESTONE: the uploader/service can read <see cref="WidgetPaths.WorkTimingLog"/> to ship the
/// day's timing to the server. Nothing here talks to the network.
/// </summary>
public sealed class WorkTimerStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    // Log lines are one compact JSON object each (jsonl), so keep them single-line.
    private static readonly JsonSerializerOptions LogJson = new()
    {
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Loads today's state, rolling a stale day over to a fresh one.</summary>
    public WorkTimerState Load(DateTimeOffset now)
    {
        try
        {
            if (File.Exists(WidgetPaths.WorkTiming))
            {
                var text = File.ReadAllText(WidgetPaths.WorkTiming);
                var state = JsonSerializer.Deserialize<WorkTimerState>(text, Json);
                if (state is not null)
                {
                    state.RollOverIfNewDay(now);
                    return state;
                }
            }
        }
        catch
        {
            // Corrupt or unreadable: start clean rather than block the employee.
        }
        return WorkTimerState.NewForToday(now);
    }

    public void Save(WorkTimerState state)
    {
        try
        {
            WidgetPaths.EnsureDirectory();
            var tmp = WidgetPaths.WorkTiming + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(state, Json));
            File.Move(tmp, WidgetPaths.WorkTiming, overwrite: true);
        }
        catch
        {
            // Best effort; the in-memory state is still correct for this session.
        }
    }

    /// <summary>
    /// Appends one action to the local log. <paramref name="action"/> is start|break|resume|end, or moved|carried when
    /// the day went to or came from <paramref name="host"/>.
    /// </summary>
    public void AppendLog(string action, WorkTimerState state, DateTimeOffset now, string? host = null)
    {
        try
        {
            WidgetPaths.EnsureDirectory();
            var entry = new WorkLogEntry(
                now,
                action,
                state.State.ToString(),
                (long)state.LiveWorked(now).TotalSeconds,
                (long)state.LiveBreak(now).TotalSeconds,
                state.BreakReason,
                state.BreakNote,
                state.EndReason,
                state.EndNote,
                host);
            File.AppendAllText(WidgetPaths.WorkTimingLog,
                JsonSerializer.Serialize(entry, LogJson) + Environment.NewLine, Encoding.UTF8);
        }
        catch
        {
            // Logging must never break the button press.
        }
    }

    private sealed record WorkLogEntry(
        DateTimeOffset At,
        string Action,
        string State,
        long WorkedSeconds,
        long BreakSeconds,
        string? BreakReason,
        string? BreakNote = null,
        string? EndReason = null,
        string? EndNote = null,
        string? Host = null);
}

using System.Text.Json;
using System.Text.Json.Serialization;
using Zinwe.Agent.Core.Messages;

namespace Zinwe.Agent.Core.Work;

/// <summary>The member's current work status, as the owner's dashboard shows it.</summary>
/// <param name="ChangedAt">When the current state began (the owner's Activity timeline uses it).</param>
/// <param name="BreakReason">Why they're on a break, when they are and picked one.</param>
/// <param name="BreakNote">The optional message they added to the break.</param>
/// <param name="EndReason">Why they logged out, once they have (picked in the widget).</param>
/// <param name="EndNote">The message they added when logging out.</param>
public sealed record WorkStatus(
    string State, long WorkedSeconds, long BreakSeconds,
    DateTimeOffset? ChangedAt = null, string? BreakReason = null, string? BreakNote = null,
    string? EndReason = null, string? EndNote = null)
{
    public static readonly WorkStatus NotStarted = new("not_started", 0, 0);
}

/// <summary>
/// Reads the widget's per-user work-timing.json so the service can report work status on each heartbeat.
/// The widget (Zinwe.Agent.Tray) owns the file; this only reads it, from the signed-in member's %LOCALAPPDATA%\Zinwe
/// via <see cref="IWidgetFolder"/> (nobody signed in = not started).
/// </summary>
public sealed class WorkStatusReader
{
    private const string FileName = "work-timing.json";

    private readonly IWidgetFolder _folder;

    public WorkStatusReader(string? directory = null) : this(new LocalWidgetFolder(directory))
    {
    }

    public WorkStatusReader(IWidgetFolder folder) => _folder = folder;

    /// <summary>Current status, or <see cref="WorkStatus.NotStarted"/> when there's no (or a stale) file or nobody is signed in.</summary>
    public WorkStatus Read(DateTimeOffset now) =>
        _folder.Use(dir => ReadFrom(Path.Combine(dir, FileName), now), WorkStatus.NotStarted);

    private static WorkStatus ReadFrom(string path, DateTimeOffset now)
    {
        try
        {
            if (!File.Exists(path))
            {
                return WorkStatus.NotStarted;
            }
            var dto = JsonSerializer.Deserialize<TimingDto>(File.ReadAllText(path), Json);
            if (dto is null)
            {
                return WorkStatus.NotStarted;
            }
            // A stale day (the widget rolls over at midnight) reads as a fresh, not-started day.
            if (!string.Equals(dto.Date, now.ToLocalTime().ToString("yyyy-MM-dd"), StringComparison.Ordinal))
            {
                return WorkStatus.NotStarted;
            }
            // "Moved" (the day went to another PC) reads as not started: this PC has no day of its own running.
            var wire = dto.State switch
            {
                "Working" => "working",
                "OnBreak" => "on_break",
                "Ended" => "ended",
                _ => "not_started",
            };
            return new WorkStatus(
                wire, Math.Max(0, dto.WorkedSeconds), Math.Max(0, dto.BreakSeconds),
                dto.LastTransitionAtUtc,
                wire == "on_break" ? dto.BreakReason : null,
                wire == "on_break" ? dto.BreakNote : null,
                wire == "ended" ? dto.EndReason : null,
                wire == "ended" ? dto.EndNote : null);
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            return WorkStatus.NotStarted;
        }
    }

    // Mirrors the fields the widget writes (Zinwe.Agent.Tray.Widget.WorkTimerState); read case-insensitively.
    private sealed class TimingDto
    {
        public string Date { get; set; } = "";
        public string State { get; set; } = "";
        public long WorkedSeconds { get; set; }
        public long BreakSeconds { get; set; }
        public DateTimeOffset? LastTransitionAtUtc { get; set; }
        public string? BreakReason { get; set; }
        public string? BreakNote { get; set; }
        public string? EndReason { get; set; }
        public string? EndNote { get; set; }
    }

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };
}

using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Zinwe.Agent.Tray.Widget;

/// <summary>
/// SEAMS (tray v2): data the v2 screens need that the named pipe and the server don't provide yet. Each seam is one
/// interface with a local implementation here; the real one (service + pipe, then the API) replaces it later without
/// touching a view. See docs 08-windows-agent.md, "Tray v2 seams".
///
/// Sample data (the board's tasks) is on in Debug builds and when <c>ZINWE_TRAY_SAMPLES=1</c>; Release builds start
/// empty, so an installed agent never shows made-up tasks.
/// </summary>
public static class TraySeams
{
    public const string SamplesVariable = "ZINWE_TRAY_SAMPLES";

    public static bool UseSamples { get; } = Environment.GetEnvironmentVariable(SamplesVariable) switch
    {
        "1" or "true" => true,
        "0" or "false" => false,
#if DEBUG
        _ => true,
#else
        _ => false,
#endif
    };
}

/// <summary>
/// SEAM 1 · Tasks. The owner's tasks for this member and the member's own to-dos, and every change the member makes
/// (start, done, ask, can't, notes, steps). Today: a local file. Later: the service syncs them with the server
/// (needs a task API on /api/devices/ and a pipe request), and <see cref="Changed"/> fires when the owner sends one.
/// </summary>
public interface ITaskSource
{
    /// <summary>True while the list is the board's sample data, not the member's real tasks.</summary>
    bool IsSample { get; }

    IReadOnlyList<MemberTask> Load();

    /// <summary>Adds or replaces the task with the same id.</summary>
    void Save(MemberTask task);

    void Remove(int id);

    /// <summary>Puts a removed task back at its old place (Undo on Delete).</summary>
    void Insert(int index, MemberTask task);

    int NextId();

    event Action? Changed;
}

/// <summary>Tasks kept in a JSON file in the member's widget folder (tasks.json, or tasks-sample.json with samples).</summary>
public sealed class LocalTaskSource : ITaskSource
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _path;
    private List<MemberTask> _tasks;

    public LocalTaskSource(bool samples, string? directory = null, Func<DateTimeOffset>? clock = null)
    {
        IsSample = samples;
        _path = Path.Combine(directory ?? WidgetPaths.Directory, samples ? "tasks-sample.json" : "tasks.json");
        _tasks = Read() ?? (samples ? SampleTasks.Make((clock ?? (() => DateTimeOffset.Now))()) : []);
        if (samples)
        {
            Write();
        }
    }

    public bool IsSample { get; }

    public event Action? Changed;

    public IReadOnlyList<MemberTask> Load() => [.. _tasks];

    public void Save(MemberTask task)
    {
        var i = _tasks.FindIndex(t => t.Id == task.Id);
        if (i >= 0)
        {
            _tasks[i] = task;
        }
        else
        {
            _tasks.Add(task);
        }
        Write();
    }

    public void Remove(int id)
    {
        if (_tasks.RemoveAll(t => t.Id == id) > 0)
        {
            Write();
        }
    }

    public void Insert(int index, MemberTask task)
    {
        _tasks.RemoveAll(t => t.Id == task.Id);
        _tasks.Insert(Math.Clamp(index, 0, _tasks.Count), task);
        Write();
    }

    public int NextId() => _tasks.Count == 0 ? 1 : _tasks.Max(t => t.Id) + 1;

    /// <summary>Re-reads the file (another copy changed it, or the real sync wrote it).</summary>
    public void Reload()
    {
        if (Read() is { } list)
        {
            _tasks = list;
            Changed?.Invoke();
        }
    }

    private List<MemberTask>? Read()
    {
        try
        {
            return File.Exists(_path) ? JsonSerializer.Deserialize<List<MemberTask>>(File.ReadAllText(_path), Json) : null;
        }
        catch
        {
            return null;
        }
    }

    private void Write()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_tasks, Json));
            File.Move(tmp, _path, overwrite: true);
        }
        catch
        {
            // Best effort: the in-memory list is still right for this session.
        }
    }
}

/// <summary>The board's sample tasks, dated around <c>now</c> so due labels and reminders behave.</summary>
public static class SampleTasks
{
    public static List<MemberTask> Make(DateTimeOffset now)
    {
        var today = now.Date;
        var fourPm = new DateTimeOffset(today.AddHours(16), now.Offset);
        var toFriday = ((int)DayOfWeek.Friday - (int)today.DayOfWeek + 7) % 7;
        var friday = today.AddDays(toFriday == 0 ? 7 : toFriday);
        return
        [
            new MemberTask
            {
                Id = 1, Title = "Write the sprint summary for Brightway", Details = "Use this sprint’s ticket notes. Keep it to one page.",
                Due = friday.ToString("ddd d MMM"), DueKey = DueKey.Later, DueDate = DateOnly.FromDateTime(friday),
                Status = MemberTaskStatus.Progress, NeedsOk = true, SentAt = now.AddDays(-1),
                Steps = [new("Collect ticket notes", true), new("Draft summary", false), new("Share with Arjun", false), new("Send to client", false)],
            },
            new MemberTask
            {
                Id = 2, Title = "Book the meeting room for the Monday demo", Details = "The big room, 10 to 11 AM, with the screen.",
                Due = "Today, 4:00 PM", DueKey = DueKey.Today, DueAt = fourPm, High = true, Status = MemberTaskStatus.New, IsNew = true,
                SentAt = now.AddMinutes(-20),
            },
            new MemberTask
            {
                Id = 3, Title = "Confirm the Austin trip dates with Karan", Due = "Tomorrow", DueKey = DueKey.Tomorrow,
                Status = MemberTaskStatus.New, Confirm = true, SentAt = now.AddDays(-1).AddMinutes(-2),
            },
        ];
    }
}

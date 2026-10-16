using System.Text.Json;
using Zinwe.Agent.Core.Messages;

namespace Zinwe.Agent.Core.Work;

/// <summary>The member's consent to monitoring on this PC, as given on the widget's consent screen.</summary>
/// <param name="Version">The consent notice version they agreed to (<see cref="ConsentReader.CurrentVersion"/>).</param>
/// <param name="AcceptedAt">When they agreed.</param>
public sealed record ConsentRecord(string Version, DateTimeOffset AcceptedAt);

/// <summary>
/// Reads the widget's per-user consent.json so the service can report the member's consent on each
/// heartbeat (the owner sees it on Team; the timestamped record is what India's DPDP Act asks for) and
/// pause recording until it's given (<see cref="ConsentGate"/>). The widget (Zinwe.Agent.Tray) owns the
/// file; this only reads it, from the signed-in member's %LOCALAPPDATA%\Zinwe via <see cref="IWidgetFolder"/>
/// (same rules as <see cref="WorkStatusReader"/>; nobody signed in = no consent).
/// </summary>
public sealed class ConsentReader
{
    /// <summary>
    /// The version of the widget's consent notice. The widget writes it when the member agrees; a new version
    /// asks everyone again (and recording pauses until they agree). Bump it only when the notice changes.
    /// </summary>
    // 2026-10-15: the notice no longer promises a 90-day retention period (not configurable yet) and says only
    // what the company's plan includes is recorded. A correction, so everyone is asked once more.
    public const string CurrentVersion = "2026-10-15";

    private const string FileName = "consent.json";

    private readonly IWidgetFolder _folder;

    public ConsentReader(string? directory = null) : this(new LocalWidgetFolder(directory))
    {
    }

    public ConsentReader(IWidgetFolder folder) => _folder = folder;

    /// <summary>The recorded consent, or null when none was given (or the file is unreadable, or nobody is signed in).</summary>
    public ConsentRecord? Read() => _folder.Use(dir => ReadFrom(Path.Combine(dir, FileName)), null);

    private static ConsentRecord? ReadFrom(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }
            var dto = JsonSerializer.Deserialize<Dto>(File.ReadAllText(path), Json);
            if (dto is null || string.IsNullOrWhiteSpace(dto.Version) || dto.AcceptedAt is not { } at)
            {
                return null;
            }
            return new ConsentRecord(dto.Version.Trim(), at);
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private sealed class Dto
    {
        public string Version { get; set; } = "";
        public DateTimeOffset? AcceptedAt { get; set; }
    }

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
}

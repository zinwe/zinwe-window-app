using System.Text.Json;
using Zinwe.Agent.Core.Messages;

namespace Zinwe.Agent.Core.Work;

/// <summary>The member's answer on the widget's consent screen.</summary>
public enum ConsentChoice
{
    /// <summary>"I agree": recording may run (for the notice version they agreed to).</summary>
    Given,

    /// <summary>"I don't agree" (or withdrawn later): nothing about the member is recorded on this PC.</summary>
    Declined,
}

/// <summary>The member's answer to the monitoring notice on this PC, as given on the widget's consent screen.</summary>
/// <param name="Version">The consent notice version they answered (<see cref="ConsentNotice"/>).</param>
/// <param name="AcceptedAt">When they answered (agreed, or declined).</param>
/// <param name="Choice">Agreed or declined. Files written before "I don't agree" existed are agreements.</param>
public sealed record ConsentRecord(string Version, DateTimeOffset AcceptedAt, ConsentChoice Choice = ConsentChoice.Given)
{
    public bool IsGiven => Choice == ConsentChoice.Given;

    public bool IsDeclined => Choice == ConsentChoice.Declined;
}

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
    /// The version of the full consent notice (everything listed). The real notice version depends on what the
    /// owner switched on (<see cref="ConsentNotice.VersionFor"/>); this one covers any policy with the current wording.
    /// </summary>
    public const string CurrentVersion = ConsentNotice.FullVersion;

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
            if (dto is null || string.IsNullOrWhiteSpace(dto.Version) || (dto.DecidedAt ?? dto.AcceptedAt) is not { } at)
            {
                return null;
            }
            var choice = string.Equals(dto.State?.Trim(), "declined", StringComparison.OrdinalIgnoreCase)
                ? ConsentChoice.Declined
                : ConsentChoice.Given;
            return new ConsentRecord(dto.Version.Trim(), at, choice);
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
        // "given" | "declined"; missing (files from before "I don't agree") = given.
        public string? State { get; set; }
        public DateTimeOffset? DecidedAt { get; set; }
    }

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
}

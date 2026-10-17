using System.Text.Json;
using System.Text.Json.Serialization;

namespace Zinwe.Agent.Core.Identity;

/// <summary>The owner removed this PC from Zinwe: who, and when (shown to the member until they connect again).</summary>
public sealed record RemovalRecord(string? CompanyName, DateTimeOffset RemovedAt);

/// <summary>
/// DataDirectory\removed.json: remembers that the owner removed this PC, so after a restart the widget keeps
/// saying "{Company} removed this PC from Zinwe" instead of "not set up yet". Deleted on the next enrolment.
/// Plain JSON (no secrets). Never throws.
/// </summary>
public sealed class RemovalMarker(string dataDirectory)
{
    public string FilePath { get; } = Path.Combine(dataDirectory, "removed.json");

    public RemovalRecord? Load()
    {
        try
        {
            return File.Exists(FilePath)
                ? JsonSerializer.Deserialize(File.ReadAllBytes(FilePath), RemovalJsonContext.Default.RemovalRecord)
                : null;
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void Save(RemovalRecord record)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var temp = FilePath + ".tmp";
            File.WriteAllBytes(temp, JsonSerializer.SerializeToUtf8Bytes(record, RemovalJsonContext.Default.RemovalRecord));
            File.Move(temp, FilePath, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Best effort: without it, a restart just shows "not connected yet".
        }
    }

    public void Delete()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                File.Delete(FilePath);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // best effort
        }
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(RemovalRecord))]
internal sealed partial class RemovalJsonContext : JsonSerializerContext;

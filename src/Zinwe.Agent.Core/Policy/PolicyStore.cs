using System.Text.Json;
using System.Text.Json.Serialization;

namespace Zinwe.Agent.Core.Policy;

/// <summary>
/// The last policy received from the backend, kept in DataDirectory\policy.json (plain JSON: it holds no secrets).
/// The service applies it at startup before any network call, so losing the backend never turns protection off.
/// </summary>
public sealed class PolicyStore(string dataDirectory)
{
    public string FilePath { get; } = Path.Combine(dataDirectory, "policy.json");

    /// <summary>Returns null when nothing is cached yet, or when the file is unreadable or malformed.</summary>
    public AgentPolicy? Load()
    {
        if (!File.Exists(FilePath))
        {
            return null;
        }
        try
        {
            var policy = JsonSerializer.Deserialize(File.ReadAllBytes(FilePath), PolicyJsonContext.Default.AgentPolicy);
            return policy is { IsWellFormed: true } ? policy : null;
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void Save(AgentPolicy policy)
    {
        if (!policy.IsWellFormed)
        {
            throw new ArgumentException("Refusing to cache a malformed policy.", nameof(policy));
        }
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var temp = FilePath + ".tmp";
        File.WriteAllBytes(temp, JsonSerializer.SerializeToUtf8Bytes(policy, PolicyJsonContext.Default.AgentPolicy));
        File.Move(temp, FilePath, overwrite: true); // atomic replace: a crash mid-write never leaves half a policy
    }

    /// <summary>Forget the cached policy (the PC was removed: nothing may be re-applied at the next start).</summary>
    public void Delete()
    {
        if (File.Exists(FilePath))
        {
            File.Delete(FilePath);
        }
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower, WriteIndented = true)]
[JsonSerializable(typeof(AgentPolicy))]
internal sealed partial class PolicyJsonContext : JsonSerializerContext;

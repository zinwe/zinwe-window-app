using System.Text.Json;
using System.Text.Json.Serialization;

namespace Zinwe.Agent.Core.Features.Web;

/// <summary>
/// The sites this PC blocks, as resolved by the backend (GET /api/devices/policy/blocklist/): the owner's lists minus
/// what they allowed, by list ("adult", "gambling", ... and "custom"). <see cref="Version"/> names the set; the agent
/// fetches again only when the policy or a heartbeat names another one. Empty version = this PC blocks nothing.
/// </summary>
public sealed record WebBlockSet(string Version, IReadOnlyDictionary<string, IReadOnlyList<string>> Sites)
{
    /// <summary>The key of the owner's own sites. Those come with the policy, so the policy's copy always wins.</summary>
    public const string CustomKey = "custom";

    /// <summary>The serializer fills missing members with null, so check the shape before trusting a file or a reply.</summary>
    [JsonIgnore] // computed guard, not part of the wire contract or the cache file
    public bool IsWellFormed => !string.IsNullOrEmpty(Version) && Sites is not null;
}

/// <summary>
/// The last set received, kept in DataDirectory\web-block-set.json so blocking keeps working offline and across
/// restarts. It holds no secrets, but it does hold the full names of the adult list. Thread-safe: <see cref="Current"/>
/// is read when the feature scope is rebuilt, on another thread than the one saving.
/// </summary>
public sealed class WebBlockSetStore(string dataDirectory)
{
    private WebBlockSet? _current;

    public string FilePath { get; } = Path.Combine(dataDirectory, "web-block-set.json");

    /// <summary>The set in force, or null before the first fetch on a new install.</summary>
    public WebBlockSet? Current => Volatile.Read(ref _current);

    /// <summary>Read the file into <see cref="Current"/>. An unreadable or malformed file counts as no set.</summary>
    public WebBlockSet? Load()
    {
        WebBlockSet? loaded = null;
        if (File.Exists(FilePath))
        {
            try
            {
                var set = JsonSerializer.Deserialize(File.ReadAllBytes(FilePath), WebBlockSetJsonContext.Default.WebBlockSet);
                loaded = set is { IsWellFormed: true } ? set : null;
            }
            catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
            {
                loaded = null;
            }
        }
        Volatile.Write(ref _current, loaded);
        return loaded;
    }

    /// <summary>Put <paramref name="set"/> in force and cache it. It's in force even when the file can't be written (that throws).</summary>
    public void Save(WebBlockSet set)
    {
        if (!set.IsWellFormed)
        {
            throw new ArgumentException("Refusing to cache a malformed block set.", nameof(set));
        }
        Volatile.Write(ref _current, set);
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var temp = FilePath + ".tmp";
        File.WriteAllBytes(temp, JsonSerializer.SerializeToUtf8Bytes(set, WebBlockSetJsonContext.Default.WebBlockSet));
        File.Move(temp, FilePath, overwrite: true); // atomic replace: a crash mid-write never leaves half a set
    }

    /// <summary>Forget the set (the PC was removed).</summary>
    public void Delete()
    {
        Volatile.Write(ref _current, null);
        if (File.Exists(FilePath))
        {
            File.Delete(FilePath);
        }
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(WebBlockSet))]
internal sealed partial class WebBlockSetJsonContext : JsonSerializerContext;

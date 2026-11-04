using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Zinwe.Agent.Core.Zingles;

/// <summary>
/// A Zingle: Zinwe's scheduled logo for a special day (Diwali, ...), from <c>GET api/auth/zingle/?for=app</c> (public,
/// no device token). The app shows it in place of the wordmark in the window's title bar while it is live.
/// </summary>
/// <param name="Id">The Zingle's id on the server.</param>
/// <param name="AnimatedUrl">The animated file (GIF, or WebP: the app then shows the still); a path on the backend or an
/// https URL. Null when the server has only a still.</param>
/// <param name="StillUrl">The still PNG (transparent). The <c>?v=</c> on both URLs changes when the file does.</param>
/// <param name="Alt">What a screen reader says for it.</param>
public sealed record Zingle(long Id, string? AnimatedUrl, string StillUrl, string Alt, string? Places = null,
    DateTimeOffset? UpdatedAt = null)
{
    /// <summary>The name a screen reader says when the server sends no alt text.</summary>
    public const string DefaultAlt = "Zinwe";

    /// <summary>The cache file name of the still without its extension (<c>zingle-&lt;id&gt;-&lt;v&gt;-still</c>); the
    /// extension follows the bytes, and only a ".png" is used.</summary>
    public string StillFileStem => ZingleFiles.FileName(Id, StillUrl, "still", "");

    /// <summary>The cache file name of the animated file without its extension (it follows the bytes).</summary>
    public string? AnimatedFileStem => AnimatedUrl is null ? null : ZingleFiles.FileName(Id, AnimatedUrl, "animated", "");
}

/// <summary>Reading the endpoint's answer: JSON <c>null</c> (no Zingle today) or one object.</summary>
public static class ZingleJson
{
    /// <summary>
    /// The endpoint's body → the live Zingle, or null for <c>null</c> / one not meant for the app (<c>"app": false</c>).
    /// Throws <see cref="JsonException"/> on anything else (a proxy page, a broken object): the caller keeps what it has.
    /// </summary>
    public static Zingle? Parse(string body)
    {
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        if (root.ValueKind == JsonValueKind.Null)
        {
            return null;
        }
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("A Zingle must be an object or null.");
        }
        if (!root.TryGetProperty("id", out var idValue) || !idValue.TryGetInt64(out var id) || id <= 0)
        {
            throw new JsonException("A Zingle needs a positive id.");
        }
        if (root.TryGetProperty("app", out var app) && app.ValueKind == JsonValueKind.False)
        {
            return null; // live on the website only
        }
        var still = Text(root, "still_url");
        if (!IsUsableUrl(still))
        {
            throw new JsonException("A Zingle needs a still_url.");
        }
        var animated = Text(root, "animated_url");
        var alt = Text(root, "alt")?.Trim();
        DateTimeOffset? updated = Text(root, "updated_at") is { } at && DateTimeOffset.TryParse(at, out var parsed) ? parsed : null;
        return new Zingle(id, IsUsableUrl(animated) ? animated : null, still!,
            string.IsNullOrEmpty(alt) ? Zingle.DefaultAlt : alt[..Math.Min(alt.Length, 200)], Text(root, "places"), updated);
    }

    private static string? Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <summary>A path on the backend ("/api/...") or an absolute https (or loopback, for dev) URL.</summary>
    internal static bool IsUsableUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }
        if (url.StartsWith('/'))
        {
            return !url.StartsWith("//", StringComparison.Ordinal);
        }
        return Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.IsLoopback);
    }
}

/// <summary>Cache naming and file types: one file per Zingle id + <c>?v=</c>, so a changed file is a new name.</summary>
public static class ZingleFiles
{
    /// <summary>Every cached or widget-folder Zingle file starts with this (old ones are deleted by it).</summary>
    public const string Prefix = "zingle-";

    /// <summary>The biggest Zingle file the agent downloads (the samples are 35-150 KB).</summary>
    public const int MaxBytes = 2 * 1024 * 1024;

    /// <summary><c>zingle-&lt;id&gt;-&lt;v&gt;-&lt;role&gt;&lt;ext&gt;</c>; v is the URL's <c>?v=</c> (safe characters only), else a hash of the URL.</summary>
    public static string FileName(long id, string url, string role, string extension) =>
        $"{Prefix}{id}-{Version(url)}-{role}{extension}";

    /// <summary>The <c>v</c> query value, kept to letters, digits, '-' and '_' (≤ 32); a short URL hash without one.</summary>
    public static string Version(string url)
    {
        var query = url.IndexOf('?') is var q and >= 0 ? url[(q + 1)..] : "";
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            if (pair.StartsWith("v=", StringComparison.Ordinal))
            {
                var v = Uri.UnescapeDataString(pair[2..]);
                if (v.Length is > 0 and <= 32 && v.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
                {
                    return v;
                }
            }
        }
        return "h" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(url)))[..12];
    }

    /// <summary>The file's extension from its first bytes: ".png", ".gif" or ".webp"; null for anything else.</summary>
    public static string? Sniff(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 8 && bytes[..8].SequenceEqual((ReadOnlySpan<byte>)[0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]))
        {
            return ".png";
        }
        if (bytes.Length >= 6 && (bytes[..6].SequenceEqual("GIF89a"u8) || bytes[..6].SequenceEqual("GIF87a"u8)))
        {
            return ".gif";
        }
        if (bytes.Length >= 12 && bytes[..4].SequenceEqual("RIFF"u8) && bytes[8..12].SequenceEqual("WEBP"u8))
        {
            return ".webp";
        }
        return null;
    }

    /// <summary>
    /// Delete every Zingle file in <paramref name="folder"/> except <paramref name="keep"/> (file names). Missing folder:
    /// nothing to do. Best effort: a file the tray still has open goes on the next round.
    /// </summary>
    public static void DeleteExcept(string folder, IReadOnlyCollection<string> keep)
    {
        try
        {
            if (!Directory.Exists(folder))
            {
                return;
            }
            foreach (var file in Directory.EnumerateFiles(folder, Prefix + "*"))
            {
                if (!keep.Contains(Path.GetFileName(file), StringComparer.OrdinalIgnoreCase))
                {
                    try
                    {
                        File.Delete(file);
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                    {
                        // in use: next round
                    }
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // best effort
        }
    }
}

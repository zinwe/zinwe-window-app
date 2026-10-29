using System.IO;
using System.Text.Json;
using Zinwe.Agent.Core.Ipc;

namespace Zinwe.Agent.Tray.Widget;

/// <summary>One drawn person of the team avatar library: paths (SVG/WPF path data) with their fills, on <see cref="Bg"/>.</summary>
public sealed record LibraryAvatar(string Key, string Label, string Bg, IReadOnlyList<(string Data, string Fill)> Paths);

/// <summary>
/// FR-44 · the avatar library, shared with the dashboard: <c>Assets/avatars.json</c> is a byte-identical copy of
/// <c>zinwe-frontend/src/dashboard/team/avatars.json</c> (embedded resource; keys are stable forever, art may change).
/// Format: <c>{version, viewBox: "0 0 48 48", avatars: [{key, label, bg, paths: [[d, fill], …]}]}</c>; path data uses
/// only M L H V C Q S T A Z, valid in both SVG and WPF path markup. <c>bg</c> fills the whole box; clip it to a circle.
/// </summary>
public static class AvatarLibrary
{
    /// <summary>The embedded resource's name (see the tray csproj).</summary>
    public const string ResourceName = "Zinwe.Agent.Tray.avatars.json";

    private static readonly Lazy<(int Version, double Size, IReadOnlyList<LibraryAvatar> All)> Loaded = new(() => Parse(ReadJson()));

    public static int Version => Loaded.Value.Version;

    /// <summary>The art's square coordinate space (48 for "0 0 48 48").</summary>
    public static double ViewSize => Loaded.Value.Size;

    public static IReadOnlyList<LibraryAvatar> All => Loaded.Value.All;

    public static LibraryAvatar? Find(string? key) => key is null ? null : All.FirstOrDefault(a => a.Key == key);

    /// <summary>The embedded JSON exactly as shipped.</summary>
    public static string ReadJson()
    {
        using var stream = typeof(AvatarLibrary).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"The avatar library ({ResourceName}) is missing from the tray.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>Reads the library format. Entries without a key or paths are skipped; a broken file throws.</summary>
    public static (int Version, double Size, IReadOnlyList<LibraryAvatar> All) Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var version = root.TryGetProperty("version", out var v) && v.TryGetInt32(out var n) ? n : 0;
        var size = 48.0;
        if (root.TryGetProperty("viewBox", out var box) && box.GetString()?.Split(' ', StringSplitOptions.RemoveEmptyEntries) is [_, _, var w, _]
            && double.TryParse(w, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var width)
            && width > 0)
        {
            size = width;
        }
        var list = new List<LibraryAvatar>();
        foreach (var a in root.GetProperty("avatars").EnumerateArray())
        {
            var key = a.TryGetProperty("key", out var k) ? k.GetString() : null;
            if (!Zinwe.Agent.Core.Backend.MemberPictures.IsValidKey(key) || !a.TryGetProperty("paths", out var paths))
            {
                continue;
            }
            var shapes = new List<(string, string)>();
            foreach (var p in paths.EnumerateArray())
            {
                if (p.GetArrayLength() >= 2 && p[0].GetString() is { Length: > 0 } d && p[1].GetString() is { Length: > 0 } fill)
                {
                    shapes.Add((d, fill));
                }
            }
            if (shapes.Count == 0)
            {
                continue;
            }
            list.Add(new LibraryAvatar(key!, a.TryGetProperty("label", out var l) ? l.GetString() ?? key! : key!,
                a.TryGetProperty("bg", out var bg) ? bg.GetString() ?? "#F4ECFB" : "#F4ECFB", shapes));
        }
        return (version, size, list);
    }
}

/// <summary>
/// What a member avatar circle draws (FR-44): a library avatar, a photo (a file the service put in the widget folder, or
/// the tray's own fresh JPEG in memory), or nothing (initials / the official mark).
/// </summary>
public sealed record PictureView(string? Kind = null, string? Key = null, string? File = null, byte[]? Jpeg = null)
{
    public static readonly PictureView None = new();

    public bool IsAvatar => Kind == PictureChoice.Avatar && AvatarLibrary.Find(Key) is not null;

    public bool IsPhoto => Kind == PictureChoice.Photo && (Jpeg is not null || !string.IsNullOrEmpty(File));

    /// <summary>Anything but the initials fallback.</summary>
    public bool HasPicture => IsAvatar || IsPhoto;

    /// <summary>The service's view (null = an older service, or not running).</summary>
    public static PictureView From(PictureStatus? status) =>
        status is null ? None : new PictureView(status.Kind, status.Key, status.File);

    /// <summary>For screen readers and the sheet's caption.</summary>
    public string Describe() =>
        IsAvatar ? $"{AvatarLibrary.Find(Key)!.Label} avatar" : IsPhoto ? "Your photo" : "Your initials";

    /// <summary>A short stamp for render keys (changes whenever what's drawn changes).</summary>
    public string Stamp => $"{Kind}:{Key}:{File}:{Jpeg?.Length}";
}

/// <summary>
/// The member's own pick, drawn at once (FR-44): kept until a status shows the service took it (its picture
/// <see cref="PictureStatus.Version"/> moved), or dropped when the service said no.
/// </summary>
public sealed class PicturePick
{
    private PictureView? _picked;
    private int? _versionAtPick;

    public bool Waiting => _picked is not null;

    /// <summary>What to draw for the member: their fresh pick, else what the service holds.</summary>
    public PictureView Current(PictureStatus? status) => _picked ?? PictureView.From(status);

    public void Pick(PictureView view, PictureStatus? status)
    {
        _picked = view;
        _versionAtPick = status?.Version;
    }

    public void Drop() => _picked = null;

    /// <summary>A newer status: once its version moved, the service's view (which now has the pick) takes over.</summary>
    public void Observe(PictureStatus? status)
    {
        if (_picked is not null && status is not null && status.Version != _versionAtPick)
        {
            _picked = null;
        }
    }
}

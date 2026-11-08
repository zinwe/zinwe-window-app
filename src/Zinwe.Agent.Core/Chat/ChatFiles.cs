using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Zinwe.Agent.Core.Chat;

// v1.5 · file sharing in chat, the "flavour" version: send and forget. The sender uploads the file once; everyone in that
// chat can download it for 7 days, then it is deleted. 25 MB per file. The service does every backend call (upload, line
// post with file_id, download, image preview); the tray only copies the picked file into the widget folder and queues an
// outbox item of kind chat_file, and asks the service for downloads and previews over the pipe ("files").

/// <summary>
/// A file on a chat line (the backend's FileJSON, api-v15 §2e): <c>{id, name, size, mime, kind, caption, risky, expires_at,
/// expired, url}</c>. Read leniently (<see cref="ChatFileConverter"/>): an odd shape is "no file", never an error, so one bad
/// line can't hide the chat. The same converter writes it for the pipe and the widget's notifications file.
/// </summary>
/// <param name="Kind">"image" (jpg, png, gif or webp by its bytes: it gets a preview) or "file".</param>
/// <param name="Caption">What the sender typed with the file (new clients show it and ignore the line's body).</param>
/// <param name="Url">The device route to download it; null once it expired or was deleted.</param>
[JsonConverter(typeof(ChatFileConverter))]
public sealed record ChatFile(
    string Id,
    string Name = "",
    long Size = 0,
    string? Mime = null,
    string Kind = ChatFile.FileKind,
    string? Caption = null,
    bool Risky = false,
    DateTimeOffset? ExpiresAt = null,
    bool Expired = false,
    string? Url = null)
{
    public const string ImageKind = "image";
    public const string FileKind = "file";

    [JsonIgnore]
    public bool IsImage => string.Equals(Kind, ImageKind, StringComparison.OrdinalIgnoreCase);

    /// <summary>A program file: the server's word, or its extension here (both are checked).</summary>
    [JsonIgnore]
    public bool IsRisky => Risky || ChatFiles.IsRisky(Name);

    /// <summary>Still downloadable at <paramref name="now"/>: not expired, a url, and not past its keep date.</summary>
    public bool AvailableAt(DateTimeOffset now) => !Expired && Url is not null && (ExpiresAt is not { } e || e > now);
}

/// <summary>
/// <c>GET /api/devices/chat/files/</c>: <c>{"allowed": true, "max_bytes": 26214400, "keep_days": 7}</c>. Allowed = the
/// attach button shows. A backend without the route (404/405) is "files off".
/// </summary>
public sealed record ChatFilesInfo(bool Allowed = false, long MaxBytes = ChatFiles.MaxBytes, int KeepDays = ChatFiles.KeepDays);

/// <summary>
/// The tray asks the service for a file (pipe "files"): <see cref="Download"/> it into Downloads\Zinwe, or fetch its
/// <see cref="Preview"/> (an image up to 5 MB) into the widget folder. <see cref="Name"/> is the name the line shows (used
/// when the server's reply names none).
/// </summary>
public sealed record FileAsk(string Action, string Id, string? Name = null, long Size = 0)
{
    public const string Download = "download";
    public const string Preview = "preview";
}

/// <summary>
/// Where one download or preview is: <see cref="Working"/>, <see cref="Done"/> (<see cref="Path"/> = the file), <see cref="Gone"/>
/// (expired or not allowed: "This file is no longer available…") or <see cref="Failed"/> (<see cref="Error"/> says why).
/// </summary>
public sealed record FileState(string Id, string State, string? Path = null, string? Error = null)
{
    public const string Working = "working";
    public const string Done = "done";
    public const string Gone = "gone";
    public const string Failed = "failed";
}

/// <summary>
/// The service's reply to a "files" pipe request (v1.5). <see cref="Info"/> null = not known yet, or a backend without file
/// sharing: no attach button. A 1.4 service answers "not supported", which reads as <see cref="Supported"/> = false.
/// </summary>
public sealed record ChatFilesReply(
    bool Supported = false,
    ChatFilesInfo? Info = null,
    IReadOnlyList<FileState>? Downloads = null,
    IReadOnlyList<FileState>? Previews = null,
    int Version = 0);

/// <summary>The v1.5 rules and words for files in chat, shared by the service and the tray.</summary>
public static class ChatFiles
{
    /// <summary>FILE_MAX_BYTES: 25 MB on every plan.</summary>
    public const long MaxBytes = 25L * 1024 * 1024;

    /// <summary>FILE_KEEP_DAYS: counted from when the line was posted.</summary>
    public const int KeepDays = 7;

    /// <summary>Images up to this size are fetched for a thumbnail on their own.</summary>
    public const long PreviewMaxBytes = 5L * 1024 * 1024;

    /// <summary>An upload not attached to a line within 1 hour is deleted by the server: re-upload a bit before that.</summary>
    public static readonly TimeSpan UploadFresh = TimeSpan.FromMinutes(50);

    /// <summary>The tray's copies of picked files, in the widget folder (the service can't see mapped drives).</summary>
    public const string OutboxFolder = "outbox-files";

    /// <summary>The image previews the service copies into the widget folder for the tray.</summary>
    public const string PreviewFolder = "chat-previews";

    /// <summary>The folder under the member's Downloads that downloads land in.</summary>
    public const string DownloadsSubfolder = "Zinwe";

    // ---- Copy (brief §4: use exactly) ------------------------------------------------------------------------------
    public const string AttachTooltip = "Attach a file";
    public const string TooBigText = "Files can be up to 25 MB.";
    public const string SharingOffText = "Your admin has turned off file sharing.";
    public const string ExpiredText = "This file is no longer available. Files are kept for 7 days.";
    public const string FailedText = "Couldn't send. Try again.";
    public const string EmptyText = "That file is empty.";
    public const string DownloadFailedText = "Couldn't download. Try again.";

    /// <summary>"This is a program file. Open it only if you trust Priya."</summary>
    public static string RiskyText(string? firstName) =>
        $"This is a program file. Open it only if you trust {(string.IsNullOrWhiteSpace(firstName) ? "the sender" : firstName.Trim())}.";

    /// <summary>"Kept until 16 Nov" (the local date the file goes).</summary>
    public static string KeptUntil(DateTimeOffset expiresAt) =>
        "Kept until " + expiresAt.ToLocalTime().ToString("d MMM", CultureInfo.InvariantCulture);

    /// <summary>The "risky" extensions (brief §1): program files that run when opened.</summary>
    public static readonly IReadOnlySet<string> RiskyExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "exe", "msi", "bat", "cmd", "com", "scr", "ps1", "vbs", "vbe", "js", "jse", "wsf", "hta", "lnk", "reg", "jar", "appx", "msix",
    };

    /// <summary>True for a name whose extension runs a program ("setup.EXE", "x.tar.ps1"; "notes.txt " is trimmed first).</summary>
    public static bool IsRisky(string? name) => Extension(name) is { Length: > 0 } ext && RiskyExtensions.Contains(ext);

    /// <summary>The extension without the dot, lower case ("" when none). Trailing dots and spaces are ignored, as Windows does.</summary>
    public static string Extension(string? name)
    {
        var n = (name ?? "").TrimEnd(' ', '.');
        var dot = n.LastIndexOf('.');
        var slash = Math.Max(n.LastIndexOf('\\'), n.LastIndexOf('/'));
        return dot > slash && dot < n.Length - 1 ? n[(dot + 1)..].ToLowerInvariant() : "";
    }

    /// <summary>"12 B", "340 KB", "2.1 MB", "25 MB" (the same rule as the server's fallback text).</summary>
    public static string FormatSize(long bytes)
    {
        if (bytes < 1024)
        {
            return Math.Max(0, bytes).ToString(CultureInfo.InvariantCulture) + " B";
        }
        if (bytes < 1024 * 1024)
        {
            return Math.Max(1, (long)Math.Round(bytes / 1024d, MidpointRounding.AwayFromZero)).ToString(CultureInfo.InvariantCulture) + " KB";
        }
        var mb = Math.Round(bytes / (1024d * 1024d), 1, MidpointRounding.AwayFromZero);
        return mb.ToString(mb == Math.Floor(mb) ? "0" : "0.0", CultureInfo.InvariantCulture) + " MB";
    }

    /// <summary>"PDF", "DOCX", or "File" when the name has no extension (the card's "2.1 MB · PDF").</summary>
    public static string TypeLabel(string? name) => Extension(name) is { Length: > 0 and <= 5 } ext ? ext.ToUpperInvariant() : "File";

    /// <summary>The card's icon: pdf, doc, sheet, slide, zip, image, video, audio or other.</summary>
    public static string IconKind(string? name) => Extension(name) switch
    {
        "pdf" => "pdf",
        "doc" or "docx" or "odt" or "rtf" or "txt" or "md" => "doc",
        "xls" or "xlsx" or "ods" or "csv" or "tsv" => "sheet",
        "ppt" or "pptx" or "odp" or "key" => "slide",
        "zip" or "rar" or "7z" or "tar" or "gz" or "tgz" or "bz2" => "zip",
        "jpg" or "jpeg" or "png" or "gif" or "webp" or "bmp" or "heic" or "svg" or "tif" or "tiff" => "image",
        "mp4" or "mov" or "avi" or "mkv" or "webm" or "wmv" => "video",
        "mp3" or "wav" or "m4a" or "aac" or "ogg" or "flac" or "wma" => "audio",
        _ => "other",
    };

    /// <summary>The chat list's line for a file line: "📎 Quote-Lumen.pdf".</summary>
    public static string Preview(string name) => "📎 " + name;

    /// <summary>
    /// What the chat list shows for a line: "📎 name" for a file line (the file's name, or the server's fallback body
    /// "📎 name (2.1 MB)" read back when the list sent no file), else the body.
    /// </summary>
    public static string PreviewLine(TeamMessage m) =>
        m.File is { Name.Length: > 0 } f ? Preview(f.Name) : FallbackName(m.Body) is { } name ? Preview(name) : m.Body;

    /// <summary>The name in a fallback body "📎 Quote-Lumen.pdf (2.1 MB)\nFinal version", or null for any other text.</summary>
    public static string? FallbackName(string? body)
    {
        if (body is null || !body.StartsWith("📎 ", StringComparison.Ordinal))
        {
            return null;
        }
        var first = body.Split('\n')[0][3..].TrimEnd();
        if (first.EndsWith(')') && first.LastIndexOf(" (", StringComparison.Ordinal) is var open and > 0)
        {
            first = first[..open];
        }
        return first.Length > 0 ? first : null;
    }

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>
    /// A name safe to create on this PC: no folders, no characters Windows refuses, no trailing dots or spaces, not a
    /// device name (CON, NUL…), at most 200 characters with its extension kept. "file" when nothing is left.
    /// </summary>
    public static string SafeName(string? name)
    {
        var n = (name ?? "").Replace('/', '\\');
        n = n[(n.LastIndexOf('\\') + 1)..];
        var invalid = Path.GetInvalidFileNameChars();
        n = new string([.. n.Select(c => c < 32 || invalid.Contains(c) ? '_' : c)]).Trim().TrimEnd('.', ' ');
        if (n.Length == 0 || n.All(c => c is '.' or '_'))
        {
            return "file";
        }
        var stem = n.Split('.')[0];
        if (ReservedNames.Contains(stem.TrimEnd()))
        {
            n = "_" + n;
        }
        if (n.Length > 200)
        {
            var ext = Extension(n);
            var keep = ext.Length is > 0 and < 20 ? "." + ext : "";
            n = n[..(200 - keep.Length)].TrimEnd('.', ' ') + keep;
        }
        return n;
    }

    /// <summary>
    /// "report.pdf", or "report (2).pdf", "report (3).pdf"… — the first name in <paramref name="folder"/> that
    /// <paramref name="exists"/> says is free (the File Explorer rule).
    /// </summary>
    public static string FreeName(string folder, string name, Func<string, bool>? exists = null)
    {
        exists ??= p => File.Exists(p) || Directory.Exists(p);
        var safe = SafeName(name);
        if (!exists(Path.Combine(folder, safe)))
        {
            return Path.Combine(folder, safe);
        }
        var ext = Path.GetExtension(safe);
        var stem = safe[..^ext.Length];
        for (var i = 2; i < 10_000; i++)
        {
            var candidate = Path.Combine(folder, $"{stem} ({i.ToString(CultureInfo.InvariantCulture)}){ext}");
            if (!exists(candidate))
            {
                return candidate;
            }
        }
        return Path.Combine(folder, $"{stem} ({Guid.NewGuid():N}){ext}");
    }

    /// <summary>
    /// Save <paramref name="source"/> as <paramref name="name"/> in <paramref name="folder"/> (made when missing), never over
    /// an existing file (" (2)" is added), then mark it as downloaded from the internet (<see cref="MarkFromInternet"/>).
    /// Returns the new file's path. The caller runs this as the signed-in member, in their Downloads folder.
    /// </summary>
    public static string SaveNew(string folder, string name, Stream source)
    {
        Directory.CreateDirectory(folder);
        for (var attempt = 0; ; attempt++)
        {
            var path = FreeName(folder, name);
            FileStream target;
            try
            {
                target = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            }
            catch (IOException) when (attempt < 20 && File.Exists(path))
            {
                continue; // taken a moment ago: the next free name
            }
            using (target)
            {
                source.CopyTo(target);
            }
            MarkFromInternet(path);
            return path;
        }
    }

    /// <summary>The Mark of the Web Windows checks before opening a downloaded file (SmartScreen, Office Protected View).</summary>
    public const string ZoneIdentifier = "[ZoneTransfer]\r\nZoneId=3\r\n";

    /// <summary>
    /// Writes the <c>Zone.Identifier</c> stream (ZoneId=3, the internet) on <paramref name="path"/>, so Windows treats the file
    /// as downloaded. False on a drive without streams (FAT32, exFAT): the file is kept anyway.
    /// </summary>
    public static bool MarkFromInternet(string path)
    {
        try
        {
            File.WriteAllText(path + ":Zone.Identifier", ZoneIdentifier);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>".jpg", ".png", ".gif" or ".webp" from an image's first bytes; null for anything else (SVG included).</summary>
    public static string? SniffImage(ReadOnlySpan<byte> b)
    {
        if (b.Length >= 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF)
        {
            return ".jpg";
        }
        if (b.Length >= 8 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47 && b[4] == 0x0D && b[5] == 0x0A && b[6] == 0x1A && b[7] == 0x0A)
        {
            return ".png";
        }
        if (b.Length >= 6 && b[0] == 'G' && b[1] == 'I' && b[2] == 'F' && b[3] == '8' && (b[4] == '7' || b[4] == '9') && b[5] == 'a')
        {
            return ".gif";
        }
        if (b.Length >= 12 && b[0] == 'R' && b[1] == 'I' && b[2] == 'F' && b[3] == 'F' && b[8] == 'W' && b[9] == 'E' && b[10] == 'B' && b[11] == 'P')
        {
            return ".webp";
        }
        return null;
    }

    /// <summary>A file id as the server makes them (a UUID); anything else is never put in a URL or a file name.</summary>
    public static bool IsValidId(string? id) => Guid.TryParse(id, out _);

    // ---- JSON ----------------------------------------------------------------------------------------------------

    /// <summary>A FileJSON object, or null for null, a non-object or one without an id.</summary>
    public static ChatFile? FromJson(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object
            || (Text(e, "id") ?? Number(e, "id")?.ToString(CultureInfo.InvariantCulture)) is not { Length: > 0 } id)
        {
            return null;
        }
        return new ChatFile(
            id,
            Text(e, "name") ?? "",
            Number(e, "size") is { } size ? Math.Max(0, size) : 0,
            Text(e, "mime"),
            Text(e, "kind") is { Length: > 0 } kind ? kind.ToLowerInvariant() : ChatFile.FileKind,
            Text(e, "caption"),
            Bool(e, "risky") ?? false,
            Time(e, "expires_at") ?? Time(e, "expiresAt"),
            Bool(e, "expired") ?? false,
            Text(e, "url"));
    }

    /// <summary><c>{"allowed", "max_bytes", "keep_days"}</c>; null when the body isn't that object.</summary>
    public static ChatFilesInfo? InfoFromJson(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var e = doc.RootElement;
            if (e.ValueKind != JsonValueKind.Object)
            {
                return null;
            }
            var max = Number(e, "max_bytes") ?? Number(e, "file_max_bytes") ?? MaxBytes;
            var days = Number(e, "keep_days") ?? Number(e, "file_keep_days") ?? KeepDays;
            return new ChatFilesInfo(Bool(e, "allowed") ?? Bool(e, "allow_files") ?? false,
                max is > 0 and <= MaxBytes * 4 ? max : MaxBytes, (int)Math.Clamp(days, 1, 365));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>A FileJSON reply body (the upload's 201), or null.</summary>
    public static ChatFile? FromJson(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return FromJson(doc.RootElement);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Text(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static long? Number(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v)
            ? v.ValueKind switch
            {
                JsonValueKind.Number when v.TryGetInt64(out var n) => n,
                JsonValueKind.String when long.TryParse(v.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) => n,
                _ => null,
            }
            : null;

    private static bool? Bool(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) ? v.ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => null } : null;

    private static DateTimeOffset? Time(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.TryGetDateTimeOffset(out var at) ? at : null;
}

/// <summary>
/// Reads a FileJSON leniently (anything but an object with an id is null, never an exception) and writes it in the backend's
/// own snake_case shape, whichever serializer runs it (the backend contracts, the pipe, the notifications file).
/// </summary>
public sealed class ChatFileConverter : JsonConverter<ChatFile?>
{
    public override bool HandleNull => true;

    public override ChatFile? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        return ChatFiles.FromJson(doc.RootElement);
    }

    public override void Write(Utf8JsonWriter writer, ChatFile? value, JsonSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNullValue();
            return;
        }
        writer.WriteStartObject();
        writer.WriteString("id", value.Id);
        writer.WriteString("name", value.Name);
        writer.WriteNumber("size", value.Size);
        if (value.Mime is not null)
        {
            writer.WriteString("mime", value.Mime);
        }
        writer.WriteString("kind", value.Kind);
        if (value.Caption is not null)
        {
            writer.WriteString("caption", value.Caption);
        }
        writer.WriteBoolean("risky", value.Risky);
        if (value.ExpiresAt is { } at)
        {
            writer.WriteString("expires_at", at);
        }
        writer.WriteBoolean("expired", value.Expired);
        if (value.Url is not null)
        {
            writer.WriteString("url", value.Url);
        }
        writer.WriteEndObject();
    }
}

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Zinwe.Agent.Core.Chat;

// v1.5 round 2 · file sharing in chat, "files stay on the sender's device". Our server never stores a file: it keeps the
// record (name, size, type, who holds it, a tiny preview) and a relay streams the bytes, in memory only, from the sender's
// PC to the receiver. On Windows the service does every backend and relay call: it registers what the tray queued (outbox
// kind chat_file), hashes it, posts the line, keeps a long-poll open on the relay while it holds files and pushes them
// from the asked offset; and it downloads (ticket → resumable Range GET into a .part → sha256 → Zone.Identifier) what the
// tray asks for over the pipe ("files"). The tray picks, makes the thumbnails, shows, and asks.

/// <summary>
/// A file on a chat line (the backend's FileJSON, BRIEF2 §2): <c>{id, name, size, mime, kind, caption, risky, batch_id, width,
/// height, duration_s, thumb_url, sender: {name, first_name}, mine, available, state, created_at}</c>, plus round 1's
/// <c>expires_at</c> / <c>expired</c> / <c>url</c>. Read leniently (<see cref="ChatFileConverter"/>): an odd shape is "no
/// file", never an error, so one bad line can't hide the chat. The same converter writes it for the pipe and the widget's
/// notifications file. The first ten parameters keep their round-1 order.
/// </summary>
/// <param name="Kind">"image", "video", "audio", "document" or "file" (round 1 sent only "image" and "file").</param>
/// <param name="Caption">What the sender typed with the file (new clients show it and ignore the line's body).</param>
/// <param name="Url">Round 1 only (a server copy): the device route to download it. Round 2 has none: ask for a ticket.</param>
/// <param name="BatchId">Files sent together share it: photos and videos of one batch show as a 2×2 grid.</param>
/// <param name="State">"ok", "stopped", "gone" (the sender's copy changed or went) or "old" (over 30 days). Null from round 1.</param>
/// <param name="Sha256">Not in the contract yet; checked after a download when a backend sends it.</param>
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
    string? Url = null,
    string? BatchId = null,
    int? Width = null,
    int? Height = null,
    double? DurationS = null,
    string? ThumbUrl = null,
    string? SenderName = null,
    string? SenderFirst = null,
    bool? Mine = null,
    bool? Available = null,
    string? State = null,
    DateTimeOffset? CreatedAt = null,
    string? Sha256 = null)
{
    public const string ImageKind = "image";
    public const string VideoKind = "video";
    public const string AudioKind = "audio";
    public const string DocumentKind = "document";
    public const string FileKind = "file";

    public const string StateOk = "ok";
    public const string StateStopped = "stopped";
    public const string StateGone = "gone";
    public const string StateOld = "old";

    [JsonIgnore]
    public bool IsImage => string.Equals(Kind, ImageKind, StringComparison.OrdinalIgnoreCase);

    [JsonIgnore]
    public bool IsVideo => string.Equals(Kind, VideoKind, StringComparison.OrdinalIgnoreCase);

    /// <summary>A photo or a video: a tile with its thumbnail (and a 2×2 grid when several were sent together).</summary>
    [JsonIgnore]
    public bool IsMedia => IsImage || IsVideo;

    /// <summary>A program file: the server's word, or its extension here (both are checked).</summary>
    [JsonIgnore]
    public bool IsRisky => Risky || ChatFiles.IsRisky(Name);

    /// <summary>
    /// Round 1's shape (a copy on the server, downloaded from <see cref="Url"/> with the device token): no state, no
    /// "available". Only the dev server ever sent it.
    /// </summary>
    [JsonIgnore]
    public bool IsLegacy => State is null && Available is null && Url is not null;

    /// <summary>
    /// Still downloadable at <paramref name="now"/>: round 2 says so itself (available, and its state ok); round 1 by its
    /// url and keep date.
    /// </summary>
    public bool AvailableAt(DateTimeOffset now)
    {
        if (Available is not null || State is not null)
        {
            return Available != false && (State is null || string.Equals(State, StateOk, StringComparison.OrdinalIgnoreCase));
        }
        return !Expired && Url is not null && (ExpiresAt is not { } e || e > now);
    }

    /// <summary>The sender's first name for "Waiting for Priya's PC…" (the file's own sender, else the line's author).</summary>
    public string FirstName(string? fallback = null)
    {
        if (!string.IsNullOrWhiteSpace(SenderFirst))
        {
            return SenderFirst.Trim();
        }
        var name = !string.IsNullOrWhiteSpace(SenderName) ? SenderName : fallback;
        return string.IsNullOrWhiteSpace(name) ? "the sender" : name.Trim().Split(' ')[0];
    }
}

/// <summary>
/// <c>GET /api/devices/chat/files/</c>: <c>{"allowed", "max_bytes", "keep_days"}</c>. Allowed = the attach button shows
/// (with <see cref="Relay"/>: this backend has round 2's relay, so files stay on this PC). A backend without the route
/// (404/405) is "files off".
/// </summary>
/// <param name="MaxBytes">The plan's own limit (Free 1 GB, Startups 2 GB, Teams 3 GB, Businesses 5 GB).</param>
/// <param name="Relay">The service got a serve ticket: a round-2 backend. Round 1 (server copies) can't take a file from this
/// app any more, so the attach button stays hidden there; its files still download.</param>
public sealed record ChatFilesInfo(bool Allowed = false, long MaxBytes = ChatFiles.DefaultMaxBytes, int KeepDays = ChatFiles.KeepDays,
    bool Relay = false)
{
    /// <summary>The attach button shows.</summary>
    [JsonIgnore]
    public bool CanSend => Allowed && Relay;
}

/// <summary>
/// The tray asks the service for something over the pipe ("files"):
/// <list type="bullet">
/// <item><see cref="Download"/> a file into <see cref="Folder"/> (the "Save files to" folder; the name is free-made there) or
///   exactly to <see cref="Target"/> (Save as…, the member chose it and agreed to replace).</item>
/// <item><see cref="Preview"/>: the file's thumbnail (round 2: <c>thumb/</c>; round 1: the image itself, ≤ 5 MB).</item>
/// <item><see cref="Cancel"/> a download (its .part goes), <see cref="Stop"/> sharing one of the member's own files.</item>
/// </list>
/// <see cref="Name"/> / <see cref="Size"/> are what the line shows; <see cref="Legacy"/> = a round-1 file (no relay).
/// </summary>
public sealed record FileAsk(string Action, string Id, string? Name = null, long Size = 0, string? Folder = null, string? Target = null,
    bool Legacy = false, string? Sha256 = null, string? SenderFirst = null)
{
    public const string Download = "download";
    public const string Preview = "preview";
    public const string Cancel = "cancel";
    public const string Stop = "stop";
}

/// <summary>
/// Where one download or preview is: <see cref="Working"/> (<see cref="Received"/> of <see cref="Total"/>),
/// <see cref="Waiting"/> (the sender's PC is off: "Waiting for <see cref="WaitingFor"/>'s PC…", tried again every 30 s),
/// <see cref="Done"/> (<see cref="Path"/> = the file), <see cref="Gone"/> ("Not shared any more."), <see cref="Failed"/>
/// (<see cref="Error"/> says why) or <see cref="Cancelled"/>.
/// </summary>
public sealed record FileState(string Id, string State, string? Path = null, string? Error = null, long Received = 0, long Total = 0,
    string? WaitingFor = null)
{
    public const string Working = "working";
    public const string Waiting = "waiting";
    public const string Done = "done";
    public const string Gone = "gone";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";

    /// <summary>Under way: the spinner ring (with %) or the waiting pulse shows, with ✕.</summary>
    [JsonIgnore]
    public bool Busy => State is Working or Waiting;

    /// <summary>0-100 (0 while the size isn't known).</summary>
    [JsonIgnore]
    public int Percent => Total > 0 ? (int)Math.Clamp(Received * 100 / Total, 0, 100) : 0;
}

/// <summary>
/// One of the member's own shared files ("My shared files"): name, size, the chat it went to (its key, and its name when the
/// server's list said it), when, and its state.
/// </summary>
public sealed record SharedFile(string Id, string Name, long Size, string? ChatKey, DateTimeOffset CreatedAt, string State = ChatFile.StateOk,
    string? ChatName = null);

/// <summary>
/// The service's reply to a "files" pipe request. <see cref="Info"/> null = not known yet, or a backend without file sharing:
/// no attach button. A 1.4 service answers "not supported", which reads as <see cref="Supported"/> = false.
/// </summary>
public sealed record ChatFilesReply(
    bool Supported = false,
    ChatFilesInfo? Info = null,
    IReadOnlyList<FileState>? Downloads = null,
    IReadOnlyList<FileState>? Previews = null,
    int Version = 0,
    IReadOnlyList<SharedFile>? Shared = null);

/// <summary>The v1.5 rules and words for files in chat, shared by the service and the tray.</summary>
public static class ChatFiles
{
    /// <summary>The plan limit when a backend doesn't say (Free: 1 GB).</summary>
    public const long DefaultMaxBytes = 1024L * 1024 * 1024;

    /// <summary>The round-1 limit (25 MB on every plan): only for reading round-1 files.</summary>
    public const long LegacyMaxBytes = 25L * 1024 * 1024;

    /// <summary>FILE_KEEP_DAYS: a file can be downloaded for 30 days (it is the sender's disk, not ours).</summary>
    public const int KeepDays = 30;

    /// <summary>At most this many files are picked and sent at once.</summary>
    public const int MaxFilesAtOnce = 10;

    /// <summary>The preview sent with a registration: at most 320 px on its long side, a JPEG of at most 40 KB.</summary>
    public const int ThumbMaxPixels = 320;
    public const int ThumbMaxBytes = 40 * 1024;

    /// <summary>Round 1: images up to this size were fetched whole for a thumbnail.</summary>
    public const long PreviewMaxBytes = 5L * 1024 * 1024;

    /// <summary>The sender's PC is off: try again this often, for this long.</summary>
    public static readonly TimeSpan RetryEvery = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan WaitUpTo = TimeSpan.FromHours(24);

    /// <summary>At most this many pushes (sending) and downloads (receiving) at once.</summary>
    public const int MaxPushes = 3;
    public const int MaxDownloads = 3;

    /// <summary>Copies of files on network or removable drives: <c>%ProgramData%\Zinwe\shared\&lt;id&gt;\</c>.</summary>
    public const string SharedFolderName = "shared";

    /// <summary>Where the tray copies such a file when it can't make one under %ProgramData% (an older service): the widget folder.</summary>
    public const string FallbackCopiesFolder = "shared-files";

    /// <summary>The thumbnails the service copies into the widget folder for the tray.</summary>
    public const string PreviewFolder = "chat-previews";

    /// <summary>The folder under the member's Downloads that downloads land in by default.</summary>
    public const string DownloadsSubfolder = "Zinwe";

    // ---- Copy (BRIEF2 §6: use exactly; and BRIEF §4 where it still applies) ------------------------------------------
    public const string AttachTooltip = "Attach a file";
    public const string AttachDocument = "Document";
    public const string AttachMedia = "Photos & videos";
    public const string TooManyText = "You can send up to 10 files at a time.";
    public const string NotSharedText = "Not shared any more.";
    public const string OwnFileText = "Kept on your PC. Others download it from here while your PC is on.";
    public const string StopText = "Stop sharing";
    public const string SaveAsText = "Save as…";
    public const string SaveToText = "Save files to";
    public const string AskWhereText = "Ask where to save each file";
    public const string SharingOffText = "Your admin has turned off file sharing.";
    public const string FailedText = "Couldn't send. Try again.";
    public const string EmptyText = "That file is empty.";
    public const string DownloadFailedText = "Couldn't download. Try again.";
    public const string MissingText = "That file was moved or deleted.";
    public const string MySharedFilesText = "My shared files";

    /// <summary>"Files can be up to 2 GB on your plan." (the plan's own number).</summary>
    public static string TooBigText(long maxBytes) => $"Files can be up to {PlanGb(maxBytes)} GB on your plan.";

    /// <summary>"Waiting for Priya's PC…".</summary>
    public static string WaitingText(string? firstName) =>
        $"Waiting for {(string.IsNullOrWhiteSpace(firstName) ? "the sender" : firstName.Trim())}'s PC…";

    /// <summary>"This is a program file. Open it only if you trust Priya."</summary>
    public static string RiskyText(string? firstName) =>
        $"This is a program file. Open it only if you trust {(string.IsNullOrWhiteSpace(firstName) ? "the sender" : firstName.Trim())}.";

    /// <summary>A plan limit in GB as the admin set it: 1, 2, 5, or 1.5.</summary>
    public static string PlanGb(long maxBytes) =>
        Math.Round(Math.Max(0, maxBytes) / (1024d * 1024 * 1024), 1, MidpointRounding.AwayFromZero).ToString("0.#", CultureInfo.InvariantCulture);

    /// <summary>The "risky" extensions: program files that run when opened.</summary>
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

    /// <summary>"12 B", "340 KB", "2.1 MB", "1.4 GB" (the same rule as the server's fallback text).</summary>
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
        if (bytes < 1024L * 1024 * 1024)
        {
            var mb = Math.Round(bytes / (1024d * 1024d), 1, MidpointRounding.AwayFromZero);
            if (mb < 1024)
            {
                return mb.ToString(mb == Math.Floor(mb) ? "0" : "0.0", CultureInfo.InvariantCulture) + " MB";
            }
        }
        var gb = Math.Round(bytes / (1024d * 1024d * 1024d), 1, MidpointRounding.AwayFromZero);
        return gb.ToString(gb == Math.Floor(gb) ? "0" : "0.0", CultureInfo.InvariantCulture) + " GB";
    }

    /// <summary>A video's length for its badge: "0:07", "1:23", "1:02:03". Empty for none.</summary>
    public static string FormatDuration(double? seconds)
    {
        if (seconds is not { } s || double.IsNaN(s) || s <= 0)
        {
            return "";
        }
        var t = TimeSpan.FromSeconds(Math.Round(s, MidpointRounding.AwayFromZero));
        return t.TotalHours >= 1
            ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}"
            : $"{t.Minutes}:{t.Seconds:00}";
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
        "mp4" or "mov" or "avi" or "mkv" or "webm" or "wmv" or "m4v" => "video",
        "mp3" or "wav" or "m4a" or "aac" or "ogg" or "flac" or "wma" => "audio",
        _ => "other",
    };

    // ---- What the attach menu picks ------------------------------------------------------------------------------

    /// <summary>"Document": pdf, Office, text, csv, rtf, OpenDocument and archives (and "All files").</summary>
    public static readonly IReadOnlyList<string> DocumentExtensions =
        ["pdf", "doc", "docx", "xls", "xlsx", "ppt", "pptx", "txt", "csv", "rtf", "odt", "ods", "odp", "zip", "rar", "7z"];

    /// <summary>"Photos & videos": images and mp4/mov/avi/mkv/webm.</summary>
    public static readonly IReadOnlyList<string> PhotoExtensions = ["jpg", "jpeg", "png", "gif", "webp", "bmp", "heic", "tif", "tiff"];
    public static readonly IReadOnlyList<string> VideoExtensions = ["mp4", "mov", "avi", "mkv", "webm"];

    /// <summary>The Windows file picker's filter for one attach menu entry.</summary>
    public static string PickerFilter(bool media) => media
        ? "Photos and videos|" + Patterns([.. PhotoExtensions, .. VideoExtensions]) + "|Photos|" + Patterns(PhotoExtensions) + "|Videos|" + Patterns(VideoExtensions)
        : "Documents|" + Patterns(DocumentExtensions) + "|All files|*.*";

    private static string Patterns(IEnumerable<string> extensions) => string.Join(";", extensions.Select(e => "*." + e));

    /// <summary>
    /// The kind the server sets from the extension + mime (the tray shows a tile before the line exists): image, video,
    /// audio, document or file. SVG is a document-like file, never an image.
    /// </summary>
    public static string KindOf(string? name, string? mime = null)
    {
        var ext = Extension(name);
        if (ext is "svg" || IsRisky(name))
        {
            return ChatFile.FileKind; // never previewed, never "a document"
        }
        if (PhotoExtensions.Contains(ext) || ext is "heif" or "avif")
        {
            return ChatFile.ImageKind;
        }
        if (VideoExtensions.Contains(ext) || ext is "wmv" or "m4v" or "3gp" or "mpg" or "mpeg")
        {
            return ChatFile.VideoKind;
        }
        if (IconKind(name) == "audio" || ext is "oga" or "opus")
        {
            return ChatFile.AudioKind;
        }
        if (ext is "zip" or "rar" or "7z")
        {
            return ChatFile.FileKind; // archives are plain files (the server's rule)
        }
        if (DocumentExtensions.Contains(ext) || ext is "md")
        {
            return ChatFile.DocumentKind;
        }
        return mime switch
        {
            not null when mime.StartsWith("image/", StringComparison.OrdinalIgnoreCase) && !mime.Contains("svg", StringComparison.OrdinalIgnoreCase) => ChatFile.ImageKind,
            not null when mime.StartsWith("video/", StringComparison.OrdinalIgnoreCase) => ChatFile.VideoKind,
            not null when mime.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) => ChatFile.AudioKind,
            _ => ChatFile.FileKind,
        };
    }

    /// <summary>The mime type for a name (informational: the server sets the kind; unknown = application/octet-stream).</summary>
    public static string MimeOf(string? name) => Extension(name) switch
    {
        "jpg" or "jpeg" => "image/jpeg",
        "png" => "image/png",
        "gif" => "image/gif",
        "webp" => "image/webp",
        "bmp" => "image/bmp",
        "heic" => "image/heic",
        "tif" or "tiff" => "image/tiff",
        "mp4" or "m4v" => "video/mp4",
        "mov" => "video/quicktime",
        "avi" => "video/x-msvideo",
        "mkv" => "video/x-matroska",
        "webm" => "video/webm",
        "wmv" => "video/x-ms-wmv",
        "mp3" => "audio/mpeg",
        "wav" => "audio/wav",
        "m4a" => "audio/mp4",
        "pdf" => "application/pdf",
        "doc" => "application/msword",
        "docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        "xls" => "application/vnd.ms-excel",
        "xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        "ppt" => "application/vnd.ms-powerpoint",
        "pptx" => "application/vnd.openxmlformats-officedocument.presentationml.presentation",
        "txt" => "text/plain",
        "csv" => "text/csv",
        "rtf" => "application/rtf",
        "odt" => "application/vnd.oasis.opendocument.text",
        "ods" => "application/vnd.oasis.opendocument.spreadsheet",
        "odp" => "application/vnd.oasis.opendocument.presentation",
        "zip" => "application/zip",
        "rar" => "application/vnd.rar",
        "7z" => "application/x-7z-compressed",
        _ => "application/octet-stream",
    };

    /// <summary>
    /// The files the member picked, checked against the limits: at most <see cref="MaxFilesAtOnce"/> in all (with
    /// <paramref name="already"/> waiting in the tray), none over <paramref name="maxBytes"/> and none empty. Returns the ones
    /// that may wait, and the words for the ones refused ("Files can be up to 2 GB on your plan.", "You can send up to 10
    /// files at a time."), each said once.
    /// </summary>
    public static (IReadOnlyList<(string Path, long Size)> Ok, IReadOnlyList<string> Refusals) CheckPicked(
        IEnumerable<(string Path, long Size)> picked, int already, long maxBytes)
    {
        var ok = new List<(string, long)>();
        var refusals = new List<string>();
        foreach (var (path, size) in picked)
        {
            string? why = size > maxBytes ? TooBigText(maxBytes) : size <= 0 ? EmptyText : already + ok.Count >= MaxFilesAtOnce ? TooManyText : null;
            if (why is null)
            {
                ok.Add((path, size));
            }
            else if (!refusals.Contains(why))
            {
                refusals.Add(why);
            }
        }
        return (ok, refusals);
    }

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
    /// The half-downloaded file for <paramref name="fileId"/>: next to where it ends up, so a download can go on (Range)
    /// from where it stopped. Into a folder: <c>&lt;folder&gt;\&lt;name&gt;.&lt;id8&gt;.part</c> (two files of the same name
    /// never share one); Save as: <c>&lt;target&gt;.part</c>.
    /// </summary>
    public static string PartPath(string fileId, string name, string? folder, string? target) =>
        target is not null
            ? target + ".part"
            : Path.Combine(folder ?? "", SafeName(name) + "." + new string([.. fileId.Where(char.IsLetterOrDigit).Take(8)]) + ".part");

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

    /// <summary>A lower-case hex sha256 (64 characters), or null.</summary>
    public static string? NormalHash(string? hash) =>
        hash is { Length: 64 } h && h.All(Uri.IsHexDigit) ? h.ToLowerInvariant() : null;

    // ---- JSON ----------------------------------------------------------------------------------------------------

    /// <summary>A FileJSON object, or null for null, a non-object or one without an id.</summary>
    public static ChatFile? FromJson(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object
            || (Text(e, "id") ?? Number(e, "id")?.ToString(CultureInfo.InvariantCulture)) is not { Length: > 0 } id)
        {
            return null;
        }
        string? senderName = null, senderFirst = null;
        if (e.TryGetProperty("sender", out var sender))
        {
            if (sender.ValueKind == JsonValueKind.Object)
            {
                senderName = Text(sender, "name");
                senderFirst = Text(sender, "first_name");
            }
            else if (sender.ValueKind == JsonValueKind.String)
            {
                senderName = sender.GetString();
            }
        }
        senderName ??= Text(e, "sender_name");
        senderFirst ??= Text(e, "sender_first_name") ?? Text(e, "sender_first");
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
            Text(e, "url"),
            Text(e, "batch_id") is { Length: > 0 } batch ? batch : null,
            Number(e, "width") is { } w and > 0 and < 100_000 ? (int)w : null,
            Number(e, "height") is { } h and > 0 and < 100_000 ? (int)h : null,
            Real(e, "duration_s") is { } d and > 0 ? d : null,
            Text(e, "thumb_url") is { Length: > 0 } thumb ? thumb : null,
            senderName,
            senderFirst,
            Bool(e, "mine"),
            Bool(e, "available"),
            Text(e, "state") is { Length: > 0 } state ? state.ToLowerInvariant() : null,
            Time(e, "created_at"),
            NormalHash(Text(e, "sha256")));
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
            var max = Number(e, "max_bytes") ?? Number(e, "file_max_bytes") ?? (Number(e, "file_max_mb") is { } mb ? mb * 1024 * 1024 : null);
            var days = Number(e, "keep_days") ?? Number(e, "file_keep_days") ?? KeepDays;
            return new ChatFilesInfo(Bool(e, "allowed") ?? Bool(e, "allow_files") ?? false,
                max is { } m and >= 1024 and <= 1024L * 1024 * 1024 * 1024 ? m : DefaultMaxBytes, (int)Math.Clamp(days, 1, 3650));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>A FileJSON reply body (the registration's 201), or null.</summary>
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

    /// <summary><c>{"ticket", "wait_url", "push_base", "expires_at"}</c>; null unless it has a ticket and both urls.</summary>
    public static ServeTicket? ServeTicketFromJson(string json, DateTimeOffset now)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var e = doc.RootElement;
            if (e.ValueKind != JsonValueKind.Object || Text(e, "ticket") is not { Length: > 0 } ticket
                || RelayUri(Text(e, "wait_url")) is not { } wait || RelayUri(Text(e, "push_base")) is not { } push)
            {
                return null;
            }
            return new ServeTicket(ticket, wait, push, Time(e, "expires_at") ?? now.AddHours(24));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary><c>{"url", "inline_url", "status_url", "expires_at"}</c>; null unless the url is one we may call.</summary>
    public static FileTicket? TicketFromJson(string json, DateTimeOffset now)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var e = doc.RootElement;
            if (e.ValueKind != JsonValueKind.Object || RelayUri(Text(e, "url")) is not { } url)
            {
                return null;
            }
            return new FileTicket(url, RelayUri(Text(e, "inline_url")), RelayUri(Text(e, "status_url")),
                Time(e, "expires_at") ?? now.AddMinutes(15), NormalHash(Text(e, "sha256")));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// <c>GET chat/files/shared/</c>: the member's files still shared (from any of their PCs), FileJSON plus <c>chat: {kind,
    /// id, name}</c>. Null when the body isn't a list.
    /// </summary>
    public static IReadOnlyList<SharedFile>? SharedFromJson(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("results", out var results))
            {
                root = results;
            }
            if (root.ValueKind != JsonValueKind.Array)
            {
                return null;
            }
            var list = new List<SharedFile>();
            foreach (var e in root.EnumerateArray())
            {
                if (FromJson(e) is not { } f || !IsValidId(f.Id))
                {
                    continue;
                }
                string? key = null, chatName = null;
                if (e.TryGetProperty("chat", out var chat) && chat.ValueKind == JsonValueKind.Object)
                {
                    chatName = Text(chat, "name");
                    var id = Number(chat, "id");
                    key = Text(chat, "kind") switch
                    {
                        "admin" => Messages.OutboxReply.AdminKey,
                        "group" or "direct" when id is { } n => "conv:" + n.ToString(CultureInfo.InvariantCulture),
                        _ => null,
                    };
                }
                list.Add(new SharedFile(f.Id.ToLowerInvariant(), f.Name, f.Size, key, f.CreatedAt ?? DateTimeOffset.MinValue,
                    f.State ?? ChatFile.StateOk, chatName));
            }
            return list;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary><c>{"job", "file_id", "offset"}</c> from the relay's wait; null when it isn't one.</summary>
    public static RelayJob? JobFromJson(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var e = doc.RootElement;
            if (e.ValueKind != JsonValueKind.Object
                || (Text(e, "job") ?? Number(e, "job")?.ToString(CultureInfo.InvariantCulture)) is not { Length: > 0 and <= 200 } job
                || Text(e, "file_id") is not { } fileId || !IsValidId(fileId))
            {
                return null;
            }
            return new RelayJob(job, fileId.ToLowerInvariant(), Math.Max(0, Number(e, "offset") ?? 0));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// A relay url as a ticket reply gives it: absolute, https (or loopback http for a dev server). Tickets authorize these
    /// calls, so the device token is never sent with them.
    /// </summary>
    public static Uri? RelayUri(string? url) =>
        url is { Length: > 0 } && Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback))
            ? uri
            : null;

    private static string? Text(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static long? Number(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v)
            ? v.ValueKind switch
            {
                JsonValueKind.Number when v.TryGetInt64(out var n) => n,
                JsonValueKind.Number when v.TryGetDouble(out var d) && d is >= long.MinValue and <= long.MaxValue => (long)d,
                JsonValueKind.String when long.TryParse(v.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) => n,
                _ => null,
            }
            : null;

    private static double? Real(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v)
            ? v.ValueKind switch
            {
                JsonValueKind.Number when v.TryGetDouble(out var d) && double.IsFinite(d) => d,
                JsonValueKind.String when double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && double.IsFinite(d) => d,
                _ => null,
            }
            : null;

    private static bool? Bool(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) ? v.ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => null } : null;

    private static DateTimeOffset? Time(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.TryGetDateTimeOffset(out var at) ? at : null;
}

/// <summary>The serve ticket (24 h) of the device holding files: its long-poll url and where to push.</summary>
public sealed record ServeTicket(string Ticket, Uri WaitUrl, Uri PushBase, DateTimeOffset ExpiresAt)
{
    /// <summary>Refreshed an hour before it runs out.</summary>
    public bool FreshAt(DateTimeOffset now) => ExpiresAt - now > TimeSpan.FromHours(1);

    /// <summary><c>&lt;push_base&gt;&lt;job&gt;?t=&lt;ticket&gt;</c>.</summary>
    public Uri PushUrl(string job) =>
        new(PushBase.GetLeftPart(UriPartial.Path).TrimEnd('/') + "/" + Uri.EscapeDataString(job) + "?t=" + Uri.EscapeDataString(Ticket));
}

/// <summary>A get ticket (15 min) for one file: the relay url, the inline one, the "is the sender online" one.</summary>
public sealed record FileTicket(Uri Url, Uri? InlineUrl, Uri? StatusUrl, DateTimeOffset ExpiresAt, string? Sha256 = null)
{
    /// <summary>Asked again a minute before it runs out.</summary>
    public bool FreshAt(DateTimeOffset now) => ExpiresAt - now > TimeSpan.FromMinutes(1);
}

/// <summary>A receiver is waiting: push <see cref="FileId"/> from <see cref="Offset"/> to <c>push/&lt;job&gt;</c>.</summary>
public sealed record RelayJob(string Job, string FileId, long Offset);

/// <summary>
/// Groups the lines of a thread into what the view draws: photos and videos sent together (the same batch_id, one after
/// another, by the same sender) become one 2×2 grid; every other line stands alone.
/// </summary>
public static class ChatFileBatches
{
    /// <summary>The lines in order, a batch of media as one group (two or more); the rest one line each.</summary>
    public static IReadOnlyList<IReadOnlyList<T>> Group<T>(IReadOnlyList<T> lines, Func<T, ChatFile?> file, Func<T, string?> sender)
    {
        var groups = new List<IReadOnlyList<T>>();
        var i = 0;
        while (i < lines.Count)
        {
            var group = new List<T> { lines[i] };
            if (file(lines[i]) is { IsMedia: true, BatchId: { Length: > 0 } batch })
            {
                var who = sender(lines[i]);
                while (i + group.Count < lines.Count && file(lines[i + group.Count]) is { IsMedia: true } next
                    && string.Equals(next.BatchId, batch, StringComparison.Ordinal) && string.Equals(sender(lines[i + group.Count]), who, StringComparison.Ordinal))
                {
                    group.Add(lines[i + group.Count]);
                }
            }
            groups.Add(group);
            i += group.Count;
        }
        return groups;
    }

    /// <summary>A grid of <paramref name="count"/>: the tiles shown (at most 4), and the "+N" on the 4th (0 = none).</summary>
    public static (int Shown, int More) Grid(int count) => (Math.Min(count, 4), Math.Max(0, count - 4));
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
        if (value.BatchId is not null)
        {
            writer.WriteString("batch_id", value.BatchId);
        }
        if (value.Width is { } w)
        {
            writer.WriteNumber("width", w);
        }
        if (value.Height is { } h)
        {
            writer.WriteNumber("height", h);
        }
        if (value.DurationS is { } d)
        {
            writer.WriteNumber("duration_s", d);
        }
        if (value.ThumbUrl is not null)
        {
            writer.WriteString("thumb_url", value.ThumbUrl);
        }
        if (value.SenderName is not null || value.SenderFirst is not null)
        {
            writer.WriteStartObject("sender");
            if (value.SenderName is not null)
            {
                writer.WriteString("name", value.SenderName);
            }
            if (value.SenderFirst is not null)
            {
                writer.WriteString("first_name", value.SenderFirst);
            }
            writer.WriteEndObject();
        }
        if (value.Mine is { } mine)
        {
            writer.WriteBoolean("mine", mine);
        }
        if (value.Available is { } available)
        {
            writer.WriteBoolean("available", available);
        }
        if (value.State is not null)
        {
            writer.WriteString("state", value.State);
        }
        if (value.CreatedAt is { } created)
        {
            writer.WriteString("created_at", created);
        }
        if (value.Sha256 is not null)
        {
            writer.WriteString("sha256", value.Sha256);
        }
        writer.WriteEndObject();
    }
}

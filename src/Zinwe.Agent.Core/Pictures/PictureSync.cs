using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Zinwe.Agent.Core.Backend;
using Zinwe.Agent.Core.Ipc;
using Zinwe.Agent.Core.Messages;

namespace Zinwe.Agent.Core.Pictures;

/// <summary>
/// The PC's member's picture (FR-44 team pictures), kept in step by the service:
/// <list type="number">
/// <item>the heartbeat reply carries <c>picture</c> (<see cref="OnHeartbeat"/>): what the owner or the member last set.
///   No such field = a backend without pictures (<see cref="PictureBackend.Off"/>);</item>
/// <item>the member's choice in the tray arrives on the pipe (<see cref="Request"/>) and waits in the shared outbox
///   (kind <c>picture</c>, only the newest kept) until <c>PUT</c>/<c>DELETE me/picture/</c> stored it: it survives
///   offline and a restart. A 4xx is final (the real picture shows again); 404/405 = an older backend, pictures off;
///   network, 429 and 5xx are retried with a backoff (15 s doubling to 5 min);</item>
/// <item>a photo is downloaded once per URL (the URL changes when the picture does) into
///   <c>&lt;DataDirectory&gt;\pictures\&lt;hash&gt;.jpg</c>, and copied, as the signed-in user, into their widget folder
///   (<c>member-picture-&lt;hash&gt;.jpg</c>): the tray can't read the service's SYSTEM-only data folder. Old copies
///   are deleted.</item>
/// </list>
/// <see cref="Status"/> goes on the pipe status with a <see cref="PictureStatus.Version"/> that moves on every change,
/// so the tray redraws only then. Never throws to the caller except on cancellation.
/// </summary>
public sealed class PictureSync
{
    public static readonly TimeSpan FirstRetry = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan MaxRetry = TimeSpan.FromMinutes(5);

    private const string StateFileName = "picture.json";
    private const string CacheFolderName = "pictures";
    /// <summary>The prefix of the tray-readable copies in the widget folder.</summary>
    public const string WidgetFilePrefix = "member-picture-";

    private readonly BackendClient _backend;
    private readonly MessageOutbox _outbox;
    private readonly IWidgetFolder _widget;
    private readonly ILogger _logger;
    private readonly string? _dataDirectory;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Lock _lock = new();

    private string _backendState = PictureBackend.Unknown;
    private MemberPicture? _server;
    private OutboxReply? _pending;
    private string? _widgetFile;
    private string? _pendingFile;
    private bool _needsFiles = true;
    private int _failures;
    private DateTimeOffset _retryAt = DateTimeOffset.MinValue;
    private PictureStatus _status = new();
    private string _fingerprint = "";

    public PictureSync(BackendClient backend, MessageOutbox outbox, IWidgetFolder widget, ILogger<PictureSync> logger,
        string? dataDirectory = null, Func<DateTimeOffset>? clock = null)
    {
        _backend = backend;
        _outbox = outbox;
        _widget = widget;
        _logger = logger;
        _dataDirectory = dataDirectory;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        if (LoadState() is { } saved)
        {
            _backendState = saved.Backend is PictureBackend.On or PictureBackend.Off ? saved.Backend : PictureBackend.Unknown;
            _server = saved.Picture is { } p ? MemberPictures.Read(JsonSerializer.SerializeToElement(p, PictureJsonContext.Default.SavedPicture), out _) : null;
        }
        Publish();
    }

    /// <summary>What the tray gets on the status.</summary>
    public PictureStatus Status => Volatile.Read(ref _status);

    /// <summary>True when a change waits to be sent or a photo to be fetched, and no backoff is running (sync at once).</summary>
    public bool HasWork
    {
        get
        {
            lock (_lock)
            {
                return (_pending is not null || _needsFiles) && _clock() >= _retryAt;
            }
        }
    }

    /// <summary>The backend's word on the member's picture, from a heartbeat reply (<see cref="HeartbeatResponse.Picture"/>).</summary>
    public void OnHeartbeat(JsonElement picture)
    {
        var value = MemberPictures.Read(picture, out var present);
        lock (_lock)
        {
            var before = _backendState;
            if (!present)
            {
                _backendState = PictureBackend.Off;
            }
            else
            {
                _backendState = PictureBackend.On;
                if (_server != value)
                {
                    _server = value;
                    _needsFiles = true;
                    _retryAt = DateTimeOffset.MinValue;
                }
            }
            if (before != _backendState && _backendState == PictureBackend.Off)
            {
                _logger.LogInformation("This backend has no team pictures: the tray hides Change picture");
            }
            SaveState();
            Publish();
        }
    }

    /// <summary>
    /// The member picked a picture in the tray (pipe "picture"). Queued in the outbox and shown on the status at once;
    /// the chat loop sends it. Refused here only when it can't be valid or can't be kept.
    /// </summary>
    public PipeProtocol.EnrollAck Request(PictureChoice? choice)
    {
        if (Status.Backend == PictureBackend.Off)
        {
            return new(false, "Pictures aren't available on your company's Zinwe yet.");
        }
        string body;
        byte[]? jpeg = null;
        switch (choice?.Action)
        {
            case PictureChoice.Avatar when MemberPictures.IsValidKey(choice.Key):
                body = choice.Key!;
                break;
            case PictureChoice.Photo when DecodeJpeg(choice.Jpeg) is { } bytes:
                jpeg = bytes;
                body = choice.Jpeg!;
                break;
            case PictureChoice.Remove:
                body = "";
                break;
            default:
                return new(false, "That picture can't be used. Try another one.");
        }
        if (_outbox.ReplacePicture(choice.Action, body) is not { } item)
        {
            return new(false, "Your picture couldn't be saved on this PC. Try again.");
        }
        var pendingFile = jpeg is null ? null : WriteWidgetCopy(PendingFileName(item.Id), jpeg);
        lock (_lock)
        {
            _pending = item;
            _pendingFile = pendingFile;
            _failures = 0;
            _retryAt = DateTimeOffset.MinValue;
            Publish();
        }
        return new(true, "saved");
    }

    public async Task SyncAsync(string deviceToken, CancellationToken ct)
    {
        try
        {
            await DrainAsync(deviceToken, ct);
            await EnsureFilesAsync(ct);
            lock (_lock)
            {
                _failures = 0;
            }
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            lock (_lock)
            {
                _failures++;
                var wait = TimeSpan.FromSeconds(Math.Min(MaxRetry.TotalSeconds, FirstRetry.TotalSeconds * Math.Pow(2, _failures - 1)));
                _retryAt = _clock() + wait;
            }
            _logger.LogDebug("Picture sync deferred: {Error}", e.Message);
        }
        catch (DeviceRejectedException)
        {
            // The heartbeat owns "removed" (it wipes the identity and calls Clear).
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("Could not keep the member's picture on disk: {Error}", e.Message);
        }
    }

    /// <summary>The PC was removed or connected as someone else: forget the old member's picture.</summary>
    public void Clear()
    {
        _outbox.RemovePictures();
        lock (_lock)
        {
            _backendState = PictureBackend.Unknown;
            _server = null;
            _pending = null;
            _pendingFile = null;
            _widgetFile = null;
            _needsFiles = true;
            _failures = 0;
            _retryAt = DateTimeOffset.MinValue;
            DeleteState();
            DeleteCacheExcept(null);
            Publish();
        }
        CleanWidgetFolder(keep: []);
    }

    /// <summary>Send the newest waiting change, if any and if the backoff allows.</summary>
    private async Task DrainAsync(string deviceToken, CancellationToken ct)
    {
        var item = _outbox.PendingPicture();
        // Queued before this start (after a restart): a waiting photo shows from its own copy, even while offline.
        var adoptedFile = item is { Action: PictureChoice.Photo } && item.Id != _pending?.Id ? PendingCopy(item) : null;
        lock (_lock)
        {
            if (item?.Id != _pending?.Id)
            {
                _pending = item; // adopted, or taken away
                _pendingFile = adoptedFile;
                Publish();
            }
            if (item is null || _clock() < _retryAt)
            {
                return;
            }
        }
        var photo = item.Action == PictureChoice.Photo ? DecodeJpeg(item.Body) : null;
        PictureResult? result = item.Action switch
        {
            PictureChoice.Avatar when MemberPictures.IsValidKey(item.Body) => await _backend.SetAvatarAsync(deviceToken, item.Body, ct),
            PictureChoice.Photo when photo is not null => await _backend.SetPhotoAsync(deviceToken, photo, ct),
            PictureChoice.Remove => await _backend.RemovePictureAsync(deviceToken, ct),
            _ => null, // nothing any backend could take: drop it
        };
        _outbox.Remove([item.Id]); // stored, refused or unusable: never again (a newer choice keeps its own item)
        lock (_lock)
        {
            switch (result?.Outcome)
            {
                case PictureOutcome.Saved:
                    _backendState = PictureBackend.On;
                    _server = result.Picture ?? item.Action switch
                    {
                        PictureChoice.Avatar => new MemberPicture(MemberPicture.AvatarKind, Key: item.Body),
                        PictureChoice.Remove => null,
                        _ => _server, // a photo without a reply body: the next heartbeat brings its URL
                    };
                    _needsFiles = true;
                    if (item.Action == PictureChoice.Photo && _pendingFile is not null)
                    {
                        _widgetFile = _pendingFile; // the same picture: shown until the stored one is downloaded
                    }
                    _logger.LogInformation("The member's picture was saved ({Action}, {Bytes} bytes sent)", item.Action,
                        photo?.Length ?? 0);
                    break;
                case PictureOutcome.Unsupported:
                    _backendState = PictureBackend.Off;
                    _logger.LogInformation("This backend has no team pictures (404): the tray hides Change picture");
                    break;
                case PictureOutcome.Refused:
                    _logger.LogWarning("The backend refused the member's picture ({Code}: {Detail}); dropping it", result.Code, result.Detail);
                    break;
                default:
                    _logger.LogWarning("Dropping an unusable picture change ({Action})", item.Action);
                    break;
            }
            _pending = null;
            _pendingFile = null;
            SaveState();
            Publish();
        }
    }

    /// <summary>Download the photo once per URL, then make sure the signed-in user's widget folder has its copy.</summary>
    private async Task EnsureFilesAsync(CancellationToken ct)
    {
        MemberPicture? server;
        OutboxReply? pending;
        lock (_lock)
        {
            server = _server;
            pending = _pending;
        }

        string? cache = null;
        if (server is { IsPhoto: true } photo && _dataDirectory is not null)
        {
            cache = Path.Combine(_dataDirectory, CacheFolderName, Hash(photo.Url!) + ".jpg");
            if (!File.Exists(cache))
            {
                var bytes = await _backend.DownloadPictureAsync(photo.Url!, ct);
                if (bytes is null)
                {
                    _logger.LogInformation("The member's photo is gone or not a JPEG; showing initials");
                    cache = null;
                }
                else
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(cache)!);
                    var tmp = cache + ".tmp";
                    await File.WriteAllBytesAsync(tmp, bytes, ct);
                    File.Move(tmp, cache, overwrite: true);
                }
            }
        }
        DeleteCacheExcept(cache);

        // The tray-readable copy, written as the signed-in user (null when nobody is, or there is no photo).
        string? widgetFile = null;
        if (cache is not null)
        {
            var name = Path.GetFileName(cache).Insert(0, WidgetFilePrefix);
            widgetFile = _widget.Use(dir => File.Exists(Path.Combine(dir, name)) ? Path.Combine(dir, name) : null, null)
                ?? WriteWidgetCopy(name, await File.ReadAllBytesAsync(cache, ct));
        }
        // A photo still waiting to be sent shows from its own copy.
        var pendingFile = pending is { Action: PictureChoice.Photo } ? PendingCopy(pending) : null;
        CleanWidgetFolder(keep: [.. new[] { widgetFile, pendingFile }.OfType<string>().Select(f => Path.GetFileName(f))]);

        lock (_lock)
        {
            if (_server == server)
            {
                _widgetFile = widgetFile;
                _needsFiles = false;
            }
            if (_pending?.Id == pending?.Id)
            {
                _pendingFile = pendingFile;
            }
            Publish();
        }
    }

    /// <summary>The status as the tray sees it: the waiting choice when there is one, else the backend's picture.</summary>
    private void Publish()
    {
        lock (_lock)
        {
            string? kind = null, key = null, file = null;
            var pending = _pending;
            if (pending is not null)
            {
                (kind, key, file) = pending.Action switch
                {
                    PictureChoice.Avatar => ((string?)MemberPicture.AvatarKind, (string?)pending.Body, (string?)null),
                    PictureChoice.Photo => (MemberPicture.PhotoKind, null, _pendingFile),
                    _ => (null, null, null),
                };
            }
            else if (_server is { IsAvatar: true } avatar)
            {
                (kind, key) = (MemberPicture.AvatarKind, avatar.Key);
            }
            else if (_server is { IsPhoto: true })
            {
                (kind, file) = (MemberPicture.PhotoKind, _widgetFile);
            }
            var print = string.Join('|', _backendState, kind, key, file, pending is not null);
            var current = Volatile.Read(ref _status);
            var changed = print != _fingerprint;
            _fingerprint = print;
            Volatile.Write(ref _status, new PictureStatus(_backendState, kind, key, file,
                changed ? current.Version + 1 : current.Version, pending is not null));
        }
    }

    private static byte[]? DecodeJpeg(string? base64)
    {
        if (string.IsNullOrEmpty(base64) || base64.Length > (PictureChoice.MaxJpegBytes + 2) / 3 * 4)
        {
            return null;
        }
        try
        {
            var bytes = Convert.FromBase64String(base64);
            return bytes.Length <= PictureChoice.MaxJpegBytes && BackendClient.IsJpeg(bytes) ? bytes : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static string Hash(string url) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(url)))[..16];

    /// <summary>The widget-folder copy of a waiting photo (written when missing). Null when nobody is signed in.</summary>
    private string? PendingCopy(OutboxReply item)
    {
        if (DecodeJpeg(item.Body) is not { } jpeg)
        {
            return null;
        }
        var name = PendingFileName(item.Id);
        return _widget.Use(dir => File.Exists(Path.Combine(dir, name)) ? Path.Combine(dir, name) : null, null)
            ?? WriteWidgetCopy(name, jpeg);
    }

    private static string PendingFileName(string itemId) => WidgetFilePrefix + "p" + itemId[..Math.Min(12, itemId.Length)] + ".jpg";

    /// <summary>Write a copy into the signed-in user's widget folder, as them. Null when nobody is signed in or it failed.</summary>
    private string? WriteWidgetCopy(string name, byte[] bytes) => _widget.Use<string?>(dir =>
    {
        try
        {
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, name);
            var tmp = path + ".tmp";
            File.WriteAllBytes(tmp, bytes);
            File.Move(tmp, path, overwrite: true);
            return path;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug("Could not copy the member's picture to the widget folder: {Error}", e.Message);
            return null;
        }
    }, null);

    private void CleanWidgetFolder(string[] keep) => _widget.Use(dir =>
    {
        try
        {
            if (!Directory.Exists(dir))
            {
                return true;
            }
            foreach (var old in Directory.EnumerateFiles(dir, WidgetFilePrefix + "*"))
            {
                if (!keep.Contains(Path.GetFileName(old), StringComparer.OrdinalIgnoreCase))
                {
                    File.Delete(old);
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // best effort: the next round tries again (the tray may still have one open)
        }
        return true;
    }, false);

    private void DeleteCacheExcept(string? keep)
    {
        if (_dataDirectory is null)
        {
            return;
        }
        try
        {
            var folder = Path.Combine(_dataDirectory, CacheFolderName);
            if (!Directory.Exists(folder))
            {
                return;
            }
            foreach (var old in Directory.EnumerateFiles(folder))
            {
                if (!string.Equals(old, keep, StringComparison.OrdinalIgnoreCase))
                {
                    File.Delete(old);
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // best effort
        }
    }

    // ---- picture.json: the backend's state + picture, so it shows offline after a restart ------------------------

    internal sealed record SavedPicture(string Kind, string? Key = null, string? Url = null, DateTimeOffset? UpdatedAt = null);

    internal sealed record SavedState(string Backend, SavedPicture? Picture);

    private string? StatePath => _dataDirectory is null ? null : Path.Combine(_dataDirectory, StateFileName);

    private SavedState? LoadState()
    {
        try
        {
            return StatePath is { } path && File.Exists(path)
                ? JsonSerializer.Deserialize(File.ReadAllText(path), PictureJsonContext.Default.SavedState)
                : null;
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null; // unreadable: the next heartbeat rebuilds it
        }
    }

    private void SaveState()
    {
        if (StatePath is not { } path)
        {
            return;
        }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var state = new SavedState(_backendState,
                _server is { } p ? new SavedPicture(p.Kind, p.Key, p.Url, p.UpdatedAt) : null);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(state, PictureJsonContext.Default.SavedState));
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug("Could not keep the picture state on disk: {Error}", e.Message);
        }
    }

    private void DeleteState()
    {
        try
        {
            if (StatePath is { } path && File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // best effort
        }
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(PictureSync.SavedState))]
[JsonSerializable(typeof(PictureSync.SavedPicture))]
internal sealed partial class PictureJsonContext : JsonSerializerContext;

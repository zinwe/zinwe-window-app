using System.Text.Json;
using Microsoft.Extensions.Logging;
using Zinwe.Agent.Core.Backend;
using Zinwe.Agent.Core.Ipc;
using Zinwe.Agent.Core.Messages;

namespace Zinwe.Agent.Core.Zingles;

/// <summary>
/// The special-day logo (Zingle) for the window's title bar, kept by the service:
/// <list type="number">
/// <item><see cref="CheckAsync"/> asks the public <c>api/auth/zingle/?for=app</c> (no device token, so it works before
///   the PC is connected): at start-up, about hourly and after a sleep (<c>ZingleWorker</c> in the service);</item>
/// <item>the files are downloaded once per Zingle id + <c>?v=</c> into <c>&lt;DataDirectory&gt;\zingles\</c>
///   (<see cref="ZingleFiles.FileName"/>) and the answer kept in <c>zingle.json</c>, so it shows offline and after a
///   restart without downloading again; older files are deleted;</item>
/// <item>copies go, as the signed-in user, into their widget folder (the tray can't read the SYSTEM-only data folder):
///   <see cref="RefreshWidget"/> makes them for whoever signs in later;</item>
/// <item>a <c>null</c> answer (or a backend without Zingles, 404) clears the cache and the copies: the normal logo.</item>
/// </list>
/// A network or server error keeps what is cached. Never throws to the caller except on cancellation.
/// </summary>
public sealed class ZingleSync
{
    private const string StateFileName = "zingle.json";
    private const string CacheFolderName = "zingles";

    private readonly BackendClient _backend;
    private readonly IWidgetFolder _widget;
    private readonly ILogger _logger;
    private readonly string? _dataDirectory;
    private readonly Lock _lock = new();

    private Zingle? _zingle;
    private string? _stillCache;
    private string? _animatedCache;
    private ZingleStatus? _status;

    public ZingleSync(BackendClient backend, IWidgetFolder widget, ILogger<ZingleSync> logger, string? dataDirectory = null)
    {
        _backend = backend;
        _widget = widget;
        _logger = logger;
        _dataDirectory = dataDirectory;
        if (LoadState() is { } saved && CacheFolder is { } folder && Find(folder, saved.StillFileStem) is { } still
            && IsPng(still))
        {
            _zingle = saved;
            _stillCache = still;
            _animatedCache = saved.AnimatedFileStem is { } stem ? Find(folder, stem) : null;
        }
        RefreshWidget();
    }

    /// <summary>What the tray gets on the status; null = the normal logo.</summary>
    public ZingleStatus? Status => Volatile.Read(ref _status);

    /// <summary>The Zingle the cache holds (null = none).</summary>
    public Zingle? Current
    {
        get
        {
            lock (_lock)
            {
                return _zingle;
            }
        }
    }

    /// <summary>
    /// Ask the backend which Zingle is live and fetch its files when they changed. True when the backend answered
    /// (whatever it said); false when it couldn't be reached (the cached Zingle stays, try again sooner).
    /// </summary>
    public async Task<bool> CheckAsync(CancellationToken ct)
    {
        try
        {
            var live = await _backend.GetZingleAsync(ct);
            if (live is null)
            {
                if (Current is not null)
                {
                    _logger.LogInformation("No Zingle today: the normal logo shows again");
                }
                Apply(null, null, null);
                return true;
            }
            if (CacheFolder is not { } folder)
            {
                return true; // no data folder (tests): nothing to keep
            }
            var still = await EnsureAsync(folder, live.StillFileStem, live.StillUrl, ct);
            if (still is null || !IsPng(still))
            {
                _logger.LogInformation("Zingle {Id}: the still is gone or not a PNG; showing the normal logo", live.Id);
                Apply(null, null, null);
                return true;
            }
            var animated = live.AnimatedUrl is null ? null
                : await EnsureAsync(folder, live.AnimatedFileStem!, live.AnimatedUrl, ct);
            if (live != Current)
            {
                _logger.LogInformation("Zingle {Id} is live ({Alt})", live.Id, live.Alt);
            }
            Apply(live, still, animated);
            return true;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            _logger.LogDebug("Zingle check deferred: {Error}", e.Message);
            return false;
        }
        catch (InvalidOperationException e)
        {
            // An absolute http URL from the server (only https is fetched): treat as no Zingle, quietly.
            _logger.LogWarning("Zingle not used: {Error}", e.Message);
            Apply(null, null, null);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("Could not keep the Zingle on disk: {Error}", e.Message);
            return false;
        }
    }

    /// <summary>
    /// Make sure the signed-in user's widget folder has the copies the status names (a member who signs in after the
    /// check gets them within a minute), and that it holds no other Zingle files. Cheap: file-exists checks.
    /// </summary>
    public void RefreshWidget()
    {
        Zingle? zingle;
        string? still, animated;
        lock (_lock)
        {
            (zingle, still, animated) = (_zingle, _stillCache, _animatedCache);
        }
        ZingleStatus? status = null;
        var copies = new List<string>();
        if (zingle is not null && still is not null && CopyToWidget(still) is { } stillCopy)
        {
            copies.Add(Path.GetFileName(stillCopy));
            var animatedCopy = animated is null ? null : CopyToWidget(animated);
            if (animatedCopy is not null)
            {
                copies.Add(Path.GetFileName(animatedCopy));
            }
            status = new ZingleStatus(zingle.Id, zingle.Alt, stillCopy, animatedCopy);
        }
        _widget.Use(dir =>
        {
            ZingleFiles.DeleteExcept(dir, copies);
            return true;
        }, false);
        Volatile.Write(ref _status, status);
    }

    /// <summary>Keep <paramref name="zingle"/> (or none), delete every other cached file, and update the widget copies.</summary>
    private void Apply(Zingle? zingle, string? still, string? animated)
    {
        lock (_lock)
        {
            (_zingle, _stillCache, _animatedCache) = (zingle, still, animated);
        }
        if (CacheFolder is { } folder)
        {
            ZingleFiles.DeleteExcept(folder, [.. new[] { still, animated }.OfType<string>().Select(f => Path.GetFileName(f))]);
        }
        SaveState(zingle);
        RefreshWidget();
    }

    /// <summary>
    /// The cached file for a URL (<paramref name="stem"/> + the type its bytes have), downloaded only when missing.
    /// Null when the server has no usable file.
    /// </summary>
    private async Task<string?> EnsureAsync(string folder, string stem, string url, CancellationToken ct)
    {
        if (Find(folder, stem) is { } found)
        {
            return found;
        }
        if (await _backend.DownloadZingleFileAsync(url, ct) is not { } file)
        {
            return null;
        }
        var path = Path.Combine(folder, stem + file.Extension);
        Directory.CreateDirectory(folder);
        var tmp = path + ".tmp";
        await File.WriteAllBytesAsync(tmp, file.Bytes, ct);
        File.Move(tmp, path, overwrite: true);
        return path;
    }

    /// <summary>A cached <c>stem.png|.gif|.webp</c> (never a half-written ".tmp"); null when there is none.</summary>
    private static string? Find(string folder, string stem)
    {
        foreach (var ext in (string[])[".png", ".gif", ".webp"])
        {
            var path = Path.Combine(folder, stem + ext);
            if (File.Exists(path))
            {
                return path;
            }
        }
        return null;
    }

    private static bool IsPng(string path) => path.EndsWith(".png", StringComparison.OrdinalIgnoreCase);

    /// <summary>The tray-readable copy in the widget folder, written as the signed-in user when missing. Null when nobody is signed in.</summary>
    private string? CopyToWidget(string cached) => _widget.Use<string?>(dir =>
    {
        try
        {
            var path = Path.Combine(dir, Path.GetFileName(cached));
            if (!File.Exists(path))
            {
                Directory.CreateDirectory(dir);
                var tmp = path + ".tmp";
                File.WriteAllBytes(tmp, File.ReadAllBytes(cached));
                File.Move(tmp, path, overwrite: true);
            }
            return path;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug("Could not copy the Zingle to the widget folder: {Error}", e.Message);
            return null;
        }
    }, null);

    // ---- zingle.json: the endpoint's answer, so the Zingle shows offline and after a restart ----------------------

    private string? CacheFolder => _dataDirectory is null ? null : Path.Combine(_dataDirectory, CacheFolderName);

    private string? StatePath => _dataDirectory is null ? null : Path.Combine(_dataDirectory, StateFileName);

    private Zingle? LoadState()
    {
        try
        {
            return StatePath is { } path && File.Exists(path) ? ZingleJson.Parse(File.ReadAllText(path)) : null;
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            return null; // unreadable: the next check rebuilds it
        }
    }

    private void SaveState(Zingle? zingle)
    {
        if (StatePath is not { } path)
        {
            return;
        }
        try
        {
            if (zingle is null)
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
                return;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, Serialize(zingle));
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug("Could not keep the Zingle state on disk: {Error}", e.Message);
        }
    }

    /// <summary>The same shape as the endpoint's answer, so <see cref="ZingleJson.Parse"/> reads it back.</summary>
    internal static string Serialize(Zingle zingle)
    {
        using var stream = new MemoryStream();
        using (var json = new Utf8JsonWriter(stream))
        {
            json.WriteStartObject();
            json.WriteNumber("id", zingle.Id);
            json.WriteString("animated_url", zingle.AnimatedUrl);
            json.WriteString("still_url", zingle.StillUrl);
            json.WriteString("alt", zingle.Alt);
            json.WriteString("places", zingle.Places);
            json.WriteBoolean("app", true);
            if (zingle.UpdatedAt is { } at)
            {
                json.WriteString("updated_at", at);
            }
            json.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }
}

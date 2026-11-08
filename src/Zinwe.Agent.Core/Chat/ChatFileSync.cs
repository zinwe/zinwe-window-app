using System.Text.Json;
using Microsoft.Extensions.Logging;
using Zinwe.Agent.Core.Backend;
using Zinwe.Agent.Core.Ipc;
using Zinwe.Agent.Core.Messages;

namespace Zinwe.Agent.Core.Chat;

/// <summary>
/// v1.5 · file sharing in chat, on the service (the tray makes no network calls):
/// <list type="number">
/// <item><b>Is it on</b>: <c>GET chat/files/</c> every <see cref="InfoEvery"/> (with the people list's pace). Null = a backend
///   without files (404/405): no attach button, and file lines show their fallback text.</item>
/// <item><b>Send</b>: each outbox item of kind <c>chat_file</c> is read from the tray's copy in the widget folder AS the
///   member (only inside <c>outbox-files\</c>), uploaded, then posted as a line with its <c>file_id</c> on the right route
///   (a group or direct chat, or the admin chat), and the copy is deleted. The stored id is kept on the item, so a network
///   error never uploads twice; an upload older than <see cref="ChatFiles.UploadFresh"/> (the server keeps unattached ones
///   1 hour) or one the server no longer finds is uploaded again once. Network errors, 429 and 5xx back off (15 s doubling to
///   5 min); a 4xx is final: the item stays as Failed with the words to show, for Retry.</item>
/// <item><b>Fetch</b>: a download the tray asked for (pipe "files") is streamed into the service's data folder, then copied
///   AS the member into <c>Downloads\Zinwe\</c> (" (2)" when the name is taken) with the Mark of the Web; an image preview
///   (≤ 5 MB, checked by its bytes) is copied into the widget folder's <c>chat-previews\</c>.</item>
/// </list>
/// Sending and fetching run on loops of their own (an upload never holds up a download, nor the chat). Never throws to the
/// caller except on cancellation.
/// </summary>
public sealed class ChatFileSync
{
    public static readonly TimeSpan InfoEvery = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan InfoRetry = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan FirstRetry = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan MaxRetry = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan SweepEvery = TimeSpan.FromMinutes(30);
    /// <summary>A copy no outbox item points at is left this long (the tray may be writing it), then deleted.</summary>
    public static readonly TimeSpan OrphanAge = TimeSpan.FromHours(6);
    public const int MaxPreviews = 150;
    public static readonly TimeSpan PreviewKeep = TimeSpan.FromDays(ChatFiles.KeepDays + 1);

    private const string DownloadFolder = "chatfiles";

    private readonly BackendClient _backend;
    private readonly ChatFileTransfer _transfer;
    private readonly MessageOutbox _outbox;
    private readonly IWidgetFolder _widget;
    private readonly IDownloadsFolder _downloads;
    private readonly ILogger _logger;
    private readonly string? _dataDirectory;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Lock _lock = new();

    private ChatFilesInfo? _info;
    private DateTimeOffset _nextInfo = DateTimeOffset.MinValue;
    private DateTimeOffset _nextSweep = DateTimeOffset.MinValue;
    private readonly Dictionary<string, (int Failures, DateTimeOffset RetryAt)> _backoff = new(StringComparer.Ordinal);
    private readonly HashSet<string> _reuploaded = new(StringComparer.Ordinal);
    private readonly Dictionary<string, FileState> _fetched = new(StringComparer.Ordinal);
    private readonly Dictionary<string, FileState> _previews = new(StringComparer.Ordinal);
    private readonly List<FileAsk> _asks = [];
    private bool _adminPosted;
    private ChatFilesReply _snapshot = new(Supported: true);
    private string _fingerprint = "";

    public ChatFileSync(BackendClient backend, ChatFileTransfer transfer, MessageOutbox outbox, IWidgetFolder widget, IDownloadsFolder downloads,
        ILogger<ChatFileSync> logger, string? dataDirectory = null, Func<DateTimeOffset>? clock = null)
    {
        _backend = backend;
        _transfer = transfer;
        _outbox = outbox;
        _widget = widget;
        _downloads = downloads;
        _logger = logger;
        _dataDirectory = dataDirectory;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>What the tray gets over the pipe ("files").</summary>
    public ChatFilesReply Snapshot => Volatile.Read(ref _snapshot);

    /// <summary>Goes up on every change of <see cref="Snapshot"/> (on the status as <see cref="AgentStatus.FilesVersion"/>).</summary>
    public int Version => Snapshot.Version;

    /// <summary>True when the send loop has something to do now: the info is due, or a file can be sent.</summary>
    public bool SendDue
    {
        get
        {
            var now = _clock();
            if (now >= _nextInfo)
            {
                return true;
            }
            try
            {
                lock (_lock)
                {
                    return _outbox.PendingFiles().Any(i => !i.Refused && Ready(i.Id, now));
                }
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    /// <summary>True while a download or a preview the tray asked for waits.</summary>
    public bool FetchDue
    {
        get
        {
            lock (_lock)
            {
                return _asks.Count > 0;
            }
        }
    }

    /// <summary>A file line went to the admin chat since the last call: pull the admin chat now (once).</summary>
    public bool TakeAdminPosted()
    {
        lock (_lock)
        {
            var posted = _adminPosted;
            _adminPosted = false;
            return posted;
        }
    }

    /// <summary>
    /// The tray's "files" request: with <paramref name="ask"/>, queue that download or preview (an id that isn't a UUID, or
    /// an action it doesn't know, is ignored); always the current snapshot back.
    /// </summary>
    public ChatFilesReply Ask(FileAsk? ask)
    {
        if (ask is not null && ChatFiles.IsValidId(ask.Id))
        {
            lock (_lock)
            {
                var id = ask.Id.ToLowerInvariant();
                switch (ask.Action)
                {
                    case FileAsk.Download when _fetched.GetValueOrDefault(id)?.State != FileState.Working:
                        _fetched[id] = new FileState(id, FileState.Working);
                        _asks.Add(ask with { Id = id });
                        break;
                    case FileAsk.Preview when !_previews.ContainsKey(id) && ask.Size <= ChatFiles.PreviewMaxBytes:
                        _previews[id] = new FileState(id, FileState.Working);
                        _asks.Add(ask with { Id = id });
                        break;
                }
                PublishLocked();
            }
        }
        return Snapshot;
    }

    /// <summary>The PC was removed or connected as someone else: forget the old member's files (their copies stay theirs).</summary>
    public void Clear()
    {
        lock (_lock)
        {
            _info = null;
            _nextInfo = _nextSweep = DateTimeOffset.MinValue;
            _backoff.Clear();
            _reuploaded.Clear();
            _fetched.Clear();
            _previews.Clear();
            _asks.Clear();
            _adminPosted = false;
            PublishLocked();
        }
        try
        {
            _outbox.Remove([.. _outbox.PendingFiles().Select(i => i.Id)]);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // best effort
        }
        DeleteDataFolder();
    }

    // ---- Send ------------------------------------------------------------------------------------------------------

    /// <summary>The send loop's round: the info when due, then each file that may go now, in order.</summary>
    public async Task SyncSendAsync(string deviceToken, CancellationToken ct)
    {
        var now = _clock();
        try
        {
            if (now >= _nextInfo)
            {
                var info = await _backend.GetChatFilesInfoAsync(deviceToken, ct);
                lock (_lock)
                {
                    if (info is null && _info is not null)
                    {
                        _logger.LogInformation("This backend has no file sharing for the PC: the attach button is hidden");
                    }
                    _info = info;
                    _nextInfo = now + InfoEvery;
                    PublishLocked();
                }
            }
        }
        catch (Exception e) when (IsDeferred(e, ct))
        {
            _nextInfo = now + InfoRetry;
            _logger.LogDebug("File sharing info deferred: {Error}", e.Message);
        }
        catch (DeviceRejectedException)
        {
            return; // the heartbeat owns "removed"
        }

        IReadOnlyList<OutboxReply> pending;
        try
        {
            pending = _outbox.PendingFiles();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return;
        }
        foreach (var item in pending)
        {
            if (item.Refused || !Ready(item.Id, _clock()))
            {
                continue;
            }
            try
            {
                await SendOneAsync(deviceToken, item, ct);
                lock (_lock)
                {
                    _backoff.Remove(item.Id);
                }
            }
            catch (Exception e) when (IsDeferred(e, ct))
            {
                BackOff(item.Id);
                _logger.LogDebug("Sending a file deferred: {Error}", e.Message);
                break; // the network is down: the rest wait too, in order
            }
            catch (DeviceRejectedException)
            {
                return;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                BackOff(item.Id);
                _logger.LogWarning("Could not read a file to send: {Error}", e.Message);
            }
        }
        if (_clock() >= _nextSweep)
        {
            _nextSweep = _clock() + SweepEvery;
            SweepCopies();
            SweepPreviews();
        }
        Publish();
    }

    private bool Ready(string itemId, DateTimeOffset now)
    {
        lock (_lock)
        {
            return !_backoff.TryGetValue(itemId, out var b) || now >= b.RetryAt;
        }
    }

    private void BackOff(string itemId)
    {
        lock (_lock)
        {
            var failures = _backoff.TryGetValue(itemId, out var b) ? b.Failures + 1 : 1;
            var wait = TimeSpan.FromSeconds(Math.Min(MaxRetry.TotalSeconds, FirstRetry.TotalSeconds * Math.Pow(2, failures - 1)));
            _backoff[itemId] = (failures, _clock() + wait);
        }
    }

    private async Task SendOneAsync(string token, OutboxReply item, CancellationToken ct)
    {
        var fileId = item.FileId;
        if (fileId is null || item.UploadedAt is not { } uploaded || _clock() - uploaded > ChatFiles.UploadFresh)
        {
            var (signedIn, stream) = OpenCopy(item.FilePath);
            if (!signedIn)
            {
                BackOff(item.Id); // nobody signed in: their copy can't be read now
                return;
            }
            if (stream is null)
            {
                Fail(item, ChatFiles.FailedText, "its copy is gone");
                return;
            }
            FileUploadResult up;
            await using (stream)
            {
                var max = Math.Min(_info?.MaxBytes ?? ChatFiles.MaxBytes, ChatFiles.MaxBytes);
                if (stream.Length > max)
                {
                    Fail(item, ChatFiles.TooBigText, "too big");
                    return;
                }
                if (stream.Length == 0)
                {
                    Fail(item, ChatFiles.EmptyText, "empty");
                    return;
                }
                up = await _transfer.UploadAsync(token, stream, item.FileName ?? Path.GetFileName(item.FilePath ?? "file"), ct);
            }
            switch (up.Outcome)
            {
                case ChatWriteOutcome.Done:
                    fileId = up.File!.Id;
                    var at = _clock();
                    _outbox.Update(item.Id, i => i with { FileId = fileId, UploadedAt = at });
                    break;
                case ChatWriteOutcome.Off:
                    SetInfo(null);
                    Fail(item, ChatFiles.SharingOffText, "no file sharing on this backend");
                    return;
                default:
                    if (up.Status == 403)
                    {
                        SetInfo((_info ?? new ChatFilesInfo()) with { Allowed = false });
                    }
                    Fail(item, up.Error ?? ChatFiles.FailedText, "upload refused");
                    return;
            }
        }

        FilePostResult result;
        var key = item.ChatKey ?? "";
        switch (ChatKeys.Parse(key))
        {
            case ("conv", var convId):
                result = await _backend.PostConversationFileAsync(token, convId, item.Body, fileId, ct);
                break;
            case ("direct", var memberId):
                var (started, conv) = await _backend.StartDirectAsync(token, memberId, ct);
                result = conv is not null
                    ? await _backend.PostConversationFileAsync(token, conv.Id, item.Body, fileId, ct)
                    : new FilePostResult(started == ChatWriteOutcome.Done ? ChatWriteOutcome.Refused : started);
                break;
            default:
                result = key == OutboxReply.AdminKey
                    ? await _backend.PostAdminFileAsync(token, item.Body, fileId, ct)
                    : new FilePostResult(ChatWriteOutcome.Refused);
                break;
        }

        if (result.Outcome == ChatWriteOutcome.Done)
        {
            _outbox.Remove([item.Id]);
            DeleteCopy(item.FilePath);
            lock (_lock)
            {
                _reuploaded.Remove(item.Id);
                _adminPosted |= key == OutboxReply.AdminKey;
            }
            _logger.LogInformation("Sent a file to {Chat}", key);
            return;
        }
        bool again;
        lock (_lock)
        {
            again = result is { Outcome: ChatWriteOutcome.Refused, Field: "file_id" } && _reuploaded.Add(item.Id);
        }
        if (again)
        {
            // The server no longer has the upload (its hour ran out): upload it again, once, on the next round.
            _outbox.Update(item.Id, i => i with { FileId = null, UploadedAt = null });
            return;
        }
        Fail(item, result.Error ?? (result.Outcome == ChatWriteOutcome.Gone ? "This chat was deleted." : ChatFiles.FailedText),
            "line refused (" + result.Outcome + ")");
    }

    /// <summary>A final refusal: the item stays as Failed (with Retry), its copy too.</summary>
    private void Fail(OutboxReply item, string text, string why)
    {
        _logger.LogWarning("A file for {Chat} wasn't sent: {Why}", item.ChatKey, why);
        try
        {
            _outbox.Update(item.Id, i => i with { Refused = true, Error = text });
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // the next round tries again
        }
    }

    private void SetInfo(ChatFilesInfo? info)
    {
        lock (_lock)
        {
            _info = info;
            PublishLocked();
        }
    }

    /// <summary>
    /// The tray's copy, opened AS the member and only inside the widget folder's <c>outbox-files\</c> (an outbox item is
    /// written by the member's own tray: never trust its path with SYSTEM rights). (false, null) when nobody is signed in;
    /// (true, null) when the copy is gone or outside that folder.
    /// </summary>
    private (bool SignedIn, FileStream? Stream) OpenCopy(string? path) => _widget.Use<(bool, FileStream?)>(dir =>
    {
        if (InsideOutbox(dir, path) is not { } full || !File.Exists(full))
        {
            return (true, null);
        }
        try
        {
            return (true, new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read, 81_920, FileOptions.Asynchronous | FileOptions.SequentialScan));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return (true, null);
        }
    }, (false, null));

    /// <summary>The full path when it is a file in <c>&lt;widget&gt;\outbox-files\&lt;folder&gt;\</c>, else null.</summary>
    internal static string? InsideOutbox(string widgetDir, string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }
        try
        {
            var root = Path.GetFullPath(Path.Combine(widgetDir, ChatFiles.OutboxFolder)).TrimEnd('\\') + "\\";
            var full = Path.GetFullPath(path);
            var parent = Path.GetDirectoryName(full);
            return full.StartsWith(root, StringComparison.OrdinalIgnoreCase) && parent is not null
                && string.Equals(Path.GetDirectoryName(parent)?.TrimEnd('\\') + "\\", root, StringComparison.OrdinalIgnoreCase)
                ? full : null;
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    /// <summary>Delete a sent file's copy and its folder (as the member, only inside outbox-files).</summary>
    private void DeleteCopy(string? path) => _widget.Use(dir =>
    {
        try
        {
            if (InsideOutbox(dir, path) is { } full && Path.GetDirectoryName(full) is { } folder && Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // the tray may still have it open: the sweep takes it later
        }
        return true;
    }, false);

    /// <summary>Copies no item points at any more (sent from another PC session, removed by the member) are deleted.</summary>
    private void SweepCopies()
    {
        HashSet<string> keep;
        try
        {
            keep = [.. _outbox.PendingFiles().Select(i => Path.GetDirectoryName(i.FilePath ?? "") ?? "").Where(p => p.Length > 0)];
        }
        catch (Exception)
        {
            return;
        }
        var now = _clock().UtcDateTime;
        _widget.Use(dir =>
        {
            try
            {
                var root = Path.Combine(dir, ChatFiles.OutboxFolder);
                if (!Directory.Exists(root))
                {
                    return true;
                }
                foreach (var folder in Directory.EnumerateDirectories(root))
                {
                    if (!keep.Contains(folder, StringComparer.OrdinalIgnoreCase) && now - Directory.GetLastWriteTimeUtc(folder) > OrphanAge)
                    {
                        Directory.Delete(folder, recursive: true);
                    }
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // best effort
            }
            return true;
        }, false);
    }

    // ---- Fetch -----------------------------------------------------------------------------------------------------

    /// <summary>The fetch loop's round: every download and preview the tray asked for, in order.</summary>
    public async Task SyncFetchAsync(string deviceToken, CancellationToken ct)
    {
        while (true)
        {
            FileAsk ask;
            lock (_lock)
            {
                if (_asks.Count == 0)
                {
                    return;
                }
                ask = _asks[0];
                _asks.RemoveAt(0);
            }
            FileState state;
            try
            {
                state = ask.Action == FileAsk.Preview ? await PreviewAsync(deviceToken, ask, ct) : await DownloadAsync(deviceToken, ask, ct);
            }
            catch (Exception e) when (IsDeferred(e, ct))
            {
                _logger.LogDebug("A file download failed: {Error}", e.Message);
                state = new FileState(ask.Id, FileState.Failed, Error: ChatFiles.DownloadFailedText);
            }
            catch (DeviceRejectedException)
            {
                state = new FileState(ask.Id, FileState.Failed, Error: ChatFiles.DownloadFailedText);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning("Could not save a file from the chat: {Error}", e.Message);
                state = new FileState(ask.Id, FileState.Failed, Error: ChatFiles.DownloadFailedText);
            }
            lock (_lock)
            {
                if (ask.Action == FileAsk.Preview)
                {
                    _previews[ask.Id] = state;
                }
                else
                {
                    _fetched[ask.Id] = state;
                }
                PublishLocked();
            }
        }
    }

    /// <summary>The file into the data folder, then copied as the member into Downloads\Zinwe (marked as from the internet).</summary>
    private async Task<FileState> DownloadAsync(string token, FileAsk ask, CancellationToken ct)
    {
        var folder = Path.Combine(_dataDirectory ?? Path.GetTempPath(), DownloadFolder);
        Directory.CreateDirectory(folder);
        var part = Path.Combine(folder, ask.Id + ".part");
        try
        {
            FileFetchResult result;
            await using (var target = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 81_920, FileOptions.Asynchronous))
            {
                // A little over the limit: the server's own limit decides, this only stops a runaway reply.
                result = await _transfer.DownloadAsync(token, ask.Id, inline: false, ChatFiles.MaxBytes + (1024 * 1024), target, ct);
            }
            switch (result.Outcome)
            {
                case FileFetchOutcome.Gone:
                    return new FileState(ask.Id, FileState.Gone, Error: ChatFiles.ExpiredText);
                case FileFetchOutcome.Done:
                    break;
                default:
                    return new FileState(ask.Id, FileState.Failed, Error: ChatFiles.DownloadFailedText);
            }
            var name = ChatFiles.SafeName(result.Name ?? ask.Name);
            // Opened as the service (the data folder is SYSTEM's), written as the member (their Downloads).
            await using var source = new FileStream(part, FileMode.Open, FileAccess.Read, FileShare.Read);
            var saved = _downloads.Use<string?>(downloads => ChatFiles.SaveNew(Path.Combine(downloads, ChatFiles.DownloadsSubfolder), name, source), null);
            if (saved is null)
            {
                return new FileState(ask.Id, FileState.Failed, Error: ChatFiles.DownloadFailedText);
            }
            _logger.LogInformation("Saved a file from the chat to the member's Downloads");
            return new FileState(ask.Id, FileState.Done, saved);
        }
        finally
        {
            try
            {
                File.Delete(part);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // the next download of it overwrites it
            }
        }
    }

    /// <summary>An image (≤ 5 MB, a real jpg/png/gif/webp) into the widget folder's chat-previews, for the thumbnail.</summary>
    private async Task<FileState> PreviewAsync(string token, FileAsk ask, CancellationToken ct)
    {
        var have = _widget.Use(dir =>
        {
            var folder = Path.Combine(dir, ChatFiles.PreviewFolder);
            return Directory.Exists(folder) ? Directory.EnumerateFiles(folder, ask.Id + ".*").FirstOrDefault() : null;
        }, null);
        if (have is not null)
        {
            return new FileState(ask.Id, FileState.Done, have);
        }
        using var buffer = new MemoryStream();
        var result = await _transfer.DownloadAsync(token, ask.Id, inline: true, ChatFiles.PreviewMaxBytes, buffer, ct);
        if (result.Outcome == FileFetchOutcome.Gone)
        {
            return new FileState(ask.Id, FileState.Gone);
        }
        var bytes = buffer.ToArray();
        if (result.Outcome != FileFetchOutcome.Done || ChatFiles.SniffImage(bytes) is not { } ext)
        {
            return new FileState(ask.Id, FileState.Failed); // the card shows without a thumbnail
        }
        var path = _widget.Use<string?>(dir =>
        {
            var folder = Path.Combine(dir, ChatFiles.PreviewFolder);
            Directory.CreateDirectory(folder);
            var file = Path.Combine(folder, ask.Id + ext);
            var tmp = file + ".tmp";
            File.WriteAllBytes(tmp, bytes);
            File.Move(tmp, file, overwrite: true);
            return file;
        }, null);
        return path is null ? new FileState(ask.Id, FileState.Failed) : new FileState(ask.Id, FileState.Done, path);
    }

    /// <summary>Previews older than the files themselves, or past the newest <see cref="MaxPreviews"/>, are deleted.</summary>
    private void SweepPreviews()
    {
        var now = _clock().UtcDateTime;
        _widget.Use(dir =>
        {
            try
            {
                var folder = Path.Combine(dir, ChatFiles.PreviewFolder);
                if (!Directory.Exists(folder))
                {
                    return true;
                }
                var files = new DirectoryInfo(folder).EnumerateFiles().OrderByDescending(f => f.LastWriteTimeUtc).ToList();
                for (var i = 0; i < files.Count; i++)
                {
                    if (i >= MaxPreviews || now - files[i].LastWriteTimeUtc > PreviewKeep)
                    {
                        files[i].Delete();
                    }
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // the tray may have one open: next time
            }
            return true;
        }, false);
        lock (_lock)
        {
            foreach (var gone in _previews.Where(p => p.Value.State == FileState.Done && p.Value.Path is { } f && !File.Exists(f)).Select(p => p.Key).ToList())
            {
                _previews.Remove(gone);
            }
        }
    }

    private void DeleteDataFolder()
    {
        if (_dataDirectory is null)
        {
            return;
        }
        try
        {
            var folder = Path.Combine(_dataDirectory, DownloadFolder);
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // best effort
        }
    }

    // ---- Snapshot --------------------------------------------------------------------------------------------------

    private void Publish()
    {
        lock (_lock)
        {
            PublishLocked();
        }
    }

    private void PublishLocked()
    {
        var reply = new ChatFilesReply(true, _info, [.. _fetched.Values.OrderBy(s => s.Id, StringComparer.Ordinal)],
            [.. _previews.Values.OrderBy(s => s.Id, StringComparer.Ordinal)]);
        var print = JsonSerializer.Serialize(reply, PipeJsonContext.Default.ChatFilesReply);
        var current = Volatile.Read(ref _snapshot);
        var changed = print != _fingerprint;
        _fingerprint = print;
        Volatile.Write(ref _snapshot, reply with { Version = changed ? current.Version + 1 : current.Version });
    }

    private static bool IsDeferred(Exception e, CancellationToken ct) =>
        e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested;
}

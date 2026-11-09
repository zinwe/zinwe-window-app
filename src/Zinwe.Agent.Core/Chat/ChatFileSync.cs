using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Zinwe.Agent.Core.Backend;
using Zinwe.Agent.Core.Ipc;
using Zinwe.Agent.Core.Messages;

namespace Zinwe.Agent.Core.Chat;

/// <summary>
/// v1.5 round 2 · file sharing in chat on the service (the tray makes no network calls). Files stay on this PC:
/// <list type="number">
/// <item><b>Is it on</b>: <c>GET chat/files/</c> every <see cref="InfoEvery"/>, and a serve ticket to know the backend has the
///   relay (round 1 and older don't: no attach button). Null info = a backend without files: file lines show their fallback text.</item>
/// <item><b>Send</b>: each outbox item of kind <c>chat_file</c> (the member's own path, or our copy of a file on a network or
///   removable drive) is checked AS the member (size, last write), registered (JSON, the tray's thumbnail with it), put on
///   this PC's list (<see cref="SharedFileStore"/>), then posted as a line with its <c>file_id</c>. The id is kept on the
///   item, so a network error never registers twice. Network errors, 429 and 5xx back off (15 s doubling to 5 min); a 4xx
///   is final: the item stays as Failed with the words to show, for Retry.</item>
/// <item><b>Hold</b>: the sha256 is made in the background and PATCHed; "Stop sharing" and "gone" are told to the server; a
///   file goes from the list after 30 days.</item>
/// <item><b>Serve</b> (<see cref="ServeAsync"/>): while it holds any file, one long-poll on the relay; on a job, the size and
///   last write are checked again (a mismatch: PATCH gone, no push), then the file is PUT from the asked offset, at most
///   <see cref="ChatFiles.MaxPushes"/> at once. A receiver that goes away ends its push.</item>
/// <item><b>Receive</b>: a download the tray asked for gets a ticket, waits while the sender's PC is off ("Waiting for
///   Priya's PC…", every 30 s for up to 24 h), then GETs from the relay into a .part next to where it ends up (resumed with
///   Range), checks the size (and the sha256 when one is sent), moves it into place without overwriting (" (2)"), and adds
///   the Mark of the Web. All of it AS the member. Thumbnails go into the widget folder's chat-previews.</item>
/// </list>
/// Never throws to the caller except on cancellation.
/// </summary>
public sealed class ChatFileSync
{
    public static readonly TimeSpan InfoEvery = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan InfoRetry = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan FirstRetry = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan MaxRetry = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan SweepEvery = TimeSpan.FromMinutes(30);
    public const int MaxPreviews = 150;
    public static readonly TimeSpan PreviewKeep = TimeSpan.FromDays(ChatFiles.KeepDays + 1);
    /// <summary>A download that fails this often in a row without getting further stops (Download again resumes it).</summary>
    public const int MaxNetworkTries = 5;

    private readonly BackendClient _backend;
    private readonly ChatFileTransfer _transfer;
    private readonly MessageOutbox _outbox;
    private readonly IWidgetFolder _widget;
    private readonly IUserFiles _user;
    private readonly ILogger _logger;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly string _sharedRoot;
    private readonly Lock _lock = new();
    private readonly SemaphoreSlim _pushGate = new(ChatFiles.MaxPushes);
    private readonly SemaphoreSlim _downloadGate = new(ChatFiles.MaxDownloads);
    private readonly SemaphoreSlim _wake = new(0);

    private ChatFilesInfo? _info;
    private ServeTicket? _serve;
    private IReadOnlyList<SharedFile>? _serverShared;
    private readonly HashSet<string> _forgotten = new(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset _nextInfo = DateTimeOffset.MinValue;
    private DateTimeOffset _nextSweep = DateTimeOffset.MinValue;
    private readonly Dictionary<string, (int Failures, DateTimeOffset RetryAt)> _backoff = new(StringComparer.Ordinal);
    private readonly HashSet<string> _reregistered = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Download> _downloads = new(StringComparer.Ordinal);
    private readonly List<Download> _starting = [];
    private readonly Dictionary<string, FileState> _previews = new(StringComparer.Ordinal);
    private readonly List<FileAsk> _previewAsks = [];
    private readonly Dictionary<string, List<CancellationTokenSource>> _pushing = new(StringComparer.Ordinal);
    private readonly List<Task> _work = [];
    private Task? _hashing;
    private bool _adminPosted;
    private ChatFilesReply _snapshot = new(Supported: true);
    private string _fingerprint = "";
    private DateTimeOffset _lastProgress;

    private sealed class Download(FileAsk ask, DateTimeOffset askedAt, CancellationTokenSource cts)
    {
        public FileAsk Ask { get; } = ask;
        public DateTimeOffset AskedAt { get; } = askedAt;
        public CancellationTokenSource Cts { get; } = cts;
        public FileState State { get; set; } = new(ask.Id, FileState.Working, Total: ask.Size);
    }

    public ChatFileSync(BackendClient backend, ChatFileTransfer transfer, MessageOutbox outbox, IWidgetFolder widget, IUserFiles user,
        ILogger<ChatFileSync> logger, string? dataDirectory = null, Func<DateTimeOffset>? clock = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null, string? sharedRoot = null)
    {
        _backend = backend;
        _transfer = transfer;
        _outbox = outbox;
        _widget = widget;
        _user = user;
        _logger = logger;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _delay = delay ?? Task.Delay;
        _sharedRoot = sharedRoot ?? SharedCopies.Root;
        Store = new SharedFileStore(dataDirectory);
    }

    /// <summary>The files this PC holds.</summary>
    public SharedFileStore Store { get; }

    /// <summary>What the tray gets over the pipe ("files").</summary>
    public ChatFilesReply Snapshot => Volatile.Read(ref _snapshot);

    /// <summary>Goes up on every change of <see cref="Snapshot"/> (on the status as <see cref="AgentStatus.FilesVersion"/>).</summary>
    public int Version => Snapshot.Version;

    /// <summary>True when the send loop has something to do now: the info is due, a file can be sent, or the list has news for the server.</summary>
    public bool SendDue
    {
        get
        {
            var now = _clock();
            if (now >= _nextInfo || now >= _nextSweep)
            {
                return true;
            }
            try
            {
                return _outbox.PendingFiles().Any(i => !i.Refused && Ready(i.Id, now))
                    || Store.All().Any(e => e.StopAsked || !e.StateSent || (e.Sha256 is not null && !e.HashSent) || (e.Active && e.Sha256 is null && _hashing is not { IsCompleted: false }));
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    /// <summary>True while a download or a preview the tray asked for waits to start.</summary>
    public bool FetchDue
    {
        get
        {
            lock (_lock)
            {
                return _starting.Count > 0 || _previewAsks.Count > 0;
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
    /// The tray's "files" request: with <paramref name="ask"/>, queue that download or preview, cancel a download, or stop
    /// sharing a file (an id that isn't a UUID, or an action it doesn't know, is ignored); always the current snapshot back.
    /// </summary>
    public ChatFilesReply Ask(FileAsk? ask)
    {
        if (ask is not null && ChatFiles.IsValidId(ask.Id))
        {
            var id = ask.Id.ToLowerInvariant();
            ask = ask with { Id = id };
            lock (_lock)
            {
                switch (ask.Action)
                {
                    case FileAsk.Download when ValidTarget(ask) && _downloads.GetValueOrDefault(id)?.State.Busy != true:
                        var download = new Download(ask, _clock(), new CancellationTokenSource());
                        _downloads[id] = download;
                        _starting.Add(download);
                        break;
                    case FileAsk.Cancel when _downloads.GetValueOrDefault(id) is { } running:
                        running.Cts.Cancel();
                        _starting.Remove(running);
                        running.State = running.State with { State = FileState.Cancelled };
                        break;
                    case FileAsk.Preview when !_previews.ContainsKey(id) && (!ask.Legacy || ask.Size <= ChatFiles.PreviewMaxBytes):
                        _previews[id] = new FileState(id, FileState.Working);
                        _previewAsks.Add(ask);
                        break;
                    case FileAsk.Stop when Store.Get(id) is { } held:
                        Store.Update(held.Id, e => e with { StopAsked = true });
                        foreach (var push in _pushing.GetValueOrDefault(held.Id) ?? [])
                        {
                            push.Cancel(); // a push under way ends now
                        }
                        break;
                }
                PublishLocked();
            }
        }
        return Snapshot;
    }

    /// <summary>A download goes into a folder or to a path the member chose: both must be full local paths.</summary>
    private static bool ValidTarget(FileAsk ask)
    {
        static bool Full(string? p) => p is { Length: > 3 } && Path.IsPathFullyQualified(p) && !p.StartsWith(@"\\?\", StringComparison.Ordinal)
            && !p.StartsWith(@"\\.\", StringComparison.Ordinal) && p.IndexOfAny(['*', '?', '"', '<', '>', '|']) < 0;
        return ask.Target is not null ? Full(ask.Target) && ask.Folder is null : Full(ask.Folder);
    }

    /// <summary>The PC was removed or connected as someone else: forget the old member's files (their copies go too).</summary>
    public void Clear()
    {
        List<Download> downloads;
        lock (_lock)
        {
            _info = null;
            _serve = null;
            _serverShared = null;
            _forgotten.Clear();
            _nextInfo = _nextSweep = DateTimeOffset.MinValue;
            _backoff.Clear();
            _reregistered.Clear();
            downloads = [.. _downloads.Values];
            _downloads.Clear();
            _starting.Clear();
            _previews.Clear();
            _previewAsks.Clear();
            foreach (var push in _pushing.Values.SelectMany(p => p))
            {
                push.Cancel();
            }
            _adminPosted = false;
        }
        foreach (var d in downloads)
        {
            d.Cts.Cancel();
        }
        foreach (var e in Store.All().Where(e => e.Copied))
        {
            DeleteCopy(e.LocalRef);
        }
        Store.Clear();
        try
        {
            _outbox.Remove([.. _outbox.PendingFiles().Select(i => i.Id)]);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // best effort
        }
        Publish();
    }

    // ---- Send ------------------------------------------------------------------------------------------------------

    /// <summary>The send loop's round: the info when due, each file that may go now (in order), then the list's chores.</summary>
    public async Task SyncSendAsync(string deviceToken, CancellationToken ct)
    {
        var now = _clock();
        try
        {
            if (now >= _nextInfo)
            {
                await RefreshInfoAsync(deviceToken, now, ct);
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
            pending = [];
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

        try
        {
            await TellServerAsync(deviceToken, ct);
        }
        catch (Exception e) when (IsDeferred(e, ct))
        {
            _logger.LogDebug("File news for the server deferred: {Error}", e.Message);
        }
        catch (DeviceRejectedException)
        {
            return;
        }
        StartHashing();
        if (_clock() >= _nextSweep)
        {
            _nextSweep = _clock() + SweepEvery;
            foreach (var old in Store.TakeOld(_clock()))
            {
                if (old.Copied)
                {
                    DeleteCopy(old.LocalRef);
                }
            }
            SweepPreviews();
        }
        Publish();
    }

    private async Task RefreshInfoAsync(string token, DateTimeOffset now, CancellationToken ct)
    {
        var info = await _backend.GetChatFilesInfoAsync(token, ct);
        ServeTicket? serve = null;
        if (info is not null)
        {
            // Round 2 hands out serve tickets; round 1 (server copies) has no such route.
            serve = await _backend.GetServeTicketAsync(token, ct);
            info = info with { Relay = serve is not null };
        }
        // The server's own "My shared files" (with each chat's name, and files shared from the member's other PCs).
        var shared = info is null ? null : await _backend.GetSharedFilesAsync(token, ct);
        if (shared is not null && shared.Count < 200)
        {
            // A file this PC holds that the server no longer lists (stopped elsewhere, deleted with its line): let it go.
            var listed = shared.Select(f => f.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var e in Store.All().Where(e => e.Active && !listed.Contains(e.Id) && now - e.CreatedAt > TimeSpan.FromHours(1)))
            {
                Forget(e);
            }
        }
        lock (_lock)
        {
            _serverShared = shared;
            if (info is null && _info is not null)
            {
                _logger.LogInformation("This backend has no file sharing for the PC: the attach button is hidden");
            }
            _info = info;
            _serve = serve ?? (info is null ? null : _serve);
            _nextInfo = now + InfoEvery;
            PublishLocked();
        }
        Wake();
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

    /// <summary>The file as the member sees it now: (signed in, exists, size, last write).</summary>
    private (bool SignedIn, bool Exists, long Size, DateTime LastWriteUtc) Stat(string? path) =>
        _user.AsUser(() =>
        {
            if (!LocalPath(path))
            {
                return (true, false, 0L, default(DateTime));
            }
            try
            {
                var info = new FileInfo(path!);
                return info.Exists ? (true, true, info.Length, info.LastWriteTimeUtc) : (true, false, 0L, default);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                return (true, false, 0L, default);
            }
        }, (false, false, 0L, default));

    private static bool LocalPath(string? path) =>
        path is { Length: > 3 } && Path.IsPathFullyQualified(path) && !path.StartsWith(@"\\?\", StringComparison.Ordinal)
        && !path.StartsWith(@"\\.\", StringComparison.Ordinal);

    private async Task SendOneAsync(string token, OutboxReply item, CancellationToken ct)
    {
        var fileId = item.FileId;
        if (fileId is null)
        {
            if (_info is not { Relay: true } info)
            {
                BackOff(item.Id); // not known yet (or a backend without the relay): wait
                return;
            }
            var (signedIn, exists, size, lastWrite) = Stat(item.FilePath);
            if (!signedIn)
            {
                BackOff(item.Id); // nobody signed in: their file can't be looked at now
                return;
            }
            if (!exists)
            {
                Fail(item, ChatFiles.MissingText, "the file is gone");
                return;
            }
            if (size == 0)
            {
                Fail(item, ChatFiles.EmptyText, "empty");
                return;
            }
            if (size > info.MaxBytes)
            {
                Fail(item, ChatFiles.TooBigText(info.MaxBytes), "too big for the plan");
                return;
            }
            var name = ChatFiles.SafeName(item.FileName ?? Path.GetFileName(item.FilePath));
            var reg = await _backend.RegisterFileAsync(token, new FileRegisterRequest(name, size, item.Mime ?? ChatFiles.MimeOf(name),
                item.FilePath!, null, item.ThumbB64, item.Width, item.Height, item.DurationS, item.BatchId), ct);
            switch (reg.Outcome)
            {
                case ChatWriteOutcome.Done:
                    fileId = reg.File!.Id.ToLowerInvariant();
                    var at = _clock();
                    Store.Add(new SharedEntry(fileId, name, size, item.FilePath!, lastWrite, item.ChatKey, at, item.Copied,
                        UserSid: _user.CurrentUser()));
                    _outbox.Update(item.Id, i => i with { FileId = fileId, UploadedAt = at });
                    Wake();
                    break;
                case ChatWriteOutcome.Off:
                    SetInfo(null);
                    Fail(item, ChatFiles.SharingOffText, "no file sharing on this backend");
                    return;
                default:
                    if (reg.Status == 403)
                    {
                        SetInfo(info with { Allowed = false });
                    }
                    Fail(item, reg.Error ?? ChatFiles.FailedText, "registration refused");
                    return;
            }
        }

        FilePostResult result;
        var key = item.ChatKey ?? "";
        var caption = item.Body ?? "";
        switch (ChatKeys.Parse(key))
        {
            case ("conv", var convId):
                result = await _backend.PostConversationFileAsync(token, convId, caption, fileId, ct);
                break;
            case ("direct", var memberId):
                var (started, conv) = await _backend.StartDirectAsync(token, memberId, ct);
                result = conv is not null
                    ? await _backend.PostConversationFileAsync(token, conv.Id, caption, fileId, ct)
                    : new FilePostResult(started == ChatWriteOutcome.Done ? ChatWriteOutcome.Refused : started);
                break;
            default:
                result = key == OutboxReply.AdminKey
                    ? await _backend.PostAdminFileAsync(token, caption, fileId, ct)
                    : new FilePostResult(ChatWriteOutcome.Refused);
                break;
        }

        if (result.Outcome == ChatWriteOutcome.Done)
        {
            _outbox.Remove([item.Id]);
            lock (_lock)
            {
                _reregistered.Remove(item.Id);
                _backoff.Remove(item.Id);
                _adminPosted |= key == OutboxReply.AdminKey;
            }
            _logger.LogInformation("Shared a file in {Chat}", key);
            return;
        }
        bool again;
        lock (_lock)
        {
            again = result is { Outcome: ChatWriteOutcome.Refused, Field: "file_id" } && _reregistered.Add(item.Id);
        }
        if (again)
        {
            // The server no longer has the registration: register it again, once, on the next round.
            Store.Remove(fileId);
            _outbox.Update(item.Id, i => i with { FileId = null, UploadedAt = null });
            return;
        }
        Fail(item, result.Error ?? (result.Outcome == ChatWriteOutcome.Gone ? "This chat was deleted." : ChatFiles.FailedText),
            "line refused (" + result.Outcome + ")");
    }

    /// <summary>A final refusal: the item stays as Failed (with Retry).</summary>
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

    /// <summary>Stop sharing, "gone" and the hashes, told to the server (each once it is heard).</summary>
    private async Task TellServerAsync(string token, CancellationToken ct)
    {
        foreach (var e in Store.All())
        {
            if (e.StopAsked)
            {
                var stopped = await _backend.StopFileAsync(token, e.Id, ct);
                if (stopped is ChatWriteOutcome.Done or ChatWriteOutcome.Refused or ChatWriteOutcome.Off)
                {
                    Forget(e);
                    _logger.LogInformation("Stopped sharing a file");
                }
            }
            else if (!e.StateSent && e.State == ChatFile.StateGone)
            {
                await _backend.PatchFileAsync(token, e.Id, new FilePatchRequest(Gone: true), ct);
                Forget(e); // the server has its word now; nothing here can be served again
            }
            else if (e is { State: ChatFile.StateOk, Sha256: { } hash, HashSent: false })
            {
                await _backend.PatchFileAsync(token, e.Id, new FilePatchRequest(Sha256: hash), ct);
                Store.Update(e.Id, x => x with { HashSent = true });
            }
        }
    }

    private void Forget(SharedEntry e)
    {
        lock (_lock)
        {
            _forgotten.Add(e.Id); // the server's list may still name it for a few minutes
        }
        Store.Remove(e.Id);
        if (e.Copied)
        {
            DeleteCopy(e.LocalRef);
        }
    }

    /// <summary>Hash one held file in the background (a 5 GB file takes a while), then the send loop PATCHes it.</summary>
    private void StartHashing()
    {
        if (_hashing is { IsCompleted: false })
        {
            return;
        }
        var me = _user.CurrentUser();
        if (Store.All().FirstOrDefault(e => e.Active && e.Sha256 is null && (e.UserSid is null || e.UserSid == me)) is not { } next)
        {
            return;
        }
        _hashing = Task.Run(async () =>
        {
            try
            {
                var hash = await HashAsync(next);
                if (hash is not null)
                {
                    Store.Update(next.Id, x => x with { Sha256 = hash, HashSent = false });
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or CryptographicException)
            {
                _logger.LogDebug("Could not hash a shared file: {Error}", e.Message);
            }
        });
        lock (_lock)
        {
            _work.Add(_hashing);
        }
    }

    /// <summary>The file's sha256 (read AS the member); null when it changed (then it is "gone") or nobody is signed in.</summary>
    internal async Task<string?> HashAsync(SharedEntry e)
    {
        var stream = OpenHeld(e, out var changed);
        if (changed)
        {
            MarkGone(e);
            return null;
        }
        if (stream is null)
        {
            return null;
        }
        await using (stream)
        {
            using var sha = SHA256.Create();
            var hash = await sha.ComputeHashAsync(stream);
            return Convert.ToHexStringLower(hash);
        }
    }

    /// <summary>
    /// The held file opened AS the member, after the holder check: it must be on the list, still shared, held for the member
    /// signed in now, and the same size and last write as when it was registered. <paramref name="changed"/> = it was changed,
    /// moved or deleted (it is then "gone"). Null without a change = can't be served now (nobody signed in, another member).
    /// </summary>
    internal FileStream? OpenHeld(SharedEntry e, out bool changed)
    {
        changed = false;
        if (!e.Active || (e.UserSid is not null && !string.Equals(e.UserSid, _user.CurrentUser(), StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }
        var (signedIn, stream, gone) = _user.AsUser<(bool, FileStream?, bool)>(() =>
        {
            if (!LocalPath(e.LocalRef))
            {
                return (true, null, true);
            }
            FileStream? s = null;
            try
            {
                var info = new FileInfo(e.LocalRef);
                if (!info.Exists || info.Length != e.Size || info.LastWriteTimeUtc != e.LastWriteUtc)
                {
                    return (true, null, true);
                }
                s = new FileStream(e.LocalRef, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 81_920,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                if (s.Length != e.Size)
                {
                    s.Dispose();
                    return (true, null, true);
                }
                return (true, s, false);
            }
            catch (Exception x) when (x is FileNotFoundException or DirectoryNotFoundException)
            {
                s?.Dispose();
                return (true, null, true);
            }
            catch (Exception x) when (x is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                s?.Dispose();
                return (true, null, false); // locked, or not readable now: not served this time
            }
        }, (false, null, false));
        changed = signedIn && gone;
        return stream;
    }

    private void MarkGone(SharedEntry e)
    {
        _logger.LogInformation("A shared file was changed, moved or deleted on this PC: it isn't shared any more");
        Store.Update(e.Id, x => x with { State = ChatFile.StateGone, StateSent = false });
        Publish();
    }

    /// <summary>Delete one of our copies (as the member, only a folder directly under a copies root).</summary>
    private void DeleteCopy(string? path)
    {
        var roots = new List<string> { _sharedRoot };
        if (_widget.Use<string?>(dir => Path.Combine(dir, ChatFiles.FallbackCopiesFolder), null) is { } fallback)
        {
            roots.Add(fallback);
        }
        _user.AsUser(() => SharedCopies.DeleteCopy(path, roots), false);
    }

    // ---- Serve -----------------------------------------------------------------------------------------------------

    /// <summary>
    /// The serve loop (its own task on the service): while this PC holds any file, the backend has the relay and the member
    /// is signed in, one long-poll on <c>/relay/wait</c>; each job is pushed on a task of its own. Returns on cancellation.
    /// </summary>
    public async Task ServeAsync(string deviceToken, CancellationToken ct)
    {
        var failures = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                failures = await ServeOnceAsync(deviceToken, failures, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
        }
    }

    /// <summary>One round of the serve loop; returns the failures in a row so far.</summary>
    internal async Task<int> ServeOnceAsync(string token, int failures, CancellationToken ct)
    {
        if (_info is not { Relay: true } || !Store.AnyActive || _user.CurrentUser() is null)
        {
            await WaitForWorkAsync(TimeSpan.FromSeconds(10), ct);
            return 0;
        }
        try
        {
            var ticket = _serve;
            if (ticket is null || !ticket.FreshAt(_clock()))
            {
                ticket = await _backend.GetServeTicketAsync(token, ct);
                lock (_lock)
                {
                    _serve = ticket;
                }
                if (ticket is null)
                {
                    await WaitForWorkAsync(TimeSpan.FromMinutes(1), ct);
                    return 0;
                }
            }
            var job = await _transfer.WaitAsync(ticket.WaitUrl, ct);
            if (job is not null)
            {
                var task = ServeJobAsync(job, ticket, ct);
                lock (_lock)
                {
                    _work.RemoveAll(t => t.IsCompleted);
                    _work.Add(task);
                }
            }
            return 0;
        }
        catch (TicketRejectedException)
        {
            lock (_lock)
            {
                _serve = null;
            }
            return failures;
        }
        catch (Exception e) when (IsDeferred(e, ct) || e is DeviceRejectedException)
        {
            _logger.LogDebug("The relay wait failed: {Error}", e.Message);
            await _delay(TimeSpan.FromSeconds(Math.Min(60, 5 * Math.Pow(2, failures))), ct);
            return failures + 1;
        }
    }

    /// <summary>
    /// A receiver waits for <paramref name="job"/>: the holder check (on the list, still shared, same size and last write;
    /// else "gone" and no push), then the file from the job's offset to the relay. At most 3 at once.
    /// </summary>
    internal async Task ServeJobAsync(RelayJob job, ServeTicket ticket, CancellationToken ct)
    {
        await _pushGate.WaitAsync(ct);
        CancellationTokenSource? cts = null;
        try
        {
            if (Store.Get(job.FileId) is not { } entry)
            {
                _logger.LogWarning("The relay asked for a file this PC doesn't hold");
                return;
            }
            var stream = OpenHeld(entry, out var changed);
            if (changed)
            {
                MarkGone(entry);
                return;
            }
            if (stream is null)
            {
                return;
            }
            await using (stream)
            {
                if (job.Offset > entry.Size)
                {
                    return;
                }
                stream.Seek(job.Offset, SeekOrigin.Begin);
                cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                lock (_lock)
                {
                    if (!_pushing.TryGetValue(entry.Id, out var list))
                    {
                        _pushing[entry.Id] = list = [];
                    }
                    list.Add(cts);
                }
                var outcome = await _transfer.PushAsync(ticket.PushUrl(job.Job), stream, entry.Size - job.Offset, cts.Token);
                _logger.LogInformation("A shared file was sent from offset {Offset}: {Outcome}", job.Offset, outcome);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogInformation("Stopped sending a file (sharing stopped)");
        }
        catch (Exception e) when (IsDeferred(e, ct) || e is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug("Sending a shared file failed: {Error}", e.Message);
        }
        finally
        {
            if (cts is not null)
            {
                lock (_lock)
                {
                    foreach (var list in _pushing.Values)
                    {
                        list.Remove(cts);
                    }
                }
                cts.Dispose();
            }
            _pushGate.Release();
        }
    }

    private void Wake()
    {
        if (_wake.CurrentCount == 0)
        {
            _wake.Release();
        }
    }

    private async Task WaitForWorkAsync(TimeSpan most, CancellationToken ct)
    {
        try
        {
            await _wake.WaitAsync(most, ct);
        }
        catch (ObjectDisposedException)
        {
            await _delay(most, ct);
        }
    }

    // ---- Receive ---------------------------------------------------------------------------------------------------

    /// <summary>The fetch loop's round: start each download asked for (on its own task), then fetch the thumbnails in order.</summary>
    public async Task SyncFetchAsync(string deviceToken, CancellationToken ct)
    {
        List<Download> start;
        lock (_lock)
        {
            start = [.. _starting];
            _starting.Clear();
        }
        foreach (var d in start)
        {
            var task = RunDownloadAsync(deviceToken, d, ct);
            lock (_lock)
            {
                _work.RemoveAll(t => t.IsCompleted);
                _work.Add(task);
            }
        }
        while (true)
        {
            FileAsk ask;
            lock (_lock)
            {
                if (_previewAsks.Count == 0)
                {
                    return;
                }
                ask = _previewAsks[0];
                _previewAsks.RemoveAt(0);
            }
            FileState state;
            try
            {
                state = await PreviewAsync(deviceToken, ask, ct);
            }
            catch (Exception e) when (IsDeferred(e, ct) || e is DeviceRejectedException or IOException or UnauthorizedAccessException)
            {
                _logger.LogDebug("A thumbnail wasn't fetched: {Error}", e.Message);
                state = new FileState(ask.Id, FileState.Failed);
            }
            lock (_lock)
            {
                _previews[ask.Id] = state;
                PublishLocked();
            }
        }
    }

    /// <summary>Tests: every download, hash and push started so far has finished.</summary>
    internal async Task IdleAsync()
    {
        while (true)
        {
            Task[] running;
            lock (_lock)
            {
                running = [.. _work.Where(t => !t.IsCompleted)];
            }
            if (running.Length == 0)
            {
                return;
            }
            await Task.WhenAll(running.Select(t => t.ContinueWith(_ => { }, TaskScheduler.Default)));
        }
    }

    private async Task RunDownloadAsync(string token, Download d, CancellationToken serviceCt)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(serviceCt, d.Cts.Token);
        var ct = linked.Token;
        var ask = d.Ask;
        string? part = null;
        try
        {
            await _downloadGate.WaitAsync(ct);
            try
            {
                part = ChatFiles.PartPath(ask.Id, ask.Name ?? "file", ask.Folder, ask.Target);
                var final = ask.Legacy ? await LegacyAsync(token, d, part, ct) : await RelayAsync(token, d, part, ct);
                Set(d, final);
            }
            finally
            {
                _downloadGate.Release();
            }
        }
        catch (OperationCanceledException) when (d.Cts.IsCancellationRequested && !serviceCt.IsCancellationRequested)
        {
            if (part is not null)
            {
                DeletePart(part);
            }
            Set(d, d.State with { State = FileState.Cancelled });
        }
        catch (OperationCanceledException) when (serviceCt.IsCancellationRequested)
        {
            // The service stops: the .part stays, and a new ask goes on from it.
        }
        catch (DeviceRejectedException)
        {
            Set(d, d.State with { State = FileState.Failed, Error = ChatFiles.DownloadFailedText });
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or HttpRequestException or InvalidOperationException)
        {
            _logger.LogWarning("A download from the chat failed: {Error}", e.Message);
            Set(d, d.State with { State = FileState.Failed, Error = ChatFiles.DownloadFailedText });
        }
    }

    /// <summary>
    /// Through the relay: a ticket, the sender online (else wait), then the GET into the .part (resumed), until it is all here.
    /// </summary>
    private async Task<FileState> RelayAsync(string token, Download d, string part, CancellationToken ct)
    {
        var ask = d.Ask;
        FileTicket? ticket = null;
        var tries = 0;
        var waitingFor = ask.SenderFirst;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (_clock() - d.AskedAt > ChatFiles.WaitUpTo)
            {
                return d.State with { State = FileState.Failed, Error = ChatFiles.DownloadFailedText };
            }
            try
            {
                if (ticket is null || !ticket.FreshAt(_clock()))
                {
                    var asked = await _backend.GetFileTicketAsync(token, ask.Id, ct);
                    switch (asked.Outcome)
                    {
                        case ChatWriteOutcome.Done:
                            ticket = asked.Ticket!;
                            break;
                        case ChatWriteOutcome.Gone:
                            DeletePart(part);
                            return d.State with { State = FileState.Gone, Error = ChatFiles.NotSharedText };
                        default:
                            return d.State with { State = FileState.Failed, Error = asked.Error ?? ChatFiles.DownloadFailedText };
                    }
                }
                if (await _transfer.OnlineAsync(ticket.StatusUrl, ct) == false)
                {
                    await WaitForSenderAsync(d, waitingFor, ct);
                    continue;
                }
                Set(d, d.State with { State = FileState.Working, WaitingFor = null });
                var target = OpenPart(part);
                if (target is null)
                {
                    return d.State with { State = FileState.Failed, Error = ChatFiles.DownloadFailedText };
                }
                RelayFetchResult got;
                await using (target)
                {
                    var before = target.Length;
                    Progress(d, before);
                    got = await _transfer.FetchAsync(ticket.Url, target, ask.Size > 0 ? ask.Size : long.MaxValue, n => Progress(d, n), ct);
                    if (got.Length > before)
                    {
                        tries = 0;
                    }
                }
                switch (got.Outcome)
                {
                    case RelayFetchOutcome.Offline:
                        await WaitForSenderAsync(d, waitingFor, ct);
                        continue;
                    case RelayFetchOutcome.TicketStale:
                        ticket = null;
                        if (++tries >= MaxNetworkTries)
                        {
                            return d.State with { State = FileState.Failed, Error = ChatFiles.DownloadFailedText };
                        }
                        continue;
                    case RelayFetchOutcome.Gone:
                        DeletePart(part);
                        return d.State with { State = FileState.Gone, Error = ChatFiles.NotSharedText };
                    case RelayFetchOutcome.Refused:
                        DeletePart(part);
                        return d.State with { State = FileState.Failed, Error = ChatFiles.DownloadFailedText };
                }
                if (ask.Size > 0 && got.Length < ask.Size)
                {
                    // The sender's PC stopped mid-way (asleep, offline): go on from here.
                    if (++tries >= MaxNetworkTries)
                    {
                        return d.State with { State = FileState.Failed, Error = ChatFiles.DownloadFailedText };
                    }
                    await _delay(TimeSpan.FromSeconds(5 * tries), ct);
                    continue;
                }
                return await FinishAsync(d, part, ticket.Sha256 ?? ask.Sha256, ct);
            }
            catch (Exception e) when (IsDeferred(e, ct))
            {
                _logger.LogDebug("A download was cut: {Error}", e.Message);
                if (++tries >= MaxNetworkTries)
                {
                    return d.State with { State = FileState.Failed, Error = ChatFiles.DownloadFailedText };
                }
                await _delay(TimeSpan.FromSeconds(5 * tries), ct);
            }
        }
    }

    private async Task WaitForSenderAsync(Download d, string? first, CancellationToken ct)
    {
        Set(d, d.State with { State = FileState.Waiting, WaitingFor = string.IsNullOrWhiteSpace(first) ? null : first });
        await _delay(ChatFiles.RetryEvery, ct);
    }

    /// <summary>Round 1 · the server's copy, from the start (that backend has no Range), then the same finish.</summary>
    private async Task<FileState> LegacyAsync(string token, Download d, string part, CancellationToken ct)
    {
        var target = OpenPart(part);
        if (target is null)
        {
            return d.State with { State = FileState.Failed, Error = ChatFiles.DownloadFailedText };
        }
        FileFetchResult result;
        await using (target)
        {
            target.SetLength(0);
            result = await _transfer.DownloadLegacyAsync(token, d.Ask.Id, inline: false, ChatFiles.LegacyMaxBytes + (1024 * 1024), target,
                n => Progress(d, n), ct);
        }
        switch (result.Outcome)
        {
            case FileFetchOutcome.Done:
                return await FinishAsync(d, part, null, ct);
            case FileFetchOutcome.Gone:
                DeletePart(part);
                return d.State with { State = FileState.Gone, Error = ChatFiles.NotSharedText };
            default:
                DeletePart(part);
                return d.State with { State = FileState.Failed, Error = ChatFiles.DownloadFailedText };
        }
    }

    /// <summary>The .part, opened (made) AS the member in the folder they chose. Null when they can't write there.</summary>
    private FileStream? OpenPart(string part) => _user.AsUser<FileStream?>(() =>
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(part)!);
            return new FileStream(part, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read, 81_920, FileOptions.Asynchronous);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            _logger.LogWarning("Could not write a download where the member chose: {Error}", e.Message);
            return null;
        }
    }, null);

    private void DeletePart(string part) => _user.AsUser(() =>
    {
        try
        {
            if (part.EndsWith(".part", StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(part);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // the next download of it starts over
        }
        return true;
    }, false);

    /// <summary>
    /// All of it is here: the sha256 checked when one is known (a mismatch starts over next time), then the .part moved to
    /// its name (Save as: the chosen path; else the first free name, " (2)"), marked as from the internet.
    /// </summary>
    private async Task<FileState> FinishAsync(Download d, string part, string? sha256, CancellationToken ct)
    {
        if (ChatFiles.NormalHash(sha256) is { } expected)
        {
            var stream = _user.AsUser<FileStream?>(() => new FileStream(part, FileMode.Open, FileAccess.Read, FileShare.Read, 81_920,
                FileOptions.Asynchronous | FileOptions.SequentialScan), null);
            if (stream is null)
            {
                return d.State with { State = FileState.Failed, Error = ChatFiles.DownloadFailedText };
            }
            string actual;
            await using (stream)
            {
                using var sha = SHA256.Create();
                actual = Convert.ToHexStringLower(await sha.ComputeHashAsync(stream, ct));
            }
            if (actual != expected)
            {
                _logger.LogWarning("A download didn't match its sha256: it was deleted");
                DeletePart(part);
                return d.State with { State = FileState.Failed, Error = ChatFiles.DownloadFailedText };
            }
        }
        var saved = _user.AsUser<string?>(() => Place(part, d.Ask), null);
        if (saved is null)
        {
            return d.State with { State = FileState.Failed, Error = ChatFiles.DownloadFailedText };
        }
        _logger.LogInformation("Saved a file from the chat where the member chose");
        return d.State with { State = FileState.Done, Path = saved, Received = d.State.Total > 0 ? d.State.Total : d.State.Received, WaitingFor = null };
    }

    /// <summary>The .part into place (as the member): never over another file unless the member chose that path (Save as).</summary>
    internal static string? Place(string part, FileAsk ask)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var final = ask.Target ?? ChatFiles.FreeName(ask.Folder!, ask.Name ?? "file");
            try
            {
                File.Move(part, final, overwrite: ask.Target is not null);
                ChatFiles.MarkFromInternet(final);
                return final;
            }
            catch (IOException) when (ask.Target is null && File.Exists(final))
            {
                // taken a moment ago: the next free name
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }
        return null;
    }

    private void Set(Download d, FileState state)
    {
        lock (_lock)
        {
            d.State = state;
            if (_downloads.GetValueOrDefault(d.Ask.Id) == d)
            {
                PublishLocked();
            }
        }
    }

    /// <summary>The bytes so far (the snapshot moves at most 4 times a second, or on each whole percent).</summary>
    private void Progress(Download d, long received)
    {
        lock (_lock)
        {
            var before = d.State.Percent;
            d.State = d.State with { Received = received, State = FileState.Working, WaitingFor = null };
            var now = _clock();
            if ((d.State.Percent != before || now - _lastProgress > TimeSpan.FromMilliseconds(250)) && _downloads.GetValueOrDefault(d.Ask.Id) == d)
            {
                _lastProgress = now;
                PublishLocked();
            }
        }
    }

    /// <summary>A thumbnail into the widget folder's chat-previews (round 2: thumb/; round 1: the image itself, ≤ 5 MB).</summary>
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
        byte[]? bytes;
        if (ask.Legacy)
        {
            using var buffer = new MemoryStream();
            var result = await _transfer.DownloadLegacyAsync(token, ask.Id, inline: true, ChatFiles.PreviewMaxBytes, buffer, null, ct);
            if (result.Outcome == FileFetchOutcome.Gone)
            {
                return new FileState(ask.Id, FileState.Gone);
            }
            bytes = result.Outcome == FileFetchOutcome.Done ? buffer.ToArray() : null;
        }
        else
        {
            bytes = await _backend.GetFileThumbAsync(token, ask.Id, ct);
        }
        if (bytes is null || ChatFiles.SniffImage(bytes) is not { } ext)
        {
            return new FileState(ask.Id, FileState.Failed); // the tile shows its icon instead
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

    /// <summary>Thumbnails older than the files themselves, or past the newest <see cref="MaxPreviews"/>, are deleted.</summary>
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
        var reply = new ChatFilesReply(true, _info, [.. _downloads.Values.Select(d => d.State).OrderBy(s => s.Id, StringComparer.Ordinal)],
            [.. _previews.Values.OrderBy(s => s.Id, StringComparer.Ordinal)], 0, SharedListing());
        var print = JsonSerializer.Serialize(reply, PipeJsonContext.Default.ChatFilesReply);
        var current = Volatile.Read(ref _snapshot);
        var changed = print != _fingerprint;
        _fingerprint = print;
        Volatile.Write(ref _snapshot, reply with { Version = changed ? current.Version + 1 : current.Version });
    }

    /// <summary>
    /// "My shared files": the server's list when it sent one (what is being stopped here shows as stopping, what went from
    /// this PC is left out), plus what was registered here since; else this PC's own list.
    /// </summary>
    private IReadOnlyList<SharedFile> SharedListing()
    {
        var local = Store.Listing(_user.CurrentUser());
        if (_serverShared is not { } server)
        {
            return local;
        }
        var all = Store.All();
        var stopping = all.Where(e => e.StopAsked).Select(e => e.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var gone = all.Where(e => e.State != ChatFile.StateOk).Select(e => e.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var forgotten = _forgotten;
        var merged = server.Where(f => !gone.Contains(f.Id) && !forgotten.Contains(f.Id))
            .Select(f => stopping.Contains(f.Id) ? f with { State = ChatFile.StateStopped } : f).ToList();
        merged.AddRange(local.Where(l => !merged.Any(f => string.Equals(f.Id, l.Id, StringComparison.OrdinalIgnoreCase))));
        return [.. merged.OrderByDescending(f => f.CreatedAt)];
    }

    private static bool IsDeferred(Exception e, CancellationToken ct) =>
        e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested;
}

using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using Zinwe.Agent.Core.Chat;
using Zinwe.Agent.Core.Ipc;
using Zinwe.Agent.Core.Messages;
using Zinwe.Agent.Tray.Widget;

namespace Zinwe.Agent.Tray;

/// <summary>
/// A file picked for a chat, waiting in the send tray above the composer until Send: where it is, its name, size and kind,
/// and what Windows told about it (the thumbnail as a JPEG, the pixels, a video's length; filled in just after the pick).
/// </summary>
public sealed record AttachDraft(string Path, string Name, long Size, string Kind, string Mime, byte[]? Thumb = null, int? Width = null,
    int? Height = null, double? DurationS = null, bool Described = false)
{
    public bool IsMedia => Kind is ChatFile.ImageKind or ChatFile.VideoKind;
}

public sealed partial class TrayModel
{
    // ======================================================================================================
    // v1.5 round 2 · file sharing in chat: files stay on the member's PC
    // The tray makes no network calls. Picked files (the attach menu: Document / Photos & videos, up to 10) wait in the send
    // tray with Windows' own thumbnails; Send queues one outbox item (kind chat_file) per file with the same batch id and
    // the caption on the last. A file on a network or removable drive is copied first into %ProgramData%\Zinwe\shared\<id>
    // (only SYSTEM, Administrators and the member can read it), because the service can't reach those later. Downloads,
    // thumbnails, cancel and Stop sharing are asked of the service over the pipe ("files"); it does them AS the member.
    // ======================================================================================================

    private ChatFilesReply? _files;
    private readonly Dictionary<string, List<AttachDraft>> _attach = new(StringComparer.Ordinal);
    private readonly List<OutboxReply> _copying = [];
    private readonly HashSet<string> _previewAsked = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _downloadAsked = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, FileAsk> _downloadTargets = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _viewWhenDone = new(StringComparer.OrdinalIgnoreCase);
    private string _widgetDir = WidgetPaths.Directory;
    private string _sharedRoot = SharedCopies.Root;
    private IShellMedia _media = new ShellMedia();
    private Func<FileAsk?, Task<ChatFilesReply?>> _filesPipe = ask => AgentPipeClient.GetFilesAsync(ask, TimeSpan.FromSeconds(3));

    /// <summary>Save as… (and "Ask where to save each file"): the view's save dialog (name, folder) → the chosen path, or null.</summary>
    public Func<string, string, string?>? SaveAsPicker { get; set; }

    /// <summary>A downloaded photo to show in the viewer (path, name).</summary>
    public event Action<string, string>? ViewerRequested;

    /// <summary>The attach button shows: a 1.5 service, a round-2 backend (the relay), and the admin allows files.</summary>
    public bool FilesOn => Status?.FilesVersion is not null && _files is { Supported: true, Info.CanSend: true };

    /// <summary>A 1.5 service that knows files (Settings › Files shows; files already shared can be listed and stopped).</summary>
    public bool FilesSupported => Status?.FilesVersion is not null && _files is { Supported: true };

    /// <summary>The plan's biggest file (Free 1 GB … Businesses 5 GB).</summary>
    public long FileMaxBytes => _files?.Info?.MaxBytes is { } max and > 0 ? max : ChatFiles.DefaultMaxBytes;

    /// <summary>A download, a thumbnail or a copy is under way: the status is asked every second.</summary>
    public bool FilesBusy => _copying.Count > 0
        || (_files?.Downloads ?? []).Any(s => s.Busy)
        || (_files?.Previews ?? []).Any(s => s.State == FileState.Working);

    /// <summary>"My shared files": the member's own files this PC holds.</summary>
    public IReadOnlyList<SharedFile> SharedFiles => _files?.Shared ?? [];

    /// <summary>Changes whenever something a file card shows changes (the views redraw on it).</summary>
    public string FilesSignature =>
        $"{_files?.Version}|{FilesOn}|{_copying.Count}|{string.Join(",", _attach.Select(a => a.Key + "=" + string.Join(";", a.Value.Select(d => d.Path + ":" + d.Described))))}";

    /// <summary>The files reply, fetched when the status says it moved.</summary>
    private async Task RefreshFilesIfMovedAsync()
    {
        if (Status?.FilesVersion is not { } version)
        {
            if (_files is not null && Status is not null)
            {
                _files = null; // an older service: no files
            }
            return;
        }
        if (_files is null || version != _files.Version || FilesBusy)
        {
            await RefreshFilesAsync();
        }
    }

    /// <summary>Ask the service for file sharing as it stands, asking for <paramref name="ask"/> too.</summary>
    internal async Task RefreshFilesAsync(FileAsk? ask = null)
    {
        if (Status?.FilesVersion is null)
        {
            return;
        }
        if (await _filesPipe(ask) is { } reply)
        {
            TakeFiles(reply);
        }
    }

    /// <summary>The service's files answer, taken in (a download that just finished or failed says so once).</summary>
    internal void TakeFiles(ChatFilesReply reply)
    {
        var before = _files;
        _files = reply;
        foreach (var s in reply.Downloads ?? [])
        {
            var was = before?.Downloads?.FirstOrDefault(b => string.Equals(b.Id, s.Id, StringComparison.OrdinalIgnoreCase));
            if (was is null || !was.Busy || s.Busy || !_downloadAsked.Contains(s.Id))
            {
                continue;
            }
            if (s is { State: FileState.Done, Path: { } path } && IsOurDownload(s.Id, path))
            {
                if (_viewWhenDone.Remove(s.Id))
                {
                    ShowPhoto(s.Id, path);
                }
                else
                {
                    ShowSnack("Saved to " + FolderLabel(Path.GetDirectoryName(path)) + ".",
                        [("Open", () => OpenFile(s.Id, path)), ("Show in folder", () => ShowInFolder(s.Id, path))]);
                }
            }
            else if (s.State == FileState.Failed)
            {
                _viewWhenDone.Remove(s.Id);
                ShowSnack(s.Error ?? ChatFiles.DownloadFailedText, [("OK", CloseSnack)]);
            }
        }
        if (before?.Version != reply.Version || before is null)
        {
            Raise();
        }
    }

    // ---- Attach and send -------------------------------------------------------------------------------------------

    /// <summary>The files waiting in a thread's send tray ("admin", "conv:12", "direct:4"), in the order picked.</summary>
    public IReadOnlyList<AttachDraft> Attached(string? key)
    {
        foreach (var k in Aliases(key))
        {
            if (_attach.TryGetValue(k, out var drafts) && drafts.Count > 0)
            {
                return drafts;
            }
        }
        return [];
    }

    /// <summary>
    /// The member picked <paramref name="paths"/> in the file picker for the thread <paramref name="key"/>. Each one over the
    /// plan's limit (or empty, or unreadable) is refused at once, and none past 10 in all; the others wait in the send tray,
    /// and the typed text becomes the caption. Windows' thumbnails and lengths follow on a background thread.
    /// </summary>
    public bool Attach(string key, IEnumerable<string> paths)
    {
        var picked = new List<(string, long)>();
        var unreadable = false;
        foreach (var path in paths)
        {
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists)
                {
                    unreadable = true;
                    continue;
                }
                picked.Add((info.FullName, info.Length));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                unreadable = true;
            }
        }
        var drafts = AttachedList(key);
        var (ok, refusals) = ChatFiles.CheckPicked(picked, drafts.Count, FileMaxBytes);
        var words = refusals.ToList();
        if (unreadable)
        {
            words.Insert(0, "That file couldn’t be opened. Try another one.");
        }
        if (words.Count > 0)
        {
            ShowSnack(string.Join(" ", words), [("OK", CloseSnack)]);
        }
        if (ok.Count == 0)
        {
            return false;
        }
        var added = ok.Select(p => new AttachDraft(p.Path, Path.GetFileName(p.Path), p.Size, ChatFiles.KindOf(p.Path),
            ChatFiles.MimeOf(p.Path))).ToList();
        drafts.AddRange(added);
        Raise();
        FocusRequested?.Invoke(key == OutboxReply.AdminKey ? "composer" : "team-composer");
        LastAttachDescribed = DescribeAsync(key, added);
        return true;
    }

    /// <summary>The last thumbnail pass started (tests wait on it).</summary>
    internal Task? LastAttachDescribed { get; private set; }

    /// <summary>Windows' thumbnails and lengths for <paramref name="added"/>, made on an STA thread (shell handlers want one).</summary>
    private async Task DescribeAsync(string key, IReadOnlyList<AttachDraft> added)
    {
        var media = _media;
        var described = await RunSta(() => added.Select(d => (d, media.Describe(d.Path))).ToList());
        var drafts = AttachedList(key);
        foreach (var (draft, info) in described)
        {
            var i = drafts.IndexOf(draft);
            if (i >= 0)
            {
                drafts[i] = draft with { Thumb = info.Thumb, Width = info.Width, Height = info.Height, DurationS = info.DurationS, Described = true };
            }
        }
        Raise();
    }

    private static Task<T> RunSta<T>(Func<T> work)
    {
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                done.SetResult(work());
            }
            catch (Exception e)
            {
                done.SetException(e);
            }
        }) { IsBackground = true, Name = "Zinwe thumbnails" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return done.Task;
    }

    private List<AttachDraft> AttachedList(string key)
    {
        foreach (var k in Aliases(key))
        {
            if (_attach.TryGetValue(k, out var drafts))
            {
                return drafts;
            }
        }
        return _attach[key] = [];
    }

    /// <summary>✕ on one tile (or, without <paramref name="draft"/>, the whole send tray).</summary>
    public void RemoveAttach(string? key, AttachDraft? draft = null)
    {
        foreach (var k in Aliases(key))
        {
            if (draft is null)
            {
                _attach.Remove(k);
            }
            else if (_attach.TryGetValue(k, out var drafts))
            {
                drafts.Remove(draft);
            }
        }
        Raise();
    }

    /// <summary>
    /// Send the files in <paramref name="key"/>'s tray with <paramref name="caption"/> (on the last one): each queued for the
    /// service with one batch id (a file on a network or removable drive is copied first). True when they are on their way.
    /// </summary>
    public bool SendFiles(string key, string? caption)
    {
        var drafts = Attached(key).ToList();
        if (drafts.Count == 0)
        {
            return false;
        }
        if (Status?.FilesVersion is null)
        {
            ShowSnack("Zinwe isn’t running, so this can’t be sent yet. Try again in a moment.", [("OK", CloseSnack)]);
            return false;
        }
        if (!FilesOn)
        {
            ShowSnack(ChatFiles.SharingOffText, [("OK", CloseSnack)]);
            return false;
        }
        foreach (var k in Aliases(key))
        {
            _attach.Remove(k);
        }
        var batch = drafts.Count > 1 ? Guid.NewGuid().ToString() : null;
        var text = (caption ?? "").Trim();
        var items = drafts.Select((d, i) => new OutboxReply("copy:" + Guid.NewGuid().ToString("N"), i == drafts.Count - 1 ? text : "",
            DateTimeOffset.UtcNow.AddTicks(i), OutboxReply.ChatFileKind, ChatKey: key, FilePath: d.Path, FileName: d.Name, FileSize: d.Size,
            BatchId: batch, Mime: d.Mime, ThumbB64: d.Thumb is { Length: > 0 } t ? Convert.ToBase64String(t) : null, Width: d.Width,
            Height: d.Height, DurationS: d.DurationS)).ToList();
        _copying.AddRange(items);
        if (key == OutboxReply.AdminKey)
        {
            ReplyTo = null;
            Draft = "";
        }
        else
        {
            TeamReplyTo = null;
        }
        Raise();
        LastFileQueued = QueueFilesAsync(key, items, drafts);
        return true;
    }

    /// <summary>The last copy-and-queue started (tests wait on it).</summary>
    internal Task? LastFileQueued { get; private set; }

    private async Task QueueFilesAsync(string key, List<OutboxReply> items, List<AttachDraft> drafts)
    {
        string? error = null;
        var copies = new List<string>();
        try
        {
            var root = _sharedRoot;
            var fallback = Path.Combine(_widgetDir, ChatFiles.FallbackCopiesFolder);
            var ready = await Task.Run(() => items.Select(item =>
            {
                if (!SharedCopies.NeedsCopy(item.FilePath!))
                {
                    return item;
                }
                // The service can't reach a network or removable drive later: a copy it can read (as the member).
                var copy = CopyForSharing(item.FilePath!, item.FileName!, root, fallback);
                copies.Add(copy);
                return item with { FilePath = copy, Copied = true };
            }).ToList());
            if (_outbox.AppendChatFiles(ready) is null)
            {
                error = ChatFiles.FailedText;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            error = ChatFiles.FailedText;
        }
        foreach (var item in items)
        {
            _copying.Remove(item);
        }
        if (error is not null)
        {
            foreach (var copy in copies)
            {
                DeleteCopy(copy);
            }
            _attach[key] = drafts; // the tray comes back, for another go
            ShowSnack(error, [("OK", CloseSnack)]);
        }
        Raise();
    }

    /// <summary>
    /// Copy <paramref name="path"/> into <c>&lt;root&gt;\&lt;id&gt;\&lt;name&gt;</c>, a folder only SYSTEM, Administrators and
    /// this member can read; under the widget folder when the root can't be used (an older service never made it).
    /// </summary>
    internal static string CopyForSharing(string path, string name, string root, string fallback)
    {
        var id = Guid.NewGuid().ToString("N");
        var sid = WindowsIdentity.GetCurrent().User?.Value;
        var folder = sid is null ? null : SharedCopies.MakeFolder(root, id, sid);
        if (folder is null)
        {
            folder = Path.Combine(fallback, id);
            Directory.CreateDirectory(folder);
        }
        var target = Path.Combine(folder, ChatFiles.SafeName(name));
        File.Copy(path, target);
        return target;
    }

    private void DeleteCopy(string? path) =>
        SharedCopies.DeleteCopy(path, [_sharedRoot, Path.Combine(_widgetDir, ChatFiles.FallbackCopiesFolder)]);

    /// <summary>Files on their way in a thread (being prepared, queued, or failed), oldest first.</summary>
    public IReadOnlyList<OutboxReply> PendingFileItems(string? key)
    {
        var keys = Aliases(key).ToHashSet(StringComparer.Ordinal);
        if (keys.Count == 0)
        {
            return [];
        }
        IReadOnlyList<OutboxReply> queued;
        try
        {
            queued = _outbox.PendingFiles();
        }
        catch (Exception)
        {
            queued = [];
        }
        return [.. queued.Concat(_copying).Where(i => i.ChatKey is { } k && keys.Contains(k)).OrderBy(i => i.CreatedAtUtc)];
    }

    /// <summary>Retry a file the backend refused: the service sends it again on its next round.</summary>
    public void RetryFile(OutboxReply item)
    {
        _outbox.Retry(item.Id);
        Raise();
    }

    /// <summary>The member gives up on a failed file: it goes (and our copy of it, if one was made).</summary>
    public void RemoveFile(OutboxReply item)
    {
        _outbox.TryRemove(item.Id);
        if (item.Copied && item.FileId is null)
        {
            DeleteCopy(item.FilePath);
        }
        Raise();
    }

    /// <summary>A thread's keys: a direct chat goes by "direct:4" until its first line makes it "conv:12".</summary>
    private IEnumerable<string> Aliases(string? key)
    {
        if (key is null)
        {
            yield break;
        }
        yield return key;
        if (ChatKeys.Parse(key) is ("conv", var id) && Conversations.FirstOrDefault(c => c.Id == id) is { IsGroup: false, WithMemberId: { } member })
        {
            yield return ChatKeys.Direct(member);
        }
    }

    // ---- Receive: download, save as, cancel, open, view ---------------------------------------------------------------

    /// <summary>Tests: their own Downloads folder instead of the member's.</summary>
    internal static string? DownloadsOverride { get; set; }

    /// <summary>The member's Downloads\Zinwe (where files go unless Settings › Files says otherwise).</summary>
    public static string DownloadsFolder => Path.Combine(DownloadsOverride ?? KnownFolders.Downloads(IntPtr.Zero)
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"), ChatFiles.DownloadsSubfolder);

    /// <summary>Settings › Files › Save files to.</summary>
    public string SaveFolder => Settings.SaveFolder is { Length: > 0 } f && Path.IsPathFullyQualified(f) ? f : DownloadsFolder;

    /// <summary>"Downloads › Zinwe" for the default folder, else the folder's own path.</summary>
    public static string FolderLabel(string? folder) =>
        folder is not null && string.Equals(Path.GetFullPath(folder).TrimEnd('\\'), Path.GetFullPath(DownloadsFolder).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)
            ? "Downloads › Zinwe"
            : folder ?? "";

    public void SetSaveFolder(string? folder)
    {
        SaveSettings(Settings with { SaveFolder = folder is { Length: > 0 } && Path.IsPathFullyQualified(folder) ? folder : null });
        Raise();
    }

    public void SetAskWhere(bool on)
    {
        SaveSettings(Settings with { AskWhere = on });
        Raise();
    }

    /// <summary>Where the service is with a download of this file (null = never asked).</summary>
    public FileState? DownloadOf(ChatFile f) =>
        _files?.Downloads?.FirstOrDefault(s => string.Equals(s.Id, f.Id, StringComparison.OrdinalIgnoreCase));

    /// <summary>The downloaded copy, when the service saved it where this tray asked and it is still there.</summary>
    public string? DownloadedPath(ChatFile f) =>
        DownloadOf(f) is { State: FileState.Done, Path: { } path } && IsOurDownload(f.Id, path) && File.Exists(path) ? path : null;

    /// <summary>
    /// Download (or, with <paramref name="saveAs"/> or "Ask where to save each file", Save as…): a program file asks once more
    /// ("This is a program file. Open it only if you trust Priya."); then the service saves it, AS the member, into Save files
    /// to (a free name) or exactly where they chose, and the card offers Open · Show in folder. <paramref name="view"/>: a
    /// photo opens in the viewer once it is here.
    /// </summary>
    public void Download(ChatFile f, string? senderFirst, bool mine = false, bool saveAs = false, bool confirmed = false, bool view = false)
    {
        if (!confirmed && !mine && f.IsRisky)
        {
            ShowSnack(ChatFiles.RiskyText(senderFirst), [("Download", () => Download(f, senderFirst, mine, saveAs, true, view)), ("Cancel", CloseSnack)]);
            return;
        }
        if (Status?.FilesVersion is null)
        {
            ShowSnack("Zinwe isn’t running, so this can’t be downloaded now. Try again in a moment.", [("OK", CloseSnack)]);
            return;
        }
        if (confirmed)
        {
            CloseSnack();
        }
        FileAsk ask;
        var first = f.FirstName(senderFirst);
        if (saveAs || Settings.AskWhere)
        {
            if (SaveAsPicker?.Invoke(ChatFiles.SafeName(f.Name), SaveFolder) is not { } target || !Path.IsPathFullyQualified(target))
            {
                return; // the member cancelled
            }
            ask = new FileAsk(FileAsk.Download, f.Id, f.Name, f.Size, Target: target, Legacy: f.IsLegacy, Sha256: f.Sha256, SenderFirst: first);
        }
        else
        {
            ask = new FileAsk(FileAsk.Download, f.Id, f.Name, f.Size, Folder: SaveFolder, Legacy: f.IsLegacy, Sha256: f.Sha256, SenderFirst: first);
        }
        _downloadAsked.Add(f.Id);
        _downloadTargets[f.Id] = ask;
        if (view)
        {
            _viewWhenDone.Add(f.Id);
        }
        _ = RefreshFilesAsync(ask);
    }

    /// <summary>✕ on the spinner ring: the download stops and its half-file goes.</summary>
    public void CancelDownload(ChatFile f)
    {
        _viewWhenDone.Remove(f.Id);
        _ = RefreshFilesAsync(new FileAsk(FileAsk.Cancel, f.Id));
    }

    /// <summary>A photo: open it in the viewer (downloading it first when it isn't here yet). A video or another file: open it.</summary>
    public void OpenMedia(ChatFile f, string? senderFirst, bool mine)
    {
        if (DownloadedPath(f) is { } path)
        {
            if (f.IsImage)
            {
                ShowPhoto(f.Id, path);
            }
            else
            {
                OpenFile(f.Id, path);
            }
            return;
        }
        if (DownloadOf(f) is { Busy: true })
        {
            return;
        }
        Download(f, senderFirst, mine, view: f.IsImage);
    }

    private void ShowPhoto(string id, string path)
    {
        if (IsOurDownload(id, path) && File.Exists(path))
        {
            ViewerRequested?.Invoke(path, Path.GetFileName(path));
        }
    }

    /// <summary>Open a downloaded file with its app (only a file the service saved where this tray asked).</summary>
    public void OpenFile(string id, string path)
    {
        if (!IsOurDownload(id, path) || !File.Exists(path))
        {
            ShowSnack("That file was moved or deleted. Download it again.", [("OK", CloseSnack)]);
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            ShowSnack("Windows couldn’t open that file. Try Show in folder.", [("Show in folder", () => ShowInFolder(id, path)), ("OK", CloseSnack)]);
        }
    }

    /// <summary>File Explorer on the file's folder with it selected.</summary>
    public void ShowInFolder(string id, string path)
    {
        if (!IsOurDownload(id, path))
        {
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = false });
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // nothing else to do
        }
    }

    /// <summary>
    /// Only a path the service reports for a download this tray asked for, and only where it asked (exactly the Save as
    /// path, or directly in the folder it named) is opened or shown: the path comes from another process.
    /// </summary>
    internal bool IsOurDownload(string id, string? path)
    {
        try
        {
            if (path is null || !Path.IsPathFullyQualified(path) || !_downloadTargets.TryGetValue(id, out var ask))
            {
                return false;
            }
            var full = Path.GetFullPath(path);
            return ask.Target is { } target
                ? string.Equals(full, Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase)
                : ask.Folder is { } folder && string.Equals(Path.GetDirectoryName(full)?.TrimEnd('\\'), Path.GetFullPath(folder).TrimEnd('\\'),
                    StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    /// <summary>
    /// A file's thumbnail once the service fetched it into the widget folder (round 2: its preview picture; round 1: an
    /// image up to 5 MB); the first call asks for it. Null = draw the tile with its type icon.
    /// </summary>
    public string? ThumbFor(ChatFile f)
    {
        if (Status?.FilesVersion is null || !f.AvailableAt(Now) && f.IsLegacy)
        {
            return null;
        }
        if (f.IsLegacy ? !f.IsImage || f.Size > ChatFiles.PreviewMaxBytes : f.ThumbUrl is null)
        {
            return null;
        }
        var state = _files?.Previews?.FirstOrDefault(s => string.Equals(s.Id, f.Id, StringComparison.OrdinalIgnoreCase));
        if (state is { State: FileState.Done, Path: { } path })
        {
            return IsOurPreview(path) && File.Exists(path) ? path : null;
        }
        if (state is null && _previewAsked.Add(f.Id))
        {
            _ = RefreshFilesAsync(new FileAsk(FileAsk.Preview, f.Id, f.Name, f.Size, Legacy: f.IsLegacy));
        }
        return null;
    }

    private bool IsOurPreview(string path)
    {
        try
        {
            return string.Equals(Path.GetDirectoryName(Path.GetFullPath(path))?.TrimEnd('\\'),
                Path.GetFullPath(Path.Combine(_widgetDir, ChatFiles.PreviewFolder)).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    // ---- My shared files -----------------------------------------------------------------------------------------------

    /// <summary>Stop sharing one of the member's files: others can't download it any more (the file itself stays).</summary>
    public void StopSharing(string id)
    {
        if (Status?.FilesVersion is null)
        {
            return;
        }
        _ = RefreshFilesAsync(new FileAsk(FileAsk.Stop, id));
        ShowSnack("Stopped sharing it. The file stays on your PC.", [("OK", CloseSnack)]);
    }

    /// <summary>The chat a shared file went to, in words ("Design", "Priya Nair", your admin's name).</summary>
    public string ChatName(string? key)
    {
        if (key is null)
        {
            return "";
        }
        if (key == OutboxReply.AdminKey)
        {
            return OwnerNameStart;
        }
        if (ChatKeys.Parse(key) is ("conv", var id) && Conversations.FirstOrDefault(c => c.Id == id) is { } c)
        {
            return c.Name;
        }
        if (ChatKeys.Parse(key) is ("direct", var member) && _chat?.People?.FirstOrDefault(p => p.MemberId == member) is { } person)
        {
            return person.Name;
        }
        return "a chat";
    }

    /// <summary>
    /// The lines of a thread as the view draws them: photos and videos sent together become one line carrying the batch
    /// (the last line of it, with its caption), drawn as a 2×2 grid.
    /// </summary>
    public static IReadOnlyList<TeamLine> Batched(IReadOnlyList<TeamLine> lines) =>
        [.. ChatFileBatches.Group(lines, l => l.Message.Deleted || l.Upload is not null ? null : l.Message.File,
                l => l.Mine + ":" + l.Message.ByKind + ":" + l.Message.ByMemberId)
            .Select(g => g.Count > 1 ? g[^1] with { Batch = [.. g.Select(l => l.Message.File!)] } : g[0])];

    // ---- Tests -------------------------------------------------------------------------------------------------------

    /// <summary>Tests: a widget folder (outbox, copies, previews) of their own, the service's "files" answers, and Windows' media.</summary>
    internal void UseFilesForTests(string widgetDir, Func<FileAsk?, Task<ChatFilesReply?>>? pipe = null, ChatFilesReply? files = null,
        IShellMedia? media = null, string? sharedRoot = null)
    {
        _widgetDir = widgetDir;
        _outbox = new MessageOutbox(widgetDir);
        if (pipe is not null)
        {
            _filesPipe = pipe;
        }
        if (media is not null)
        {
            _media = media;
        }
        _sharedRoot = sharedRoot ?? Path.Combine(widgetDir, "programdata-shared");
        _settingsStore = new TraySettingsStore(widgetDir); // Settings › Files writes there, never the member's own file
        _files = files;
    }
}

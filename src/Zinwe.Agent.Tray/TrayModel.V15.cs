using System.Diagnostics;
using System.IO;
using Zinwe.Agent.Core.Chat;
using Zinwe.Agent.Core.Ipc;
using Zinwe.Agent.Core.Messages;
using Zinwe.Agent.Tray.Widget;

namespace Zinwe.Agent.Tray;

/// <summary>A file picked for a chat, waiting above the composer as a chip (name, size, ✕) until Send.</summary>
public sealed record AttachDraft(string Path, string Name, long Size);

public sealed partial class TrayModel
{
    // ======================================================================================================
    // v1.5 · file sharing in chat ("flavour" version: send and forget, 25 MB, kept 7 days)
    // The tray makes no network calls: a picked file is copied into the widget folder (outbox-files\<guid>\) and queued
    // in the outbox (kind chat_file); the service uploads it and posts the line. Downloads and image previews are asked of
    // the service over the pipe ("files"), which saves them as the member and says where.
    // ======================================================================================================

    private ChatFilesReply? _files;
    private readonly Dictionary<string, AttachDraft> _attach = new(StringComparer.Ordinal);
    private readonly List<OutboxReply> _copying = [];
    private readonly HashSet<string> _previewAsked = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _downloadAsked = new(StringComparer.OrdinalIgnoreCase);
    private string _widgetDir = WidgetPaths.Directory;
    private Func<FileAsk?, Task<ChatFilesReply?>> _filesPipe = ask => AgentPipeClient.GetFilesAsync(ask, TimeSpan.FromSeconds(3));

    /// <summary>The attach button shows: a service that sends files, and a backend (and admin) that allow them.</summary>
    public bool FilesOn => Status?.FilesVersion is not null && _files is { Supported: true, Info.Allowed: true };

    /// <summary>The biggest file the member may pick (25 MB, or less when the backend says so).</summary>
    public long FileMaxBytes => Math.Min(_files?.Info?.MaxBytes is { } max and > 0 ? max : ChatFiles.MaxBytes, ChatFiles.MaxBytes);

    /// <summary>A download, a preview or a copy is under way: the status is asked every second.</summary>
    public bool FilesBusy => _copying.Count > 0
        || (_files?.Downloads ?? []).Any(s => s.State == FileState.Working)
        || (_files?.Previews ?? []).Any(s => s.State == FileState.Working);

    /// <summary>Changes whenever something a file card shows changes (the views redraw on it).</summary>
    public string FilesSignature =>
        $"{_files?.Version}|{FilesOn}|{_copying.Count}|{string.Join(",", _attach.Select(a => a.Key + "=" + a.Value.Path + ":" + a.Value.Size))}";

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
            if (was?.State != FileState.Working || s.State == FileState.Working || !_downloadAsked.Contains(s.Id))
            {
                continue;
            }
            if (s is { State: FileState.Done, Path: { } path })
            {
                ShowSnack("Saved to Downloads › Zinwe.", [("Open", () => OpenFile(path)), ("Show in folder", () => ShowInFolder(path))]);
            }
            else if (s.State == FileState.Failed)
            {
                ShowSnack(s.Error ?? ChatFiles.DownloadFailedText, [("OK", CloseSnack)]);
            }
        }
        if (before?.Version != reply.Version || before is null)
        {
            Raise();
        }
    }

    // ---- Attach and send -------------------------------------------------------------------------------------------

    /// <summary>The chip waiting in a thread's composer ("admin", "conv:12", "direct:4"), or null.</summary>
    public AttachDraft? AttachFor(string? key)
    {
        foreach (var k in Aliases(key))
        {
            if (_attach.TryGetValue(k, out var draft))
            {
                return draft;
            }
        }
        return null;
    }

    /// <summary>
    /// The member picked <paramref name="path"/> in the file picker for the thread <paramref name="key"/>. Over the limit (or
    /// empty, or unreadable) it is refused at once; else it waits as a chip above the composer, and the typed text becomes
    /// its caption.
    /// </summary>
    public bool Attach(string key, string path)
    {
        long size;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                ShowSnack("That file couldn’t be opened. Try another one.", [("OK", CloseSnack)]);
                return false;
            }
            size = info.Length;
            path = info.FullName;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            ShowSnack("That file couldn’t be opened. Try another one.", [("OK", CloseSnack)]);
            return false;
        }
        if (size > FileMaxBytes)
        {
            ShowSnack(ChatFiles.TooBigText, [("OK", CloseSnack)]);
            return false;
        }
        if (size == 0)
        {
            ShowSnack(ChatFiles.EmptyText, [("OK", CloseSnack)]);
            return false;
        }
        foreach (var k in Aliases(key))
        {
            _attach.Remove(k);
        }
        _attach[key] = new AttachDraft(path, Path.GetFileName(path), size);
        Raise();
        FocusRequested?.Invoke(key == OutboxReply.AdminKey ? "composer" : "team-composer");
        return true;
    }

    /// <summary>✕ on the chip.</summary>
    public void RemoveAttach(string? key)
    {
        foreach (var k in Aliases(key))
        {
            _attach.Remove(k);
        }
        Raise();
    }

    /// <summary>
    /// Send the chip of <paramref name="key"/> with <paramref name="caption"/>: copied into the widget folder (the service
    /// can't see mapped drives), then queued for the service. True when it is on its way (the composer may clear).
    /// </summary>
    public bool SendFile(string key, string? caption)
    {
        if (AttachFor(key) is not { } draft)
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
        var copying = new OutboxReply("copy:" + Guid.NewGuid().ToString("N"), (caption ?? "").Trim(), DateTimeOffset.UtcNow, OutboxReply.ChatFileKind,
            ChatKey: key, FileName: draft.Name, FileSize: draft.Size);
        _copying.Add(copying);
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
        LastFileQueued = QueueFileAsync(copying, draft);
        return true;
    }

    /// <summary>The last copy-and-queue started (tests wait on it).</summary>
    internal Task? LastFileQueued { get; private set; }

    private async Task QueueFileAsync(OutboxReply copying, AttachDraft draft)
    {
        string? error = null;
        var target = Path.Combine(_widgetDir, ChatFiles.OutboxFolder, Guid.NewGuid().ToString("N"), ChatFiles.SafeName(draft.Name));
        long size = 0;
        try
        {
            var max = FileMaxBytes;
            size = await Task.Run(() =>
            {
                var info = new FileInfo(draft.Path);
                if (info.Length > max)
                {
                    return -1L;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(draft.Path, target);
                return new FileInfo(target).Length;
            });
            if (size < 0)
            {
                error = ChatFiles.TooBigText; // it grew since it was picked
            }
            else if (_outbox.AppendChatFile(copying.ChatKey!, target, draft.Name, size, copying.Body) is null)
            {
                error = ChatFiles.FailedText;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            error = ChatFiles.FailedText;
        }
        _copying.Remove(copying);
        if (error is not null)
        {
            DeleteCopyFolder(target);
            _attach[copying.ChatKey!] = draft; // the chip comes back, for another go
            ShowSnack(error, [("OK", CloseSnack)]);
        }
        Raise();
    }

    /// <summary>Files on their way in a thread (being copied, queued, or failed), oldest first.</summary>
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

    /// <summary>The member gives up on a failed file: it and its copy go.</summary>
    public void RemoveFile(OutboxReply item)
    {
        _outbox.TryRemove(item.Id);
        DeleteCopyFolder(item.FilePath);
        Raise();
    }

    /// <summary>Delete a copy's own folder (outbox-files\&lt;guid&gt;\), never anything outside it.</summary>
    private void DeleteCopyFolder(string? path)
    {
        try
        {
            var root = Path.GetFullPath(Path.Combine(_widgetDir, ChatFiles.OutboxFolder)).TrimEnd('\\') + "\\";
            if (path is not null && Path.GetDirectoryName(Path.GetFullPath(path)) is { } folder
                && string.Equals(Path.GetDirectoryName(folder)?.TrimEnd('\\') + "\\", root, StringComparison.OrdinalIgnoreCase) && Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // the service's sweep takes it later
        }
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

    // ---- Receive: download, open, preview ---------------------------------------------------------------------------

    /// <summary>Where the service is with a download of this file (null = never asked).</summary>
    public FileState? DownloadOf(ChatFile f) =>
        _files?.Downloads?.FirstOrDefault(s => string.Equals(s.Id, f.Id, StringComparison.OrdinalIgnoreCase));

    /// <summary>The downloaded copy, when the service saved it and it is still there.</summary>
    public string? DownloadedPath(ChatFile f) =>
        DownloadOf(f) is { State: FileState.Done, Path: { } path } && IsOurDownload(path) && File.Exists(path) ? path : null;

    /// <summary>
    /// Download: a program file asks once more ("This is a program file. Open it only if you trust Priya."); then the service
    /// saves it into Downloads\Zinwe and the card offers Open · Show in folder.
    /// </summary>
    public void Download(ChatFile f, string? senderFirst, bool mine = false, bool confirmed = false)
    {
        if (!confirmed && !mine && f.IsRisky)
        {
            ShowSnack(ChatFiles.RiskyText(senderFirst), [("Download", () => Download(f, senderFirst, mine, true)), ("Cancel", CloseSnack)]);
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
        _downloadAsked.Add(f.Id);
        _ = RefreshFilesAsync(new FileAsk(FileAsk.Download, f.Id, f.Name, f.Size));
    }

    /// <summary>Open a downloaded file with its app (only a file in Downloads\Zinwe).</summary>
    public void OpenFile(string path)
    {
        if (!IsOurDownload(path) || !File.Exists(path))
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
            ShowSnack("Windows couldn’t open that file. Try Show in folder.", [("Show in folder", () => ShowInFolder(path)), ("OK", CloseSnack)]);
        }
    }

    /// <summary>File Explorer on Downloads\Zinwe with the file selected.</summary>
    public void ShowInFolder(string path)
    {
        if (!IsOurDownload(path))
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

    /// <summary>Tests: their own Downloads folder instead of the member's.</summary>
    internal static string? DownloadsOverride { get; set; }

    /// <summary>The member's Downloads\Zinwe (where the service saves files from the chat).</summary>
    public static string DownloadsFolder => Path.Combine(DownloadsOverride ?? KnownFolders.Downloads(IntPtr.Zero)
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"), ChatFiles.DownloadsSubfolder);

    /// <summary>Only a file directly in Downloads\Zinwe is opened or shown (the path comes from another process).</summary>
    internal static bool IsOurDownload(string? path)
    {
        try
        {
            return path is not null && Path.IsPathFullyQualified(path)
                && string.Equals(Path.GetDirectoryName(Path.GetFullPath(path))?.TrimEnd('\\'), Path.GetFullPath(DownloadsFolder).TrimEnd('\\'),
                    StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    /// <summary>
    /// The thumbnail of an image line (≤ 5 MB, still kept) once the service fetched it into the widget folder; the first
    /// call asks for it. Null = draw the file card without one.
    /// </summary>
    public string? PreviewFor(ChatFile f)
    {
        if (!f.IsImage || f.Size > ChatFiles.PreviewMaxBytes || !f.AvailableAt(Now) || Status?.FilesVersion is null)
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
            _ = RefreshFilesAsync(new FileAsk(FileAsk.Preview, f.Id, f.Name, f.Size));
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

    // ---- Tests -------------------------------------------------------------------------------------------------------

    /// <summary>Tests: a widget folder (outbox, copies, previews) of their own, and the service's "files" answers.</summary>
    internal void UseFilesForTests(string widgetDir, Func<FileAsk?, Task<ChatFilesReply?>>? pipe = null, ChatFilesReply? files = null)
    {
        _widgetDir = widgetDir;
        _outbox = new MessageOutbox(widgetDir);
        if (pipe is not null)
        {
            _filesPipe = pipe;
        }
        _files = files;
    }
}

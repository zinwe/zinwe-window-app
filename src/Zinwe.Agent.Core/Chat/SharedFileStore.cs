using System.Security.AccessControl;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Zinwe.Agent.Core.Chat;

/// <summary>
/// v1.5 round 2 · runs file work AS the member signed in to this PC. The files a member shares stay where they are (their
/// own path, a user-chosen folder), so the LocalSystem service must never open, stat, write or delete them with its own
/// rights: a planted junction or a path to a system file would then reach what the member can't. As the service this
/// impersonates the signed-in user; under <c>dotnet run</c> and in tests it simply runs. Nobody signed in →
/// <c>whenNobody</c>, nothing touched (a PC with nobody signed in doesn't serve its files).
/// </summary>
public interface IUserFiles
{
    T AsUser<T>(Func<T> op, T whenNobody);

    /// <summary>The signed-in member's Windows SID (null: nobody). A file is served only while the one who shared it is signed in.</summary>
    string? CurrentUser();
}

/// <summary>The current process user (the dev console agent, tests).</summary>
public sealed class LocalUserFiles(string? sid = null) : IUserFiles
{
    public T AsUser<T>(Func<T> op, T whenNobody) => op();

    public string? CurrentUser() => sid ?? System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value;
}

/// <summary>
/// One file this PC holds for the chat (the service's own list, in its data folder, never the member's): what it registered
/// (id, name, size, the path and its last write time, the chat it went to), its sha256 once hashed, and what the server
/// still has to hear (the hash, "gone", "stop").
/// </summary>
/// <param name="LocalRef">The member's own path, or our copy under <c>%ProgramData%\Zinwe\shared\&lt;id&gt;\</c> (<paramref name="Copied"/>).</param>
/// <param name="State">ok, stopped or gone (<see cref="ChatFile"/>'s words).</param>
public sealed record SharedEntry(
    string Id,
    string Name,
    long Size,
    string LocalRef,
    DateTime LastWriteUtc,
    string? ChatKey,
    DateTimeOffset CreatedAt,
    bool Copied = false,
    string? Sha256 = null,
    bool HashSent = false,
    string State = ChatFile.StateOk,
    bool StateSent = true,
    bool StopAsked = false,
    string? UserSid = null)
{
    [JsonIgnore]
    public bool Active => State == ChatFile.StateOk && !StopAsked;
}

/// <summary>
/// The list of files this PC holds (<c>shared-files.json</c> in the service's data folder, SYSTEM + Administrators only).
/// The serve loop answers a relay job only for a file on it; "My shared files" shows it. Entries go after 30 days (the
/// server's own limit), when stopped, and once a "gone" was told to the server.
/// </summary>
public sealed class SharedFileStore
{
    private const string FileName = "shared-files.json";
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };
    private readonly string? _path;
    private readonly Lock _lock = new();
    private List<SharedEntry>? _entries;

    public SharedFileStore(string? dataDirectory) => _path = dataDirectory is null ? null : Path.Combine(dataDirectory, FileName);

    public IReadOnlyList<SharedEntry> All()
    {
        lock (_lock)
        {
            return [.. Load()];
        }
    }

    public SharedEntry? Get(string id)
    {
        lock (_lock)
        {
            return Load().FirstOrDefault(e => string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>True while any file may be asked for: the relay long-poll stays open.</summary>
    public bool AnyActive
    {
        get
        {
            lock (_lock)
            {
                return Load().Any(e => e.Active);
            }
        }
    }

    public void Add(SharedEntry entry)
    {
        lock (_lock)
        {
            var all = Load();
            all.RemoveAll(e => string.Equals(e.Id, entry.Id, StringComparison.OrdinalIgnoreCase));
            all.Add(entry with { Id = entry.Id.ToLowerInvariant() });
            Save(all);
        }
    }

    /// <summary>Change one entry in place; false when it isn't here.</summary>
    public bool Update(string id, Func<SharedEntry, SharedEntry> change)
    {
        lock (_lock)
        {
            var all = Load();
            var i = all.FindIndex(e => string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase));
            if (i < 0)
            {
                return false;
            }
            all[i] = change(all[i]);
            Save(all);
            return true;
        }
    }

    public SharedEntry? Remove(string id)
    {
        lock (_lock)
        {
            var all = Load();
            var gone = all.FirstOrDefault(e => string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase));
            if (gone is not null)
            {
                all.Remove(gone);
                Save(all);
            }
            return gone;
        }
    }

    /// <summary>Entries older than the keep days: removed and returned (their copies are deleted by the caller).</summary>
    public IReadOnlyList<SharedEntry> TakeOld(DateTimeOffset now)
    {
        lock (_lock)
        {
            var all = Load();
            var old = all.Where(e => now - e.CreatedAt > TimeSpan.FromDays(ChatFiles.KeepDays)).ToList();
            if (old.Count > 0)
            {
                all.RemoveAll(old.Contains);
                Save(all);
            }
            return old;
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _entries = [];
            Save(_entries);
        }
    }

    /// <summary>"My shared files" of <paramref name="userSid"/>: what is still shared (or being stopped), newest first.</summary>
    public IReadOnlyList<SharedFile> Listing(string? userSid) =>
        [.. All().Where(e => e.State == ChatFile.StateOk && (e.UserSid is null || string.Equals(e.UserSid, userSid, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(e => e.CreatedAt)
            .Select(e => new SharedFile(e.Id, e.Name, e.Size, e.ChatKey, e.CreatedAt, e.StopAsked ? ChatFile.StateStopped : e.State))];

    private List<SharedEntry> Load()
    {
        if (_entries is not null)
        {
            return _entries;
        }
        _entries = [];
        try
        {
            if (_path is not null && File.Exists(_path))
            {
                _entries = JsonSerializer.Deserialize<List<SharedEntry>>(File.ReadAllText(_path), Json)?
                    .Where(e => e is not null && ChatFiles.IsValidId(e.Id) && !string.IsNullOrWhiteSpace(e.LocalRef)).ToList() ?? [];
            }
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            _entries = [];
        }
        return _entries;
    }

    private void Save(List<SharedEntry> entries)
    {
        _entries = entries;
        if (_path is null)
        {
            return;
        }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(entries, Json));
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // kept in memory; written on the next change
        }
    }
}

/// <summary>
/// The copies of shared files that sit on a network or removable drive (the service can't reach those later):
/// <c>%ProgramData%\Zinwe\shared\&lt;id&gt;\&lt;name&gt;</c>, each folder readable only by SYSTEM, Administrators and the
/// member who shared it.
/// </summary>
public static class SharedCopies
{
    /// <summary><c>%ProgramData%\Zinwe\shared</c>.</summary>
    public static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Zinwe", ChatFiles.SharedFolderName);

    /// <summary>
    /// The root as the service makes it: SYSTEM and Administrators full; signed-in users may only make a folder in it (and
    /// get full rights on their own as its creator). Nobody lists another member's copies.
    /// </summary>
    public const string RootSddl = "D:PAI(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;;0x1000A4;;;AU)(A;OICIIO;FA;;;CO)";

    /// <summary>One copy's folder: SYSTEM, Administrators and that member only (no inheritance).</summary>
    public static string FolderSddl(string userSid) => $"D:PAI(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;FA;;;{userSid})";

    /// <summary>A file on a network share (a UNC path or a mapped drive) or a removable / optical drive: copy it first.</summary>
    public static bool NeedsCopy(string path, Func<string, DriveType?>? driveType = null)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }
        if (path.StartsWith(@"\\", StringComparison.Ordinal) && !path.StartsWith(@"\\?\", StringComparison.Ordinal) && !path.StartsWith(@"\\.\", StringComparison.Ordinal))
        {
            return true; // \\server\share\…
        }
        driveType ??= DriveTypeOf;
        string? root;
        try
        {
            root = Path.GetPathRoot(path);
        }
        catch (ArgumentException)
        {
            return false;
        }
        return root is { Length: > 0 } && driveType(root) is DriveType.Network or DriveType.Removable or DriveType.CDRom;
    }

    private static DriveType? DriveTypeOf(string root)
    {
        try
        {
            return new DriveInfo(root).DriveType;
        }
        catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The service makes the root (once at start); best effort.</summary>
    public static void EnsureRoot(string? root = null)
    {
        root ??= Root;
        try
        {
            var security = new DirectorySecurity();
            security.SetSecurityDescriptorSddlForm(RootSddl);
            var dir = new DirectoryInfo(root);
            if (!dir.Exists)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(root)!);
                dir.Create(security);
            }
            else
            {
                dir.SetAccessControl(security);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or PrivilegeNotHeldException
            or ArgumentException or PlatformNotSupportedException)
        {
            // a dev run without rights: the tray falls back to its own folder
        }
    }

    /// <summary>
    /// The copy's folder <paramref name="id"/> as the member makes it (from the tray), locked to SYSTEM, Administrators
    /// and <paramref name="userSid"/>. Null when it can't be made there.
    /// </summary>
    public static string? MakeFolder(string root, string id, string userSid)
    {
        try
        {
            var folder = Path.Combine(root, id);
            Directory.CreateDirectory(root);
            var security = new DirectorySecurity();
            security.SetSecurityDescriptorSddlForm(FolderSddl(userSid));
            new DirectoryInfo(folder).Create(security);
            return folder;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException
            or PlatformNotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// Delete a copy's folder: only <c>&lt;root&gt;\&lt;folder&gt;\&lt;file&gt;</c> (never anything else the path names).
    /// The caller runs this as the member.
    /// </summary>
    public static bool DeleteCopy(string? path, IEnumerable<string> roots)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }
        try
        {
            var full = Path.GetFullPath(path);
            var folder = Path.GetDirectoryName(full);
            var parent = folder is null ? null : Path.GetDirectoryName(folder);
            if (parent is null || !roots.Any(r => string.Equals(Path.GetFullPath(r).TrimEnd('\\'), parent.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder!, recursive: true);
            }
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }
}

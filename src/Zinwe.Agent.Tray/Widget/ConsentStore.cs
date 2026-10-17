using System.IO;
using System.Text.Json;
using Zinwe.Agent.Core.Work;

namespace Zinwe.Agent.Tray.Widget;

/// <summary>
/// The member's answer to the monitoring notice on this PC (per Windows user), written when they press "I agree"
/// or "I don't agree" (or withdraw later) on the widget. The service reads it (<see cref="ConsentReader"/>) and
/// reports it on the heartbeat; until the member agrees to the notice in force, the service records nothing.
/// </summary>
public sealed class ConsentStore(string? directory = null)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private readonly string _directory = directory ?? WidgetPaths.Directory;

    private string FilePath => Path.Combine(_directory, "consent.json");

    // AcceptedAt only for an agreement: an older service reads a file with AcceptedAt as "agreed" (it doesn't know
    // State), and one without it as "no consent", so a decline is never mistaken for an agreement.
    private sealed record ConsentFile(string Version, DateTimeOffset? AcceptedAt, string State, DateTimeOffset DecidedAt);

    /// <summary>The recorded answer (any version), or null.</summary>
    public ConsentRecord? Load() => new ConsentReader(_directory).Read();

    /// <summary>Where the member stands for the notice <paramref name="noticeVersion"/> (null = the full notice).</summary>
    public ConsentState State(string? noticeVersion) => ConsentNotice.StateOf(Load(), noticeVersion);

    /// <summary>"I agree" to the notice <paramref name="version"/>. Best effort: a failed write just asks again.</summary>
    public void Accept(string version, DateTimeOffset now) => Write(new ConsentFile(version, now, "given", now));

    /// <summary>"I don't agree", or withdrawing an earlier agreement.</summary>
    public void Decline(string version, DateTimeOffset now) => Write(new ConsentFile(version, null, "declined", now));

    /// <summary>Forget the answer (the PC was removed from its company: a new company asks afresh).</summary>
    public void Clear()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                File.Delete(FilePath);
            }
        }
        catch
        {
            // best effort
        }
    }

    private void Write(ConsentFile file)
    {
        try
        {
            Directory.CreateDirectory(_directory);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(file, Json));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch
        {
            // Never crash the widget over a locked or roaming profile.
        }
    }
}

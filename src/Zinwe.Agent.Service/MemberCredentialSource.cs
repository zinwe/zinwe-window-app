using System.Text.Json;
using Microsoft.Extensions.Options;
using Zinwe.Agent.Core;

namespace Zinwe.Agent.Service;

/// <summary>The member a PC enrols as: the owner's member email + the generated member id.</summary>
internal sealed record MemberCredential(string Email, string MemberId);

/// <summary>
/// Where the agent finds the member credential, highest priority first:
///   1. a PENDING file the member just typed in the widget (DataDirectory\pending-credential.json),
///   2. the MSI's registry values (silent/managed install),
///   3. Agent:MemberEmail + Agent:MemberId config (dev).
/// Read live, so a credential supplied after start is picked up without a restart.
/// </summary>
internal sealed class MemberCredentialSource(
    IOptionsMonitor<AgentOptions> options,
    ILogger<MemberCredentialSource> logger,
    string dataDirectory)
{
    private readonly string _pendingPath = Path.Combine(dataDirectory, "pending-credential.json");

    // member ids already used or refused this run; a removed PC can't silently re-join with a leftover id.
    private readonly HashSet<string> _consumed = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _lock = new();
    private int _submissions;

    /// <summary>Goes up each time the member submits a credential in the widget (the enrol loop wakes up for it).</summary>
    public int Submissions => Volatile.Read(ref _submissions);

    /// <summary>The usable member credential, or null when none is present (or it was already used/refused).</summary>
    public MemberCredential? Current()
    {
        var credential = FromPendingFile() ?? FromRegistry() ?? FromConfig();
        if (credential is null)
        {
            return null;
        }
        lock (_lock)
        {
            return _consumed.Contains(credential.MemberId) ? null : credential;
        }
    }

    /// <summary>Save a member-typed credential (from the widget) for the enrol loop to pick up within seconds.</summary>
    public void SubmitPending(string email, string memberId)
    {
        email = email.Trim();
        memberId = memberId.Trim();
        lock (_lock)
        {
            _consumed.Remove(memberId); // a re-typed id after a refusal should get a fresh attempt
        }
        try
        {
            Directory.CreateDirectory(dataDirectory);
            var tmp = _pendingPath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(new MemberCredential(email, memberId)));
            File.Move(tmp, _pendingPath, overwrite: true);
            Interlocked.Increment(ref _submissions);
            logger.LogInformation("Member credential submitted from the widget; attempting to connect.");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            logger.LogError(e, "Could not save the submitted member credential.");
        }
    }

    /// <summary>Called when the agent starts already enrolled: a leftover credential is stale.</summary>
    public void ConsumeAnyPresent()
    {
        while (Current() is { } credential)
        {
            Consume(credential);
        }
    }

    /// <summary>Called after a successful enrollment, and after a refusal. Clears the pending file + registry copy.</summary>
    public void Consume(MemberCredential credential)
    {
        lock (_lock)
        {
            _consumed.Add(credential.MemberId);
        }
        DeletePendingFile();
        var removed = MachineSettings.TryDeleteValue(MachineSettings.MemberIdValue);
        MachineSettings.TryDeleteValue(MachineSettings.MemberEmailValue);
        if (removed)
        {
            logger.LogInformation("Removed the member credential from the registry.");
        }
    }

    private MemberCredential? FromPendingFile()
    {
        try
        {
            if (File.Exists(_pendingPath))
            {
                var c = JsonSerializer.Deserialize<MemberCredential>(File.ReadAllText(_pendingPath));
                return Pair(c?.Email, c?.MemberId);
            }
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            // Unreadable: ignore; the member can re-submit.
        }
        return null;
    }

    private void DeletePendingFile()
    {
        try
        {
            if (File.Exists(_pendingPath))
            {
                File.Delete(_pendingPath);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // best effort
        }
    }

    private static MemberCredential? FromRegistry() => Pair(
        MachineSettings.ReadString(MachineSettings.MemberEmailValue),
        MachineSettings.ReadString(MachineSettings.MemberIdValue));

    private MemberCredential? FromConfig() => Pair(options.CurrentValue.MemberEmail, options.CurrentValue.MemberId);

    private static MemberCredential? Pair(string? email, string? memberId)
    {
        email = email?.Trim();
        memberId = memberId?.Trim();
        return string.IsNullOrEmpty(email) || string.IsNullOrEmpty(memberId)
            ? null
            : new MemberCredential(email, memberId);
    }
}

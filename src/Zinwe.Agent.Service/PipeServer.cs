using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using Zinwe.Agent.Core.Backend;
using Zinwe.Agent.Core.Ipc;

namespace Zinwe.Agent.Service;

/// <summary>
/// Local-only named pipe the tray app uses to talk to the service (status, connect, update, usage readings).
/// FIX-015 hardening:
/// <list type="bullet">
/// <item>ACL: SYSTEM full control; INTERACTIVE (people signed in at the PC, incl. Remote Desktop) read/write only -
///   no creating pipe instances, no changing the ACL; NETWORK denied, so the pipe is never reachable from another PC.
///   Under <c>dotnet run</c> (not SYSTEM) the developer's own account also gets full control of its dev pipe.</item>
/// <item>Client check: the connecting process must be the Zinwe tray from the install folder
///   (<see cref="PipePeers.IsTrustedTray(NamedPipeServerStream, string, out string?)"/>), else the pipe is closed
///   without a reply - for every request type, so no script can enrol, start an update or post fake usage.</item>
/// <item>"enroll" is taken only while the PC isn't connected (<see cref="PipeProtocol.AcceptsEnrollment"/>).</item>
/// </list>
/// Mixed versions during an update: an older tray still running from the same install folder passes the client check
/// (same path); a refused client just sees the pipe close, which every tray build treats as "Zinwe isn't running".
/// </summary>
internal sealed partial class PipeServer(
    AgentStateHolder state, MemberCredentialSource credentials, Update.AgentUpdater updater, ILogger<PipeServer> logger,
    Zinwe.Agent.Core.Features.Web.ForegroundInbox? inbox = null)
    : BackgroundService
{
    private const int MaxInstances = 8;
    private static readonly TimeSpan RefusalLogEvery = TimeSpan.FromMinutes(10);

    // One warning per refused program per 10 minutes, so a looping script can't flood the Event Log.
    private readonly ConcurrentDictionary<string, DateTimeOffset> _refusalsLogged = new(StringComparer.OrdinalIgnoreCase);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            NamedPipeServerStream pipe;
            try
            {
                pipe = NamedPipeServerStreamAcl.Create(
                    PipeProtocol.PipeName, PipeDirection.InOut, MaxInstances,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, CreateSecurity());
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // All instances busy (or another process owns the name): back off briefly.
                LogPipeCreateFailed(e.Message);
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
                continue;
            }

            try
            {
                await pipe.WaitForConnectionAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                await pipe.DisposeAsync();
                break;
            }

            _ = HandleAsync(pipe, stoppingToken); // serve concurrently; the loop immediately offers the next instance
        }
    }

    private async Task HandleAsync(NamedPipeServerStream pipe, CancellationToken stoppingToken)
    {
        await using (pipe)
        {
            try
            {
                if (!PipePeers.IsTrustedTray(pipe, AppContext.BaseDirectory, out var client))
                {
                    LogRefusedOnce(client);
                    return; // close without a word: an untrusted program learns nothing and can change nothing
                }

                using var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                cts.CancelAfter(TimeSpan.FromSeconds(5)); // a client that connects and goes silent can't pin an instance

                using var reader = new StreamReader(pipe, leaveOpen: true);
                await using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };

                var request = await PipeProtocol.ReadRequestAsync(reader, cts.Token);
                if (request?.Type == PipeProtocol.RequestTypes.Status)
                {
                    await writer.WriteLineAsync(PipeProtocol.Serialize(state.Current).AsMemory(), cts.Token);
                }
                else if (request?.Type == PipeProtocol.RequestTypes.Enroll)
                {
                    await writer.WriteLineAsync(PipeProtocol.Serialize(HandleEnroll(request)).AsMemory(), cts.Token);
                }
                else if (request?.Type == PipeProtocol.RequestTypes.Usage)
                {
                    // The tray's foreground readings; the web feature records them under the owner's settings.
                    var accepted = inbox?.Post(request.Usage) ?? 0;
                    await writer.WriteLineAsync(PipeProtocol.Serialize(new PipeProtocol.EnrollAck(inbox is not null, $"{accepted}")).AsMemory(), cts.Token);
                }
                else if (request?.Type == PipeProtocol.RequestTypes.Update)
                {
                    // "Update now": download, verify and install the offered release (progress goes on the status).
                    await writer.WriteLineAsync(PipeProtocol.Serialize(updater.Start()).AsMemory(), cts.Token);
                }
            }
            catch (Exception e) when (e is IOException or OperationCanceledException)
            {
                // Client went away or timed out -- nothing to do.
            }
            catch (Exception e)
            {
                LogClientFailed(e);
            }
        }
    }

    /// <summary>Save the member-typed credential so the enrol loop uses it; the widget shows the result via status.</summary>
    private PipeProtocol.EnrollAck HandleEnroll(PipeProtocol.Request request)
    {
        if (!PipeProtocol.AcceptsEnrollment(state.Current.State))
        {
            return new PipeProtocol.EnrollAck(false, "This PC is already connected to Zinwe.");
        }
        // Forgiving input (spaces, no dashes, lower case, a pasted label), plain errors; same rules as the widget.
        var (email, memberId, problem) = MemberIdInput.Validate(request.Email, request.MemberId);
        if (problem is not null)
        {
            return new PipeProtocol.EnrollAck(false, problem);
        }
        credentials.SubmitPending(email, memberId);
        // Show "Connecting…" at once: a refusal of the previous attempt must not flash up again for this one.
        state.Set(AgentState.Enrolling);
        return new PipeProtocol.EnrollAck(true, "Connecting this PC…");
    }

    private void LogRefusedOnce(string? client)
    {
        var key = client ?? "(unknown process)";
        var now = DateTimeOffset.UtcNow;
        if (_refusalsLogged.TryGetValue(key, out var last) && now - last < RefusalLogEvery)
        {
            return;
        }
        if (_refusalsLogged.Count > 256)
        {
            _refusalsLogged.Clear(); // bounded: many different programs just means more log lines
        }
        _refusalsLogged[key] = now;
        LogClientRefused(key);
    }

    private static PipeSecurity CreateSecurity()
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        // What the tray needs and nothing more: connect, read, write (no CreateNewInstance / ChangePermissions).
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.InteractiveSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));
        // Under `dotnet run` the service is a normal user process; let that user own its own (dev) pipe instances.
        using var current = WindowsIdentity.GetCurrent();
        if (!current.IsSystem)
        {
            security.AddAccessRule(new PipeAccessRule(current.User!, PipeAccessRights.FullControl, AccessControlType.Allow));
        }
        return security;
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Could not create pipe instance: {Error}")]
    private partial void LogPipeCreateFailed(string error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Pipe: refused a client that isn't the Zinwe tray: {Client}")]
    private partial void LogClientRefused(string client);

    [LoggerMessage(Level = LogLevel.Error, Message = "Pipe client handler failed")]
    private partial void LogClientFailed(Exception e);
}

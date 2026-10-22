using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using Zinwe.Agent.Core.Backend;
using Zinwe.Agent.Core.Ipc;

namespace Zinwe.Agent.Service;

/// <summary>
/// Local-only named pipe the tray app uses to ask the service for its status.
/// Signed-in users may read/write; only SYSTEM and Administrators may create the pipe or change its ACL.
/// Pipes are never reachable from the network because the ACL denies the NETWORK logon SID.
/// </summary>
internal sealed partial class PipeServer(
    AgentStateHolder state, MemberCredentialSource credentials, Update.AgentUpdater updater, ILogger<PipeServer> logger,
    Zinwe.Agent.Core.Features.Web.ForegroundInbox? inbox = null)
    : BackgroundService
{
    private const int MaxInstances = 8;

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
            catch (IOException e)
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

    private static PipeSecurity CreateSecurity()
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));
        // Under `dotnet run` the service is a normal user process; let that user own its own pipe instances.
        using var current = WindowsIdentity.GetCurrent();
        security.AddAccessRule(new PipeAccessRule(current.User!, PipeAccessRights.FullControl, AccessControlType.Allow));
        return security;
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Could not create pipe instance: {Error}")]
    private partial void LogPipeCreateFailed(string error);

    [LoggerMessage(Level = LogLevel.Error, Message = "Pipe client handler failed")]
    private partial void LogClientFailed(Exception e);
}

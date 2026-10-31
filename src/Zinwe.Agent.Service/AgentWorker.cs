using Microsoft.Extensions.Options;
using Zinwe.Agent.Core;
using Zinwe.Agent.Core.Backend;
using Zinwe.Agent.Core.Identity;
using Zinwe.Agent.Core.Ipc;
using Zinwe.Agent.Core.Messages;
using Zinwe.Agent.Core.Metrics;
using Zinwe.Agent.Core.Policy;
using Zinwe.Agent.Core.Work;

namespace Zinwe.Agent.Service;

/// <summary>
/// The agent's control loop: enroll once, then heartbeat forever.
///
///   NotEnrolled ──ID typed──► Enrolling ──ok──► Connected ◄──► Offline (network errors, backoff)
///        ▲                         │ refused                │ 401 = removed: company rules cleared
///        └── new Member ID ◄── Rejected ◄───────────────────┘
///
/// Enforcement hangs off this loop's policy_version, not off its health: the cached policy is applied at startup
/// before any network call, and losing the backend never switches protection off. Only the owner removing the PC
/// (401) clears the rules, since nothing is left to manage it. After each successful
/// heartbeat a changed policy_version triggers <see cref="PolicySync.SyncAsync"/> (fetch → cache → apply → event).
/// </summary>
internal sealed partial class AgentWorker(
    BackendClient backend,
    IdentityStore identityStore,
    MemberCredentialSource keySource,
    PolicySync policy,
    MessageSync messages,
    Zinwe.Agent.Core.Tasks.TaskSync tasks,
    Zinwe.Agent.Core.Tasks.TodoSync todos,
    Zinwe.Agent.Core.Pictures.PictureSync pictures,
    MetricsUploadPump metrics,
    WorkStatusReader work,
    WorkTransitionLog workLog,
    ConsentGate consentGate,
    AgentStateHolder state,
    RemovalMarker removal,
    NotificationWriter notifications,
    IOptions<AgentOptions> options,
    ILogger<AgentWorker> logger) : BackgroundService
{
    // Short so a credential the member types in the widget is picked up within a few seconds.
    private static readonly TimeSpan WaitForKeyInterval = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(5);
    // How often the chat loop checks for a reply the member just typed (a small local file read).
    private static readonly TimeSpan ChatReplyCheckInterval = TimeSpan.FromSeconds(2);
    // How often the heartbeat loop checks the widget's change log (a file-size check).
    private static readonly TimeSpan WorkChangeCheckInterval = TimeSpan.FromSeconds(2);
    // One enrol call never keeps the member waiting longer than this (no internet = a clear message, not a spinner).
    private static readonly TimeSpan EnrollTimeout = TimeSpan.FromSeconds(20);
    // After "can't reach Zinwe", retry the same Member ID this often (sooner when the member presses Connect again).
    private static readonly TimeSpan EnrollRetryInterval = TimeSpan.FromSeconds(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LogStarting(AgentInfo.Version, options.Value.BackendBaseUrl);

        // Protection first: the last known policy goes on before we even try the network.
        state.SetPolicy(policy.ApplyCached());

        // Removed by the owner before this restart: keep saying so (not "not set up yet") until a new connect.
        if (identityStore.Load() is null && removal.Load() is { } removed)
        {
            state.SetServerInfo(removed.CompanyName, null, null);
            state.Set(AgentState.Rejected, message: RemovedMessage(removed.CompanyName), removed: true);
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            var identity = identityStore.Load();
            if (identity is not null)
            {
                keySource.ConsumeAnyPresent();
            }
            else
            {
                identity = await EnrollAsync(stoppingToken);
            }

            if (identity is null)
            {
                await WaitForCredentialAsync(
                    state.Current.State == AgentState.NotEnrolled && state.Current.Message is not null
                        ? EnrollRetryInterval // the last try couldn't reach Zinwe: don't hammer it
                        : WaitForKeyInterval,
                    stoppingToken);
                continue;
            }
            await HeartbeatLoopAsync(identity, stoppingToken);
        }
    }

    /// <summary>Returns null when there is no usable member credential yet (caller waits and retries).</summary>
    private async Task<DeviceIdentity?> EnrollAsync(CancellationToken ct)
    {
        var credential = keySource.Current();
        if (credential is null)
        {
            // Rejected (refused ID, or removed) and "can't reach Zinwe" keep their message until a new attempt.
            if (state.Current.State is not (AgentState.Rejected or AgentState.NotEnrolled))
            {
                state.Set(AgentState.NotEnrolled);
            }
            return null;
        }

        state.Set(AgentState.Enrolling);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(EnrollTimeout);
        try
        {
            var response = await backend.EnrollAsync(
                new EnrollRequest(credential.Email, MemberIdInput.Normalize(credential.MemberId) ?? credential.MemberId,
                    MachineInfo.MachineId, MachineInfo.Hostname, MachineInfo.OsVersion, AgentInfo.Version), timeout.Token);

            var identity = new DeviceIdentity(response.DeviceId, response.DeviceToken, options.Value.BackendBaseUrl, DateTimeOffset.UtcNow);
            identityStore.Save(identity);
            keySource.Consume(credential);
            removal.Delete();
            tasks.Clear(); // a (re)connect may be another member: never show the last one's tasks
            todos.Clear(); // ... nor send their to-dos as the new member's
            pictures.Clear(); // ... nor their picture
            LogEnrolled(response.DeviceId, response.ReEnrolled);
            return identity;
        }
        catch (EnrollmentRejectedException e)
        {
            keySource.Consume(credential); // a refused credential will never start working; wait for a new one
            state.Set(AgentState.Rejected, message: EnrollmentMessages.ForRefusal(e.Field, e.Message));
            LogEnrollmentRejected(e.Message);
            return null;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            // Keep the credential: it's retried every EnrollRetryInterval, or at once when Connect is pressed again.
            var busy = e is HttpRequestException { StatusCode: { } code } && ((int)code == 429 || (int)code >= 500);
            state.Set(AgentState.NotEnrolled, message: busy ? EnrollmentMessages.ServerBusy : EnrollmentMessages.Unreachable);
            LogBackendUnreachable(e.Message);
            return null;
        }
    }

    /// <summary>Waits <paramref name="delay"/>, but returns at once when the member submits a credential in the widget.</summary>
    private async Task WaitForCredentialAsync(TimeSpan delay, CancellationToken ct)
    {
        var mark = keySource.Submissions;
        var deadline = DateTimeOffset.UtcNow + delay;
        while (DateTimeOffset.UtcNow < deadline && keySource.Submissions == mark)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(500), ct);
        }
    }

    private static string RemovedMessage(string? company) =>
        $"{(string.IsNullOrWhiteSpace(company) ? "Your company" : company.Trim())} removed this PC from Zinwe. Monitoring has stopped.";

    private async Task HeartbeatLoopAsync(DeviceIdentity identity, CancellationToken ct)
    {
        // The chat runs on its own, shorter loop for as long as this identity is heartbeating.
        using var chatCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var chatLoop = MessageLoopAsync(identity, chatCts.Token);
        try
        {
            await BeatAsync(identity, ct);
        }
        finally
        {
            await chatCts.CancelAsync();
            try
            {
                await chatLoop;
            }
            catch (OperationCanceledException)
            {
                // expected on stop / device removal
            }
        }
    }

    /// <summary>
    /// Owner↔member chat, the owner's tasks, the member's own to-dos and picture (FR-44): pulls them every <see cref="AgentOptions.MessagePollSeconds"/>, and
    /// sooner (within ~2s) when the member has typed a reply or acted on a task in the widget (the outbox has
    /// something). The only caller of <see cref="MessageSync"/>, <see cref="Zinwe.Agent.Core.Tasks.TaskSync"/> and
    /// <see cref="Zinwe.Agent.Core.Tasks.TodoSync"/>, so outbox drains never overlap.
    /// </summary>
    private async Task MessageLoopAsync(DeviceIdentity identity, CancellationToken ct)
    {
        var interval = TimeSpan.FromSeconds(Math.Clamp(options.Value.MessagePollSeconds, 5, 300));
        var next = DateTimeOffset.MinValue;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (DateTimeOffset.UtcNow >= next || messages.HasPendingReplies || pictures.HasWork)
                {
                    await messages.SyncAsync(identity.DeviceToken, ct);
                    await tasks.SyncAsync(identity.DeviceToken, ct);
                    await todos.SyncAsync(identity.DeviceToken, ct);
                    await pictures.SyncAsync(identity.DeviceToken, ct);
                    next = DateTimeOffset.UtcNow + interval;
                }
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // SyncAsync handles network/file faults itself; anything else must not take the agent down.
                LogChatLoopFailed(e.Message);
                next = DateTimeOffset.UtcNow + interval;
            }
            await Task.Delay(ChatReplyCheckInterval, ct);
        }
    }

    /// <summary>
    /// Waits out the heartbeat interval, but heartbeats at once (within ~2s) when the member logs in, takes a
    /// break, comes back or logs out, so the owner's Activity and "who's working" are live.
    /// </summary>
    private async Task WaitForNextBeatAsync(TimeSpan interval, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow + interval;
        var mark = workLog.Fingerprint();
        // The member agreeing, declining or withdrawing reaches their employer within seconds too.
        var consentMark = consentGate.Fingerprint();
        while (DateTimeOffset.UtcNow < deadline)
        {
            var left = deadline - DateTimeOffset.UtcNow;
            await Task.Delay(left < WorkChangeCheckInterval ? left : WorkChangeCheckInterval, ct);
            if (workLog.Fingerprint() != mark || consentGate.Fingerprint() != consentMark)
            {
                return;
            }
        }
    }

    private async Task BeatAsync(DeviceIdentity identity, CancellationToken ct)
    {
        var interval = TimeSpan.FromSeconds(options.Value.HeartbeatSeconds);
        var failures = 0;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var ws = work.Read(DateTimeOffset.Now);
                // Every log in / break / back / log out since the last heartbeat that got through, with exact times.
                var workEvents = workLog.Pending(DateTimeOffset.Now);
                // The member's answer on the widget's consent screen (DPDP record): "given" or "declined", with the
                // notice version. Until they agree, the event queue and metrics store record nothing (ConsentGate);
                // a decline also makes the backend send this PC a policy with monitoring off.
                var consent = consentGate.ForHeartbeat();
                var given = consent is { IsGiven: true };
                var beat = await backend.HeartbeatAsync(
                    identity.DeviceToken,
                    new HeartbeatRequest(MachineInfo.Hostname, MachineInfo.OsVersion, AgentInfo.Version,
                        ws.State, ws.WorkedSeconds, ws.BreakSeconds, ws.ChangedAt, ws.BreakReason, ws.BreakNote,
                        ws.EndReason, ws.EndNote, workEvents,
                        // The older fields mean "given": only sent for an agreement.
                        given ? consent!.Version : null, given ? consent!.At : null,
                        consent is null ? null : new ConsentDecision(consent.State, consent.Version)), ct);
                workLog.Acknowledge(workEvents);
                state.SetServerInfo(beat.CompanyName, beat.OwnerName, beat.ConsentState);
                pictures.OnHeartbeat(beat.Picture); // FR-44: no field = a backend without pictures

                failures = 0;
                interval = TimeSpan.FromSeconds(Math.Clamp(beat.NextHeartbeatSeconds, 15, 3600));
                if (beat.UpdateRequired)
                {
                    // Forced upgrade (M12): protection and the work day carry on; the widget asks the employee to
                    // install the update until a newer agent reports in.
                    state.Set(AgentState.UpdateRequired, identity.DeviceId, DateTimeOffset.UtcNow,
                        message: "Zinwe needs an update. Your times are saved and you can keep working.",
                        downloadUrl: beat.DownloadUrl);
                    LogUpdateRequired(beat.DownloadUrl ?? "(none)");
                }
                else
                {
                    state.Set(AgentState.Connected, identity.DeviceId, DateTimeOffset.UtcNow);
                }
                // A newer release published from the admin app (null = up to date): the widget's Update button.
                state.SetUpdate(beat.Update);
                LogHeartbeat(identity.DeviceId, beat.PolicyVersion, (int)interval.TotalSeconds);

                // Fetch failures are logged inside and retried next beat; a 401 here lands in the catch below.
                if (await policy.SyncAsync(identity.DeviceToken, beat.PolicyVersion, ct))
                {
                    state.SetPolicy(policy.Current);
                }
                // The owner pressed "Restore USB" (after an over-limit lockdown): back to the policy's USB rule.
                if (beat.UsbRestore == true && policy.RestoreUsb())
                {
                    NotifyUsbRestored();
                }
                await metrics.UploadAsync(identity.DeviceToken, DateTimeOffset.Now, ct); // usage metrics for the dashboard
                await WaitForNextBeatAsync(interval, ct);
            }
            catch (DeviceRejectedException e)
            {
                // Removed by the owner: nothing of the company's stays in force on a PC it no longer manages.
                var company = state.Current.CompanyName;
                policy.ClearForRemoval();
                state.SetPolicy(null);
                identityStore.Delete();
                removal.Save(new RemovalRecord(company, DateTimeOffset.UtcNow));
                tasks.Clear();
                todos.Clear();
                pictures.Clear();
                state.Set(AgentState.Rejected, message: RemovedMessage(company), removed: true);
                LogDeviceRejected(identity.DeviceId, e.Message);
                return; // back to the enrollment loop, which waits for a new Member ID
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                failures++;
                state.Set(AgentState.Offline, identity.DeviceId, message: "Can't reach Zinwe. Protection stays on.");
                var backoff = TimeSpan.FromSeconds(Math.Min(MaxBackoff.TotalSeconds, interval.TotalSeconds * Math.Pow(2, failures - 1)));
                LogHeartbeatFailed(failures, (int)backoff.TotalSeconds, e.Message);
                await Task.Delay(backoff, ct);
            }
        }
    }

    /// <summary>Tell the member on their widget that USB works again (best effort).</summary>
    private void NotifyUsbRestored()
    {
        try
        {
            notifications.AddNotices([new AgentMessage(-DateTimeOffset.UtcNow.Ticks, "USB drives work again",
                "Your admin restored USB on this PC. Your company's usual USB rule applies again.",
                Core.Events.EventSeverity.Info, DateTimeOffset.Now)]);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // the widget note is best effort
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Zinwe agent {Version} starting; backend {BackendBaseUrl}")]
    private partial void LogStarting(string version, string backendBaseUrl);

    [LoggerMessage(Level = LogLevel.Information, Message = "Enrolled as device {DeviceId} (re-enrolled: {ReEnrolled})")]
    private partial void LogEnrolled(long deviceId, bool reEnrolled);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Enrollment key refused: {Reason}")]
    private partial void LogEnrollmentRejected(string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Backend unreachable during enrollment: {Error}")]
    private partial void LogBackendUnreachable(string error);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Heartbeat ok for device {DeviceId}; policy v{PolicyVersion}; next in {Seconds}s")]
    private partial void LogHeartbeat(long deviceId, int policyVersion, int seconds);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Device {DeviceId} rejected by backend ({Reason}); identity cleared")]
    private partial void LogDeviceRejected(long deviceId, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Update required by backend; work paused until the agent is updated. Download: {DownloadUrl}")]
    private partial void LogUpdateRequired(string downloadUrl);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Heartbeat failed ({Failures} in a row); retrying in {Seconds}s: {Error}")]
    private partial void LogHeartbeatFailed(int failures, int seconds, string error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Chat sync failed; retrying next round: {Error}")]
    private partial void LogChatLoopFailed(string error);
}

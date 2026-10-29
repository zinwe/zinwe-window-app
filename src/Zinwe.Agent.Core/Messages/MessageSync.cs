using Microsoft.Extensions.Logging;
using Zinwe.Agent.Core.Backend;

namespace Zinwe.Agent.Core.Messages;

/// <summary>
/// Keeps the owner↔member chat in step: posts the member's queued replies, pulls the conversation into the
/// widget's notification file, and reports "read" once the member has opened the admin's messages.
/// Run on its own short loop (see AgentWorker), not on the heartbeat.
/// Best-effort: network/HTTP failures are logged and retried next round (a real 401 is handled by the
/// heartbeat path, which owns the device-rejected state). Never throws to the caller except on cancellation.
/// </summary>
public sealed class MessageSync(BackendClient backend, NotificationWriter writer, MessageOutbox outbox, ILogger<MessageSync> logger)
{
    /// <summary>True while the member has typed a reply (or feedback) the agent hasn't posted yet (sync right away).</summary>
    /// <remarks>A waiting picture change (FR-44) doesn't count: <c>PictureSync.HasWork</c> paces its own retries.</remarks>
    public bool HasPendingReplies => outbox.HasPendingOtherThanPicture;

    public async Task SyncAsync(string deviceToken, CancellationToken ct)
    {
        // Nobody signed in to this PC: there is no widget to deliver to, so don't pull. That keeps the admin's
        // "Delivered" meaning "it's on the member's screen", not "the PC was on at the login screen".
        if (!writer.HasSignedInUser)
        {
            return;
        }
        try
        {
            // 0) Feedback for the Zinwe team first: it doesn't depend on the plan having chat.
            await DrainFeedbackAsync(deviceToken, ct);

            // 1) Push any replies the member typed in the widget, so they reach the admin...
            await DrainOutboxAsync(deviceToken, ct);

            // 2) ...then pull the whole conversation (both directions) into the widget.
            var messages = await backend.GetMessagesAsync(deviceToken, ct);
            var added = writer.MergeChat(messages);
            if (added > 0)
            {
                logger.LogInformation("Delivered {Count} new message(s) to the widget", added);
            }

            // 3) Tell the backend the member has READ the admin's messages - only once they opened them in the
            //    widget's chat (Read=true locally). Skipped when nothing is unread server-side, so the short
            //    poll doesn't write on every round.
            var unreadOnServer = messages
                .Where(m => !m.ReadByMember && !string.Equals(m.Sender, "member", StringComparison.OrdinalIgnoreCase))
                .Select(m => m.Id)
                .ToList();
            if (unreadOnServer.Count > 0 && writer.AllReadLocally(unreadOnServer))
            {
                await backend.MarkMessagesReadAsync(deviceToken, ct);
            }
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            logger.LogDebug("Message sync deferred: {Error}", e.Message);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            logger.LogError(e, "Could not write delivered messages to the widget file");
        }
    }

    private async Task DrainFeedbackAsync(string deviceToken, CancellationToken ct)
    {
        var done = new List<string>();
        try
        {
            foreach (var item in outbox.PendingFeedback())
            {
                if (!await backend.SendFeedbackAsync(deviceToken, item.Rating ?? 5, item.Body, ct))
                {
                    logger.LogWarning("The backend refused the member's feedback; dropping it");
                }
                done.Add(item.Id);
            }
        }
        finally
        {
            outbox.Remove(done);
            if (done.Count > 0)
            {
                logger.LogInformation("Sent {Count} feedback item(s) to the Zinwe team", done.Count);
            }
        }
    }

    /// <summary>Post each pending member reply; remove the ones that went through. Stops on the first network error.</summary>
    private async Task DrainOutboxAsync(string deviceToken, CancellationToken ct)
    {
        var pending = outbox.Pending();
        if (pending.Count == 0)
        {
            return;
        }
        var posted = new List<string>();
        try
        {
            foreach (var reply in pending)
            {
                await backend.ReplyAsync(deviceToken, reply.Body, ct);
                posted.Add(reply.Id); // only after a confirmed 2xx, so we never double-send
            }
        }
        finally
        {
            // Remove everything confirmed-sent even if a later one failed (prevents re-sending duplicates).
            outbox.Remove(posted);
            if (posted.Count > 0)
            {
                logger.LogInformation("Sent {Count} member reply(ies)", posted.Count);
            }
        }
    }
}

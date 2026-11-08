using System.Globalization;
using System.Net;
using Zinwe.Agent.Core.Chat;

namespace Zinwe.Agent.Core.Backend;

// v1.4 · team chat (§1b /api/devices/chat/*) and the task chat (§1a tasks/<id>/chat/). Same rules as the rest of the
// client: a 403/404/405 is "this backend (or plan) doesn't have it" (null / Off: the app falls back to 1.3.11's
// behaviour), another 4xx (not 429) is a refusal for good, and network errors, 429 and 5xx throw so the caller retries.
public sealed partial class BackendClient
{
    private const string ChatRoot = "api/devices/chat/";

    private static string Id(long id) => id.ToString(CultureInfo.InvariantCulture);

    private static bool IsOff(HttpResponseMessage r) =>
        r.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden or HttpStatusCode.MethodNotAllowed;

    /// <summary>GET chat/: the member's groups and direct chats. Null = a backend without team chat.</summary>
    public async Task<IReadOnlyList<TeamConversation>?> GetConversationsAsync(string deviceToken, CancellationToken ct)
    {
        using var response = await SendAsDeviceAsync(HttpMethod.Get, ChatRoot, deviceToken, null, ct);
        if (IsOff(response))
        {
            return null;
        }
        response.EnsureSuccessStatusCode();
        return ChatJson.Conversations(await response.Content.ReadAsStringAsync(ct));
    }

    /// <summary>GET chat/people/: the teammates this member may message (they share a group). Null = not on this backend.</summary>
    public async Task<IReadOnlyList<TeamMember>?> GetChatPeopleAsync(string deviceToken, CancellationToken ct)
    {
        using var response = await SendAsDeviceAsync(HttpMethod.Get, ChatRoot + "people/", deviceToken, null, ct);
        if (IsOff(response))
        {
            return null;
        }
        response.EnsureSuccessStatusCode();
        return ChatJson.People(await response.Content.ReadAsStringAsync(ct));
    }

    /// <summary>GET chat/&lt;id&gt;/messages/: the lines of one conversation, oldest first. Null = it's gone (or no chat).</summary>
    public async Task<IReadOnlyList<TeamMessage>?> GetConversationMessagesAsync(string deviceToken, long conversationId, CancellationToken ct) =>
        (await FetchConversationAsync(deviceToken, conversationId, ct)).Messages;

    /// <summary>
    /// v1.4.1 · GET chat/&lt;id&gt;/messages/ telling a deleted conversation (404: <c>Gone</c>) from a backend without team
    /// chat for this PC (403/405: no messages, not gone). Only the list endpoint decides that the chat itself is off.
    /// </summary>
    public async Task<(IReadOnlyList<TeamMessage>? Messages, bool Gone)> FetchConversationAsync(string deviceToken, long conversationId,
        CancellationToken ct)
    {
        using var response = await SendAsDeviceAsync(HttpMethod.Get, $"{ChatRoot}{Id(conversationId)}/messages/", deviceToken, null, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return (null, true);
        }
        if (IsOff(response))
        {
            return (null, false);
        }
        response.EnsureSuccessStatusCode();
        return (ChatJson.Messages(await response.Content.ReadAsStringAsync(ct)), false);
    }

    /// <summary>v1.4.1 · PATCH chat/&lt;id&gt;/messages/&lt;mid&gt;/ {body}: the member's own line, within 15 minutes.</summary>
    public Task<ChatWriteResult> EditConversationMessageAsync(string deviceToken, long conversationId, long messageId, string body,
        CancellationToken ct) =>
        WriteLineAsync(HttpMethod.Patch, deviceToken, conversationId, messageId,
            JsonBody(new ChatMessageRequest(body.Trim()), BackendJsonContext.Default.ChatMessageRequest), ct);

    /// <summary>v1.4.1 · DELETE chat/&lt;id&gt;/messages/&lt;mid&gt;/: everyone then sees "This message was deleted".</summary>
    public Task<ChatWriteResult> DeleteConversationMessageAsync(string deviceToken, long conversationId, long messageId, CancellationToken ct) =>
        WriteLineAsync(HttpMethod.Delete, deviceToken, conversationId, messageId, null, ct);

    private async Task<ChatWriteResult> WriteLineAsync(HttpMethod method, string deviceToken, long conversationId, long messageId,
        HttpContent? body, CancellationToken ct)
    {
        using var response = await SendAsDeviceAsync(method, $"{ChatRoot}{Id(conversationId)}/messages/{Id(messageId)}/", deviceToken, body, ct);
        var outcome = ConversationOutcome(response);
        if (outcome == ChatWriteOutcome.Done)
        {
            var text = await response.Content.ReadAsStringAsync(ct);
            return new ChatWriteResult(outcome, Line: text.TrimStart().StartsWith('{') ? ChatJson.Messages("[" + text + "]").FirstOrDefault() : null);
        }
        return new ChatWriteResult(outcome, outcome == ChatWriteOutcome.Refused ? await RefusalTextAsync(response, ct) : null);
    }

    /// <summary>The backend's own sentence for a refusal, or null when it sent none we can show.</summary>
    private static async Task<string?> RefusalTextAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var (_, message) = await ReadFirstErrorAsync(response, ct);
        return message.StartsWith("The backend refused", StringComparison.Ordinal) ? null : message;
    }

    /// <summary>POST chat/&lt;id&gt;/messages/ (a reply quotes <paramref name="replyTo"/>).</summary>
    public async Task<ChatWriteOutcome> PostConversationMessageAsync(string deviceToken, long conversationId, string body, long? replyTo,
        CancellationToken ct)
    {
        using var response = await SendAsDeviceAsync(HttpMethod.Post, $"{ChatRoot}{Id(conversationId)}/messages/", deviceToken,
            JsonBody(new ChatMessageRequest(body.Trim(), replyTo), BackendJsonContext.Default.ChatMessageRequest), ct);
        return ConversationOutcome(response);
    }

    /// <summary>POST chat/&lt;id&gt;/read/: the member read this conversation. Best effort; <c>Gone</c> when it was deleted.</summary>
    public async Task<ChatWriteOutcome> MarkConversationReadAsync(string deviceToken, long conversationId, CancellationToken ct)
    {
        using var response = await SendAsDeviceAsync(HttpMethod.Post, $"{ChatRoot}{Id(conversationId)}/read/", deviceToken, null, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return ChatWriteOutcome.Gone;
        }
        if (!IsOff(response) && (int)response.StatusCode is not (>= 400 and < 500))
        {
            response.EnsureSuccessStatusCode();
        }
        return ChatWriteOutcome.Done;
    }

    /// <summary>PATCH chat/&lt;id&gt;/ {pinned?, muted?, unread?}: the member's pin, mute or "mark as unread" (api-v14 §2).</summary>
    public async Task<ChatWriteOutcome> SetConversationPrefsAsync(string deviceToken, long conversationId, ChatPrefsRequest prefs, CancellationToken ct) =>
        (await UpdateConversationPrefsAsync(deviceToken, conversationId, prefs, ct)).Outcome;

    /// <summary>As <see cref="SetConversationPrefsAsync"/>, with the backend's sentence when it refused ("You can pin up to 3 chats.").</summary>
    public async Task<ChatWriteResult> UpdateConversationPrefsAsync(string deviceToken, long conversationId, ChatPrefsRequest prefs, CancellationToken ct)
    {
        using var response = await SendAsDeviceAsync(HttpMethod.Patch, $"{ChatRoot}{Id(conversationId)}/", deviceToken,
            JsonBody(prefs, BackendJsonContext.Default.ChatPrefsRequest), ct);
        var outcome = ConversationOutcome(response);
        return new ChatWriteResult(outcome, outcome == ChatWriteOutcome.Refused ? await RefusalTextAsync(response, ct) : null);
    }

    /// <summary>POST chat/direct/ {member_id}: the direct chat with a teammate (made on first use).</summary>
    public async Task<(ChatWriteOutcome Outcome, TeamConversation? Conversation)> StartDirectAsync(string deviceToken, long memberId,
        CancellationToken ct)
    {
        using var response = await SendAsDeviceAsync(HttpMethod.Post, ChatRoot + "direct/", deviceToken,
            JsonBody(new DirectChatRequest(memberId), BackendJsonContext.Default.DirectChatRequest), ct);
        var outcome = Outcome(response);
        return outcome == ChatWriteOutcome.Done
            ? (outcome, ChatJson.ConversationReply(await response.Content.ReadAsStringAsync(ct)))
            : (outcome, null);
    }

    /// <summary>POST chat/&lt;id&gt;/report/ {message_id?, note}: to the admin, with only that message (or the last few).</summary>
    public async Task<ChatWriteOutcome> ReportChatAsync(string deviceToken, long conversationId, long? messageId, string note,
        CancellationToken ct)
    {
        using var response = await SendAsDeviceAsync(HttpMethod.Post, $"{ChatRoot}{Id(conversationId)}/report/", deviceToken,
            JsonBody(new ChatReportRequest(note.Trim(), messageId), BackendJsonContext.Default.ChatReportRequest), ct);
        return ConversationOutcome(response);
    }

    /// <summary>GET tasks/&lt;id&gt;/chat/: everyone's lines on a task. Null = a 1.3 backend (its notes stand in).</summary>
    public async Task<IReadOnlyList<TeamMessage>?> GetTaskChatAsync(string deviceToken, long taskId, CancellationToken ct)
    {
        using var response = await SendAsDeviceAsync(HttpMethod.Get, $"api/devices/tasks/{Id(taskId)}/chat/", deviceToken, null, ct);
        if (IsOff(response))
        {
            return null;
        }
        response.EnsureSuccessStatusCode();
        return ChatJson.Messages(await response.Content.ReadAsStringAsync(ct));
    }

    /// <summary>POST tasks/&lt;id&gt;/chat/ {body}: Off on a 1.3 backend (the caller posts a task note instead).</summary>
    public async Task<ChatWriteOutcome> PostTaskChatAsync(string deviceToken, long taskId, string body, CancellationToken ct)
    {
        using var response = await SendAsDeviceAsync(HttpMethod.Post, $"api/devices/tasks/{Id(taskId)}/chat/", deviceToken,
            JsonBody(new ChatMessageRequest(body.Trim()), BackendJsonContext.Default.ChatMessageRequest), ct);
        return Outcome(response);
    }

    /// <summary>POST tasks/&lt;id&gt;/chat/read/. Best effort.</summary>
    public async Task MarkTaskChatReadAsync(string deviceToken, long taskId, CancellationToken ct)
    {
        using var response = await SendAsDeviceAsync(HttpMethod.Post, $"api/devices/tasks/{Id(taskId)}/chat/read/", deviceToken, null, ct);
        if (!IsOff(response) && (int)response.StatusCode is not (>= 400 and < 500))
        {
            response.EnsureSuccessStatusCode();
        }
    }

    /// <summary>A write on one conversation's own route: a 404 there means that conversation was deleted (v1.4.1).</summary>
    private static ChatWriteOutcome ConversationOutcome(HttpResponseMessage response) =>
        response.StatusCode == HttpStatusCode.NotFound ? ChatWriteOutcome.Gone : Outcome(response);

    private static ChatWriteOutcome Outcome(HttpResponseMessage response)
    {
        if (IsOff(response))
        {
            return ChatWriteOutcome.Off;
        }
        var code = (int)response.StatusCode;
        if (code is >= 400 and < 500 and not 429)
        {
            return ChatWriteOutcome.Refused;
        }
        response.EnsureSuccessStatusCode();
        return ChatWriteOutcome.Done;
    }
}

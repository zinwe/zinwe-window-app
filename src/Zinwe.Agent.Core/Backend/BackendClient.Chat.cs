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
    public async Task<IReadOnlyList<TeamMessage>?> GetConversationMessagesAsync(string deviceToken, long conversationId, CancellationToken ct)
    {
        using var response = await SendAsDeviceAsync(HttpMethod.Get, $"{ChatRoot}{Id(conversationId)}/messages/", deviceToken, null, ct);
        if (IsOff(response))
        {
            return null;
        }
        response.EnsureSuccessStatusCode();
        return ChatJson.Messages(await response.Content.ReadAsStringAsync(ct));
    }

    /// <summary>POST chat/&lt;id&gt;/messages/ (a reply quotes <paramref name="replyTo"/>).</summary>
    public async Task<ChatWriteOutcome> PostConversationMessageAsync(string deviceToken, long conversationId, string body, long? replyTo,
        CancellationToken ct)
    {
        using var response = await SendAsDeviceAsync(HttpMethod.Post, $"{ChatRoot}{Id(conversationId)}/messages/", deviceToken,
            JsonBody(new ChatMessageRequest(body.Trim(), replyTo), BackendJsonContext.Default.ChatMessageRequest), ct);
        return Outcome(response);
    }

    /// <summary>POST chat/&lt;id&gt;/read/: the member read this conversation. Best effort.</summary>
    public async Task MarkConversationReadAsync(string deviceToken, long conversationId, CancellationToken ct)
    {
        using var response = await SendAsDeviceAsync(HttpMethod.Post, $"{ChatRoot}{Id(conversationId)}/read/", deviceToken, null, ct);
        if (!IsOff(response) && (int)response.StatusCode is not (>= 400 and < 500))
        {
            response.EnsureSuccessStatusCode();
        }
    }

    /// <summary>PATCH chat/&lt;id&gt;/ {pinned?, muted?, unread?}: the member's pin, mute or "mark as unread" (api-v14 §2).</summary>
    public async Task<ChatWriteOutcome> SetConversationPrefsAsync(string deviceToken, long conversationId, ChatPrefsRequest prefs, CancellationToken ct)
    {
        using var response = await SendAsDeviceAsync(HttpMethod.Patch, $"{ChatRoot}{Id(conversationId)}/", deviceToken,
            JsonBody(prefs, BackendJsonContext.Default.ChatPrefsRequest), ct);
        return Outcome(response);
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
        return Outcome(response);
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

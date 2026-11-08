using System.Net;
using Zinwe.Agent.Core.Chat;

namespace Zinwe.Agent.Core.Backend;

// v1.5 · file sharing in chat: the quick calls (is it on; post the line that carries an uploaded file). The upload and the
// downloads themselves go through ChatFileTransfer, which has a client of its own with a timeout long enough for 25 MB.
// Same rules as the rest: 404/405 = a backend without files, another 4xx (not 429) is final, network/429/5xx throw.
public sealed partial class BackendClient
{
    /// <summary>The device's file route (upload with POST; <c>&lt;id&gt;/</c> to download).</summary>
    public const string ChatFilesPath = "api/devices/chat/files/";

    /// <summary>
    /// GET chat/files/: <c>{"allowed", "max_bytes", "keep_days"}</c>. Null = a backend without file sharing (404/405, or a
    /// 403 / an unreadable body): no attach button. Throws on network errors, 429 and 5xx (the last answer stays).
    /// </summary>
    public async Task<ChatFilesInfo?> GetChatFilesInfoAsync(string deviceToken, CancellationToken ct)
    {
        using var response = await SendAsDeviceAsync(HttpMethod.Get, ChatFilesPath, deviceToken, null, ct);
        if (IsOff(response))
        {
            return null;
        }
        var code = (int)response.StatusCode;
        if (code is >= 400 and < 500 and not 429)
        {
            return null;
        }
        response.EnsureSuccessStatusCode();
        return ChatFiles.InfoFromJson(await response.Content.ReadAsStringAsync(ct));
    }

    /// <summary>
    /// POST chat/&lt;id&gt;/messages/ {body: caption, file_id}: the line that carries an uploaded file in a group or direct
    /// chat. <c>Gone</c> = that conversation was deleted (404).
    /// </summary>
    public async Task<FilePostResult> PostConversationFileAsync(string deviceToken, long conversationId, string caption, string fileId,
        CancellationToken ct)
    {
        using var response = await SendAsDeviceAsync(HttpMethod.Post, $"{ChatRoot}{Id(conversationId)}/messages/", deviceToken,
            JsonBody(new ChatMessageRequest(caption.Trim(), null, fileId), BackendJsonContext.Default.ChatMessageRequest), ct);
        return await FilePostAsync(response, ConversationOutcome(response), ct);
    }

    /// <summary>POST messages/ {body: caption, file_id}: the line that carries an uploaded file in the admin chat.</summary>
    public async Task<FilePostResult> PostAdminFileAsync(string deviceToken, string caption, string fileId, CancellationToken ct)
    {
        using var response = await SendAsDeviceAsync(HttpMethod.Post, "api/devices/messages/", deviceToken,
            JsonBody(new MemberReplyRequest(caption.Trim(), fileId), BackendJsonContext.Default.MemberReplyRequest), ct);
        return await FilePostAsync(response, Outcome(response), ct);
    }

    private static async Task<FilePostResult> FilePostAsync(HttpResponseMessage response, ChatWriteOutcome outcome, CancellationToken ct)
    {
        // A 403 (file sharing or chat turned off) carries the backend's sentence; a 404/405 is just "not here".
        if (outcome is not (ChatWriteOutcome.Refused or ChatWriteOutcome.Off) || IsMissingRoute(response))
        {
            return new FilePostResult(outcome);
        }
        var (field, message) = await ReadFirstErrorAsync(response, ct);
        return new FilePostResult(outcome, field.Length > 0 ? field : null,
            message.StartsWith("The backend refused", StringComparison.Ordinal) ? null : message);
    }

    /// <summary>The 4xx sentence of a refusal (upload, download), or null when it sent none we can show.</summary>
    internal static async Task<string?> RefusalSentenceAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var (_, message) = await ReadFirstErrorAsync(response, ct);
        return message.StartsWith("The backend refused", StringComparison.Ordinal) ? null : message;
    }

    /// <summary>True for 404/405: this backend has no such route (403 is a refusal with a sentence here).</summary>
    internal static bool IsMissingRoute(HttpResponseMessage r) =>
        r.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed;
}

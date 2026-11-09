using System.Net;
using Zinwe.Agent.Core.Chat;

namespace Zinwe.Agent.Core.Backend;

// v1.5 · file sharing in chat: the backend calls (BRIEF2 §2). The bytes never go to the backend: they go through the relay
// (ChatFileTransfer), with tickets these calls hand out. Same rules as the rest: 404/405 = a backend without files, another
// 4xx (not 429) is final, network/429/5xx throw so the caller retries.
public sealed partial class BackendClient
{
    /// <summary>The device's file route (register with POST; <c>&lt;id&gt;/</c> to PATCH; round 1: to download).</summary>
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
    /// GET chat/files/serve-ticket/: the 24 h ticket this PC long-polls the relay with. Null = a backend without the relay
    /// (round 1 or older: 404/405, or another refusal). Throws on network errors, 429 and 5xx.
    /// </summary>
    public async Task<ServeTicket?> GetServeTicketAsync(string deviceToken, CancellationToken ct)
    {
        using var response = await SendAsDeviceAsync(HttpMethod.Get, ChatFilesPath + "serve-ticket/", deviceToken, null, ct);
        var code = (int)response.StatusCode;
        if (code is >= 400 and < 500 and not 429)
        {
            return null;
        }
        response.EnsureSuccessStatusCode();
        return ChatFiles.ServeTicketFromJson(await response.Content.ReadAsStringAsync(ct), DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// POST chat/files/ (JSON): register a file that stays on this PC. 201 = its FileJSON (not on a line yet).
    /// <c>Off</c> = 404/405; <c>Refused</c> = another 4xx with the backend's sentence ("Files can be up to 2 GB on your
    /// plan.", sharing off, too many waiting).
    /// </summary>
    public async Task<FileRegisterResult> RegisterFileAsync(string deviceToken, FileRegisterRequest file, CancellationToken ct)
    {
        using var response = await SendAsDeviceAsync(HttpMethod.Post, ChatFilesPath, deviceToken,
            JsonBody(file, BackendJsonContext.Default.FileRegisterRequest), ct);
        var code = (int)response.StatusCode;
        if (IsMissingRoute(response))
        {
            return new FileRegisterResult(ChatWriteOutcome.Off, Status: code);
        }
        if (code is >= 400 and < 500 and not 429)
        {
            return new FileRegisterResult(ChatWriteOutcome.Refused, Error: await RefusalSentenceAsync(response, ct), Status: code);
        }
        response.EnsureSuccessStatusCode();
        var registered = ChatFiles.FromJson(await response.Content.ReadAsStringAsync(ct));
        return registered is not null && ChatFiles.IsValidId(registered.Id)
            ? new FileRegisterResult(ChatWriteOutcome.Done, registered, Status: code)
            : throw new HttpRequestException("The backend returned an unexpected registration reply.");
    }

    /// <summary>
    /// PATCH chat/files/&lt;id&gt;/ with the hash or "gone". Done also when the file is no longer there (404/410): there is
    /// nothing left to tell. Refused = another 4xx (not this PC's file). Throws on network errors, 429 and 5xx.
    /// </summary>
    public async Task<ChatWriteOutcome> PatchFileAsync(string deviceToken, string fileId, FilePatchRequest patch, CancellationToken ct)
    {
        if (!ChatFiles.IsValidId(fileId))
        {
            return ChatWriteOutcome.Refused;
        }
        using var response = await SendAsDeviceAsync(HttpMethod.Patch, ChatFilesPath + fileId + "/", deviceToken,
            JsonBody(patch, BackendJsonContext.Default.FilePatchRequest), ct);
        return FileWriteOutcome(response);
    }

    /// <summary>POST chat/files/&lt;id&gt;/stop/: the member stops sharing one of their files. Done also when it is already gone.</summary>
    public async Task<ChatWriteOutcome> StopFileAsync(string deviceToken, string fileId, CancellationToken ct)
    {
        if (!ChatFiles.IsValidId(fileId))
        {
            return ChatWriteOutcome.Refused;
        }
        using var response = await SendAsDeviceAsync(HttpMethod.Post, ChatFilesPath + fileId + "/stop/", deviceToken, null, ct);
        return FileWriteOutcome(response);
    }

    private static ChatWriteOutcome FileWriteOutcome(HttpResponseMessage response)
    {
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
        {
            return ChatWriteOutcome.Done;
        }
        var code = (int)response.StatusCode;
        if (response.StatusCode == HttpStatusCode.MethodNotAllowed)
        {
            return ChatWriteOutcome.Off;
        }
        if (code is >= 400 and < 500 and not 429)
        {
            return ChatWriteOutcome.Refused;
        }
        response.EnsureSuccessStatusCode();
        return ChatWriteOutcome.Done;
    }

    /// <summary>
    /// POST chat/files/&lt;id&gt;/ticket/: a 15-minute get ticket. <c>Gone</c> = 404/410 ("This file isn't shared any
    /// more.", or not this member's to see); <c>Off</c> = 405; <c>Refused</c> = another 4xx. Throws on network errors, 429 and 5xx.
    /// </summary>
    public async Task<FileTicketResult> GetFileTicketAsync(string deviceToken, string fileId, CancellationToken ct)
    {
        if (!ChatFiles.IsValidId(fileId))
        {
            return new FileTicketResult(ChatWriteOutcome.Gone);
        }
        using var response = await SendAsDeviceAsync(HttpMethod.Post, ChatFilesPath + fileId + "/ticket/", deviceToken, null, ct);
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
        {
            return new FileTicketResult(ChatWriteOutcome.Gone, Error: await RefusalSentenceAsync(response, ct));
        }
        if (response.StatusCode == HttpStatusCode.MethodNotAllowed)
        {
            return new FileTicketResult(ChatWriteOutcome.Off);
        }
        var code = (int)response.StatusCode;
        if (code is >= 400 and < 500 and not 429)
        {
            return new FileTicketResult(ChatWriteOutcome.Refused, Error: await RefusalSentenceAsync(response, ct));
        }
        response.EnsureSuccessStatusCode();
        return ChatFiles.TicketFromJson(await response.Content.ReadAsStringAsync(ct), DateTimeOffset.UtcNow) is { } ticket
            ? new FileTicketResult(ChatWriteOutcome.Done, ticket)
            : throw new HttpRequestException("The backend returned an unexpected ticket.");
    }

    /// <summary>
    /// GET chat/files/shared/: the member's files still shared, newest first (at most 200). Null = a backend without the
    /// list (404/405, another refusal, an unreadable body). Throws on network errors, 429 and 5xx.
    /// </summary>
    public async Task<IReadOnlyList<SharedFile>?> GetSharedFilesAsync(string deviceToken, CancellationToken ct)
    {
        using var response = await SendAsDeviceAsync(HttpMethod.Get, ChatFilesPath + "shared/", deviceToken, null, ct);
        var code = (int)response.StatusCode;
        if (code is >= 400 and < 500 and not 429)
        {
            return null;
        }
        response.EnsureSuccessStatusCode();
        return ChatFiles.SharedFromJson(await response.Content.ReadAsStringAsync(ct));
    }

    /// <summary>The biggest thumbnail kept (the server's are ≤ 40 KB).</summary>
    public const int MaxThumbBytes = 256 * 1024;

    /// <summary>
    /// GET chat/files/&lt;id&gt;/thumb/: the preview picture (a JPEG or WebP). Null when there is none, it isn't this
    /// member's to see, or it is too big. Throws on network errors, 429 and 5xx.
    /// </summary>
    public async Task<byte[]?> GetFileThumbAsync(string deviceToken, string fileId, CancellationToken ct)
    {
        if (!ChatFiles.IsValidId(fileId))
        {
            return null;
        }
        using var response = await SendAsDeviceAsync(HttpMethod.Get, ChatFilesPath + fileId + "/thumb/", deviceToken, null, ct);
        var code = (int)response.StatusCode;
        if (code is >= 400 and < 500 and not 429)
        {
            return null;
        }
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > MaxThumbBytes)
        {
            return null;
        }
        var bytes = await response.Content.ReadAsByteArrayAsync(ct);
        return bytes.Length is > 0 and <= MaxThumbBytes ? bytes : null;
    }

    /// <summary>
    /// POST chat/&lt;id&gt;/messages/ {body: caption, file_id}: the line that carries a registered file in a group or direct
    /// chat. <c>Gone</c> = that conversation was deleted (404).
    /// </summary>
    public async Task<FilePostResult> PostConversationFileAsync(string deviceToken, long conversationId, string caption, string fileId,
        CancellationToken ct)
    {
        using var response = await SendAsDeviceAsync(HttpMethod.Post, $"{ChatRoot}{Id(conversationId)}/messages/", deviceToken,
            JsonBody(new ChatMessageRequest(caption.Trim(), null, fileId), BackendJsonContext.Default.ChatMessageRequest), ct);
        return await FilePostAsync(response, ConversationOutcome(response), ct);
    }

    /// <summary>POST messages/ {body: caption, file_id}: the line that carries a registered file in the admin chat.</summary>
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

    /// <summary>The 4xx sentence of a refusal, or null when it sent none we can show.</summary>
    internal static async Task<string?> RefusalSentenceAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var (_, message) = await ReadFirstErrorAsync(response, ct);
        return message.StartsWith("The backend refused", StringComparison.Ordinal) ? null : message;
    }

    /// <summary>True for 404/405: this backend has no such route (403 is a refusal with a sentence here).</summary>
    internal static bool IsMissingRoute(HttpResponseMessage r) =>
        r.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed;
}

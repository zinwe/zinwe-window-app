using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Zinwe.Agent.Core.Events;
using Zinwe.Agent.Core.Policy;

namespace Zinwe.Agent.Core.Backend;

/// <summary>The member credential was refused (unknown email/id, or the account is disabled).</summary>
/// <param name="field">The response field that carried the refusal ("member_id", "email", "non_field_errors", "detail").</param>
public sealed class EnrollmentRejectedException(string message, string field = "") : Exception(message)
{
    public string Field { get; } = field;
}

/// <summary>The backend no longer recognises this device's token (removed by the owner, or reinstalled elsewhere).</summary>
public sealed class DeviceRejectedException(string message) : Exception(message);

/// <summary>The backend refused an event batch's content (400). Re-sending the same batch will not help.</summary>
public sealed class EventsRejectedException(string message) : Exception(message);

/// <summary>
/// Typed HTTP client for the Django device API. Transport failures surface as
/// <see cref="HttpRequestException"/> / <see cref="TaskCanceledException"/> so callers can retry.
/// </summary>
public sealed class BackendClient
{
    private readonly HttpClient _http;

    public BackendClient(HttpClient http)
    {
        _http = http;
        EnsureSafeBaseAddress(http.BaseAddress);
        if (_http.DefaultRequestHeaders.UserAgent.Count == 0)
        {
            _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("ZinweAgent", AgentInfo.Version));
        }
    }

    /// <summary>Device tokens must never cross the network in clear text; plain HTTP is allowed only for loopback (local dev).</summary>
    internal static void EnsureSafeBaseAddress(Uri? baseAddress)
    {
        if (baseAddress is null || !baseAddress.IsAbsoluteUri)
        {
            throw new InvalidOperationException("Agent:BackendBaseUrl must be an absolute URL.");
        }
        if (baseAddress.Scheme != Uri.UriSchemeHttps && !baseAddress.IsLoopback)
        {
            throw new InvalidOperationException(
                $"Agent:BackendBaseUrl must use https (got {baseAddress}). Plain http is only allowed for localhost.");
        }
    }

    public async Task<EnrollResponse> EnrollAsync(EnrollRequest request, CancellationToken ct)
    {
        using var response = await _http.PostAsync(
            "api/devices/enroll/", JsonBody(request, BackendJsonContext.Default.EnrollRequest), ct);

        if (response.StatusCode == HttpStatusCode.BadRequest)
        {
            var (field, message) = await ReadFirstErrorAsync(response, ct);
            // A bad member credential (or disabled account) is the admin's problem -> Rejected, wait for a fix.
            // Any other 400 is an agent/backend contract bug -> retryable, logged.
            throw field is "email" or "member_id" or "non_field_errors" or "detail"
                ? new EnrollmentRejectedException(message, field)
                : new HttpRequestException($"Enrollment request rejected ({field}: {message}).");
        }
        response.EnsureSuccessStatusCode();
        return await ReadAsync(response, BackendJsonContext.Default.EnrollResponse, ct);
    }

    /// <summary>The member's conversation for this device (both directions, newest first). Empty when none.</summary>
    public async Task<AgentMessage[]> GetMessagesAsync(string deviceToken, CancellationToken ct)
    {
        using var response = await SendAsDeviceAsync(HttpMethod.Get, "api/devices/messages/", deviceToken, null, ct);
        response.EnsureSuccessStatusCode();
        return await ReadAsync(response, BackendJsonContext.Default.AgentMessageArray, ct) ?? [];
    }

    /// <summary>POST a member reply into the thread.</summary>
    public async Task ReplyAsync(string deviceToken, string body, CancellationToken ct)
    {
        using var response = await SendAsDeviceAsync(
            HttpMethod.Post, "api/devices/messages/", deviceToken,
            JsonBody(new MemberReplyRequest(body), BackendJsonContext.Default.MemberReplyRequest), ct);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Post the member's feedback (FR-14). True when it's stored, or when the backend refused it for good (a 4xx
    /// other than 429: an older backend without the endpoint, a bad rating): either way, don't send it again.
    /// Throws on network errors, 429 and 5xx so it's retried.
    /// </summary>
    public async Task<bool> SendFeedbackAsync(string deviceToken, int rating, string message, CancellationToken ct)
    {
        using var response = await SendAsDeviceAsync(
            HttpMethod.Post, "api/devices/feedback/", deviceToken,
            JsonBody(new MemberFeedbackRequest(rating, message), BackendJsonContext.Default.MemberFeedbackRequest), ct);
        var code = (int)response.StatusCode;
        if (code is >= 400 and < 500 and not 429)
        {
            return false;
        }
        response.EnsureSuccessStatusCode();
        return true;
    }

    /// <summary>
    /// GET /api/devices/tasks/ (FR-20 v2): the member's open tasks + those approved in the last 7 days. Null when this
    /// backend has no tasks for the PC: an older backend without the endpoint (404), or a plan without it (403) - the
    /// widget then keeps only the member's own to-dos. Throws on network errors, 429 and 5xx so it's retried.
    /// </summary>
    public async Task<DeviceTask[]?> GetTasksAsync(string deviceToken, CancellationToken ct)
    {
        using var response = await SendAsDeviceAsync(HttpMethod.Get, "api/devices/tasks/", deviceToken, null, ct);
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden or HttpStatusCode.MethodNotAllowed)
        {
            return null;
        }
        response.EnsureSuccessStatusCode();
        var tasks = await ReadAsync(response, BackendJsonContext.Default.DeviceTaskArray, ct);
        return [.. tasks.Where(t => t is not null && t.Id != 0)];
    }

    /// <summary>
    /// POST /api/devices/tasks/&lt;id&gt;/action/ (FR-20 v2): what the member did on an owner's task. Returns the
    /// outcome and, when stored, the backend's view of the task. A 4xx other than 429 is a refusal for good (an
    /// older backend, a task that was cancelled or deleted, a move the backend doesn't allow): don't send it again.
    /// Throws on network errors, 429 and 5xx so the outbox keeps it and retries.
    /// </summary>
    public async Task<(TaskActionOutcome Outcome, DeviceTask? Task)> PostTaskActionAsync(
        string deviceToken, long taskId, string action, string? note = null, long? stepId = null, bool? stepDone = null,
        CancellationToken ct = default)
    {
        var body = new TaskActionRequest(action, string.IsNullOrWhiteSpace(note) ? null : note.Trim(), stepId, stepId is null ? null : stepDone);
        using var response = await SendAsDeviceAsync(
            HttpMethod.Post, $"api/devices/tasks/{taskId.ToString(System.Globalization.CultureInfo.InvariantCulture)}/action/",
            deviceToken, JsonBody(body, BackendJsonContext.Default.TaskActionRequest), ct);
        var code = (int)response.StatusCode;
        if (code is >= 400 and < 500 and not 429)
        {
            return (TaskActionOutcome.Refused, null);
        }
        response.EnsureSuccessStatusCode();
        try
        {
            return (TaskActionOutcome.Done, await response.Content.ReadFromJsonAsync(BackendJsonContext.Default.DeviceTask, ct));
        }
        catch (JsonException)
        {
            return (TaskActionOutcome.Done, null); // stored; the shape is the next sync's business
        }
    }

    /// <summary>
    /// GET /api/devices/todos/: the member's own to-dos, deleted ones included. Null when this backend has none for the
    /// PC: an older backend (404), or a plan without tasks (403): they then stay on this PC. Throws on network errors,
    /// 429 and 5xx so it's retried.
    /// </summary>
    public async Task<DeviceTodoList?> GetTodosAsync(string deviceToken, CancellationToken ct)
    {
        using var response = await SendAsDeviceAsync(HttpMethod.Get, "api/devices/todos/", deviceToken, null, ct);
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden or HttpStatusCode.MethodNotAllowed)
        {
            return null;
        }
        response.EnsureSuccessStatusCode();
        var list = await ReadAsync(response, BackendJsonContext.Default.DeviceTodoList, ct);
        return list with { Todos = [.. (list.Todos ?? []).Where(t => t is not null && t.ClientId.Length > 0)] };
    }

    /// <summary>
    /// PUT or DELETE /api/devices/todos/&lt;client id&gt;/: one to-do as it stands now. Safe to send twice. See
    /// <see cref="TodoWriteOutcome"/>; throws on network errors, 429 and 5xx so the outbox keeps it and retries.
    /// </summary>
    public async Task<TodoWriteOutcome> SendTodoAsync(string deviceToken, DeviceTodo todo, CancellationToken ct = default)
    {
        var path = $"api/devices/todos/{Uri.EscapeDataString(todo.ClientId)}/";
        using var response = todo.Deleted
            ? await SendAsDeviceAsync(
                HttpMethod.Delete, path, deviceToken,
                JsonBody(new TodoDeleteRequest(todo.UpdatedAt), BackendJsonContext.Default.TodoDeleteRequest), ct)
            : await SendAsDeviceAsync(
                HttpMethod.Put, path, deviceToken,
                JsonBody(new TodoSaveRequest(todo.Title, todo.DueDate, todo.Steps ?? [], todo.Status, todo.UpdatedAt),
                    BackendJsonContext.Default.TodoSaveRequest), ct);
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden or HttpStatusCode.MethodNotAllowed)
        {
            return TodoWriteOutcome.Off;
        }
        var code = (int)response.StatusCode;
        if (code is >= 400 and < 500 and not 429)
        {
            return TodoWriteOutcome.Refused;
        }
        response.EnsureSuccessStatusCode();
        return TodoWriteOutcome.Done;
    }

    /// <summary>
    /// FR-44 · <c>PUT /api/devices/me/picture/</c> with a library avatar (<c>{"kind":"avatar","key":"a07"}</c>).
    /// See <see cref="PictureOutcome"/>; throws on network errors, 429 and 5xx so the outbox keeps it and retries.
    /// </summary>
    public async Task<PictureResult> SetAvatarAsync(string deviceToken, string key, CancellationToken ct)
    {
        using var response = await SendAsDeviceAsync(
            HttpMethod.Put, MyPicturePath, deviceToken,
            JsonBody(new AvatarPictureRequest(MemberPicture.AvatarKind, key), BackendJsonContext.Default.AvatarPictureRequest), ct);
        return await ReadPictureResultAsync(response, ct);
    }

    /// <summary>
    /// FR-44 · <c>PUT /api/devices/me/picture/</c> with a photo: multipart <c>file</c> = the tray's 256 × 256 JPEG
    /// (the server re-encodes it anyway). Same outcomes as <see cref="SetAvatarAsync"/>.
    /// </summary>
    public async Task<PictureResult> SetPhotoAsync(string deviceToken, byte[] jpeg, CancellationToken ct)
    {
        var file = new ByteArrayContent(jpeg);
        file.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        using var form = new MultipartFormDataContent();
        form.Add(file, "file", "picture.jpg");
        // Buffered, so the request has a Content-Length (see JsonBody: WSGI reads a chunked body as empty).
        await form.LoadIntoBufferAsync(ct);
        using var response = await SendAsDeviceAsync(HttpMethod.Put, MyPicturePath, deviceToken, form, ct);
        return await ReadPictureResultAsync(response, ct);
    }

    /// <summary>FR-44 · <c>DELETE /api/devices/me/picture/</c>: back to initials. Same outcomes as <see cref="SetAvatarAsync"/>.</summary>
    public async Task<PictureResult> RemovePictureAsync(string deviceToken, CancellationToken ct)
    {
        using var response = await SendAsDeviceAsync(HttpMethod.Delete, MyPicturePath, deviceToken, null, ct);
        return await ReadPictureResultAsync(response, ct);
    }

    /// <summary>The biggest picture file the agent downloads (the server stores 256 × 256 JPEGs of 12-35 KB).</summary>
    public const int MaxPictureDownloadBytes = 512 * 1024;

    /// <summary>
    /// FR-44 · download a member photo (<see cref="MemberPicture.Url"/>). The URL is signed, so the device token is
    /// never sent with it (also not to our own backend). A path is resolved against the backend; an absolute URL must be
    /// https (or loopback, for dev). Returns null when the picture is gone (404/410), or the file isn't a JPEG / is too
    /// big; throws on network errors and 5xx so it's tried again.
    /// </summary>
    public async Task<byte[]?> DownloadPictureAsync(string url, CancellationToken ct)
    {
        using var message = new HttpRequestMessage(HttpMethod.Get, ToDownloadUri(url));
        using var response = await _http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone or HttpStatusCode.Forbidden)
        {
            return null;
        }
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > MaxPictureDownloadBytes)
        {
            return null;
        }
        await using var body = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await body.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > MaxPictureDownloadBytes)
            {
                return null;
            }
            buffer.Write(chunk, 0, read);
        }
        var bytes = buffer.ToArray();
        return IsJpeg(bytes) ? bytes : null;
    }

    /// <summary>A JPEG starts with the SOI marker FF D8 FF.</summary>
    public static bool IsJpeg(ReadOnlySpan<byte> bytes) => bytes.Length > 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF;

    private const string MyPicturePath = "api/devices/me/picture/";

    private static async Task<PictureResult> ReadPictureResultAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var code = (int)response.StatusCode;
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed)
        {
            return new PictureResult(PictureOutcome.Unsupported);
        }
        if (code is >= 400 and < 500 and not 429)
        {
            var (refusal, detail) = await ReadRefusalAsync(response, ct);
            return new PictureResult(PictureOutcome.Refused, Code: refusal, Detail: detail);
        }
        response.EnsureSuccessStatusCode();
        if (response.StatusCode == HttpStatusCode.NoContent)
        {
            return new PictureResult(PictureOutcome.Saved);
        }
        try
        {
            var text = await response.Content.ReadAsStringAsync(ct);
            if (string.IsNullOrWhiteSpace(text))
            {
                return new PictureResult(PictureOutcome.Saved);
            }
            using var doc = JsonDocument.Parse(text);
            return new PictureResult(PictureOutcome.Saved, MemberPictures.ReadReply(doc.RootElement));
        }
        catch (JsonException)
        {
            return new PictureResult(PictureOutcome.Saved); // stored; the next heartbeat carries the picture
        }
    }

    /// <summary>The plan's 400 shape <c>{"code": "...", "detail": "..."}</c>; DRF's field errors otherwise.</summary>
    private static async Task<(string? Code, string Detail)> ReadRefusalAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var text = await response.Content.ReadAsStringAsync(ct);
        try
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                var code = doc.RootElement.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
                if (doc.RootElement.TryGetProperty("detail", out var d) && FirstMessage(d) is { } detail)
                {
                    return (code, detail);
                }
                foreach (var property in doc.RootElement.EnumerateObject())
                {
                    if (FirstMessage(property.Value) is { } message)
                    {
                        return (code, message);
                    }
                }
                return (code, $"The backend refused the picture ({(int)response.StatusCode}).");
            }
        }
        catch (JsonException)
        {
            // not JSON
        }
        return (null, $"The backend refused the picture ({(int)response.StatusCode}).");
    }

    /// <summary>Mark the admin's messages to this member as seen on the PC. Best-effort (ignored on failure).</summary>
    public async Task MarkMessagesReadAsync(string deviceToken, CancellationToken ct)
    {
        using var response = await SendAsDeviceAsync(HttpMethod.Post, "api/devices/messages/read/", deviceToken, null, ct);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>Upload rolled-up usage metrics (1..MaxMetricsPerUpload). The backend replaces each (bucket,category,key).</summary>
    public async Task<MetricUploadResponse> UploadMetricsAsync(string deviceToken, IReadOnlyList<MetricUpload> metrics, CancellationToken ct)
    {
        if (metrics.Count is < 1 or > MaxMetricsPerUpload)
        {
            throw new ArgumentOutOfRangeException(nameof(metrics), metrics.Count, $"Upload 1..{MaxMetricsPerUpload} metrics at a time.");
        }
        using var response = await SendAsDeviceAsync(
            HttpMethod.Post, "api/devices/metrics/upload/", deviceToken,
            JsonBody(new MetricUploadRequest([.. metrics]), BackendJsonContext.Default.MetricUploadRequest), ct);
        response.EnsureSuccessStatusCode();
        return await ReadAsync(response, BackendJsonContext.Default.MetricUploadResponse, ct);
    }

    public async Task<HeartbeatResponse> HeartbeatAsync(string deviceToken, HeartbeatRequest request, CancellationToken ct)
    {
        using var response = await SendAsDeviceAsync(
            HttpMethod.Post, "api/devices/heartbeat/", deviceToken,
            JsonBody(request, BackendJsonContext.Default.HeartbeatRequest), ct);
        response.EnsureSuccessStatusCode();
        return await ReadAsync(response, BackendJsonContext.Default.HeartbeatResponse, ct);
    }

    /// <summary>GET /api/devices/policy/current/ -- the owner's rules for this PC.</summary>
    public async Task<AgentPolicy> GetPolicyAsync(string deviceToken, CancellationToken ct)
    {
        using var response = await SendAsDeviceAsync(HttpMethod.Get, "api/devices/policy/current/", deviceToken, null, ct);
        response.EnsureSuccessStatusCode();
        var policy = await ReadAsync(response, BackendJsonContext.Default.AgentPolicy, ct);
        return policy.IsWellFormed
            ? policy
            : throw new HttpRequestException("The backend returned a malformed policy.");
    }

    /// <summary>
    /// POST /api/devices/events/upload/ (1..100 events). Idempotent on event id: re-sent events come back as duplicates.
    /// A 400 (the backend refused the batch's content) throws <see cref="EventsRejectedException"/>.
    /// </summary>
    public async Task<EventUploadResponse> UploadEventsAsync(string deviceToken, IReadOnlyList<AgentEvent> events, CancellationToken ct)
    {
        if (events.Count is < 1 or > MaxEventsPerUpload)
        {
            throw new ArgumentOutOfRangeException(nameof(events), events.Count, $"Upload 1..{MaxEventsPerUpload} events at a time.");
        }
        var request = new EventUploadRequest([.. events.Select(ToUpload)]);

        using var response = await SendAsDeviceAsync(
            HttpMethod.Post, "api/devices/events/upload/", deviceToken,
            JsonBody(request, BackendJsonContext.Default.EventUploadRequest), ct);
        if (response.StatusCode == HttpStatusCode.BadRequest)
        {
            var (field, message) = await ReadFirstErrorAsync(response, ct);
            throw new EventsRejectedException(string.IsNullOrEmpty(field) ? message : $"{field}: {message}");
        }
        response.EnsureSuccessStatusCode();
        return await ReadAsync(response, BackendJsonContext.Default.EventUploadResponse, ct);
    }

    public const int MaxEventsPerUpload = 100;
    public const int MaxMetricsPerUpload = 1000;

    private static EventUpload ToUpload(AgentEvent e)
    {
        using var data = JsonDocument.Parse(e.DataJson);
        return new EventUpload(e.Id, e.Type, e.OccurredAt, e.Severity, data.RootElement.Clone());
    }

    /// <summary>Sends with "Authorization: Device &lt;token&gt;". A 401 means the owner removed this PC.</summary>
    /// <summary>
    /// Stream a release installer into <paramref name="destination"/>. A path ("/api/devices/releases/5/download/")
    /// is fetched from our backend with this PC's device token; an absolute URL (a CDN) is fetched WITHOUT it - the
    /// token never goes to another host - and must be https. The caller verifies the SHA-256 before using the file.
    /// </summary>
    public async Task DownloadAsync(string urlOrPath, string deviceToken, Stream destination, CancellationToken ct)
    {
        using var message = new HttpRequestMessage(HttpMethod.Get, ToDownloadUri(urlOrPath));
        if (!message.RequestUri!.IsAbsoluteUri)
        {
            message.Headers.Authorization = new AuthenticationHeaderValue("Device", deviceToken);
        }
        // Headers-only completion: a 100 MB installer must not be buffered, nor hit HttpClient.Timeout mid-stream.
        using var response = await _http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode == HttpStatusCode.Unauthorized && !message.RequestUri.IsAbsoluteUri)
        {
            throw new DeviceRejectedException((await ReadFirstErrorAsync(response, ct)).Message);
        }
        response.EnsureSuccessStatusCode();
        await using var body = await response.Content.ReadAsStreamAsync(ct);
        await body.CopyToAsync(destination, ct);
    }

    internal static Uri ToDownloadUri(string urlOrPath)
    {
        if (urlOrPath.StartsWith('/'))
        {
            return new Uri(urlOrPath.TrimStart('/'), UriKind.Relative);
        }
        var uri = new Uri(urlOrPath, UriKind.Absolute);
        if (uri.Scheme != Uri.UriSchemeHttps && !uri.IsLoopback)
        {
            throw new InvalidOperationException("Release downloads must use https.");
        }
        return uri;
    }

    private async Task<HttpResponseMessage> SendAsDeviceAsync(
        HttpMethod method, string path, string deviceToken, HttpContent? body, CancellationToken ct)
    {
        using var message = new HttpRequestMessage(method, path) { Content = body };
        message.Headers.Authorization = new AuthenticationHeaderValue("Device", deviceToken);

        var response = await _http.SendAsync(message, ct);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            using (response)
            {
                throw new DeviceRejectedException((await ReadFirstErrorAsync(response, ct)).Message);
            }
        }
        return response;
    }

    /// <summary>
    /// Buffered JSON body with a Content-Length. JsonContent streams with chunked transfer encoding,
    /// which WSGI servers (Django runserver, many gunicorn setups) silently read as an empty body.
    /// </summary>
    private static ByteArrayContent JsonBody<T>(T value, JsonTypeInfo<T> type)
    {
        var content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(value, type));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        return content;
    }

    /// <summary>An unparseable 2xx (proxy page, contract drift) is a retryable transport problem, not a crash.</summary>
    private static async Task<T> ReadAsync<T>(
        HttpResponseMessage response, JsonTypeInfo<T> type, CancellationToken ct)
    {
        try
        {
            return await response.Content.ReadFromJsonAsync(type, ct)
                   ?? throw new HttpRequestException("The backend returned an empty response.");
        }
        catch (JsonException e)
        {
            throw new HttpRequestException($"The backend returned an unexpected response: {e.Message}", e);
        }
    }

    /// <summary>
    /// DRF errors are {"field": ["msg"]}, {"detail": "msg"}, or nested for list fields
    /// (DRF 3.18: {"events": {"1": {"severity": ["msg"]}}}). Returns the top-level field and the first message
    /// found anywhere under it. Never throws on an unexpected shape.
    /// </summary>
    private static async Task<(string Field, string Message)> ReadFirstErrorAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var fallback = $"The backend refused the request ({(int)response.StatusCode}).";
        try
        {
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return ("", FirstMessage(doc.RootElement) ?? fallback);
            }
            foreach (var property in doc.RootElement.EnumerateObject())
            {
                if (FirstMessage(property.Value) is { } message)
                {
                    return (property.Name, message);
                }
            }
        }
        catch (JsonException)
        {
            // Not JSON (e.g. a proxy error page) -- fall through.
        }
        return ("", fallback);
    }

    private static string? FirstMessage(JsonElement element, int depth = 0)
    {
        if (depth > 8)
        {
            return null;
        }
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                return element.GetString();
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    if (FirstMessage(item, depth + 1) is { } fromItem)
                    {
                        return fromItem;
                    }
                }
                return null;
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (FirstMessage(property.Value, depth + 1) is { } fromProperty)
                    {
                        return fromProperty;
                    }
                }
                return null;
            default:
                return null;
        }
    }
}

using System.Net;
using System.Net.Http.Headers;
using Zinwe.Agent.Core.Backend;

namespace Zinwe.Agent.Core.Chat;

/// <summary>
/// v1.5 · the big transfers of file sharing: the upload (multipart <c>file</c>, streamed from the member's copy with a
/// Content-Length, never held in memory) and the downloads (the file into Downloads, an image into the preview cache). It
/// has an HttpClient of its own: the backend client's 30 s timeout would cut a 25 MB upload on a slow line.
/// The device token only ever goes to the backend's own device route (<c>api/devices/chat/files/&lt;uuid&gt;/</c>, relative to
/// the base address), whatever host a line's <c>url</c> names.
/// </summary>
public sealed class ChatFileTransfer
{
    private readonly HttpClient _http;

    public ChatFileTransfer(HttpClient http)
    {
        _http = http;
        BackendClient.EnsureSafeBaseAddress(http.BaseAddress);
        if (_http.DefaultRequestHeaders.UserAgent.Count == 0)
        {
            _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("ZinweAgent", AgentInfo.Version));
        }
    }

    /// <summary>
    /// POST chat/files/ with <paramref name="content"/> as <c>file</c> (named <paramref name="name"/>). 201 = the stored
    /// file (not attached yet). <c>Off</c> = 404/405 (a backend without files); <c>Refused</c> = another 4xx with the
    /// backend's sentence (too big, empty, sharing off, too many waiting). Throws on network errors, 429 and 5xx (retried).
    /// </summary>
    public async Task<FileUploadResult> UploadAsync(string deviceToken, Stream content, string name, CancellationToken ct)
    {
        using var form = new MultipartFormDataContent();
        var part = new StreamContent(content, 81_920);
        part.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(part, "file", ChatFiles.SafeName(name));
        using var message = new HttpRequestMessage(HttpMethod.Post, BackendClient.ChatFilesPath) { Content = form };
        message.Headers.Authorization = new AuthenticationHeaderValue("Device", deviceToken);
        using var response = await _http.SendAsync(message, ct);
        ThrowIfRejected(response);
        var code = (int)response.StatusCode;
        if (BackendClient.IsMissingRoute(response))
        {
            return new FileUploadResult(ChatWriteOutcome.Off, Status: code);
        }
        if (code is >= 400 and < 500 and not 429)
        {
            return new FileUploadResult(ChatWriteOutcome.Refused, Error: await BackendClient.RefusalSentenceAsync(response, ct), Status: code);
        }
        response.EnsureSuccessStatusCode();
        var file = ChatFiles.FromJson(await response.Content.ReadAsStringAsync(ct));
        return file is not null && ChatFiles.IsValidId(file.Id)
            ? new FileUploadResult(ChatWriteOutcome.Done, file, Status: code)
            : throw new HttpRequestException("The backend returned an unexpected upload reply.");
    }

    /// <summary>
    /// GET chat/files/&lt;id&gt;/ (<paramref name="inline"/>: <c>?inline=1</c>, an image's real type) into
    /// <paramref name="destination"/>, at most <paramref name="maxBytes"/>. <c>Gone</c> = 404/410 (expired, deleted or not
    /// this member's to see). Throws on network errors, 429 and 5xx.
    /// </summary>
    public async Task<FileFetchResult> DownloadAsync(string deviceToken, string fileId, bool inline, long maxBytes, Stream destination,
        CancellationToken ct)
    {
        if (!ChatFiles.IsValidId(fileId))
        {
            return new FileFetchResult(FileFetchOutcome.Gone);
        }
        var path = BackendClient.ChatFilesPath + Uri.EscapeDataString(fileId) + "/" + (inline ? "?inline=1" : "");
        using var message = new HttpRequestMessage(HttpMethod.Get, path);
        message.Headers.Authorization = new AuthenticationHeaderValue("Device", deviceToken);
        // Headers only: the body is streamed (not buffered), and HttpClient.Timeout doesn't cut it mid-way.
        using var response = await _http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct);
        ThrowIfRejected(response);
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
        {
            return new FileFetchResult(FileFetchOutcome.Gone);
        }
        var code = (int)response.StatusCode;
        if (code is >= 400 and < 500 and not 429)
        {
            return new FileFetchResult(FileFetchOutcome.Refused);
        }
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > maxBytes)
        {
            return new FileFetchResult(FileFetchOutcome.TooBig);
        }
        var disposition = response.Content.Headers.ContentDisposition;
        var name = (disposition?.FileNameStar ?? disposition?.FileName)?.Trim('"', ' ');
        await using var body = await response.Content.ReadAsStreamAsync(ct);
        var buffer = new byte[81_920];
        long total = 0;
        int read;
        while ((read = await body.ReadAsync(buffer, ct)) > 0)
        {
            total += read;
            if (total > maxBytes)
            {
                return new FileFetchResult(FileFetchOutcome.TooBig);
            }
            await destination.WriteAsync(buffer.AsMemory(0, read), ct);
        }
        return new FileFetchResult(FileFetchOutcome.Done, string.IsNullOrWhiteSpace(name) ? null : name,
            response.Content.Headers.ContentType?.MediaType);
    }

    private static void ThrowIfRejected(HttpResponseMessage response)
    {
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            throw new DeviceRejectedException("The backend no longer knows this device.");
        }
    }
}

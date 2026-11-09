using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Zinwe.Agent.Core.Backend;

namespace Zinwe.Agent.Core.Chat;

/// <summary>What the relay did with a push.</summary>
public enum PushOutcome
{
    /// <summary>Every byte went through.</summary>
    Done,
    /// <summary>The receiver went away (499/410, or the relay closed the stream): stop.</summary>
    ReceiverGone,
    /// <summary>The relay refused it (another 4xx: a stale ticket, a job that isn't ours).</summary>
    Refused,
}

/// <summary>What became of one relay GET.</summary>
public enum RelayFetchOutcome
{
    /// <summary>The body ended: the destination holds the file (check its size).</summary>
    Done,
    /// <summary>503: the sender's PC didn't start within 20 s. Wait and try again.</summary>
    Offline,
    /// <summary>404/410: not shared any more.</summary>
    Gone,
    /// <summary>401/403: the ticket is stale. Ask for a new one.</summary>
    TicketStale,
    /// <summary>Another refusal.</summary>
    Refused,
}

/// <param name="Length">The destination's length after it (resumed or restarted).</param>
/// <param name="Detail">The relay's own sentence (503: "Priya's PC is offline. The download starts when it's back.").</param>
public sealed record RelayFetchResult(RelayFetchOutcome Outcome, long Length = 0, string? Detail = null);

/// <summary>
/// v1.5 round 2 · the big transfers, through the relay (BRIEF2 §1): the sender's long-poll (<c>GET /relay/wait</c>), its
/// push (<c>PUT /relay/push/&lt;job&gt;</c>, streamed from an offset, never held in memory), the receiver's resumable GET
/// (<c>Range: bytes=N-</c>) and the "is the sender online" check. Relay urls carry their ticket, so the device token is never
/// sent with them; they must be https (or loopback for a dev server). Round 1's downloads (a server copy on the device route,
/// with the device token, relative to the backend) still work. Its own HttpClient (no overall timeout: a 5 GB push takes
/// long); each call has its own deadline, and a stream that stalls for <see cref="StallAfter"/> is cut.
/// </summary>
public sealed class ChatFileTransfer
{
    /// <summary>The relay holds a wait ≤ 25 s: give up on one after this.</summary>
    public static readonly TimeSpan WaitDeadline = TimeSpan.FromSeconds(45);
    public static readonly TimeSpan StallAfter = TimeSpan.FromSeconds(90);
    private const int Chunk = 81_920;

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

    // ---- Sender ------------------------------------------------------------------------------------------------------

    /// <summary>
    /// The long-poll: a job (a receiver is waiting), or null after the relay's 25 s with none (204). Throws
    /// <see cref="TicketRejectedException"/> on 401/403 (ask for a new serve ticket), and on network errors and 5xx.
    /// </summary>
    public async Task<RelayJob?> WaitAsync(Uri waitUrl, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(WaitDeadline);
        using var message = new HttpRequestMessage(HttpMethod.Get, Checked(waitUrl));
        using var response = await _http.SendAsync(message, deadline.Token);
        if (response.StatusCode == HttpStatusCode.NoContent)
        {
            return null;
        }
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw new TicketRejectedException();
        }
        response.EnsureSuccessStatusCode();
        return ChatFiles.JobFromJson(await response.Content.ReadAsStringAsync(deadline.Token));
    }

    /// <summary>
    /// PUT the file to the waiting receiver: <paramref name="length"/> bytes of <paramref name="source"/> from where it stands
    /// (the caller seeks to the job's offset), with a Content-Length. <see cref="PushOutcome.ReceiverGone"/> when the relay
    /// answers 499/410 or closes the stream mid-way (the receiver went away). Throws on network errors and 5xx.
    /// </summary>
    public async Task<PushOutcome> PushAsync(Uri pushUrl, Stream source, long length, CancellationToken ct)
    {
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var message = new HttpRequestMessage(HttpMethod.Put, Checked(pushUrl))
        {
            Content = new StallingStreamContent(source, length, stall, StallAfter),
        };
        message.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        message.Content.Headers.ContentLength = length;
        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, stall.Token);
        }
        catch (Exception e) when (e is HttpRequestException or IOException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            // The relay closed the connection while we were still sending, or nothing moved for the stall time: the
            // receiver is gone.
            return PushOutcome.ReceiverGone;
        }
        using (response)
        {
            var code = (int)response.StatusCode;
            if (code is 499 or 410)
            {
                return PushOutcome.ReceiverGone;
            }
            if (code is >= 400 and < 500 and not 429)
            {
                return PushOutcome.Refused;
            }
            response.EnsureSuccessStatusCode();
            return PushOutcome.Done;
        }
    }

    // ---- Receiver ----------------------------------------------------------------------------------------------------

    /// <summary>
    /// The relay's "is the sender online" (<c>{"online": true|false}</c>). Null when it can't tell (no url, an error):
    /// then the GET itself says (503 = offline).
    /// </summary>
    public async Task<bool?> OnlineAsync(Uri? statusUrl, CancellationToken ct)
    {
        if (statusUrl is null)
        {
            return null;
        }
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(15));
            using var response = await _http.GetAsync(Checked(statusUrl), deadline.Token);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(deadline.Token));
            return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("online", out var online)
                ? online.ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => null }
                : null;
        }
        catch (Exception e) when (e is HttpRequestException or JsonException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>
    /// GET the file from the relay into <paramref name="destination"/> (seekable; it may already hold the first bytes from an
    /// earlier try): asks <c>Range: bytes=N-</c> from its length, appends on 206, starts over on a 200, and reports the length
    /// as it grows. At most <paramref name="maxBytes"/>. Throws on network errors and 5xx other than 503 (the caller tries
    /// again from where it got to).
    /// </summary>
    public async Task<RelayFetchResult> FetchAsync(Uri url, Stream destination, long maxBytes, Action<long>? progress, CancellationToken ct)
    {
        var offset = destination.Length;
        using var message = new HttpRequestMessage(HttpMethod.Get, Checked(url));
        if (offset > 0)
        {
            message.Headers.Range = new RangeHeaderValue(offset, null);
        }
        using var response = await _http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct);
        var code = (int)response.StatusCode;
        switch (response.StatusCode)
        {
            case HttpStatusCode.ServiceUnavailable:
                return new RelayFetchResult(RelayFetchOutcome.Offline, offset, await DetailAsync(response, ct));
            case HttpStatusCode.NotFound or HttpStatusCode.Gone:
                return new RelayFetchResult(RelayFetchOutcome.Gone, offset, await DetailAsync(response, ct));
            case HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden:
                return new RelayFetchResult(RelayFetchOutcome.TicketStale, offset);
            case HttpStatusCode.RequestedRangeNotSatisfiable:
                // We already have every byte (or more): the caller checks the size.
                return new RelayFetchResult(RelayFetchOutcome.Done, offset);
        }
        if (code is >= 400 and < 500 and not 429)
        {
            return new RelayFetchResult(RelayFetchOutcome.Refused, offset, await DetailAsync(response, ct));
        }
        response.EnsureSuccessStatusCode();
        var start = 0L;
        if (response.StatusCode == HttpStatusCode.PartialContent && response.Content.Headers.ContentRange is { From: { } from })
        {
            start = from;
        }
        if (start != offset)
        {
            // A 200 (the Range was ignored), or a part that doesn't start where we are: begin again from its start.
            if (start > offset)
            {
                return new RelayFetchResult(RelayFetchOutcome.Refused, offset);
            }
            destination.SetLength(start);
        }
        destination.Seek(start, SeekOrigin.Begin);
        var length = start;
        await using var body = await response.Content.ReadAsStreamAsync(ct);
        var buffer = new byte[Chunk];
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
        while (true)
        {
            stall.CancelAfter(StallAfter);
            int read;
            try
            {
                read = await body.ReadAsync(buffer, stall.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new HttpRequestException("The download stalled.");
            }
            if (read == 0)
            {
                break;
            }
            length += read;
            if (length > maxBytes)
            {
                return new RelayFetchResult(RelayFetchOutcome.Refused, length);
            }
            await destination.WriteAsync(buffer.AsMemory(0, read), ct);
            progress?.Invoke(length);
        }
        await destination.FlushAsync(ct);
        return new RelayFetchResult(RelayFetchOutcome.Done, length);
    }

    /// <summary>
    /// Round 1 · GET chat/files/&lt;id&gt;/ (<paramref name="inline"/>: <c>?inline=1</c>) from the server's copy into
    /// <paramref name="destination"/>, at most <paramref name="maxBytes"/>. The device token only ever goes to this route,
    /// relative to the backend, whatever host a line's url names. <c>Gone</c> = 404/410. Throws on network errors, 429 and 5xx.
    /// </summary>
    public async Task<FileFetchResult> DownloadLegacyAsync(string deviceToken, string fileId, bool inline, long maxBytes, Stream destination,
        Action<long>? progress, CancellationToken ct)
    {
        if (!ChatFiles.IsValidId(fileId))
        {
            return new FileFetchResult(FileFetchOutcome.Gone);
        }
        var path = BackendClient.ChatFilesPath + Uri.EscapeDataString(fileId) + "/" + (inline ? "?inline=1" : "");
        using var message = new HttpRequestMessage(HttpMethod.Get, path);
        message.Headers.Authorization = new AuthenticationHeaderValue("Device", deviceToken);
        using var response = await _http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            throw new DeviceRejectedException("The backend no longer knows this device.");
        }
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
        var buffer = new byte[Chunk];
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
            progress?.Invoke(total);
        }
        return new FileFetchResult(FileFetchOutcome.Done, string.IsNullOrWhiteSpace(name) ? null : name,
            response.Content.Headers.ContentType?.MediaType);
    }

    private static Uri Checked(Uri url) =>
        ChatFiles.RelayUri(url.OriginalString) ?? throw new InvalidOperationException("Relay calls must use https.");

    private static async Task<string?> DetailAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var text = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(text);
            return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("detail", out var d)
                && d.ValueKind == JsonValueKind.String ? d.GetString() : null;
        }
        catch (Exception e) when (e is JsonException or HttpRequestException or IOException)
        {
            return null;
        }
    }

    /// <summary>
    /// A request body read from a stream, <c>length</c> bytes at most, that cancels its request when no chunk could be sent
    /// for the stall time (the relay holds at most 64 MB: a stuck receiver must not hold a push forever).
    /// </summary>
    private sealed class StallingStreamContent(Stream source, long length, CancellationTokenSource stall, TimeSpan after) : HttpContent
    {
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            await SerializeToStreamAsync(stream, context, CancellationToken.None);

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
        {
            var buffer = new byte[Chunk];
            var left = length;
            while (left > 0)
            {
                stall.CancelAfter(after);
                var read = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, left)), cancellationToken);
                if (read == 0)
                {
                    throw new IOException("The file got shorter while it was being sent.");
                }
                await stream.WriteAsync(buffer.AsMemory(0, read), stall.Token);
                left -= read;
            }
        }

        protected override bool TryComputeLength(out long size)
        {
            size = length;
            return true;
        }
    }
}

/// <summary>The relay refused a serve ticket (401/403): ask the backend for a new one.</summary>
public sealed class TicketRejectedException() : Exception("The relay refused the serve ticket.");

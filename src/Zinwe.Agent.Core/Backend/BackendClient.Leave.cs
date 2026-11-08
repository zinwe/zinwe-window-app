using Zinwe.Agent.Core.Work;

namespace Zinwe.Agent.Core.Backend;

// v1.4.1 · the member's asks for a day off (api-v14 §3 /api/devices/leave/). Same rules as the chat calls: 403/404/405 =
// this backend has no leave (null / Off), another 4xx (not 429) is a refusal for good, network errors, 429 and 5xx throw.
public sealed partial class BackendClient
{
    private const string LeavePath = "api/devices/leave/";

    /// <summary>GET leave/: the member's own asks (last 90 days and the coming ones). Null = a backend without leave.</summary>
    public async Task<IReadOnlyList<LeaveAsk>?> GetLeaveAsync(string deviceToken, CancellationToken ct)
    {
        using var response = await SendAsDeviceAsync(HttpMethod.Get, LeavePath, deviceToken, null, ct);
        if (IsOff(response))
        {
            return null;
        }
        response.EnsureSuccessStatusCode();
        return LeaveJson.Asks(await response.Content.ReadAsStringAsync(ct));
    }

    /// <summary>POST leave/ {date, half, note}: ask for that day (a second ask for the same day replaces a waiting one).</summary>
    public async Task<ChatWriteResult> AskForLeaveAsync(string deviceToken, LeaveRequest request, CancellationToken ct)
    {
        using var response = await SendAsDeviceAsync(HttpMethod.Post, LeavePath, deviceToken,
            JsonBody(request, BackendJsonContext.Default.LeaveRequest), ct);
        var outcome = Outcome(response);
        return new ChatWriteResult(outcome, outcome == ChatWriteOutcome.Refused ? await RefusalTextAsync(response, ct) : null);
    }
}

using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;
using Microsoft.Diagnostics.Tracing.Session;
using Microsoft.Extensions.Logging;
using Zinwe.Agent.Core.Features.Network;

namespace Zinwe.Agent.Service.Features.Network;

/// <summary>
/// User-mode ETW trace source (no kernel driver, no TLS inspection): the kernel Network provider gives per-connection
/// SEND byte counts with the owning process and destination IP; the DNS-Client provider gives name→IP answers so the
/// collector can attribute upload bytes to a DOMAIN. Needs a LocalSystem (service) context to open the kernel session;
/// everything is best-effort and swallowed, so a failure just means no uploads are reported.
///
/// NOTE: ETW capture can only be validated on a real elevated/SYSTEM run with live traffic — it is off by default.
/// </summary>
internal sealed partial class EtwNetworkTraceSource(ILogger<EtwNetworkTraceSource> logger) : INetworkTraceSource
{
    private const string SessionName = "ZinweNetworkTrace";
    private const string DnsProvider = "Microsoft-Windows-DNS-Client";
    private const int DnsQueryCompletedId = 3008;

    private TraceEventSession? _session;
    private Thread? _thread;

    public event EventHandler<DnsAnswer>? DnsAnswered;
    public event EventHandler<UploadSample>? BytesSent;
    public event EventHandler<TrafficSample>? Traffic;

    public void Start()
    {
        try
        {
            _session = new TraceEventSession(SessionName) { StopOnDispose = true };
            // Process: names for the network events (without it ProcessName can come back empty and every
            // sample was dropped as "not a tracked app").
            _session.EnableKernelProvider(KernelTraceEventParser.Keywords.NetworkTCPIP | KernelTraceEventParser.Keywords.Process);
            _session.EnableProvider(DnsProvider);

            _session.Source.Kernel.TcpIpSend += d => RaiseUpload(NameOf(d.ProcessName, d.ProcessID), d.daddr, d.size);
            _session.Source.Kernel.TcpIpSendIPV6 += d => RaiseUpload(NameOf(d.ProcessName, d.ProcessID), d.daddr, d.size);
            _session.Source.Kernel.UdpIpSend += d => RaiseUpload(NameOf(d.ProcessName, d.ProcessID), d.daddr, d.size);
            // Received data too, for data-used totals (FR-04).
            _session.Source.Kernel.TcpIpRecv += d => RaiseTraffic(NameOf(d.ProcessName, d.ProcessID), d.saddr, d.size, false);
            _session.Source.Kernel.TcpIpRecvIPV6 += d => RaiseTraffic(NameOf(d.ProcessName, d.ProcessID), d.saddr, d.size, false);
            _session.Source.Kernel.UdpIpRecv += d => RaiseTraffic(NameOf(d.ProcessName, d.ProcessID), d.saddr, d.size, false);
            _session.Source.Dynamic.All += OnDynamic;

            _thread = new Thread(ProcessLoop) { IsBackground = true, Name = "zinwe-etw-net" };
            _thread.Start();
            logger.LogInformation("ETW network trace session started");
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Could not start the ETW network session; upload monitoring is off");
            Dispose();
        }
    }

    private void ProcessLoop()
    {
        try
        {
            _session?.Source.Process(); // blocks until the session is disposed
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "ETW network processing stopped");
        }
    }

    private void RaiseUpload(string? processName, IPAddress? dest, int size)
    {
        if (dest is null || size <= 0)
        {
            return;
        }
        var proc = StripExe(processName);
        BytesSent?.Invoke(this, new UploadSample(proc, dest.ToString(), size));
        Traffic?.Invoke(this, new TrafficSample(proc, dest.ToString(), size, true));
    }

    private void RaiseTraffic(string? processName, IPAddress? remote, int size, bool outbound)
    {
        if (size <= 0)
        {
            return;
        }
        Traffic?.Invoke(this, new TrafficSample(StripExe(processName), remote?.ToString(), size, outbound));
    }

    // ETW names a process only once it has seen it start; for one already running, look it up once by id.
    private readonly Dictionary<int, string?> _pidNames = [];

    private string? NameOf(string? etwName, int pid)
    {
        if (!string.IsNullOrWhiteSpace(etwName))
        {
            return etwName;
        }
        if (pid <= 4)
        {
            return pid <= 0 ? null : "system";
        }
        if (_pidNames.TryGetValue(pid, out var cached))
        {
            return cached;
        }
        string? name;
        try
        {
            using var p = System.Diagnostics.Process.GetProcessById(pid);
            name = p.ProcessName;
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException)
        {
            name = null; // already gone
        }
        if (_pidNames.Count > 4096)
        {
            _pidNames.Clear(); // pids are reused; a small cache is plenty
        }
        _pidNames[pid] = name;
        return name;
    }

    private void OnDynamic(TraceEvent data)
    {
        if (!string.Equals(data.ProviderName, DnsProvider, StringComparison.Ordinal) || (int)data.ID != DnsQueryCompletedId)
        {
            return;
        }
        try
        {
            var name = data.PayloadByName("QueryName") as string;
            var results = data.PayloadByName("QueryResults") as string;
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(results))
            {
                return;
            }
            // QueryResults mixes CNAMEs and IPs in a ';'-delimited string; pull out the IP tokens defensively.
            foreach (Match m in IpToken().Matches(results))
            {
                DnsAnswered?.Invoke(this, new DnsAnswer(name, m.Value));
            }
        }
        catch (Exception e)
        {
            logger.LogDebug(e, "Could not parse a DNS answer");
        }
    }

    private static string? StripExe(string? name) =>
        string.IsNullOrWhiteSpace(name) ? null
        : name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;

    // IPv4 or (compressed) IPv6 tokens inside the DNS QueryResults string.
    [GeneratedRegex(@"\b(?:\d{1,3}(?:\.\d{1,3}){3}|[0-9A-Fa-f:]{3,}:[0-9A-Fa-f:]*)\b")]
    private static partial Regex IpToken();

    public void Dispose()
    {
        try
        {
            _session?.Dispose(); // ends Source.Process() on the worker thread
            _thread?.Join(TimeSpan.FromSeconds(3));
        }
        catch (Exception e)
        {
            logger.LogDebug(e, "Error disposing the ETW network session");
        }
        finally
        {
            _session = null;
        }
    }
}

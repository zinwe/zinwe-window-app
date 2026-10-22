using System.Net;
using System.Text;
using Microsoft.Extensions.Logging;
using Zinwe.Agent.Core.Backend;
using Zinwe.Agent.Core.Policy;
using Zinwe.Agent.Core.Usb;

namespace Zinwe.Agent.Core.Tests;

/// <summary>A request as the fake backend saw it (captured before the client disposes the message).</summary>
internal sealed record SeenRequest(HttpMethod Method, string Path, string Body, long? ContentLength, string? AuthScheme, string? AuthParameter);

/// <summary>Programmable backend: <c>Respond</c> decides each response; every request is recorded.</summary>
internal sealed class FakeBackend : HttpMessageHandler
{
    public List<SeenRequest> Requests { get; } = [];

    public Func<SeenRequest, HttpResponseMessage> Respond { get; set; } =
        _ => throw new InvalidOperationException("The backend should not have been called.");

    public BackendClient Client() => new(new HttpClient(this) { BaseAddress = new Uri("http://127.0.0.1:8000/") });

    public static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var seen = new SeenRequest(
            request.Method,
            request.RequestUri!.AbsolutePath,
            request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct),
            request.Content?.Headers.ContentLength,
            request.Headers.Authorization?.Scheme,
            request.Headers.Authorization?.Parameter);
        Requests.Add(seen);
        return Respond(seen);
    }
}

/// <summary>Records registry edits instead of making them. Tests never touch the real HKLM keys.</summary>
internal sealed class FakeRegistryWriter : IRegistryWriter
{
    /// <summary>"&lt;short key&gt;/&lt;value&gt;" → DWORD, e.g. "53f5630d/Deny_Write" → 1.</summary>
    public Dictionary<string, int> Values { get; } = [];
    public List<string> Operations { get; } = [];

    private static readonly HashSet<string> KnownKeys =
        [.. RemovableStoragePolicy.ClassKeys, RemovableStoragePolicy.WriteProtectKeyPath];

    private static string Id(string keyPath, string valueName)
    {
        Assert.Contains(keyPath, KnownKeys); // never anything but the USB policy keys
        return $"{RemovableStoragePolicy.ShortName(keyPath)}/{valueName}";
    }

    public int? ReadDword(string keyPath, string valueName) =>
        Values.TryGetValue(Id(keyPath, valueName), out var v) ? v : null;

    public void SetDword(string keyPath, string valueName, int value)
    {
        Values[Id(keyPath, valueName)] = value;
        Operations.Add($"set {Id(keyPath, valueName)}={value}");
    }

    public void DeleteValue(string keyPath, string valueName)
    {
        Values.Remove(Id(keyPath, valueName));
        Operations.Add($"delete {Id(keyPath, valueName)}");
    }
}

internal sealed class FakeRefresher : IUsbStorageRefresher
{
    public int Calls { get; private set; }

    public int RefreshConnectedDrives()
    {
        Calls++;
        return 1;
    }
}

internal sealed class FakeEnforcer(bool dryRun = false) : IUsbEnforcer
{
    public List<UsbMode> Applied { get; } = [];
    public Exception? FailWith { get; set; }
    public bool IsDryRun => dryRun;

    public void Apply(UsbMode mode)
    {
        if (FailWith is not null)
        {
            throw FailWith;
        }
        Applied.Add(mode);
    }
}

/// <summary>Collects log lines so tests can assert a warning was written.</summary>
internal sealed class ListLoggerFactory : ILoggerFactory
{
    public List<(LogLevel Level, string Message)> Entries { get; } = [];

    public ILogger CreateLogger(string categoryName) => new ListLogger(Entries);

    public void AddProvider(ILoggerProvider provider)
    {
    }

    public void Dispose()
    {
    }

    private sealed class ListLogger(List<(LogLevel, string)> entries) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (entries)
            {
                entries.Add((logLevel, formatter(state, exception)));
            }
        }
    }
}

/// <summary>A fresh temp directory per test, deleted afterwards.</summary>
internal sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "zinwe-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(Path))
        {
            Directory.Delete(Path, recursive: true);
        }
    }
}

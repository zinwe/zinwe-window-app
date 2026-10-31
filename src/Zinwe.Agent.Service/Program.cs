using Microsoft.Extensions.Options;
using Zinwe.Agent.Core;
using Zinwe.Agent.Core.Backend;
using Zinwe.Agent.Core.Events;
using Zinwe.Agent.Core.Identity;
using Zinwe.Agent.Core.Messages;
using Zinwe.Agent.Core.Metrics;
using Zinwe.Agent.Core.Policy;
using Zinwe.Agent.Core.Work;
using Zinwe.Agent.Core.Usb;
using Zinwe.Agent.Service;
using Zinwe.Agent.Service.Features;
using Zinwe.Agent.Service.Session;
using Zinwe.Agent.Service.Update;
using Zinwe.Agent.Core.Update;
using Zinwe.Agent.Service.Usb;

// Content root = the exe's folder, so appsettings*.json load no matter what the working directory is
// (the SCM starts services in System32).
var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
});

// Config priority (low → high): appsettings*.json, HKLM\SOFTWARE\Zinwe\Agent (MSI), ZINWE_Agent__* env vars.
builder.Configuration.AddInMemoryCollection(MachineSettings.ReadConfigOverrides());
builder.Configuration.AddEnvironmentVariables("ZINWE_");

// No-op when started from a console (`dotnet run`); under the SCM this wires service lifetime + Event Log.
builder.Services.AddWindowsService(o => o.ServiceName = AgentInfo.ServiceName);

builder.Services.AddOptions<AgentOptions>()
    .Bind(builder.Configuration.GetSection(AgentOptions.Section))
    .Validate(o => Uri.TryCreate(o.BackendBaseUrl, UriKind.Absolute, out _), "Agent:BackendBaseUrl must be an absolute URL.")
    .Validate(o => o.HeartbeatSeconds is >= 15 and <= 3600, "Agent:HeartbeatSeconds must be 15..3600.")
    .ValidateOnStart();

builder.Services.AddHttpClient<BackendClient>((sp, http) =>
{
    var options = sp.GetRequiredService<IOptions<AgentOptions>>().Value;
    http.BaseAddress = new Uri(options.BackendBaseUrl.TrimEnd('/') + "/");
    http.Timeout = TimeSpan.FromSeconds(30);
});

static string DataDirectory(IServiceProvider sp) =>
    Environment.ExpandEnvironmentVariables(sp.GetRequiredService<IOptions<AgentOptions>>().Value.DataDirectory);

builder.Services.AddSingleton(sp => new IdentityStore(DataDirectory(sp)));
builder.Services.AddSingleton(sp => new PolicyStore(DataDirectory(sp)));
// The member's consent on the widget: until they agree, events and usage metrics aren't recorded at all. The notice in
// force follows the policy (what is switched on), so turning on something new asks again. PolicySync is resolved
// lazily: it (through UsbProtection → EventQueue) depends on this gate.
builder.Services.AddSingleton(sp =>
{
    PolicySync? sync = null;
    return new ConsentGate(
        new ConsentReader(sp.GetRequiredService<IWidgetFolder>()),
        noticeVersion: () => ConsentNotice.VersionFor(
            MonitoringScope.From((sync ??= sp.GetRequiredService<PolicySync>()).Current)));
});
builder.Services.AddSingleton(sp => new RemovalMarker(DataDirectory(sp)));
builder.Services.AddSingleton(sp => new EventQueue(
    DataDirectory(sp), recordingAllowed: sp.GetRequiredService<ConsentGate>().IsGranted));
builder.Services.AddSingleton(sp => new MetricsStore(
    DataDirectory(sp), recordingAllowed: sp.GetRequiredService<ConsentGate>().IsGranted));
builder.Services.AddSingleton<AgentStateHolder>();
builder.Services.AddSingleton(sp => new MemberCredentialSource(
    sp.GetRequiredService<IOptionsMonitor<AgentOptions>>(),
    sp.GetRequiredService<ILogger<MemberCredentialSource>>(),
    DataDirectory(sp)));

// USB module. SAFETY: real registry enforcement only as LocalSystem with Agent:Enforcement:DryRun=false.
builder.Services.AddSingleton<IUsbEnforcer>(sp => UsbEnforcerSelector.Select(
    UsbEnforcerSelector.IsRunningAsSystem(),
    sp.GetRequiredService<IOptions<AgentOptions>>().Value.Enforcement.DryRun,
    // FR-01: every storage class + USB write-protect, and connected drives restarted when the rule changes.
    () => new RegistryUsbEnforcer(new HklmRegistryWriter(), sp.GetRequiredService<ILogger<RegistryUsbEnforcer>>(),
        new PnpUsbStorageRefresher(sp.GetRequiredService<ILogger<PnpUsbStorageRefresher>>())),
    sp.GetRequiredService<ILoggerFactory>()));
builder.Services.AddSingleton<UsbProtection>();
builder.Services.AddSingleton<IUsbDeviceWatcher, WmiUsbDeviceWatcher>();
builder.Services.AddSingleton<PolicySync>();
// The widget folder (%LOCALAPPDATA%\Zinwe) of whoever is signed in: the dev console agent's own, or - as the
// LocalSystem service - the signed-in user's, with every file operation done AS that user.
builder.Services.AddSingleton<IWidgetFolder, SignedInUserWidgetFolder>();
// Owner ↔ member chat + protection notices for the widget (NOT the agent data dir, so the widget sees them).
builder.Services.AddSingleton(sp => new NotificationWriter(sp.GetRequiredService<IWidgetFolder>()));
// The member's outbound replies (widget writes, agent drains) — shared per-user file.
builder.Services.AddSingleton(sp => new MessageOutbox(sp.GetRequiredService<IWidgetFolder>()));
builder.Services.AddSingleton<MessageSync>();
// FR-20 v2: the owner's tasks - the member's actions (outbox kind task_action) out, GET tasks/ in, served on the pipe.
builder.Services.AddSingleton(sp => new Zinwe.Agent.Core.Tasks.TaskSync(
    sp.GetRequiredService<BackendClient>(), sp.GetRequiredService<MessageOutbox>(),
    sp.GetRequiredService<ILogger<Zinwe.Agent.Core.Tasks.TaskSync>>(), DataDirectory(sp)));
// The member's own to-dos - what the tray queued (outbox kind todo) out, GET todos/ in, served on the pipe.
builder.Services.AddSingleton(sp => new Zinwe.Agent.Core.Tasks.TodoSync(
    sp.GetRequiredService<BackendClient>(), sp.GetRequiredService<MessageOutbox>(),
    sp.GetRequiredService<ILogger<Zinwe.Agent.Core.Tasks.TodoSync>>(), DataDirectory(sp)));
// FR-44: the member's picture - heartbeat in, the tray's choice (pipe "picture" → outbox kind picture) out via
// PUT/DELETE me/picture/, photos cached in the data folder and copied into the member's widget folder for the tray.
builder.Services.AddSingleton(sp => new Zinwe.Agent.Core.Pictures.PictureSync(
    sp.GetRequiredService<BackendClient>(), sp.GetRequiredService<MessageOutbox>(), sp.GetRequiredService<IWidgetFolder>(),
    sp.GetRequiredService<ILogger<Zinwe.Agent.Core.Pictures.PictureSync>>(), DataDirectory(sp)));
// Usage metrics upload + the member's work status (read from the widget's per-user file).
builder.Services.AddSingleton<MetricsUploadPump>();
builder.Services.AddSingleton(sp => new WorkStatusReader(sp.GetRequiredService<IWidgetFolder>()));
// Every log in / break / back / log out from the widget's change log, sent on the heartbeat until confirmed.
builder.Services.AddSingleton(sp => new WorkTransitionLog(sp.GetRequiredService<IWidgetFolder>(), DataDirectory(sp)));
builder.Services.AddSingleton<EventUploadPump>();

// Self-contained feature modules (internet usage, data-transfer, remote-software...) register and run themselves.
builder.AddFeatureModules();

builder.Services.AddHostedService<AgentWorker>();
// One-click update: download + verify the release the backend offers, then install it (LocalSystem only).
// FIX-016: the installer must also be Authenticode-signed by a pinned Zinwe certificate (ZinweSigners).
builder.Services.AddSingleton<IInstallerSignatureCheck>(_ => new AuthenticodeSignatureCheck());
builder.Services.AddSingleton<UpdateDownloader>();
builder.Services.AddSingleton<AgentUpdater>();
builder.Services.AddHostedService<PostUpdateRelauncher>();
builder.Services.AddHostedService<PipeServer>();
builder.Services.AddHostedService<EventUploader>();
builder.Services.AddHostedService<UsbWatcherService>();

builder.Build().Run();

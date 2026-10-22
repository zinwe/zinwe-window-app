using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Zinwe.Agent.Core.Features;
using Zinwe.Agent.Core.Features.Network;
using Zinwe.Agent.Core.Usb;

namespace Zinwe.Agent.Service.Features.Network;

/// <summary>
/// DI wiring for the "network" feature (upload monitoring by destination domain). Auto-discovered; no Program.cs
/// edits. SAFETY/overhead: the real ETW trace source is used only when the feature is enabled AND the process is
/// LocalSystem (an ETW kernel session needs that). Every other context gets the no-op source, so dev and
/// non-elevated runs are safe and quiet.
/// </summary>
public sealed class NetworkFeatureRegistration : IFeatureRegistration
{
    public void Register(IServiceCollection services, IConfiguration config)
    {
        var defaults = config.GetSection(NetworkFeatureOptions.Section).Get<NetworkFeatureOptions>()
            ?? new NetworkFeatureOptions();

        // On whenever the owner's Online protection (policy "web" block) is on for this PC — it's off when the plan
        // lacks it or the member declined consent — or when config forces it (FR-04: it used to be config-only and
        // off, so no PC measured anything). A new policy rebuilds the scope.
        services.AddScoped(sp => NetworkPolicyMap.FromPolicy(defaults, sp.GetRequiredService<FeaturePolicyState>().Current?.Web));
        services.AddScoped<NetworkUploadCollector>();
        services.AddScoped<NetworkUsageCollector>();

        services.AddScoped<INetworkTraceSource>(sp =>
            sp.GetRequiredService<NetworkFeatureOptions>().Enabled && UsbEnforcerSelector.IsRunningAsSystem()
                ? new EtwNetworkTraceSource(sp.GetRequiredService<ILogger<EtwNetworkTraceSource>>())
                : new NullNetworkTraceSource());

        services.AddScoped<NetworkFeatureModule>();
        services.AddScoped<IFeatureModule>(sp => sp.GetRequiredService<NetworkFeatureModule>());
    }
}

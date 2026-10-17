using Zinwe.Agent.Core.Features;
using Zinwe.Agent.Core.Features.Web;
using Zinwe.Agent.Core.Usb;

namespace Zinwe.Agent.Service.Features.Web;

/// <summary>
/// DI wiring for the "web" feature (internet usage + website blocking). Auto-discovered by the feature host, so
/// this adds nothing to Program.cs. SAFETY: the real hosts-file blocker is selected only when running as
/// LocalSystem with Agent:Enforcement:DryRun=false — every other context gets the dry-run blocker.
/// </summary>
public sealed class WebFeatureRegistration : IFeatureRegistration
{
    public void Register(IServiceCollection services, IConfiguration config)
    {
        // appsettings defaults, captured once; the owner's policy block overlays them per feature scope.
        var defaults = new WebFeatureOptions();
        config.GetSection(WebFeatureOptions.Section).Bind(defaults);
        var dryRun = config.GetValue<bool>("Agent:Enforcement:DryRun");

        // Scoped: a new policy rebuilds the scope with options mapped from its web block (or defaults if absent).
        services.AddScoped(sp => WebPolicyMap.FromPolicy(defaults, sp.GetRequiredService<FeaturePolicyState>().Current?.Web));

        services.AddScoped(sp => Blocklist.FromOptions(sp.GetRequiredService<WebFeatureOptions>()));
        services.AddScoped<IForegroundWindowSource, Win32ForegroundWindowSource>();
        services.AddScoped<IActiveTabSource, UiaActiveTabSource>();
        services.AddScoped<BrowserDomainSampler>();
        services.AddScoped<IBlockedAttemptSource, NullBlockedAttemptSource>();

        services.AddScoped<IWebBlocker>(sp => WebBlockerSelector.Select(
            UsbEnforcerSelector.IsRunningAsSystem(),
            dryRun,
            () => new HostsFileWebBlocker(new HostsFileWriter(), sp.GetRequiredService<ILogger<HostsFileWebBlocker>>()),
            sp.GetRequiredService<ILoggerFactory>()));

        services.AddScoped<WebProtection>();
        // "Zinwe blocked {site} — your company's rule" notes in the member's widget (NotificationWriter is the
        // Program.cs singleton targeting the signed-in member's widget folder). One throttle for the service's life.
        services.AddScoped<IWebBlockNotices>(sp => new WidgetWebBlockNotices(sp.GetRequiredService<Zinwe.Agent.Core.Messages.NotificationWriter>()));
        services.AddSingleton<BlockedSiteThrottle>(_ => new BlockedSiteThrottle());
        services.AddScoped<ForegroundUsageSampler>();
        services.AddScoped<WebFeatureModule>();
        services.AddScoped<IFeatureModule>(sp => sp.GetRequiredService<WebFeatureModule>());
    }
}

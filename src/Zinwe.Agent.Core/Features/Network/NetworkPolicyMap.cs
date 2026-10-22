using Zinwe.Agent.Core.Policy;

namespace Zinwe.Agent.Core.Features.Network;

/// <summary>
/// The network feature's options for one policy (FR-04): the appsettings defaults, switched on whenever the owner's
/// Online protection (<see cref="WebPolicy"/>) is on for this PC. The backend turns that block off when the plan
/// lacks it or the member declined consent, so data-used measuring follows the same rules. No policy yet (first
/// boot) keeps the defaults.
/// </summary>
public static class NetworkPolicyMap
{
    public static NetworkFeatureOptions FromPolicy(NetworkFeatureOptions defaults, WebPolicy? web) => new()
    {
        Enabled = defaults.Enabled || web?.Enabled == true,
        FlushSeconds = defaults.FlushSeconds,
        MaxTrackedHosts = defaults.MaxTrackedHosts,
        Apps = [.. defaults.Apps],
        WarnBytes = defaults.WarnBytes,
    };
}

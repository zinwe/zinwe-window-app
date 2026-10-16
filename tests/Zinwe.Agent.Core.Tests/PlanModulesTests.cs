using System.Net;
using Zinwe.Agent.Core.Ipc;
using Zinwe.Agent.Core.Policy;

namespace Zinwe.Agent.Core.Tests;

/// <summary>The owner's plan, as sent with the policy: what the widget hides (work hours, chat) and that it's carried to the tray.</summary>
public sealed class PlanModulesTests
{
    [Fact]
    public void No_list_means_an_older_backend_so_nothing_is_hidden()
    {
        Assert.True(PlanModules.Allows(null, PlanModules.WorkTime));
        Assert.True(PlanModules.Allows(null, PlanModules.TeamChat));
    }

    [Fact]
    public void A_list_switches_on_only_what_it_names()
    {
        var startups = new[] { "usb_alerts", "usb_control", "web" };
        Assert.False(PlanModules.Allows(startups, PlanModules.WorkTime));
        Assert.False(PlanModules.Allows(startups, PlanModules.TeamChat));
        Assert.True(PlanModules.Allows(new[] { "work_time", "team_chat" }, PlanModules.TeamChat));
    }

    [Fact]
    public async Task GetPolicy_reads_the_plan_from_the_backend()
    {
        var backend = new FakeBackend
        {
            Respond = _ => FakeBackend.Json(
                HttpStatusCode.OK,
                """{"version": 3572, "usb": {"mode": "allow"}, "entitlements": ["usb_alerts"], "max_devices": 3}"""),
        };

        var policy = await backend.Client().GetPolicyAsync("tok", CancellationToken.None);

        Assert.Equal(3572, policy.Version);
        Assert.Equal(new[] { "usb_alerts" }, policy.Entitlements);
        Assert.Equal(3, policy.MaxDevices);
    }

    [Fact]
    public async Task GetPolicy_from_an_older_backend_has_no_plan()
    {
        var backend = new FakeBackend { Respond = _ => FakeBackend.Json(HttpStatusCode.OK, """{"version": 3, "usb": {"mode": "allow"}}""") };

        var policy = await backend.Client().GetPolicyAsync("tok", CancellationToken.None);

        Assert.Null(policy.Entitlements);
        Assert.Null(policy.MaxDevices);
    }

    [Fact]
    public void The_tray_gets_the_plan_over_the_pipe()
    {
        var status = new AgentStatus(AgentState.Connected, "0.1.0", "PC-01", 7, null, null, Entitlements: new[] { "usb_alerts", "web" });

        var back = PipeProtocol.DeserializeStatus(PipeProtocol.Serialize(status));

        Assert.Equal(new[] { "usb_alerts", "web" }, back?.Entitlements);
    }
}

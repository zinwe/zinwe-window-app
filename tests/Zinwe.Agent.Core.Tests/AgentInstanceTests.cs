namespace Zinwe.Agent.Core.Tests;

public sealed class AgentInstanceTests
{
    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("  ", "")]
    [InlineData("dev", "dev")]
    [InlineData(" DEV ", "dev")]
    [InlineData(@"dev\..\x", "devx")]
    [InlineData("a-very-long-instance-name-indeed", "a-very-long-instance")]
    public void Normalize_keeps_a_short_safe_name(string? raw, string expected) =>
        Assert.Equal(expected, AgentInstance.Normalize(raw));

    [Fact]
    public void The_installed_agent_keeps_the_plain_names()
    {
        Assert.Equal("Zinwe.Agent", AgentInstance.Apply("Zinwe.Agent", ".", ""));
        Assert.Equal(@"SOFTWARE\Zinwe\Agent", AgentInstance.Apply(@"SOFTWARE\Zinwe\Agent", "-", ""));
    }

    [Fact]
    public void A_named_instance_gets_its_own_names()
    {
        Assert.Equal("Zinwe.Agent.dev", AgentInstance.Apply("Zinwe.Agent", ".", "dev"));
        Assert.Equal(@"SOFTWARE\Zinwe\Agent-dev", AgentInstance.Apply(@"SOFTWARE\Zinwe\Agent", "-", "dev"));
        Assert.Equal("Zinwe-dev", AgentInstance.Apply("Zinwe", "-", "dev"));
    }

    [Fact]
    public void Tests_run_as_the_installed_instance()
    {
        // The test runner doesn't set ZINWE_INSTANCE, so the shipped names are what gets tested elsewhere.
        Assert.True(AgentInstance.IsDefault);
        Assert.Equal("Zinwe.Agent", Ipc.PipeProtocol.PipeName);
        Assert.Equal(@"SOFTWARE\Zinwe\Agent", AgentInfo.RegistryKeyPath);
    }
}

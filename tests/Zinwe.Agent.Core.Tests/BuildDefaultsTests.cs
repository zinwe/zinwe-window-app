using System.Text.Json;
using System.Xml.Linq;

namespace Zinwe.Agent.Core.Tests;

/// <summary>FIX-017: what a build gets when nobody passes -BackendUrl / -Version.</summary>
public sealed class BuildDefaultsTests
{
    private static readonly string[] LiveBackends = ["https://prod.backend.zinwe.io", "https://dev.backend.zinwe.io"];

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Zinwe.Agent.slnx")))
        {
            dir = dir.Parent;
        }
        return dir?.FullName ?? throw new InvalidOperationException("window-app root not found");
    }

    [Fact]
    public void The_committed_backend_is_the_live_production_server()
    {
        using var settings = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoRoot(), "src", "Zinwe.Agent.Service", "appsettings.json")));
        var url = settings.RootElement.GetProperty("Agent").GetProperty("BackendBaseUrl").GetString();

        Assert.Equal("https://prod.backend.zinwe.io", url);
        Assert.Contains(url, LiveBackends);
    }

    [Fact]
    public void The_version_line_is_a_real_release_not_the_old_placeholder()
    {
        var props = XDocument.Load(Path.Combine(RepoRoot(), "Directory.Build.props"));
        var version = Version.Parse(props.Descendants("Version").Single().Value);

        Assert.True(version >= new Version(1, 1, 2), $"Directory.Build.props says {version}");
        Assert.Equal(version.ToString(3), AgentInfo.Version); // the binaries carry it
    }
}

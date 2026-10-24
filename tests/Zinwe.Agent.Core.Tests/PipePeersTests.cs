using System.IO.Pipes;
using Zinwe.Agent.Core.Ipc;

namespace Zinwe.Agent.Core.Tests;

/// <summary>FIX-015: the service only serves the Zinwe tray, and the tray only talks to the Zinwe service.</summary>
public sealed class PipePeersTests
{
    private const string InstalledService = @"C:\Program Files\Zinwe Agent\service\";
    private const string InstalledTray = @"C:\Program Files\Zinwe Agent\tray\Zinwe.Agent.Tray.exe";
    private const string DevService = @"E:\work\window-app\src\Zinwe.Agent.Service\bin\Debug\net10.0-windows\win-x64\";
    private const string DevTray = @"E:\work\window-app\src\Zinwe.Agent.Tray\bin\Debug\net10.0-windows\win-x64\";

    [Theory]
    [InlineData(InstalledTray, true)]
    [InlineData(@"c:\program files\zinwe agent\TRAY\zinwe.agent.tray.exe", true)] // Windows paths are case-insensitive
    [InlineData(@"C:\Users\asha\AppData\Local\Temp\Zinwe.Agent.Tray.exe", false)] // a look-alike copied elsewhere
    [InlineData(@"C:\Program Files\Zinwe Agent\tray\powershell.exe", false)]
    [InlineData(@"C:\Program Files\Zinwe Agent\service\Zinwe.Agent.Tray.exe", false)]
    [InlineData(@"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe", false)]
    [InlineData(DevTray + "Zinwe.Agent.Tray.exe", false)] // the installed service never serves a dev build
    [InlineData("", false)]
    [InlineData(null, false)]
    public void The_installed_service_serves_only_its_own_tray(string? client, bool trusted) =>
        Assert.Equal(trusted, PipePeers.IsTrustedTrayPath(client, InstalledService));

    [Fact]
    public void A_dev_service_serves_the_tray_built_in_the_same_tree()
    {
        Assert.True(PipePeers.IsTrustedTrayPath(DevTray + "Zinwe.Agent.Tray.exe", DevService));
        Assert.True(PipePeers.IsTrustedTrayPath(@"E:\work\window-app\src\Zinwe.Agent.Tray\bin\Release\net10.0-windows\win-x64\Zinwe.Agent.Tray.exe", DevService));
        Assert.False(PipePeers.IsTrustedTrayPath(@"E:\other\src\Zinwe.Agent.Tray\bin\Debug\Zinwe.Agent.Tray.exe", DevService));
        Assert.False(PipePeers.IsTrustedTrayPath(DevTray + "evil.exe", DevService));
        Assert.False(PipePeers.IsTrustedTrayPath(InstalledTray, DevService));
    }

    [Fact]
    public void The_tray_trusts_the_process_the_service_manager_started()
    {
        Assert.True(PipePeers.IsTrustedServer(4321, scmServiceProcessId: 4321, @"C:\Program Files\Zinwe Agent\service\Zinwe.Agent.Service.exe",
            @"C:\Program Files\Zinwe Agent\tray\", defaultInstance: true));
        // The real service exe started by a user (not by the SCM) to squat the pipe name: same path, wrong process.
        Assert.False(PipePeers.IsTrustedServer(999, scmServiceProcessId: 4321, @"C:\Program Files\Zinwe Agent\service\Zinwe.Agent.Service.exe",
            @"C:\Program Files\Zinwe Agent\tray\", defaultInstance: true));
        // Service stopped (no SCM process): nobody on the pipe is trusted.
        Assert.False(PipePeers.IsTrustedServer(999, scmServiceProcessId: null, @"C:\Temp\Zinwe.Agent.Service.exe",
            @"C:\Program Files\Zinwe Agent\tray\", defaultInstance: true));
        // The dev pipe is never the installed service's.
        Assert.False(PipePeers.IsTrustedServer(4321, scmServiceProcessId: 4321, @"C:\Program Files\Zinwe Agent\service\Zinwe.Agent.Service.exe",
            DevTray, defaultInstance: false));
    }

    [Fact]
    public void A_dev_tray_trusts_the_service_built_in_the_same_tree()
    {
        Assert.True(PipePeers.IsTrustedServer(7, null, DevService + "Zinwe.Agent.Service.exe", DevTray, defaultInstance: false));
        Assert.True(PipePeers.IsTrustedServer(7, null, DevService + "Zinwe.Agent.Service.exe", DevTray, defaultInstance: true));
        Assert.False(PipePeers.IsTrustedServer(7, null, @"E:\other\src\Zinwe.Agent.Service\bin\Zinwe.Agent.Service.exe", DevTray, defaultInstance: false));
        Assert.False(PipePeers.IsTrustedServer(7, null, DevService + "Zinwe.Agent.Service.exe", @"C:\Program Files\Zinwe Agent\tray\", defaultInstance: false));
    }

    [Fact]
    public void The_service_manager_reports_process_ids()
    {
        Assert.True(PipePeers.ServiceProcessId("EventLog") > 0); // always running on Windows
        Assert.Null(PipePeers.ServiceProcessId("ZinweNoSuchService-" + Guid.NewGuid().ToString("N")));
    }

    [Fact]
    public async Task Each_end_sees_which_program_is_on_the_other_end()
    {
        var name = "Zinwe.Agent.test-" + Guid.NewGuid().ToString("N");
        await using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var waiting = server.WaitForConnectionAsync();
        await using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        await client.ConnectAsync(5000);
        await waiting;

        // Both ends are this test process - neither the Zinwe tray nor the Zinwe service.
        Assert.Equal(Environment.ProcessPath, PipePeers.ClientImage(server), StringComparer.OrdinalIgnoreCase);
        Assert.Equal(Environment.ProcessPath, PipePeers.ServerImage(client), StringComparer.OrdinalIgnoreCase);
        Assert.False(PipePeers.IsTrustedTray(server, AppContext.BaseDirectory, out var image));
        Assert.Equal(Environment.ProcessPath, image, StringComparer.OrdinalIgnoreCase);
        Assert.False(PipePeers.IsTrustedService(client, AppContext.BaseDirectory));
    }

    [Fact]
    public async Task An_impostor_server_never_receives_the_enrolment()
    {
        var name = "Zinwe.Agent.test-" + Guid.NewGuid().ToString("N");
        await using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var waiting = server.WaitForConnectionAsync();

        // This test process squats a pipe name: the client must hang up without sending the email + Member ID.
        var reply = await AgentPipeClient.ExchangeAsync(name,
            new PipeProtocol.Request(PipeProtocol.RequestTypes.Enroll, "a@b.in", "ZM-SECRET"), TimeSpan.FromSeconds(5), CancellationToken.None);
        await waiting;

        Assert.Null(reply);
        using var reader = new StreamReader(server);
        Assert.Null(await reader.ReadLineAsync()); // the client closed without writing a byte
    }

    [Theory]
    [InlineData(AgentState.NotEnrolled, true)]
    [InlineData(AgentState.Enrolling, true)]
    [InlineData(AgentState.Rejected, true)]
    [InlineData(AgentState.Connected, false)]
    [InlineData(AgentState.Offline, false)]
    [InlineData(AgentState.UpdateRequired, false)]
    public void Enrolment_over_the_pipe_only_while_not_connected(AgentState state, bool accepted) =>
        Assert.Equal(accepted, PipeProtocol.AcceptsEnrollment(state));
}

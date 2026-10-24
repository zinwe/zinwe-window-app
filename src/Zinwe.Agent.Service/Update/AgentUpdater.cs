using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Zinwe.Agent.Core;
using Zinwe.Agent.Core.Backend;
using Zinwe.Agent.Core.Identity;
using Zinwe.Agent.Core.Ipc;
using Zinwe.Agent.Core.Update;
using Zinwe.Agent.Core.Usb;

namespace Zinwe.Agent.Service.Update;

/// <summary>
/// The one-click update the member starts from the widget ("Update now"):
/// <list type="number">
/// <item>download the release's MSI and verify its SHA-256 and its Authenticode signature by a pinned Zinwe
///       certificate (<see cref="UpdateDownloader"/>, <see cref="AuthenticodeSignatureCheck"/>);</item>
/// <item>leave a marker so the NEXT service start reopens the tray (<see cref="PostUpdateRelauncher"/>);</item>
/// <item>say "installing" - the tray closes itself so its files can be replaced (stragglers are closed after
///       a grace period);</item>
/// <item>run <c>msiexec /i … /qn</c> as SYSTEM. The MSI's MajorUpgrade stops this service, replaces both apps and
///       starts the new service, which reopens the tray. If msiexec fails, this process is still alive: it reopens
///       the tray itself and reports the failure.</item>
/// </list>
/// Only as the installed LocalSystem service with enforcement on. The dev console agent (dry-run) downloads and
/// verifies, then stops - it never installs anything on a developer's PC.
/// </summary>
internal sealed partial class AgentUpdater(
    UpdateDownloader downloader,
    IdentityStore identities,
    AgentStateHolder state,
    IOptions<AgentOptions> options,
    ILogger<AgentUpdater> logger)
{
    private static readonly TimeSpan TrayCloseGrace = TimeSpan.FromSeconds(12);
    private const string TrayProcessName = "Zinwe.Agent.Tray";

    private int _running;

    private string DataDirectory => Environment.ExpandEnvironmentVariables(options.Value.DataDirectory);

    /// <summary>The widget's "Update now". Returns at once; progress is reported on the status the tray polls.</summary>
    public PipeProtocol.EnrollAck Start()
    {
        var offer = state.Offer;
        if (offer is null)
        {
            return new(false, "Zinwe is already up to date.");
        }
        if (string.IsNullOrWhiteSpace(offer.DownloadUrl) || string.IsNullOrWhiteSpace(offer.Sha256))
        {
            return new(false, "There's no installer for this update yet. Use \"Download manually\" or ask your admin.");
        }
        var token = identities.Load()?.DeviceToken;
        if (token is null)
        {
            return new(false, "This PC isn't connected to Zinwe yet.");
        }
        if (Interlocked.Exchange(ref _running, 1) == 1)
        {
            return new(true, "The update is already running.");
        }
        _ = Task.Run(() => RunAsync(offer, token));
        return new(true, $"Updating to Zinwe {offer.Version}…");
    }

    private async Task RunAsync(AgentUpdate offer, string deviceToken)
    {
        try
        {
            state.SetUpdateStage(UpdateStages.Downloading, $"Downloading Zinwe {offer.Version}…");
            LogDownloading(offer.Version);
            var updates = Path.Combine(DataDirectory, "updates");
            var msi = await downloader.DownloadAsync(offer, deviceToken, updates, CancellationToken.None);
            LogVerified(offer.Version, msi);

            if (!UsbEnforcerSelector.IsRunningAsSystem() || options.Value.Enforcement.DryRun)
            {
                state.SetUpdateStage(UpdateStages.Ready,
                    $"Zinwe {offer.Version} is downloaded and checked. This is a test build, so it isn't installed.");
                LogDryRun(msi);
                return;
            }
            await InstallAsync(msi, offer, updates);
        }
        catch (UpdateException e)
        {
            state.SetUpdateStage(UpdateStages.Failed, e.Message);
            LogFailed(e.Detail is null ? e.Message : $"{e.Message} {e.Detail}");
        }
        catch (Exception e)
        {
            state.SetUpdateStage(UpdateStages.Failed, "The update didn't work. Please try again, or ask your admin.");
            LogCrashed(e);
        }
        finally
        {
            Interlocked.Exchange(ref _running, 0);
        }
    }

    private async Task InstallAsync(string msi, AgentUpdate offer, string updates)
    {
        PostUpdateRelauncher.MarkPending(DataDirectory, offer.Version);
        state.SetUpdateStage(UpdateStages.Installing, "Installing the update. Zinwe will reopen in a moment.");
        await CloseTraysAsync();

        // Keep this PC pointed at the same backend: the old product's registry value goes with it on upgrade.
        var backend = options.Value.BackendBaseUrl;
        var log = Path.Combine(updates, $"install-{offer.Version}.log");
        var arguments = $"/i \"{msi}\" /qn /norestart /l*v \"{log}\"" +
                        (string.IsNullOrWhiteSpace(backend) ? "" : $" BACKEND_URL=\"{backend}\"");
        LogInstalling(offer.Version, log);
        using var msiexec = Process.Start(new ProcessStartInfo("msiexec.exe", arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        }) ?? throw new UpdateException("Windows Installer couldn't be started.");

        // A successful install stops this service before msiexec exits, so we only get past this on failure.
        await msiexec.WaitForExitAsync();
        if (msiexec.ExitCode is 0 or 3010)
        {
            return; // installed (3010: a restart finishes it); the new service takes over from here
        }
        PostUpdateRelauncher.ClearPending(DataDirectory);
        TrayLauncher.TryLaunch(logger);
        throw new UpdateException(
            $"The update couldn't be installed (Windows Installer code {msiexec.ExitCode}). Please try again, or ask your admin.");
    }

    /// <summary>Give the tray time to see "installing" and close; then close any that didn't (it's reopened after).</summary>
    private async Task CloseTraysAsync()
    {
        var deadline = DateTimeOffset.UtcNow + TrayCloseGrace;
        while (DateTimeOffset.UtcNow < deadline && Process.GetProcessesByName(TrayProcessName).Length > 0)
        {
            await Task.Delay(500);
        }
        foreach (var tray in Process.GetProcessesByName(TrayProcessName))
        {
            using (tray)
            {
                try
                {
                    tray.Kill();
                }
                catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    // already gone / no access: the installer will report the file in use
                }
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Update: downloading Zinwe {Version}")]
    private partial void LogDownloading(string version);

    [LoggerMessage(Level = LogLevel.Information, Message = "Update: Zinwe {Version} downloaded and verified ({Path})")]
    private partial void LogVerified(string version, string path);

    [LoggerMessage(Level = LogLevel.Information, Message = "Update: dry-run / not LocalSystem, so the verified installer is not run: {Path}")]
    private partial void LogDryRun(string path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Update: installing Zinwe {Version} (msiexec log: {Log})")]
    private partial void LogInstalling(string version, string log);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Update failed: {Reason}")]
    private partial void LogFailed(string reason);

    [LoggerMessage(Level = LogLevel.Error, Message = "Update crashed")]
    private partial void LogCrashed(Exception e);
}

using System.Text;
using Zinwe.Agent.Core.Features.Web;

namespace Zinwe.Agent.Service.Features.Web;

/// <summary>
/// The only code that edits the system hosts file. Lives in the service (not Core) so no test can construct it,
/// and is only instantiated by <see cref="WebBlockerSelector"/> when the process is LocalSystem and dry run is
/// off. All the block-merge logic is the pure <see cref="HostsBlock"/> in Core; this just reads and writes bytes.
/// </summary>
internal sealed class HostsFileWriter : IHostsFileWriter
{
    private static string HostsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.System), "drivers", "etc", "hosts");

    public void SetBlockedDomains(IReadOnlyList<string> hostnames) => Rewrite(hostnames);

    public void ClearBlockedDomains() => Rewrite([]);

    private static void Rewrite(IReadOnlyList<string> hostnames)
    {
        var path = HostsPath;
        var existing = File.Exists(path) ? File.ReadAllText(path) : "";
        var updated = HostsBlock.ApplyToHosts(existing, hostnames);
        // Only on a real change: every write makes the DNS Client service re-read the whole file.
        if (!string.Equals(existing, updated, StringComparison.Ordinal))
        {
            // No BOM: the hosts file must stay plain ASCII/UTF-8 without a byte-order mark.
            AtomicFile.Replace(path, updated, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
    }
}

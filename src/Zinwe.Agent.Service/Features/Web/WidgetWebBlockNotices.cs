using Zinwe.Agent.Core.Backend;
using Zinwe.Agent.Core.Events;
using Zinwe.Agent.Core.Features.Web;
using Zinwe.Agent.Core.Messages;

namespace Zinwe.Agent.Service.Features.Web;

/// <summary>
/// Writes the website-blocking notes into the member's widget (the tray toasts new ones), so a blocked site
/// explains itself instead of looking like a broken internet. Best effort: never affects blocking.
/// </summary>
internal sealed class WidgetWebBlockNotices(NotificationWriter writer) : IWebBlockNotices
{
    public void BlocklistInForce(Blocklist list)
    {
        var (title, body) = WebBlockWording.InForce(list);
        // A stable id per list: the same list re-applied (every restart, every policy re-fetch) adds nothing.
        Write(WebBlockWording.StableId(list), title, body);
    }

    public void SiteBlocked(string domain, string? category)
    {
        var (title, body) = WebBlockWording.Blocked(domain, category);
        Write(-DateTimeOffset.UtcNow.Ticks - Random.Shared.Next(1000), title, body);
    }

    private void Write(long id, string title, string body)
    {
        try
        {
            writer.AddNotices([new AgentMessage(id, title, body, EventSeverity.Info, DateTimeOffset.Now)]);
        }
        catch
        {
            // the widget note is best-effort; never let it affect blocking
        }
    }
}

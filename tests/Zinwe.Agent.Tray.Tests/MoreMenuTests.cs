using System.Windows.Input;
using Zinwe.Agent.Core.Ipc;
using Zinwe.Agent.Core.Work;
using Zinwe.Agent.Tray.Resources;
using Zinwe.Agent.Tray.Widget;

namespace Zinwe.Agent.Tray.Tests;

/// <summary>The widget's ⋯ menu (version, feedback, what Zinwe records, policies) and the Ctrl+Alt+Z shortcut.</summary>
public sealed class MoreMenuTests
{
    private static AgentStatus Connected(string? update = null) =>
        new(AgentState.Connected, "1.1.2", "pc", 1, null, null, UpdateVersion: update);

    [Fact]
    public void The_version_line_says_up_to_date()
    {
        var menu = MoreMenu.From(Connected(), "1.1.2", WidgetScreen.Main, ConsentState.Given);

        Assert.Equal("Zinwe 1.1.2", menu.Version);
        Assert.Equal("Up to date", menu.VersionDetail);
        Assert.False(menu.UpdateWaiting);
        Assert.True(menu.FeedbackEnabled);
        Assert.True(menu.RecordsEnabled);
    }

    [Fact]
    public void A_waiting_update_is_named_and_leads_to_the_update_card()
    {
        var menu = MoreMenu.From(Connected(update: "1.2.0"), "1.1.2", WidgetScreen.Main, ConsentState.Given);

        Assert.Equal("Version 1.2.0 is ready to install", menu.VersionDetail);
        Assert.True(menu.UpdateWaiting);
    }

    [Fact]
    public void Without_the_service_it_shows_this_window_version_and_claims_nothing()
    {
        var menu = MoreMenu.From(null, "1.1.2", WidgetScreen.None, ConsentState.Pending);

        Assert.Equal("Zinwe 1.1.2", menu.Version);
        Assert.Null(menu.VersionDetail);
        Assert.False(menu.FeedbackEnabled);
        Assert.False(menu.RecordsEnabled);
    }

    [Fact]
    public void The_notice_cannot_be_reopened_before_it_was_answered()
    {
        var menu = MoreMenu.From(Connected(), "1.1.2", WidgetScreen.Consent, ConsentState.Pending);

        Assert.False(menu.RecordsEnabled);
        Assert.False(menu.FeedbackEnabled);
    }

    [Fact]
    public void Ctrl_alt_z_registers_without_key_repeat()
    {
        // MOD_ALT 0x1 | MOD_CONTROL 0x2 | MOD_NOREPEAT 0x4000
        Assert.Equal(0x4003u, GlobalHotkey.Win32Modifiers(ModifierKeys.Control | ModifierKeys.Alt));
        Assert.Equal("Ctrl+Alt+Z", Strings.Shortcut);
        Assert.Contains("Ctrl + Alt + Z", Strings.ShortcutHint);
    }
}

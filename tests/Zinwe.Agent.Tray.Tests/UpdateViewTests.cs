using Zinwe.Agent.Core.Ipc;
using Zinwe.Agent.Tray.Widget;

namespace Zinwe.Agent.Tray.Tests;

public sealed class UpdateViewTests
{
    private static AgentStatus Status(AgentState state, string? version = "0.3.0", string? stage = null,
        string? message = null, bool installable = true, string? notes = "Faster chat", string? downloadUrl = null) =>
        new(state, "0.2.0", "pc", 1, null, null, DownloadUrl: downloadUrl,
            UpdateVersion: version, UpdateNotes: notes, UpdateStage: stage, UpdateMessage: message, UpdateInstallable: installable);

    [Fact]
    public void Up_to_date_shows_nothing()
    {
        Assert.False(UpdateView.From(Status(AgentState.Connected, version: null)).Show);
        Assert.False(UpdateView.From(null).Show);
    }

    [Fact]
    public void A_required_update_keeps_the_work_day_going_and_offers_one_click()
    {
        var view = UpdateView.From(Status(AgentState.UpdateRequired));
        Assert.True(view.Show && view.Required);
        Assert.Equal("Zinwe needs an update", view.Title);
        Assert.Contains("Zinwe 0.3.0 is needed", view.Body);
        Assert.Contains("you can keep working", view.Body);
        Assert.DoesNotContain("paused", view.Body);
        Assert.DoesNotContain("your company", view.Body); // Zinwe delivers it, not the employer
        Assert.Equal(("Update now", true, true), (view.Button, view.ButtonVisible, view.ButtonEnabled));
        Assert.Equal("Faster chat", view.Notes);
        Assert.False(view.ShowManual);
    }

    [Fact]
    public void An_optional_update_does_not_pause_anything()
    {
        var view = UpdateView.From(Status(AgentState.Connected));
        Assert.True(view.Show);
        Assert.False(view.Required);
        Assert.Equal("Update available", view.Title);
        Assert.DoesNotContain("paused", view.Body);
    }

    [Theory]
    [InlineData(UpdateStages.Downloading, "Downloading…", true)]
    [InlineData(UpdateStages.Installing, "Installing…", true)]
    [InlineData(UpdateStages.Ready, "Downloaded", false)]
    public void Progress_disables_the_button_and_shows_the_stage(string stage, string button, bool busy)
    {
        var view = UpdateView.From(Status(AgentState.UpdateRequired, stage: stage, message: "msg"));
        Assert.Equal((button, false, busy, "msg"), (view.Button, view.ButtonEnabled, view.Busy, view.Progress));
    }

    [Fact]
    public void A_failure_offers_try_again_and_the_download_page()
    {
        var view = UpdateView.From(Status(AgentState.UpdateRequired, stage: UpdateStages.Failed, message: "safety check",
            downloadUrl: "https://zinwe.io/download"));
        Assert.Equal(("Try again", true, true), (view.Button, view.ButtonEnabled, view.ShowManual));
        Assert.True(view.ProgressIsError);
        Assert.Equal("safety check", view.Progress);
    }

    [Fact]
    public void Without_a_published_installer_there_is_nothing_for_the_employee_to_do()
    {
        // e.g. only the env minimum version was raised, no MSI uploaded
        var view = UpdateView.From(Status(AgentState.UpdateRequired, installable: false));
        Assert.False(view.ButtonVisible);
        Assert.False(view.ShowManual);
        Assert.Contains("nothing for you to do", view.Body);
        Assert.DoesNotContain("admin", view.Body);
    }

    [Fact]
    public void Without_an_installer_but_with_a_download_page_the_page_is_offered()
    {
        var view = UpdateView.From(Status(AgentState.UpdateRequired, installable: false, downloadUrl: "https://zinwe.io/download"));
        Assert.True(view.ShowManual);
    }
}

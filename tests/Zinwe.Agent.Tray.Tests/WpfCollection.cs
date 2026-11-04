namespace Zinwe.Agent.Tray.Tests;

/// <summary>
/// Tests that build the tray's WPF app, model or windows share one Application and its resources (TrayModel applies
/// dark mode to them), so they run one after another, never at the same time.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class WpfCollection
{
    public const string Name = "The Zinwe window (WPF)";
}

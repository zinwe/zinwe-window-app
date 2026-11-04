namespace Zinwe.Agent.Core.Ipc;

/// <summary>
/// The Zingle live today (the special-day logo), on <see cref="AgentStatus.Zingle"/>; null there = none (or an older
/// service), and the tray shows the normal wordmark. The files are copies in the member's own widget folder (the tray
/// can't read the service's data folder); their names change when the files do. Add fields only as optional trailing
/// parameters.
/// </summary>
/// <param name="Id">The Zingle's id on the server.</param>
/// <param name="Alt">What a screen reader says for it.</param>
/// <param name="StillFile">The still PNG (transparent).</param>
/// <param name="AnimatedFile">The animated GIF or WebP; null when there is none or it couldn't be fetched.</param>
public sealed record ZingleStatus(long Id, string Alt, string StillFile, string? AnimatedFile = null);

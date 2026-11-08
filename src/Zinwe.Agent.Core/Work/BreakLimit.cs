using Zinwe.Agent.Core.Backend;

namespace Zinwe.Agent.Core.Work;

/// <summary>
/// v1.4 · the break-time note on Home, from the work rules the heartbeat carries (§1c). Shown only when the owner lets
/// the member see it (<see cref="WorkRules.TellMemberOver"/>), there is a limit, and the member is near it (within
/// <see cref="NearMinutes"/>, or past 80 %) or over it. Plain words, no warning tone: "You've used 1 h 10 min of your
/// 1 h 15 min break time today."
/// </summary>
public static class BreakLimit
{
    public const int NearMinutes = 15;

    public enum Level { None, Near, Over }

    public static Level LevelOf(WorkRules? rules, TimeSpan used)
    {
        if (rules is not { TellMemberOver: true, BreakAllowedMin: > 0 } r)
        {
            return Level.None;
        }
        var allowed = TimeSpan.FromMinutes(r.BreakAllowedMin);
        if (used > allowed)
        {
            return Level.Over;
        }
        return used >= allowed - TimeSpan.FromMinutes(NearMinutes) || used.TotalMinutes >= r.BreakAllowedMin * 0.8 ? Level.Near : Level.None;
    }

    /// <summary>The note, or null when nothing needs saying.</summary>
    public static string? Note(WorkRules? rules, TimeSpan used)
    {
        var level = LevelOf(rules, used);
        if (level == Level.None)
        {
            return null;
        }
        var allowed = TimeSpan.FromMinutes(rules!.BreakAllowedMin);
        var line = $"You’ve used {Span(used)} of your {Span(allowed)} break time today.";
        return level == Level.Over ? line + $" That’s {Span(used - allowed)} over." : line;
    }

    /// <summary>"1 h 10 min", "45 min", "1 h".</summary>
    public static string Span(TimeSpan t)
    {
        var m = Math.Max(0, (int)Math.Floor(t.TotalMinutes));
        return m >= 60 ? m % 60 == 0 ? $"{m / 60} h" : $"{m / 60} h {m % 60} min" : $"{m} min";
    }
}

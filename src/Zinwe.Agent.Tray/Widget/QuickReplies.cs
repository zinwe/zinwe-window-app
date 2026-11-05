namespace Zinwe.Agent.Tray.Widget;

/// <summary>
/// The chat's quick replies (prototype "Windows app next"): the ⚡ button in the composer lists them all, and the member
/// stars up to <see cref="RowSize"/> for the row over the composer. The stars are kept on this PC (tray settings).
/// </summary>
public static class QuickReplies
{
    public const int RowSize = 3;

    /// <summary>Every quick reply, in the order the list shows them.</summary>
    public static readonly IReadOnlyList<string> All =
    [
        "On it", "Thanks!", "Running 10 min late", "Can we talk?", "Give me 5 minutes", "Done, please check",
        "On a call, back soon", "Can you call me?",
    ];

    /// <summary>The row before the member changes it.</summary>
    public static readonly IReadOnlyList<string> Defaults = ["On it", "Thanks!", "Running 10 min late"];

    /// <summary>What the list says when a fourth star is asked for.</summary>
    public const string RowFull = "The row holds 3. Unstar one first.";

    /// <summary>The starred replies to show: the defaults until changed; only known ones, once each, at most three.</summary>
    public static IReadOnlyList<string> Favourites(TraySettings settings) =>
        [.. (settings.QuickFavourites ?? Defaults).Where(All.Contains).Distinct().Take(RowSize)];

    /// <summary>
    /// A star pressed: an unstarred reply joins the row (unless it already holds three: then <paramref name="note"/>
    /// says why and nothing changes), a starred one leaves it.
    /// </summary>
    public static IReadOnlyList<string> Toggle(IReadOnlyList<string> favourites, string reply, out string? note)
    {
        note = null;
        if (favourites.Contains(reply))
        {
            return [.. favourites.Where(f => f != reply)];
        }
        if (favourites.Count >= RowSize)
        {
            note = RowFull;
            return favourites;
        }
        return [.. favourites, reply];
    }

    /// <summary>A few common emoji the composer's 🙂 button adds where the member is typing.</summary>
    public static readonly IReadOnlyList<string> Emoji =
    [
        "👍", "🙏", "😊", "😂", "🎉", "✅", "👀", "🔥", "❤️", "😅", "🤝", "💡", "⏰", "📞", "☕", "👌",
    ];
}

/// <summary>The chat backgrounds (Chat options ⋮): a soft pattern behind the messages, or none.</summary>
public static class ChatPatterns
{
    public const string Default = "calm";

    public static readonly IReadOnlyList<(string Key, string Name, string Sub)> All =
    [
        ("calm", "Calm", "Soft waves and leaves"),
        ("focus", "Focus", "Targets and ticks"),
        ("geek", "Geek", "Brackets and code"),
        ("money", "Money", "Rupees and coins"),
        ("plain", "Plain", "No pattern"),
    ];

    /// <summary>The pattern in force: the saved one when it is known, else the default.</summary>
    public static string Of(TraySettings settings) =>
        All.Any(p => p.Key == settings.ChatPattern) ? settings.ChatPattern! : Default;
}

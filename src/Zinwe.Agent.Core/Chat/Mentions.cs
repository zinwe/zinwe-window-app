namespace Zinwe.Agent.Core.Chat;

/// <summary>
/// v1.4.1 · @mentions in a group line: "@Priya Nair" (what the composer inserts) or "@Priya". Matching is by the names the
/// app knows (the group's people, the admin, the member), longest first, ignoring case, and never inside a longer word
/// ("@Priyanka" is not "@Priya"). A word after "@" that names nobody known still reads as a mention when it starts with a
/// capital letter, so it is drawn the same way in every line.
/// </summary>
public static class Mentions
{
    /// <summary>One "@Name" in a line: where it starts (at the "@"), how long it is ("@" included) and the name.</summary>
    public readonly record struct Span(int Start, int Length, string Name);

    /// <summary>The longest text after "@" the composer still treats as a name being typed.</summary>
    public const int MaxQuery = 30;

    /// <summary>Every mention in <paramref name="body"/>, in order.</summary>
    public static IReadOnlyList<Span> Find(string? body, IEnumerable<string?> names)
    {
        var found = new List<Span>();
        if (string.IsNullOrEmpty(body) || !body.Contains('@'))
        {
            return found;
        }
        var candidates = Candidates(names);
        for (var i = 0; i < body.Length; i++)
        {
            if (body[i] != '@' || (i > 0 && IsWord(body[i - 1])))
            {
                continue;
            }
            var rest = body[(i + 1)..];
            var hit = candidates.FirstOrDefault(n => rest.StartsWith(n, StringComparison.OrdinalIgnoreCase)
                && (rest.Length == n.Length || !IsWord(rest[n.Length])));
            if (hit is null)
            {
                // An unknown name: one capitalised word.
                var len = 0;
                while (len < rest.Length && IsWord(rest[len]))
                {
                    len++;
                }
                if (len == 0 || !char.IsUpper(rest[0]))
                {
                    continue;
                }
                hit = rest[..len].ToString();
            }
            else
            {
                hit = body.Substring(i + 1, hit.Length);
            }
            found.Add(new Span(i, hit.Length + 1, hit));
            i += hit.Length;
        }
        return found;
    }

    /// <summary>True when the line names the member: "@First Last" or "@First".</summary>
    public static bool MentionsMe(string? body, string? myName)
    {
        if (string.IsNullOrWhiteSpace(myName) || string.IsNullOrEmpty(body) || !body.Contains('@'))
        {
            return false;
        }
        var full = myName.Trim();
        var first = full.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
        var mine = new[] { full, first };
        return Find(body, mine).Any(s => mine.Any(n => string.Equals(n, s.Name, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>
    /// The name being typed in the composer: the "@" before the caret (at the start or after a space) and the text from it to
    /// the caret. Null when the caret isn't in one (a line break, too long, or a finished "@First Last ").
    /// </summary>
    public static (int At, string Query)? Query(string? text, int caret)
    {
        if (string.IsNullOrEmpty(text) || caret <= 0 || caret > text.Length)
        {
            return null;
        }
        var from = Math.Max(0, caret - MaxQuery - 1);
        for (var i = caret - 1; i >= from; i--)
        {
            var c = text[i];
            if (c == '\n' || c == '\r')
            {
                return null;
            }
            if (c != '@')
            {
                continue;
            }
            if (i > 0 && !char.IsWhiteSpace(text[i - 1]))
            {
                return null; // an email address, not a mention
            }
            var q = text[(i + 1)..caret];
            var spaces = q.Count(ch => ch == ' ');
            return spaces > 1 || q.StartsWith(' ') || (spaces == 1 && q.EndsWith(' ')) ? null : (i, q);
        }
        return null;
    }

    /// <summary>The people whose name (or any part of it) starts with <paramref name="query"/>; all of them for "".</summary>
    public static IReadOnlyList<TeamMember> Suggest(string? query, IEnumerable<TeamMember> people, int max = 6)
    {
        var q = (query ?? "").Trim();
        return [.. people
            .Where(p => !string.IsNullOrWhiteSpace(p.Name))
            .DistinctBy(p => p.Name.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(p => (p, rank: q.Length == 0 ? 0 : p.Name.StartsWith(q, StringComparison.OrdinalIgnoreCase) ? 0
                : p.Name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Any(w => w.StartsWith(q, StringComparison.OrdinalIgnoreCase)) ? 1 : 2))
            .Where(x => x.rank < 2)
            .OrderBy(x => x.rank).ThenBy(x => x.p.Name, StringComparer.CurrentCultureIgnoreCase)
            .Take(max)
            .Select(x => x.p)];
    }

    /// <summary>Put "@Name " in place of the typed "@que…" (from <paramref name="at"/> to the caret); the new text and caret.</summary>
    public static (string Text, int Caret) Insert(string text, int at, int caret, string name)
    {
        var tail = caret < text.Length ? text[caret..] : "";
        var insert = "@" + name.Trim() + " ";
        if (tail.StartsWith(' '))
        {
            tail = tail[1..];
        }
        return (text[..at] + insert + tail, at + insert.Length);
    }

    private static List<string> Candidates(IEnumerable<string?> names)
    {
        var all = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var n in names)
        {
            if (string.IsNullOrWhiteSpace(n))
            {
                continue;
            }
            var full = string.Join(' ', n.Split(' ', StringSplitOptions.RemoveEmptyEntries));
            all.Add(full);
            all.Add(full.Split(' ')[0]);
        }
        return [.. all.OrderByDescending(n => n.Length)];
    }

    private static bool IsWord(char c) => char.IsLetterOrDigit(c) || c is '_' or '\'' or '’' or '-';
}

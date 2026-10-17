using System.Text;

namespace Zinwe.Agent.Core.Backend;

/// <summary>
/// The Member ID and work email as the member types them on the Connect screen: forgiving about spaces, missing
/// dashes, lower case and a pasted label ("Member ID: zm du64 vj7m"), and friendly about what is wrong.
/// Pure, so it's unit-tested; used by the widget (before sending) and the service (defence in depth).
/// </summary>
public static class MemberIdInput
{
    /// <summary>What a Member ID looks like, for hints and errors.</summary>
    public const string Example = "ZM-XXXX-XXXX";

    private const string Prefix = "ZM";
    private const int CodeLength = 8;

    /// <summary>
    /// "ZM-DU64-VJ7M" from anything that ends in those 10 letters/digits (or just the 8 after "ZM"); null when the
    /// text can't be a Member ID.
    /// </summary>
    public static string? Normalize(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return null;
        }
        var chars = new StringBuilder(input.Length);
        foreach (var c in input.ToUpperInvariant())
        {
            if (c is >= 'A' and <= 'Z' or >= '0' and <= '9')
            {
                chars.Append(c);
            }
        }
        var s = chars.ToString();
        string code;
        if (s.Length >= Prefix.Length + CodeLength
            && s.AsSpan(s.Length - CodeLength - Prefix.Length, Prefix.Length).SequenceEqual(Prefix))
        {
            code = s[^CodeLength..];
        }
        else if (s.Length == CodeLength)
        {
            code = s;
        }
        else
        {
            return null;
        }
        return $"{Prefix}-{code[..4]}-{code[4..]}";
    }

    /// <summary>The trimmed, lower-cased email, or null when it can't be an email address.</summary>
    public static string? NormalizeEmail(string? input)
    {
        var email = input?.Trim().ToLowerInvariant() ?? "";
        var at = email.IndexOf('@', StringComparison.Ordinal);
        var domain = at < 0 ? "" : email[(at + 1)..];
        var ok = at > 0
            && at == email.LastIndexOf('@')
            && domain.IndexOf('.', StringComparison.Ordinal) > 0
            && !domain.EndsWith('.')
            && email.Length <= 254
            && !email.Any(char.IsWhiteSpace);
        return ok ? email : null;
    }

    /// <summary>
    /// Checks both fields. Returns the cleaned values, or the one sentence to show the member when something is
    /// missing or can't be right (nothing is sent then).
    /// </summary>
    public static (string Email, string MemberId, string? Problem) Validate(string? email, string? memberId)
    {
        var rawEmail = email?.Trim() ?? "";
        var rawId = memberId?.Trim() ?? "";
        if (rawEmail.Length == 0 && rawId.Length == 0)
        {
            return ("", "", "Enter your work email and your Member ID. Both are in the invite email from your admin.");
        }
        if (rawEmail.Length == 0)
        {
            return ("", "", "Enter your work email (the address your invite was sent to).");
        }
        if (rawId.Length == 0)
        {
            return ("", "", $"Enter your Member ID. It looks like {Example} and is in the invite email.");
        }
        var cleanEmail = NormalizeEmail(rawEmail);
        if (cleanEmail is null)
        {
            return ("", "", "That doesn't look like an email address. It should look like name@company.com.");
        }
        var cleanId = Normalize(rawId);
        if (cleanId is null)
        {
            return (cleanEmail, "", $"That doesn't look like a Member ID. It looks like {Example} (letters and numbers) — copy it from the invite email.");
        }
        return (cleanEmail, cleanId, null);
    }
}

/// <summary>Plain wording for a refused Connect, keyed off the field the backend named (never Django's own text).</summary>
public static class EnrollmentMessages
{
    public const string ServerBusy = "Zinwe's server is busy right now. We'll keep trying — you don't need to do anything.";
    public const string Unreachable = "Can't reach Zinwe. Check the internet and try again.";

    public static string ForRefusal(string? field, string? serverMessage)
    {
        var message = (serverMessage ?? "").Trim();
        if (field == "email")
        {
            return "That email doesn't match this Member ID. Use the work email your invite was sent to.";
        }
        if (field == "member_id" || message.StartsWith("Ensure this field", StringComparison.OrdinalIgnoreCase))
        {
            return $"That Member ID isn't right. It looks like {MemberIdInput.Example} — check the invite email from your admin.";
        }
        if (message.Contains("no longer active", StringComparison.OrdinalIgnoreCase))
        {
            return "This Member ID doesn't work any more. Your admin may have removed you or sent you a new ID — ask them for the latest invite email.";
        }
        return message.Length > 0 ? message : "That didn't work. Check your email and Member ID and try again.";
    }
}

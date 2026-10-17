using Zinwe.Agent.Core.Backend;

namespace Zinwe.Agent.Core.Tests;

public sealed class MemberIdInputTests
{
    [Theory]
    [InlineData("ZM-DU64-VJ7M", "ZM-DU64-VJ7M")]
    [InlineData("zm-du64-vj7m", "ZM-DU64-VJ7M")]
    [InlineData("ZMDU64VJ7M", "ZM-DU64-VJ7M")]
    [InlineData("  zm du64 vj7m  ", "ZM-DU64-VJ7M")]
    [InlineData("ZM–DU64–VJ7M", "ZM-DU64-VJ7M")] // en dashes from an email client
    [InlineData("Member ID: ZM-DU64-VJ7M", "ZM-DU64-VJ7M")]
    [InlineData("DU64-VJ7M", "ZM-DU64-VJ7M")] // without the ZM
    [InlineData("ZM-DU64-VJ7M.", "ZM-DU64-VJ7M")]
    public void Forgiving_about_spaces_dashes_case_and_labels(string typed, string expected)
    {
        Assert.Equal(expected, MemberIdInput.Normalize(typed));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("ZM-DU64-VJ7")]
    [InlineData("ZM-DU64-VJ7MX")]
    [InlineData("hello")]
    public void Not_a_member_id(string? typed)
    {
        Assert.Null(MemberIdInput.Normalize(typed));
    }

    [Theory]
    [InlineData(" Priya@Example.COM ", "priya@example.com")]
    [InlineData("a@b.in", "a@b.in")]
    public void Emails_are_trimmed_and_lower_cased(string typed, string expected)
    {
        Assert.Equal(expected, MemberIdInput.NormalizeEmail(typed));
    }

    [Theory]
    [InlineData("priya")]
    [InlineData("priya@")]
    [InlineData("priya@example")]
    [InlineData("@example.com")]
    [InlineData("pri ya@example.com")]
    [InlineData("a@b@c.com")]
    [InlineData("a@example.")]
    public void Not_an_email(string typed)
    {
        Assert.Null(MemberIdInput.NormalizeEmail(typed));
    }

    [Fact]
    public void Validate_returns_clean_values()
    {
        Assert.Equal(("priya@example.com", "ZM-DU64-VJ7M", (string?)null),
            MemberIdInput.Validate(" Priya@example.com", "zmdu64vj7m"));
    }

    [Theory]
    [InlineData("", "", "Enter your work email and your Member ID")]
    [InlineData("", "ZM-DU64-VJ7M", "Enter your work email")]
    [InlineData("priya@example.com", "", "Enter your Member ID")]
    [InlineData("priya", "ZM-DU64-VJ7M", "doesn't look like an email")]
    [InlineData("priya@example.com", "Ensure 16 characters", "doesn't look like a Member ID")]
    public void Validate_says_plainly_what_is_wrong(string email, string id, string expected)
    {
        var (_, _, problem) = MemberIdInput.Validate(email, id);
        Assert.NotNull(problem);
        Assert.Contains(expected, problem);
        Assert.DoesNotContain("Ensure this field", problem);
    }

    [Theory]
    [InlineData("member_id", "This member id is not valid. Ask your admin.", "That Member ID isn't right")]
    [InlineData("email", "This email does not match the member id.", "doesn't match this Member ID")]
    [InlineData("non_field_errors", "This member is no longer active.", "doesn't work any more")]
    [InlineData("member_id", "Ensure this field has no more than 16 characters.", "It looks like ZM-XXXX-XXXX")]
    public void Refusals_are_worded_for_the_employee(string field, string server, string expected)
    {
        var message = EnrollmentMessages.ForRefusal(field, server);
        Assert.Contains(expected, message);
        Assert.DoesNotContain("member id", message); // always "Member ID"
    }

    [Fact]
    public void A_specific_refusal_like_the_pc_limit_is_kept()
    {
        const string limit = "Your company's plan covers 3 PCs and they're all in use. Ask your admin to upgrade the plan or remove a PC.";
        Assert.Equal(limit, EnrollmentMessages.ForRefusal("non_field_errors", limit));
    }
}

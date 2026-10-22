using Zinwe.Agent.Tray.Widget;

namespace Zinwe.Agent.Tray.Tests;

public sealed class ChatLinksTests
{
    private static List<ChatLinkToken> Links(string text) =>
        ChatLinks.Tokens(text).Where(t => t.Kind != ChatLinkKind.Text).ToList();

    [Fact]
    public void Finds_phone_numbers_with_or_without_country_code()
    {
        var links = Links("Call me on +91 98100 00000 or 98100-00001.");
        Assert.Equal([ChatLinkKind.Phone, ChatLinkKind.Phone], links.Select(l => l.Kind));
        Assert.Equal("+919810000000", links[0].Target);
        Assert.Equal("9810000001", links[1].Target);
    }

    [Fact]
    public void Finds_emails_and_links_without_the_sentence_full_stop()
    {
        var links = Links("Mail asha@acme.in, or see https://zinwe.io/plans. Also www.example.com)");
        Assert.Equal(("mailto:asha@acme.in", ChatLinkKind.Email), (links[0].Target, links[0].Kind));
        Assert.Equal("https://zinwe.io/plans", links[1].Text);
        Assert.Equal("https://www.example.com", links[2].Target);
    }

    [Fact]
    public void Leaves_short_numbers_dates_and_other_schemes_alone()
    {
        Assert.Empty(Links("OTP 123456, ₹1,799 due 22-10-2026, ID ZW-12345678, javascript:alert(1), file://c:/x"));
    }

    [Fact]
    public void Text_round_trips()
    {
        const string text = "Ping 9810000000 or mail a@b.co — thanks!";
        Assert.Equal(text, string.Concat(ChatLinks.Tokens(text).Select(t => t.Text)));
    }
}

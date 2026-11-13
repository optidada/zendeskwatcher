using ZendeskWatcher.UI;

namespace ZendeskWatcher.Tests;

/// <summary>The little markdown in notification texts: **bold**, and escaping text that comes from Zendesk.</summary>
public class MarkdownTests
{
    [Fact]
    public void Bold_parts()
        => Assert.Equal([new Markdown.Run("Jane", true), new Markdown.Run(" replied", false)], Markdown.Parse("**Jane** replied"));

    [Theory]
    [InlineData("a ** b")]          // no partner: shows as written
    [InlineData("2 * 3 = 6")]
    [InlineData(@"C:\temp\**x**")]
    [InlineData("**")]
    public void Escaped_text_shows_exactly_as_written(string text)
    {
        var runs = Markdown.Parse(Markdown.Escape(text));
        Assert.Equal(text, string.Concat(runs.Select(r => r.Text)));
        Assert.DoesNotContain(runs, r => r.Bold);
    }

    [Fact]
    public void Lonely_marker_stays_literal()
        => Assert.Equal([new Markdown.Run("Jane", true), new Markdown.Run(" said ** hi", false)], Markdown.Parse("**Jane** said ** hi"));

    [Fact]
    public void Html_and_plain_text()
    {
        Assert.Equal("<b>Jane &amp; Co</b> replied", Markdown.ToHtml("**Jane & Co** replied"));
        Assert.Equal("Jane & Co replied", Markdown.Strip("**Jane & Co** replied"));
    }

    [Fact]
    public void Truncate_keeps_valid_markdown()
    {
        Assert.Equal("**Jan**", Markdown.Truncate("**Jane** replied", 3));
        Assert.Equal("**Jane** r",Markdown.Truncate("**Jane** replied", 6));
        Assert.Equal(@"a\*", Markdown.Truncate(@"a\*b", 2));
    }

    [Fact]
    public void Copy_starts_with_the_client_and_keeps_bold_for_teams()
    {
        var content = new NotificationContent(NotificationKind.MyTicket, "Ticket updated", "#1 Broken", "**Jane** replied: hi", 1, "high")
            { Client = "Contoso", IsTicketNotification = true };

        var (html, text) = TicketShare.Format(content, null);

        Assert.Equal("<b>#1 Broken</b> · High (P2)<br>Client: <b>Contoso</b><br><b>Jane</b> replied: hi", html);
        Assert.Equal("#1 Broken (High (P2))\r\nClient: Contoso\r\nJane replied: hi", text);
    }

    [Fact]
    public void Heading_has_kind_priority_and_group()
    {
        var content = new NotificationContent(NotificationKind.NewTicket, "New ticket", "#1", Priority: "high", Context: "Support Tier 2");
        Assert.Equal("New ticket · P2 · Support Tier 2", content.FullHeading);
        Assert.Equal("New ticket", (content with { Priority = null, Context = null }).FullHeading);
    }
}

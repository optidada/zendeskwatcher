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
        Assert.Equal("#1 Broken · High (P2)\r\nClient: Contoso\r\nJane replied: hi", text);
    }

    [Fact]
    public void Heading_has_kind_priority_and_group()
    {
        var content = new NotificationContent(NotificationKind.NewTicket, "New ticket", "#1", Priority: "high", Context: "Support Tier 2");
        Assert.Equal("New ticket · P2 · Support Tier 2", content.FullHeading);
        Assert.Equal("New ticket", (content with { Priority = null, Context = null }).FullHeading);
    }

    [Fact]
    public void Priority_tags_parse_as_tags_and_strip_to_plain_text()
    {
        var text = $"**Set as {Markdown.Tag("normal")} · looks like {Markdown.Tag("P1")}:** site down";

        Assert.Equal("Set as P3 · looks like P1: site down", Markdown.Strip(text));
        Assert.Equal(["P3", "P1"], Markdown.Parse(text).Where(r => r.IsTag).Select(r => r.Text));
        Assert.Equal(Markdown.Tag("urgent"), Markdown.Tag("P1"));
        Assert.Equal("custom", Markdown.Tag("custom")); // unknown: plain text
        Assert.Equal("", Markdown.Tag(null));
    }

    [Fact]
    public void Text_from_Zendesk_with_the_tag_characters_never_becomes_a_tag()
    {
        var sneaky = "look " + Markdown.Tag("urgent") + " here \uE000P2\uE001 and \uE001\uE000";

        var runs = Markdown.Parse("**Jane** wrote: " + Markdown.Escape(sneaky));

        Assert.DoesNotContain(runs, r => r.IsTag);
        Assert.Equal("Jane wrote: " + sneaky, Markdown.Strip("**Jane** wrote: " + Markdown.Escape(sneaky)));
    }

    [Fact]
    public void Truncate_shows_a_tag_only_when_whole()
    {
        var text = $"Open {Markdown.Tag("P1")} now";

        Assert.Equal("Open P", Markdown.Strip(Markdown.Truncate(text, 6)));
        Assert.DoesNotContain(Markdown.Parse(Markdown.Truncate(text, 6)), r => r.IsTag);
        Assert.Contains(Markdown.Parse(Markdown.Truncate(text, 7)), r => r.IsTag && r.Text == "P1");
    }
}

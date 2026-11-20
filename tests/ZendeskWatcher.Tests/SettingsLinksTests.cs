using ZendeskWatcher.UI;

namespace ZendeskWatcher.Tests;

/// <summary>References to other Settings pages in texts become links (and nothing else does).</summary>
public class SettingsLinksTests
{
    [Theory]
    [InlineData("Notifications → Working hours", "Notifications", "Working hours")]
    [InlineData("Notifications → Behavior → Snooze", "Behavior", "Snooze")]
    [InlineData("Groups → Clients", "Clients", null)]
    [InlineData("Groups → Products", "Products", null)]
    [InlineData("Groups page", "Groups", null)]
    [InlineData("Look & sound", "Look & sound", null)]
    public void A_path_through_the_sidebar_goes_to_its_page(string reference, string page, string? section) =>
        Assert.Equal((page, section), SettingsLinks.Resolve(reference));

    [Theory]
    [InlineData("New → Open")]                 // statuses, not pages
    [InlineData("e.g. In progress → Escalated")]
    [InlineData("the ticket's current priority; none counts as Normal")]
    public void Other_arrows_and_brackets_stay_text(string reference) => Assert.Null(SettingsLinks.Resolve(reference));

    [Fact]
    public void The_products_page_in_a_sentence_is_a_link() =>
        Assert.All([SettingsPages.NotificationsPage.ProductsHint(0), SettingsPages.NotificationsPage.ProductsHint(3)],
            hint => Assert.Equal("Products", Assert.Single(SettingsLinks.Parse(hint).Links).Page));

    [Fact]
    public void Brackets_sentences_and_written_out_links()
    {
        var (text, links) = SettingsLinks.Parse("Fewer priorities (Notifications → Ticket changes) give fewer. Tick groups on the Groups page. " +
                                                "[[Set my hours|Notifications/Working hours]]");

        Assert.Equal("Fewer priorities (Notifications → Ticket changes) give fewer. Tick groups on the Groups page. Set my hours", text);
        Assert.Equal(
            [("Notifications → Ticket changes", "Notifications", "Ticket changes"), ("Groups page", "Groups", null), ("Set my hours", "Notifications", "Working hours")],
            links.Select(l => (text.Substring(l.Start, l.Length), l.Page, l.Section)));
    }

    [Fact]
    public void A_text_without_references_has_no_links() =>
        Assert.False(SettingsLinks.HasLinks("Status changes leave out New → Open, Solved → Closed (the reply says it)."));
}

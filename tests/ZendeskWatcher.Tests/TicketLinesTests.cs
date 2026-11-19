using ZendeskWatcher.UI;
using ZendeskWatcher.UI.Popups;

namespace ZendeskWatcher.Tests;

/// <summary>In a notification's text, "#123456" opens that ticket and "Settings → Groups" opens that Settings page.</summary>
public class TicketLinesTests
{
    [Fact]
    public void Each_ticket_number_is_a_link_with_where_it_is()
    {
        var rich = new RichText(Theme.Body, Theme.BodyBold);
        var body = "#101 Search broken\n#102 Images missing · **Scheduled**\n… and 3 more: click to see them all";

        var links = rich.Links(body, new Rectangle(10, 20, 400, 300), maxLines: 6);

        Assert.Equal([101L, 102L], links.Select(l => l.Link.TicketId!.Value));
        Assert.All(links, l => Assert.Equal(NotificationActions.OpenTicket, l.Link.Action));
        Assert.True(links[0].Bounds.Bottom <= links[1].Bounds.Top);  // one under the other
        Assert.All(links, l => Assert.Equal(10, l.Bounds.Left));      // from the start of the line
        Assert.True(links[0].Bounds.Width < 80);                      // only the number, not the subject
    }

    [Fact]
    public void Ticket_numbers_inside_a_sentence_are_links_too()
    {
        var links = new RichText(Theme.Body, Theme.BodyBold).Links("**Jane** replied: see #101 too", new Rectangle(0, 0, 400, 100), 4);
        Assert.Equal(101L, Assert.Single(links).Link.TicketId);
    }

    [Fact]
    public void Settings_references_link_to_their_page()
    {
        var parts = RichText.SplitLinks("No groups watched yet: Settings → Groups. Or Settings → Groups → Clients").ToList();

        Assert.Equal(["settings:Groups", "settings:Clients"], parts.Where(p => p.Link is not null).Select(p => p.Link!.Action));
        Assert.Equal("Settings → Groups", parts.First(p => p.Link is not null).Text);
    }

    [Fact]
    public void Not_links_hash_tags_short_numbers_and_unknown_pages()
    {
        Assert.All(RichText.SplitLinks("Order #12, C#1234, Settings → Nowhere, 2+3 more"), p => Assert.Null(p.Link));
    }

    [Fact]
    public void Catch_up_more_opens_all_your_tickets_to_work_on_in_Zendesk()
    {
        var parts = RichText.SplitLinks("#101 Login fails\n+9 more").ToList();

        var more = Assert.Single(parts, p => p.Text == "+9 more");
        Assert.Equal(NotificationActions.Search(Tracking.TicketRules.WorkingOnSearch), more.Link!.Action);
    }
}

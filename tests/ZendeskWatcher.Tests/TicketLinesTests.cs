using ZendeskWatcher.UI;
using ZendeskWatcher.UI.Popups;

namespace ZendeskWatcher.Tests;

/// <summary>In a list notification, each "#123456 Subject" line can be clicked to open that ticket.</summary>
public class TicketLinesTests
{
    [Fact]
    public void Each_ticket_line_is_found_with_where_it_is()
    {
        var rich = new RichText(Theme.Body, Theme.BodyBold);
        var body = "#101 Search broken\n#102 Images missing · **Scheduled**\n… and 3 more: click to see them all";

        var lines = rich.TicketLines(body, new Rectangle(10, 20, 400, 300), maxLines: 6);

        Assert.Equal([101L, 102L], lines.Select(l => l.TicketId));
        Assert.True(lines[0].Bounds.Bottom <= lines[1].Bounds.Top);  // one under the other
        Assert.All(lines, l => Assert.Equal(10, l.Bounds.Left));      // from the start of the line
    }

    [Fact]
    public void Text_that_isnt_a_ticket_list_has_no_lines() =>
        Assert.Empty(new RichText(Theme.Body, Theme.BodyBold).TicketLines("**Jane** replied: see #101 too", new Rectangle(0, 0, 400, 100), 4));
}

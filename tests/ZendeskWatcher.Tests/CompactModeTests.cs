using ZendeskWatcher.UI.Popups;

namespace ZendeskWatcher.Tests;

/// <summary>The pill, ticker and achievement ways notifications can appear (Look &amp; sound → How they appear).</summary>
public class CompactModeTests
{
    private static NotificationContent Ticket(NotificationKind kind, long id, string? priority = null, string heading = "New ticket") =>
        new(kind, heading, $"#{id} Customer cannot log in", "", TicketId: id, Priority: priority);

    [Fact]
    public void Pill_shows_one_notification_as_heading_priority_and_number()
    {
        Assert.Equal("New ticket · P2 · #123456", CompactText.PillSummary([Ticket(NotificationKind.NewTicket, 123456, "high")]));
        Assert.Equal("Ticket updated · #7", CompactText.PillSummary([Ticket(NotificationKind.MyTicket, 7, heading: "Ticket updated")]));
    }

    [Fact]
    public void Pill_shows_an_app_message_by_its_heading()
    {
        var update = new NotificationContent(NotificationKind.MyTicket, "Update ready", "Zendesk Watcher 2.0 is ready");
        Assert.Equal("Update ready", CompactText.PillSummary([update]));
        Assert.Equal("Zendesk Watcher 2.0 is ready", CompactText.PillSummary([update with { Heading = "" }]));
    }

    [Fact]
    public void Pill_counts_several_by_kind_and_urgent_ones()
    {
        var items = new[]
        {
            Ticket(NotificationKind.NewTicket, 1, "urgent"), Ticket(NotificationKind.NewTicket, 2, "low"),
            Ticket(NotificationKind.MyTicket, 3), Ticket(NotificationKind.HelpColleague, 4),
            new NotificationContent(NotificationKind.MyTicket, "Update ready", "Zendesk Watcher 2.0 is ready")
        };
        Assert.Equal("2 new · 1 update · 1 waiting · 1 message · 1 P1", CompactText.PillSummary(items));
        Assert.Equal("2 new", CompactText.PillSummary(items[..2].Select(i => i with { Priority = null }).ToList()));
        Assert.Equal("", CompactText.PillSummary([]));
    }

    [Fact]
    public void Ticker_item_is_heading_title_and_client_on_one_line()
    {
        var content = Ticket(NotificationKind.NewTicket, 123456, "high") with { Client = "Contoso · Premium", ClientName = "Contoso" };
        Assert.Equal("New ticket · #123456 Customer cannot log in · Contoso", CompactText.TickerItem(content));
        var multiline = new NotificationContent(NotificationKind.MyTicket, "Ticket updated", "#9 Two\nlines   here");
        Assert.Equal("Ticket updated · #9 Two lines here", CompactText.TickerItem(multiline));
    }

    [Fact]
    public void Ticker_item_shortens_a_long_subject()
    {
        var content = new NotificationContent(NotificationKind.NewTicket, "New ticket", "#1 " + new string('x', 200));
        var text = CompactText.TickerItem(content);
        Assert.EndsWith("…", text);
        Assert.True(text.Length < 100);
    }

    [Theory]
    [InlineData(PopupPosition.BottomRight, false)]
    [InlineData(PopupPosition.TopRight, true)]
    [InlineData(PopupPosition.TopCenter, true)]
    [InlineData(PopupPosition.Center, false)]
    [InlineData(PopupPosition.LeftCenter, false)]
    public void Ticker_follows_top_or_bottom_of_the_position(PopupPosition position, bool top) =>
        Assert.Equal(top, TickerBar.AtTop(new Settings { Position = position, Stacking = PopupStacking.Ticker }));

    [Fact]
    public void Pill_and_achievement_have_a_fixed_place()
    {
        Assert.Equal(PopupPosition.TopCenter, PopupManager.EffectivePosition(new Settings { Position = PopupPosition.BottomRight, Stacking = PopupStacking.Pill }));
        Assert.Equal(PopupPosition.BottomCenter, PopupManager.EffectivePosition(new Settings { Position = PopupPosition.TopRight, Stacking = PopupStacking.Achievement }));
        Assert.Equal(PopupForm.PeekEdge.None, PopupManager.PeekEdgeFor(new Settings { Stacking = PopupStacking.Pill }));
    }
}

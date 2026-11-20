using ZendeskWatcher.UI.Popups;

namespace ZendeskWatcher.Tests;

/// <summary>Peek (Look & sound → How they appear): which screen edge the notifications wait at.</summary>
public class PeekTests
{
    [Theory]
    [InlineData(PopupPosition.BottomRight, PopupForm.PeekEdge.Right)]
    [InlineData(PopupPosition.TopRight, PopupForm.PeekEdge.Right)]
    [InlineData(PopupPosition.LeftCenter, PopupForm.PeekEdge.Left)]
    [InlineData(PopupPosition.TopLeft, PopupForm.PeekEdge.Left)]
    [InlineData(PopupPosition.BottomLeft, PopupForm.PeekEdge.Left)]
    [InlineData(PopupPosition.TopCenter, PopupForm.PeekEdge.Top)]
    [InlineData(PopupPosition.RightCenter, PopupForm.PeekEdge.Right)]
    [InlineData(PopupPosition.BottomCenter, PopupForm.PeekEdge.Bottom)]
    [InlineData(PopupPosition.Center, PopupForm.PeekEdge.Top)] // no edge in the middle: Top center
    public void Peek_uses_the_edge_of_the_position(PopupPosition position, PopupForm.PeekEdge edge)
    {
        var settings = new Settings { Position = position, Stacking = PopupStacking.Peek };
        Assert.Equal(edge, PopupManager.PeekEdgeFor(settings));
    }

    [Fact]
    public void Center_moves_to_top_center_only_with_peek()
    {
        Assert.Equal(PopupPosition.TopCenter, PopupManager.EffectivePosition(new Settings { Position = PopupPosition.Center, Stacking = PopupStacking.Peek }));
        Assert.Equal(PopupPosition.Center, PopupManager.EffectivePosition(new Settings { Position = PopupPosition.Center, Stacking = PopupStacking.Deck }));
        Assert.Equal(PopupForm.PeekEdge.None, PopupManager.PeekEdgeFor(new Settings { Stacking = PopupStacking.List }));
    }

    [Theory]
    [InlineData("urgent", false, true, NotificationActions.OpenTicket, true)]   // urgent ticket
    [InlineData("high", true, true, NotificationActions.OpenTicket, true)]      // important client
    [InlineData("high", false, true, NotificationActions.OpenTicket, false)]    // waits as a tab
    [InlineData(null, false, false, NotificationActions.SignIn, true)]          // an app message: sign-in required
    [InlineData(null, false, false, NotificationActions.OpenZendesk, true)]     // the welcome summary
    [InlineData("high", false, false, NotificationActions.None, false)]         // a "Try it" sample: like the ticket it shows
    public void What_slides_out_by_itself(string? priority, bool important, bool ticket, string action, bool comesOut)
    {
        var content = new UI.Popups.NotificationContent(NotificationKind.MyTicket, "Heading", "Title", Priority: priority)
            { Important = important, IsTicketNotification = ticket, Action = action };
        Assert.Equal(comesOut, UI.Popups.PopupForm.ComesOutByItself(content));
    }
}

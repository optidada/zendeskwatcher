using ZendeskWatcher.UI.Popups;

namespace ZendeskWatcher.Tests;

/// <summary>Peek (Look & sound → Several at once): which screen edge the notifications wait at.</summary>
public class PeekTests
{
    [Theory]
    [InlineData(PopupPosition.BottomRight, PopupForm.PeekEdge.Right)]
    [InlineData(PopupPosition.TopRight, PopupForm.PeekEdge.Right)]
    [InlineData(PopupPosition.LeftCenter, PopupForm.PeekEdge.Left)]
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
}

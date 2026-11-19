using ZendeskWatcher.UI.Popups;

namespace ZendeskWatcher.Tests;

public class NotificationScreenTests
{
    private static readonly string[] Two = [@"\\.\DISPLAY1", @"\\.\DISPLAY2"];

    [Fact]
    public void Notifications_stay_on_the_monitor_they_started_on_while_it_is_connected()
    {
        Assert.Equal(@"\\.\DISPLAY2", NotificationScreen.Resolve(Two, held: @"\\.\DISPLAY2", primary: @"\\.\DISPLAY1"));
    }

    [Fact]
    public void A_menu_sticking_out_onto_the_next_monitor_is_moved_back_wholly_onto_this_one()
    {
        var laptop = new Rectangle(-1920, 0, 1920, 1040); // left of the main monitor
        Assert.Equal(new Point(-220, 300), NotificationScreen.Clamp(new Rectangle(-100, 300, 220, 180), laptop)); // was over the edge on the right
        Assert.Equal(new Point(-1920, 860), NotificationScreen.Clamp(new Rectangle(-2000, 950, 220, 180), laptop)); // left and below
        Assert.Equal(new Point(-500, 300), NotificationScreen.Clamp(new Rectangle(-500, 300, 220, 180), laptop)); // fits: stays
    }

    [Fact]
    public void An_unplugged_monitor_sends_them_to_the_primary_one_and_before_anything_showed_too()
    {
        Assert.Equal(@"\\.\DISPLAY1", NotificationScreen.Resolve([@"\\.\DISPLAY1"], held: @"\\.\DISPLAY2", primary: @"\\.\DISPLAY1"));
        Assert.Equal(@"\\.\DISPLAY1", NotificationScreen.Resolve(Two, held: null, primary: @"\\.\DISPLAY1"));
    }
}

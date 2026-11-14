using ZendeskWatcher.UI;

namespace ZendeskWatcher.Tests;

/// <summary>The working-hours bar: whole hours, the ends at least an hour apart.</summary>
public class HourRangeTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(100, 8)]     // a third of the bar: 08:00
    [InlineData(-20, 0)]     // left of the bar
    [InlineData(400, 24)]    // right of the bar
    [InlineData(107, 9)]     // 8.56 rounds to 9
    public void A_point_on_the_bar_is_a_whole_hour(float x, int hour) => Assert.Equal(hour, HourRange.HourAt(x, 0, 300));

    [Fact]
    public void The_ends_stay_in_the_day_and_an_hour_apart()
    {
        using var range = new HourRange();
        range.SetRange(9, 18);
        Assert.Equal((9, 18), (range.Start, range.End));
        range.SetRange(-3, 30);
        Assert.Equal((0, 24), (range.Start, range.End));
        range.SetRange(12, 12);
        Assert.Equal((12, 13), (range.Start, range.End));
        range.End = 5; // before the start: the start moves along
        Assert.Equal((4, 5), (range.Start, range.End));
    }
}

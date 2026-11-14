using System.Globalization;
using System.Runtime.CompilerServices;

namespace ZendeskWatcher.Tests;

/// <summary>Times like this PC's clock: 24-hour or 12-hour with AM/PM.</summary>
public class ClockTests
{
    /// <summary>Every test sees the 24-hour clock, whatever the PC (or the build server) uses.</summary>
    [ModuleInitializer]
    internal static void TwentyFourHours() => Clock.TwelveHour = false;

    [Fact]
    public void Follows_the_windows_time_format()
    {
        Assert.True(Clock.UsesTwelveHour(CultureInfo.GetCultureInfo("en-US")));
        Assert.False(Clock.UsesTwelveHour(CultureInfo.GetCultureInfo("sv-SE")));
        Assert.False(Clock.UsesTwelveHour(CultureInfo.GetCultureInfo("en-GB")));
    }

    [Fact]
    public void Twelve_hours_with_am_and_pm()
    {
        try
        {
            Clock.TwelveHour = true;
            var friday = new DateTime(2026, 11, 13, 17, 5, 0);
            Assert.Equal("5:05 PM", Clock.Time(friday));
            Assert.Equal("Fri 5:05 PM", Clock.DayTime(friday));
            Assert.Equal("8:00 AM", Clock.Hour(8));
            Assert.Equal("12:00 AM", Clock.Hour(24));
            Assert.Equal(["12a", "6a", "12p", "6p", "12a"], new[] { 0, 6, 12, 18, 24 }.Select(Clock.HourMark));
        }
        finally
        {
            Clock.TwelveHour = false;
        }
        Assert.Equal("17:05", Clock.Time(new DateTime(2026, 11, 13, 17, 5, 0)));
    }
}

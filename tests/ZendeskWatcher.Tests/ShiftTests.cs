using ZendeskWatcher.Tracking;

namespace ZendeskWatcher.Tests;

/// <summary>"Mute until my shift ends" and "until my next shift", from your working hours (08-17, Monday to Friday here).</summary>
public class ShiftTests
{
    private static readonly DateTime Tuesday10 = new(2026, 11, 17, 10, 0, 0);

    [Fact]
    public void In_your_shift_it_ends_at_your_end_hour()
    {
        Assert.Equal(new DateTime(2026, 11, 17, 17, 0, 0), TicketRules.ShiftEnd(Tuesday10, 8, 17));
    }

    [Fact]
    public void Outside_your_shift_there_is_no_end_to_offer()
    {
        Assert.Null(TicketRules.ShiftEnd(Tuesday10.AddHours(9), 8, 17));            // Tuesday 19:00
        Assert.Null(TicketRules.ShiftEnd(new DateTime(2026, 11, 21, 10, 0, 0), 8, 17)); // Saturday
    }

    [Fact]
    public void The_next_shift_is_tomorrow_morning_while_you_work()
    {
        Assert.Equal(new DateTime(2026, 11, 18, 8, 0, 0), TicketRules.NextShiftStart(Tuesday10, 8));
    }

    [Fact]
    public void Before_your_shift_starts_the_next_one_is_today()
    {
        Assert.Equal(new DateTime(2026, 11, 17, 8, 0, 0), TicketRules.NextShiftStart(new DateTime(2026, 11, 17, 6, 30, 0), 8));
    }

    [Fact]
    public void On_Friday_evening_the_next_shift_is_Monday()
    {
        Assert.Equal(new DateTime(2026, 11, 23, 8, 0, 0), TicketRules.NextShiftStart(new DateTime(2026, 11, 20, 18, 0, 0), 8));
    }

    [Fact]
    public void Your_own_working_days_count()
    {
        DayOfWeek[] sundayToThursday = [DayOfWeek.Sunday, DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday];
        Assert.Equal(new DateTime(2026, 11, 22, 8, 0, 0), TicketRules.NextShiftStart(new DateTime(2026, 11, 20, 18, 0, 0), 8, sundayToThursday));
    }
}

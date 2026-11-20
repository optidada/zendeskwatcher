using ZendeskWatcher.Tracking;
using ZendeskWatcher.UI.Popups;

namespace ZendeskWatcher.Tests;

public class BusyRuleTests
{
    private static readonly DateTime Start = new(2026, 11, 20, 9, 0, 0, DateTimeKind.Utc);

    /// <summary>Feeds seconds; active when <paramref name="pattern"/> says so for that second.</summary>
    private static DateTime Feed(BusyRule rule, DateTime from, int seconds, Func<int, bool> pattern)
    {
        for (var i = 0; i < seconds; i++)
            rule.Add(pattern(i), from.AddSeconds(i));
        return from.AddSeconds(seconds);
    }

    [Fact]
    public void Typing_every_second_is_busy_after_about_two_minutes_more()
    {
        var rule = new BusyRule();
        var t = Feed(rule, Start, 165, _ => true);
        Assert.False(rule.Busy); // 50 s to reach 50 of 60, then 2 min
        Feed(rule, t, 10, _ => true);
        Assert.True(rule.Busy);
    }

    [Fact]
    public void Reading_with_pauses_is_not_busy()
    {
        var rule = new BusyRule();
        Feed(rule, Start, 600, i => i % 60 < 45); // 45 of every 60 s: semi-busy
        Assert.False(rule.Busy);
    }

    [Fact]
    public void Busy_ends_about_45_seconds_after_stopping()
    {
        var rule = new BusyRule();
        var t = Feed(rule, Start, 200, _ => true);
        Assert.True(rule.Busy);
        t = Feed(rule, t, 35, _ => false); // below 35 of 60 after 26 s, then 15 s calm
        Assert.True(rule.Busy);
        Feed(rule, t, 10, _ => false);
        Assert.False(rule.Busy);
    }

    [Fact]
    public void A_short_pause_does_not_end_it()
    {
        var rule = new BusyRule();
        var t = Feed(rule, Start, 200, _ => true);
        t = Feed(rule, t, 25, _ => false); // 35 of 60 left: still busy
        Feed(rule, t, 60, _ => true);
        Assert.True(rule.Busy);
    }

    [Fact]
    public void Slower_but_steady_work_keeps_it_going()
    {
        var rule = new BusyRule();
        var t = Feed(rule, Start, 200, _ => true);
        Feed(rule, t, 600, i => i % 60 < 38); // 38 of every 60 s
        Assert.True(rule.Busy);
    }

    [Fact]
    public void Only_non_urgent_ticket_notifications_wait()
    {
        NotificationContent Ticket(string priority) =>
            new(NotificationKind.NewTicket, "t", "b", TicketId: 1, Priority: priority) { IsTicketNotification = true };
        Assert.True(BusyRule.Holds(Ticket("high")));
        Assert.False(BusyRule.Holds(Ticket("urgent")));
        Assert.False(BusyRule.Holds(Ticket("low") with { Important = true })); // Important clients never wait
        var now = DateTimeOffset.UtcNow;
        Assert.False(BusyRule.Holds(Ticket("normal") with { SlaDue = now.AddMinutes(10) }, now)); // SLA due soon: never waits
        Assert.False(BusyRule.Holds(Ticket("normal") with { SlaDue = now.AddMinutes(-5) }, now)); // already breached
        Assert.True(BusyRule.Holds(Ticket("normal") with { SlaDue = now.AddHours(2) }, now));     // plenty of time: may wait
        Assert.False(BusyRule.Holds(Ticket("normal") with { IsSample = true }));
        Assert.False(BusyRule.Holds(new NotificationContent(NotificationKind.NewTicket, "app", "message")));
    }
}

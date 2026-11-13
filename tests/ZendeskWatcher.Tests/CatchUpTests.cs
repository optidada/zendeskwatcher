namespace ZendeskWatcher.Tests;

/// <summary>"Catch up": what still needs attention since you stopped working last time.</summary>
public class CatchUpTests
{
    private const long Customer = 500, Berlin = 20;

    // 2026-11-11 is a Wednesday.
    [Theory]
    [InlineData("2026-11-11 10:00", "2026-11-10 17:00")] // working: since yesterday's end
    [InlineData("2026-11-11 07:00", "2026-11-10 17:00")] // before work: the same
    [InlineData("2026-11-11 19:00", "2026-11-11 17:00")] // after work: since today's end
    [InlineData("2026-11-09 09:00", "2026-11-06 17:00")] // Monday: since Friday
    [InlineData("2026-11-14 12:00", "2026-11-13 17:00")] // Saturday: since Friday
    public void Catch_up_starts_when_you_stopped_working_last_time(string now, string expected) =>
        Assert.Equal(DateTime.Parse(expected), TicketRules.CatchUpStart(DateTime.Parse(now), 8, 17));

    [Theory]
    [InlineData("2026-11-11 19:00", "2026-11-11 17:00", "since 17:00")]
    [InlineData("2026-11-11 10:00", "2026-11-10 17:00", "since yesterday 17:00")]
    [InlineData("2026-11-09 09:00", "2026-11-06 17:00", "since Fri 17:00")]
    public void Since_is_said_in_words(string now, string since, string expected) =>
        Assert.Equal(expected, TicketRules.CatchUpSince(DateTime.Parse(since), DateTime.Parse(now)));

    [Fact]
    public void The_summary_has_a_line_per_kind_with_something()
    {
        var now = DateTime.Parse("2026-11-11 10:00");
        var since = DateTime.Parse("2026-11-10 17:00");
        Assert.Equal("**3 unassigned tickets** in your groups.\n" +
                     "**1 customer** wrote since yesterday 17:00 and is waiting for your reply.\n" +
                     "**2 customers** wrote since yesterday 17:00 and are waiting on off-work colleagues' tickets.",
            TicketRules.CatchUpText(since, now, 3, 1, 0, 2));
        Assert.Equal("**11 unassigned tickets** in your groups.\nNo customer is waiting for an answer since yesterday 17:00.",
            TicketRules.CatchUpText(since, now, 11, 0, 0, 0));
        Assert.Equal("No unassigned tickets in your groups.\nNo customer is waiting for an answer since yesterday 17:00.",
            TicketRules.CatchUpText(since, now, 0, 0, 0, 0));
    }

    [Fact]
    public async Task Runs_by_itself_once_per_working_day()
    {
        File.Delete(AppPaths.StateFile);
        var settings = new Settings { Subdomain = "test", MyWorkdayStartHour = 8, MyWorkdayEndHour = 17, MyWorkDays = [.. Enum.GetValues<DayOfWeek>()] };
        var watcher = new TicketWatcher(settings, new ZendeskClient(settings, new FakeZendesk()), new RecordingNotifier());
        await watcher.InitializeAsync(CancellationToken.None);
        var monday = DateTime.Parse("2026-11-09 09:00");

        Assert.False(watcher.DayStartCatchUpDue(monday));               // the very first time: only remembers
        Assert.False(watcher.DayStartCatchUpDue(monday.AddHours(1)));   // already caught up today
        Assert.False(watcher.DayStartCatchUpDue(monday.AddHours(13)));  // 22:00: not working
        Assert.True(watcher.DayStartCatchUpDue(monday.AddDays(1)));     // next morning: due

        settings.CatchUpAtDayStart = false;
        Assert.False(watcher.DayStartCatchUpDue(monday.AddDays(1)));    // switched off
    }

    [Fact]
    public async Task Many_unassigned_go_in_the_summary_and_waiting_customers_still_come_one_by_one()
    {
        File.Delete(AppPaths.StateFile);
        var zendesk = new FakeZendesk();
        var notifier = new RecordingNotifier();
        var settings = new Settings { Subdomain = "test", WatchedGroups = [new WatchedGroup { Id = 10, Name = "Tier 2" }], HelpColleagues = false };
        zendesk.Users[Customer] = new User { Id = Customer, Name = "Sam Customer", Role = "end-user" };
        foreach (var id in new long[] { 101, 102, 103, 104 })
            zendesk.AddUnassigned(id);
        zendesk.AddAssigned(201, "normal");

        var watcher = new TicketWatcher(settings, new ZendeskClient(settings, zendesk), notifier);
        await watcher.InitializeAsync(CancellationToken.None);
        await watcher.PollAsync(CancellationToken.None);
        zendesk.Reply(201, Customer, "Any news?");
        await watcher.PollAsync(CancellationToken.None);
        notifier.Shown.Clear();

        await watcher.CatchUpAsync(CancellationToken.None, since: DateTime.Now.AddDays(-1));

        Assert.Equal(2, notifier.Shown.Count);
        Assert.Equal("5 things need attention", notifier.Shown[0].Title); // the summary counts everything
        Assert.StartsWith("**4 unassigned tickets** in your groups.", notifier.Shown[0].Body);
        Assert.Equal(201, notifier.Shown[1].TicketId);                    // the waiting customer on its own
    }

    [Fact]
    public async Task Finds_unassigned_tickets_and_customers_still_waiting()
    {
        File.Delete(AppPaths.StateFile);
        var zendesk = new FakeZendesk();
        var notifier = new RecordingNotifier();
        var settings = new Settings
        {
            Subdomain = "test",
            WatchedGroups = [new WatchedGroup { Id = 10, Name = "Tier 2" }],
            HelpColleagues = true,
            HelpRegionDefaultDone = true,
            HelpPriorities = ["urgent", "high", "normal", "low"],
            WorkDays = [] // colleagues are off work right now
        };
        zendesk.Users[Customer] = new User { Id = Customer, Name = "Sam Customer", Role = "end-user" };
        zendesk.Users[Berlin] = new User { Id = Berlin, Name = "Berit Berlin", Role = "agent", IanaTimeZone = "Europe/Berlin" };
        zendesk.AddUnassigned(101);
        zendesk.AddAssigned(201, "normal", subject: "Waiting for me");
        zendesk.AddAssigned(202, "normal", subject: "Already answered");
        zendesk.AddAssigned(301, "high", assignee: Berlin, subject: "Waiting for Berit");

        var watcher = new TicketWatcher(settings, new ZendeskClient(settings, zendesk), notifier);
        await watcher.InitializeAsync(CancellationToken.None);
        await watcher.PollAsync(CancellationToken.None); // first look: records what's there
        zendesk.Reply(201, Customer, "Any news?");
        zendesk.Reply(202, Customer, "Any news?");
        zendesk.Reply(202, zendesk.Me.Id, "Yes, fixed.");
        zendesk.Reply(301, Customer, "Hello?");
        await watcher.PollAsync(CancellationToken.None);
        notifier.Shown.Clear();

        var result = await watcher.CatchUpAsync(CancellationToken.None, since: DateTime.Now.AddDays(-1));

        Assert.Equal([101L], result.Unassigned.Select(n => n.TicketId));
        Assert.Equal([201L], result.Mine.Select(n => n.TicketId));   // 202 was answered after the customer wrote
        Assert.Equal([301L], result.Colleagues.Select(n => n.TicketId));
        Assert.Equal(3, notifier.Shown.Count);                         // a few: one notification each
        Assert.All(notifier.Shown, n => Assert.True(n.RequestedByUser)); // shown even while paused
        Assert.True(result.Unassigned[0].CanTake);
    }
}

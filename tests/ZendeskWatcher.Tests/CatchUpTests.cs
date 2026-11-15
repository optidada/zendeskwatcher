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
    public void The_text_is_a_line_per_thing_number_first_most_urgent_first()
    {
        var due = DateTime.Parse("2026-11-11 10:30");
        Assert.Equal("**1** waiting for your reply\n" +
                     $"**2** past SLA · **3** due today, first {Clock.Time(due)}\n" +
                     "**4** unassigned in your groups · 1 Scheduled\n" +
                     "**2** waiting on colleagues' tickets\n" +
                     "**1** Escalated, no reply to the customer for 2+ days\n" +
                     "**5** Pending, no update for 7+ days\n" +
                     "\nYour tickets: 3 Open",
            TicketRules.CatchUpBody(new TicketRules.CatchUpCounts
            {
                Mine = 1, SlaPast = 2, SlaToday = 3, FirstDueToday = due, Unassigned = 4, Scheduled = 1, Colleagues = 2,
                QuietEscalated = 1, QuietPending = 5
            }, "Your tickets: 3 Open"));
        // Only what's there: nothing about zeros, and a clear word when nothing needs you.
        Assert.Equal($"**2** SLA due today, first {Clock.Time(due)}", TicketRules.CatchUpBody(new() { SlaToday = 2, FirstDueToday = due }, null));
        Assert.Equal("Nothing needs you right now", TicketRules.CatchUpBody(new(), null));
        Assert.StartsWith("No groups watched yet", TicketRules.CatchUpBody(new() { NoGroups = true }, null));
    }

    [Fact]
    public void Working_on_lists_open_tickets_most_urgent_first()
    {
        var now = DateTimeOffset.UtcNow;
        TicketRules.WorkItem T(long id, string status, string name, string? priority, double hoursAgo, DateTimeOffset? sla = null) =>
            new(id, $"Ticket {id}", name, status, priority, sla, now.AddHours(-hoursAgo));

        Assert.Null(TicketRules.WorkingOnText([T(1, "pending", "Pending", "urgent", 1), T(2, "open", "Escalated to Engineering", "high", 1)]));

        var text = TicketRules.WorkingOnText([
            T(10, "open", "Open", "normal", 1),
            T(11, "open", "Open", "normal", 5),             // untouched longer: before 10
            T(12, "open", "In Progress", "urgent", 1),      // P1 first
            T(13, "pending", "Pending", "urgent", 1),       // waits on the customer: not listed
            T(14, "new", "New", "normal", 1, now.AddHours(1)), // SLA soon: first among the normal ones
            T(15, "open", "Open", "high", 1),
            T(16, "open", "Open", "low", 9),
            T(17, "open", "Open", "low", 1)]);
        Assert.Equal("To work on:\n" +
                     $"#12 {ZendeskWatcher.UI.Markdown.Tag("urgent")} Ticket 12\n" +
                     $"#15 {ZendeskWatcher.UI.Markdown.Tag("high")} Ticket 15\n" +
                     "#14 Ticket 14\n#11 Ticket 11\n#10 Ticket 10\n+2 more", text);
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
        Assert.StartsWith("Catch up · since ", notifier.Shown[0].Heading);
        Assert.Contains("**4** unassigned in your groups", notifier.Shown[0].Body);     // the greeting counts everything
        Assert.Contains("**1** waiting for your reply", notifier.Shown[0].Body);
        Assert.Equal(201, notifier.Shown[1].TicketId);                    // the waiting customer on its own
    }

    [Fact]
    public async Task The_greeting_lists_your_tickets_by_status_and_quiet_escalated_ones()
    {
        File.Delete(AppPaths.StateFile);
        var zendesk = new FakeZendesk();
        var notifier = new RecordingNotifier();
        var settings = new Settings { Subdomain = "test", WatchedGroups = [new WatchedGroup { Id = 10, Name = "Tier 2" }], HelpColleagues = false };
        zendesk.Users[Customer] = new User { Id = Customer, Name = "Sam Customer", Role = "end-user" };
        zendesk.CustomStatuses.Add(new CustomStatus { Id = 7, AgentLabel = "Escalated", StatusCategory = "hold" });
        zendesk.AddAssigned(201, "urgent");
        zendesk.AddAssigned(202, "normal").CustomStatusId = 7;   // nobody wrote to the customer: quiet
        zendesk.AddAssigned(203, "normal").CustomStatusId = 7;
        zendesk.AddAssigned(301, "normal").Slas = new TicketSlas   // its SLA ran out an hour ago
        {
            PolicyMetrics = [new SlaMetric { Metric = "next_reply_time", Stage = "active", BreachAt = DateTimeOffset.UtcNow.AddHours(-1) }]
        };
        var stale = zendesk.AddAssigned(302, "normal");           // Pending, untouched for 10 days
        stale.Status = "pending";
        stale.UpdatedAt = DateTimeOffset.UtcNow.AddDays(-10);

        var watcher = new TicketWatcher(settings, new ZendeskClient(settings, zendesk), notifier);
        await watcher.InitializeAsync(CancellationToken.None);
        await watcher.PollAsync(CancellationToken.None);
        zendesk.Reply(201, Customer, "Any news?");
        zendesk.Reply(203, zendesk.Me.Id, "Engineering is on it.");   // the customer had an update
        await watcher.PollAsync(CancellationToken.None);
        notifier.Shown.Clear();

        // At app start, the first time ever: just the greeting, nothing one by one.
        await watcher.CatchUpAsync(CancellationToken.None, since: DateTime.Now.AddDays(-1), reason: CatchUpReason.AppStart);

        var greeting = Assert.Single(notifier.Shown);
        Assert.StartsWith("Catch up · since ", greeting.Heading);
        var lines = greeting.Body!.Replace('\u00a0', ' ').Split('\n');
        // The priorities are tags: as plain text "P1".
        Assert.Contains("Your tickets: 2 Open (1 P1) · 2 Escalated · 1 Pending", lines.Select(l => ZendeskWatcher.UI.Markdown.Strip(l)));
        Assert.DoesNotContain("Your tickets: 2 Open (1 P1) · 2 Escalated · 1 Pending", lines);
        // The waiting customer, the past SLA, the quiet escalated and the quiet pending one; your tickets, then what to work on.
        Assert.Contains("**1** waiting for your reply", lines);
        Assert.Contains("**1** past SLA", lines);
        Assert.Contains("**1** Escalated, no reply to the customer for 2+ days", lines);
        Assert.Contains("**1** Pending, no update for 7+ days", lines);
        var work = Array.IndexOf(lines, "To work on:");
        Assert.True(work > 0);
        Assert.StartsWith("Your tickets:", lines[work - 1]);
        Assert.All(lines[(work + 1)..], l => Assert.StartsWith("#", l));
        Assert.False(greeting.RequestedByUser);
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
        Assert.Equal(4, notifier.Shown.Count);                         // the greeting, then a few one by one
        Assert.All(notifier.Shown, n => Assert.True(n.RequestedByUser)); // shown even while paused
        Assert.True(result.Unassigned[0].CanTake);
    }
}

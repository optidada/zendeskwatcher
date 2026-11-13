namespace ZendeskWatcher.Tests;

/// <summary>
/// "Help colleagues" end to end: a customer replies on a colleague's ticket while the colleague is off work.
/// Until the Colleagues list has unticked your own region once, the app skips that region by itself.
/// </summary>
public class HelpColleaguesTests
{
    private const long Customer = 500, Berlin = 20, NewYork = 21;

    private readonly FakeZendesk _zendesk = new();
    private readonly RecordingNotifier _notifier = new();
    private readonly Settings _settings = new()
    {
        Subdomain = "test",
        WatchedGroups = [new WatchedGroup { Id = 10, Name = "Tier 2" }],
        TrackMyTickets = false,
        NotifyUnassigned = false,
        HelpColleagues = true,
        HelpOnlyDuringMyHours = false,
        WorkDays = [] // no working days: every colleague is off work right now
    };

    public HelpColleaguesTests()
    {
        File.Delete(AppPaths.StateFile);
        _zendesk.Me.IanaTimeZone = "Europe/Stockholm"; // my region: Europe & Africa
        _zendesk.Users[Customer] = new User { Id = Customer, Name = "Sam Customer", Role = "end-user" };
        _zendesk.Users[Berlin] = new User { Id = Berlin, Name = "Berit Berlin", Role = "agent", IanaTimeZone = "Europe/Berlin" };
        _zendesk.Users[NewYork] = new User { Id = NewYork, Name = "Nia NewYork", Role = "agent", IanaTimeZone = "America/New_York" };
        _zendesk.AddAssigned(301, "urgent", assignee: Berlin);
        _zendesk.AddAssigned(302, "urgent", assignee: NewYork);
    }

    private async Task<List<long?>> CustomerRepliesNotifiedAsync()
    {
        var watcher = new TicketWatcher(_settings, new ZendeskClient(_settings, _zendesk), _notifier);
        await watcher.InitializeAsync(CancellationToken.None);
        await watcher.PollAsync(CancellationToken.None); // first look at the group: nothing old pops up

        _zendesk.Reply(301, Customer, "Any news?");
        _zendesk.Reply(302, Customer, "Any news?");
        await watcher.PollAsync(CancellationToken.None);
        return _notifier.Shown.Where(n => n.Kind == NotificationKind.HelpColleague).Select(n => n.TicketId).Order().ToList();
    }

    [Fact]
    public async Task Before_the_list_was_reviewed_your_own_region_is_skipped()
    {
        Assert.Null(_settings.HelpRegionDefaultDone);
        Assert.Equal([302L], await CustomerRepliesNotifiedAsync()); // New York only, not Berlin (my region)
    }

    [Fact]
    public async Task Someone_from_your_company_cced_as_end_user_is_not_the_customer()
    {
        _settings.HelpRegionDefaultDone = true;
        _zendesk.Me.Email = "agent@acme.com";
        _zendesk.Users[600] = new User { Id = 600, Name = "Kim Sales", Role = "end-user", Email = "kim@acme.com" };
        var watcher = new TicketWatcher(_settings, new ZendeskClient(_settings, _zendesk), _notifier);
        await watcher.InitializeAsync(CancellationToken.None);
        await watcher.PollAsync(CancellationToken.None);

        _zendesk.Reply(301, 600, "Adding my colleague here."); // only our salesperson wrote
        _zendesk.Reply(302, Customer, "Any news?");
        _zendesk.Reply(302, 600, "We're on it!");              // our salesperson answered the customer
        await watcher.PollAsync(CancellationToken.None);

        Assert.DoesNotContain(_notifier.Shown, n => n.Kind == NotificationKind.HelpColleague);
    }

    [Fact]
    public async Task Any_status_counts_while_the_customer_waits_and_the_status_is_shown()
    {
        _settings.HelpRegionDefaultDone = true;
        _zendesk.CustomStatuses.Add(new CustomStatus { Id = 77, AgentLabel = "Escalated", StatusCategory = "hold" });
        _zendesk.Tickets[302].Status = "hold";
        _zendesk.Tickets[302].CustomStatusId = 77;

        var notified = await CustomerRepliesNotifiedAsync();

        Assert.Contains(302L, notified);
        Assert.EndsWith(" · Escalated", _notifier.Shown.Single(n => n.TicketId == 302).Body);
    }

    [Fact]
    public async Task In_progress_is_trusted_for_a_while_then_followed_up()
    {
        _settings.HelpRegionDefaultDone = true;
        _settings.InProgressGraceHours = 4; // the customers wrote about 2 hours ago (the fake clock)
        _zendesk.CustomStatuses.Add(new CustomStatus { Id = 88, AgentLabel = "In progress", StatusCategory = "open" });
        _zendesk.Tickets[302].CustomStatusId = 88;

        Assert.Equal([301L], await CustomerRepliesNotifiedAsync()); // 302 is in progress: not yet

        // Later (here: a shorter trust time), it's looked at again: still no answer, so it comes after all.
        _settings.InProgressGraceHours = 1;
        var watcher = new TicketWatcher(_settings, new ZendeskClient(_settings, _zendesk), _notifier);
        await watcher.InitializeAsync(CancellationToken.None);
        await watcher.PollAsync(CancellationToken.None);
        var followUp = _notifier.Shown.Single(n => n.Kind == NotificationKind.HelpColleague && n.TicketId == 302);
        Assert.Matches(@" · In progress, no answer for \d h$", followUp.Body);
    }

    [Theory]
    [InlineData("In progress", 1, false)]  // someone is on it: trusted
    [InlineData("Scheduled", 1, false)]    // a time was agreed: trusted too
    [InlineData("Scheduled", 6, true)]
    [InlineData("In Progress", 5, true)]   // waited longer than 4 hours
    [InlineData("Escalated", 0, true)]     // any other status: right away
    [InlineData("Pending", 0, true)]
    public void Only_in_progress_waits(string status, int hoursWaited, bool expected)
    {
        var now = DateTimeOffset.UtcNow;
        Assert.Equal(expected, TicketRules.CustomerWaitingNeedsNotice(status, now.AddHours(-hoursWaited), now, TimeSpan.FromHours(4)));
    }

    [Fact]
    public async Task Once_reviewed_the_ticks_decide()
    {
        _settings.HelpRegionDefaultDone = true; // e.g. Berlin was ticked again on purpose
        Assert.Equal([301L, 302L], await CustomerRepliesNotifiedAsync());
    }
}

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
    public async Task In_progress_notifies_right_away_like_any_status()
    {
        _settings.HelpRegionDefaultDone = true;
        _zendesk.CustomStatuses.Add(new CustomStatus { Id = 88, AgentLabel = "In progress", StatusCategory = "open" });
        _zendesk.Tickets[302].CustomStatusId = 88;

        Assert.Equal([301L, 302L], await CustomerRepliesNotifiedAsync());
        Assert.EndsWith(" · In progress", _notifier.Shown.Single(n => n.TicketId == 302).Body.Split('\n')[1]); // the second line: why, then the status
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
        Assert.EndsWith(" · Escalated", _notifier.Shown.Single(n => n.TicketId == 302).Body.Split('\n')[1]);
    }

    [Fact]
    public async Task An_unticked_region_skips_its_colleagues_except_the_ones_ticked()
    {
        _settings.HelpRegionDefaultDone = true;
        _settings.HelpOffRegions = [TimeRegion.Americas];
        Assert.Equal([301L], await CustomerRepliesNotifiedAsync()); // New York's region is unticked

        _notifier.Shown.Clear();
        _settings.HelpIncludedAssignees = [new Colleague { Id = NewYork, Name = "Nia NewYork" }];
        _settings.HelpExcludedAssignees = [new Colleague { Id = Berlin, Name = "Berit Berlin" }];
        Assert.Equal([302L], await CustomerRepliesNotifiedAsync()); // ticked one by one; Berlin unticked one by one
    }

    [Fact]
    public async Task Once_reviewed_the_ticks_decide()
    {
        _settings.HelpRegionDefaultDone = true; // e.g. Berlin was ticked again on purpose
        Assert.Equal([301L, 302L], await CustomerRepliesNotifiedAsync());
    }

    [Fact]
    public async Task Off_work_but_active_in_Zendesk_just_now_is_left_alone()
    {
        _settings.HelpRegionDefaultDone = true;
        _zendesk.AddAssigned(303, "normal", assignee: Berlin);
        _zendesk.Reply(303, Berlin, "Looking into it.");
        _zendesk.Audits[303][^1].CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-10); // Berlin worked late

        Assert.Equal([302L], await CustomerRepliesNotifiedAsync()); // New York is off and idle: still notified
        Assert.Contains(" is off work · ", _notifier.Shown.Single(n => n.TicketId == 302).Body);
    }

    [Fact]
    public async Task In_working_hours_but_inactive_for_hours_counts_as_away()
    {
        _settings.HelpRegionDefaultDone = true;
        _settings.WorkDays = [.. Enum.GetValues<DayOfWeek>()]; // everyone is in their working hours
        _settings.WorkdayStartHour = 0;
        _settings.WorkdayEndHour = 24;
        _zendesk.AddAssigned(303, "normal", assignee: Berlin);
        _zendesk.Reply(303, Berlin, "Looking into it.");       // Berlin did something within the last 3 hours

        Assert.Equal([302L], await CustomerRepliesNotifiedAsync()); // New York has done nothing: away
        Assert.Contains("Nia NewYork: no activity in Zendesk for 3+ h", _notifier.Shown.Single(n => n.TicketId == 302).Body);
    }
}

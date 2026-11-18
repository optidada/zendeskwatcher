namespace ZendeskWatcher.Tests;

/// <summary>
/// A request that fails halfway through a check (Zendesk busy, the hidden browser restarting) never costs a
/// notification: what wasn't announced yet is announced by the next check, not passed over as already seen.
/// </summary>
public class MissedNotificationTests
{
    private const long Customer = 500, NewYork = 21, Colleague = 60;

    private readonly FakeZendesk _zendesk = new();
    private readonly RecordingNotifier _notifier = new();

    public MissedNotificationTests()
    {
        File.Delete(AppPaths.StateFile);
        _zendesk.Users[Customer] = new User { Id = Customer, Name = "Sam Customer", Role = "end-user" };
        _zendesk.Users[Colleague] = new User { Id = Colleague, Name = "Sam Colleague", Role = "agent" };
        _zendesk.Users[NewYork] = new User { Id = NewYork, Name = "Nia NewYork", Role = "agent", IanaTimeZone = "America/New_York" };
    }

    /// <summary>A watcher whose first check has recorded what's there.</summary>
    private async Task<TicketWatcher> StartAsync(Settings settings)
    {
        var watcher = new TicketWatcher(settings, new ZendeskClient(settings, _zendesk), _notifier);
        await watcher.InitializeAsync(CancellationToken.None);
        await watcher.PollAsync(CancellationToken.None);
        _notifier.Shown.Clear();
        return watcher;
    }

    [Fact]
    public async Task A_new_ticket_whose_notification_failed_comes_with_the_next_check()
    {
        var watcher = await StartAsync(new Settings { Subdomain = "test", WatchedGroups = [new WatchedGroup { Id = 10, Name = "Tier 2" }], TrackMyTickets = false });

        _zendesk.AddUnassigned(101);
        _notifier.FailNextShows = 1;
        await Assert.ThrowsAsync<HttpRequestException>(() => watcher.PollAsync(CancellationToken.None));
        Assert.Empty(_notifier.Shown);

        await watcher.PollAsync(CancellationToken.None);
        Assert.Equal(101, Assert.Single(_notifier.Shown).TicketId);
        _notifier.Shown.Clear();

        await watcher.PollAsync(CancellationToken.None);
        Assert.Empty(_notifier.Shown); // and only once
    }

    [Fact]
    public async Task A_ticket_newly_assigned_to_me_whose_history_failed_comes_with_the_next_check()
    {
        _zendesk.AddAssigned(699, "high"); // one I already have (the fake's clock runs behind: it sets where "newer" starts)
        var watcher = await StartAsync(new Settings { Subdomain = "test", WatchedGroups = [], TrackMyTickets = true, TrackCcTickets = false });

        _zendesk.AddAssigned(700, "high", subject: "Login broken");
        _zendesk.Reply(700, Customer, "Still broken");
        _zendesk.FailNext("/tickets/700/audits");
        await Assert.ThrowsAsync<HttpRequestException>(() => watcher.PollAsync(CancellationToken.None));
        Assert.Empty(_notifier.Shown);

        await watcher.PollAsync(CancellationToken.None);
        Assert.Equal(700, Assert.Single(_notifier.Shown).TicketId);
    }

    [Fact]
    public async Task A_customer_reply_on_a_colleagues_ticket_whose_comments_failed_comes_with_the_next_check()
    {
        var watcher = await StartAsync(new Settings
        {
            Subdomain = "test", WatchedGroups = [new WatchedGroup { Id = 10, Name = "Tier 2" }], TrackMyTickets = false,
            NotifyUnassigned = false, HelpColleagues = true, HelpOnlyDuringMyHours = false, HelpRegionDefaultDone = true,
            WorkDays = [] // every colleague is off work
        });
        _zendesk.AddAssigned(301, "urgent", assignee: NewYork);
        await watcher.PollAsync(CancellationToken.None); // the colleague's ticket is known

        _zendesk.Reply(301, Customer, "Any news?");
        _zendesk.FailNext("/tickets/301/comments");
        await watcher.PollAsync(CancellationToken.None); // the colleagues part fails quietly, the rest goes on
        Assert.Empty(_notifier.Shown);

        await watcher.PollAsync(CancellationToken.None);
        var shown = Assert.Single(_notifier.Shown);
        Assert.Equal((NotificationKind.HelpColleague, 301L), (shown.Kind, shown.TicketId!.Value));
    }

    [Fact]
    public async Task An_already_taken_ticket_whose_history_failed_comes_with_the_next_check()
    {
        var settings = new Settings { Subdomain = "test", WatchedGroups = [new WatchedGroup { Id = 10, Name = "Tier 2" }], TrackMyTickets = false };
        var watcher = await StartAsync(settings);
        settings.TakenQuicklyPriorities = ["urgent"];

        _zendesk.AddTakenQuickly(202, "urgent", by: Colleague, to: Colleague);
        _zendesk.FailNext("/tickets/202/audits");
        await Assert.ThrowsAsync<HttpRequestException>(() => watcher.PollAsync(CancellationToken.None));
        Assert.Empty(_notifier.Shown);

        await watcher.PollAsync(CancellationToken.None);
        Assert.Equal(202, Assert.Single(_notifier.Shown).TicketId);
    }
}

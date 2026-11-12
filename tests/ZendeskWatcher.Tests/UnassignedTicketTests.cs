namespace ZendeskWatcher.Tests;

/// <summary>
/// The "new unassigned ticket" behavior end to end: <see cref="TicketWatcher"/> against a fake Zendesk.
/// The rules: only verified-unassigned tickets notify; a newly monitored group gets one summary instead of a flood;
/// tickets that arrived while the app was off still notify after a restart.
/// </summary>
public class UnassignedTicketTests
{
    private readonly FakeZendesk _zendesk = new();
    private readonly RecordingNotifier _notifier = new();
    private readonly Settings _settings = new()
    {
        Subdomain = "test",
        WatchedGroups = [new WatchedGroup { Id = 10, Name = "Tier 2" }],
        TrackMyTickets = false // keep these tests about unassigned tickets
    };

    public UnassignedTicketTests() => File.Delete(AppPaths.StateFile); // every test starts like a first install

    /// <summary>A fresh watcher, as after an app start: it loads state.json from the previous run.</summary>
    private async Task<TicketWatcher> StartAsync()
    {
        var watcher = new TicketWatcher(_settings, new ZendeskClient(_settings, _zendesk), _notifier);
        await watcher.InitializeAsync(CancellationToken.None);
        return watcher;
    }

    private List<NotificationContent> TakeShown()
    {
        var shown = _notifier.Shown.ToList();
        _notifier.Shown.Clear();
        return shown;
    }

    [Fact]
    public async Task Newly_monitored_group_gets_one_summary_of_what_is_waiting()
    {
        _zendesk.AddUnassigned(101);
        _zendesk.AddUnassigned(102);
        var watcher = await StartAsync();

        await watcher.PollAsync(CancellationToken.None);

        var shown = Assert.Single(TakeShown());
        Assert.Equal("Now watching", shown.Heading);
        Assert.Equal("2 unassigned tickets already waiting", shown.Title);
        Assert.True(watcher.ShowedAlreadyWaiting);
        Assert.Equal(2, watcher.UnassignedCount);
    }

    [Fact]
    public async Task Newly_monitored_group_with_one_ticket_shows_that_ticket()
    {
        _zendesk.AddUnassigned(101, subject: "Login broken");
        var watcher = await StartAsync();

        await watcher.PollAsync(CancellationToken.None);

        var shown = Assert.Single(TakeShown());
        Assert.Equal("Already waiting", shown.Heading);
        Assert.Equal(101, shown.TicketId);
        Assert.True(shown.CanTake);
    }

    [Fact]
    public async Task Empty_group_is_quiet_then_a_new_ticket_notifies_once()
    {
        var watcher = await StartAsync();
        await watcher.PollAsync(CancellationToken.None);
        Assert.Empty(TakeShown());

        _zendesk.AddUnassigned(103, subject: "Payment failed");
        await watcher.PollAsync(CancellationToken.None);

        var shown = Assert.Single(TakeShown());
        Assert.Equal("New ticket", shown.Heading);
        Assert.Equal("#103 Payment failed", shown.Title);
        Assert.Equal(103, shown.TicketId);
        Assert.Equal("Tier 2", shown.Context);
        Assert.True(shown.CanTake);

        await watcher.PollAsync(CancellationToken.None);
        Assert.Empty(TakeShown()); // not again on the next check
    }

    [Fact]
    public async Task Stale_search_hit_that_is_really_assigned_does_not_notify()
    {
        var watcher = await StartAsync();
        await watcher.PollAsync(CancellationToken.None);

        var ticket = _zendesk.AddUnassigned(104);
        ticket.AssigneeId = 42;                     // really assigned...
        _zendesk.StaleSearchHits[10] = [104];       // ...but the search index still says unassigned
        await watcher.PollAsync(CancellationToken.None);

        Assert.Empty(TakeShown());
        Assert.Equal(0, watcher.UnassignedCount);
    }

    [Fact]
    public async Task Ticket_that_arrived_while_the_app_was_off_notifies_after_restart()
    {
        _zendesk.AddUnassigned(101);
        var first = await StartAsync();
        await first.PollAsync(CancellationToken.None); // baseline: "Already waiting" for 101
        TakeShown();

        _zendesk.AddUnassigned(105);                    // created while the app is closed
        var afterRestart = await StartAsync();
        await afterRestart.PollAsync(CancellationToken.None);

        var shown = Assert.Single(TakeShown());          // 105 only; 101 was already seen
        Assert.Equal("New ticket", shown.Heading);
        Assert.Equal(105, shown.TicketId);
    }

    [Fact]
    public async Task Many_new_tickets_at_once_become_one_summary()
    {
        var watcher = await StartAsync();
        await watcher.PollAsync(CancellationToken.None);

        for (var id = 201; id <= 206; id++)
            _zendesk.AddUnassigned(id);
        await watcher.PollAsync(CancellationToken.None);

        var shown = Assert.Single(TakeShown());
        Assert.Equal("6 new unassigned tickets", shown.Title);
        Assert.Null(shown.TicketId);
    }

    [Fact]
    public async Task Five_new_tickets_still_notify_separately()
    {
        var watcher = await StartAsync();
        await watcher.PollAsync(CancellationToken.None);

        for (var id = 201; id <= 205; id++)
            _zendesk.AddUnassigned(id);
        await watcher.PollAsync(CancellationToken.None);

        Assert.Equal([201L, 202, 203, 204, 205], TakeShown().Select(s => s.TicketId!.Value)); // oldest first
    }

    [Fact]
    public async Task Tickets_that_arrive_while_notifications_are_off_are_not_reported_later()
    {
        var watcher = await StartAsync();
        await watcher.PollAsync(CancellationToken.None);

        _settings.NotifyUnassigned = false;
        _zendesk.AddUnassigned(301);
        await watcher.PollAsync(CancellationToken.None);
        Assert.Empty(TakeShown());

        _settings.NotifyUnassigned = true;
        await watcher.PollAsync(CancellationToken.None);
        Assert.Empty(TakeShown());
    }

    [Fact]
    public async Task Ticket_moved_out_of_the_group_is_not_counted()
    {
        var watcher = await StartAsync();
        await watcher.PollAsync(CancellationToken.None);

        var ticket = _zendesk.AddUnassigned(401);
        _zendesk.StaleSearchHits[10] = [401];
        ticket.GroupId = 99;                         // moved, but search still lists it under group 10
        await watcher.PollAsync(CancellationToken.None);

        Assert.Empty(TakeShown());
    }

    [Fact]
    public async Task Missing_monitored_group_is_reported()
    {
        _settings.WatchedGroups.Add(new WatchedGroup { Id = 777, Name = "Gone group" });

        await StartAsync();

        Assert.Contains("Gone group", _notifier.MissingGroups);
    }
}

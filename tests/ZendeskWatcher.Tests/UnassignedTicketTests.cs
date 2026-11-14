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
    public async Task Start_summary_says_what_is_waiting_and_what_is_watched_in_words()
    {
        _zendesk.AddUnassigned(101);
        _settings.HelpColleagues = true;
        _settings.HelpPriorities = ["urgent", "high"];
        var watcher = await StartAsync();
        await watcher.PollAsync(CancellationToken.None);

        var now = watcher.Snapshot(); // Insights' "Right now" card
        Assert.Equal(1, now.Unassigned);
        Assert.Equal(("Tier 2", 1), (now.Groups.Single().Group.Name, now.Groups.Single().Count));
        Assert.Null(now.Mine); // not tracked in these tests

        var lines = watcher.StartSummary().Body!.Split('\n');
        Assert.StartsWith("**1 unassigned**, created ", lines[0]);
        Assert.Equal("In Tier 2", lines[1]);
        Assert.Equal("Colleagues off work: replies on Urgent and High", lines[^1]);
    }

    [Fact]
    public async Task Scheduled_unassigned_tickets_stand_out_in_notifications_and_summaries()
    {
        _zendesk.CustomStatuses.Add(new CustomStatus { Id = 7, AgentLabel = "Scheduled", StatusCategory = "hold" });
        _zendesk.AddUnassigned(101);
        var watcher = await StartAsync();
        await watcher.PollAsync(CancellationToken.None); // baseline: "Already waiting"
        TakeShown();

        // One new Scheduled ticket: said in the heading, counted for the "while you were away" summary.
        _zendesk.AddUnassigned(102).CustomStatusId = 7;
        await watcher.PollAsync(CancellationToken.None);
        var shown = Assert.Single(TakeShown());
        Assert.Equal("New ticket · Scheduled", shown.Heading);
        Assert.Equal(1, shown.ScheduledCount);

        // The start summary counts them.
        Assert.Equal(1, watcher.Snapshot().Scheduled);
        Assert.Equal("**1 of them Scheduled**", watcher.StartSummary().Body!.Split('\n')[2]);

        // An ordinary one: no mention.
        _zendesk.AddUnassigned(103);
        await watcher.PollAsync(CancellationToken.None);
        Assert.Equal("New ticket", Assert.Single(TakeShown()).Heading);
    }

    [Fact]
    public async Task A_new_sla_type_is_learned_from_zendesks_timers()
    {
        _zendesk.Organizations[900] = "Gold Corp";
        _zendesk.OrganizationFieldValues[900] = new() { ["sla_type"] = "slagold" };
        var watcher = await StartAsync();
        await watcher.PollAsync(CancellationToken.None); // baseline

        // Two new tickets with Zendesk timers: 45 min to the first reply.
        foreach (var id in new long[] { 301, 302 })
        {
            var t = _zendesk.AddUnassigned(id);
            t.Priority = "high";
            t.OrganizationId = 900;
            t.Slas = new TicketSlas { PolicyMetrics = [new SlaMetric { Metric = "first_reply_time", Stage = "active", BreachAt = t.CreatedAt.AddMinutes(45) }] };
        }
        await watcher.PollAsync(CancellationToken.None);
        TakeShown();
        Assert.Equal((SlaTarget.Min(45), 2), SlaLearning.Current!.Estimate("slagold", "high", true));

        // A third without a timer: the estimate gives its deadline.
        var third = _zendesk.AddUnassigned(303);
        third.Priority = "high";
        third.OrganizationId = 900;
        await watcher.PollAsync(CancellationToken.None);
        Assert.Equal(third.CreatedAt.AddMinutes(45), Assert.Single(TakeShown()).SlaDue);
    }

    [Theory]
    [InlineData(1, 1, ", **Scheduled**")]
    [InlineData(2, 2, ", **both Scheduled**")]
    [InlineData(5, 5, ", **all Scheduled**")]
    [InlineData(5, 2, ", **2 of them Scheduled**")]
    [InlineData(5, 0, "")]
    public void Scheduled_note_reads_naturally(int total, int scheduled, string expected) =>
        Assert.Equal(expected, TicketRules.ScheduledNote(total, scheduled));

    [Fact]
    public void Catch_up_and_away_summaries_mention_scheduled_ones()
    {
        var now = new DateTime(2026, 11, 14, 9, 0, 0);
        Assert.StartsWith("**3 unassigned tickets** in your groups, **1 of them Scheduled**.",
            TicketRules.CatchUpText(now.AddHours(-16), now, 3, 0, 0, 0, scheduled: 1));

        var waiting = new List<NotificationContent>
        {
            new(NotificationKind.NewTicket, "New ticket · Scheduled", "#1 A", null) { ScheduledCount = 1 },
            new(NotificationKind.NewTicket, "New ticket", "#2 B", null)
        };
        Assert.Equal("2 new tickets, **1 of them Scheduled**", Notifier.AwaySummary(waiting, 0).Body);
    }

    [Fact]
    public async Task New_tickets_outside_my_hours_are_skipped_when_chosen()
    {
        var hour = DateTime.Now.Hour; // working hours that are over right now
        (_settings.MyWorkdayStartHour, _settings.MyWorkdayEndHour) = hour <= 21 ? (hour + 1, hour + 2) : (0, 1);
        _settings.OnlyNotifyDuringMyHours = true;
        var watcher = await StartAsync();
        await watcher.PollAsync(CancellationToken.None); // baseline

        _zendesk.AddUnassigned(201);
        await watcher.PollAsync(CancellationToken.None);
        Assert.Empty(TakeShown());
        Assert.Equal(1, watcher.UnassignedCount); // still counted

        // Back at work: only tickets that are new from now on notify, the skipped one doesn't come back.
        _settings.OnlyNotifyDuringMyHours = false;
        _zendesk.AddUnassigned(202);
        await watcher.PollAsync(CancellationToken.None);
        Assert.Equal("#202 Help!", Assert.Single(TakeShown()).Title);
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
        Assert.True(shown.IsTicketNotification); // counted in the statistics
        Assert.False(watcher.StartSummary().IsTicketNotification); // the summary isn't

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

public class TicketListTests
{
    [Fact]
    public void Lists_five_tickets_then_how_many_more()
    {
        var tickets = Enumerable.Range(1, 7).Select(i => new ZendeskWatcher.Zendesk.Ticket { Id = i, Subject = $"Problem {i}" }).ToList();

        var lines = ZendeskWatcher.Tracking.TicketWatcher.TicketList(tickets).Split('\n');

        Assert.Equal(6, lines.Length);
        Assert.Equal("#1 Problem 1", lines[0]);
        Assert.Equal("… and 2 more", lines[^1]);
        Assert.True(lines.Length <= ZendeskWatcher.Tracking.TicketWatcher.ListLines);
    }
}

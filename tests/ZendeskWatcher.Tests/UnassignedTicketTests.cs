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
    public async Task Snapshot_counts_what_is_waiting()
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

        // Insights' "Right now" counts them.
        Assert.Equal(1, watcher.Snapshot().Scheduled);

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
        Assert.Equal("**3** unassigned in your groups · 1 Scheduled",
            TicketRules.CatchUpBody(new TicketRules.CatchUpCounts { Unassigned = 3, Scheduled = 1 }, null));

        var waiting = new List<NotificationContent>
        {
            new(NotificationKind.NewTicket, "New ticket · Scheduled", "#1 A", "") { ScheduledCount = 1 },
            new(NotificationKind.NewTicket, "New ticket", "#2 B", "")
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
        Assert.Equal("Group added", shown.Heading);
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
    public async Task An_only_urgent_clients_ticket_raised_to_high_comes_as_priority_raised()
    {
        _zendesk.Organizations[70] = "Contoso";
        _settings.SetClientChoice(new ClientChoice { Id = 70, Name = "Contoso", GroupId = 10, Mode = ClientMode.OnlyUrgent });
        var watcher = await StartAsync();
        await watcher.PollAsync(CancellationToken.None);

        var ticket = _zendesk.AddUnassigned(103);
        ticket.OrganizationId = 70;
        ticket.Priority = "normal";
        await watcher.PollAsync(CancellationToken.None);
        Assert.Empty(TakeShown()); // the group takes All, but the client only urgent: the stricter wins

        ticket.Priority = "high";
        await watcher.PollAsync(CancellationToken.None);
        var shown = Assert.Single(TakeShown());
        Assert.Equal("Priority raised", shown.Heading);
        Assert.Equal(103, shown.TicketId);
    }

    [Fact]
    public async Task Unticked_priorities_stay_quiet_until_raised()
    {
        _settings.NewTicketPriorities[10] = ["urgent", "high"];
        var watcher = await StartAsync();
        await watcher.PollAsync(CancellationToken.None);

        // A Normal and a Low ticket: no notification, not counted.
        var normal = _zendesk.AddUnassigned(103);
        normal.Priority = "normal";
        _zendesk.AddUnassigned(104).Priority = "low";
        await watcher.PollAsync(CancellationToken.None);
        Assert.Empty(TakeShown());
        Assert.Equal(0, watcher.Snapshot().Unassigned);

        // Raised to High while still unassigned: notifies then, once.
        normal.Priority = "high";
        await watcher.PollAsync(CancellationToken.None);
        var shown = Assert.Single(TakeShown());
        Assert.Equal("Priority raised", shown.Heading);
        Assert.Equal(103, shown.TicketId);
        await watcher.PollAsync(CancellationToken.None);
        Assert.Empty(TakeShown());

        // Ticking Low later isn't a raise: the waiting Low ticket doesn't notify now.
        _settings.NewTicketPriorities[10] = ["urgent", "high", "low"];
        await watcher.PollAsync(CancellationToken.None);
        Assert.Empty(TakeShown());
        Assert.Equal(2, watcher.Snapshot().Unassigned);
    }

    [Fact]
    public async Task A_P1_taken_before_any_check_saw_it_still_notifies_when_chosen()
    {
        _zendesk.Users[55] = new User { Id = 55, Name = "Anna Agent" };
        _zendesk.Users[66] = new User { Id = 66, Name = "Bob Lead" };
        var watcher = await StartAsync();
        await watcher.PollAsync(CancellationToken.None); // the group's first look

        // Off (the default): nothing, and no extra search.
        _zendesk.AddTakenQuickly(201, "urgent", by: 55, to: 55);
        await watcher.PollAsync(CancellationToken.None);
        Assert.Empty(TakeShown());

        // P1 chosen: an urgent ticket taken between checks comes, once, saying who took it and how fast.
        _settings.TakenQuicklyPriorities = ["urgent"];
        _zendesk.AddTakenQuickly(202, "urgent", by: 66, to: 55);
        _zendesk.AddTakenQuickly(203, "normal", by: 55, to: 55); // not a chosen priority
        _zendesk.AddTakenQuickly(204, "urgent", by: 55, to: 55, createdAssigned: true); // nobody took it: it came assigned
        await watcher.PollAsync(CancellationToken.None);
        var shown = TakeShown();
        Assert.Equal([201L, 202L], shown.Select(n => n.TicketId!.Value).Order());
        var assigned = shown.Single(n => n.TicketId == 202);
        Assert.Equal("Already taken", assigned.Heading);
        Assert.Contains("**Bob Lead** assigned it to **Anna Agent** after 1 min", assigned.Body);
        Assert.Contains("**Anna Agent** took it after 1 min", shown.Single(n => n.TicketId == 201).Body);
        Assert.False(assigned.CanTake);

        await watcher.PollAsync(CancellationToken.None);
        Assert.Empty(TakeShown()); // not again

        // Solved straight away (solving gives it an assignee): found too, and the line says it's solved. Only the full
        // check looks (every 3 minutes, 30 minutes back): the quick one in between doesn't search for it.
        _zendesk.AddTakenQuickly(205, "urgent", by: 55, to: 55).Status = "solved";
        var searches = _zendesk.Requested.Count;
        await watcher.PollAsync(CancellationToken.None, newTicketsOnly: true);
        Assert.Empty(TakeShown());
        static bool TakenSearch(string url) => Uri.UnescapeDataString(url.Replace('+', ' ')).Contains("-assignee:none");
        Assert.DoesNotContain(_zendesk.Requested.Skip(searches), TakenSearch);
        searches = _zendesk.Requested.Count;
        await watcher.PollAsync(CancellationToken.None);
        Assert.Contains(_zendesk.Requested.Skip(searches), TakenSearch);
        Assert.EndsWith("**Anna Agent** took it after 1 min · solved", Assert.Single(TakeShown()).Body);
    }

    [Fact]
    public void Taken_line_says_who_and_how_fast()
    {
        Assert.Equal("**Anna** took it within a minute", TicketRules.TakenLine("Anna", null, false, TimeSpan.FromSeconds(20)));
        Assert.Equal("**Bob** assigned it to **Anna** after 4 min", TicketRules.TakenLine("Anna", "Bob", false, TimeSpan.FromMinutes(4.5)));
        Assert.Equal("Assigned to **Anna** by a rule after 1 h 5 min", TicketRules.TakenLine("Anna", null, true, TimeSpan.FromMinutes(65)));
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

        await watcher.PollAsync(CancellationToken.None);
        Assert.Empty(TakeShown()); // not again on the next check
    }

    [Fact]
    public async Task Notification_of_a_new_ticket_closes_once_someone_takes_it()
    {
        var watcher = await StartAsync();
        await watcher.PollAsync(CancellationToken.None);
        var taken = _zendesk.AddUnassigned(106);
        var moved = _zendesk.AddUnassigned(107);
        await watcher.PollAsync(CancellationToken.None);
        Assert.Equal(2, TakeShown().Count);

        // Still unassigned: stays, also when it left the monitored group (no longer in the search, but nobody took it).
        moved.GroupId = 99;
        await watcher.PollAsync(CancellationToken.None);
        Assert.Empty(_notifier.ClosedTaken);

        _zendesk.Users[42] = new User { Id = 42, Name = "Anna Svensson", Role = "agent" };
        taken.AssigneeId = 42;
        await watcher.PollAsync(CancellationToken.None);
        Assert.Equal([106L], _notifier.ClosedTaken);
        Assert.Equal("Taken by Anna", _notifier.TakenNotes[106]);

        moved.Status = "solved";
        await watcher.PollAsync(CancellationToken.None);
        Assert.Equal([106L, 107L], _notifier.ClosedTaken);
        Assert.Equal("Solved", _notifier.TakenNotes[107]);

        await watcher.PollAsync(CancellationToken.None);
        Assert.Equal(2, _notifier.ClosedTaken.Count); // once each
        Assert.Empty(_notifier.NotedTaken);
        Assert.Empty(TakeShown());
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
        Assert.Equal("… and 2 more: click to see them all", lines[^1]);
        Assert.True(lines.Length <= ZendeskWatcher.Tracking.TicketWatcher.ListLines);
    }
}

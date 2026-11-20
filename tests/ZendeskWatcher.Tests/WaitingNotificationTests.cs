namespace ZendeskWatcher.Tests;

/// <summary>
/// Notifications that waited a while (behind others, or while away): which are still needed when they finally show,
/// the "while you were away" summary, and "only while I'm working" for my own tickets.
/// </summary>
public class WaitingNotificationTests
{
    private const long Customer = 50;
    private const long Colleague = 60;

    private readonly FakeZendesk _zendesk = new();
    private readonly Settings _settings = new() { Subdomain = "test", WatchedGroups = [] };

    public WaitingNotificationTests()
    {
        File.Delete(AppPaths.StateFile);
        _zendesk.Users[Customer] = new User { Id = Customer, Name = "Jane Customer", Role = "end-user" };
        _zendesk.Users[Colleague] = new User { Id = Colleague, Name = "Sam Colleague", Role = "agent" };
    }

    private Task<List<NotificationContent>> Check(NotificationContent content, bool dropAnswered = true, bool deep = true)
    {
        _settings.DropAnswered = dropAnswered;
        return TicketWatcher.StillNeededAsync(new ZendeskClient(_settings, _zendesk), _zendesk.Me.Id, [content], _settings, deep, CancellationToken.None);
    }

    private DateTimeOffset LastChange(long ticket) => _zendesk.Audits[ticket].Max(a => a.CreatedAt);

    private static NotificationContent About(NotificationKind kind, long ticket, DateTimeOffset? changedAt = null) =>
        new(kind, "Heading", $"#{ticket}", TicketId: ticket) { IsTicketNotification = true, ChangedAt = changedAt };

    // ---------------------------------------------------------------- still needed?

    [Fact]
    public async Task New_ticket_taken_meanwhile_is_dropped_when_chosen()
    {
        _zendesk.AddUnassigned(100);
        _zendesk.AddUnassigned(101).AssigneeId = Colleague;

        Assert.Single(await Check(About(NotificationKind.NewTicket, 100)));
        Assert.Empty(await Check(About(NotificationKind.NewTicket, 101)));
    }

    [Fact]
    public async Task Each_kind_of_solving_has_its_own_switch_deleted_is_always_dropped()
    {
        _settings.DropAnswered = false;
        var since = DateTimeOffset.UtcNow.AddDays(-1);
        _zendesk.AddAssigned(420, "normal");
        _zendesk.Change(420, _zendesk.Me.Id, "status", "open", "solved");
        _zendesk.Tickets[420].Status = "solved";
        _zendesk.AddAssigned(421, "normal");
        _zendesk.Change(421, Colleague, "status", "open", "closed");
        _zendesk.Tickets[421].Status = "closed";

        Assert.Empty(await Check(About(NotificationKind.MyTicket, 420, since))); // defaults: all stop
        Assert.Empty(await Check(About(NotificationKind.MyTicket, 421, since)));
        Assert.Empty(await Check(About(NotificationKind.MyTicket, 999, since))); // deleted

        _settings.DropSolvedByMe = false;
        Assert.Equal("Solved by you", Assert.Single(await Check(About(NotificationKind.MyTicket, 420, since))).Meanwhile);
        Assert.Empty(await Check(About(NotificationKind.MyTicket, 421, since)));

        _settings.DropSolvedByColleague = false;
        Assert.Equal("Closed by Sam", Assert.Single(await Check(About(NotificationKind.MyTicket, 421, since))).Meanwhile);

        _settings.DropSolvedByMe = _settings.DropSolvedByClient = false;
        Assert.Empty(await Check(About(NotificationKind.MyTicket, 999, since))); // deleted: always
    }

    [Fact]
    public async Task Assigned_to_me_shows_anyway_when_chosen()
    {
        _settings.DropAssignedToMe = false;
        _zendesk.AddUnassigned(130).AssigneeId = _zendesk.Me.Id;

        Assert.Equal("Taken by you", Assert.Single(await Check(About(NotificationKind.NewTicket, 130))).Meanwhile);
    }

    [Fact]
    public async Task Customer_reply_answered_by_a_colleague_is_dropped()
    {
        _zendesk.AddAssigned(200, "high", assignee: Colleague);
        _zendesk.Reply(200, Customer, "Any news?");
        var notification = About(NotificationKind.HelpColleague, 200, LastChange(200));

        Assert.Single(await Check(notification));
        _zendesk.Reply(200, Colleague, "Looking into it");
        Assert.Empty(await Check(notification));
        Assert.Single(await Check(notification, dropAnswered: false)); // the switch is off
        Assert.Single(await Check(notification, deep: false));         // only the cheap ticket check
    }

    [Fact]
    public async Task Internal_note_does_not_count_as_an_answer()
    {
        _zendesk.AddAssigned(201, "high", assignee: Colleague);
        _zendesk.Reply(201, Customer, "Any news?");
        var notification = About(NotificationKind.HelpColleague, 201, LastChange(201));
        _zendesk.Audits[201].Add(new Audit
        {
            Id = 9, AuthorId = Colleague, CreatedAt = LastChange(201).AddMinutes(1),
            Events = [new AuditEvent { Type = "Comment", Public = false, PlainBody = "note" }]
        });

        Assert.Single(await Check(notification));
    }

    [Fact]
    public async Task Change_on_my_ticket_is_dropped_once_I_acted_on_it()
    {
        _zendesk.AddAssigned(300, "normal");
        _zendesk.Reply(300, Customer, "Still broken");
        var notification = About(NotificationKind.MyTicket, 300, LastChange(300));

        _zendesk.Reply(300, Colleague, "FYI");
        Assert.Single(await Check(notification)); // someone else: still news to me
        _zendesk.Reply(300, _zendesk.Me.Id, "On it");
        Assert.Empty(await Check(notification));
    }

    [Fact]
    public async Task Solved_ticket_is_dropped_and_notifications_without_a_ticket_are_kept()
    {
        _zendesk.AddAssigned(400, "normal").Status = "solved";

        Assert.Empty(await Check(About(NotificationKind.MyTicket, 400)));
        Assert.Single(await Check(new NotificationContent(NotificationKind.NewTicket, "New tickets", "7 new unassigned tickets")));
    }

    [Fact]
    public async Task Taken_by_a_colleague_shows_anyway_when_chosen_but_never_taken_by_me()
    {
        _settings.DropTaken = false;
        _zendesk.AddUnassigned(110).AssigneeId = Colleague;
        _zendesk.AddUnassigned(111).AssigneeId = _zendesk.Me.Id;

        var kept = Assert.Single(await Check(About(NotificationKind.NewTicket, 110)));
        Assert.Equal("Taken by Sam", kept.Meanwhile);
        Assert.Contains("**Taken by Sam** meanwhile", kept.DisplayBody(DateTimeOffset.UtcNow));
        Assert.Empty(await Check(About(NotificationKind.NewTicket, 111)));
    }

    [Fact]
    public async Task Solved_by_the_client_shows_anyway_when_chosen_by_an_agent_never()
    {
        _settings.DropSolvedByClient = false;
        _zendesk.AddAssigned(410, "normal");
        var since = DateTimeOffset.UtcNow.AddDays(-1);
        _zendesk.Change(410, Customer, "status", "open", "solved");
        _zendesk.Tickets[410].Status = "solved";
        _zendesk.AddAssigned(411, "normal");
        _zendesk.Change(411, Colleague, "status", "open", "solved");
        _zendesk.Tickets[411].Status = "solved";

        var kept = Assert.Single(await Check(About(NotificationKind.MyTicket, 410, since)));
        Assert.Equal("Solved by the client", kept.Meanwhile);
        Assert.Empty(await Check(About(NotificationKind.MyTicket, 411, since)));

        _settings.DropSolvedByClient = true; // the default: solved is solved
        Assert.Empty(await Check(About(NotificationKind.MyTicket, 410, since)));
    }

    [Fact]
    public async Task Taken_and_moved_to_the_takers_group_still_shows_as_taken()
    {
        _settings.DropTaken = false;
        _settings.WatchedGroups = [new WatchedGroup { Id = 10, Name = "Support" }];
        var ticket = _zendesk.AddUnassigned(122);
        ticket.AssigneeId = Colleague;
        ticket.GroupId = 99; // Sam's group

        Assert.Equal("Taken by Sam", Assert.Single(await Check(About(NotificationKind.NewTicket, 122))).Meanwhile);
    }

    [Fact]
    public async Task New_ticket_moved_to_a_group_I_dont_monitor_is_dropped()
    {
        _settings.WatchedGroups = [new WatchedGroup { Id = 10, Name = "Support" }];
        _zendesk.AddUnassigned(120);
        _zendesk.AddUnassigned(121, group: 99);

        Assert.Single(await Check(About(NotificationKind.NewTicket, 120)));
        Assert.Empty(await Check(About(NotificationKind.NewTicket, 121)));
    }

    // ---------------------------------------------------------------- summary

    [Fact]
    public void Away_summary_counts_by_kind()
    {
        NotificationContent[] waiting =
        [
            About(NotificationKind.NewTicket, 1), About(NotificationKind.NewTicket, 2),
            new(NotificationKind.NewTicket, "New tickets", "6 new unassigned tickets") { TicketCount = 6 },
            About(NotificationKind.MyTicket, 3),
            About(NotificationKind.HelpColleague, 4), About(NotificationKind.HelpColleague, 5)
        ];

        var summary = Notifier.AwaySummary(waiting, dropped: 4);

        Assert.Equal("While you were away", summary.Title);
        Assert.Equal("8 new tickets\n1 update on your tickets\n2 clients waiting on colleagues' tickets\n" +
                     "4 others are no longer needed (taken, solved or answered)", summary.Body);
        Assert.Equal(Notifier.ActionOpenZendesk, summary.ResolvedAction);
        Assert.False(summary.IsTicketNotification); // not counted in the statistics
    }

    // ---------------------------------------------------------------- rules

    [Fact]
    public void New_tickets_are_never_handled_by_later_changes()
    {
        var reply = new Audit { AuthorId = 1, Events = [new AuditEvent { Type = "Comment", Public = true }] };
        Assert.False(TicketRules.HandledSince(NotificationKind.NewTicket, [(reply, "agent")], me: 1));
    }

    // ---------------------------------------------------------------- only while I'm working

    [Fact]
    public void My_hours_start_as_the_colleagues_hours_until_chosen()
    {
        var old = Settings.FromJson("""{ "WorkdayStartHour": 9, "WorkdayEndHour": 18 }""");
        Assert.Equal((9, 18), (old.MyWorkdayStartHour, old.MyWorkdayEndHour));

        old.MyWorkdayStartHour = 6;
        Assert.Equal(9, old.WorkdayStartHour); // separate from now on
    }

    [Fact]
    public async Task My_ticket_changes_outside_my_hours_are_skipped_when_chosen()
    {
        // Working hours that are over right now (an hour that hasn't started yet today).
        var hour = DateTime.Now.Hour;
        (_settings.MyWorkdayStartHour, _settings.MyWorkdayEndHour) = hour <= 21 ? (hour + 1, hour + 2) : (0, 1);
        _settings.TrackMyTickets = true;
        _settings.OnlyNotifyDuringMyHours = true;
        var notifier = new RecordingNotifier();
        _zendesk.AddAssigned(500, "high");
        var watcher = new TicketWatcher(_settings, new ZendeskClient(_settings, _zendesk), notifier);
        await watcher.InitializeAsync(CancellationToken.None);
        await watcher.PollAsync(CancellationToken.None);

        _zendesk.Reply(500, Customer, "Hello?");
        await watcher.PollAsync(CancellationToken.None);
        Assert.Empty(notifier.Shown);

        // Switched off again: the next change notifies, and the skipped one doesn't come back.
        _settings.OnlyNotifyDuringMyHours = false;
        _zendesk.Reply(500, Customer, "Anyone?");
        await watcher.PollAsync(CancellationToken.None);
        var shown = Assert.Single(notifier.Shown);
        Assert.Equal("**Jane Customer**\n“Anyone?”", shown.Body);
        Assert.NotNull(shown.ChangedAt);
    }
}

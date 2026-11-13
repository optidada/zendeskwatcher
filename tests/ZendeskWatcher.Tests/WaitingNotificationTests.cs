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

    private Task<List<NotificationContent>> Check(NotificationContent content, bool dropAnswered = true, bool deep = true) =>
        TicketWatcher.StillNeededAsync(new ZendeskClient(_settings, _zendesk), _zendesk.Me.Id, [content], dropAnswered, deep, CancellationToken.None);

    private DateTimeOffset LastChange(long ticket) => _zendesk.Audits[ticket].Max(a => a.CreatedAt);

    private static NotificationContent About(NotificationKind kind, long ticket, DateTimeOffset? changedAt = null) =>
        new(kind, "Heading", $"#{ticket}", TicketId: ticket) { IsTicketNotification = true, ChangedAt = changedAt };

    // ---------------------------------------------------------------- still needed?

    [Fact]
    public async Task New_ticket_taken_meanwhile_is_dropped()
    {
        _zendesk.AddUnassigned(100);
        _zendesk.AddUnassigned(101).AssigneeId = Colleague;

        Assert.Single(await Check(About(NotificationKind.NewTicket, 100)));
        Assert.Empty(await Check(About(NotificationKind.NewTicket, 101)));
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
        Assert.Equal("8 new tickets\n1 update on your tickets\n2 customers waiting on colleagues' tickets\n" +
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
        Assert.Equal("**Jane Customer** replied: Anyone?", shown.Body);
        Assert.NotNull(shown.ChangedAt);
    }
}

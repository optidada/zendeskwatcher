namespace ZendeskWatcher.Tests;

/// <summary>
/// Changes on my tickets and on tickets I'm CC'd on or follow, end to end against a fake Zendesk: who changed it,
/// which kind of notification, and the priority filters.
/// </summary>
public class TicketChangeTests
{
    private const long Customer = 50;
    private const long Colleague = 60;

    private readonly FakeZendesk _zendesk = new();
    private readonly RecordingNotifier _notifier = new();
    private readonly Settings _settings = new()
    {
        Subdomain = "test",
        WatchedGroups = [],      // no unassigned checks here
        TrackMyTickets = true,
        TrackCcTickets = true
    };

    public TicketChangeTests()
    {
        File.Delete(AppPaths.StateFile);
        _zendesk.Users[Customer] = new User { Id = Customer, Name = "Jane Customer", Role = "end-user" };
        _zendesk.Users[Colleague] = new User { Id = Colleague, Name = "Sam Colleague", Role = "agent" };
    }

    /// <summary>A watcher whose first check has recorded the current tickets silently.</summary>
    private async Task<TicketWatcher> StartAsync()
    {
        var watcher = new TicketWatcher(_settings, new ZendeskClient(_settings, _zendesk), _notifier);
        await watcher.InitializeAsync(CancellationToken.None);
        await watcher.PollAsync(CancellationToken.None);
        Assert.Empty(TakeShown()); // the first check never notifies
        return watcher;
    }

    private List<NotificationContent> TakeShown()
    {
        var shown = _notifier.Shown.ToList();
        _notifier.Shown.Clear();
        return shown;
    }

    private Ticket AddCc(long id, string? priority)
    {
        var ticket = _zendesk.AddAssigned(id, priority, assignee: Colleague);
        ticket.EmailCcIds = [_zendesk.Me.Id];
        return ticket;
    }

    // ---------------------------------------------------------------- my tickets

    [Fact]
    public async Task Customer_reply_on_my_ticket_notifies()
    {
        _zendesk.AddAssigned(500, "high", subject: "Login broken");
        var watcher = await StartAsync();

        _zendesk.Reply(500, Customer, "Still broken for us");
        await watcher.PollAsync(CancellationToken.None);

        var shown = Assert.Single(TakeShown());
        Assert.Equal(NotificationKind.MyTicket, shown.Kind);
        Assert.True(shown.IsTicketNotification); // counted in the statistics
        Assert.Equal("#500 Login broken", shown.Title);
        Assert.Equal("Jane Customer replied: Still broken for us", shown.Body);
        Assert.Null(shown.Context);
    }

    [Fact]
    public async Task Requester_and_organization_come_first()
    {
        _zendesk.Organizations[900] = "Acme Corp";
        var ticket = _zendesk.AddAssigned(510, "high", subject: "Login broken");
        ticket.RequesterId = Customer;
        ticket.OrganizationId = 900;
        var watcher = await StartAsync();

        _zendesk.Reply(510, Customer, "Still broken for us");
        await watcher.PollAsync(CancellationToken.None);

        Assert.Equal("Jane Customer (Acme Corp)\nJane Customer replied: Still broken for us", Assert.Single(TakeShown()).Body);
    }

    [Fact]
    public async Task My_own_changes_never_notify()
    {
        _zendesk.AddAssigned(501, "high");
        var watcher = await StartAsync();

        _zendesk.Reply(501, _zendesk.Me.Id, "On it");
        await watcher.PollAsync(CancellationToken.None);

        Assert.Empty(TakeShown());
    }

    [Fact]
    public async Task All_priorities_of_my_tickets_notify_by_default()
    {
        _zendesk.AddAssigned(502, "low");
        _zendesk.AddAssigned(503, null); // no priority
        var watcher = await StartAsync();

        _zendesk.Reply(502, Customer, "Any news?");
        _zendesk.Reply(503, Customer, "Hello?");
        await watcher.PollAsync(CancellationToken.None);

        Assert.Equal(2, TakeShown().Count);
    }

    [Fact]
    public async Task My_ticket_with_an_unchosen_priority_is_quiet_and_not_reported_later()
    {
        _settings.MyTicketPriorities = ["urgent", "high"];
        _zendesk.AddAssigned(504, "low");
        var watcher = await StartAsync();

        _zendesk.Reply(504, Customer, "Low priority question");
        await watcher.PollAsync(CancellationToken.None);
        Assert.Empty(TakeShown());

        _settings.MyTicketPriorities = [.. Settings.AllPriorities]; // tick Low again
        await watcher.PollAsync(CancellationToken.None);
        Assert.Empty(TakeShown()); // the earlier reply stays unreported

        _zendesk.Reply(504, Customer, "Another one");
        await watcher.PollAsync(CancellationToken.None);
        Assert.Equal("Jane Customer replied: Another one", Assert.Single(TakeShown()).Body);
    }

    // ---------------------------------------------------------------- CC / following

    [Fact]
    public async Task Cc_tickets_notify_for_urgent_and_high_by_default()
    {
        AddCc(600, "urgent");
        AddCc(601, "normal");
        var watcher = await StartAsync();

        _zendesk.Reply(600, Customer, "Site is down");
        _zendesk.Reply(601, Customer, "Small question");
        await watcher.PollAsync(CancellationToken.None);

        var shown = Assert.Single(TakeShown());
        Assert.Equal(600, shown.TicketId);
        Assert.Equal(NotificationKind.CcTicket, shown.Kind);
        Assert.Equal("You're CC", shown.Context);
    }

    [Fact]
    public async Task Cc_ticket_raised_to_urgent_notifies_with_the_priority_change()
    {
        AddCc(602, "low");
        var watcher = await StartAsync();

        _zendesk.Change(602, Colleague, "priority", "low", "urgent");
        await watcher.PollAsync(CancellationToken.None);

        var shown = Assert.Single(TakeShown());
        Assert.Equal("Priority Low (P4) → Urgent (P1) (Sam Colleague)", shown.Body);
    }

    [Fact]
    public async Task Cc_priorities_can_include_normal()
    {
        _settings.CcTicketPriorities = ["urgent", "high", "normal"];
        AddCc(603, "normal");
        var watcher = await StartAsync();

        _zendesk.Reply(603, Customer, "Small question");
        await watcher.PollAsync(CancellationToken.None);

        Assert.Single(TakeShown());
    }

    [Fact]
    public async Task Default_priority_choices()
    {
        var settings = new Settings();
        Assert.Equal(["urgent", "high", "normal", "low"], settings.MyTicketPriorities);
        Assert.Equal(["urgent", "high"], settings.CcTicketPriorities);
        await Task.CompletedTask;
    }
}

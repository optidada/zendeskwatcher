using ZendeskWatcher.SettingsPages;

namespace ZendeskWatcher.Tests;

/// <summary>
/// Groups → Clients: clients you untick don't notify about new unassigned tickets or customer replies on colleagues'
/// tickets, and aren't counted. Everyone is included by default; ticking a client again doesn't replay old tickets.
/// </summary>
public class ClientFilterTests
{
    private const long Contoso = 70, Fabrikam = 71, Customer = 500, NewYork = 21;

    private readonly FakeZendesk _zendesk = new();
    private readonly RecordingNotifier _notifier = new();
    private readonly Settings _settings = new()
    {
        Subdomain = "test",
        WatchedGroups = [new WatchedGroup { Id = 10, Name = "Tier 2" }],
        TrackMyTickets = false,
        HelpColleagues = false
    };

    public ClientFilterTests()
    {
        File.Delete(AppPaths.StateFile);
        _zendesk.Organizations[Contoso] = "Contoso";
        _zendesk.Organizations[Fabrikam] = "Fabrikam";
    }

    private async Task<TicketWatcher> StartAsync()
    {
        var watcher = new TicketWatcher(_settings, new ZendeskClient(_settings, _zendesk), _notifier);
        await watcher.InitializeAsync(CancellationToken.None);
        await watcher.PollAsync(CancellationToken.None); // first look: records what's there
        _notifier.Shown.Clear();
        return watcher;
    }

    private void Exclude(long id, string name) =>
        _settings.SetClientChoice(new ClientChoice { Id = id, Name = name, GroupId = 10, IsMuted = true });

    private void Set(long id, string name, ClientMode mode, DateTimeOffset? until = null, ToastSound? sound = null) =>
        _settings.SetClientChoice(new ClientChoice { Id = id, Name = name, GroupId = 10, Mode = mode, MutedUntil = until, Sound = sound });

    [Fact]
    public void Every_client_is_normal_by_default_and_no_organization_is_its_own_client()
    {
        var now = DateTimeOffset.UtcNow;
        var withOrg = new Ticket { OrganizationId = Contoso, Priority = "normal" };
        var without = new Ticket();
        Assert.Equal(ClientMode.Normal, TicketRules.ClientMode(withOrg, _settings, now));
        Exclude(ClientChoice.NoClientId, ClientChoice.NoClientName);
        Assert.Equal(ClientMode.Muted, TicketRules.ClientMode(without, _settings, now));
        Assert.Equal(ClientMode.Normal, TicketRules.ClientMode(withOrg, _settings, now));
    }

    [Theory]
    [InlineData(ClientMode.Normal, "low", true)]
    [InlineData(ClientMode.Muted, "urgent", false)]
    [InlineData(ClientMode.OnlyUrgent, "urgent", true)]
    [InlineData(ClientMode.OnlyUrgent, "high", true)]
    [InlineData(ClientMode.OnlyUrgent, "normal", false)]
    [InlineData(ClientMode.OnlyUrgent, null, false)]
    [InlineData(ClientMode.Important, "low", true)]
    public void Which_tickets_a_client_choice_lets_through(ClientMode mode, string? priority, bool expected) =>
        Assert.Equal(expected, TicketRules.IsWantedClient(new Ticket { Priority = priority }, mode));

    [Fact]
    public void A_mute_for_a_while_runs_out_by_itself()
    {
        var now = DateTimeOffset.UtcNow;
        var choice = new ClientChoice { Id = Contoso, IsMuted = true, MutedUntil = now.AddHours(1) };
        Assert.Equal(ClientMode.Muted, choice.ModeAt(now));
        Assert.Equal(ClientMode.Normal, choice.ModeAt(now.AddHours(2)));
        Assert.True(choice.IsDefaultAt(now.AddHours(2))); // then it isn't stored any more
        choice.Sound = ToastSound.ChipBlip;
        Assert.False(choice.IsDefaultAt(now.AddHours(2))); // unless it has its own sound
    }

    [Fact]
    public void Setting_a_client_back_to_normal_forgets_it()
    {
        Set(Contoso, "Contoso", ClientMode.Important);
        Set(Contoso, "Contoso", ClientMode.Normal);
        Assert.Empty(_settings.Clients);
    }

    [Fact]
    public void A_label_from_an_organization_field_shows_a_dropdowns_option_name_a_ticked_checkbox_or_text() // groundwork, not used yet
    {
        var plan = new OrganizationField { Key = "plan", Title = "Plan", CustomFieldOptions = [new FieldOption { Name = "Enterprise", Value = "plan_enterprise" }] };
        var vip = new OrganizationField { Key = "vip", Title = "VIP" };
        Organization Org(string key, object? value) => new()
        {
            Name = "Contoso",
            OrganizationFields = new() { [key] = System.Text.Json.JsonSerializer.SerializeToElement(value) }
        };
        Assert.Equal("Enterprise", TicketRules.ClientLabel(Org("plan", "plan_enterprise"), plan));
        Assert.Equal("VIP", TicketRules.ClientLabel(Org("vip", true), vip));
        Assert.Null(TicketRules.ClientLabel(Org("vip", false), vip));
        Assert.Null(TicketRules.ClientLabel(Org("plan", null), plan));
        Assert.Null(TicketRules.ClientLabel(Org("plan", "plan_enterprise"), null)); // no field chosen
    }

    [Fact]
    public async Task Important_clients_notify_outside_my_hours_with_a_star_their_sound_and_stay_until_closed()
    {
        var hour = DateTime.Now.Hour; // working hours that are over right now
        (_settings.MyWorkdayStartHour, _settings.MyWorkdayEndHour) = hour <= 21 ? (hour + 1, hour + 2) : (0, 1);
        _settings.OnlyNotifyDuringMyHours = true;
        Set(Contoso, "Contoso", ClientMode.Important, sound: ToastSound.ChipFanfare);
        var watcher = await StartAsync();

        _zendesk.AddUnassigned(101).OrganizationId = Contoso;
        _zendesk.AddUnassigned(102).OrganizationId = Fabrikam; // normal: quiet outside my hours
        await watcher.PollAsync(CancellationToken.None);

        var shown = Assert.Single(_notifier.Shown);
        Assert.Equal(101, shown.TicketId);
        Assert.True(shown.Important);
        Assert.StartsWith("★ New ticket", shown.FullHeading);
        Assert.Equal(ToastSound.ChipFanfare, _settings.SoundFor(shown));
        Assert.Equal(0, _settings.DisplaySecondsFor(shown)); // until dismissed
    }

    [Fact]
    public async Task Only_urgent_clients_notify_about_urgent_and_high_new_tickets()
    {
        Set(Contoso, "Contoso", ClientMode.OnlyUrgent);
        var watcher = await StartAsync();

        var low = _zendesk.AddUnassigned(101);
        (low.OrganizationId, low.Priority) = (Contoso, "low");
        var high = _zendesk.AddUnassigned(102);
        (high.OrganizationId, high.Priority) = (Contoso, "high");
        await watcher.PollAsync(CancellationToken.None);

        Assert.Equal(102, Assert.Single(_notifier.Shown).TicketId);
    }

    [Fact]
    public void Clients_with_a_choice_come_first_then_ticked_then_muted()
    {
        var groups = new List<WatchedGroup> { new() { Id = 1, Name = "Premium" } };
        var loaded = new Dictionary<long, List<ClientsView.Client>>
        {
            [1] = [new(21, "Adventure", 1), new(22, "Beta", 1), new(23, "Contoso", 1), new(24, "Delta", 1), new(25, "Echo", 1)]
        };
        var choices = new List<ClientChoice>
        {
            new() { Id = 21, Name = "Adventure", IsMuted = true },
            new() { Id = 24, Name = "Delta", Mode = ClientMode.Important },
            new() { Id = 25, Name = "Echo", Sound = ToastSound.ChipBlip },
            new() { Id = 22, Name = "Beta", IsMuted = true, MutedUntil = DateTimeOffset.UtcNow.AddHours(-1) } // ran out: normal again
        };

        var names = ClientsView.Sections(groups, loaded, choices)[0].Clients.Select(c => c.Name);

        Assert.Equal(["Delta", "Echo", "Beta", "Contoso", "Adventure"], names);
    }

    [Fact]
    public void Customized_and_muted_clients_get_their_own_sections_per_group()
    {
        var sections = new List<(WatchedGroup Group, List<ClientsView.Client> Clients)>
        {
            (new WatchedGroup { Id = 1, Name = "Premium" }, [new(21, "Contoso", 3), new(22, "Beta", 1), new(23, "Fabrikam", 2)]),
            (new WatchedGroup { Id = 2, Name = "Tier 2" }, [new(21, "Contoso", 4), new(24, "Delta", 1), new(23, "Fabrikam", 1)])
        };
        var choices = new List<ClientChoice>
        {
            new() { Id = 21, Name = "Contoso", Mode = ClientMode.Important },
            new() { Id = 23, Name = "Fabrikam", IsMuted = true }
        };

        var layout = ClientsView.Layout(sections, choices);

        Assert.Equal(["Premium \u00b7 \u2605 Customized", "Tier 2 \u00b7 \u2605 Customized", "Premium", "Tier 2", "Premium \u00b7 Muted", "Tier 2 \u00b7 Muted"],
            layout.Select(s => s.Title));
        Assert.Equal(new ClientsView.Client(21, "Contoso", 3), layout[0].Clients.Single()); // in each of its groups
        Assert.Equal(new ClientsView.Client(21, "Contoso", 4), layout[1].Clients.Single());
        Assert.Equal(["Beta"], layout[2].Clients.Select(c => c.Name));
        Assert.Equal(["Delta"], layout[3].Clients.Select(c => c.Name));
        Assert.Equal(["Fabrikam"], layout[4].Clients.Select(c => c.Name));
    }

    [Fact]
    public void Muting_keeps_the_clients_choice_and_unmuting_brings_it_back()
    {
        var now = DateTimeOffset.UtcNow;
        var choice = new ClientChoice { Id = Contoso, Mode = ClientMode.Important, Sound = ToastSound.ChipBlip, IsMuted = true };
        Assert.Equal(ClientMode.Muted, choice.ModeAt(now));
        Assert.Equal(("\u2605 Important  \u266a", false), ClientsView.BadgeFor(choice)); // still shown, not in the accent color
        choice.IsMuted = false;
        Assert.Equal(ClientMode.Important, choice.ModeAt(now));
        Assert.Equal(("\u2605 Important  \u266a", true), ClientsView.BadgeFor(choice));
    }

    [Fact]
    public void A_settings_file_from_a_test_build_with_muted_as_the_mode_still_mutes()
    {
        var json = """{ "Clients": [ { "Id": 70, "Name": "Contoso", "Mode": 1 } ] }""";
        var choice = Settings.FromJson(json).Clients.Single();
        choice.Upgrade();
        Assert.True(choice.IsMuted);
        Assert.Equal(ClientMode.Normal, choice.Mode);
    }

    [Fact]
    public void Insights_suggest_muting_a_client_you_never_open()
    {
        var stats = new NotificationStats();
        for (var i = 0; i < 12; i++)
        {
            stats.RecordClient(Contoso, "Contoso", StatOutcome.Shown);
            stats.RecordClient(Fabrikam, "Fabrikam", StatOutcome.Shown);
        }
        for (var i = 0; i < 5; i++)
            stats.RecordClient(Fabrikam, "Fabrikam", StatOutcome.OpenedTicket); // Fabrikam's are useful

        var clients = stats.ClientTotals(DateOnly.FromDateTime(DateTime.Now));
        var tip = Assert.Single(StatsInsights.Clients(clients, _settings));
        Assert.StartsWith("Contoso: 12 notifications and you opened none.", tip.Text);

        Exclude(Contoso, "Contoso"); // already muted: no more suggestion
        Assert.Empty(StatsInsights.Clients(clients, _settings));
        Assert.Empty(stats.Totals(DateOnly.FromDateTime(DateTime.Now))); // client counts never mix into the kind totals
    }

    [Fact]
    public async Task Unticked_clients_new_tickets_neither_notify_nor_count()
    {
        Exclude(Fabrikam, "Fabrikam");
        var watcher = await StartAsync();

        _zendesk.AddUnassigned(101).OrganizationId = Contoso;
        _zendesk.AddUnassigned(102).OrganizationId = Fabrikam;
        await watcher.PollAsync(CancellationToken.None);

        var shown = Assert.Single(_notifier.Shown);
        Assert.Equal(101, shown.TicketId);
        Assert.Equal("Contoso", shown.Client);
        Assert.Equal((Contoso, "Contoso", 10L), (shown.MuteTarget!.Id, shown.MuteTarget.Name, shown.MuteTarget.GroupId));
        Assert.Equal(1, watcher.UnassignedCount);
    }

    [Fact]
    public async Task Ticking_a_client_again_does_not_replay_its_old_tickets()
    {
        Exclude(Fabrikam, "Fabrikam");
        var watcher = await StartAsync();
        _zendesk.AddUnassigned(102).OrganizationId = Fabrikam;
        await watcher.PollAsync(CancellationToken.None);
        Assert.Empty(_notifier.Shown);

        _settings.Clients.Clear();
        await watcher.PollAsync(CancellationToken.None);
        Assert.Empty(_notifier.Shown); // already seen while unticked
        Assert.Equal(1, watcher.UnassignedCount); // but counted again
    }

    [Fact]
    public async Task Customer_replies_on_colleagues_tickets_of_unticked_clients_stay_quiet()
    {
        _settings.NotifyUnassigned = false;
        _settings.HelpColleagues = true;
        _settings.HelpOnlyDuringMyHours = false;
        _settings.HelpRegionDefaultDone = true;
        _settings.WorkDays = []; // every colleague is off work
        _zendesk.Users[Customer] = new User { Id = Customer, Name = "Sam Customer", Role = "end-user" };
        _zendesk.Users[NewYork] = new User { Id = NewYork, Name = "Nia NewYork", Role = "agent", IanaTimeZone = "America/New_York" };
        _zendesk.AddAssigned(301, "urgent", assignee: NewYork).OrganizationId = Contoso;
        _zendesk.AddAssigned(302, "urgent", assignee: NewYork).OrganizationId = Fabrikam;
        Exclude(Fabrikam, "Fabrikam");
        var watcher = await StartAsync();

        _zendesk.Reply(301, Customer, "Any news?");
        _zendesk.Reply(302, Customer, "Any news?");
        await watcher.PollAsync(CancellationToken.None);

        var shown = Assert.Single(_notifier.Shown, n => n.Kind == NotificationKind.HelpColleague);
        Assert.Equal(301, shown.TicketId);
        Assert.Equal(Contoso, shown.MuteTarget?.Id);
    }

    [Fact]
    public async Task A_groups_clients_come_from_its_tickets_most_tickets_first()
    {
        _zendesk.AddUnassigned(101).OrganizationId = Contoso;
        _zendesk.AddUnassigned(102).OrganizationId = Fabrikam;
        _zendesk.AddUnassigned(103).OrganizationId = Fabrikam;
        _zendesk.AddUnassigned(104); // no organization
        _zendesk.AddUnassigned(105, group: 11).OrganizationId = Contoso; // another group

        var clients = await new ZendeskClient(_settings, _zendesk).GetGroupClientsAsync(10, CancellationToken.None);

        Assert.Equal([(Fabrikam, "Fabrikam", 2), (ClientChoice.NoClientId, ClientChoice.NoClientName, 1), (Contoso, "Contoso", 1)], clients);
    }

    [Fact]
    public void Clients_list_shows_ticked_clients_first_and_muted_ones_without_recent_tickets_under_their_group()
    {
        var groups = new List<WatchedGroup> { new() { Id = 1, Name = "Premium" }, new() { Id = 2, Name = "Tier 2" } };
        var loaded = new Dictionary<long, List<ClientsView.Client>>
        {
            [1] = [new(ClientChoice.NoClientId, ClientChoice.NoClientName, 3), new(21, "Contoso", 9)],
            [2] = [new(22, "Northwind", 2)]
        };
        var sections = ClientsView.Sections(groups, loaded,
            [new ClientChoice { Id = 30, Name = "Old Client", GroupId = 2, IsMuted = true }, new ClientChoice { Id = 31, Name = "Gone Group", GroupId = 99, IsMuted = true }]);

        Assert.Equal(["Contoso", "(No client)", "Gone Group"], sections[0].Clients.Select(c => c.Name)); // ticked first, by name, "(No client)" last; then muted
        Assert.Equal(["Northwind", "Old Client"], sections[1].Clients.Select(c => c.Name));
    }
}

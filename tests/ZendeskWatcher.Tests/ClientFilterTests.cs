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
    public void One_section_per_group_open_when_it_has_changes()
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

        Assert.Equal(["Premium", "Tier 2"], layout.Select(s => s.Title));
        Assert.All(layout, s => Assert.True(s.Changed));                              // Contoso important, Fabrikam muted: open
        Assert.Contains(new ClientsView.Client(21, "Contoso", 4), layout[1].Clients); // in each of its groups

        var plain = ClientsView.Layout(sections, []);
        Assert.All(plain, s => Assert.False(s.Changed));                              // nothing changed: folded
    }

    [Fact]
    public void Worked_with_shows_only_clients_I_had_tickets_from_in_that_group_and_combines_with_the_name()
    {
        const long Me = 1;
        List<ClientHandler> mine = [new(Me, "Me", 2)], theirs = [new(8, "Alex Kim", 4)];
        var sections = new List<(WatchedGroup Group, List<ClientsView.Client> Clients)>
        {
            (new WatchedGroup { Id = 1, Name = "Premium" }, [new(21, "Contoso", 3, Handlers: mine), new(22, "Beta", 1, Handlers: theirs)]),
            (new WatchedGroup { Id = 2, Name = "Tier 2" }, [new(21, "Contoso", 1, Handlers: theirs)]) // not mine in this group
        };
        bool Worked(ClientsView.Client c) => c.Handlers?.Any(h => h.Id == Me) == true;

        var worked = ClientsView.Layout(sections, [], shows: Worked);
        Assert.Equal(["Premium"], worked.Select(s => s.Title));
        Assert.Equal(["Contoso"], worked[0].Clients.Select(c => c.Name));

        var others = ClientsView.Layout(sections, [], shows: c => !Worked(c));
        Assert.Equal(["Beta"], others[0].Clients.Select(c => c.Name));
        Assert.Equal(["Contoso"], others[1].Clients.Select(c => c.Name)); // someone else's in Tier 2

        Assert.Empty(ClientsView.Layout(sections, [], "beta", Worked)); // both filters at once
    }

    [Fact]
    public void The_filter_shows_only_matching_clients_and_their_groups()
    {
        var sections = new List<(WatchedGroup Group, List<ClientsView.Client> Clients)>
        {
            (new WatchedGroup { Id = 1, Name = "Premium" }, [new(21, "Contoso", 3), new(22, "Beta", 1)]),
            (new WatchedGroup { Id = 2, Name = "Tier 2" }, [new(24, "Delta", 1), new(25, "Contoso Labs", 1)])
        };

        var contoso = ClientsView.Layout(sections, [], "conTOSO");
        Assert.Equal(["Premium", "Tier 2"], contoso.Select(s => s.Title));
        Assert.Equal(["Contoso"], contoso[0].Clients.Select(c => c.Name));
        Assert.Equal(["Contoso Labs"], contoso[1].Clients.Select(c => c.Name));

        var beta = ClientsView.Layout(sections, [], "bet");
        Assert.Equal(["Premium"], beta.Select(s => s.Title));                    // a group without a match is left out

        Assert.Empty(ClientsView.Layout(sections, [], "zzz"));
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

        Assert.Equal([(Fabrikam, "Fabrikam", 2), (ClientChoice.NoClientId, ClientChoice.NoClientName, 1), (Contoso, "Contoso", 1)], clients.Select(c => (c.Id, c.Name, c.Tickets)));
    }

    [Fact]
    public async Task A_clients_handlers_are_who_its_tickets_in_the_group_are_assigned_to_most_first()
    {
        _zendesk.Users[7] = new User { Id = 7, Name = "Maria Svensson", Role = "agent" };
        _zendesk.Users[8] = new User { Id = 8, Name = "Alex Kim", Role = "agent" };
        _zendesk.AddUnassigned(101).OrganizationId = Contoso; // unassigned: not counted
        foreach (var (id, to) in new[] { (102L, 7L), (103L, 7L), (104L, 8L) })
        {
            var t = _zendesk.AddUnassigned(id);
            t.OrganizationId = Contoso;
            t.AssigneeId = to;
        }
        var elsewhere = _zendesk.AddUnassigned(105, group: 11); // another group: not counted
        elsewhere.OrganizationId = Contoso;
        elsewhere.AssigneeId = 8;

        var clients = await new ZendeskClient(_settings, _zendesk).GetGroupClientsAsync(10, CancellationToken.None);

        Assert.Equal([new ClientHandler(7, "Maria Svensson", 2), new ClientHandler(8, "Alex Kim", 1)], clients.Single(c => c.Id == Contoso).Handlers);
    }

    [Fact]
    public async Task The_next_load_reads_only_what_changed_and_drops_tickets_past_90_days()
    {
        var now = DateTimeOffset.UtcNow;
        _zendesk.Users[7] = new User { Id = 7, Name = "Maria Svensson", Role = "agent" };
        _zendesk.Users[8] = new User { Id = 8, Name = "Alex Kim", Role = "agent" };
        var previous = new GroupDigest(now.AddHours(-1),
        [
            new(1, Contoso, 7, now.AddDays(-10)),
            new(2, Contoso, 7, now.AddDays(-100)), // past 90 days now: dropped
            new(3, Fabrikam, 8, now.AddDays(-5))
        ]);
        var unchanged = _zendesk.AddUnassigned(1); // not updated since: not read again, kept from the digest
        unchanged.UpdatedAt = now.AddDays(-2);
        var reassigned = _zendesk.AddUnassigned(3); // Alex → Maria an hour ago... just now
        (reassigned.OrganizationId, reassigned.AssigneeId, reassigned.CreatedAt, reassigned.UpdatedAt) = (Fabrikam, 7, now.AddDays(-5), now);
        var created = _zendesk.AddUnassigned(4); // a new ticket from Contoso
        (created.OrganizationId, created.UpdatedAt) = (Contoso, now);
        var known = new Dictionary<long, (string Name, List<string> Products)> { [Fabrikam] = ("Fabrikam (saved)", ["CMS"]) };

        var (clients, digest) = await new ZendeskClient(_settings, _zendesk).GetGroupClientsAsync(10, previous, known, CancellationToken.None);

        Assert.Equal([1L, 3, 4], digest.Tickets.Select(t => t.Id).Order());
        var contoso = clients.Single(c => c.Id == Contoso);
        Assert.Equal(2, contoso.Tickets); // 1 (kept) + 4 (new); 2 dropped
        Assert.Equal([new ClientHandler(7, "Maria Svensson", 1)], contoso.Handlers);
        var fabrikam = clients.Single(c => c.Id == Fabrikam);
        Assert.Equal("Fabrikam (saved)", fabrikam.Name); // not looked up again
        Assert.Equal(["CMS"], fabrikam.Products);
        Assert.Equal([new ClientHandler(7, "Maria Svensson", 1)], fabrikam.Handlers); // reassigned
        Assert.Contains(_zendesk.Requested, u => u.Contains("updated%3E")); // only what changed
        Assert.DoesNotContain(_zendesk.Requested, u => u.Contains("organizations/show_many") && u.Contains(Fabrikam.ToString()));
    }

    [Fact]
    public void Usually_line_names_the_top_three_short_and_adds_you_even_when_not_among_them()
    {
        List<ClientHandler> handlers = [new(7, "Maria Svensson", 12), new(8, "Alex Kim", 5), new(9, "Sam Lee", 4), new(10, "Kai Berg", 3), new(1, "Me Myself", 2)];

        Assert.Equal("Usually: Maria S. 12 · Alex K. 5 · Sam L. 4 · you 2", ClientsView.UsuallyLine(handlers, me: 1));
        Assert.Equal("Usually: you 12 · Alex K. 5 · Sam L. 4", ClientsView.UsuallyLine(handlers, me: 7));
        Assert.Equal("Usually: Maria S. 12 · Alex K. 5 · Sam L. 4", ClientsView.UsuallyLine(handlers, me: null));
        Assert.Null(ClientsView.UsuallyLine([], me: 1));
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
        List<ClientChoice> choices = [new() { Id = 30, Name = "Old Client", GroupId = 2, IsMuted = true }, new() { Id = 31, Name = "Gone Group", GroupId = 99, IsMuted = true }];
        var sections = ClientsView.Sections(groups, loaded, choices);

        Assert.Equal(["Contoso", "(No client)"], sections[0].Clients.Select(c => c.Name)); // ticked first, by name, "(No client)" last
        Assert.Equal(["Northwind", "Old Client"], sections[1].Clients.Select(c => c.Name));
        // Muted in a group no longer monitored: not under some other group, but in that group's own section, last.
        Assert.Equal(3, sections.Count);
        Assert.Equal("Another group", sections[2].Group.Name); // name unknown
        Assert.Equal(["Gone Group"], sections[2].Clients.Select(c => c.Name));
    }

    [Fact]
    public void Groups_no_longer_monitored_show_after_the_others_with_their_old_choices_greyed()
    {
        var groups = new List<WatchedGroup> { new() { Id = 1, Name = "Tier 2" } };
        var loaded = new Dictionary<long, List<ClientsView.Client>> { [1] = [new(21, "Contoso", 9)] };
        List<ClientChoice> choices =
        [
            new() { Id = 21, Name = "Contoso", GroupId = 7, IsMuted = true },                // has tickets in Tier 2: shown there
            new() { Id = 31, Name = "Stewart-MacDonald", GroupId = 7, IsMuted = true },
            new() { Id = 32, Name = "Nordiska Kompaniet AB", GroupId = 7, IsMuted = true },
            new() { Id = 33, Name = "Initech", GroupId = 8, Mode = ClientMode.Important }
        ];
        string? Name(long id) => id switch { 7 => "Problem Management", 8 => "Billing", _ => null };

        var layout = ClientsView.Layout(ClientsView.Sections(groups, loaded, choices, Name), choices);

        Assert.Equal(["Tier 2", "Billing", "Problem Management"], layout.Select(s => s.Title));
        Assert.Equal([false, true, true], layout.Select(s => s.Old));
        Assert.Equal(["Contoso"], layout[0].Clients.Select(c => c.Name));
        Assert.Equal(["Nordiska Kompaniet AB", "Stewart-MacDonald"], layout[2].Clients.Select(c => c.Name));
    }

    [Fact]
    public void A_loaded_group_without_clients_still_shows_unless_a_filter_is_on()
    {
        var groups = new List<WatchedGroup> { new() { Id = 1, Name = "Experimentation" }, new() { Id = 2, Name = "Tier 2" }, new() { Id = 3, Name = "Not loaded" } };
        var loaded = new Dictionary<long, List<ClientsView.Client>> { [1] = [], [2] = [new(22, "Northwind", 2)] };
        var sections = ClientsView.Sections(groups, loaded, []);

        Assert.Equal(["Experimentation", "Tier 2"], ClientsView.Layout(sections, [], keepEmpty: loaded.Keys.ToHashSet()).Select(s => s.Title));
        Assert.Equal(["Tier 2"], ClientsView.Layout(sections, [], "north").Select(s => s.Title)); // filtering: only groups with matches
    }
    [Fact]
    public void Products_come_from_the_comma_separated_field()
    {
        Assert.Equal(["Content Management PaaS", "Configured Commerce"], Organization.ParseProducts(" Content Management PaaS,Configured Commerce, "));
        Assert.Empty(Organization.ParseProducts(null));
        Assert.Empty(Organization.ParseProducts("  "));
    }

    [Fact]
    public void Filter_finds_clients_by_name_or_product_and_says_which_product()
    {
        var byProduct = new ClientsView.Client(1, "Initech", 3, ["Configured Commerce", "Content Management PaaS"]);
        var byName = new ClientsView.Client(2, "Commerce Partners", 1);
        var neither = new ClientsView.Client(3, "Contoso", 1, ["Web Experimentation"]);

        Assert.True(ClientsView.Matches(byProduct, "commerce"));
        Assert.Equal(["Configured Commerce"], ClientsView.MatchedProducts(byProduct, "commerce"));
        Assert.True(ClientsView.Matches(byName, "commerce"));
        Assert.False(ClientsView.Matches(neither, "commerce"));
        Assert.True(ClientsView.Matches(neither, "")); // no filter: everyone
    }
}

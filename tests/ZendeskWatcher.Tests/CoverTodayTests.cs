namespace ZendeskWatcher.Tests;

/// <summary>
/// "Cover today?": a colleague in my region who hasn't been active for 3 hours can be covered until midnight, as if
/// ticked on Colleagues.
/// </summary>
public class CoverTodayTests
{
    private const long Customer = 500, Berlin = 20, NewYork = 21;

    private readonly FakeZendesk _zendesk = new();
    private readonly RecordingNotifier _notifier = new();
    private readonly Settings _settings = new()
    {
        Subdomain = "covertoday", // its own key in the shared group cache (other tests use "test")
        WatchedGroups = [new WatchedGroup { Id = 10, Name = "Tier 2" }],
        HelpSameAsMonitored = true,
        TrackMyTickets = false,
        NotifyUnassigned = false,
        HelpColleagues = true,
        HelpOnlyDuringMyHours = false,
        HelpPriorities = ["urgent", "high", "normal", "low"]
    };

    public CoverTodayTests()
    {
        File.Delete(AppPaths.StateFile);
        _zendesk.Me.IanaTimeZone = "Europe/Stockholm"; // my region: Europe & Africa
        _zendesk.Users[Customer] = new User { Id = Customer, Name = "Sam Customer", Role = "end-user" };
        _zendesk.Users[Berlin] = new User { Id = Berlin, Name = "Berit Berlin", Role = "agent", IanaTimeZone = "Europe/Berlin" };
        _zendesk.Users[NewYork] = new User { Id = NewYork, Name = "Nia NewYork", Role = "agent", IanaTimeZone = "America/New_York" };
        _zendesk.GroupMembers[10] = [Berlin, NewYork];
        _zendesk.AddAssigned(301, "normal", assignee: Berlin);
        _zendesk.AddAssigned(302, "normal", assignee: NewYork);
    }

    private TicketWatcher Watcher() => new(_settings, new ZendeskClient(_settings, _zendesk), _notifier);

    [Fact]
    public void Covered_today_counts_as_ticked_until_midnight()
    {
        var region = TimeRegion.EuropeAfrica;
        Assert.True(_settings.HelpRegionDefaultDone != true); // my own region isn't covered to begin with
        Assert.False(_settings.CoversColleague(Berlin, region, region));

        _settings.CoverToday(Berlin, "Berit Berlin");
        Assert.True(_settings.CoversColleague(Berlin, region, region));
        Assert.True(_settings.HelpCoveredToday.Single().Until > DateTimeOffset.UtcNow);

        _settings.HelpCoveredToday[0].Until = DateTimeOffset.UtcNow.AddMinutes(-1); // midnight has passed
        Assert.False(_settings.CoversColleague(Berlin, region, region));
    }

    [Fact]
    public void Always_ticks_them_for_good()
    {
        var region = TimeRegion.EuropeAfrica;
        _settings.CoverToday(Berlin, "Berit Berlin");
        _settings.CoverAlways(Berlin, "Berit Berlin");
        Assert.Empty(_settings.HelpCoveredToday);
        Assert.True(_settings.CoversColleague(Berlin, region, region)); // ticked in my (unticked) region
        Assert.Contains(_settings.HelpIncludedAssignees, c => c.Id == Berlin);
    }

    [Fact]
    public async Task A_colleague_covered_today_gets_their_clients_replies_to_you()
    {
        _settings.WorkDays = []; // everyone is off work: only coverage decides
        _settings.CoverToday(Berlin, "Berit Berlin");
        var watcher = Watcher();
        await watcher.InitializeAsync(CancellationToken.None);
        await watcher.PollAsync(CancellationToken.None);

        _zendesk.Reply(301, Customer, "Any news?");
        await watcher.PollAsync(CancellationToken.None);
        Assert.Contains(_notifier.Shown, n => n.Kind == NotificationKind.HelpColleague && n.TicketId == 301);
    }

    [Fact]
    public async Task Once_a_day_it_asks_about_an_idle_colleague_in_my_region()
    {
        // "3 hours into their working day" can't be true yet before 03:00 in Berlin (and Stockholm, my zone): the
        // colleague's clock counts, not this machine's (GitHub's runs on UTC, an hour behind; it failed there at 23:25).
        var berlin = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, TicketRules.ResolveTimeZone("Europe/Berlin")!);
        if (berlin.Hour < 3)
            return;
        // Everyone works around the clock, every day: 3 hours into the day has passed for all.
        _settings.MyWorkdayStartHour = _settings.WorkdayStartHour = 0;
        _settings.MyWorkdayEndHour = _settings.WorkdayEndHour = 24;
        _settings.MyWorkDays = _settings.WorkDays = [.. Enum.GetValues<DayOfWeek>()];
        var watcher = Watcher();
        await watcher.InitializeAsync(CancellationToken.None);
        await watcher.PollAsync(CancellationToken.None);
        await watcher.MissingCheck; // runs beside the check

        var asked = _notifier.Shown.Where(n => n.Action?.StartsWith(NotificationActions.CoverPrefix) == true).ToList();
        var question = Assert.Single(asked); // Berlin (my region, nothing for 3 h); not New York (another region)
        Assert.Equal(NotificationActions.Cover(Berlin, "Berit Berlin"), question.Action);
        Assert.Contains("Berit Berlin hasn't been active in Zendesk today", question.Title);
        Assert.Equal(("Always", NotificationActions.CoverAlways(Berlin, "Berit Berlin")), question.SecondChoice);

        _notifier.Shown.Clear();
        await watcher.PollAsync(CancellationToken.None);
        await watcher.MissingCheck;
        Assert.DoesNotContain(_notifier.Shown, n => n.Action?.StartsWith(NotificationActions.CoverPrefix) == true); // once a day
    }
}

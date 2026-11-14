namespace ZendeskWatcher.Tests;

public class TicketRulesTests
{
    private static readonly WatchedGroup Tier2 = new() { Id = 10, Name = "Tier 2" };
    private static readonly WatchedGroup NoGroup = new() { Id = WatchedGroup.NoGroupId, Name = "No group" };

    private static readonly WatchedGroup Premium = new() { Id = 11, Name = "Premium" };
    private static readonly DateTimeOffset Now = new(2026, 11, 13, 9, 0, 0, TimeSpan.Zero);

    private static WatchSnapshot Snapshot(int tier2, int premium, (int, int, int)? mine, int? cc, bool ccAtLeast = false) =>
        new(DateTime.Now, [new(Tier2, tier2), new(Premium, premium)], tier2 + premium > 0 ? Now.AddMinutes(-130) : null, mine, cc, ccAtLeast);

    [Theory]
    [InlineData("end-user", "sam@contoso.com", false, true)]       // a customer
    [InlineData("end-user", "kim@acme.com", false, false)]         // someone from your company, CC'd as end-user
    [InlineData("end-user", "kim@ACME.com ", false, false)]        // the domain in any case
    [InlineData("end-user", "kim@acme.com", true, true)]           // ... unless they opened the ticket: then they're the customer
    [InlineData("agent", "pat@acme.com", false, false)]            // agents never
    [InlineData("end-user", null, false, true)]                    // no email: a customer
    public void Who_counts_as_the_customer(string role, string? email, bool isRequester, bool expected)
    {
        var author = new User { Id = 7, Role = role, Email = email };
        var ticket = new Ticket { Id = 1, RequesterId = isRequester ? 7 : 99 };
        Assert.Equal(expected, TicketRules.IsCustomer(author, ticket, "me@acme.com"));
    }

    [Fact]
    public void Summary_text_is_short_rows_quick_to_scan()
    {
        var text = TicketRules.SummaryText(Snapshot(3, 2, (3, 4, 1), 2), [Tier2, Premium], ["urgent", "high"], Now);

        Assert.Equal(
            "**5 unassigned**, oldest 2 h 10 min ago\n" +
            "3 in Tier 2 · 2 in Premium\n" +
            "Your tickets: 3 open · 4 pending · 1 on hold\n" +
            "CC / following: 2 open\n" +
            "Colleagues off work: replies on Urgent and High", text);
    }

    [Fact]
    public void Summary_text_leaves_out_what_does_not_apply()
    {
        Assert.Equal("No unassigned tickets in your groups\nYour tickets: 1 open",
            TicketRules.SummaryText(Snapshot(0, 0, (1, 0, 0), 0), [Tier2, Premium], null, Now));
        Assert.Equal("**2 unassigned**, oldest 2 h 10 min ago\nAll in Premium\n" +
                     "Your tickets: none open\nCC / following: at least 60 open",
            TicketRules.SummaryText(Snapshot(0, 2, (0, 0, 0), 60, ccAtLeast: true), [Tier2, Premium], null, Now));
        Assert.Equal("No groups watched yet: Settings → Groups\nCC / following: 1 open",
            TicketRules.SummaryText(Snapshot(0, 0, null, 1) with { Groups = [] }, [], null, Now));
    }

    private static Ticket T(string status = "new", long? assignee = null, long? group = 10) =>
        new() { Id = 1, Status = status, AssigneeId = assignee, GroupId = group };

    // ---------------------------------------------------------------- unassigned

    [Fact]
    public void Unassigned_open_ticket_in_the_group_counts()
        => Assert.True(TicketRules.IsReallyUnassigned(T(), Tier2));

    [Theory]
    [InlineData("open")]
    [InlineData("pending")]
    [InlineData("hold")]
    public void Any_open_status_counts(string status)
        => Assert.True(TicketRules.IsReallyUnassigned(T(status), Tier2));

    [Fact]
    public void Assigned_ticket_does_not_count_even_if_search_said_so()
        => Assert.False(TicketRules.IsReallyUnassigned(T(assignee: 42), Tier2));

    [Theory]
    [InlineData("solved")]
    [InlineData("closed")]
    public void Solved_or_closed_does_not_count(string status)
        => Assert.False(TicketRules.IsReallyUnassigned(T(status), Tier2));

    [Fact]
    public void Ticket_moved_to_another_group_does_not_count()
        => Assert.False(TicketRules.IsReallyUnassigned(T(group: 99), Tier2));

    [Fact]
    public void No_group_means_tickets_without_a_group()
    {
        Assert.True(TicketRules.IsReallyUnassigned(T(group: null), NoGroup));
        Assert.False(TicketRules.IsReallyUnassigned(T(group: 10), NoGroup));
    }

    // ---------------------------------------------------------------- working hours (8-17)

    [Theory]
    [InlineData("2026-11-09 07:59", true)]  // Monday, before start
    [InlineData("2026-11-09 08:00", false)] // start is inclusive
    [InlineData("2026-11-09 16:59", false)]
    [InlineData("2026-11-09 17:00", true)]  // end is exclusive
    [InlineData("2026-11-14 12:00", true)]  // Saturday
    [InlineData("2026-11-15 12:00", true)]  // Sunday
    public void Outside_hours(string local, bool expected)
        => Assert.Equal(expected, TicketRules.IsOutsideHours(DateTime.Parse(local), 8, 17));

    [Theory]
    [InlineData("2026-11-09 12:00", true)]  // Monday: not a working day here
    [InlineData("2026-11-10 12:00", false)] // Tuesday
    [InlineData("2026-11-14 12:00", false)] // Saturday is
    [InlineData("2026-11-14 18:00", true)]  // but not after hours
    public void Weekend_work_days(string local, bool expected) => Assert.Equal(expected, TicketRules.IsOutsideHours(
        DateTime.Parse(local), 8, 17, [DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday]));

    // ---------------------------------------------------------------- help colleagues priorities

    [Theory]
    [InlineData("urgent", true)]
    [InlineData("high", true)]
    [InlineData("normal", false)]
    [InlineData(null, false)] // no priority counts as normal
    [InlineData("", false)]
    public void Help_priority_filter(string? priority, bool expected)
        => Assert.Equal(expected, TicketRules.MatchesPriority(priority, ["urgent", "high"]));

    [Fact]
    public void No_priority_matches_when_normal_is_chosen()
        => Assert.True(TicketRules.MatchesPriority(null, ["normal"]));

    // ---------------------------------------------------------------- CC / follow

    [Fact]
    public void Involvement_cc_beats_follow()
    {
        Assert.Equal("You're CC", TicketRules.Involvement(new Ticket { EmailCcIds = [7], FollowerIds = [7] }, 7));
        Assert.Equal("You're CC", TicketRules.Involvement(new Ticket { CollaboratorIds = [7] }, 7));
        Assert.Equal("You follow", TicketRules.Involvement(new Ticket { FollowerIds = [7] }, 7));
        Assert.Null(TicketRules.Involvement(new Ticket { EmailCcIds = [8] }, 7));
    }

    // ---------------------------------------------------------------- snooze

    [Fact]
    public void Snoozed_new_ticket_is_dropped_once_someone_takes_it()
    {
        Assert.True(TicketRules.StillNeedsAttention(NotificationKind.NewTicket, T()));
        Assert.False(TicketRules.StillNeedsAttention(NotificationKind.NewTicket, T(assignee: 42)));
        Assert.False(TicketRules.StillNeedsAttention(NotificationKind.NewTicket, T("solved")));
    }

    [Theory]
    [InlineData(NotificationKind.MyTicket)]
    [InlineData(NotificationKind.HelpColleague)]
    public void Snoozed_other_ticket_is_kept_until_solved(NotificationKind kind)
    {
        Assert.True(TicketRules.StillNeedsAttention(kind, T("pending", assignee: 42)));
        Assert.False(TicketRules.StillNeedsAttention(kind, T("solved", assignee: 42)));
    }

    [Fact]
    public void Snoozed_ticket_that_disappeared_is_dropped()
        => Assert.False(TicketRules.StillNeedsAttention(NotificationKind.MyTicket, null));

    // ---------------------------------------------------------------- texts

    [Theory]
    [InlineData(5, "Good morning, Daniel")]
    [InlineData(9, "Good morning, Daniel")]
    [InlineData(10, "Good day, Daniel")]
    [InlineData(17, "Good day, Daniel")]
    [InlineData(18, "Good evening, Daniel")]
    [InlineData(4, "Good evening, Daniel")]
    public void Greeting_by_time_of_day(int hour, string expected)
        => Assert.Equal(expected, TicketRules.Greeting(new DateTime(2026, 11, 12, hour, 0, 0), "Daniel Dahlin"));

    [Fact]
    public void Greeting_without_a_name()
        => Assert.Equal("Good day", TicketRules.Greeting(new DateTime(2026, 11, 12, 12, 0, 0), null));

    [Theory]
    [InlineData("pending", "Pending")]
    [InlineData("hold", "On-hold")]
    [InlineData("OPEN", "Open")]
    [InlineData(null, null)]
    [InlineData("", null)]
    public void Status_label(string? status, string? expected)
        => Assert.Equal(expected, TicketRules.StatusLabel(status));

    [Fact]
    public void Time_zones_resolve_by_iana_or_windows_id()
    {
        Assert.NotNull(TicketRules.ResolveTimeZone("Europe/Stockholm"));
        Assert.NotNull(TicketRules.ResolveTimeZone("W. Europe Standard Time"));
        Assert.Null(TicketRules.ResolveTimeZone("Stockholm")); // Zendesk's display name isn't an id
        Assert.Null(TicketRules.ResolveTimeZone(null));
    }

    // ---------------------------------------------------------------- regions

    [Theory]
    [InlineData("Europe/Stockholm", TimeRegion.EuropeAfrica)]
    [InlineData("Africa/Cairo", TimeRegion.EuropeAfrica)]
    [InlineData("America/New_York", TimeRegion.Americas)]
    [InlineData("US/Pacific", TimeRegion.Americas)]
    [InlineData("Asia/Kolkata", TimeRegion.AsiaPacific)]
    [InlineData("Australia/Sydney", TimeRegion.AsiaPacific)]
    [InlineData("Etc/UTC", TimeRegion.Other)]
    [InlineData(null, TimeRegion.NoTimeZone)]
    public void Region_of_time_zone(string? zone, TimeRegion expected)
        => Assert.Equal(expected, TimeRegions.Of(zone));
}

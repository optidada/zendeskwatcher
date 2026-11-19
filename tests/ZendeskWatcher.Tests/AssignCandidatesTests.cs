using ZendeskWatcher.Tracking;
using ZendeskWatcher.Zendesk;

namespace ZendeskWatcher.Tests;

/// <summary>"▾" next to Take it: who could take a new ticket.</summary>
public class AssignCandidatesTests
{
    private const long Busy = 2, Free = 3, Idle = 4, NoZone = 5;

    [Fact]
    public async Task Least_busy_first_among_those_in_working_hours_and_active()
    {
        File.Delete(AppPaths.StateFile);
        GroupWorkloadCache.Clear();
        var settings = new Settings
        {
            Subdomain = "test", WatchedGroups = [new WatchedGroup { Id = 10, Name = "Tier 2" }],
            WorkdayStartHour = 0, WorkdayEndHour = 24, WorkDays = [.. Enum.GetValues<DayOfWeek>()] // everyone is in working hours
        };
        var zendesk = new FakeZendesk();
        zendesk.Me.IanaTimeZone = "Europe/Stockholm";
        zendesk.Users[zendesk.Me.Id] = zendesk.Me;
        zendesk.Users[Busy] = new User { Id = Busy, Name = "Bea Busy", Role = "agent", IanaTimeZone = "Europe/Berlin" };
        zendesk.Users[Free] = new User { Id = Free, Name = "Fred Free", Role = "agent", IanaTimeZone = "Europe/Berlin" };
        zendesk.Users[Idle] = new User { Id = Idle, Name = "Ida Idle", Role = "agent", IanaTimeZone = "Europe/Berlin" };
        zendesk.Users[NoZone] = new User { Id = NoZone, Name = "Nils NoZone", Role = "agent" };
        zendesk.GroupMembers[10] = [zendesk.Me.Id, Busy, Free, Idle, NoZone];
        for (var i = 0; i < 3; i++)
            zendesk.AddAssigned(200 + i, i == 0 ? "urgent" : "normal", assignee: Busy).GroupId = 10;
        zendesk.AddAssigned(210, "normal", assignee: Free).GroupId = 10;
        zendesk.AddAssigned(220, "normal", assignee: Idle).GroupId = 10; // nothing done in Zendesk lately
        zendesk.Reply(200, Busy, "On it");
        zendesk.Reply(210, Free, "Looking");
        zendesk.AddUnassigned(500).OrganizationId = 4711; // the client's new ticket
        zendesk.Tickets[201].OrganizationId = 4711; // Bea has two of the client's tickets in the group
        zendesk.Tickets[202].OrganizationId = 4711;

        var watcher = new TicketWatcher(settings, new ZendeskClient(settings, zendesk), new RecordingNotifier());
        await watcher.InitializeAsync(CancellationToken.None);
        var (candidates, note) = await watcher.FindCandidatesAsync(500, CancellationToken.None);

        Assert.Equal(["Fred Free", "Bea Busy"], candidates.Select(c => c.Name)); // not me, not idle, not without a time zone
        Assert.Null(note);
        Assert.Equal([0, 2], candidates.Select(c => c.ClientTickets)); // Bea knows the client, but 3 open (one P1) is clearly busier than Fred's 1
        Assert.Equal((3, 1), (candidates[1].Open, candidates[1].UrgentOrHigh));
        Assert.NotNull(candidates[0].LastActive);
    }

    [Fact]
    public void Knowing_the_client_counts_up_to_three_open_tickets()
    {
        var two = new WorkloadRow(1, "Two Open", 2, 0, 0, 0, 0, false);   // score 4
        var one = new WorkloadRow(2, "One Open", 1, 0, 0, 0, 0, false);   // score 2
        Assert.Equal(1, AssignCandidates.Rank(two, 3));   // 4 - 3: knows the client, first
        Assert.Equal(2, AssignCandidates.Rank(one, 0));
        Assert.Equal(4 - AssignCandidates.MaxClientBonus, AssignCandidates.Rank(two, 50)); // capped

        // Also who's looked at: of 9 people, the one who knows the client is checked though 8 are less busy.
        var rows = Enumerable.Range(1, 8).Select(i => new WorkloadRow(i, $"P{i}", 1, 0, 0, 0, 0, false))
            .Append(new WorkloadRow(99, "Knows Client", 3, 0, 0, 0, 0, false)).ToList();
        Assert.DoesNotContain(rows[^1], AssignCandidates.ToCheck(rows, _ => false));
        Assert.Equal(99, AssignCandidates.ToCheck(rows, _ => false, new Dictionary<long, int> { [99] = 6 })[0].AssigneeId);
    }

    [Fact]
    public async Task Only_people_who_work_the_group_ranked_by_their_tickets_in_every_group()
    {
        const long Premium = 6;
        File.Delete(AppPaths.StateFile);
        GroupWorkloadCache.Clear();
        var settings = new Settings
        {
            Subdomain = "test", WatchedGroups = [new WatchedGroup { Id = 10, Name = "Tier 2" }],
            WorkdayStartHour = 0, WorkdayEndHour = 24, WorkDays = [.. Enum.GetValues<DayOfWeek>()]
        };
        var zendesk = new FakeZendesk();
        zendesk.Users[zendesk.Me.Id] = zendesk.Me;
        zendesk.Users[Busy] = new User { Id = Busy, Name = "Bea Busy", Role = "agent", IanaTimeZone = "Europe/Berlin" };
        zendesk.Users[Free] = new User { Id = Free, Name = "Fred Free", Role = "agent", IanaTimeZone = "Europe/Berlin" };
        zendesk.Users[Idle] = new User { Id = Idle, Name = "Ida Manager", Role = "admin", IanaTimeZone = "Europe/Berlin" };
        zendesk.Users[Premium] = new User { Id = Premium, Name = "Pia Premium", Role = "agent", IanaTimeZone = "Europe/Berlin" };
        zendesk.GroupMembers[10] = [zendesk.Me.Id, Busy, Free, Idle, Premium];
        zendesk.AddAssigned(200, "normal", assignee: Free).GroupId = 10; // Fred: 2 in Tier 2…
        zendesk.AddAssigned(201, "normal", assignee: Free).GroupId = 10;
        zendesk.AddAssigned(210, "normal", assignee: Busy).GroupId = 10; // …Bea: 1 in Tier 2, but 4 more elsewhere
        for (var i = 0; i < 4; i++)
            zendesk.AddAssigned(300 + i, "normal", assignee: Busy).GroupId = 20;
        var waiting = zendesk.AddAssigned(310, "normal", assignee: Busy);
        (waiting.GroupId, waiting.Status) = (20, "pending");
        zendesk.AddAssigned(330, "normal", assignee: Premium).GroupId = 20; // a member who works another group
        var solved = zendesk.AddAssigned(320, "normal", assignee: Idle); // the manager: active, but nothing unsolved
        (solved.GroupId, solved.Status) = (20, "solved");
        zendesk.Reply(200, Free, "Looking");
        zendesk.Reply(210, Busy, "On it");
        zendesk.Reply(320, Idle, "Approved");
        zendesk.Reply(330, Premium, "Checking");
        zendesk.AddUnassigned(500);

        var watcher = new TicketWatcher(settings, new ZendeskClient(settings, zendesk), new RecordingNotifier());
        await watcher.InitializeAsync(CancellationToken.None);
        var (candidates, _) = await watcher.FindCandidatesAsync(500, CancellationToken.None);

        Assert.Equal(["Fred Free", "Bea Busy"], candidates.Select(c => c.Name)); // not the manager, not Premium's agent
        Assert.Equal((5, 1), (candidates[1].Open, candidates[1].Waiting));

        var requests = zendesk.Requested.Count;
        await watcher.FindCandidatesAsync(500, CancellationToken.None);
        Assert.DoesNotContain(zendesk.Requested.Skip(requests), u => u.Contains("search") || u.Contains("/groups/") || u.Contains("metric")); // moments later: no workloads or activity read again
    }

    [Fact]
    public void Menu_line_says_load_and_activity()
    {
        var now = DateTimeOffset.UtcNow;
        Assert.Equal("Maria · 4 open, 1 P1/P2 · active 5 min ago", AssignCandidates.Line(new AssignCandidate(1, "Maria", 4, 1, now.AddMinutes(-5)), now));
        Assert.Equal("Kim · nothing open · in working hours", AssignCandidates.Line(new AssignCandidate(2, "Kim", 0, 0, null), now));
        Assert.Equal("Kim · nothing open, 6 waiting · in working hours", AssignCandidates.Line(new AssignCandidate(2, "Kim", 0, 0, null, 6), now));
        Assert.Equal("Kim · nothing open · in working hours · handled 12 of their tickets", AssignCandidates.Line(new AssignCandidate(2, "Kim", 0, 0, null, 0, 12), now));
    }

    [Fact]
    public async Task A_group_read_moments_ago_is_reused_until_too_old_or_cleared() // shared by Insights and ▾
    {
        var settings = new Settings { Subdomain = "cache-test" };
        var zendesk = new FakeZendesk();
        zendesk.Users[Free] = new User { Id = Free, Name = "Fred Free", Role = "agent" };
        zendesk.GroupMembers[77] = [Free];
        zendesk.AddAssigned(700, "normal", assignee: Free).GroupId = 77;
        var client = new ZendeskClient(settings, zendesk);
        int Exports() => zendesk.Requested.Count(u => u.Contains("search/export"));

        GroupWorkloadCache.Clear();
        var both = await Task.WhenAll(
            GroupWorkloadCache.GetAsync(client, "cache-test", 77, TimeSpan.FromMinutes(5), CancellationToken.None),
            GroupWorkloadCache.GetAsync(client, "cache-test", 77, TimeSpan.FromMinutes(5), CancellationToken.None));
        Assert.Equal(1, Exports()); // two at once: one read
        Assert.Equal([700L], both[1].Tickets.Select(t => t.Id));
        Assert.Equal([Free], both[1].Members.Select(u => u.Id));

        await GroupWorkloadCache.GetAsync(client, "cache-test", 77, TimeSpan.FromMinutes(5), CancellationToken.None);
        Assert.Equal(1, Exports()); // fresh enough
        await GroupWorkloadCache.GetAsync(client, "cache-test", 77, TimeSpan.Zero, CancellationToken.None);
        Assert.Equal(2, Exports()); // too old for this caller
        GroupWorkloadCache.Clear();
        await GroupWorkloadCache.GetAsync(client, "cache-test", 77, TimeSpan.FromMinutes(5), CancellationToken.None);
        Assert.Equal(3, Exports()); // after an assign
    }
}

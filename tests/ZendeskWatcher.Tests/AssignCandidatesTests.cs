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
        zendesk.AddUnassigned(500);

        var watcher = new TicketWatcher(settings, new ZendeskClient(settings, zendesk), new RecordingNotifier());
        await watcher.InitializeAsync(CancellationToken.None);
        var (candidates, note) = await watcher.FindCandidatesAsync(500, CancellationToken.None);

        Assert.Equal(["Fred Free", "Bea Busy"], candidates.Select(c => c.Name)); // not me, not idle, not without a time zone
        Assert.Null(note);
        Assert.Equal((3, 1), (candidates[1].Open, candidates[1].UrgentOrHigh));
        Assert.NotNull(candidates[0].LastActive);
    }

    [Fact]
    public void Menu_line_says_load_and_activity()
    {
        var now = DateTimeOffset.UtcNow;
        Assert.Equal("Maria · 4 open, 1 P1/P2 · active 5 min ago", AssignCandidates.Line(new AssignCandidate(1, "Maria", 4, 1, now.AddMinutes(-5)), now));
        Assert.Equal("Kim · nothing open · in working hours", AssignCandidates.Line(new AssignCandidate(2, "Kim", 0, 0, null), now));
    }
}

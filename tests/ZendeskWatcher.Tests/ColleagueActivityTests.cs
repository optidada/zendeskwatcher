using System.Text.Json;

namespace ZendeskWatcher.Tests;

/// <summary>
/// Who was active, from what the app reads anyway (<see cref="ColleagueActivity"/>), and who solved a ticket
/// (<see cref="TicketRules.SolvedBy"/>, for Notifications → Behavior → Drop notifications).
/// </summary>
public class ColleagueActivityTests
{
    private const long Customer = 50, Colleague = 60;

    private static Audit StatusChange(long author, string to, DateTimeOffset at) => new()
    {
        AuthorId = author, CreatedAt = at,
        Events = [new AuditEvent { Type = "Change", FieldName = "status", Value = JsonSerializer.SerializeToElement(to) }]
    };

    [Fact]
    public void Solved_by_is_the_person_not_the_automation_that_closed_it_later()
    {
        var now = DateTimeOffset.UtcNow;
        Assert.Equal(Colleague, TicketRules.SolvedBy([
            StatusChange(Customer, "open", now.AddHours(-3)),
            StatusChange(Colleague, "solved", now.AddHours(-2)),
            StatusChange(-1, "closed", now.AddHours(-1)) // Zendesk closes solved tickets after some days
        ]));
        Assert.Equal(Customer, TicketRules.SolvedBy([StatusChange(Customer, "solved", now)]));
        Assert.Null(TicketRules.SolvedBy([StatusChange(Colleague, "pending", now)]));
        Assert.Null(TicketRules.SolvedBy([]));
    }

    [Fact]
    public void The_latest_sighting_counts_and_automations_are_nobody()
    {
        var activity = new ColleagueActivity();
        var now = DateTimeOffset.UtcNow;
        activity.Saw(Colleague, now.AddMinutes(-20));
        activity.Saw(Colleague, now.AddMinutes(-40)); // older: doesn't replace the newer one
        activity.Saw(-1, now);
        activity.Saw(Customer, now.AddHours(1));       // a clock far ahead: ignored

        Assert.Equal(now.AddMinutes(-20), activity.LastSeen(Colleague));
        Assert.Null(activity.LastSeen(-1));
        Assert.Null(activity.LastSeen(Customer));
        Assert.Null(activity.LastChecked(Colleague));
    }

    [Fact]
    public async Task A_colleague_seen_just_now_isnt_looked_up_again()
    {
        var zendesk = new FakeZendesk();
        var settings = new Settings
        {
            Subdomain = "test", WatchedGroups = [new WatchedGroup { Id = 10, Name = "Tier 2" }], TrackMyTickets = false,
            NotifyUnassigned = false, HelpColleagues = true, HelpOnlyDuringMyHours = false, HelpRegionDefaultDone = true,
            WorkDays = [] // everyone is off work: only activity can keep a colleague from counting as away
        };
        File.Delete(AppPaths.StateFile);
        zendesk.Me.IanaTimeZone = "Europe/Stockholm";
        zendesk.Users[Customer] = new User { Id = Customer, Name = "Sam Customer", Role = "end-user" };
        zendesk.Users[Colleague] = new User { Id = Colleague, Name = "Berit Berlin", Role = "agent", IanaTimeZone = "Europe/Berlin" };
        zendesk.AddAssigned(301, "urgent", assignee: Colleague);
        var notifier = new RecordingNotifier();
        var watcher = new TicketWatcher(settings, new ZendeskClient(settings, zendesk), notifier);
        await watcher.InitializeAsync(CancellationToken.None);
        await watcher.PollAsync(CancellationToken.None);

        zendesk.Activity.Saw(Colleague, DateTimeOffset.UtcNow.AddMinutes(-2)); // e.g. their note on a ticket I'm CC'd on
        zendesk.Reply(301, Customer, "Any news?");
        await watcher.PollAsync(CancellationToken.None);

        Assert.DoesNotContain(notifier.Shown, n => n.Kind == NotificationKind.HelpColleague); // working late: left alone
        Assert.DoesNotContain(zendesk.Requested, url => url.Contains($"/users/{Colleague}/tickets/assigned"));
    }

    [Fact]
    public void Sightings_and_reads_survive_a_restart_but_failed_reads_and_other_accounts_dont()
    {
        var file = Path.Combine(AppPaths.DataDir, "cache", "activity-test.json");
        var now = DateTimeOffset.UtcNow;
        var before = new ColleagueActivity();
        before.UseFile(file, "yourcompany");
        before.Saw(Colleague, now.AddMinutes(-20));
        before.Saw(Customer, now.AddDays(-2)); // older than a day: not kept
        before.NoteChecked(Colleague, now.AddMinutes(-5));
        before.NoteFailed(Customer, now);
        Assert.Equal((now, false), before.LastRead(Customer));
        before.Save();

        var after = new ColleagueActivity();
        after.UseFile(file, "yourcompany");
        Assert.Equal(now.AddMinutes(-20), after.LastSeen(Colleague));
        Assert.Equal((now.AddMinutes(-5), true), after.LastRead(Colleague));
        Assert.Null(after.LastSeen(Customer));
        Assert.Null(after.LastRead(Customer)); // a failure is tried again after a restart

        var other = new ColleagueActivity();
        other.UseFile(file, "othercompany");
        Assert.Null(other.LastSeen(Colleague));
    }
}

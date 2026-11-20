using Xunit.Abstractions;

namespace ZendeskWatcher.Tests;

/// <summary>
/// A simulated working day against the fake Zendesk, to see how many requests the checks and clicks need. Not a normal
/// test: it only runs with the environment variable ZW_SIMULATE=1 (e.g. <c>set ZW_SIMULATE=1 &amp;&amp; dotnet test
/// --filter WorkdaySimulation</c>) and writes its report to the test output and to workday-simulation.txt in the data folder.
/// <para>
/// The setup is a heavy one: two groups, "Also tickets taken right away" for P1 and P2, my and CC'd tickets, Help
/// colleagues. 8 hours = 160 rounds of one full check and one quick check (the app: every 3 minutes, new tickets every 1½).
/// The day: ~40 new tickets (most taken by a colleague within a few minutes), 20 customer replies on my tickets, 10 on CC'd
/// ones, 10 on colleagues' tickets, 12 clicks on Take it ▾. What runs on the clock (15-minute caches) can't expire in a fast
/// run: the group workload is cleared every 5 rounds to stand in for it; activity reads stay remembered (counted low).
/// </para>
/// </summary>
public class WorkdaySimulation(ITestOutputHelper output)
{
    private const long Customer = 500, Colleague = 60, OtherColleague = 61, Tier2 = 10, Premium = 20;

    [Fact]
    public async Task A_working_day_of_checks_and_clicks()
    {
        if (Environment.GetEnvironmentVariable("ZW_SIMULATE") != "1")
            return;
        File.Delete(AppPaths.StateFile);
        GroupWorkloadCache.Forget();
        var settings = new Settings
        {
            Subdomain = "simulation",
            WatchedGroups = [new WatchedGroup { Id = Tier2, Name = "Tier 2" }, new WatchedGroup { Id = Premium, Name = "Premium" }],
            TakenQuicklyPriorities = ["urgent", "high"],
            TrackMyTickets = true, TrackCcTickets = true, HelpColleagues = true, HelpOnlyDuringMyHours = false, HelpRegionDefaultDone = true,
            WorkdayStartHour = 0, WorkdayEndHour = 24, WorkDays = [.. Enum.GetValues<DayOfWeek>()]
        };
        var zendesk = new FakeZendesk();
        zendesk.Groups.Add(new ZendeskWatcher.Zendesk.Group { Id = Premium, Name = "Premium" });
        zendesk.Me.IanaTimeZone = "Europe/Stockholm";
        zendesk.Users[zendesk.Me.Id] = zendesk.Me;
        zendesk.Users[Customer] = new User { Id = Customer, Name = "Sam Customer", Role = "end-user", Email = "sam@client.example" };
        zendesk.Users[Colleague] = new User { Id = Colleague, Name = "Berit Berlin", Role = "agent", IanaTimeZone = "Europe/Berlin" };
        zendesk.Users[OtherColleague] = new User { Id = OtherColleague, Name = "Nia NewYork", Role = "agent", IanaTimeZone = "America/New_York" };
        zendesk.GroupMembers[Tier2] = [zendesk.Me.Id, Colleague, OtherColleague];
        zendesk.GroupMembers[Premium] = [zendesk.Me.Id, Colleague];

        // What's there at the start of the day: my open tickets, CC'd ones, colleagues' ones, some waiting unassigned.
        var nextId = 1000L;
        var mine = Enumerable.Range(0, 14).Select(_ => zendesk.AddAssigned(nextId++, "normal").Id).ToList();
        var ccd = Enumerable.Range(0, 6).Select(_ =>
        {
            var t = zendesk.AddAssigned(nextId++, "normal", assignee: Colleague);
            t.EmailCcIds = [zendesk.Me.Id];
            return t.Id;
        }).ToList();
        var colleagues = Enumerable.Range(0, 20).Select(i => zendesk.AddAssigned(nextId++, "normal", assignee: i % 2 == 0 ? Colleague : OtherColleague).Id).ToList();
        for (var i = 0; i < 8; i++)
            zendesk.AddUnassigned(nextId++, i % 2 == 0 ? Tier2 : Premium).Priority = "normal";

        var notifier = new RecordingNotifier();
        var watcher = new TicketWatcher(settings, new ZendeskClient(settings, zendesk), notifier);
        ZendeskClient.TakeRequestCount();
        ZendeskClient.TakeRequestBreakdown();
        ZendeskClient.TakeRequestsByReason();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        await watcher.InitializeAsync(CancellationToken.None);
        await watcher.PollAsync(CancellationToken.None);
        var startRequests = zendesk.Requested.Count;

        const int Rounds = 160;
        var random = new Random(20261121);
        var full = new List<int>();
        var quick = new List<int>();
        var clicks = new List<int>();
        var waiting = new List<long>(); // new tickets nobody took yet
        bool Chance(double perDay) => random.NextDouble() < perDay / Rounds;

        for (var round = 0; round < Rounds; round++)
        {
            // What happens in Zendesk in these 3 minutes.
            if (Chance(40))
            {
                var t = zendesk.AddUnassigned(nextId++, random.Next(2) == 0 ? Tier2 : Premium);
                t.Priority = random.Next(10) switch { 0 => "urgent", < 3 => "high", _ => "normal" };
                t.OrganizationId = 4700 + random.Next(20);
                waiting.Add(t.Id);
            }
            foreach (var id in waiting.Where(_ => random.Next(3) == 0).ToList()) // taken within a few minutes, mostly
            {
                zendesk.Tickets[id].AssigneeId = random.Next(2) == 0 ? Colleague : OtherColleague;
                zendesk.Tickets[id].Status = "open";
                waiting.Remove(id);
            }
            if (Chance(20))
                zendesk.Reply(mine[random.Next(mine.Count)], Customer, "Any news?");
            if (Chance(10))
                zendesk.Reply(ccd[random.Next(ccd.Count)], Customer, "Thanks, one more question");
            if (Chance(10))
                zendesk.Reply(colleagues[random.Next(colleagues.Count)], Customer, "Hello?");
            if (round % 5 == 0)
                GroupWorkloadCache.Clear(); // stands in for the 15-minute workload age

            var before = zendesk.Requested.Count;
            await watcher.PollAsync(CancellationToken.None);
            full.Add(zendesk.Requested.Count - before);

            if (Chance(12) && waiting.Count > 0)
            {
                before = zendesk.Requested.Count;
                await watcher.FindCandidatesAsync(waiting[^1], CancellationToken.None);
                clicks.Add(zendesk.Requested.Count - before);
            }

            before = zendesk.Requested.Count;
            await watcher.PollAsync(CancellationToken.None, newTicketsOnly: true);
            quick.Add(zendesk.Requested.Count - before);
        }

        var total = zendesk.Requested.Count - startRequests;
        static string Spread(List<int> n) => n.Count == 0 ? "none" : $"{n.Average():0.0} on average (min {n.Min()}, max {n.Max()})";
        var report = string.Join(Environment.NewLine,
            $"Simulated working day: {Rounds} full checks + {Rounds} quick checks, {clicks.Count} Take it ▾ clicks, {notifier.Shown.Count} notifications",
            $"Requests: {total} in 8 h = {total / 480.0:0.0} per minute (start of the app not counted: {startRequests})",
            $"Full check: {Spread(full)} requests",
            $"Quick check (new tickets only): {Spread(quick)} requests",
            $"Take it ▾ click: {Spread(clicks)} requests",
            $"By kind: {ZendeskClient.TakeRequestBreakdown()}",
            $"By reason: {ZendeskClient.TakeRequestsByReason()}",
            $"Ran in {clock.Elapsed.TotalSeconds:0.0} s against the fake Zendesk (no network: times say nothing about Zendesk's speed)");
        output.WriteLine(report);
        File.WriteAllText(Path.Combine(AppPaths.DataDir, "workday-simulation.txt"), report);
    }
}

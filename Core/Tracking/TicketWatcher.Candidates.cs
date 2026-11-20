using ZendeskWatcher.Zendesk;

namespace ZendeskWatcher.Tracking;

public sealed partial class TicketWatcher
{
    /// <summary>How fresh the group's workload should be for "Who could take it" (tickets are heavy: counts move slowly; an assign clears it).</summary>
    private static readonly TimeSpan CandidatesDataAge = CacheAges.WorkloadFresh;

    /// <summary>
    /// Who could take a new ticket ("▾" next to Take it): colleagues in the ticket's group who are in their working
    /// hours and were active in Zendesk lately (<see cref="TicketRules.ColleagueAway"/>), least busy first by their open
    /// tickets in all groups (some have two home groups), with knowing the client counting in (<see cref="AssignCandidates.Rank"/>). Only people who work in the group (<see cref="AssignCandidates.ToCheck"/>).
    /// Read only when asked. Note: why
    /// the list is empty (already assigned, no group, nobody around), else null.
    /// </summary>
    /// <param name="measuring">Developer switch --measure-take: an older ticket as if it were new (its assignee is ignored), named in the log line.</param>
    public async Task<(List<AssignCandidate> Candidates, string? Note)> FindCandidatesAsync(long ticketId, CancellationToken ct, string? measuring = null)
    {
        // Measured, for the log: how long each step took and how many requests it needed (cached data needs none).
        var requests = ZendeskClient.Counter.Value = new ZendeskClient.RequestCounter();
        ZendeskClient.Clicked.Value = measuring is null; // a click: not behind the background checks
        if (measuring is null && IsTestTicket(ticketId))
            measuring = "test notification"; // --test-take: an old ticket, as if it were new
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var ticket = (await zendesk.GetTicketsAsync([ticketId], ct)).FirstOrDefault();
        var ticketTime = clock.Elapsed;
        if (ticket is null)
            return ([], "The ticket couldn't be read.");
        if (ticket.AssigneeId is { } assignee && measuring is null)
            return ([], $"Already assigned to {await zendesk.GetUserNameAsync(assignee, ct)}.");
        if (ticket.GroupId is not { } groupId)
            return ([], "The ticket has no group.");

        // Who handled the client's tickets in this group (a hint on each line), read alongside the rest.
        var clientTickets = ticket.OrganizationId is { } org ? ClientHandlersAsync(groupId, org, ct) : Task.FromResult(new Dictionary<long, int>());

        // Members and the group's workload may be a few minutes old (shared with Insights → Team workload); they pick
        // whom to look at. Each one's whole workload too (remembered per person); the ticket above is read fresh.
        var group = await GroupWorkloadCache.GetAsync(zendesk, settings.Subdomain, groupId, CandidatesDataAge, ct);
        var members = group.Members;
        var team = members.ToDictionary(u => u.Id, u => u.Name ?? $"User {u.Id}");
        var statuses = _statusNames.Count > 0 ? _statusNames
            : (await zendesk.GetCustomStatusesAsync(ct)).Where(s => s.AgentLabel is { Length: > 0 }).ToDictionary(s => s.Id, s => s.AgentLabel!);
        var rows = TeamWorkload.Count(group.Tickets, team, Me?.Id ?? 0, statuses);
        var handled = await clientTickets; // decides whom to look at too: someone who knows the client isn't skipped
        var groupTime = clock.Elapsed - ticketTime;

        var now = DateTimeOffset.UtcNow;
        var users = members.ToDictionary(u => u.Id);
        var turns = new SemaphoreSlim(3); // not disposed: a failed look may leave others still finishing with it
        var looks = AssignCandidates.ToCheck(rows, id => OutsideWorkingHours(users[id], now).Away, handled).Select(async Task<(WorkloadRow Row, DateTimeOffset? Last)?> (row) =>
        {
            await turns.WaitAsync(ct);
            try
            {
                var (known, last) = await LastActiveAsync(row.AssigneeId, now, ct);
                if (TicketRules.ColleagueAway(false, known, last, now) != false)
                    return null; // nothing in Zendesk for 3 hours: probably not at their desk
                return (await WholeWorkloadAsync(row, statuses, ct), last);
            }
            finally { turns.Release(); }
        });
        var looked = await Task.WhenAll(looks);
        var found = looked.OfType<(WorkloadRow Row, DateTimeOffset? Last)>() // the ones around
            .OrderBy(c => AssignCandidates.Rank(c.Row, handled.GetValueOrDefault(c.Row.AssigneeId))).ThenBy(c => c.Row.Open + c.Row.InProgress)
            .ThenBy(c => c.Row.Name, StringComparer.CurrentCultureIgnoreCase)
            .Take(AssignCandidates.Shown)
            .ToList();
        var offered = found
            .Select(c => new AssignCandidate(c.Row.AssigneeId, c.Row.Name, c.Row.Open + c.Row.InProgress, c.Row.UrgentOrHigh, c.Last,
                c.Row.Escalated + c.Row.Scheduled + c.Row.Pending, handled.GetValueOrDefault(c.Row.AssigneeId)))
            .ToList();
        var peopleTime = clock.Elapsed - ticketTime - groupTime;
        static string S(TimeSpan t) => $"{t.TotalSeconds:0.0} s";
        Log.Info($"Who could take #{ticketId}{(measuring is null ? "" : $" ({measuring})")}: {offered.Count} offered, {looked.Length} looked at ({looked.Count(l => l is null)} not active), " +
                 $"{members.Count} in the group · {S(clock.Elapsed)} (ticket {S(ticketTime)}, group {S(groupTime)}, people {S(peopleTime)}) · " +
                 $"{requests.Count} requests");
        return (offered, offered.Count == 0 ? "Nobody who works this group's tickets is in working hours and active in Zendesk right now." : null);
    }

    /// <summary>
    /// How many of the client's tickets in the group (last 90 days) each agent has now (1 search export). Answered at
    /// once when known (<see cref="ClientHandlerStore"/>, on disk): fresh for <see cref="CacheAges.ClientHandlersFresh"/>;
    /// older (up to <see cref="CacheAges.ClientHandlersKeep"/>) it's used right away while a fresh read runs in the
    /// background for next time. Only a client never read waits. Empty when it can't be read: the suggestions just don't say it.
    /// </summary>
    private async Task<Dictionary<long, int>> ClientHandlersAsync(long groupId, long organizationId, CancellationToken ct)
    {
        var key = (settings.Subdomain, groupId, organizationId);
        var cached = ClientHandlerStore.Get(key);
        var age = DateTime.UtcNow - cached.At;
        if (cached.Counts is not null && age <= CacheAges.ClientHandlersFresh)
            return cached.Counts;
        if (cached.Counts is not null && age <= CacheAges.ClientHandlersKeep)
        {
            _ = RefreshAsync(); // for next time; not awaited, not cancelled with this menu
            return cached.Counts;
        }
        return await RefreshAsync(ct) ?? [];

        async Task<Dictionary<long, int>?> RefreshAsync(CancellationToken token = default)
        {
            if (!ClientHandlerStore.StartRead(key))
                return null; // already being read
            ZendeskClient.Reason.Value = "who could take it";
            try
            {
                var counts = (await zendesk.GetClientTicketsInGroupAsync(groupId, organizationId, token))
                    .Where(t => t.AssigneeId is not null).GroupBy(t => t.AssigneeId!.Value).ToDictionary(g => g.Key, g => g.Count());
                ClientHandlerStore.Set(key, counts);
                return counts;
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or AuthRequiredException)
            {
                if (ex is HttpRequestException)
                    Log.Error($"Could not read client {organizationId}'s tickets in group {groupId}; suggestions without it", ex);
                return null;
            }
            finally
            {
                ClientHandlerStore.EndRead(key);
            }
        }
    }

    /// <summary>A colleague's unsolved tickets in every group; just the ticket's group (<paramref name="inGroup"/>) if that can't be read.</summary>
    private async Task<WorkloadRow> WholeWorkloadAsync(WorkloadRow inGroup, IReadOnlyDictionary<long, string> statuses, CancellationToken ct)
    {
        ZendeskClient.Reason.Value = "who could take it";
        try
        {
            var tickets = await GroupWorkloadCache.GetPersonAsync(zendesk, settings.Subdomain, inGroup.AssigneeId, CandidatesDataAge, ct);
            return TeamWorkload.Count(tickets, new Dictionary<long, string> { [inGroup.AssigneeId] = inGroup.Name }, Me?.Id ?? 0, statuses)[0];
        }
        catch (HttpRequestException ex)
        {
            Log.Error($"Could not read all open tickets of user {inGroup.AssigneeId}; counting the ticket's group only", ex);
            return inGroup;
        }
    }

    /// <summary>
    /// Developer switch --measure-take: "who could take it" for a few tickets created in the monitored groups in the last
    /// day (taken ones too, as if they were new), each four times: cold (the workload must be read, as right after an
    /// assign; group members known), warm (right after), after a restart (memory forgotten, what's on disk read again), and
    /// with the group read early (as when the notification came on screen).
    /// Only reads; the times go to the log.
    /// </summary>
    public async Task MeasureCandidatesAsync(CancellationToken ct)
    {
        var since = DateTimeOffset.UtcNow.AddDays(-1);
        var picked = new List<long>();
        foreach (var group in settings.WatchedGroups.Where(g => !g.IsNoGroup))
        {
            if (picked.Count >= 4)
                break;
            var recent = await zendesk.GetAssignedTicketsCreatedSinceAsync(group.Id, since, ct);
            picked.AddRange(recent.OrderByDescending(t => t.CreatedAt).Take(2).Select(t => t.Id));
        }
        Log.Info($"Measuring who could take it for {picked.Count} recent tickets: {string.Join(", ", picked.Select(id => $"#{id}"))}");
        foreach (var id in picked.Take(4))
        {
            GroupWorkloadCache.Clear(); // as on the first click
            await FindCandidatesAsync(id, ct, "measuring, cold");
            await FindCandidatesAsync(id, ct, "measuring, warm");
            GroupWorkloadCache.Reload(); // as after a restart: only what's on disk
            ClientHandlerStore.Reload();
            await FindCandidatesAsync(id, ct, "measuring, after a restart");
            GroupWorkloadCache.Clear(); // the group read when the notification came on screen, then the click
            var ticket = (await zendesk.GetTicketsAsync([id], ct)).FirstOrDefault();
            if (ticket?.GroupId is { } groupId)
                await ReadyCandidatesGroupAsync(groupId);
            await FindCandidatesAsync(id, ct, "measuring, group read early");
        }
        Log.Info("Measuring who could take it: done");
    }

    /// <summary>Tickets shown by the developer switch --test-take: ▾ treats them as new; Take it and assigning are refused.</summary>
    private static readonly HashSet<long> TestTickets = [];

    public static bool IsTestTicket(long ticketId)
    {
        lock (TestTickets)
            return TestTickets.Contains(ticketId);
    }

    /// <summary>
    /// Developer switch --test-take: a new-ticket notification for a recent ticket in the monitored groups (already taken,
    /// shown as if it were new), or <paramref name="ticketId"/>, so ▾ next to Take it can be tried any time. Nothing can be
    /// assigned from it.
    /// </summary>
    public async Task ShowTestTicketAsync(long? ticketId, CancellationToken ct)
    {
        if (ticketId is { } id)
        {
            if ((await zendesk.GetTicketsAsync([id], ct)).FirstOrDefault() is not { } chosen
                || settings.WatchedGroups.FirstOrDefault(g => g.Id == chosen.GroupId) is not { } itsGroup)
            {
                Log.Info($"Test notification: #{id} couldn't be read or isn't in a monitored group");
                return;
            }
            lock (TestTickets)
                TestTickets.Add(id);
            Log.Info($"Test notification for #{id} (Take it ▾ as if new; nothing can be assigned from it)");
            notifier.Show(await UnassignedTicketAsync(chosen, itsGroup, "Test · not really new", ct));
            return;
        }
        var since = DateTimeOffset.UtcNow.AddDays(-1);
        foreach (var group in settings.WatchedGroups.Where(g => !g.IsNoGroup))
        {
            var recent = await zendesk.GetAssignedTicketsCreatedSinceAsync(group.Id, since, ct);
            if (recent.Where(t => t.OrganizationId is not null).MaxBy(t => t.CreatedAt) is not { } ticket)
                continue;
            lock (TestTickets)
                TestTickets.Add(ticket.Id);
            Log.Info($"Test notification for #{ticket.Id} (Take it ▾ as if new; nothing can be assigned from it)");
            notifier.Show(await UnassignedTicketAsync(ticket, group, "Test · not really new", ct));
            return;
        }
        Log.Info("Test notification: no ticket created in the monitored groups in the last day");
    }

    /// <summary>
    /// A new-ticket notification with Take it ▾ came on screen: its group's members and open tickets are read now (2
    /// requests, unless read in the last 15 minutes), so a click on ▾ only has the people left (the slowest part done).
    /// Not a click: it keeps the plain request limit. Errors only go to the log; the click reads again.
    /// </summary>
    public async Task ReadyCandidatesGroupAsync(long groupId)
    {
        ZendeskClient.Reason.Value = "who could take it (ready early)";
        try
        {
            await GroupWorkloadCache.GetAsync(zendesk, settings.Subdomain, groupId, CandidatesDataAge, CancellationToken.None);
        }
        catch (Exception ex) // anything: nothing to tell the user, the click reads again
        {
            Log.Info($"Could not read group {groupId} early for Take it ▾ ({ex.GetType().Name}); a click reads it");
        }
    }
}

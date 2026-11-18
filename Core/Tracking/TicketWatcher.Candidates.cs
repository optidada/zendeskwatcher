using ZendeskWatcher.Zendesk;

namespace ZendeskWatcher.Tracking;

public sealed partial class TicketWatcher
{
    /// <summary>How old the group's members and workload may be for "Who could take it" (people work one ticket at a time: little changes in 15 minutes; an assign clears it).</summary>
    private static readonly TimeSpan CandidatesDataAge = TimeSpan.FromMinutes(15);

    /// <summary>How old "handled 12 of their tickets" may be: 90 days of history hardly changes in a day.</summary>
    private static readonly TimeSpan ClientHandlersAge = TimeSpan.FromHours(24);

    /// <summary>
    /// Who could take a new ticket ("▾" next to Take it): colleagues in the ticket's group who are in their working
    /// hours and were active in Zendesk lately (<see cref="TicketRules.ColleagueAway"/>), least busy first by their open
    /// tickets in all groups (some have two home groups), with knowing the client counting in (<see cref="AssignCandidates.Rank"/>). Only people who work in the group (<see cref="AssignCandidates.ToCheck"/>).
    /// Read only when asked. Note: why
    /// the list is empty (already assigned, no group, nobody around), else null.
    /// </summary>
    public async Task<(List<AssignCandidate> Candidates, string? Note)> FindCandidatesAsync(long ticketId, CancellationToken ct)
    {
        var ticket = (await zendesk.GetTicketsAsync([ticketId], ct)).FirstOrDefault();
        if (ticket is null)
            return ([], "The ticket couldn't be read.");
        if (ticket.AssigneeId is { } assignee)
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
        var found = (await Task.WhenAll(looks)).OfType<(WorkloadRow Row, DateTimeOffset? Last)>() // the ones around
            .OrderBy(c => AssignCandidates.Rank(c.Row, handled.GetValueOrDefault(c.Row.AssigneeId))).ThenBy(c => c.Row.Open + c.Row.InProgress)
            .ThenBy(c => c.Row.Name, StringComparer.CurrentCultureIgnoreCase)
            .Take(AssignCandidates.Shown)
            .ToList();
        var offered = found
            .Select(c => new AssignCandidate(c.Row.AssigneeId, c.Row.Name, c.Row.Open + c.Row.InProgress, c.Row.UrgentOrHigh, c.Last,
                c.Row.Escalated + c.Row.Scheduled + c.Row.Pending, handled.GetValueOrDefault(c.Row.AssigneeId)))
            .ToList();
        return (offered, offered.Count == 0 ? "Nobody who works this group's tickets is in working hours and active in Zendesk right now." : null);
    }

    /// <summary>Client and group → (assignee → tickets), memory only (<see cref="ClientHandlersAsync"/> says how long).</summary>
    private static readonly Dictionary<(string Subdomain, long Group, long Client), (DateTime At, Dictionary<long, int> Counts)> ClientHandlerCache = [];

    /// <summary>Reads running in the background, so one client isn't read twice at once.</summary>
    private static readonly HashSet<(string Subdomain, long Group, long Client)> ClientHandlerRefreshing = [];

    /// <summary>Older than this, "handled 12" isn't shown even while a fresh read runs: 90 days of history has moved on.</summary>
    private static readonly TimeSpan ClientHandlersKeep = TimeSpan.FromDays(7);

    /// <summary>
    /// How many of the client's tickets in the group (last 90 days) each agent has now (1 search export). Answered at
    /// once from memory when there is something (stale-while-revalidate): fresh up to 24 hours; older (up to 7 days) it's
    /// used right away while a fresh read runs in the background for next time. Only a client never read waits. Empty
    /// when it can't be read: the suggestions just don't say it.
    /// </summary>
    private async Task<Dictionary<long, int>> ClientHandlersAsync(long groupId, long organizationId, CancellationToken ct)
    {
        var key = (settings.Subdomain, groupId, organizationId);
        (DateTime At, Dictionary<long, int> Counts) cached;
        lock (ClientHandlerCache)
            if (!ClientHandlerCache.TryGetValue(key, out cached))
                cached = default;
        var age = DateTime.UtcNow - cached.At;
        if (cached.Counts is not null && age <= ClientHandlersAge)
            return cached.Counts;
        if (cached.Counts is not null && age <= ClientHandlersKeep)
        {
            _ = RefreshAsync(); // for next time; not awaited, not cancelled with this menu
            return cached.Counts;
        }
        return await RefreshAsync(ct) ?? [];

        async Task<Dictionary<long, int>?> RefreshAsync(CancellationToken token = default)
        {
            lock (ClientHandlerCache)
                if (!ClientHandlerRefreshing.Add(key))
                    return null; // already being read
            ZendeskClient.Reason.Value = "who could take it";
            try
            {
                var counts = (await zendesk.GetClientTicketsInGroupAsync(groupId, organizationId, token))
                    .Where(t => t.AssigneeId is not null).GroupBy(t => t.AssigneeId!.Value).ToDictionary(g => g.Key, g => g.Count());
                lock (ClientHandlerCache)
                    ClientHandlerCache[key] = (DateTime.UtcNow, counts);
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
                lock (ClientHandlerCache)
                    ClientHandlerRefreshing.Remove(key);
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
}

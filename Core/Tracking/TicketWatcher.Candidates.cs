using ZendeskWatcher.Zendesk;

namespace ZendeskWatcher.Tracking;

public sealed partial class TicketWatcher
{
    /// <summary>How old the group's members and workload may be for "Who could take it" (a few tickets more or less at most).</summary>
    private static readonly TimeSpan CandidatesDataAge = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Who could take a new ticket ("▾" next to Take it): colleagues in the ticket's group who are in their working
    /// hours and were active in Zendesk lately (<see cref="TicketRules.ColleagueAway"/>), least busy first by their open
    /// tickets in all groups (a person with nothing in this group may be busy in another); people with no unsolved
    /// tickets anywhere are left out (they don't work tickets, e.g. managers). Read only when asked. Note: why
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

        // Members and the group's workload may be a few minutes old (shared with Insights → Team workload); they pick
        // whom to look at. The ticket above, and each one's activity and whole workload below, are read fresh.
        var group = await GroupWorkloadCache.GetAsync(zendesk, settings.Subdomain, groupId, CandidatesDataAge, ct);
        var members = group.Members;
        var team = members.ToDictionary(u => u.Id, u => u.Name ?? $"User {u.Id}");
        var statuses = _statusNames.Count > 0 ? _statusNames
            : (await zendesk.GetCustomStatusesAsync(ct)).Where(s => s.AgentLabel is { Length: > 0 }).ToDictionary(s => s.Id, s => s.AgentLabel!);
        var rows = TeamWorkload.Count(group.Tickets, team, Me?.Id ?? 0, statuses);

        var now = DateTimeOffset.UtcNow;
        var users = members.ToDictionary(u => u.Id);
        var turns = new SemaphoreSlim(3); // not disposed: a failed look may leave others still finishing with it
        var looks = AssignCandidates.ToCheck(rows, id => OutsideWorkingHours(users[id], now).Away).Select(async Task<(WorkloadRow Row, DateTimeOffset? Last)?> (row) =>
        {
            await turns.WaitAsync(ct);
            try
            {
                var (known, last) = await LastActiveAsync(row.AssigneeId, now, ct);
                if (TicketRules.ColleagueAway(false, known, last, now) != false)
                    return null; // nothing in Zendesk for 3 hours: probably not at their desk
                return await WholeWorkloadAsync(row, statuses, ct) is { } whole ? (whole, last) : null;
            }
            finally { turns.Release(); }
        });
        var found = (await Task.WhenAll(looks)).OfType<(WorkloadRow Row, DateTimeOffset? Last)>() // the ones around
            .OrderBy(c => TeamWorkload.Score(c.Row)).ThenBy(c => c.Row.Open + c.Row.InProgress)
            .ThenBy(c => c.Row.Name, StringComparer.CurrentCultureIgnoreCase)
            .Take(AssignCandidates.Shown)
            .Select(c => new AssignCandidate(c.Row.AssigneeId, c.Row.Name, c.Row.Open + c.Row.InProgress, c.Row.UrgentOrHigh, c.Last,
                c.Row.Escalated + c.Row.Scheduled + c.Row.Pending))
            .ToList();
        return (found, found.Count == 0 ? "Nobody in the group is in working hours and active in Zendesk right now." : null);
    }

    /// <summary>
    /// A colleague's unsolved tickets in every group; just the ticket's group (<paramref name="inGroup"/>) if that can't be
    /// read. Null when they have none at all: someone who doesn't work tickets (a manager, say), not someone to suggest.
    /// </summary>
    private async Task<WorkloadRow?> WholeWorkloadAsync(WorkloadRow inGroup, IReadOnlyDictionary<long, string> statuses, CancellationToken ct)
    {
        ZendeskClient.Reason.Value = "who could take it";
        try
        {
            var tickets = await zendesk.GetOpenTicketsAssignedToAsync(inGroup.AssigneeId, ct);
            var whole = TeamWorkload.Count(tickets, new Dictionary<long, string> { [inGroup.AssigneeId] = inGroup.Name }, Me?.Id ?? 0, statuses)[0];
            return TeamWorkload.Score(whole) == 0 ? null : whole;
        }
        catch (HttpRequestException ex)
        {
            Log.Error($"Could not read all open tickets of user {inGroup.AssigneeId}; counting the ticket's group only", ex);
            return inGroup;
        }
    }
}

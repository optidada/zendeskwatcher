using ZendeskWatcher.Zendesk;

namespace ZendeskWatcher.Tracking;

public sealed partial class TicketWatcher
{
    /// <summary>How old the group's members and workload may be for "Who could take it" (a few tickets more or less at most).</summary>
    private static readonly TimeSpan CandidatesDataAge = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Who could take a new ticket ("▾" next to Take it): colleagues in the ticket's group who are in their working
    /// hours and were active in Zendesk lately (<see cref="TicketRules.ColleagueAway"/>), least busy first. Read only
    /// when asked. Note: why the list is empty (already assigned, no group, nobody around), else null.
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

        // Members and workload may be a few minutes old (shared with Insights → Team workload); the ticket above and
        // everyone's activity below are read fresh.
        var group = await GroupWorkloadCache.GetAsync(zendesk, settings.Subdomain, groupId, CandidatesDataAge, ct);
        var members = group.Members;
        var team = members.ToDictionary(u => u.Id, u => u.Name ?? $"User {u.Id}");
        var statuses = _statusNames.Count > 0 ? _statusNames
            : (await zendesk.GetCustomStatusesAsync(ct)).Where(s => s.AgentLabel is { Length: > 0 }).ToDictionary(s => s.Id, s => s.AgentLabel!);
        var rows = TeamWorkload.Count(group.Tickets, team, Me?.Id ?? 0, statuses);

        var now = DateTimeOffset.UtcNow;
        var users = members.ToDictionary(u => u.Id);
        var found = new List<AssignCandidate>();
        foreach (var row in AssignCandidates.ToCheck(rows, id => OutsideWorkingHours(users[id], now).Away))
        {
            var (known, last) = await LastActiveAsync(row.AssigneeId, now, ct);
            if (TicketRules.ColleagueAway(false, known, last, now) != false)
                continue; // nothing in Zendesk for 3 hours: probably not at their desk
            found.Add(new AssignCandidate(row.AssigneeId, row.Name, row.Open + row.InProgress, row.UrgentOrHigh, last));
            if (found.Count == AssignCandidates.Shown)
                break;
        }
        return (found, found.Count == 0 ? "Nobody in the group is in working hours and active in Zendesk right now." : null);
    }
}

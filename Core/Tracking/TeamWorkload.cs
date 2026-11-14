using ZendeskWatcher.Zendesk;

namespace ZendeskWatcher.Tracking;

/// <summary>A teammate's open tickets in the monitored groups (Insights → Right now → Your team).</summary>
/// <param name="Open">Needs an agent: New or Open, also custom statuses like In progress (by their category).</param>
/// <param name="UrgentOrHigh">Of the open ones, Urgent (P1) or High (P2).</param>
/// <param name="Escalated">Escalated: custom statuses with "escalat" in the name (to Engineering, internally, …), whatever their category.</param>
/// <param name="Scheduled">Scheduled: a time is booked (a custom status matched by name), whatever its category.</param>
/// <param name="Waiting">Pending or On-hold: waiting on the customer or someone else.</param>
public sealed record WorkloadRow(long AssigneeId, string Name, int Open, int UrgentOrHigh, int Escalated, int Scheduled, int Waiting, bool IsMe);

public static class TeamWorkload
{
    /// <summary>A custom status that means escalated, e.g. "Escalated to Engineering" or "Escalated internally" (matched by name).</summary>
    public static bool IsEscalatedStatus(string? label) => label?.Contains("escalat", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>
    /// Workload: every unsolved ticket counts, since a pending one can turn into work any minute. Open (also In progress)
    /// Urgent or High weighs 3, other open ones 2, Escalated, Scheduled, Pending and On-hold 1 each.
    /// </summary>
    public static int Score(WorkloadRow row) => 2 * row.Open + row.UrgentOrHigh + Other(row);

    /// <summary>Escalated, Scheduled, Pending and On-hold together.</summary>
    private static int Other(WorkloadRow row) => row.Escalated + row.Scheduled + row.Waiting;

    /// <summary>
    /// Counts the tickets of each teammate (also the ones with none), busiest first (<see cref="Score"/>).
    /// A ticket found in two groups counts once. <paramref name="statusNames"/>: custom status id → its name (agents' label).
    /// </summary>
    public static List<WorkloadRow> Count(IEnumerable<Ticket> tickets, IReadOnlyDictionary<long, string> team, long meId,
        IReadOnlyDictionary<long, string>? statusNames = null)
    {
        var counts = team.Keys.ToDictionary(id => id, _ => (Open: 0, Urgent: 0, Escalated: 0, Scheduled: 0, Waiting: 0));
        foreach (var ticket in tickets.DistinctBy(t => t.Id))
        {
            if (ticket.AssigneeId is not { } id || !counts.TryGetValue(id, out var c))
                continue;
            var name = ticket.CustomStatusId is { } status ? statusNames?.GetValueOrDefault(status) : null;
            if (IsEscalatedStatus(name))
                c.Escalated++;
            else if (TicketRules.IsScheduled(name))
                c.Scheduled++;
            else if (ticket.Status is "new" or "open")
            {
                c.Open++;
                if (ticket.Priority is "urgent" or "high")
                    c.Urgent++;
            }
            else if (ticket.Status is "pending" or "hold")
                c.Waiting++;
            counts[id] = c;
        }
        return counts.Select(kv => new WorkloadRow(kv.Key, team[kv.Key], kv.Value.Open, kv.Value.Urgent, kv.Value.Escalated,
                kv.Value.Scheduled, kv.Value.Waiting, kv.Key == meId))
            .OrderByDescending(Score).ThenByDescending(r => r.Open).ThenBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }
}

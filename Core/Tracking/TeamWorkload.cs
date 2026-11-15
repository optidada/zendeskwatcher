using ZendeskWatcher.Zendesk;

namespace ZendeskWatcher.Tracking;

/// <summary>A teammate's open tickets in the monitored groups (Insights → Tickets right now → Your team).</summary>
/// <param name="Open">Needs an agent: New or Open, also open-category custom statuses other than In progress.</param>
/// <param name="UrgentOrHigh">Of the open and In progress ones, Urgent (P1) or High (P2).</param>
/// <param name="Escalated">Escalated: custom statuses with "escalat" in the name (to Engineering, internally, …), whatever their category; all together.</param>
/// <param name="Scheduled">Scheduled: a time is booked (a custom status matched by name), whatever its category.</param>
/// <param name="Pending">Pending or On-hold: waiting on the customer or someone else.</param>
/// <param name="InProgress">In progress: a custom status matched by name, whatever its category.</param>
/// <param name="Solved">Solved or Closed in the last <see cref="TeamWorkload.SolvedDays"/> days (not workload: done).</param>
/// <param name="EscalatedBy">Escalated per status name (e.g. "Escalated to Engineering" → 2); null when not split up.</param>
public sealed record WorkloadRow(long AssigneeId, string Name, int Open, int UrgentOrHigh, int Escalated, int Scheduled, int Pending, bool IsMe,
    int InProgress = 0, IReadOnlyDictionary<string, int>? EscalatedBy = null, int Solved = 0);

public static class TeamWorkload
{
    /// <summary>The Solved column counts tickets solved in this many days.</summary>
    public const int SolvedDays = 7;

    /// <summary>A custom status that means escalated, e.g. "Escalated to Engineering" or "Escalated internally" (matched by name).</summary>
    public static bool IsEscalatedStatus(string? label) => label?.Contains("escalat", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>A custom status that means someone works on it, e.g. "In progress" (matched by name).</summary>
    public static bool IsInProgressStatus(string? label) => label?.Contains("in progress", StringComparison.OrdinalIgnoreCase) == true
        || label?.Contains("in-progress", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>
    /// A short column heading for an escalated status: "Escalated to Engineering" → "Esc. Eng.", "Escalated internally" →
    /// "Esc. Int." (the Your team table is narrow). Just "Escalated" when the name says nothing more.
    /// </summary>
    public static string ShortEscalatedName(string label)
    {
        var words = label.Split([' ', '-', '_', '/'], StringSplitOptions.RemoveEmptyEntries)
            .Where(w => !IsEscalatedStatus(w) && !w.Equals("to", StringComparison.OrdinalIgnoreCase)).ToList();
        if (words.Count == 0)
            return "Escalated";
        var word = char.ToUpperInvariant(words[0][0]) + words[0][1..];
        if (word.Length > 5)
            word = word[..3] + ".";
        return $"Esc. {word}";
    }

    /// <summary>
    /// Names for the narrow Your team table: "Maria Svensson" → "Maria S.". Full names where two would look the same,
    /// and single words ("You") as they are.
    /// </summary>
    public static Dictionary<string, string> ShortNames(IEnumerable<string> names)
    {
        var all = names.Distinct().ToList();
        var shortOf = all.ToDictionary(n => n, n =>
        {
            var words = n.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return words.Length < 2 ? n : $"{words[0]} {char.ToUpper(words[^1][0])}.";
        });
        var taken = shortOf.Values.GroupBy(s => s, StringComparer.CurrentCultureIgnoreCase).Where(g => g.Count() > 1)
            .Select(g => g.Key).ToHashSet(StringComparer.CurrentCultureIgnoreCase);
        return shortOf.ToDictionary(kv => kv.Key, kv => taken.Contains(kv.Value) ? kv.Key : kv.Value);
    }

    /// <summary>
    /// Workload: every unsolved ticket counts, since a pending one can turn into work any minute. Open or In progress
    /// Urgent or High weighs 3, other open ones 2, Escalated, Scheduled, Pending and On-hold 1 each.
    /// </summary>
    public static int Score(WorkloadRow row) => 2 * (row.Open + row.InProgress) + row.UrgentOrHigh + Other(row);

    /// <summary>Escalated, Scheduled, Pending and On-hold together.</summary>
    private static int Other(WorkloadRow row) => row.Escalated + row.Scheduled + row.Pending;

    /// <summary>
    /// Counts the tickets of each teammate (also the ones with none), busiest first (<see cref="Score"/>): unsolved ones,
    /// and the ones solved in the last <see cref="SolvedDays"/> days.
    /// A ticket found in two groups counts once. <paramref name="statusNames"/>: custom status id → its name (agents' label).
    /// </summary>
    public static List<WorkloadRow> Count(IEnumerable<Ticket> tickets, IReadOnlyDictionary<long, string> team, long meId,
        IReadOnlyDictionary<long, string>? statusNames = null)
    {
        var counts = team.Keys.ToDictionary(id => id, _ => (Open: 0, InProgress: 0, Urgent: 0, Escalated: 0, Scheduled: 0, Pending: 0, Solved: 0));
        var escalatedBy = team.Keys.ToDictionary(id => id, _ => new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase));
        foreach (var ticket in tickets.DistinctBy(t => t.Id))
        {
            if (ticket.AssigneeId is not { } id || !counts.TryGetValue(id, out var c))
                continue;
            var name = ticket.CustomStatusId is { } status ? statusNames?.GetValueOrDefault(status) : null;
            var urgent = ticket.Priority is "urgent" or "high";
            if (ticket.Status is "solved" or "closed")
                c.Solved++;
            else if (IsEscalatedStatus(name))
            {
                c.Escalated++;
                escalatedBy[id][name!] = escalatedBy[id].GetValueOrDefault(name!) + 1;
            }
            else if (TicketRules.IsScheduled(name))
                c.Scheduled++;
            else if (IsInProgressStatus(name))
            {
                c.InProgress++;
                if (urgent)
                    c.Urgent++;
            }
            else if (ticket.Status is "new" or "open")
            {
                c.Open++;
                if (urgent)
                    c.Urgent++;
            }
            else if (ticket.Status is "pending" or "hold")
                c.Pending++;
            counts[id] = c;
        }
        return counts.Select(kv => new WorkloadRow(kv.Key, team[kv.Key], kv.Value.Open, kv.Value.Urgent, kv.Value.Escalated,
                kv.Value.Scheduled, kv.Value.Pending, kv.Key == meId, kv.Value.InProgress, escalatedBy[kv.Key], kv.Value.Solved))
            .OrderByDescending(Score).ThenByDescending(r => r.Open + r.InProgress).ThenBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }
}

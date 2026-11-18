using ZendeskWatcher.UI.Popups;

namespace ZendeskWatcher.Tracking;

/// <summary>A colleague who could take a new ticket ("▾" next to Take it on a notification).</summary>
/// <param name="Open">Open and In progress tickets, in all groups.</param>
/// <param name="UrgentOrHigh">Of those, Urgent (P1) or High (P2).</param>
/// <param name="LastActive">Their latest change or comment in Zendesk (null: none in the last 3 hours, or unreadable).</param>
/// <param name="Waiting">Pending, On-hold, Escalated and Scheduled ones: not work right now, but may be soon.</param>
/// <param name="ClientTickets">The ticket's client's tickets in the group (last 90 days) assigned to them now: a hint, not a rank.</param>
public sealed record AssignCandidate(long UserId, string Name, int Open, int UrgentOrHigh, DateTimeOffset? LastActive, int Waiting = 0,
    int ClientTickets = 0);

/// <summary>Who could take a new ticket: in working hours, active in Zendesk lately, and least busy first.</summary>
public static class AssignCandidates
{
    /// <summary>How many are offered.</summary>
    public const int Shown = 3;

    /// <summary>
    /// At most this many colleagues are looked at (each takes a few Zendesk requests): the least busy ones who work in the
    /// ticket's group and are in their working hours. Of those around, the <see cref="Shown"/> least busy in all groups are offered.
    /// </summary>
    public const int MaxChecked = 8;

    /// <summary>
    /// The order to check them in: people who work in the ticket's group (an unsolved ticket in it: a group is people's
    /// home, so a member who works elsewhere, or a manager, isn't suggested), in their working hours
    /// (<paramref name="outsideHours"/> false; unknown time zone and off work are left out), least busy first
    /// (<see cref="TeamWorkload.Score"/>), me never. <paramref name="rows"/>: the group's unsolved tickets per member.
    /// </summary>
    public static List<WorkloadRow> ToCheck(IEnumerable<WorkloadRow> rows, Func<long, bool?> outsideHours,
        IReadOnlyDictionary<long, int>? clientTickets = null) =>
        rows.Where(r => !r.IsMe && TeamWorkload.Score(r) > 0 && outsideHours(r.AssigneeId) == false)
            .OrderBy(r => Rank(r, clientTickets?.GetValueOrDefault(r.AssigneeId) ?? 0))
            .ThenBy(r => r.Open + r.InProgress).ThenBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase)
            .Take(MaxChecked).ToList();

    /// <summary>Knowing the client is worth at most this much (3 open tickets: <see cref="TeamWorkload.Score"/> counts 2 each).</summary>
    public const int MaxClientBonus = 6;

    /// <summary>
    /// Who's suggested first (lowest): how busy they are (<see cref="TeamWorkload.Score"/>), minus 1 for each of the
    /// client's tickets in the group they have (last 90 days), at most <see cref="MaxClientBonus"/>. Someone who knows the
    /// client comes first unless they're clearly busier.
    /// </summary>
    public static int Rank(WorkloadRow row, int clientTickets) => TeamWorkload.Score(row) - Math.Min(Math.Max(0, clientTickets), MaxClientBonus);

    /// <summary>The menu line: "Maria Svensson · 4 open, 1 P1/P2, 6 waiting · active 5 min ago · handled 12 of their tickets".</summary>
    public static string Line(AssignCandidate c, DateTimeOffset now)
    {
        var load = c.Open == 0 ? "nothing open" : $"{c.Open} open{(c.UrgentOrHigh > 0 ? $", {c.UrgentOrHigh} P1/P2" : "")}";
        if (c.Waiting > 0)
            load += $", {c.Waiting} waiting";
        var active = c.LastActive is { } at ? $"active {NotificationContent.Ago(now - at)}" : "in working hours";
        var client = c.ClientTickets switch
        {
            0 => "",
            1 => " · handled 1 of their tickets",
            var n => $" · handled {n} of their tickets"
        };
        return $"{c.Name} · {load} · {active}{client}";
    }
}

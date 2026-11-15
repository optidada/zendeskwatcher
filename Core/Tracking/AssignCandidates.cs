using ZendeskWatcher.UI.Popups;

namespace ZendeskWatcher.Tracking;

/// <summary>A colleague who could take a new ticket ("▾" next to Take it on a notification).</summary>
/// <param name="Open">Open and In progress tickets in the ticket's group.</param>
/// <param name="UrgentOrHigh">Of those, Urgent (P1) or High (P2).</param>
/// <param name="LastActive">Their latest change or comment in Zendesk (null: none in the last 3 hours, or unreadable).</param>
public sealed record AssignCandidate(long UserId, string Name, int Open, int UrgentOrHigh, DateTimeOffset? LastActive);

/// <summary>Who could take a new ticket: in working hours, active in Zendesk lately, and least busy first.</summary>
public static class AssignCandidates
{
    /// <summary>How many are offered.</summary>
    public const int Shown = 3;

    /// <summary>
    /// At most this many colleagues' activity is read (each takes a few Zendesk requests): the least busy ones in their
    /// working hours, until <see cref="Shown"/> are found who are around.
    /// </summary>
    public const int MaxChecked = 8;

    /// <summary>
    /// The order to check them in: in their working hours (<paramref name="outsideHours"/> false; unknown time zone
    /// and off work are left out), least busy first (<see cref="TeamWorkload.Score"/>), me never.
    /// </summary>
    public static List<WorkloadRow> ToCheck(IEnumerable<WorkloadRow> rows, Func<long, bool?> outsideHours) =>
        rows.Where(r => !r.IsMe && outsideHours(r.AssigneeId) == false)
            .OrderBy(TeamWorkload.Score).ThenBy(r => r.Open + r.InProgress).ThenBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase)
            .Take(MaxChecked).ToList();

    /// <summary>The menu line: "Maria Svensson · 4 open, 1 P1/P2 · active 5 min ago".</summary>
    public static string Line(AssignCandidate c, DateTimeOffset now)
    {
        var load = c.Open == 0 ? "nothing open" : $"{c.Open} open{(c.UrgentOrHigh > 0 ? $", {c.UrgentOrHigh} P1/P2" : "")}";
        var active = c.LastActive is { } at ? $"active {NotificationContent.Ago(now - at)}" : "in working hours";
        return $"{c.Name} · {load} · {active}";
    }
}

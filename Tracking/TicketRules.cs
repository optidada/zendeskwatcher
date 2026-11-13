using ZendeskWatcher.UI;
using ZendeskWatcher.UI.Popups;
using ZendeskWatcher.Zendesk;

namespace ZendeskWatcher.Tracking;

/// <summary>
/// The decisions behind the notifications, as plain functions without Zendesk calls, files or UI, so they can be
/// unit-tested (see tests/ZendeskWatcher.Tests). <see cref="TicketWatcher"/> does the fetching and applies these.
/// </summary>
public static class TicketRules
{
    /// <summary>
    /// The start summary as running text, e.g. "**5 unassigned tickets** are waiting: 3 in Support Tier 2 and 2 in
    /// Premium Support. The oldest was created 2 h ago. You have 3 open and 4 pending tickets, and you're CC'd on or
    /// following 2 more. You'll also hear about customer replies on Urgent (P1) and High (P2) tickets of colleagues
    /// who are off work." One line per topic (waiting, your tickets, colleagues); parts that don't apply are left out.
    /// </summary>
    /// <param name="helpPriorities">Help colleagues' priorities, or null when it's off.</param>
    public static string SummaryText(WatchSnapshot now, IReadOnlyList<WatchedGroup> groups, IReadOnlyList<string>? helpPriorities, DateTimeOffset utcNow)
    {
        static string Name(WatchedGroup g) => Markdown.Escape(g.IsNoGroup ? "No group" : g.Name);
        static string List(IReadOnlyList<string> items) => items.Count <= 1 ? string.Concat(items) : $"{string.Join(", ", items.Take(items.Count - 1))} and {items[^1]}";
        static string Plural(int n, string word) => $"{n} {word}{(n == 1 ? "" : "s")}";

        var sentences = new List<string>();
        var withTickets = now.Groups.Where(g => g.Count > 0).ToList();
        var waiting = now.Unassigned;
        if (groups.Count == 0)
            sentences.Add("You're not watching any groups yet: choose them under Settings \u2192 Groups.");
        else
        {
            var where = groups.Count == 1 ? $"in {Name(groups[0])}" : "in your groups";
            if (waiting == 0)
                sentences.Add($"No unassigned tickets are waiting {where}.");
            else
            {
                var head = $"**{Plural(waiting, "unassigned ticket")}** {(waiting == 1 ? "is" : "are")} waiting";
                sentences.Add(withTickets.Count > 1
                    ? $"{head}: {List(withTickets.Select(g => $"{g.Count} in {Name(g.Group)}").ToList())}."
                    : $"{head} in {(groups.Count == 1 ? Name(groups[0]) : Name(withTickets[0].Group))}.");
                if (now.OldestUnassigned is { } oldest)
                    sentences[^1] += $" {(waiting == 1 ? "It" : "The oldest")} was created {NotificationContent.Ago(utcNow - oldest)}.";
            }
        }

        var cc = now.Cc is { } count && count > 0 ? (now.CcAtLeast ? $"at least {count}" : count.ToString()) : null;
        if (now.Mine is var (open, pending, hold))
        {
            var parts = new[] { (open, "open"), (pending, "pending"), (hold, "on hold") }.Where(p => p.Item1 > 0).Select(p => $"{p.Item1} {p.Item2}").ToList();
            var total = open + pending + hold;
            var mine = total == 0 ? "You have no open tickets" : $"You have {List(parts)} ticket{(total == 1 ? "" : "s")}";
            sentences.Add(cc is null ? mine + "." : $"{mine}, and you're CC'd on or following {cc} {(total == 0 ? "" : "more ")}.".Replace(" .", "."));
        }
        else if (cc is not null)
            sentences.Add($"You're CC'd on or following {cc} open ticket{(cc == "1" ? "" : "s")}.");

        if (helpPriorities is not null)
        {
            var labels = Settings.AllPriorities.Where(helpPriorities.Contains).Select(p => NotificationContent.LabelForPriority(p)!).ToList();
            sentences.Add(labels.Count is 0 or 4
                ? "You'll also hear about customer replies on tickets of colleagues who are off work."
                : $"You'll also hear about customer replies on {List(labels)} tickets of colleagues who are off work.");
        }

        return string.Join("\n", sentences);
    }

    /// <summary>
    /// Really unassigned, re-read from the ticket itself (search can be stale for hours): no assignee, still open,
    /// and still in the group (no group at all for <see cref="WatchedGroup.NoGroupId"/>).
    /// </summary>
    public static bool IsReallyUnassigned(Ticket ticket, WatchedGroup group) =>
        ticket.AssigneeId is null && ticket.IsOpen && ticket.GroupId == (group.IsNoGroup ? null : group.Id);

    /// <summary>Monday to Friday: the working days unless chosen otherwise.</summary>
    public static readonly DayOfWeek[] Weekdays =
        [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday];

    /// <summary>
    /// Not a working day (<paramref name="workDays"/>, Monday to Friday if not given), or before/after the working hours
    /// (start inclusive, end exclusive), for a local time.
    /// </summary>
    public static bool IsOutsideHours(DateTime local, int startHour, int endHour, IReadOnlyCollection<DayOfWeek>? workDays = null) =>
        !(workDays ?? Weekdays).Contains(local.DayOfWeek) ||
        local.Hour < startHour || local.Hour >= endHour;

    /// <summary>Whether a ticket's priority is one of the chosen ones. No priority counts as normal.</summary>
    public static bool MatchesPriority(string? priority, ICollection<string> chosen) =>
        chosen.Contains(string.IsNullOrEmpty(priority) ? "normal" : priority);

    /// <summary>"You're CC" or "You follow" when the user is on the ticket that way, otherwise null.</summary>
    public static string? Involvement(Ticket ticket, long me) =>
        ticket.EmailCcIds.Contains(me) || ticket.CollaboratorIds.Contains(me) ? "You're CC" :
        ticket.FollowerIds.Contains(me) ? "You follow" : null;

    /// <summary>
    /// For a snoozed popup: a new unassigned ticket must still be unassigned and open; any other ticket must not be
    /// solved or closed. Null (deleted, or no longer visible): not needed.
    /// </summary>
    public static bool StillNeedsAttention(NotificationKind kind, Ticket? ticket) =>
        ticket is not null && (kind == NotificationKind.NewTicket ? ticket.IsOpen && ticket.AssigneeId is null : ticket.IsOpen);

    /// <summary>
    /// Whether what a notification reports has been dealt with by the changes made after it (<paramref name="after"/>,
    /// each with its author's Zendesk role): a customer reply on a colleague's ticket once an agent replied publicly;
    /// a change on my own or a CC'd ticket once I changed or commented on the ticket myself. New tickets: never (see
    /// <see cref="StillNeedsAttention"/>).
    /// </summary>
    public static bool HandledSince(NotificationKind kind, IEnumerable<(Audit Audit, string? AuthorRole)> after, long me) => kind switch
    {
        NotificationKind.HelpColleague => after.Any(a =>
            a.Audit.AuthorId > 0 && a.AuthorRole is "agent" or "admin" &&
            a.Audit.Events.Any(e => e.Type == "Comment" && e.Public == true)),
        NotificationKind.MyTicket or NotificationKind.CcTicket => after.Any(a => a.Audit.AuthorId == me),
        _ => false
    };

    /// <summary>"Good morning, Daniel" (05-10), "Good day, Daniel" (10-18), "Good evening, Daniel" (otherwise).</summary>
    public static string Greeting(DateTime localTime, string? fullName)
    {
        var greeting = localTime.Hour switch
        {
            >= 5 and < 10 => "Good morning",
            >= 10 and < 18 => "Good day",
            _ => "Good evening"
        };
        var firstName = fullName?.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return firstName is null ? greeting : $"{greeting}, {firstName}";
    }

    /// <summary>"pending" → "Pending", "hold" → "On-hold", as Zendesk shows them.</summary>
    public static string? StatusLabel(string? status) => status?.ToLowerInvariant() switch
    {
        null or "" => null,
        "hold" => "On-hold",
        var other => char.ToUpperInvariant(other[0]) + other[1..]
    };

    /// <summary>A time zone from a profile (IANA or Windows id), or null if it's missing or unknown on this PC.</summary>
    public static TimeZoneInfo? ResolveTimeZone(string? id) =>
        !string.IsNullOrWhiteSpace(id) && TimeZoneInfo.TryFindSystemTimeZoneById(id, out var zone) ? zone : null;
}

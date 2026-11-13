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
    /// Status changes not worth a notification: New → Open (it just got picked up; the assignment says so) and
    /// Solved → Closed (Zendesk closes solved tickets by itself after a few days). Also no real change.
    /// </summary>
    public static bool IsNoiseStatusChange(string? from, string? to) =>
        string.Equals(from, to, StringComparison.OrdinalIgnoreCase)
        || (string.Equals(from, "New", StringComparison.OrdinalIgnoreCase) && string.Equals(to, "Open", StringComparison.OrdinalIgnoreCase))
        || (string.Equals(from, "Solved", StringComparison.OrdinalIgnoreCase) && string.Equals(to, "Closed", StringComparison.OrdinalIgnoreCase));

    /// <summary>Whether a status name means someone is working on it ("In progress"): then the customer may wait a while.</summary>
    public static bool IsInProgress(string? statusLabel) =>
        statusLabel?.Contains("in progress", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>
    /// A customer who wrote last is worth a notification whatever the status (pending, on-hold, escalated, …): they
    /// still need an answer. Only "In progress" is trusted for <paramref name="grace"/> after their reply.
    /// </summary>
    public static bool CustomerWaitingNeedsNotice(string? statusLabel, DateTimeOffset repliedAt, DateTimeOffset now, TimeSpan grace) =>
        !IsInProgress(statusLabel) || now - repliedAt >= grace;

    /// <summary>
    /// Whether a comment's author is the customer: an end-user, but not someone from your own company (an end-user
    /// with your email domain, e.g. a salesperson CC'd from their mailbox). The requester always counts as the
    /// customer, so internal tickets opened by a colleague still work. Agents and admins are never the customer.
    /// </summary>
    public static bool IsCustomer(User? author, Ticket ticket, string? myEmail) =>
        author?.Role == "end-user" && (author.Id == ticket.RequesterId || !SameDomain(author.Email, myEmail));

    private static bool SameDomain(string? a, string? b)
    {
        static string? Domain(string? email) => email?.LastIndexOf('@') is > 0 and var at ? email[(at + 1)..].Trim().ToLowerInvariant() : null;
        return Domain(a) is { Length: > 0 } x && x == Domain(b);
    }

    /// <summary>
    /// Where Catch up starts: when you stopped working last time, i.e. the end of your previous working day (yesterday
    /// 17:00; Friday 17:00 on a Monday or the weekend). So replies that came in while you were off count too.
    /// </summary>
    public static DateTime CatchUpStart(DateTime localNow, int startHour, int endHour, IReadOnlyCollection<DayOfWeek>? workDays = null)
    {
        var days = workDays is { Count: > 0 } ? workDays : Weekdays;
        startHour = Math.Clamp(startHour, 0, 23);
        endHour = Math.Clamp(endHour, startHour + 1, 24);
        // The latest end of a working day that's already past; while you're working, the one before today.
        var working = days.Contains(localNow.DayOfWeek) && localNow >= localNow.Date.AddHours(startHour) && localNow < localNow.Date.AddHours(endHour);
        var day = working ? localNow.Date.AddDays(-1) : localNow.Date;
        for (var i = 0; i < 8; i++, day = day.AddDays(-1))
            if (days.Contains(day.DayOfWeek) && day.AddHours(endHour) <= localNow)
                return day.AddHours(endHour);
        return localNow.AddHours(-16); // no working days found: about since last evening
    }

    /// <summary>"since 17:00", "since yesterday 17:00" or "since Fri 17:00".</summary>
    public static string CatchUpSince(DateTime since, DateTime localNow)
    {
        var time = since.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);
        return since.Date == localNow.Date ? $"since {time}"
            : since.Date == localNow.Date.AddDays(-1) ? $"since yesterday {time}"
            : $"since {since.ToString("ddd", System.Globalization.CultureInfo.GetCultureInfo("en-GB"))} {time}";
    }

    /// <summary>
    /// The Catch up summary: always both questions, unassigned tickets and waiting customers, e.g.
    /// "**11 unassigned tickets** in your groups." and "No customer is waiting for an answer since yesterday 17:00."
    /// or "**2 customers** wrote since yesterday 17:00 and are waiting for your reply."
    /// </summary>
    public static string CatchUpText(DateTime since, DateTime localNow, int unassigned, int mine, int cc, int colleagues)
    {
        var when = CatchUpSince(since, localNow);
        var lines = new List<string>
        {
            unassigned switch
            {
                0 => "No unassigned tickets in your groups.",
                1 => "**1 unassigned ticket** in your groups.",
                _ => $"**{unassigned} unassigned tickets** in your groups."
            }
        };
        string Waiting(int n, string where) => n == 1 ? $"**1 customer** wrote {when} and is waiting {where}." : $"**{n} customers** wrote {when} and are waiting {where}.";
        if (mine + cc + colleagues == 0)
            lines.Add($"No customer is waiting for an answer {when}.");
        if (mine > 0)
            lines.Add(Waiting(mine, "for your reply"));
        if (cc > 0)
            lines.Add(Waiting(cc, "on tickets you're CC'd on or follow"));
        if (colleagues > 0)
            lines.Add(Waiting(colleagues, "on off-work colleagues' tickets"));
        return string.Join("\n", lines);
    }

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

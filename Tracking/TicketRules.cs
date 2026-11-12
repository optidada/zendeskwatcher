using ZendeskWatcher.Zendesk;

namespace ZendeskWatcher.Tracking;

/// <summary>
/// The decisions behind the notifications, as plain functions without Zendesk calls, files or UI, so they can be
/// unit-tested (see tests/ZendeskWatcher.Tests). <see cref="TicketWatcher"/> does the fetching and applies these.
/// </summary>
public static class TicketRules
{
    /// <summary>
    /// Really unassigned, re-read from the ticket itself (search can be stale for hours): no assignee, still open,
    /// and still in the group (no group at all for <see cref="WatchedGroup.NoGroupId"/>).
    /// </summary>
    public static bool IsReallyUnassigned(Ticket ticket, WatchedGroup group) =>
        ticket.AssigneeId is null && ticket.IsOpen && ticket.GroupId == (group.IsNoGroup ? null : group.Id);

    /// <summary>Weekend, or before/after the working hours (start inclusive, end exclusive), for a local time.</summary>
    public static bool IsOutsideHours(DateTime local, int startHour, int endHour) =>
        local.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday ||
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

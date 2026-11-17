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

    /// <summary>
    /// A change to Open that comes with a public reply from the same person in the same update: Zendesk reopens a ticket
    /// when someone replies (e.g. the customer on a Pending ticket), so the reply line already says it.
    /// </summary>
    public static bool IsImpliedReopen(string? to, bool withPublicReply) =>
        withPublicReply && string.Equals(to, "Open", StringComparison.OrdinalIgnoreCase);

    /// <summary>"Scheduled" (a custom status, matched by name): on an unassigned ticket, a time is booked and nobody owns it.</summary>
    public static bool IsScheduled(string? statusLabel) =>
        statusLabel?.Contains("scheduled", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>", **2 of them Scheduled**" after a count of unassigned tickets; empty when none are.</summary>
    public static string ScheduledNote(int total, int scheduled) => scheduled <= 0 ? ""
        : total == 1 ? ", **Scheduled**"
        : scheduled >= total ? $", **{(total == 2 ? "both" : "all")} Scheduled**"
        : $", **{scheduled} of them Scheduled**";


    /// <summary>
    /// How the ticket's client should notify right now (Clients page): Normal unless a choice was made. A ticket
    /// without an organization is "(No client)"; a mute for a while that has run out is Normal again.
    /// </summary>
    public static ClientMode ClientMode(Ticket ticket, Settings settings, DateTimeOffset now) =>
        settings.ClientChoiceFor(ticket.OrganizationId)?.ModeAt(now) ?? ZendeskWatcher.ClientMode.Normal;

    /// <summary>The confirmation after a choice from a notification's "Options ▾" menu: a title and what it means.</summary>
    public static (string Title, string Body) ClientChoiceConfirmation(ClientChoice choice, DateTimeOffset now)
    {
        const string where = "Change it under Settings → Groups → Clients.";
        return choice.ModeAt(now) switch
        {
            ZendeskWatcher.ClientMode.Muted when choice.MutedUntil is { } until =>
                ($"Muted {choice.Name} until {Clock.DayTime(until.ToLocalTime().DateTime)}",
                 $"No notifications about their new tickets or colleagues' tickets until then. Your own tickets still notify. {where}"),
            ZendeskWatcher.ClientMode.Muted =>
                ($"Muted {choice.Name}", $"No more notifications about their new tickets or colleagues' tickets. Your own tickets still notify. {where}"),
            ZendeskWatcher.ClientMode.OnlyUrgent =>
                ($"{choice.Name}: only urgent", $"Only Urgent (P1) and High (P2) new tickets and colleagues' tickets notify. {where}"),
            ZendeskWatcher.ClientMode.Important =>
                ($"{choice.Name} is important ★", $"Their tickets always notify, also outside your hours, and stay until you close them. {where}"),
            _ => ($"{choice.Name}: back to normal", where)
        };
    }

    /// <summary>
    /// Whether a new ticket or a customer reply on a colleague's ticket from this client may notify: not when muted,
    /// and only Urgent (P1) and High (P2) with "only urgent". Your own and CC'd tickets don't use this.
    /// </summary>
    public static bool IsWantedClient(Ticket ticket, ClientMode mode) => mode switch
    {
        ZendeskWatcher.ClientMode.Muted => false,
        ZendeskWatcher.ClientMode.OnlyUrgent => ticket.Priority?.ToLowerInvariant() is "urgent" or "high",
        _ => true
    };

    /// <summary>
    /// Whether a comment's author is the customer: an end-user, but not someone from your own company (an end-user
    /// with your email domain, e.g. a salesperson CC'd from their mailbox). The requester always counts as the
    /// customer, so internal tickets opened by a colleague still work. Agents and admins are never the customer.
    /// </summary>
    public static bool IsCustomer(User? author, Ticket ticket, string? myEmail) =>
        author?.Role == "end-user" && (author.Id == ticket.RequesterId || !SameDomain(author.Email, myEmail));

    /// <summary>
    /// An integration account rather than a person: tagged "serviceaccountuser" in Zendesk (e.g. the "Service User"
    /// that greets new tickets and routes them, through the API, so its changes aren't marked as a trigger's).
    /// </summary>
    public static bool IsServiceAccount(User? user) =>
        user?.Tags?.Any(t => string.Equals(t, "serviceaccountuser", StringComparison.OrdinalIgnoreCase)) == true;

    /// <summary>
    /// A change nobody made by hand: by a service account (<see cref="IsServiceAccount"/>), or by a Zendesk trigger or
    /// automation (via "rule", on the update or on the event a trigger added to someone's update). Status changes never
    /// count as automatic: a ticket reopened or closed as "No response" matters whoever did it.
    /// </summary>
    public static bool IsAutomaticChange(AuditEvent ev, Audit audit, bool byServiceAccount) =>
        ev is not { Type: "Change", FieldName: "status" or "custom_status_id" } &&
        (byServiceAccount || audit.Via?.Channel == "rule" || ev.Via?.Channel == "rule");

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
        var time = Clock.Time(since);
        return since.Date == localNow.Date ? $"since {time}"
            : since.Date == localNow.Date.AddDays(-1) ? $"since yesterday {time}"
            : $"since {since.ToString("ddd", System.Globalization.CultureInfo.GetCultureInfo("en-GB"))} {time}";
    }

    /// <summary>What Catch up found, counted (the "since" time goes in the heading, see <see cref="CatchUpSince"/>).</summary>
    public sealed record CatchUpCounts
    {
        public int Mine { get; init; }
        public int Cc { get; init; }
        public int Colleagues { get; init; }
        public int Unassigned { get; init; }
        public int Scheduled { get; init; }
        public int SlaPast { get; init; }
        public int SlaToday { get; init; }
        public DateTime? FirstDueToday { get; init; }
        public int QuietEscalated { get; init; }
        public int QuietPending { get; init; }
        public bool NoGroups { get; init; }

        /// <summary>The things that need you (the "N things need attention" count).</summary>
        public int Attention => Mine + Cc + Colleagues + Unassigned + SlaPast + SlaToday + QuietEscalated + QuietPending;
    }

    /// <summary>
    /// The Catch up text, quick to scan: one line per thing that needs you, the bold number first, most urgent first
    /// (waiting customers, SLA, unassigned, things to follow up); nothing about what's zero. Then, after an empty line,
    /// your tickets by status (<paramref name="yourTickets"/>, null when your own tickets aren't tracked), e.g.
    /// "**2** waiting for your reply" / "**1** past SLA · **2** due today, first 10:30" / "**5** unassigned in your groups · 1 Scheduled".
    /// </summary>
    public static string CatchUpBody(CatchUpCounts c, string? yourTickets)
    {
        var lines = new List<string>();
        if (c.NoGroups)
            lines.Add("No groups watched yet: choose them on the **Groups** page in Settings.");
        if (c.Mine > 0)
            lines.Add($"**{c.Mine}** waiting for your reply");
        var first = c.FirstDueToday is { } due ? $", first {Clock.Time(due)}" : "";
        if (c.SlaPast > 0)
            lines.Add($"**{c.SlaPast}** past SLA" + (c.SlaToday > 0 ? $" · **{c.SlaToday}** due today{first}" : ""));
        else if (c.SlaToday > 0)
            lines.Add($"**{c.SlaToday}** SLA due today{first}");
        if (c.Unassigned > 0)
            lines.Add($"**{c.Unassigned}** unassigned in your groups" + (c.Scheduled > 0 ? $" · {c.Scheduled} Scheduled" : ""));
        if (c.Cc > 0)
            lines.Add($"**{c.Cc}** waiting on tickets you're CC'd on or follow");
        if (c.Colleagues > 0)
            lines.Add($"**{c.Colleagues}** waiting on colleagues' tickets");
        if (c.QuietEscalated > 0)
            lines.Add($"**{c.QuietEscalated}** Escalated, no reply to the client for {EscalatedQuietFor.TotalDays:0}+ days");
        if (c.QuietPending > 0)
            lines.Add($"**{c.QuietPending}** Pending, no update for {PendingQuietFor.TotalDays:0}+ days");
        if (c.Attention == 0 && !c.NoGroups)
            lines.Add("Nothing needs you right now");
        if (yourTickets is not null)
            lines.Add("\n" + yourTickets); // set apart: the overview, not something to act on
        return string.Join("\n", lines);
    }

    /// <summary>Escalated tickets without a public reply to the customer for this long are counted in Catch up.</summary>
    public static readonly TimeSpan EscalatedQuietFor = TimeSpan.FromDays(2);

    private const char Nbsp = '\u00a0';

    /// <summary>
    /// Catch up's overview of your own unsolved tickets, by status as agents see it, e.g.
    /// "Your tickets: 4 Open (1 P1) · 2 In progress · 1 Escalated · 1 Scheduled · 3 Pending". New and Open-like statuses
    /// first, then Pending, then On-hold; P1 / P2 counted per status. <paramref name="tickets"/>: (status name, basic
    /// status, priority) of each ticket.
    /// </summary>
    public static string YourTicketsText(IReadOnlyList<(string Name, string? Status, string? Priority)> tickets)
    {
        if (tickets.Count == 0)
            return "You have no open tickets.";
        static int Rank(string? status) => status switch { "new" => 0, "open" => 1, "pending" => 2, "hold" => 3, _ => 4 };
        var parts = tickets
            .GroupBy(t => t.Name.Length == 0 ? "Open" : t.Name, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Min(t => Rank(t.Status)))
            .ThenBy(g => g.Key is "New" or "Open" ? 0 : 1)
            .ThenBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase)
            .Select(g =>
            {
                var p1 = g.Count(t => t.Priority == "urgent");
                var p2 = g.Count(t => t.Priority == "high");
                var urgent = new[] { (p1, "P1"), (p2, "P2") }.Where(p => p.Item1 > 0).Select(p => $"{p.Item1}{Nbsp}{Markdown.Tag(p.Item2)}").ToList();
                // No break inside "1 Escalated (1 P1)": the line wraps between statuses.
                return $"{g.Count()}{Nbsp}{Markdown.Escape(g.Key).Replace(' ', Nbsp)}" + (urgent.Count > 0 ? $"{Nbsp}({string.Join($",{Nbsp}", urgent)})" : "");
            });
        return $"Your tickets:\n{string.Join(" · ", parts)}"; // the counts below the heading, like "To work on:"
    }

    /// <summary>Catch up lists at most this many of the tickets you're working on.</summary>
    public const int MaxWorkingOn = 5;

    /// <summary>One of your tickets for Catch up's "To work on" list.</summary>
    public readonly record struct WorkItem(long Id, string Subject, string StatusName, string? Status, string? Priority,
        DateTimeOffset? SlaDue, DateTimeOffset UpdatedAt);

    /// <summary>
    /// Catch up's reminder of what you're working on: your New, Open and In progress tickets (not Pending, On-hold or
    /// Escalated: those wait on someone else), most urgent first, then the nearest SLA, then the longest untouched.
    /// "#123 [P1] Subject" per line, at most <see cref="MaxWorkingOn"/>, then "+N more". Null when there are none.
    /// </summary>
    public static string? WorkingOnText(IEnumerable<WorkItem> tickets)
    {
        static int Rank(string? priority) => priority switch { "urgent" => 0, "high" => 1, "low" => 3, _ => 2 };
        var work = tickets
            .Where(t => t.Status is "new" or "open" && !TeamWorkload.IsEscalatedStatus(t.StatusName))
            .OrderBy(t => Rank(t.Priority))
            .ThenBy(t => t.SlaDue ?? DateTimeOffset.MaxValue)
            .ThenBy(t => t.UpdatedAt)
            .ToList();
        if (work.Count == 0)
            return null;
        var lines = work.Take(MaxWorkingOn).Select(t =>
        {
            var tag = t.Priority is "urgent" or "high" ? Markdown.Tag(t.Priority) + Nbsp : "";
            var subject = TextCleanup.Plain(t.Subject);
            if (subject.Length > 60)
                subject = subject[..59].TrimEnd() + "…";
            return $"#{t.Id} {tag}{Markdown.Escape(subject)}";
        }).ToList();
        if (work.Count > MaxWorkingOn)
            lines.Add($"+{work.Count - MaxWorkingOn} more");
        return "To work on:\n" + string.Join("\n", lines);
    }

    /// <summary>A priority in a notification's text: "Urgent [P1]" (a tag, kept on the line with its word); "None" for none.</summary>
    public static string PriorityWithTag(string? priority) => priority?.ToLowerInvariant() switch
    {
        null or "" => "None",
        "urgent" or "high" or "normal" or "low" => $"{char.ToUpperInvariant(priority[0])}{priority[1..].ToLowerInvariant()}{Nbsp}{Markdown.Tag(priority)}",
        _ => Markdown.Escape(priority)
    };

    /// <summary>A colleague who changed or commented on a ticket this recently is working, whatever their hours say.</summary>
    public static readonly TimeSpan ActiveWithin = TimeSpan.FromMinutes(30);

    /// <summary>A colleague with no activity in Zendesk for this long counts as away, even in their working hours.</summary>
    public static readonly TimeSpan InactiveFor = TimeSpan.FromHours(3);

    /// <summary>
    /// Whether to step in on a colleague's ticket: they're off work by their hours, unless they did something in Zendesk
    /// in the last 30 minutes; or it's their working hours but they've done nothing for 3 hours. Unknown time zone: null
    /// (skipped). <paramref name="lastActive"/>: their latest change or comment in the last 3 hours (null: none).
    /// <paramref name="activityKnown"/>: false when it couldn't be read; then only the hours count.
    /// </summary>
    public static bool? ColleagueAway(bool? outsideHours, bool activityKnown, DateTimeOffset? lastActive, DateTimeOffset now)
    {
        if (!activityKnown)
            return outsideHours;
        if (lastActive is { } recent && now - recent < ActiveWithin)
            return false;
        if (outsideHours is not false)
            return outsideHours; // off work: step in; unknown time zone: always skipped (Colleagues page)
        return lastActive is null || now - lastActive >= InactiveFor;
    }

    /// <summary>Pending tickets without any update for this long are counted in Catch up (follow up, or solve them).</summary>
    public static readonly TimeSpan PendingQuietFor = TimeSpan.FromDays(7);

    /// <summary>
    /// Really unassigned, re-read from the ticket itself (search can be stale for hours): no assignee, still open,
    /// and still in the group (no group at all for <see cref="WatchedGroup.NoGroupId"/>).
    /// </summary>
    public static bool IsReallyUnassigned(Ticket ticket, WatchedGroup group) =>
        ticket.AssigneeId is null && ticket.IsOpen && ticket.GroupId == (group.IsNoGroup ? null : group.Id);

    /// <summary>Someone took the ticket, or it was solved: its new-ticket notification is no longer needed.</summary>
    public static bool IsTaken(Ticket ticket) => ticket.AssigneeId is not null || !ticket.IsOpen;

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

    /// <summary>When the working shift you're in ends (local time), or null outside your working hours.</summary>
    public static DateTime? ShiftEnd(DateTime local, int startHour, int endHour, IReadOnlyCollection<DayOfWeek>? workDays = null) =>
        IsOutsideHours(local, startHour, endHour, workDays) ? null : local.Date.AddHours(endHour);

    /// <summary>When your next working shift starts (local time): later today if it hasn't started yet, else the next working day.</summary>
    public static DateTime NextShiftStart(DateTime local, int startHour, IReadOnlyCollection<DayOfWeek>? workDays = null)
    {
        var days = workDays is { Count: > 0 } chosen ? chosen : Weekdays;
        for (var day = local.Date; ; day = day.AddDays(1))
            if (days.Contains(day.DayOfWeek) && day.AddHours(startHour) > local)
                return day.AddHours(startHour);
    }

    /// <summary>Whether a ticket's priority is one of the chosen ones. No priority counts as normal.</summary>
    public static bool MatchesPriority(string? priority, IEnumerable<string> chosen) =>
        chosen.Contains(string.IsNullOrEmpty(priority) ? "normal" : priority);

    /// <summary>Low 0, normal (or none) 1, high 2, urgent 3.</summary>
    public static int PriorityRank(string? priority) => priority?.ToLowerInvariant() switch
    {
        "urgent" => 3,
        "high" => 2,
        "low" => 0,
        _ => 1
    };

    /// <summary>
    /// A new unassigned ticket kept quiet for its priority notifies once it's raised to a ticked one while still
    /// unassigned. Ticking the priority later doesn't count as raised (no burst of old tickets).
    /// </summary>
    public static bool WasRaisedToWanted(string? quietPriority, string? priority, IEnumerable<string> chosen) =>
        quietPriority is not null && MatchesPriority(priority, chosen) && PriorityRank(priority) > PriorityRank(quietPriority);

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

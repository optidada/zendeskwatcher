using ZendeskWatcher.UI;
using ZendeskWatcher.UI.Popups;
using ZendeskWatcher.Zendesk;

namespace ZendeskWatcher.Tracking;

/// <summary>Why Catch up runs: clicked, your working day started, or the app started (the greeting).</summary>
public enum CatchUpReason { Requested, DayStart, AppStart }

/// <summary>
/// "Catch up": on request, what still needs attention since you stopped working last time (the end of your previous
/// working day): tickets still unassigned in your groups, and customers still waiting for an answer on your tickets,
/// CC'd / followed tickets and (by the Colleagues rules) off-work colleagues' tickets. Replies that came in while you
/// were off count too. Shown as notifications, or as one summary when there are many.
/// </summary>
public sealed partial class TicketWatcher
{
    /// <summary>Waiting customers up to this many get a notification each (they matter most).</summary>
    internal const int MaxSeparateWaiting = 5;

    /// <summary>Unassigned tickets up to this many get a notification each; more are counted in the summary.</summary>
    internal const int MaxSeparateUnassigned = 3;

    /// <summary>What Catch up found, by kind. <see cref="Since"/> is when you stopped working last time.</summary>
    public sealed record CatchUpResult(DateTime Since, List<NotificationContent> Unassigned, List<NotificationContent> Mine,
        List<NotificationContent> Cc, List<NotificationContent> Colleagues)
    {
        public int Count => Unassigned.Count + Mine.Count + Cc.Count + Colleagues.Count;
        public IEnumerable<NotificationContent> All => Unassigned.Concat(Mine).Concat(Cc).Concat(Colleagues);
    }

    /// <summary>
    /// Catch up is due by itself: it's on (<see cref="Settings.CatchUpAtDayStart"/>), you're working, and it hasn't run
    /// since your working day started. The very first time it only remembers the moment, so a new install isn't flooded.
    /// </summary>
    internal bool DayStartCatchUpDue(DateTime localNow)
    {
        if (!settings.CatchUpAtDayStart || TicketRules.IsOutsideHours(localNow, settings.MyWorkdayStartHour, settings.MyWorkdayEndHour, settings.MyWorkDays))
            return false;
        if (_state.LastCatchUp == default)
        {
            _state.LastCatchUp = localNow;
            return false;
        }
        return _state.LastCatchUp.Date < localNow.Date; // once a day: not when it already came today (start, or asked for)
    }

    public bool DayStartCatchUpDue() => DayStartCatchUpDue(DateTime.Now);

    /// <summary>A Catch up already came today: a later start of the app (an update, a restart) doesn't bring another.</summary>
    public bool CaughtUpToday => _state.LastCatchUp.Date == DateTime.Now.Date;

    /// <summary>
    /// Finds what still needs attention since you stopped working last time, and shows it: a greeting with the counts
    /// and your tickets by status (always), plus the waiting customers and a few unassigned tickets one by one.
    /// When the app starts, the one-by-one ones only come if it's also the first Catch up of your working day.
    /// </summary>
    /// <param name="since">For tests; normally worked out from your working hours.</param>
    public async Task<CatchUpResult> CatchUpAsync(CancellationToken ct, DateTime? since = null, CatchUpReason reason = CatchUpReason.Requested)
    {
        ZendeskClient.Reason.Value = "catch up";
        var requestedByUser = reason == CatchUpReason.Requested;
        var oneByOne = reason != CatchUpReason.AppStart || !CaughtUpToday;
        _state.LastCatchUp = DateTime.Now;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var requestsBefore = ZendeskClient.RequestCount;
        var timingsBefore = ZendeskClient.Timings;
        var start = since ?? TicketRules.CatchUpStart(DateTime.Now, settings.MyWorkdayStartHour, settings.MyWorkdayEndHour, settings.MyWorkDays);
        var sinceUtc = new DateTimeOffset(start).ToUniversalTime();
        var me = Me!.Id;
        var result = new CatchUpResult(start, [], [], [], []);

        // 1. Still unassigned: from the check that just ran (verified, not the search index). Only a few come one by one;
        // more are just counted, so they aren't looked up in full (client, SLA, requester: requests for nothing).
        var oneByOneUnassigned = _unassigned.Sum(g => g.Tickets.Count) <= MaxSeparateUnassigned;
        foreach (var (group, tickets) in _unassigned)
            foreach (var ticket in tickets.OrderBy(t => t.CreatedAt))
                result.Unassigned.Add(oneByOneUnassigned
                    ? await UnassignedTicketAsync(ticket, group, "Catch up · Unassigned", ct)
                    : new NotificationContent(NotificationKind.NewTicket, "Catch up · Unassigned", $"#{ticket.Id} {TextCleanup.Plain(ticket.Subject)}", "",
                        ticket.Id, ticket.Priority, TicketLabel(ticket, group)) { ScheduledCount = IsScheduled(ticket) ? 1 : 0, IsTicketNotification = true });

        // 2 and 3. Your own and CC'd / followed tickets where a customer wrote last (copied first: the check may change them).
        // Read side by side with the colleagues' tickets (step 4): the slow part is reading ticket histories one by one, so
        // up to 3 run at once (Zendesk: a short burst, well within its limits).
        var mineIds = settings.TrackMyTickets ? _state.MyTickets.Keys.ToList() : [];
        var ccIds = settings.TrackCcTickets ? _state.CcTickets.Keys.Except(mineIds).ToList() : [];
        var trackedTask = mineIds.Count + ccIds.Count > 0 ? zendesk.GetTicketsAsync(mineIds.Concat(ccIds).ToList(), ct) : Task.FromResult(new List<Ticket>());
        var colleagueTask = Task.WhenAll(settings.EffectiveHelpGroups.Select(async group =>
            (await zendesk.GetAssignedOpenTicketsUpdatedSinceAsync(group.Id, sinceUtc, ct)).Select(t => (Group: group, Ticket: t)).ToList()));
        await Task.WhenAll(trackedTask, colleagueTask);
        var fetched = clock.Elapsed;
        var tracked = await trackedTask;
        var now = DateTimeOffset.UtcNow;
        var turns = new SemaphoreSlim(3); // not disposed: if one read fails, the others may still be finishing with it
        async Task<T> InTurn<T>(Func<Task<T>> work)
        {
            await turns.WaitAsync(ct);
            try { return await work(); }
            finally { turns.Release(); }
        }

        // Any status before solved: a customer who wrote last needs an answer.
        var trackedWork = tracked.Where(t => t.IsOpen && t.UpdatedAt >= sinceUtc).Select(ticket => InTurn(async () =>
        {
            if (await CustomerWaitingAsync(ticket, sinceUtc, ct) is not { } waiting || IsQuietThankYou(waiting.Comment))
                return null;
            var (customer, comment, at) = waiting;
            var isMine = ticket.AssigneeId == me;
            var said = Said(customer, comment, verb: "wrote");
            var content = new NotificationContent(isMine ? NotificationKind.MyTicket : NotificationKind.CcTicket,
                isMine ? "Catch up · Waiting for you" : "Catch up · CC / following",
                $"#{ticket.Id} {TextCleanup.Plain(ticket.Subject)}",
                (isMine ? $"{said}\nNo reply yet" : $"{said}\nNo agent has answered yet") + StatusSuffix(ticket),
                ticket.Id, ticket.Priority, GroupName(ticket.GroupId))
            { EventTime = at, ChangedAt = at, IsTicketNotification = true };
            return await WithClientAsync(content, ticket, null, ct, sla: (at, FirstReply: false));
        })).ToList();

        // 4. Colleagues' tickets, by the Colleagues rules: customer waiting, colleague off work right now. Whether the
        // colleague is away is asked first (one request per colleague, remembered 15 minutes): most are at work, and then
        // their tickets' histories aren't read at all. The same result, far fewer requests.
        var skip = mineIds.Concat(ccIds).ToHashSet();
        var colleagueTickets = (await colleagueTask).SelectMany(g => g).ToList();
        // Their assignees in one go (100 per request), not one request each.
        await zendesk.PrefetchUsersAsync(colleagueTickets.Select(c => c.Ticket.AssigneeId).OfType<long>(), ct);
        var candidates = new List<(WatchedGroup Group, Ticket Ticket, long AssigneeId)>();
        foreach (var (group, ticket) in colleagueTickets)
        {
            if (ticket.AssigneeId is not { } assigneeId || assigneeId == me || !skip.Add(ticket.Id))
                continue;
            if ((!TicketRules.MatchesPriority(ticket.Priority, settings.HelpPriorities) && !IsImportant(ticket)) || !TicketRules.IsWantedClient(ticket, ClientMode(ticket)) ||
                !await WantedProductAsync(ticket, ct))
                continue;
            var assignee = await zendesk.GetUserAsync(assigneeId, ct);
            if (!settings.CoversColleague(assigneeId, TimeRegions.Of(TimeRegions.ZoneOf(assignee)), TimeRegions.Mine(Me)))
                continue;
            if (OutsideWorkingHours(assignee, DateTimeOffset.UtcNow).Away is null)
                continue; // unknown time zone: never stepped in for, so the ticket's history isn't read for nothing
            candidates.Add((group, ticket, assigneeId));
        }
        var awayWork = candidates.Select(c => c.AssigneeId).Distinct().Select(id => InTurn(async () =>
            (Id: id, Away: await ColleagueAwayAsync(id, await zendesk.GetUserAsync(id, ct), DateTimeOffset.UtcNow, ct)))).ToList();

        // Your own Escalated tickets where the customer hasn't heard from anyone lately (also side by side).
        var myOpen = tracked.Where(t => t.AssigneeId == me && t.IsOpen).ToList();
        var escalatedWork = myOpen.Where(t => TeamWorkload.IsEscalatedStatus(StatusName(t)))
            .Select(ticket => InTurn(() => RepliedToCustomerSinceAsync(ticket, now - TicketRules.EscalatedQuietFor, ct))).ToList();

        var away = (await Task.WhenAll(awayWork)).Where(a => a.Away.Away is true).ToDictionary(a => a.Id, a => a.Away.Why);
        var awayChecked = clock.Elapsed;
        var colleagueWork = candidates.Where(c => away.ContainsKey(c.AssigneeId)).Select(c => InTurn(async () =>
        {
            if (await CustomerWaitingAsync(c.Ticket, sinceUtc, ct) is not { } waiting || IsQuietThankYou(waiting.Comment))
                return null;
            var (customer, comment, at) = waiting;
            var said = Said(customer, comment, verb: "wrote");
            return await WithClientAsync(new NotificationContent(NotificationKind.HelpColleague, "Catch up · Colleague's ticket",
                $"#{c.Ticket.Id} {TextCleanup.Plain(c.Ticket.Subject)}",
                WhyUnderName(said, away[c.AssigneeId] + StatusSuffix(c.Ticket)),
                c.Ticket.Id, c.Ticket.Priority, TicketLabel(c.Ticket, c.Group))
            { EventTime = at, ChangedAt = at, IsTicketNotification = true }, c.Ticket, c.Group, ct, sla: (at, FirstReply: false));
        })).ToList();

        foreach (var content in (await Task.WhenAll(trackedWork)).OfType<NotificationContent>())
            (content.Kind == NotificationKind.MyTicket ? result.Mine : result.Cc).Add(content);
        result.Colleagues.AddRange((await Task.WhenAll(colleagueWork)).OfType<NotificationContent>());
        var quietEscalated = (await Task.WhenAll(escalatedWork)).Count(replied => !replied);
        // Where the time went (the parts overlap: each is when it was done): for finding what's still slow.
        var timing = $"tickets read {fetched.TotalSeconds:0.0}s, {awayWork.Count} colleagues checked by {awayChecked.TotalSeconds:0.0}s " +
                     $"({away.Count} away), histories {trackedWork.Count} own/CC + {colleagueWork.Count} colleagues' + {escalatedWork.Count} escalated, " +
                     $"done {clock.Elapsed.TotalSeconds:0.0}s, {ZendeskClient.RequestCount - requestsBefore} requests " +
                     $"(waiting for Zendesk {(ZendeskClient.Timings.Waiting - timingsBefore.Waiting).TotalSeconds:0.0}s in all, " +
                     $"converting {(ZendeskClient.Timings.Reading - timingsBefore.Reading).TotalMilliseconds:0} ms, " +
                     $"{(ZendeskClient.Timings.Chars - timingsBefore.Chars) / 1000} KB received)";

        // Pending ones nobody has touched for a week, and Zendesk's SLA timers: past, or breaching before midnight.
        var quietPending = myOpen.Count(t => t.Status == "pending" && t.UpdatedAt < now - TicketRules.PendingQuietFor);
        var breaches = myOpen.Select(t => t.Slas?.PolicyMetrics.Where(m => m.Stage == "active" && m.BreachAt is not null).Min(m => m.BreachAt))
            .OfType<DateTimeOffset>().ToList();
        var midnight = new DateTimeOffset(DateTime.Today.AddDays(1));
        var overdue = breaches.Count(b => b <= now);
        var dueToday = breaches.Where(b => b > now && b < midnight).Order().ToList();

        Log.Info($"Catch up since {start:yyyy-MM-dd HH:mm} ({clock.Elapsed.TotalSeconds:0.0}s, {reason}): {result.Unassigned.Count} unassigned, {result.Mine.Count} waiting on mine, " +
                 $"{result.Cc.Count} on CC'd, {result.Colleagues.Count} on colleagues' tickets, {myOpen.Count} of mine open ({quietEscalated} escalated and quiet, {quietPending} pending and quiet, SLA {overdue} past / {dueToday.Count} today) · {timing}");

        _state.Save();
        // Waiting customers one by one (up to 5), unassigned tickets only when there are few; the greeting counts it all.
        var customers = result.Mine.Concat(result.Cc).Concat(result.Colleagues).ToList();
        var separate = !oneByOne ? [] : (customers.Count <= MaxSeparateWaiting ? customers : [])
            .Concat(result.Unassigned.Count <= MaxSeparateUnassigned ? result.Unassigned : []).ToList();
        // By itself: tickets the normal checks notified about in the last half hour (e.g. when the app was just
        // started) aren't repeated one by one (the greeting still counts them). Asked for: the full picture.
        if (!requestedByUser)
        {
            var recent = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(30);
            separate.RemoveAll(c => c.TicketId is { } id && _notifiedAt.TryGetValue(id, out var at) && at >= recent);
        }
        var counts = new TicketRules.CatchUpCounts
        {
            Mine = result.Mine.Count, Cc = result.Cc.Count, Colleagues = result.Colleagues.Count,
            Unassigned = result.Unassigned.Count, Scheduled = result.Unassigned.Sum(c => c.ScheduledCount),
            SlaPast = overdue, SlaToday = dueToday.Count, FirstDueToday = dueToday.Count > 0 ? dueToday[0].LocalDateTime : null,
            QuietEscalated = quietEscalated, QuietPending = quietPending, NoGroups = settings.WatchedGroups.Count == 0
        };
        var yourTickets = settings.TrackMyTickets ? TicketRules.YourTicketsText(myOpen.Select(t => (StatusName(t), t.Status, t.Priority)).ToList()) : null;
        // And a reminder of what you're working on today, by name (each number opens the ticket).
        if (yourTickets is not null && TicketRules.WorkingOnText(myOpen.Select(t => new TicketRules.WorkItem(t.Id, t.Subject ?? "", StatusName(t), t.Status,
                t.Priority, t.Slas?.PolicyMetrics.Where(m => m.Stage == "active" && m.BreachAt is not null).Min(m => m.BreachAt), t.UpdatedAt))) is { } workingOn)
            yourTickets += "\n\n" + workingOn; // set apart too: the tickets to start with
        notifier.Show(new NotificationContent(NotificationKind.MyTicket, $"Catch up · {TicketRules.CatchUpSince(start, DateTime.Now)}",
            TicketRules.Greeting(DateTime.Now, Me?.Name), TicketRules.CatchUpBody(counts, yourTickets))
        {
            Action = settings.WatchedGroups.Count == 0 ? NotificationActions.Settings : NotificationActions.OpenZendesk,
            Style = settings.SummaryStyle, RequestedByUser = requestedByUser
        });
        foreach (var content in separate)
            notifier.Show(content with { RequestedByUser = requestedByUser });
        return result;
    }

    /// <summary>Someone other than the customer wrote publicly on the ticket since <paramref name="since"/>.</summary>
    private async Task<bool> RepliedToCustomerSinceAsync(Ticket ticket, DateTimeOffset since, CancellationToken ct)
    {
        foreach (var audit in await zendesk.GetCommentsSinceAsync(ticket.Id, since, ct))
        {
            if (audit.AuthorId == SystemUserId || !audit.Events.Any(e => e.Type == "Comment" && e.Public == true))
                continue;
            if (!TicketRules.IsCustomer(await zendesk.GetUserAsync(audit.AuthorId, ct), ticket, Me?.Email))
                return true;
        }
        return false;
    }

    /// <summary>The newest public comment since <paramref name="since"/>, if a customer wrote it (so they're waiting).</summary>
    private async Task<(string Customer, AuditEvent Comment, DateTimeOffset At)?> CustomerWaitingAsync(Ticket ticket, DateTimeOffset since, CancellationToken ct)
    {
        foreach (var audit in (await zendesk.GetCommentsSinceAsync(ticket.Id, since, ct)).OrderByDescending(a => a.CreatedAt))
        {
            var comment = audit.Events.FirstOrDefault(e => e.Type == "Comment" && e.Public == true);
            if (comment is null || audit.AuthorId == SystemUserId)
                continue;
            var author = await zendesk.GetUserAsync(audit.AuthorId, ct);
            return TicketRules.IsCustomer(author, ticket, Me?.Email) ? (author!.Name ?? "The client", comment, audit.CreatedAt) : null;
        }
        return null;
    }

    private string? GroupName(long? groupId) => groupId is { } id ? _groupNames.GetValueOrDefault(id) : null;
}

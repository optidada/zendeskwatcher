using ZendeskWatcher.UI;
using ZendeskWatcher.UI.Popups;
using ZendeskWatcher.Zendesk;

namespace ZendeskWatcher.Tracking;

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

    /// <summary>Finds what still needs attention since you stopped working last time, and shows it.</summary>
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
        return _state.LastCatchUp < localNow.Date.AddHours(Math.Clamp(settings.MyWorkdayStartHour, 0, 23));
    }

    public bool DayStartCatchUpDue() => DayStartCatchUpDue(DateTime.Now);

    /// <param name="since">For tests; normally worked out from your working hours.</param>
    /// <param name="requestedByUser">Clicked (shown even while paused, "All caught up" too), or by itself at the start of the day.</param>
    public async Task<CatchUpResult> CatchUpAsync(CancellationToken ct, DateTime? since = null, bool requestedByUser = true)
    {
        _state.LastCatchUp = DateTime.Now;
        var start = since ?? TicketRules.CatchUpStart(DateTime.Now, settings.MyWorkdayStartHour, settings.MyWorkdayEndHour, settings.MyWorkDays);
        var sinceUtc = new DateTimeOffset(start).ToUniversalTime();
        var me = Me!.Id;
        var result = new CatchUpResult(start, [], [], [], []);

        // 1. Still unassigned: from the check that just ran (verified, not the search index).
        foreach (var (group, tickets) in _unassigned)
            foreach (var ticket in tickets.OrderBy(t => t.CreatedAt))
                result.Unassigned.Add(await WithClientAsync(new NotificationContent(NotificationKind.NewTicket, "Catch up · Unassigned",
                    $"#{ticket.Id} {TextCleanup.Plain(ticket.Subject)}", await CreatedLineAsync(ticket, ct), ticket.Id, ticket.Priority, GroupLabel(group))
                    { CanTake = true, EventTime = ticket.CreatedAt, IsTicketNotification = true }, ticket, group, ct));

        // 2 and 3. Your own and CC'd / followed tickets where a customer wrote last (copied first: the check may change them).
        var mineIds = settings.TrackMyTickets ? _state.MyTickets.Keys.ToList() : [];
        var ccIds = settings.TrackCcTickets ? _state.CcTickets.Keys.Except(mineIds).ToList() : [];
        var tracked = mineIds.Count + ccIds.Count > 0 ? await zendesk.GetTicketsAsync(mineIds.Concat(ccIds).ToList(), ct) : [];
        var now = DateTimeOffset.UtcNow;
        // Any status before solved: a customer who wrote last needs an answer (only "In progress" and "Scheduled" are trusted a while).
        foreach (var ticket in tracked.Where(t => t.IsOpen && t.UpdatedAt >= sinceUtc))
        {
            if (await CustomerWaitingAsync(ticket, sinceUtc, ct) is not { } waiting)
                continue;
            var (customer, comment, at) = waiting;
            if (!TicketRules.CustomerWaitingNeedsNotice(StatusName(ticket), at, now, InProgressGrace))
                continue;
            var isMine = ticket.AssigneeId == me;
            var said = settings.ShowCommentText ? $"**{Markdown.Escape(customer)}** wrote: {Markdown.Escape(Snippet(comment))}" : $"**{Markdown.Escape(customer)}** wrote";
            var content = new NotificationContent(isMine ? NotificationKind.MyTicket : NotificationKind.CcTicket,
                isMine ? "Catch up · Waiting for you" : "Catch up · CC / following",
                $"#{ticket.Id} {TextCleanup.Plain(ticket.Subject)}",
                (isMine ? $"{said}\nNo reply yet" : $"{said}\nNo agent has answered yet") + StatusSuffix(ticket, at, now),
                ticket.Id, ticket.Priority, GroupName(ticket.GroupId))
            { EventTime = at, ChangedAt = at, IsTicketNotification = true };
            content = await WithClientAsync(content, ticket, null, ct);
            (isMine ? result.Mine : result.Cc).Add(content);
        }

        // 4. Colleagues' tickets, by the Colleagues rules: customer waiting, colleague off work right now.
        var excluded = settings.HelpExcludedAssignees.Select(c => c.Id).ToHashSet();
        var skip = mineIds.Concat(ccIds).ToHashSet();
        foreach (var group in settings.EffectiveHelpGroups)
        {
            foreach (var ticket in await zendesk.GetAssignedOpenTicketsUpdatedSinceAsync(group.Id, sinceUtc, ct))
            {
                if (ticket.AssigneeId is not { } assigneeId || assigneeId == me || excluded.Contains(assigneeId) || !skip.Add(ticket.Id))
                    continue;
                if ((!TicketRules.MatchesPriority(ticket.Priority, settings.HelpPriorities) && !IsImportant(ticket)) || !TicketRules.IsWantedClient(ticket, ClientMode(ticket)))
                    continue;
                var assignee = await zendesk.GetUserAsync(assigneeId, ct);
                if (settings.HelpRegionDefaultDone != true && TimeRegions.Of(TimeRegions.ZoneOf(assignee)) == TimeRegions.Mine(Me))
                    continue;
                var (away, local) = OutsideWorkingHours(assignee, DateTimeOffset.UtcNow);
                if (away is not true)
                    continue;
                if (await CustomerWaitingAsync(ticket, sinceUtc, ct) is not { } waiting)
                    continue;
                var (customer, comment, at) = waiting;
                if (!TicketRules.CustomerWaitingNeedsNotice(StatusName(ticket), at, now, InProgressGrace))
                    continue;
                var who = assignee?.Name ?? $"User {assigneeId}";
                var said = settings.ShowCommentText ? $"**{Markdown.Escape(customer)}** wrote: {Markdown.Escape(Snippet(comment))}" : $"**{Markdown.Escape(customer)}** wrote";
                result.Colleagues.Add(await WithClientAsync(new NotificationContent(NotificationKind.HelpColleague, "Catch up · Customer waiting",
                    $"#{ticket.Id} {TextCleanup.Plain(ticket.Subject)}",
                    $"{said}\n{Markdown.Escape(who)} is off work · {English(local, "ddd HH:mm")} their time{StatusSuffix(ticket, at, now)}",
                    ticket.Id, ticket.Priority, GroupLabel(group))
                { EventTime = at, ChangedAt = at, IsTicketNotification = true }, ticket, group, ct));
            }
        }

        // By itself at the start of the day: tickets the normal checks notified about in the last half hour (e.g. when
        // the app was just started) aren't repeated. Asked for: the full picture.
        if (!requestedByUser)
        {
            var recent = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(30);
            foreach (var list in new[] { result.Unassigned, result.Mine, result.Cc, result.Colleagues })
                list.RemoveAll(c => c.TicketId is { } id && _notifiedAt.TryGetValue(id, out var at) && at >= recent);
        }

        Log.Info($"Catch up since {start:yyyy-MM-dd HH:mm}: {result.Unassigned.Count} unassigned, {result.Mine.Count} waiting on mine, " +
                 $"{result.Cc.Count} on CC'd, {result.Colleagues.Count} on colleagues' tickets");

        _state.Save();
        // Asked for: shown even while notifications are paused. By itself: quiet when there's nothing.
        if (!requestedByUser && result.Count == 0)
            return result;
        // Waiting customers one by one (up to 5), unassigned tickets only when there are few; the summary (whenever
        // not everything is shown one by one) counts it all.
        var customers = result.Mine.Concat(result.Cc).Concat(result.Colleagues).ToList();
        var separate = (customers.Count <= MaxSeparateWaiting ? customers : [])
            .Concat(result.Unassigned.Count <= MaxSeparateUnassigned ? result.Unassigned : []).ToList();
        if (separate.Count < result.Count || result.Count == 0)
            notifier.Show(new NotificationContent(NotificationKind.MyTicket, "Catch up",
                result.Count == 0 ? "All caught up" : result.Count == 1 ? "1 thing needs attention" : $"{result.Count} things need attention",
                TicketRules.CatchUpText(start, DateTime.Now, result.Unassigned.Count, result.Mine.Count, result.Cc.Count, result.Colleagues.Count))
            { Action = NotificationActions.OpenZendesk, MaxBodyLines = 7, Style = settings.AppMessageStyle, RequestedByUser = requestedByUser });
        foreach (var content in separate)
            notifier.Show(content with { RequestedByUser = requestedByUser });
        return result;
    }

    /// <summary>The newest public comment since <paramref name="since"/>, if a customer wrote it (so they're waiting).</summary>
    private async Task<(string Customer, AuditEvent Comment, DateTimeOffset At)?> CustomerWaitingAsync(Ticket ticket, DateTimeOffset since, CancellationToken ct)
    {
        foreach (var audit in (await zendesk.GetAuditsSinceAsync(ticket.Id, since, ct)).OrderByDescending(a => a.CreatedAt))
        {
            var comment = audit.Events.FirstOrDefault(e => e.Type == "Comment" && e.Public == true);
            if (comment is null || audit.AuthorId == SystemUserId)
                continue;
            var author = await zendesk.GetUserAsync(audit.AuthorId, ct);
            return TicketRules.IsCustomer(author, ticket, Me?.Email) ? (author!.Name ?? "The customer", comment, audit.CreatedAt) : null;
        }
        return null;
    }

    private string? GroupName(long? groupId) => groupId is { } id ? _groupNames.GetValueOrDefault(id) : null;
}

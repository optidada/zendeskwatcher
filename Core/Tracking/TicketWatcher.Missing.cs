using ZendeskWatcher.UI;
using ZendeskWatcher.UI.Popups;
using ZendeskWatcher.Zendesk;

namespace ZendeskWatcher.Tracking;

/// <summary>
/// "Cover today?": once a day, 3 hours into my working day, colleagues in my region (members of the groups I cover on
/// Colleagues, with open tickets there, not covered yet) who have done nothing in Zendesk since their working day
/// started, e.g. off sick or on holiday (3 hours of nothing alone could just be a morning's work and then lunch). "Cover today" covers them until midnight as if ticked on Colleagues (<see cref="Settings.CoverToday"/>).
/// About 1 request per colleague, once a day; the members and their open tickets are shared with Team workload.
/// </summary>
public sealed partial class TicketWatcher
{
    /// <summary>The day's check, running beside the regular check (the tests wait for it).</summary>
    internal Task MissingCheck { get; private set; } = Task.CompletedTask;

    /// <summary>Asked about at most this many colleagues a day (one notification each).</summary>
    private const int MaxMissingAsked = 3;

    /// <summary>Colleagues looked at, at most (each costs an activity lookup).</summary>
    private const int MaxMissingLookups = 15;

    /// <summary>Of those who look idle on their own tickets, at most this many get the wider look (comments anywhere: up to 6 requests each).</summary>
    private const int MaxCommentLookups = 4;
    private int _commentLookups;

    /// <summary>Due: Help colleagues on, not asked yet today, in my working hours and at least 3 hours into them.</summary>
    private bool MissingCheckDue()
    {
        if (!settings.HelpColleagues || Me is null || _state.MissingColleaguesChecked.Date == DateTime.Today || IAmOffWork())
            return false;
        return DateTime.Now.TimeOfDay >= TimeSpan.FromHours(settings.MyWorkdayStartHour) + TicketRules.InactiveFor;
    }

    /// <summary>
    /// Whether the colleague did something in Zendesk since <paramref name="since"/>: changed or commented on one of
    /// their own tickets, or commented on anyone's (a colleague's ticket, one they passed on). Null when it couldn't be
    /// read (then they aren't asked about).
    /// </summary>
    private async Task<bool?> ActiveSinceAsync(long userId, DateTimeOffset since, CancellationToken ct)
    {
        try
        {
            if (await ActiveOnOwnTicketsSinceAsync(userId, since, ct))
                return true;
            if (++_commentLookups > MaxCommentLookups)
                return null; // enough requests for today: not sure, so not asked about
            // Their own tickets say nothing: a comment anywhere else? 1 search, and only for the few who look idle.
            foreach (var ticket in await zendesk.GetCommentedTicketsUpdatedSinceAsync(userId, since, ct))
                if ((await zendesk.GetCommentsSinceAsync(ticket.Id, since, ct)).Any(c => c.AuthorId == userId))
                    return true;
            return false;
        }
        catch (HttpRequestException ex)
        {
            Log.Error($"Could not read the activity of user {userId} today", ex);
            return null;
        }
    }

    /// <summary>
    /// A change or comment on one of their own tickets since <paramref name="since"/>: 1 request (Zendesk's "assignee
    /// updated at" of their 5 latest tickets), or their histories when Zendesk doesn't send that.
    /// </summary>
    private async Task<bool> ActiveOnOwnTicketsSinceAsync(long userId, DateTimeOffset since, CancellationToken ct)
    {
        {
            if (!_assigneeMetricsUnavailable)
            {
                List<DateTimeOffset>? updates = null;
                try { updates = await zendesk.GetAssigneeLastUpdatesAsync(userId, ct); }
                catch (HttpRequestException ex) when (ex.StatusCode is System.Net.HttpStatusCode.BadRequest or System.Net.HttpStatusCode.Forbidden
                                                          or System.Net.HttpStatusCode.NotFound)
                {
                    Log.Error("Ticket metrics not readable", ex); // as in LastActiveAsync: histories from now on
                }
                if (updates is not null)
                    return updates.Any(at => at >= since);
                _assigneeMetricsUnavailable = true;
                Log.Info("Colleague activity: no ticket metrics from Zendesk, reading ticket histories instead");
            }
            foreach (var ticket in (await zendesk.GetAssigneeTicketsUpdatedSinceAsync(userId, since, ct)).Take(5))
                foreach (var audit in await zendesk.GetAuditsSinceAsync(ticket.Id, since, ct, ticket.UpdatedAt))
                    if (audit.AuthorId == userId)
                        return true;
            return false;
        }
    }

    private async Task AskAboutMissingColleaguesAsync(CancellationToken ct)
    {
        _state.MissingColleaguesChecked = DateTime.Today; // once a day, also when it fails (saved with the state)
        try
        {
            await FindMissingColleaguesAsync(ct);
        }
        catch (AuthRequiredException)
        {
            _state.MissingColleaguesChecked = default; // signed out meanwhile (the check says so): again after signing in
            _state.Save(); // saved now: the next start of checking reads it from disk
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _state.MissingColleaguesChecked = default; // checking stopped (Settings, quitting): again next time
            _state.Save();
        }
        catch (Exception ex)
        {
            Log.Error("Could not look for missing colleagues", ex); // the rest of the check goes on
        }
    }

    private async Task FindMissingColleaguesAsync(CancellationToken ct)
    {
        if (Me is not { } me)
            return;
        ZendeskClient.Reason.Value = "missing colleagues";
        _commentLookups = 0;
        var myRegion = TimeRegions.Mine(me);
        var now = DateTimeOffset.UtcNow;

        // Members with open tickets in the groups I cover (as on Colleagues): covering them there is what "Cover today" does.
        var candidates = new Dictionary<long, User>();
        foreach (var group in settings.EffectiveHelpGroups)
        {
            var entry = await GroupWorkloadCache.GetAsync(zendesk, settings.Subdomain, group.Id, CacheAges.WorkloadFresh, ct);
            var withTickets = entry.Tickets.Select(t => t.AssigneeId).OfType<long>().ToHashSet();
            foreach (var member in entry.Members.Where(m => m.Id != me.Id && withTickets.Contains(m.Id)))
                candidates.TryAdd(member.Id, member);
        }

        var missing = new List<(User User, DateTime Local)>();
        foreach (var user in candidates.Values
                     // Not the ones covered anyway; those covered today still count, for their amber dot on Colleagues.
                     .Where(u => TimeRegions.Of(TimeRegions.ZoneOf(u)) == myRegion && (!settings.CoversColleague(u.Id, myRegion, myRegion) || settings.CoveredToday(u.Id)))
                     .OrderBy(u => u.Name, StringComparer.CurrentCultureIgnoreCase)
                     // In their working hours, and at least 3 hours into them (otherwise "nothing for 3 h" says nothing):
                     // checked before the cap, so the cap only limits the lookups, not who's considered.
                     .Where(u => OutsideWorkingHours(u, now) is (false, var at) && at.TimeOfDay >= TimeSpan.FromHours(settings.WorkdayStartHour) + TicketRules.InactiveFor)
                     .Take(MaxMissingLookups))
        {
            var (_, local) = OutsideWorkingHours(user, now);
            var zone = TicketRules.ResolveTimeZone(user.IanaTimeZone) ?? TicketRules.ResolveTimeZone(user.TimeZone);
            if (zone is null)
                continue;
            var startLocal = local.Date.AddHours(settings.WorkdayStartHour);
            if (zone.IsInvalidTime(startLocal))
                startLocal = startLocal.AddHours(1); // the clocks jumped over it (daylight saving): an hour later
            var dayStart = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(startLocal, zone), TimeSpan.Zero);
            if (await ActiveSinceAsync(user.Id, dayStart, ct) == false)
                missing.Add((user, local));
        }
        _state.AwayTodayIds = [.. missing.Select(m => m.User.Id)];
        AwayToday.Set(DateTime.Today, _state.AwayTodayIds); // amber dots on Colleagues
        _state.Save(); // with today's mark, so a restart shows today's dots (not the regular check's next save)
        Log.Info($"Missing colleagues: {candidates.Count} in the covered groups with open tickets, {missing.Count} in my region without activity today" +
                 (missing.Count > 0 ? ": " + string.Join(", ", missing.Select(m => m.User.Name ?? m.User.Id.ToString())) : ""));

        foreach (var (user, local) in missing.Where(m => !settings.CoveredToday(m.User.Id)).Take(MaxMissingAsked)) // already covered: no question
        {
            var name = Markdown.Escape(user.Name ?? $"User {user.Id}");
            notifier.Show(new NotificationContent(NotificationKind.MyTicket, "Colleague away?",
                $"{name} hasn't been active in Zendesk today",
                $"No changes or comments on their tickets since their day started ({Clock.DayTime(local)} their time). " +
                "**Cover today**: when their clients write, you get a notification as for the colleagues you cover, until " +
                "midnight. **Always** ticks them on Settings → Notifications → Colleagues.")
            {
                Action = NotificationActions.Cover(user.Id, user.Name ?? $"User {user.Id}"),
                SecondChoice = ("Always", NotificationActions.CoverAlways(user.Id, user.Name ?? $"User {user.Id}")),
                Style = settings.AppMessageStyle // an app message (its sound and time on screen), not a ticket
            });
        }
    }
}

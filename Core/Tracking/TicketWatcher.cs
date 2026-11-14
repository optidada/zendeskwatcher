using System.Text.Json;
using System.Text.RegularExpressions;
using ZendeskWatcher.UI;
using ZendeskWatcher.UI.Popups;
using ZendeskWatcher.Zendesk;

namespace ZendeskWatcher.Tracking;

/// <summary>
/// Polls Zendesk and decides what is new:
/// <list type="bullet">
/// <item>new unassigned tickets in the watched groups (via search),</item>
/// <item>changes made by others on tickets assigned to the user (via ticket lists and audits, no search lag),</item>
/// <item>optionally the same for tickets where the user is CC'd or a follower.</item>
/// </list>
/// The first poll of each list only records the current tickets, so starting the app does not flood notifications.
/// </summary>
public sealed partial class TicketWatcher(Settings settings, ZendeskClient zendesk, INotificationSink notifier)
{
    private const long SystemUserId = -1;
    private const int MaxSeparateUnassignedToasts = 5;
    private const int MaxLinesPerToast = 4;
    private static readonly TimeSpan ForgetUnassignedAfter = TimeSpan.FromDays(1);

    /// <summary>Missing groups already reported in this run, so restarting the watcher doesn't repeat it.</summary>
    private static readonly HashSet<long> ReportedMissingGroups = [];

    private WatchState _state = new();
    private Dictionary<long, string> _groupNames = [];

    /// <summary>Custom status id -> its name for agents ("In progress", "Escalated"); empty if the account has none.</summary>
    private Dictionary<long, string> _statusNames = [];

    public User? Me { get; private set; }
    public int UnassignedCount { get; private set; }

    /// <summary>Unassigned tickets per monitored group, as of the last check.</summary>
    private List<(WatchedGroup Group, List<Ticket> Tickets)> _unassigned = [];

    /// <summary>The last check showed "already waiting" for a newly monitored group (so no start summary is needed).</summary>
    public bool ShowedAlreadyWaiting { get; private set; }
    public int MyOpenCount => _state.MyTickets.Count;

    public async Task InitializeAsync(CancellationToken ct)
    {
        Me = await zendesk.GetMeAsync(ct);
        Log.Info($"Signed in as {Me.Name} ({Me.Role}, id {Me.Id})");

        _state = WatchState.Load();
        if (_state.Subdomain != settings.Subdomain || _state.UserId != Me.Id)
        {
            _state = new WatchState { Subdomain = settings.Subdomain, UserId = Me.Id };
        }

        SlaLearning.Current = _state.Sla; // kept with what the app has seen (also across a sign-in as someone else: it's about the account)

        // A group that is unchecked and checked again later gets a fresh silent baseline.
        _state.BaselinedGroups.IntersectWith(settings.WatchedGroups.Select(g => g.Id));
        _state.HelpBaselinedGroups.IntersectWith(settings.EffectiveHelpGroups.Select(g => g.Id));
        // Save right away: if Settings is reopened before the first check finishes, the check is cancelled, and an
        // unticked group would otherwise still count as "already seen" when it's ticked again.
        _state.Save();

        _groupNames = settings.WatchedGroups.ToDictionary(g => g.Id, g => g.Name);
        try
        {
            var groups = await zendesk.GetGroupsAsync(ct);
            foreach (var group in groups)
                _groupNames[group.Id] = group.Name;

            // A monitored group Zendesk no longer lists just returns no tickets; say so once instead of going quiet.
            var listed = groups.Select(g => g.Id).ToHashSet();
            var missing = settings.WatchedGroups.Where(g => !g.IsNoGroup && !listed.Contains(g.Id)).ToList();
            if (missing.Count > 0)
                Log.Info($"Monitored group(s) not found in Zendesk: {string.Join(", ", missing.Select(g => $"{g.Name} ({g.Id})"))}");
            var unreported = missing.Where(g => ReportedMissingGroups.Add(g.Id)).ToList();
            if (unreported.Count > 0)
                notifier.ShowMissingGroups(unreported.Select(g => g.Name).ToList());
        }
        catch (HttpRequestException ex)
        {
            Log.Error("Could not load group names", ex);
        }

        try
        {
            _statusNames = (await zendesk.GetCustomStatusesAsync(ct))
                .Where(s => !string.IsNullOrWhiteSpace(s.AgentLabel)).ToDictionary(s => s.Id, s => s.AgentLabel!.Trim());
            if (_statusNames.Count > 0)
                Log.Info($"Custom ticket statuses: {string.Join(", ", _statusNames.Values.Distinct())}");
        }
        catch (HttpRequestException ex)
        {
            Log.Error("Could not load the custom ticket statuses", ex);
        }
    }

    /// <summary>How long "In progress" and "Scheduled" are trusted (Notifications → Colleagues).</summary>
    private TimeSpan InProgressGrace => TimeSpan.FromHours(Math.Clamp(settings.InProgressGraceHours, 1, 24));

    /// <summary>The status as agents see it: the custom one ("In progress") if any, else "Open", "Pending", "On-hold", …</summary>
    private string StatusName(Ticket ticket) =>
        ticket.CustomStatusId is { } id && _statusNames.TryGetValue(id, out var name) ? name : TicketRules.StatusLabel(ticket.Status) ?? "";

    /// <summary>" · Escalated" after the off-work line, unless it's plain Open/New; "In progress" and "Scheduled" say how long the customer has waited.</summary>
    private string StatusSuffix(Ticket ticket, DateTimeOffset repliedAt, DateTimeOffset now)
    {
        var name = StatusName(ticket);
        if (name.Length == 0 || name is "Open" or "New")
            return "";
        return TicketRules.IsTrustedStatus(name) ? $" · {name}, no answer for {(int)(now - repliedAt).TotalHours} h" : $" · {name}";
    }

    private static readonly TimeSpan RequestReportInterval = TimeSpan.FromMinutes(10);
    private DateTime _lastRequestReport = DateTime.UtcNow;

    public async Task PollAsync(CancellationToken ct)
    {
        // Every 10 minutes: how many Zendesk API requests were made (shows the app's load on Zendesk).
        if (DateTime.UtcNow - _lastRequestReport >= RequestReportInterval)
        {
            var minutes = (DateTime.UtcNow - _lastRequestReport).TotalMinutes;
            var count = ZendeskClient.TakeRequestCount();
            Log.Info($"Zendesk API requests in the last {minutes:0} min: {count} ({count / minutes:0.#} per minute)");
            _lastRequestReport = DateTime.UtcNow;
        }

        // The parts of a check run side by side: each waits mostly for Zendesk, and they keep separate state (new
        // tickets, my tickets, CC, colleagues). On the app's UI thread their steps never run at the same instant.
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var parts = new List<string>();
        async Task Timed(string name, Func<Task> check)
        {
            var own = System.Diagnostics.Stopwatch.StartNew();
            await check();
            lock (parts)
                parts.Add($"{name} {own.Elapsed.TotalSeconds:0.0}s");
        }
        var checks = new List<Task> { Timed("unassigned", () => CheckUnassignedAsync(ct)) };

        if (settings.TrackMyTickets)
            checks.Add(Timed("my tickets", () => CheckMyTicketsAsync(ct)));
        else if (_state.MyTicketsBaselined)
        {
            // Rebuild the baseline when tracking is switched on again, instead of reporting stale changes.
            _state.MyTickets.Clear();
            _state.MyTicketsBaselined = false;
        }

        checks.Add(Timed("help colleagues", async () =>
        {
            try
            {
                await CheckHelpColleaguesAsync(ct);
            }
            catch (Exception ex) when (ex is not (AuthRequiredException or OperationCanceledException))
            {
                Log.Error("Help colleagues check failed", ex); // never let it stop the other notifications
            }
        }));

        if (settings.TrackCcTickets)
            checks.Add(Timed("cc", () => CheckCcTicketsAsync(ct)));
        else if (_state.CcTicketsBaselined)
        {
            _state.CcTickets.Clear();
            _state.CcTicketsBaselined = false;
            _state.CcBaselineFull = false;
        }

        await Task.WhenAll(checks);
        _state.Save();

        // Slow checks are logged with a breakdown, to see which part takes the time.
        var total = clock.Elapsed.TotalSeconds;
        if (total > 5)
            Log.Info($"Slow check ({total:0.0}s, parts side by side): {string.Join(", ", parts)}");
    }

    private async Task CheckUnassignedAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var total = 0;
        var byGroup = new List<(WatchedGroup Group, List<Ticket> Tickets)>();
        ShowedAlreadyWaiting = false;

        // Search finds the candidates, but its index can be stale for hours: a ticket assigned long ago may still
        // match "assignee:none". Re-read every candidate (exact, no index) and keep only the really unassigned ones.
        // One search per group, all at once.
        var found = (await Task.WhenAll(settings.WatchedGroups.Select(async group =>
            (Group: group, Tickets: await zendesk.GetUnassignedTicketsAsync(group.Id, ct))))).ToList();

        var allIds = found.SelectMany(f => f.Tickets).Select(t => t.Id).Distinct().ToList();
        var live = allIds.Count > 0 ? (await zendesk.GetTicketsAsync(allIds, ct)).ToDictionary(t => t.Id) : [];
        await LearnSlaAsync(live.Values, ct);

        foreach (var (group, candidates) in found)
        {
            var tickets = candidates
                .Select(t => live.GetValueOrDefault(t.Id))
                .OfType<Ticket>()
                .Where(t => TicketRules.IsReallyUnassigned(t, group))
                .ToList();
            if (tickets.Count < candidates.Count)
                Log.Info($"Search returned {candidates.Count - tickets.Count} ticket(s) {group.Describe()} that are no longer unassigned: " +
                         string.Join(",", candidates.Select(t => t.Id).Except(tickets.Select(t => t.Id))));

            // All are recorded as seen (so ticking a client again later doesn't report its old tickets as new), but
            // muted clients (and "only urgent" ones below High) neither notify nor count (Clients page).
            var isBaseline = _state.BaselinedGroups.Add(group.Id);
            var fresh = tickets.Where(t => !_state.SeenUnassigned.ContainsKey(t.Id)).OrderBy(t => t.CreatedAt).ToList();
            foreach (var ticket in tickets)
                _state.SeenUnassigned[ticket.Id] = now;
            tickets = tickets.Where(t => TicketRules.IsWantedClient(t, ClientMode(t))).ToList();
            fresh = fresh.Where(t => TicketRules.IsWantedClient(t, ClientMode(t))).ToList();

            total += tickets.Count;
            byGroup.Add((group, tickets));

            // A newly monitored group: one summary of what's already waiting instead of a notification per ticket.
            if (isBaseline && settings.NotifyUnassigned && tickets.Count > 0)
            {
                await ShowAlreadyWaitingAsync(group, tickets, ct);
                ShowedAlreadyWaiting = true;
                continue;
            }

            // New tickets are always recorded, so switching notifications on later does not report old ones.
            if (isBaseline || fresh.Count == 0 || !settings.NotifyUnassigned)
                continue;
            // Important clients always notify, one by one, also outside my hours.
            var important = fresh.Where(IsImportant).ToList();
            fresh = settings.OnlyNotifyDuringMyHours && IAmOffWork() ? [] : fresh.Except(important).ToList();
            foreach (var ticket in important)
                await ShowNewTicketAsync(ticket, group, ct);
            if (fresh.Count == 0)
                continue;

            Log.Info($"{fresh.Count} new unassigned ticket(s) {group.Describe()}: {string.Join(",", fresh.Select(t => t.Id))}");

            if (fresh.Count > MaxSeparateUnassignedToasts)
            {
                var scheduled = fresh.Count(IsScheduled);
                Show(new NotificationContent(NotificationKind.NewTicket, "New tickets",
                    $"{fresh.Count} new unassigned tickets{Markdown.Strip(TicketRules.ScheduledNote(fresh.Count, scheduled))}", TicketList(fresh, IsScheduled),
                    Context: GroupLabel(group)) { IsTicketNotification = true, TicketCount = fresh.Count, ScheduledCount = scheduled, MaxBodyLines = ListLines },
                    fresh.Select(t => t.Id));
                continue;
            }

            foreach (var ticket in fresh)
                await ShowNewTicketAsync(ticket, group, ct);
        }

        // Forget tickets that have not been unassigned for a while, so they notify again if they come back.
        foreach (var id in _state.SeenUnassigned.Where(kv => now - kv.Value > ForgetUnassignedAfter).Select(kv => kv.Key).ToList())
            _state.SeenUnassigned.Remove(id);

        UnassignedCount = total;
        _unassigned = byGroup;
    }

    private async Task ShowNewTicketAsync(Ticket ticket, WatchedGroup group, CancellationToken ct, string heading = "New ticket") =>
        Show(await UnassignedTicketAsync(ticket, group, heading, ct));

    /// <summary>One unassigned ticket; "· Scheduled" in the heading when a time is booked and nobody owns it.</summary>
    private async Task<NotificationContent> UnassignedTicketAsync(Ticket ticket, WatchedGroup group, string heading, CancellationToken ct)
    {
        var scheduled = IsScheduled(ticket);
        return await WithClientAsync(new NotificationContent(NotificationKind.NewTicket, scheduled ? $"{heading} \u00b7 Scheduled" : heading,
            $"#{ticket.Id} {TextCleanup.Plain(ticket.Subject)}", await CreatedLineAsync(ticket, ct), ticket.Id, ticket.Priority, GroupLabel(group))
            { CanTake = true, EventTime = ticket.CreatedAt, IsTicketNotification = true, ScheduledCount = scheduled ? 1 : 0 }, ticket, group, ct,
            sla: (ticket.CreatedAt, FirstReply: true));
    }

    /// <summary>Its status is "Scheduled" (custom statuses, by name).</summary>
    private bool IsScheduled(Ticket ticket) => TicketRules.IsScheduled(StatusName(ticket));

    /// <summary>What the last check found, for the "Right now" card on Insights and the start summary.</summary>
    public WatchSnapshot Snapshot()
    {
        var waiting = _unassigned.SelectMany(g => g.Tickets).ToList();
        (int, int, int)? mine = null;
        if (settings.TrackMyTickets)
        {
            var tickets = _state.MyTickets.Values;
            mine = (tickets.Count(t => t.Status is "new" or "open"), tickets.Count(t => t.Status == "pending"), tickets.Count(t => t.Status == "hold"));
        }
        return new WatchSnapshot(DateTime.Now, _unassigned.Select(g => new GroupCount(g.Group, g.Tickets.Count, g.Tickets.Count(IsScheduled))).ToList(),
            waiting.Count > 0 ? waiting.Min(t => t.CreatedAt) : null, mine,
            settings.TrackCcTickets ? _state.CcTickets.Count : null, settings.TrackCcTickets && _state.CcBaselineFull);
    }

    /// <summary>The start summary: a greeting, then what's waiting and what's watched as running text.</summary>
    public NotificationContent StartSummary() => new(NotificationKind.MyTicket, "Zendesk Watcher", TicketRules.Greeting(DateTime.Now, Me?.Name),
        TicketRules.SummaryText(Snapshot(), settings.WatchedGroups, settings.EffectiveHelpGroups.Count > 0 ? settings.HelpPriorities : null, DateTimeOffset.UtcNow));

    private async Task CheckMyTicketsAsync(CancellationToken ct)
    {
        var me = Me!.Id;

        if (!_state.MyTicketsBaselined)
        {
            var open = await zendesk.GetMyOpenTicketsAsync(ct);
            var recentAtBaseline = await zendesk.GetRecentlyUpdatedAssignedAsync(me, ct);

            _state.MyTickets = open.Concat(recentAtBaseline.Where(t => t.IsOpen))
                .DistinctBy(t => t.Id)
                .ToDictionary(t => t.Id, ToTracked);
            _state.MyTicketsWatermark = recentAtBaseline.Count > 0 ? recentAtBaseline.Max(t => t.UpdatedAt) : DateTimeOffset.UtcNow;
            _state.MyTicketsBaselined = true;
            Log.Info($"Baseline: tracking {_state.MyTickets.Count} open ticket(s) assigned to me");
            return;
        }

        // 1. Newly assigned: open tickets at the top of my assigned list that are not tracked yet.
        var previousWatermark = _state.MyTicketsWatermark;
        // Both lists at once: what's newly assigned, and the tracked tickets as they are now.
        var recentTask = zendesk.GetRecentlyUpdatedAssignedAsync(me, ct);
        var currentTask = _state.MyTickets.Count > 0 ? zendesk.GetTicketsAsync(_state.MyTickets.Keys.ToList(), ct) : Task.FromResult(new List<Ticket>());
        var recent = await recentTask;
        var newlyAssigned = recent
            .Where(t => t.IsOpen && !_state.MyTickets.ContainsKey(t.Id) && t.UpdatedAt > previousWatermark)
            .ToList();
        if (recent.Count > 0 && recent.Max(t => t.UpdatedAt) > previousWatermark)
            _state.MyTicketsWatermark = recent.Max(t => t.UpdatedAt);

        // 2. Changes on tickets already tracked. show_many also covers tickets that left my assigned list.
        var current = await currentTask;

        foreach (var missing in _state.MyTickets.Keys.Except(current.Select(t => t.Id)).ToList())
        {
            Log.Info($"Ticket #{missing} is no longer visible (deleted or no access); stop tracking");
            _state.MyTickets.Remove(missing);
        }

        foreach (var ticket in current)
        {
            var tracked = _state.MyTickets[ticket.Id];
            if (ticket.UpdatedAt <= tracked.UpdatedAt)
                continue;

            await ReportChangesAsync(ticket, since: tracked.UpdatedAt, ct);

            if (ticket.AssigneeId != me || !ticket.IsOpen)
                _state.MyTickets.Remove(ticket.Id);
            else
                _state.MyTickets[ticket.Id] = ToTracked(ticket);
        }

        foreach (var ticket in newlyAssigned)
        {
            await ReportChangesAsync(ticket, since: previousWatermark, ct);
            _state.MyTickets[ticket.Id] = ToTracked(ticket);
        }
    }

    /// <summary>
    /// Tickets where I'm CC'd or follow, but am not the assignee (those are covered by my tickets).
    /// Same approach as my tickets: a silent baseline, then the CC'd/followed lists for new ones and audits for changes.
    /// </summary>
    private async Task CheckCcTicketsAsync(CancellationToken ct)
    {
        var me = Me!.Id;
        bool Relevant(Ticket t) => t.IsOpen && t.AssigneeId != me;

        if (!_state.CcTicketsBaselined)
        {
            var atBaseline = await zendesk.GetRecentlyUpdatedInvolvedAsync(me, ct);
            _state.CcTickets = atBaseline.Where(Relevant).ToDictionary(t => t.Id, ToTracked);
            _state.CcTicketsWatermark = atBaseline.Count > 0 ? atBaseline.Max(t => t.UpdatedAt) : DateTimeOffset.UtcNow;
            _state.CcTicketsBaselined = true;
            _state.CcBaselineFull = zendesk.LastInvolvedListFull;
            Log.Info($"Baseline: tracking {_state.CcTickets.Count} open ticket(s) where I'm CC or follower");
            return;
        }

        // 1. Newly CC'd/followed: updated since the last poll and not tracked yet.
        var previousWatermark = _state.CcTicketsWatermark;
        var recentTask = zendesk.GetRecentlyUpdatedInvolvedAsync(me, ct);
        var currentTask = _state.CcTickets.Count > 0 ? zendesk.GetTicketsAsync(_state.CcTickets.Keys.ToList(), ct) : Task.FromResult(new List<Ticket>());
        var recent = await recentTask;
        var added = recent
            .Where(t => Relevant(t) && !_state.CcTickets.ContainsKey(t.Id) && t.UpdatedAt > previousWatermark)
            .ToList();
        if (recent.Count > 0 && recent.Max(t => t.UpdatedAt) > previousWatermark)
            _state.CcTicketsWatermark = recent.Max(t => t.UpdatedAt);

        // 2. Changes on tracked tickets.
        var current = await currentTask;
        foreach (var missing in _state.CcTickets.Keys.Except(current.Select(t => t.Id)).ToList())
            _state.CcTickets.Remove(missing);

        foreach (var ticket in current)
        {
            var tracked = _state.CcTickets[ticket.Id];

            // Once it's assigned to me, my-ticket tracking reports it; don't notify twice.
            if (ticket.UpdatedAt > tracked.UpdatedAt && ticket.AssigneeId != me)
                await ReportChangesAsync(ticket, since: tracked.UpdatedAt, ct, context: TicketRules.Involvement(ticket, me) ?? "CC", kind: NotificationKind.CcTicket);

            if (Relevant(ticket) && TicketRules.Involvement(ticket, me) is not null)
                _state.CcTickets[ticket.Id] = ToTracked(ticket);
            else
                _state.CcTickets.Remove(ticket.Id);
        }

        foreach (var ticket in added)
        {
            await ReportChangesAsync(ticket, since: previousWatermark, ct, context: TicketRules.Involvement(ticket, me) ?? "CC", kind: NotificationKind.CcTicket);
            _state.CcTickets[ticket.Id] = ToTracked(ticket);
        }
    }

    // ------------------------------------------------------------------ help colleagues

    /// <summary>Search lags behind; look back this much further than the last check.</summary>
    private static readonly TimeSpan HelpLookBack = TimeSpan.FromMinutes(10);

    /// <summary>
    /// "Help colleagues": in the chosen groups, a customer replied on a colleague's open ticket while the colleague is
    /// outside working hours (in their own time zone). The first check of a group only records what's there.
    /// </summary>
    private async Task CheckHelpColleaguesAsync(CancellationToken ct)
    {
        var groups = settings.EffectiveHelpGroups;
        _state.HelpBaselinedGroups.IntersectWith(groups.Select(g => g.Id));
        if (groups.Count == 0)
        {
            _state.HelpSeen.Clear();
            return;
        }

        var me = Me!.Id;
        var excluded = settings.HelpExcludedAssignees.Select(c => c.Id).ToHashSet();
        var now = DateTimeOffset.UtcNow;
        var since = (_state.HelpLastCheck == default ? now.AddHours(-1) : _state.HelpLastCheck) - HelpLookBack;

        // Outside your own working hours: keep track (so nothing old pops up later) but don't notify.
        var iAmWorking = !settings.HelpOnlyDuringMyHours || !IAmOffWork();

        // One search per covered group, all at once; then the tickets are looked at group by group.
        var fetched = await Task.WhenAll(groups.Select(group => zendesk.GetAssignedOpenTicketsUpdatedSinceAsync(group.Id, since, ct)));
        foreach (var (group, tickets) in groups.Zip(fetched))
        {
            var baseline = _state.HelpBaselinedGroups.Add(group.Id);

            foreach (var ticket in tickets)
            {
                if (ticket.AssigneeId is not { } assigneeId || assigneeId == me)
                    continue; // your own tickets are covered by "my tickets"
                if (excluded.Contains(assigneeId))
                    continue; // a colleague you chose not to cover
                if (!TicketRules.MatchesPriority(ticket.Priority, settings.HelpPriorities) && !IsImportant(ticket))
                    continue; // not a priority you chose (no priority counts as normal), unless the client is important
                var seen = _state.HelpSeen.GetValueOrDefault(ticket.Id);
                if (ticket.UpdatedAt <= seen)
                    continue;
                _state.HelpSeen[ticket.Id] = ticket.UpdatedAt;
                if (baseline || (!iAmWorking && !IsImportant(ticket)) || _state.CcTickets.ContainsKey(ticket.Id))
                    continue; // first look at this group, you're off work (important clients still notify), or already reported as a CC'd ticket
                if (!TicketRules.IsWantedClient(ticket, ClientMode(ticket)))
                    continue; // a muted client, or "only urgent" and it isn't (Clients page)

                var reply = await LatestCustomerReplyAsync(ticket, seen == default ? since : seen, ct);
                if (reply is null)
                    continue;

                var assignee = await zendesk.GetUserAsync(assigneeId, ct);
                // Until the Colleagues list has unticked your own region once (it does when it first loads), skip
                // that region here too, so the app never covers more than the list will show.
                if (settings.HelpRegionDefaultDone != true && TimeRegions.Of(TimeRegions.ZoneOf(assignee)) == TimeRegions.Mine(Me))
                    continue;
                var (away, local) = OutsideWorkingHours(assignee, now);
                if (away is not true)
                {
                    if (away is null)
                        Log.Info($"Ticket #{ticket.Id}: customer replied, but {assignee?.Name ?? $"user {assigneeId}"} has no usable time zone");
                    continue;
                }

                var who = assignee?.Name ?? $"User {assigneeId}";
                var repliedAt = reply.Value.At;
                // "In progress" or "Scheduled": trusted for a while, then looked at again (below).
                if (!TicketRules.CustomerWaitingNeedsNotice(StatusName(ticket), repliedAt, now, InProgressGrace))
                {
                    Log.Info($"Ticket #{ticket.Id}: customer replied, {who} is off work, but it's {StatusName(ticket)}: looking again in {InProgressGrace.TotalHours:0} h");
                    _state.HelpInProgress[ticket.Id] = repliedAt;
                    continue;
                }
                _state.HelpInProgress.Remove(ticket.Id);
                Log.Info($"Ticket #{ticket.Id}: {reply.Value.Customer} replied while {who} is outside working hours ({English(local, "ddd HH:mm")} their time)");
                await ShowCustomerWaitingAsync(ticket, group, who, local, reply.Value, now, ct);
            }
        }

        if (iAmWorking)
            await FollowUpInProgressAsync(groups, excluded, now, ct);

        _state.HelpLastCheck = now;
        foreach (var old in _state.HelpSeen.Where(kv => now - kv.Value > TimeSpan.FromDays(3)).Select(kv => kv.Key).ToList())
            _state.HelpSeen.Remove(old);
        foreach (var old in _state.HelpInProgress.Where(kv => now - kv.Value > TimeSpan.FromDays(3)).Select(kv => kv.Key).ToList())
            _state.HelpInProgress.Remove(old);
    }

    /// <summary>"Customer waiting": what the customer wrote, that the colleague is off work, and the status if it says something.</summary>
    private async Task ShowCustomerWaitingAsync(Ticket ticket, WatchedGroup group, string who, DateTime local,
        (string Customer, AuditEvent Comment, DateTimeOffset At) reply, DateTimeOffset now, CancellationToken ct)
    {
        var name = Markdown.Escape(reply.Customer);
        var said = settings.ShowCommentText ? $"**{name}** replied: {Markdown.Escape(Snippet(reply.Comment))}" : $"**{name}** replied";
        Show(await WithClientAsync(new NotificationContent(NotificationKind.HelpColleague, "Customer waiting",
            $"#{ticket.Id} {TextCleanup.Plain(ticket.Subject)}",
            $"{said}\n{Markdown.Escape(who)} is off work · {English(local, "ddd HH:mm")} their time{StatusSuffix(ticket, reply.At, now)}",
            ticket.Id, ticket.Priority, GroupLabel(group))
        { IsTicketNotification = true, ChangedAt = reply.At }, ticket, group, ct, sla: (reply.At, FirstReply: false)));
    }

    /// <summary>
    /// Tickets "In progress" whose customer has now waited long enough: if nobody has answered publicly since (whatever
    /// the status is now), and the colleague is still off work, the customer-waiting notification comes after all.
    /// </summary>
    private async Task FollowUpInProgressAsync(IReadOnlyList<WatchedGroup> groups, HashSet<long> excluded, DateTimeOffset now, CancellationToken ct)
    {
        var due = _state.HelpInProgress.Where(kv => now - kv.Value >= InProgressGrace).ToList();
        if (due.Count == 0)
            return;
        foreach (var (id, _) in due)
            _state.HelpInProgress.Remove(id);

        var me = Me!.Id;
        var byId = groups.ToDictionary(g => g.Id);
        foreach (var ticket in await zendesk.GetTicketsAsync(due.Select(kv => kv.Key).ToList(), ct))
        {
            if (!ticket.IsOpen || ticket.AssigneeId is not { } assigneeId || assigneeId == me || excluded.Contains(assigneeId))
                continue;
            if (ticket.GroupId is not { } groupId || !byId.TryGetValue(groupId, out var group))
                continue;
            if (!TicketRules.MatchesPriority(ticket.Priority, settings.HelpPriorities) && !IsImportant(ticket))
                continue;
            if (!TicketRules.IsWantedClient(ticket, ClientMode(ticket)))
                continue;
            var repliedAt = due.First(kv => kv.Key == ticket.Id).Value;
            if (await LatestCustomerReplyAsync(ticket, repliedAt.AddSeconds(-1), ct) is not { } reply)
                continue; // answered meanwhile
            var assignee = await zendesk.GetUserAsync(assigneeId, ct);
            if (settings.HelpRegionDefaultDone != true && TimeRegions.Of(TimeRegions.ZoneOf(assignee)) == TimeRegions.Mine(Me))
                continue;
            var (away, local) = OutsideWorkingHours(assignee, now);
            if (away is not true)
                continue; // the colleague is back
            var who = assignee?.Name ?? $"User {assigneeId}";
            Log.Info($"Ticket #{ticket.Id}: {reply.Customer} has waited {InProgressGrace.TotalHours:0} h without an answer ({StatusName(ticket)}), {who} is off work");
            await ShowCustomerWaitingAsync(ticket, group, who, local, reply, now, ct);
        }
    }

    /// <summary>
    /// The newest public reply by an end user (the customer) after <paramref name="since"/>, if any. With
    /// <see cref="Settings.DropAnswered"/>, none when an agent has already replied publicly after it.
    /// </summary>
    private async Task<(string Customer, AuditEvent Comment, DateTimeOffset At)?> LatestCustomerReplyAsync(Ticket ticket, DateTimeOffset since, CancellationToken ct)
    {
        var audits = await zendesk.GetAuditsSinceAsync(ticket.Id, since, ct);
        foreach (var audit in audits.OrderByDescending(a => a.CreatedAt))
        {
            var comment = audit.Events.FirstOrDefault(e => e.Type == "Comment" && e.Public == true);
            if (comment is null || audit.AuthorId == SystemUserId)
                continue;
            var author = await zendesk.GetUserAsync(audit.AuthorId, ct);
            if (TicketRules.IsCustomer(author, ticket, Me?.Email))
                return (author!.Name ?? "The customer", comment, audit.CreatedAt);
            // Someone from us (an agent, or an end-user from your own company) wrote after the customer.
            if (settings.DropAnswered && author is not null)
            {
                Log.Info($"Ticket #{ticket.Id}: an agent has already answered the latest customer reply");
                return null;
            }
        }
        return null;
    }

    /// <summary>
    /// Of notifications that waited a while, the ones still worth showing: the ticket still needs attention
    /// (<see cref="TicketRules.StillNeedsAttention"/>) and, with <paramref name="dropAnswered"/> and
    /// <paramref name="deep"/>, nobody has dealt with it since (<see cref="TicketRules.HandledSince"/>, one audit
    /// read per ticket). Notifications without a ticket are kept. Failing reads keep the notification.
    /// </summary>
    public static async Task<List<NotificationContent>> StillNeededAsync(ZendeskClient zendesk, long me,
        IReadOnlyList<NotificationContent> waiting, bool dropAnswered, bool deep, CancellationToken ct)
    {
        var ids = waiting.Select(c => c.TicketId).OfType<long>().Distinct().ToList();
        var live = new Dictionary<long, Ticket>();
        try
        {
            foreach (var chunk in ids.Chunk(100)) // show_many takes up to 100 ids
                foreach (var t in await zendesk.GetTicketsAsync(chunk.ToList(), ct))
                    live[t.Id] = t;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Error("Could not check waiting notifications; showing them anyway", ex);
            return waiting.ToList();
        }

        var kept = new List<NotificationContent>();
        foreach (var content in waiting)
        {
            if (content.TicketId is not { } id)
            {
                kept.Add(content);
                continue;
            }
            if (!TicketRules.StillNeedsAttention(content.Kind, live.GetValueOrDefault(id)))
            {
                Log.Info($"No longer needed (assigned, solved or gone): {content.Title}");
                continue;
            }
            if (deep && dropAnswered && content.ChangedAt is { } changedAt && content.Kind != NotificationKind.NewTicket)
            {
                try
                {
                    var after = new List<(Audit, string?)>();
                    foreach (var audit in await zendesk.GetAuditsSinceAsync(id, changedAt, ct))
                        after.Add((audit, audit.AuthorId > 0 ? (await zendesk.GetUserAsync(audit.AuthorId, ct))?.Role : null));
                    if (TicketRules.HandledSince(content.Kind, after, me))
                    {
                        Log.Info($"No longer needed (answered or handled since): {content.Title}");
                        continue;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Log.Error($"Could not read the changes of #{id}; showing it anyway", ex);
                }
            }
            kept.Add(content);
        }
        return kept;
    }

    /// <summary>
    /// Whether it's outside working hours (Mon-Fri, <see cref="Settings.WorkdayStartHour"/>-<see cref="Settings.WorkdayEndHour"/>)
    /// in the user's time zone. Null when the time zone is unknown: then we can't tell and stay quiet.
    /// </summary>
    private (bool? Away, DateTime Local) OutsideWorkingHours(User? user, DateTimeOffset now)
    {
        var zone = TicketRules.ResolveTimeZone(user?.IanaTimeZone) ?? TicketRules.ResolveTimeZone(user?.TimeZone);
        if (zone is null)
            return (null, default);
        var local = TimeZoneInfo.ConvertTime(now, zone).DateTime;
        return (IsOutsideHours(local), local);
    }

    private bool IsOutsideHours(DateTime local) =>
        TicketRules.IsOutsideHours(local, settings.WorkdayStartHour, settings.WorkdayEndHour, settings.WorkDays);

    /// <summary>Outside my own working hours right now (Notifications → My working hours).</summary>
    private bool IAmOffWork() =>
        TicketRules.IsOutsideHours(DateTime.Now, settings.MyWorkdayStartHour, settings.MyWorkdayEndHour, settings.MyWorkDays);

    /// <summary>Reads the audits since the last known update and notifies about changes made by someone else.</summary>
    /// <param name="context">Shown next to the heading, e.g. "You're CC" for tickets that aren't mine.</param>
    /// <param name="kind"><see cref="NotificationKind.CcTicket"/> for tickets I'm CC'd on or follow (their own sound).</param>
    private async Task ReportChangesAsync(Ticket ticket, DateTimeOffset since, CancellationToken ct, string? context = null,
        NotificationKind kind = NotificationKind.MyTicket)
    {
        // Not a priority you chose: stay quiet (and skip reading its changes). The caller still records the ticket's
        // new state, so these changes don't pop up later if the priority is ticked again.
        var priorities = kind == NotificationKind.CcTicket ? settings.CcTicketPriorities : settings.MyTicketPriorities;
        var important = IsImportant(ticket); // important clients: whatever the priority, also outside my hours
        if (!TicketRules.MatchesPriority(ticket.Priority, priorities) && !important)
            return;
        // Outside my own working hours (if chosen): same, the changes are skipped rather than saved for later.
        if (settings.OnlyNotifyDuringMyHours && IAmOffWork() && !important)
            return;

        var me = Me!.Id;
        var all = (await zendesk.GetAuditsSinceAsync(ticket.Id, since, ct)).OrderBy(a => a.CreatedAt).ToList();
        var audits = all.Where(a => a.AuthorId != me);
        // The customer wrote last (no public answer since, also not mine): the follow-up SLA clock runs from then.
        DateTimeOffset? customerWroteAt = null;
        if (all.LastOrDefault(a => a.Events.Any(e => e.Type == "Comment" && e.Public == true)) is { } lastPublic && lastPublic.AuthorId != me
            && TicketRules.IsCustomer(await zendesk.GetUserAsync(lastPublic.AuthorId, ct), ticket, Me?.Email))
            customerWroteAt = lastPublic.CreatedAt;

        var lines = new List<string>();
        DateTimeOffset? changedAt = null;
        var logLines = new List<string>(); // never with comment text: the log may be shared when troubleshooting
        foreach (var audit in audits)
        {
            var author = audit.AuthorId == SystemUserId ? "Zendesk" : await zendesk.GetUserNameAsync(audit.AuthorId, ct);
            // A custom status change ("In progress → Escalated") says more than the basic one recorded with it.
            var hasCustomStatus = audit.Events.Any(e => e.Type == "Change" && e.FieldName == "custom_status_id");
            // A reply shown in the same update: a reopen with it goes without saying (TicketRules.IsImpliedReopen).
            var withReply = settings.NotifyPublicReplies && audit.Events.Any(e => e.Type == "Comment" && e.Public == true);
            foreach (var ev in audit.Events)
            {
                if (hasCustomStatus && ev is { Type: "Change", FieldName: "status" })
                    continue;
                if (await DescribeAsync(ev, author, settings.ShowCommentText, ct) is not { } line)
                    continue;
                if (withReply && TicketRules.IsImpliedReopen(StatusTo(ev), withPublicReply: true))
                    continue;
                lines.Add(line);
                changedAt = audit.CreatedAt;
                logLines.Add(Markdown.Strip(ev.Type == "Comment" ? (await DescribeAsync(ev, author, withText: false, ct))! : line));
            }
        }

        if (lines.Count == 0)
            return;

        Log.Info($"Ticket #{ticket.Id}: {string.Join(" | ", logLines)}");

        var shown = lines.TakeLast(MaxLinesPerToast).ToList();
        if (lines.Count > shown.Count)
            shown.Insert(0, $"(+{lines.Count - shown.Count} earlier changes)");
        // Who the ticket is from, then what changed; not when a line already names them ("Jane Doe replied: …").
        if (await RequesterNameAsync(ticket, ct) is { } requester && !shown.Any(l => Markdown.Strip(l).Contains(requester)))
            shown.Insert(0, Markdown.Escape(requester));

        Show(await WithClientAsync(new NotificationContent(kind, "Ticket updated", $"#{ticket.Id} {TextCleanup.Plain(ticket.Subject)}",
            string.Join("\n", shown), ticket.Id, ticket.Priority, context)
        { IsTicketNotification = true, ChangedAt = changedAt }, ticket, null, ct,
            sla: customerWroteAt is { } wrote ? (wrote, FirstReply: false) : null));
    }

    /// <summary>"Now watching Tier 2 · 3 unassigned tickets" when a group is added; a ticket notification if there's just one.</summary>
    private async Task ShowAlreadyWaitingAsync(WatchedGroup group, List<Ticket> tickets, CancellationToken ct)
    {
        var oldestFirst = tickets.OrderBy(t => t.CreatedAt).ToList();
        Log.Info($"Now watching {GroupLabel(group)}: {tickets.Count} unassigned ticket(s) already waiting: {string.Join(",", oldestFirst.Select(t => t.Id))}");

        if (oldestFirst.Count == 1)
        {
            await ShowNewTicketAsync(oldestFirst[0], group, ct, heading: "Already waiting");
            return;
        }

        var scheduled = tickets.Count(IsScheduled);
        Show(new NotificationContent(NotificationKind.NewTicket, "Now watching",
            $"{tickets.Count} unassigned tickets already waiting{Markdown.Strip(TicketRules.ScheduledNote(tickets.Count, scheduled))}",
            TicketList(oldestFirst, IsScheduled), Context: GroupLabel(group))
            { IsTicketNotification = true, TicketCount = tickets.Count, ScheduledCount = scheduled, MaxBodyLines = ListLines },
            tickets.Select(t => t.Id));
    }

    /// <summary>Tickets listed in one notification; more become "… and 4 more".</summary>
    private const int ListedTickets = 5;

    /// <summary>Room for the listed tickets and the "… and N more" line: the notification grows to fit them.</summary>
    internal const int ListLines = ListedTickets + 1;

    /// <summary>"#123 Subject" per line ("· **Scheduled**" after Scheduled ones), up to <see cref="ListedTickets"/>, then "… and N more".</summary>
    internal static string TicketList(IReadOnlyList<Ticket> tickets, Func<Ticket, bool>? scheduled = null)
    {
        var lines = tickets.Take(ListedTickets)
            .Select(t => $"#{t.Id} {Markdown.Escape(TextCleanup.Plain(t.Subject))}{(scheduled?.Invoke(t) == true ? " \u00b7 **Scheduled**" : "")}").ToList();
        if (tickets.Count > ListedTickets)
            lines.Add($"\u2026 and {tickets.Count - ListedTickets} more");
        return string.Join("\n", lines);
    }

    /// <summary>Day and month names in English, whatever the PC's language (the app's texts are English).</summary>
    private static string English(DateTime value, string format) =>
        value.ToString(format, System.Globalization.CultureInfo.GetCultureInfo("en-GB"));

    /// <summary>Who the ticket is from ("Jane Doe"). Null if it can't be read.</summary>
    private async Task<string?> RequesterNameAsync(Ticket ticket, CancellationToken ct) =>
        ticket.RequesterId is { } id && (await zendesk.GetUserAsync(id, ct))?.Name is { Length: > 0 } name ? name : null;

    /// <summary>The client as the "Mute ▾" menu needs it: its current choice (or a Normal one), current name and group.</summary>
    private static ClientChoice MenuTarget(ClientChoice? choice, long id, string name, long groupId)
    {
        var target = choice?.Copy() ?? new ClientChoice { Id = id };
        target.Name = name;
        target.GroupId = groupId;
        return target;
    }

    /// <summary>How the ticket's client notifies right now (Clients page).</summary>
    private ClientMode ClientMode(Ticket ticket) => TicketRules.ClientMode(ticket, settings, DateTimeOffset.UtcNow);

    private bool IsImportant(Ticket ticket) => ClientMode(ticket) == ZendeskWatcher.ClientMode.Important;

    /// <summary>
    /// Adds the client: "Client: Contoso", ★ and "until dismissed"
    /// for important clients, the client's own sound, and on new tickets and colleagues' tickets the "Mute ▾" menu.
    /// </summary>
    /// <param name="sla">Where the SLA clock starts and which target applies (first reply or follow-up); null: no deadline.</param>
    private async Task<NotificationContent> WithClientAsync(NotificationContent content, Ticket ticket, WatchedGroup? group, CancellationToken ct,
        (DateTimeOffset Start, bool FirstReply)? sla = null)
    {
        var choice = settings.ClientChoiceFor(ticket.OrganizationId);
        var important = ClientMode(ticket) == ZendeskWatcher.ClientMode.Important;
        content = content with
        {
            Important = important,
            Heading = important ? $"★ {content.Heading}" : content.Heading,
            Sound = choice?.Sound
        };
        var org = ticket.OrganizationId is { } id ? await zendesk.GetOrganizationAsync(id, ct) : null;
        // The client's SLA type (its organization's "SLA Type"; none counts as Standard) sets the deadline.
        var slaType = SlaTypeOf(org);
        if (org is not null)
            _state.Sla.Types.Add(slaType); // every type seen gets a row on the SLA page, also new ones
        if (settings.ShowSlaDeadlines && sla is var (start, firstReply))
        {
            // Zendesk's own timer when the ticket has one (the real policy, business hours and pauses included), which
            // also teaches the target; otherwise the target for the client's SLA type and the priority.
            var breach = await zendesk.GetSlaBreachAsync(ticket.Id, firstReply ? "first_reply_time" : "next_reply_time", ct);
            if (breach is { } at)
                await NoteSlaTypeAsync(slaType, ct, () => _state.Sla.Record(ticket.Id, slaType, ticket.Priority, firstReply, start, at));
            var due = breach ?? Sla.Due(start, slaType, ticket.Priority, firstReply, settings.SlaOverrides, _state.Sla);
            content = content with { SlaDue = due, SlaFirstReply = firstReply };
        }
        if (ticket.OrganizationId is not { } orgId || org?.Name is not { Length: > 0 } name)
            return content;
        return content with
        {
            // "Contoso · Premium Support": the SLA type when it isn't Standard (only with SLA deadlines on).
            Client = settings.ShowSlaDeadlines && slaType != Sla.StandardKey ? $"{name} · {Sla.Name(slaType, _state.Sla)}" : name,
            ClientId = orgId,
            ClientName = name,
            MuteTarget = group is not null && content.Kind is NotificationKind.NewTicket or NotificationKind.HelpColleague
                ? MenuTarget(choice, orgId, name, group.Id)
                : null
        };
    }

    /// <summary>The client's SLA type: its organization's "SLA Type" value; none counts as Standard.</summary>
    private static string SlaTypeOf(Organization? org) =>
        Sla.Parse(org?.OrganizationFields?.GetValueOrDefault("sla_type") is { ValueKind: System.Text.Json.JsonValueKind.String } v ? v.GetString() : null);

    private bool _slaNamesAsked;

    /// <summary>Records an SLA observation; a type not seen before also fetches the types' names (once per run).</summary>
    private async Task NoteSlaTypeAsync(string type, CancellationToken ct, Func<bool> record)
    {
        var isNew = !_state.Sla.Types.Contains(type);
        record();
        if (isNew && !_slaNamesAsked && Sla.Name(type, _state.Sla) == type)
        {
            _slaNamesAsked = true;
            foreach (var (value, name) in await zendesk.GetSlaTypeNamesAsync(ct))
                _state.Sla.Names[value] = name;
        }
    }

    /// <summary>
    /// Learns the SLA targets from tickets just read: a new ticket's running first-reply timer, from when it was
    /// created to when it breaches (see <see cref="SlaLearning"/>).
    /// </summary>
    private async Task LearnSlaAsync(IEnumerable<Ticket> tickets, CancellationToken ct)
    {
        foreach (var ticket in tickets)
        {
            if (ticket.Slas?.PolicyMetrics.FirstOrDefault(m => m is { Metric: "first_reply_time", Stage: "active", BreachAt: not null }) is not { } timer)
                continue;
            var org = ticket.OrganizationId is { } id ? await zendesk.GetOrganizationAsync(id, ct) : null;
            var type = SlaTypeOf(org);
            await NoteSlaTypeAsync(type, ct, () => _state.Sla.Record(ticket.Id, type, ticket.Priority, true, ticket.CreatedAt, timer.BreachAt!.Value));
        }
    }

    /// <summary>"Jane Doe · Created 14:02" (the time since is added when shown).</summary>
    private async Task<string> CreatedLineAsync(Ticket ticket, CancellationToken ct) =>
        await RequesterNameAsync(ticket, ct) is { } requester ? $"{Markdown.Escape(requester)} · Created {CreatedLabel(ticket)}" : $"Created {CreatedLabel(ticket)}";

    private static string CreatedLabel(Ticket t)
    {
        var local = t.CreatedAt.ToLocalTime();
        return local.Date == DateTime.Today ? $"{local:HH:mm}" : English(local.DateTime, "d MMM HH:mm");
    }

    /// <summary>When each ticket was last notified about in this run, so Catch up doesn't show it again right after.</summary>
    private readonly Dictionary<long, DateTimeOffset> _notifiedAt = [];

    /// <summary>The status a status change goes to, by its name ("Open", "Escalated"); null for other events.</summary>
    private string? StatusTo(AuditEvent ev) => ev switch
    {
        { Type: "Change", FieldName: "status" } => TicketRules.StatusLabel(Text(ev.Value)),
        { Type: "Change", FieldName: "custom_status_id" } => Id(ev.Value) is { } id ? _statusNames.GetValueOrDefault(id) : null,
        _ => null
    };

    /// <summary>Shows a notification and remembers its ticket (and the tickets a summary covers) for Catch up.</summary>
    private void Show(NotificationContent content, IEnumerable<long>? covers = null)
    {
        var now = DateTimeOffset.UtcNow;
        if (content.TicketId is { } id)
            _notifiedAt[id] = now;
        foreach (var covered in covers ?? [])
            _notifiedAt[covered] = now;
        notifier.Show(content);
    }

    internal static string GroupLabel(WatchedGroup group) => group.IsNoGroup ? "No group" : group.Name;

    /// <param name="withText">Include (the start of) the comment text, or only say who commented.</param>
    private async Task<string?> DescribeAsync(AuditEvent ev, string authorName, bool withText, CancellationToken ct)
    {
        var author = Markdown.Escape(authorName); // the text is markdown: who wrote a comment is bold
        switch (ev.Type)
        {
            case "Comment" when ev.Public == true && settings.NotifyPublicReplies:
                return withText ? $"**{author}** replied: {Markdown.Escape(Snippet(ev))}" : $"**{author}** replied";

            case "Comment" when ev.Public == false && settings.NotifyInternalNotes:
                return withText ? $"**{author}** added an internal note: {Markdown.Escape(Snippet(ev))}" : $"**{author}** added an internal note";

            case "Change" when ev.FieldName == "status" && settings.NotifyStatusChanges:
            {
                var (from, to) = (TicketRules.StatusLabel(Text(ev.PreviousValue)), TicketRules.StatusLabel(Text(ev.Value)));
                return TicketRules.IsNoiseStatusChange(from, to) ? null : $"Status {from ?? "?"} → {to} ({author})";
            }

            case "Change" when ev.FieldName == "custom_status_id" && settings.NotifyStatusChanges:
            {
                string? Name(long? id) => id is { } i ? _statusNames.GetValueOrDefault(i) : null;
                var (from, to) = (Name(Id(ev.PreviousValue)), Name(Id(ev.Value)));
                return to is null || TicketRules.IsNoiseStatusChange(from, to) ? null : $"Status {from ?? "?"} → {to} ({author})";
            }

            case "Change" when ev.FieldName == "priority" && settings.NotifyPriorityChanges:
                return $"Priority {NotificationContent.LabelForPriority(Text(ev.PreviousValue)) ?? "None"} → {NotificationContent.LabelForPriority(Text(ev.Value)) ?? "None"} ({author})";

            case "Change" when ev.FieldName == "assignee_id" && settings.NotifyAssignmentChanges:
                var assignee = Id(ev.Value);
                if (assignee == Me!.Id) return $"Assigned to you by {author}";
                if (assignee is null) return $"Unassigned by {author}";
                return $"Reassigned to {Markdown.Escape(await zendesk.GetUserNameAsync(assignee.Value, ct))} by {author}";

            case "Change" when ev.FieldName == "group_id" && settings.NotifyAssignmentChanges:
                var group = Id(ev.Value);
                return $"Moved to group {(group is { } g && _groupNames.TryGetValue(g, out var name) ? name : group?.ToString() ?? "none")} ({author})";

            default:
                return null;
        }
    }

    // Only what change detection needs: no subjects or other ticket content in state.json.
    private static TrackedTicket ToTracked(Ticket t) => new() { Status = t.Status, UpdatedAt = t.UpdatedAt };

    private static string? Text(JsonElement? value) => value?.ValueKind switch
    {
        JsonValueKind.String => value.Value.GetString(),
        JsonValueKind.Number => value.Value.GetRawText(),
        _ => null
    };

    private static long? Id(JsonElement? value) => long.TryParse(Text(value), out var id) ? id : null;

    private static string Snippet(AuditEvent ev)
    {
        var text = TextCleanup.Plain(ev.PlainBody ?? ev.Body);
        return text.Length <= 140 ? text : text[..140] + "…";
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}

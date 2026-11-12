using System.Text.Json;
using System.Text.RegularExpressions;
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

        var clock = System.Diagnostics.Stopwatch.StartNew();
        var parts = new List<string>();
        var total = 0.0;
        void Lap(string name)
        {
            total += clock.Elapsed.TotalSeconds;
            parts.Add($"{name} {clock.Elapsed.TotalSeconds:0.0}s");
            clock.Restart();
        }

        await CheckUnassignedAsync(ct);
        Lap("unassigned");

        if (settings.TrackMyTickets)
        {
            await CheckMyTicketsAsync(ct);
            Lap("my tickets");
        }
        else if (_state.MyTicketsBaselined)
        {
            // Rebuild the baseline when tracking is switched on again, instead of reporting stale changes.
            _state.MyTickets.Clear();
            _state.MyTicketsBaselined = false;
        }

        try
        {
            await CheckHelpColleaguesAsync(ct);
            Lap("help colleagues");
        }
        catch (Exception ex) when (ex is not (AuthRequiredException or OperationCanceledException))
        {
            Log.Error("Help colleagues check failed", ex); // never let it stop the other notifications
        }

        if (settings.TrackCcTickets)
        {
            await CheckCcTicketsAsync(ct);
            Lap("cc");
        }
        else if (_state.CcTicketsBaselined)
        {
            _state.CcTickets.Clear();
            _state.CcTicketsBaselined = false;
        }

        _state.Save();

        // Slow checks are logged with a breakdown, to see which part takes the time.
        if (total > 5)
            Log.Info($"Slow check ({total:0.0}s): {string.Join(", ", parts)}");
    }

    private async Task CheckUnassignedAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var total = 0;
        var byGroup = new List<(WatchedGroup Group, List<Ticket> Tickets)>();
        ShowedAlreadyWaiting = false;

        // Search finds the candidates, but its index can be stale for hours: a ticket assigned long ago may still
        // match "assignee:none". Re-read every candidate (exact, no index) and keep only the really unassigned ones.
        var found = new List<(WatchedGroup Group, List<Ticket> Tickets)>();
        foreach (var group in settings.WatchedGroups)
            found.Add((group, await zendesk.GetUnassignedTicketsAsync(group.Id, ct)));

        var allIds = found.SelectMany(f => f.Tickets).Select(t => t.Id).Distinct().ToList();
        var live = allIds.Count > 0 ? (await zendesk.GetTicketsAsync(allIds, ct)).ToDictionary(t => t.Id) : [];

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
            total += tickets.Count;
            byGroup.Add((group, tickets));

            var isBaseline = _state.BaselinedGroups.Add(group.Id);
            var fresh = tickets.Where(t => !_state.SeenUnassigned.ContainsKey(t.Id)).OrderBy(t => t.CreatedAt).ToList();

            foreach (var ticket in tickets)
                _state.SeenUnassigned[ticket.Id] = now;

            // A newly monitored group: one summary of what's already waiting instead of a notification per ticket.
            if (isBaseline && settings.NotifyUnassigned && tickets.Count > 0)
            {
                ShowAlreadyWaiting(group, tickets);
                ShowedAlreadyWaiting = true;
                continue;
            }

            // New tickets are always recorded, so switching notifications on later does not report old ones.
            if (isBaseline || fresh.Count == 0 || !settings.NotifyUnassigned)
                continue;

            Log.Info($"{fresh.Count} new unassigned ticket(s) {group.Describe()}: {string.Join(",", fresh.Select(t => t.Id))}");

            if (fresh.Count > MaxSeparateUnassignedToasts)
            {
                notifier.Show(new NotificationContent(NotificationKind.NewTicket, "New tickets",
                    $"{fresh.Count} new unassigned tickets",
                    string.Join("\n", fresh.Take(3).Select(t => $"#{t.Id} {TextCleanup.Plain(t.Subject)}")),
                    Context: GroupLabel(group)));
                continue;
            }

            foreach (var ticket in fresh)
            {
                notifier.Show(new NotificationContent(NotificationKind.NewTicket, "New ticket", $"#{ticket.Id} {TextCleanup.Plain(ticket.Subject)}",
                    $"Created {CreatedLabel(ticket)}", ticket.Id, ticket.Priority, GroupLabel(group)) { CanTake = true, EventTime = ticket.CreatedAt });
            }
        }

        // Forget tickets that have not been unassigned for a while, so they notify again if they come back.
        foreach (var id in _state.SeenUnassigned.Where(kv => now - kv.Value > ForgetUnassignedAfter).Select(kv => kv.Key).ToList())
            _state.SeenUnassigned.Remove(id);

        UnassignedCount = total;
        _unassigned = byGroup;
    }

    /// <summary>
    /// What's waiting and what's being watched, from the last check. The headline is the title ("5 unassigned tickets
    /// waiting"); each line below is one fact: "Support Tier 2: 3 · Premium: 2", "Oldest: 2 h ago",
    /// "Your tickets: 3 open, 4 pending", "CC / following: 2 tickets", "Help colleagues: Urgent, High".
    /// </summary>
    public NotificationContent StartSummary()
    {
        var groups = settings.WatchedGroups;
        var waiting = _unassigned.SelectMany(g => g.Tickets).DistinctBy(t => t.Id).ToList();
        var headline = (groups.Count, waiting.Count) switch
        {
            (0, _) => "Not watching any groups",
            (_, 0) => "No unassigned tickets",
            (_, 1) => "1 unassigned ticket waiting",
            (_, var n) => $"{n} unassigned tickets waiting"
        };

        var lines = new List<string> { headline };
        if (groups.Count > 0)
        {
            // Where: the count per group when it's spread over several, otherwise just the groups watched.
            var withTickets = _unassigned.Where(g => g.Tickets.Count > 0).ToList();
            if (groups.Count > 1 && withTickets.Count > 0)
                lines.Add(string.Join(" · ", withTickets.Select(g => $"{GroupLabel(g.Group)}: {g.Tickets.Count}")));
            else
                lines.Add(groups.Count <= 3 ? "In " + string.Join(", ", groups.Select(GroupLabel)) : $"In {groups.Count} groups");
            if (waiting.Count > 0)
                lines.Add("Oldest: " + NotificationContent.Ago(DateTimeOffset.UtcNow - waiting.Min(t => t.CreatedAt)));
        }

        if (settings.TrackMyTickets)
        {
            var mine = _state.MyTickets.Values;
            var parts = new[]
            {
                (Count: mine.Count(t => t.Status is "new" or "open"), Label: "open"),
                (Count: mine.Count(t => t.Status == "pending"), Label: "pending"),
                (Count: mine.Count(t => t.Status == "hold"), Label: "on hold")
            }.Where(p => p.Count > 0).Select(p => $"{p.Count} {p.Label}").ToList();
            lines.Add("Your tickets: " + (parts.Count == 0 ? "none open" : string.Join(", ", parts)));
        }

        if (settings.TrackCcTickets)
            lines.Add("CC / following: " + _state.CcTickets.Count switch { 0 => "none open", 1 => "1 ticket", var n => $"{n} tickets" });

        if (settings.EffectiveHelpGroups.Count > 0)
        {
            var priorities = new[] { ("urgent", "Urgent"), ("high", "High"), ("normal", "Normal"), ("low", "Low") }
                .Where(p => settings.HelpPriorities.Contains(p.Item1)).Select(p => p.Item2).ToList();
            lines.Add("Help colleagues: " + (priorities.Count is 0 or 4 ? "all priorities" : string.Join(", ", priorities)));
        }

        return new NotificationContent(NotificationKind.MyTicket, "Zendesk Watcher", TicketRules.Greeting(DateTime.Now, Me?.Name), string.Join("\n", lines));
    }

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
        var recent = await zendesk.GetRecentlyUpdatedAssignedAsync(me, ct);
        var newlyAssigned = recent
            .Where(t => t.IsOpen && !_state.MyTickets.ContainsKey(t.Id) && t.UpdatedAt > previousWatermark)
            .ToList();
        if (recent.Count > 0 && recent.Max(t => t.UpdatedAt) > previousWatermark)
            _state.MyTicketsWatermark = recent.Max(t => t.UpdatedAt);

        // 2. Changes on tickets already tracked. show_many also covers tickets that left my assigned list.
        var current = _state.MyTickets.Count > 0
            ? await zendesk.GetTicketsAsync(_state.MyTickets.Keys.ToList(), ct)
            : [];

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
            Log.Info($"Baseline: tracking {_state.CcTickets.Count} open ticket(s) where I'm CC or follower");
            return;
        }

        // 1. Newly CC'd/followed: updated since the last poll and not tracked yet.
        var previousWatermark = _state.CcTicketsWatermark;
        var recent = await zendesk.GetRecentlyUpdatedInvolvedAsync(me, ct);
        var added = recent
            .Where(t => Relevant(t) && !_state.CcTickets.ContainsKey(t.Id) && t.UpdatedAt > previousWatermark)
            .ToList();
        if (recent.Count > 0 && recent.Max(t => t.UpdatedAt) > previousWatermark)
            _state.CcTicketsWatermark = recent.Max(t => t.UpdatedAt);

        // 2. Changes on tracked tickets.
        var current = _state.CcTickets.Count > 0 ? await zendesk.GetTicketsAsync(_state.CcTickets.Keys.ToList(), ct) : [];
        foreach (var missing in _state.CcTickets.Keys.Except(current.Select(t => t.Id)).ToList())
            _state.CcTickets.Remove(missing);

        foreach (var ticket in current)
        {
            var tracked = _state.CcTickets[ticket.Id];

            // Once it's assigned to me, my-ticket tracking reports it; don't notify twice.
            if (ticket.UpdatedAt > tracked.UpdatedAt && ticket.AssigneeId != me)
                await ReportChangesAsync(ticket, since: tracked.UpdatedAt, ct, context: TicketRules.Involvement(ticket, me) ?? "CC");

            if (Relevant(ticket) && TicketRules.Involvement(ticket, me) is not null)
                _state.CcTickets[ticket.Id] = ToTracked(ticket);
            else
                _state.CcTickets.Remove(ticket.Id);
        }

        foreach (var ticket in added)
        {
            await ReportChangesAsync(ticket, since: previousWatermark, ct, context: TicketRules.Involvement(ticket, me) ?? "CC");
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
        var iAmWorking = !settings.HelpOnlyDuringMyHours || !IsOutsideHours(DateTime.Now);

        foreach (var group in groups)
        {
            var baseline = _state.HelpBaselinedGroups.Add(group.Id);
            var tickets = await zendesk.GetAssignedOpenTicketsUpdatedSinceAsync(group.Id, since, ct);

            foreach (var ticket in tickets)
            {
                if (ticket.AssigneeId is not { } assigneeId || assigneeId == me)
                    continue; // your own tickets are covered by "my tickets"
                if (excluded.Contains(assigneeId))
                    continue; // a colleague you chose not to cover
                if (!TicketRules.MatchesHelpPriority(ticket.Priority, settings.HelpPriorities))
                    continue; // not a priority you chose (no priority counts as normal)
                var seen = _state.HelpSeen.GetValueOrDefault(ticket.Id);
                if (ticket.UpdatedAt <= seen)
                    continue;
                _state.HelpSeen[ticket.Id] = ticket.UpdatedAt;
                if (baseline || !iAmWorking || _state.CcTickets.ContainsKey(ticket.Id))
                    continue; // first look at this group, you're off work, or already reported as a CC'd ticket

                var reply = await LatestCustomerReplyAsync(ticket, seen == default ? since : seen, ct);
                if (reply is null)
                    continue;

                var assignee = await zendesk.GetUserAsync(assigneeId, ct);
                var (away, local) = OutsideWorkingHours(assignee, now);
                if (away is not true)
                {
                    if (away is null)
                        Log.Info($"Ticket #{ticket.Id}: customer replied, but {assignee?.Name ?? $"user {assigneeId}"} has no usable time zone");
                    continue;
                }

                var who = assignee?.Name ?? $"User {assigneeId}";
                var (customer, comment) = reply.Value;
                Log.Info($"Ticket #{ticket.Id}: {customer} replied while {who} is outside working hours ({English(local, "ddd HH:mm")} their time)");

                var said = settings.ShowCommentText ? $"{customer} replied: {Snippet(comment)}" : $"{customer} replied";
                notifier.Show(new NotificationContent(NotificationKind.HelpColleague, "Customer waiting",
                    $"#{ticket.Id} {TextCleanup.Plain(ticket.Subject)}",
                    $"{said}\n{who} is off work · {English(local, "ddd HH:mm")} their time",
                    ticket.Id, ticket.Priority, GroupLabel(group)));
            }
        }

        _state.HelpLastCheck = now;
        foreach (var old in _state.HelpSeen.Where(kv => now - kv.Value > TimeSpan.FromDays(3)).Select(kv => kv.Key).ToList())
            _state.HelpSeen.Remove(old);
    }

    /// <summary>The newest public reply by an end user (the customer) after <paramref name="since"/>, if any.</summary>
    private async Task<(string Customer, AuditEvent Comment)?> LatestCustomerReplyAsync(Ticket ticket, DateTimeOffset since, CancellationToken ct)
    {
        var audits = await zendesk.GetAuditsSinceAsync(ticket.Id, since, ct);
        foreach (var audit in audits.OrderByDescending(a => a.CreatedAt))
        {
            var comment = audit.Events.FirstOrDefault(e => e.Type == "Comment" && e.Public == true);
            if (comment is null || audit.AuthorId == SystemUserId)
                continue;
            var author = await zendesk.GetUserAsync(audit.AuthorId, ct);
            if (author?.Role == "end-user")
                return (author.Name ?? "The customer", comment);
        }
        return null;
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
        TicketRules.IsOutsideHours(local, settings.WorkdayStartHour, settings.WorkdayEndHour);

    /// <summary>Reads the audits since the last known update and notifies about changes made by someone else.</summary>
    /// <param name="context">Shown next to the heading, e.g. "You're CC" for tickets that aren't mine.</param>
    private async Task ReportChangesAsync(Ticket ticket, DateTimeOffset since, CancellationToken ct, string? context = null)
    {
        var me = Me!.Id;
        var audits = (await zendesk.GetAuditsSinceAsync(ticket.Id, since, ct))
            .Where(a => a.AuthorId != me)
            .OrderBy(a => a.CreatedAt);

        var lines = new List<string>();
        var logLines = new List<string>(); // never with comment text: the log may be shared when troubleshooting
        foreach (var audit in audits)
        {
            var author = audit.AuthorId == SystemUserId ? "Zendesk" : await zendesk.GetUserNameAsync(audit.AuthorId, ct);
            foreach (var ev in audit.Events)
            {
                if (await DescribeAsync(ev, author, settings.ShowCommentText, ct) is not { } line)
                    continue;
                lines.Add(line);
                logLines.Add(ev.Type == "Comment" ? (await DescribeAsync(ev, author, withText: false, ct))! : line);
            }
        }

        if (lines.Count == 0)
            return;

        Log.Info($"Ticket #{ticket.Id}: {string.Join(" | ", logLines)}");

        var shown = lines.TakeLast(MaxLinesPerToast).ToList();
        if (lines.Count > shown.Count)
            shown.Insert(0, $"(+{lines.Count - shown.Count} earlier changes)");

        notifier.Show(new NotificationContent(NotificationKind.MyTicket, "Ticket updated", $"#{ticket.Id} {TextCleanup.Plain(ticket.Subject)}",
            string.Join("\n", shown), ticket.Id, ticket.Priority, context));
    }

    /// <summary>"Now watching Tier 2 · 3 unassigned tickets" when a group is added; a ticket notification if there's just one.</summary>
    private void ShowAlreadyWaiting(WatchedGroup group, List<Ticket> tickets)
    {
        var oldestFirst = tickets.OrderBy(t => t.CreatedAt).ToList();
        Log.Info($"Now watching {GroupLabel(group)}: {tickets.Count} unassigned ticket(s) already waiting: {string.Join(",", oldestFirst.Select(t => t.Id))}");

        if (oldestFirst.Count == 1)
        {
            var ticket = oldestFirst[0];
            notifier.Show(new NotificationContent(NotificationKind.NewTicket, "Already waiting", $"#{ticket.Id} {TextCleanup.Plain(ticket.Subject)}",
                $"Created {CreatedLabel(ticket)}", ticket.Id, ticket.Priority, GroupLabel(group)) { CanTake = true, EventTime = ticket.CreatedAt });
            return;
        }

        var lines = oldestFirst.Take(3).Select(t => $"#{t.Id} {TextCleanup.Plain(t.Subject)}").ToList();
        if (oldestFirst.Count > 3)
            lines.Add($"… and {oldestFirst.Count - 3} more");
        notifier.Show(new NotificationContent(NotificationKind.NewTicket, "Now watching",
            $"{tickets.Count} unassigned tickets already waiting", string.Join("\n", lines), Context: GroupLabel(group)));
    }

    /// <summary>Day and month names in English, whatever the PC's language (the app's texts are English).</summary>
    private static string English(DateTime value, string format) =>
        value.ToString(format, System.Globalization.CultureInfo.GetCultureInfo("en-GB"));

    private static string CreatedLabel(Ticket t)
    {
        var local = t.CreatedAt.ToLocalTime();
        return local.Date == DateTime.Today ? $"{local:HH:mm}" : English(local.DateTime, "d MMM HH:mm");
    }

    private static string GroupLabel(WatchedGroup group) => group.IsNoGroup ? "No group" : group.Name;

    /// <param name="withText">Include (the start of) the comment text, or only say who commented.</param>
    private async Task<string?> DescribeAsync(AuditEvent ev, string author, bool withText, CancellationToken ct)
    {
        switch (ev.Type)
        {
            case "Comment" when ev.Public == true && settings.NotifyPublicReplies:
                return withText ? $"{author} replied: {Snippet(ev)}" : $"{author} replied";

            case "Comment" when ev.Public == false && settings.NotifyInternalNotes:
                return withText ? $"{author} added an internal note: {Snippet(ev)}" : $"{author} added an internal note";

            case "Change" when ev.FieldName == "status" && settings.NotifyStatusChanges:
                return $"Status {TicketRules.StatusLabel(Text(ev.PreviousValue)) ?? "?"} → {TicketRules.StatusLabel(Text(ev.Value))} ({author})";

            case "Change" when ev.FieldName == "priority" && settings.NotifyPriorityChanges:
                return $"Priority {NotificationContent.LabelForPriority(Text(ev.PreviousValue)) ?? "None"} → {NotificationContent.LabelForPriority(Text(ev.Value)) ?? "None"} ({author})";

            case "Change" when ev.FieldName == "assignee_id" && settings.NotifyAssignmentChanges:
                var assignee = Id(ev.Value);
                if (assignee == Me!.Id) return $"Assigned to you by {author}";
                if (assignee is null) return $"Unassigned by {author}";
                return $"Reassigned to {await zendesk.GetUserNameAsync(assignee.Value, ct)} by {author}";

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

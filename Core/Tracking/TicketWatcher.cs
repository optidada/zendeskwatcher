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

    /// <summary>The status as agents see it: the custom one ("In progress") if any, else "Open", "Pending", "On-hold", …</summary>
    private string StatusName(Ticket ticket) =>
        ticket.CustomStatusId is { } id && _statusNames.TryGetValue(id, out var name) ? name : TicketRules.StatusLabel(ticket.Status) ?? "";

    /// <summary>" · Escalated" after the off-work line, unless it's plain Open/New.</summary>
    private string StatusSuffix(Ticket ticket)
    {
        var name = StatusName(ticket);
        return name.Length == 0 || name is "Open" or "New" ? "" : $" · {name}";
    }

    private static readonly TimeSpan RequestReportInterval = TimeSpan.FromMinutes(10);
    // Shared like the request counter: a Settings save restarts the watcher, and the minutes must still match the count.
    private static DateTime _lastRequestReport = DateTime.UtcNow;

    /// <summary>
    /// A check. <paramref name="newTicketsOnly"/>: only new unassigned tickets (the quick check between two full ones,
    /// <see cref="Settings.NewTicketsCheckInterval"/>): two or three requests instead of eight.
    /// </summary>
    public async Task PollAsync(CancellationToken ct, bool newTicketsOnly = false)
    {
        if (newTicketsOnly)
        {
            ZendeskClient.Reason.Value = "check: new tickets only";
            await CheckUnassignedAsync(ct);
            _state.Save();
            return;
        }
        // Every 10 minutes: how many Zendesk API requests were made (shows the app's load on Zendesk).
        if (DateTime.UtcNow - _lastRequestReport >= RequestReportInterval)
        {
            var minutes = (DateTime.UtcNow - _lastRequestReport).TotalMinutes;
            var count = ZendeskClient.TakeRequestCount();
            Log.Info($"Zendesk API requests in the last {minutes:0} min: {count} ({count / minutes:0.#} per minute): {ZendeskClient.TakeRequestBreakdown()} · for: {ZendeskClient.TakeRequestsByReason()}");
            _lastRequestReport = DateTime.UtcNow;
        }

        // The parts of a check run side by side: each waits mostly for Zendesk, and they keep separate state (new
        // tickets, my tickets, CC, colleagues). On the app's UI thread their steps never run at the same instant.
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var parts = new List<string>();
        async Task Timed(string name, Func<Task> check)
        {
            ZendeskClient.Reason.Value = "check: " + name;
            var own = System.Diagnostics.Stopwatch.StartNew();
            await check();
            lock (parts)
                parts.Add($"{name} {own.Elapsed.TotalSeconds:0.0}s");
        }
        KeepHistoryFresh(ct); // Experimental features: 90 days of tickets, read in the background
        // New, my and CC'd tickets each re-read some tickets: one request for all of them (fewer requests to Zendesk).
        // Every part joins before any starts, and leaves when done, so one that never asks doesn't hold the others up.
        var batch = new TicketBatch(ids => zendesk.GetTicketsAsync(ids, ct));
        Func<Task> InBatch(Func<TicketBatch.Part, Task> check)
        {
            var part = batch.Join();
            return async () =>
            {
                using (part)
                    await check(part);
            };
        }
        var unassigned = InBatch(part => CheckUnassignedAsync(ct, part));
        var mine = settings.TrackMyTickets ? InBatch(part => CheckMyTicketsAsync(ct, part)) : null;
        var cc = settings.TrackCcTickets ? InBatch(part => CheckCcTicketsAsync(ct, part)) : null;
        var checks = new List<Task> { Timed("unassigned", unassigned) };

        if (mine is not null)
            checks.Add(Timed("my tickets", mine));
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

        if (cc is not null)
            checks.Add(Timed("cc", cc));
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

    /// <summary>Tickets as they are now: through the check's shared request when there is one.</summary>
    private Task<List<Ticket>> ReadTicketsAsync(TicketBatch.Part? part, List<long> ids, CancellationToken ct) =>
        part is not null ? part.ReadAsync(ids) : zendesk.GetTicketsAsync(ids, ct);

    /// <summary>"Taken by Anna" (first name), "Taken by you", or "Solved" / "Closed" when nobody has it but it's done.</summary>
    private async Task<string> TakenNoteAsync(Ticket ticket, CancellationToken ct)
    {
        if (ticket.AssigneeId is not { } assigneeId)
            return ticket.Status == "closed" ? "Closed" : "Solved";
        if (assigneeId == Me?.Id)
            return "Taken by you";
        var name = (await zendesk.GetUserAsync(assigneeId, ct))?.Name;
        return string.IsNullOrWhiteSpace(name) ? "Taken by a colleague" : $"Taken by {name.Trim().Split(' ')[0]}";
    }

    private async Task CheckUnassignedAsync(CancellationToken ct, TicketBatch.Part? part = null)
    {
        var now = DateTimeOffset.UtcNow;
        var total = 0;
        var byGroup = new List<(WatchedGroup Group, List<Ticket> Tickets)>();
        var justBaselined = new HashSet<long>();
        ShowedAlreadyWaiting = false;

        // Search finds the candidates, but its index can be stale for hours: a ticket assigned long ago may still
        // match "assignee:none". Re-read every candidate (exact, no index) and keep only the really unassigned ones.
        // One search per group, all at once.
        var found = (await Task.WhenAll(settings.WatchedGroups.Select(async group =>
            (Group: group, Tickets: await zendesk.GetUnassignedTicketsAsync(group.Id, ct))))).ToList();

        // Plus the new tickets whose notification may still be up, to close it once someone took the ticket.
        var foundIds = found.SelectMany(f => f.Tickets).Select(t => t.Id).ToHashSet();
        var allIds = foundIds.Concat(_announcedNew.Keys).Distinct().ToList();
        var live = allIds.Count > 0 ? (await ReadTicketsAsync(part, allIds, ct)).ToDictionary(t => t.Id) : [];
        await CloseTakenTicketsAsync(live, now, ct);
        await LearnSlaAsync(live.Values.Where(t => foundIds.Contains(t.Id)), ct);

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
            if (isBaseline)
                justBaselined.Add(group.Id);
            var fresh = tickets.Where(t => !_state.SeenUnassigned.ContainsKey(t.Id)).OrderBy(t => t.CreatedAt).ToList();
            foreach (var ticket in tickets)
                _state.SeenUnassigned[ticket.Id] = now;
            tickets = tickets.Where(t => TicketRules.IsWantedClient(t, ClientMode(t))).ToList();
            fresh = fresh.Where(t => TicketRules.IsWantedClient(t, ClientMode(t))).ToList();

            // Priorities not chosen for the group (Notifications → New tickets) work the same way: neither notify nor count. Their priority is kept, so
            // one raised to a chosen priority while still unassigned notifies then.
            var raised = new List<Ticket>();
            foreach (var ticket in tickets)
            {
                if (!WantedPriority(ticket, group))
                    _state.QuietPriority[ticket.Id] = ticket.Priority ?? "normal";
                else if (_state.QuietPriority.Remove(ticket.Id, out var was) &&
                         TicketRules.WasRaisedToWanted(was, ticket.Priority, settings.NewTicketPrioritiesFor(group.Id)) && !fresh.Contains(ticket))
                    raised.Add(ticket);
            }
            tickets = tickets.Where(t => WantedPriority(t, group)).ToList();
            fresh = fresh.Where(t => WantedPriority(t, group)).ToList();

            total += tickets.Count;
            byGroup.Add((group, tickets));

            // A newly monitored group: one summary of what's already waiting instead of a notification per ticket.
            if (isBaseline && settings.NotifyUnassigned && tickets.Count > 0)
            {
                await ShowAlreadyWaitingAsync(group, tickets, ct);
                ShowedAlreadyWaiting = true;
                continue;
            }

            if (!isBaseline && raised.Count > 0 && settings.NotifyUnassigned && !(settings.OnlyNotifyDuringMyHours && await IAmOffWorkAsync(ct)))
            {
                Log.Info($"{raised.Count} unassigned ticket(s) raised to a chosen priority {group.Describe()}: {string.Join(",", raised.Select(t => t.Id))}");
                foreach (var ticket in raised)
                    await ShowNewTicketAsync(ticket, group, ct, "Priority raised");
            }

            // New tickets are always recorded, so switching notifications on later does not report old ones.
            if (isBaseline || fresh.Count == 0 || !settings.NotifyUnassigned)
                continue;
            // Important clients always notify, one by one, also outside my hours.
            var important = fresh.Where(IsImportant).ToList();
            fresh = settings.OnlyNotifyDuringMyHours && await IAmOffWorkAsync(ct) ? [] : fresh.Except(important).ToList();
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
                    Context: GroupLabel(group))
                    {
                        IsTicketNotification = true, TicketCount = fresh.Count, ScheduledCount = scheduled, MaxBodyLines = ListLines, OneRowPerLine = true,
                        Action = NotificationActions.Search(UnassignedQuery(group)) // a click shows them all, not just the first five
                    },
                    fresh.Select(t => t.Id));
                continue;
            }

            foreach (var ticket in fresh)
                await ShowNewTicketAsync(ticket, group, ct);
        }

        await CheckTakenQuicklyAsync(justBaselined, now, ct);

        // Forget tickets that have not been unassigned for a while, so they notify again if they come back.
        foreach (var id in _state.SeenUnassigned.Where(kv => now - kv.Value > ForgetUnassignedAfter).Select(kv => kv.Key).ToList())
            _state.SeenUnassigned.Remove(id);
        foreach (var id in _state.TakenQuickly.Where(kv => now - kv.Value > ForgetUnassignedAfter).Select(kv => kv.Key).ToList())
            _state.TakenQuickly.Remove(id);
        foreach (var id in _state.QuietPriority.Keys.Where(id => !_state.SeenUnassigned.ContainsKey(id)).ToList())
            _state.QuietPriority.Remove(id);

        UnassignedCount = total;
        _unassigned = byGroup;
    }

    /// <summary>How far back to look for tickets someone took between two checks (the search index lags a few minutes).</summary>
    private static readonly TimeSpan TakenQuicklyWindow = TimeSpan.FromMinutes(30);

    /// <summary>
    /// New tickets someone took between two checks, before they were ever seen unassigned: for the priorities chosen under
    /// Notifications → New tickets, an "Already taken" notification anyway, saying who took it and how fast (for managers
    /// who don't want to miss a P1). One search per group, plus each candidate's history, only while it's switched on.
    /// Muted clients stay quiet, important ones always come; my working hours count as for new tickets.
    /// </summary>
    private async Task CheckTakenQuicklyAsync(IReadOnlySet<long> justBaselined, DateTimeOffset now, CancellationToken ct)
    {
        if (settings.TakenQuicklyPriorities.Count == 0 || !settings.NotifyUnassigned)
            return;
        var since = now - TakenQuicklyWindow;
        bool? offWork = null;
        foreach (var group in settings.WatchedGroups.Where(g => !g.IsNoGroup && !justBaselined.Contains(g.Id)))
        {
            List<Ticket> found;
            try
            {
                found = await zendesk.GetAssignedTicketsCreatedSinceAsync(group.Id, since, ct);
            }
            catch (HttpRequestException ex)
            {
                Log.Error($"Could not look for quickly taken tickets {group.Describe()}", ex);
                continue;
            }
            foreach (var ticket in found.Where(t => t.CreatedAt > since && t.AssigneeId is not null && t.GroupId == group.Id &&
                                                    !_state.SeenUnassigned.ContainsKey(t.Id) && !_state.TakenQuickly.ContainsKey(t.Id)))
            {
                _state.TakenQuickly[ticket.Id] = now; // looked at once, whatever comes of it
                var important = IsImportant(ticket);
                if (!important && (!TicketRules.MatchesPriority(ticket.Priority, settings.TakenQuicklyPriorities) ||
                                   !TicketRules.IsWantedClient(ticket, ClientMode(ticket))))
                    continue;
                if (!important && settings.OnlyNotifyDuringMyHours && (offWork ??= await IAmOffWorkAsync(ct)))
                    continue;

                // Its history: when it got its first assignee, and from whom. Created assigned: nobody took it.
                var audits = await zendesk.GetAuditsSinceAsync(ticket.Id, ticket.CreatedAt.AddSeconds(-1), ct);
                if (TicketRules.FirstAssignment(audits) is not { } taken)
                    continue;
                var assignee = await zendesk.GetUserNameAsync(taken.AssigneeId, ct);
                var assigner = taken.ByRule || taken.AuthorId == taken.AssigneeId ? null : await zendesk.GetUserNameAsync(taken.AuthorId, ct);
                Log.Info($"Ticket #{ticket.Id} {group.Describe()} was taken before it was seen unassigned ({ticket.Priority}, after {(taken.At - ticket.CreatedAt).TotalMinutes:0} min)");
                // Solving needs an assignee, so a ticket solved or closed straight away is found here too: the line says so.
                var body = await CreatedLineAsync(ticket, ct) + "\n" + TicketRules.TakenLine(assignee, assigner, taken.ByRule, taken.At - ticket.CreatedAt) +
                           (ticket.IsOpen ? "" : $" · {ticket.Status}");
                Show(await WithClientAsync(new NotificationContent(NotificationKind.NewTicket, "Already taken",
                    $"#{ticket.Id} {TextCleanup.Plain(ticket.Subject)}", body, ticket.Id, ticket.Priority, GroupLabel(group))
                    { EventTime = ticket.CreatedAt, IsTicketNotification = true }, ticket, group, ct));
            }
        }
    }

    private async Task ShowNewTicketAsync(Ticket ticket, WatchedGroup group, CancellationToken ct, string heading = "New ticket")
    {
        Show(await UnassignedTicketAsync(ticket, group, heading, ct));
        _announcedNew[ticket.Id] = DateTimeOffset.UtcNow;
    }

    /// <summary>New tickets with a notification of their own, and when: once taken, the notification closes.</summary>
    private readonly Dictionary<long, DateTimeOffset> _announcedNew = [];

    /// <summary>
    /// Closes the notifications of new tickets that someone took (or solved) since. Read exactly (the same request as
    /// the candidates, so no extra one), never from the search alone: its index lags and could close one too early.
    /// </summary>
    private async Task CloseTakenTicketsAsync(IReadOnlyDictionary<long, Ticket> live, DateTimeOffset now, CancellationToken ct)
    {
        foreach (var (id, at) in _announcedNew.ToList())
        {
            var ticket = live.GetValueOrDefault(id);
            var taken = ticket is not null && TicketRules.IsTaken(ticket);
            if (taken)
            {
                // Says who took it for a few seconds ("Taken by Anna"), instead of vanishing without a word.
                var note = await TakenNoteAsync(ticket!, ct);
                Log.Info($"Ticket #{id} was taken: its notification closes ({note})");
                notifier.CloseTaken(id, note);
            }
            if (taken || now - at > ForgetAnnouncedAfter)
                _announcedNew.Remove(id);
        }
    }

    private static readonly TimeSpan ForgetAnnouncedAfter = TimeSpan.FromHours(12);

    /// <summary>One unassigned ticket; "· Scheduled" in the heading when a time is booked and nobody owns it.</summary>
    private async Task<NotificationContent> UnassignedTicketAsync(Ticket ticket, WatchedGroup group, string heading, CancellationToken ct)
    {
        var scheduled = IsScheduled(ticket);
        var body = await CreatedLineAsync(ticket, ct);
        // What the customer wrote (Look & sound → show what was written), the start of it: the notification is a preview.
        if (settings.ShowCommentText && TextCleanup.Plain(ticket.Description) is { Length: > 0 } message)
            body += "\n" + Quote(Markdown.Escape(message.Length <= 200 ? message : message[..200] + "\u2026"));
        return await WithClientAsync(new NotificationContent(NotificationKind.NewTicket, scheduled ? $"{heading} \u00b7 Scheduled" : heading,
            $"#{ticket.Id} {TextCleanup.Plain(ticket.Subject)}", body, ticket.Id, ticket.Priority, GroupLabel(group))
            { CanTake = true, EventTime = ticket.CreatedAt, IsTicketNotification = true, ScheduledCount = scheduled ? 1 : 0 }, ticket, group, ct,
            sla: (ticket.CreatedAt, FirstReply: true), customerTexts: settings.GuessPriority ? await FirstMessageAsync(ticket, ct) : null);
    }

    /// <summary>The ticket's first message (for the priority guess); empty if it can't be read.</summary>
    private async Task<IReadOnlyList<string>> FirstMessageAsync(Ticket ticket, CancellationToken ct)
    {
        try
        {
            var first = (await zendesk.GetAuditsSinceAsync(ticket.Id, ticket.CreatedAt.AddSeconds(-1), ct)).OrderBy(a => a.CreatedAt)
                .SelectMany(a => a.Events).FirstOrDefault(e => e.Type == "Comment");
            return first is null ? [] : [TextCleanup.Plain(first.PlainBody ?? first.Body)];
        }
        catch (HttpRequestException ex)
        {
            Log.Error($"Ticket #{ticket.Id}: could not read the first message for the priority guess", ex);
            return [];
        }
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

    private async Task CheckMyTicketsAsync(CancellationToken ct, TicketBatch.Part? part = null)
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
        var currentTask = _state.MyTickets.Count > 0 ? ReadTicketsAsync(part, _state.MyTickets.Keys.ToList(), ct) : Task.FromResult(new List<Ticket>());
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
    private async Task CheckCcTicketsAsync(CancellationToken ct, TicketBatch.Part? part = null)
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
        var currentTask = _state.CcTickets.Count > 0 ? ReadTicketsAsync(part, _state.CcTickets.Keys.ToList(), ct) : Task.FromResult(new List<Ticket>());
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
        var now = DateTimeOffset.UtcNow;
        var since = (_state.HelpLastCheck == default ? now.AddHours(-1) : _state.HelpLastCheck) - HelpLookBack;

        // Outside your own working hours: keep track (so nothing old pops up later) but don't notify.
        var iAmWorking = !settings.HelpOnlyDuringMyHours || !await IAmOffWorkAsync(ct);

        // One search per covered group, all at once; then the tickets are looked at group by group.
        var fetched = await Task.WhenAll(groups.Select(group => zendesk.GetAssignedOpenTicketsUpdatedSinceAsync(group.Id, since, ct)));
        foreach (var (group, tickets) in groups.Zip(fetched))
        {
            var baseline = _state.HelpBaselinedGroups.Add(group.Id);

            foreach (var ticket in tickets)
            {
                if (ticket.AssigneeId is not { } assigneeId || assigneeId == me)
                    continue; // your own tickets are covered by "my tickets"
                var assignee = await zendesk.GetUserAsync(assigneeId, ct); // remembered for the session
                if (!settings.CoversColleague(assigneeId, TimeRegions.Of(TimeRegions.ZoneOf(assignee)), TimeRegions.Mine(Me)))
                    continue; // a colleague you chose not to cover (their region's tick, or one by one)
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
                if (IsQuietThankYou(reply.Value.Comment))
                {
                    Log.Info($"Ticket #{ticket.Id}: the customer's reply is just a thank-you; no need to step in");
                    continue;
                }

                // Off work by their hours (unless active in Zendesk just now), or in their hours but inactive for long.
                var (away, local, why) = await ColleagueAwayAsync(assigneeId, assignee, now, ct);
                var who = assignee?.Name ?? $"User {assigneeId}";
                if (away is not true)
                {
                    if (away is null)
                        Log.Info($"Ticket #{ticket.Id}: customer replied, but {who} has no usable time zone");
                    else if (OutsideWorkingHours(assignee, now).Away is true)
                        Log.Info($"Ticket #{ticket.Id}: customer replied; {who} is outside working hours but active in Zendesk");
                    continue;
                }

                Log.Info($"Ticket #{ticket.Id}: {reply.Value.Customer} replied; stepping in for {who} ({Markdown.Strip(why)})");
                await ShowCustomerWaitingAsync(ticket, group, why, reply.Value, now, ct);
            }
        }

        _state.HelpLastCheck = now;
        foreach (var old in _state.HelpSeen.Where(kv => now - kv.Value > TimeSpan.FromDays(3)).Select(kv => kv.Key).ToList())
            _state.HelpSeen.Remove(old);
    }

    /// <summary>"Colleague's ticket": what the customer wrote, why the colleague can't (<paramref name="why"/>), and the status if it says something.</summary>
    private async Task ShowCustomerWaitingAsync(Ticket ticket, WatchedGroup group, string why,
        (string Customer, AuditEvent Comment, DateTimeOffset At) reply, DateTimeOffset now, CancellationToken ct)
    {
        var said = Said(reply.Customer, reply.Comment);
        Show(await WithClientAsync(new NotificationContent(NotificationKind.HelpColleague, "Colleague's ticket",
            $"#{ticket.Id} {TextCleanup.Plain(ticket.Subject)}",
            WhyUnderName(said, why + StatusSuffix(ticket)),
            ticket.Id, ticket.Priority, GroupLabel(group))
        { IsTicketNotification = true, ChangedAt = reply.At, EventTime = reply.At }, ticket, group, ct, sla: (reply.At, FirstReply: false),
            customerTexts: [TextCleanup.Plain(reply.Comment.PlainBody ?? reply.Comment.Body)], waitingSince: reply.At));
    }

    /// <summary>
    /// The newest public reply by an end user (the customer) after <paramref name="since"/>, if any. With
    /// <see cref="Settings.DropAnswered"/>, none when an agent has already replied publicly after it.
    /// </summary>
    private async Task<(string Customer, AuditEvent Comment, DateTimeOffset At)?> LatestCustomerReplyAsync(Ticket ticket, DateTimeOffset since, CancellationToken ct)
    {
        // Only the comments (who wrote what): much less than the full history, and all this needs.
        var audits = await zendesk.GetCommentsSinceAsync(ticket.Id, since, ct);
        foreach (var audit in audits.OrderByDescending(a => a.CreatedAt))
        {
            var comment = audit.Events.FirstOrDefault(e => e.Type == "Comment" && e.Public == true);
            if (comment is null || audit.AuthorId == SystemUserId)
                continue;
            var author = await zendesk.GetUserAsync(audit.AuthorId, ct);
            if (TicketRules.IsCustomer(author, ticket, Me?.Email))
                return (author!.Name ?? "The client", comment, audit.CreatedAt);
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
                    // A colleague's ticket is handled by an agent's public reply: the comments are enough. My own or a
                    // CC'd one by any change of mine (a status, a field): the full history.
                    var changes = content.Kind == NotificationKind.HelpColleague
                        ? await zendesk.GetCommentsSinceAsync(id, changedAt, ct)
                        : await zendesk.GetAuditsSinceAsync(id, changedAt, ct);
                    foreach (var audit in changes)
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

    /// <summary>Colleague id → when their activity was read, and their latest change or comment then (null: none in 3 h).</summary>
    private readonly Dictionary<long, (DateTimeOffset ReadAt, DateTimeOffset? Last, bool Known)> _activity = [];
    private static readonly TimeSpan RememberActivity = TimeSpan.FromMinutes(15);

    /// <summary>Zendesk didn't send ticket metrics: colleague activity is read from ticket histories (until checking restarts).</summary>
    private bool _assigneeMetricsUnavailable;

    /// <summary>
    /// The colleague's latest change or comment on one of their tickets in the last 3 hours (null: none), from the 5
    /// they updated last; remembered for 15 minutes. Known false when Zendesk couldn't be read.
    /// </summary>
    private async Task<(bool Known, DateTimeOffset? Last)> LastActiveAsync(long userId, DateTimeOffset now, CancellationToken ct)
    {
        if (_activity.TryGetValue(userId, out var remembered) && now - remembered.ReadAt < RememberActivity)
            return (remembered.Known, remembered.Last); // a failed read is remembered too: not retried on every check
        ZendeskClient.Reason.Value = "colleague activity";
        try
        {
            var since = now - TicketRules.InactiveFor;
            DateTimeOffset? last = null;
            // One request: Zendesk's "assignee updated at" of their 5 latest tickets. Without it (not sent, or refused):
            // each of those tickets' history, as before (up to 6 requests).
            List<DateTimeOffset>? updates = null;
            if (!_assigneeMetricsUnavailable)
            {
                try { updates = await zendesk.GetAssigneeLastUpdatesAsync(userId, ct); }
                catch (HttpRequestException ex) when (ex.StatusCode is System.Net.HttpStatusCode.BadRequest or System.Net.HttpStatusCode.Forbidden
                                                          or System.Net.HttpStatusCode.NotFound)
                {
                    Log.Error("Ticket metrics not readable", ex); // refused for agents: histories from now on (a busy Zendesk isn't this)
                }
                if (updates is null && !_assigneeMetricsUnavailable)
                {
                    _assigneeMetricsUnavailable = true;
                    Log.Info("Colleague activity: no ticket metrics from Zendesk, reading ticket histories instead");
                }
            }
            if (updates is not null)
            {
                var recent = updates.Where(at => at >= since).ToList();
                last = recent.Count > 0 ? recent.Max() : null;
            }
            else
                foreach (var ticket in (await zendesk.GetAssigneeTicketsUpdatedSinceAsync(userId, since, ct)).Take(5))
                    foreach (var audit in await zendesk.GetAuditsSinceAsync(ticket.Id, since, ct, ticket.UpdatedAt))
                        if (audit.AuthorId == userId && (last is null || audit.CreatedAt > last))
                            last = audit.CreatedAt;
            _activity[userId] = (now, last, true);
            return (true, last);
        }
        catch (HttpRequestException ex)
        {
            Log.Error($"Could not read the activity of user {userId}; going by working hours only", ex);
            _activity[userId] = (now, null, false);
            return (false, null);
        }
    }

    /// <summary>
    /// Whether to step in on <paramref name="assignee"/>'s ticket (<see cref="TicketRules.ColleagueAway"/>), their local
    /// time, and the line saying why: "Anna is off work · Tue 22:10 their time · last active 4 h ago" or
    /// "Anna: no activity in Zendesk for 3+ h · Tue 11:10 their time".
    /// </summary>
    private async Task<(bool? Away, DateTime Local, string Why)> ColleagueAwayAsync(long assigneeId, User? assignee, DateTimeOffset now, CancellationToken ct)
    {
        var (outside, local) = OutsideWorkingHours(assignee, now);
        // Unknown time zone: always skipped, whatever they did lately, so their activity isn't read for nothing.
        var (known, last) = outside is null ? (false, null) : await LastActiveAsync(assigneeId, now, ct);
        var away = TicketRules.ColleagueAway(outside, known, last, now);
        var who = Markdown.Escape(assignee?.Name ?? $"User {assigneeId}");
        var why = outside is true
            ? $"{who} is off work · {Clock.DayTime(local)} their time{(last is { } at ? $" · last active {NotificationContent.Ago(now - at).Replace(' ', ' ')}" : "")}"
            : $"{who}: no activity in Zendesk for {TicketRules.InactiveFor.TotalHours:0}+ h · {Clock.DayTime(local)} their time";
        return (away, local, why);
    }

    private bool IsOutsideHours(DateTime local) =>
        TicketRules.IsOutsideHours(local, settings.WorkdayStartHour, settings.WorkdayEndHour, settings.WorkDays);

    /// <summary>Outside my own working hours right now (Notifications → My working hours).</summary>
    private bool IAmOffWork() =>
        TicketRules.IsOutsideHours(DateTime.Now, settings.MyWorkdayStartHour, settings.MyWorkdayEndHour, settings.MyWorkDays);

    /// <summary>How fresh my own activity must be read for "working late" (shorter than colleagues': it's about me now).</summary>
    private static readonly TimeSpan RememberMyActivity = TimeSpan.FromMinutes(5);

    /// <summary>The local date the "Working late?" notice was last shown: once per evening.</summary>
    private DateTime _workingLateNoticeShown;

    /// <summary>
    /// Off work for "only during my working hours": outside my hours, unless I'm working late (Notifications → Working
    /// hours → Also outside them, while I'm active in Zendesk): I changed or commented on a ticket in the last 30 minutes, as for colleagues
    /// (<see cref="TicketRules.ActiveWithin"/>). Only reads my activity when it matters (outside my hours, switched on).
    /// </summary>
    private async Task<bool> IAmOffWorkAsync(CancellationToken ct)
    {
        if (!IAmOffWork())
            return false;
        if (!settings.NotifyWhenWorkingLate || Me is not { } me)
            return true;
        var now = DateTimeOffset.UtcNow;
        if (_activity.TryGetValue(me.Id, out var read) && now - read.ReadAt >= RememberMyActivity)
            _activity.Remove(me.Id);
        var (known, last) = await LastActiveAsync(me.Id, now, ct);
        if (!known || last is not { } at || now - at >= TicketRules.ActiveWithin)
            return true;
        if (_workingLateNoticeShown != DateTime.Today)
        {
            _workingLateNoticeShown = DateTime.Today;
            Log.Info("Working late: active in Zendesk outside my hours, notifying as during them");
            notifier.Show(new NotificationContent(NotificationKind.MyTicket, "Zendesk Watcher", "Working late?",
                "You're active in Zendesk after your hours, so notifications come as during your shift, until you've been " +
                "inactive for 30 minutes (Notifications → Working hours)."));
        }
        return false;
    }

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
        if (settings.OnlyNotifyDuringMyHours && !important && await IAmOffWorkAsync(ct))
            return;

        var me = Me!.Id;
        var all = (await zendesk.GetAuditsSinceAsync(ticket.Id, since, ct, ticket.UpdatedAt)).OrderBy(a => a.CreatedAt).ToList();
        var audits = all.Where(a => a.AuthorId != me);
        // The customer wrote last (no public answer since, also not mine): the follow-up SLA clock runs from then.
        DateTimeOffset? customerWroteAt = null;
        List<string>? customerTexts = null; // what they wrote since the last check, for the priority guess
        long? customerId = null;
        var thankYou = false; // their latest reply is just a thank-you (quiet with the Experimental switch)
        if (all.LastOrDefault(a => a.Events.Any(e => e.Type == "Comment" && e.Public == true)) is { } lastPublic && lastPublic.AuthorId != me
            && TicketRules.IsCustomer(await zendesk.GetUserAsync(lastPublic.AuthorId, ct), ticket, Me?.Email))
        {
            customerWroteAt = lastPublic.CreatedAt;
            customerId = lastPublic.AuthorId;
            thankYou = IsQuietThankYou(lastPublic.Events.First(e => e.Type == "Comment" && e.Public == true));
            if (settings.GuessPriority)
                customerTexts = all.Where(a => a.AuthorId == lastPublic.AuthorId)
                    .SelectMany(a => a.Events.Where(e => e.Type == "Comment" && e.Public == true))
                    .Select(e => TextCleanup.Plain(e.PlainBody ?? e.Body)).TakeLast(3).ToList();
        }

        var lines = new List<string>();
        DateTimeOffset? changedAt = null;
        var logLines = new List<string>(); // never with comment text: the log may be shared when troubleshooting
        foreach (var audit in audits)
        {
            var author = audit.AuthorId == SystemUserId ? "Zendesk" : await zendesk.GetUserNameAsync(audit.AuthorId, ct);
            // Automatic changes (Notifications → Which changes): the auto-reply and routing on a new ticket, triggers.
            var byServiceAccount = !settings.NotifyAutomaticChanges && audit.AuthorId > 0 &&
                                   TicketRules.IsServiceAccount(await zendesk.GetUserAsync(audit.AuthorId, ct)); // cached by the name above
            bool Shown(AuditEvent e) => settings.NotifyAutomaticChanges || !TicketRules.IsAutomaticChange(e, audit, byServiceAccount);
            // A custom status change ("In progress → Escalated") says more than the basic one recorded with it.
            var hasCustomStatus = audit.Events.Any(e => e.Type == "Change" && e.FieldName == "custom_status_id");
            // A reply shown in the same update: a reopen with it goes without saying (TicketRules.IsImpliedReopen).
            var withReply = settings.NotifyPublicReplies && audit.Events.Any(e => e.Type == "Comment" && e.Public == true && Shown(e));
            foreach (var ev in audit.Events)
            {
                if (hasCustomStatus && ev is { Type: "Change", FieldName: "status" })
                    continue;
                if (!Shown(ev))
                    continue;
                if (await DescribeAsync(ev, author, settings.ShowCommentText, ct) is not { } line)
                    continue;
                if (withReply && TicketRules.IsImpliedReopen(StatusTo(ev), withPublicReply: true))
                    continue;
                lines.Add(line);
                changedAt = audit.CreatedAt;
                thankYou &= audit.AuthorId == customerId; // quiet only when the customer's thanks is all there is to say
                logLines.Add(Markdown.Strip(ev.Type == "Comment" ? (await DescribeAsync(ev, author, withText: false, ct))! : line));
            }
        }

        if (lines.Count == 0)
            return;

        Log.Info($"Ticket #{ticket.Id}: {string.Join(" | ", logLines)}{(thankYou ? " (a thank-you: quiet)" : "")}");

        var shown = lines.TakeLast(MaxLinesPerToast).ToList();
        if (lines.Count > shown.Count)
            shown.Insert(0, $"(+{lines.Count - shown.Count} earlier changes)");
        // Who the ticket is from, then what changed; not when a line already names them ("Jane Doe replied: …").
        if (await RequesterNameAsync(ticket, ct) is { } requester && !shown.Any(l => Markdown.Strip(l).Contains(requester)))
            shown.Insert(0, Markdown.Escape(requester));

        Show(await WithClientAsync(new NotificationContent(kind, "Ticket updated", $"#{ticket.Id} {TextCleanup.Plain(ticket.Subject)}",
            string.Join("\n", shown), ticket.Id, ticket.Priority, context)
        { IsTicketNotification = true, ChangedAt = changedAt, EventTime = changedAt, Quiet = thankYou }, ticket, null, ct,
            sla: customerWroteAt is { } wrote ? (wrote, FirstReply: false) : null, customerTexts: customerTexts, waitingSince: customerWroteAt));
    }

    /// <summary>"Group added · Tier 2 · 3 unassigned tickets already waiting" when a group is added; a ticket notification if there's just one.</summary>
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
        Show(new NotificationContent(NotificationKind.NewTicket, "Group added",
            $"{tickets.Count} unassigned tickets already waiting{Markdown.Strip(TicketRules.ScheduledNote(tickets.Count, scheduled))}",
            TicketList(oldestFirst, IsScheduled), Context: GroupLabel(group))
            {
                IsTicketNotification = true, TicketCount = tickets.Count, ScheduledCount = scheduled, MaxBodyLines = ListLines, OneRowPerLine = true,
                Action = NotificationActions.Search(UnassignedQuery(group)) // a click shows them all, not just the first five
            },
            tickets.Select(t => t.Id));
    }

    /// <summary>The Zendesk search for a group's unassigned tickets ("No group": tickets without one).</summary>
    internal static string UnassignedQuery(WatchedGroup group) =>
        $"type:ticket group:{(group.IsNoGroup ? "none" : group.Id.ToString())} assignee:none status<solved";

    /// <summary>Tickets listed in one notification; more become "… and 4 more".</summary>
    private const int ListedTickets = 5;

    /// <summary>Room for the listed tickets and the "… and N more" line: the notification grows to fit them.</summary>
    internal const int ListLines = ListedTickets + 1;

    /// <summary>
    /// "#123 Subject" per line ("#123 **Scheduled** · Subject" for Scheduled ones, so cutting a long subject keeps it), up
    /// to <see cref="ListedTickets"/>, then "… and N more: click to see them all". Shown one row each (<see cref="NotificationContent.OneRowPerLine"/>).
    /// </summary>
    internal static string TicketList(IReadOnlyList<Ticket> tickets, Func<Ticket, bool>? scheduled = null)
    {
        var lines = tickets.Take(ListedTickets)
            .Select(t => $"#{t.Id} {(scheduled?.Invoke(t) == true ? "**Scheduled** \u00b7 " : "")}{Markdown.Escape(TextCleanup.Plain(t.Subject))}").ToList();
        if (tickets.Count > ListedTickets)
            lines.Add($"\u2026 and {tickets.Count - ListedTickets} more: click to see them all");
        return string.Join("\n", lines);
    }

    /// <summary>Day and month names in English, whatever the PC's language (the app's texts are English).</summary>
    private static string English(DateTime value, string format) =>
        value.ToString(format, System.Globalization.CultureInfo.GetCultureInfo("en-GB"));

    /// <summary>Who the ticket is from ("Jane Doe"). Null if it can't be read.</summary>
    private async Task<string?> RequesterNameAsync(Ticket ticket, CancellationToken ct) =>
        ticket.RequesterId is { } id && (await zendesk.GetUserAsync(id, ct))?.Name is { Length: > 0 } name ? name : null;

    /// <summary>The client as the "Options ▾" menu needs it: its current choice (or a Normal one), current name and group.</summary>
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

    /// <summary>A new unassigned ticket with a priority chosen for its group (Notifications → New tickets), or from an important client.</summary>
    private bool WantedPriority(Ticket ticket, WatchedGroup group) => TicketRules.MatchesPriority(ticket.Priority, settings.NewTicketPrioritiesFor(group.Id)) || IsImportant(ticket);

    /// <summary>
    /// Adds the client: "Client: Contoso", ★ and "until dismissed"
    /// for important clients, the client's own sound, and on new tickets and colleagues' tickets the "Options ▾" menu.
    /// </summary>
    /// <param name="sla">Where the SLA clock starts and which target applies (first reply or follow-up); null: no deadline.</param>
    /// <param name="customerTexts">What the customer wrote, oldest first, for the priority guess (only with it on); null: no guess.</param>
    /// <param name="waitingSince">Since when the customer has waited for an answer (the guess counts a long wait).</param>
    private async Task<NotificationContent> WithClientAsync(NotificationContent content, Ticket ticket, WatchedGroup? group, CancellationToken ct,
        (DateTimeOffset Start, bool FirstReply)? sla = null, IReadOnlyList<string>? customerTexts = null, DateTimeOffset? waitingSince = null)
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
            // Tickets read with show_many already carry their timers (include=slas): no extra request then.
            var metric = firstReply ? "first_reply_time" : "next_reply_time";
            var breach = ticket.Slas is { } slas
                ? slas.PolicyMetrics.FirstOrDefault(m => m.Metric == metric && m.Stage == "active" && m.BreachAt is not null)?.BreachAt
                : await zendesk.GetSlaBreachAsync(ticket.Id, metric, ct);
            if (breach is { } at)
                await NoteSlaTypeAsync(slaType, ct, () => _state.Sla.Record(ticket.Id, slaType, ticket.Priority, firstReply, start, at));
            var due = breach ?? Sla.Due(start, slaType, ticket.Priority, firstReply, settings.SlaOverrides, _state.Sla);
            content = content with { SlaDue = due, SlaFirstReply = firstReply };
        }
        if (settings.GuessPriority && customerTexts is not null &&
            PriorityGuess.Guess(ticket.Priority, ticket.Subject, customerTexts, slaType,
                waitingSince is { } since ? DateTimeOffset.UtcNow - since : TimeSpan.Zero, LikeEarlier(ticket)) is { } hint)
        {
            Log.Info($"Ticket #{ticket.Id}: priority guess: {Markdown.Strip(hint.Text)}");
            content = content with { PriorityHint = hint };
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

    /// <summary>"Jane Doe" (the time since, "· 2 min ago", is added when shown); "Created 14:02" when nobody is named.</summary>
    private async Task<string> CreatedLineAsync(Ticket ticket, CancellationToken ct) =>
        await RequesterNameAsync(ticket, ct) is { } requester ? Markdown.Escape(requester) : $"Created {CreatedLabel(ticket)}";

    /// <summary>What someone wrote, as a quote: “Thanks, that worked!”</summary>
    internal static string Quote(string text) => $"“{text}”";

    /// <summary>
    /// Who wrote, on its own line (the notification adds "· 5 min ago" and the SLA deadline to it), then what they wrote
    /// as a quote: "**Jane Doe**\n“Still broken”". Without the text: "**Jane Doe** replied".
    /// </summary>
    /// <summary>
    /// A colleague's ticket: who wrote (the time goes on that line), then why it's shown (off work, last active), then the
    /// message. Only ticket notifications' first lines show, so a long message is what's cut, never the reason.
    /// </summary>
    internal static string WhyUnderName(string said, string why)
    {
        var lineEnd = said.IndexOf('\n');
        return lineEnd < 0 ? $"{said}\n{why}" : $"{said[..lineEnd]}\n{why}\n{said[(lineEnd + 1)..]}";
    }

    private string Said(string name, AuditEvent comment, string label = "", string verb = "replied") =>
        settings.ShowCommentText ? $"**{Markdown.Escape(name)}**{label}\n{Quote(Markdown.Escape(Snippet(comment)))}" : $"**{Markdown.Escape(name)}**{label} {verb}";

    private static string CreatedLabel(Ticket t)
    {
        var local = t.CreatedAt.ToLocalTime();
        return local.Date == DateTime.Today ? Clock.Time(local.DateTime) : $"{English(local.DateTime, "d MMM")} {Clock.Time(local.DateTime)}";
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
                return withText ? $"**{author}**\n{Quote(Markdown.Escape(Snippet(ev)))}" : $"**{author}** replied";

            case "Comment" when ev.Public == false && settings.NotifyInternalNotes:
                return withText ? $"**{author}** · internal note\n{Quote(Markdown.Escape(Snippet(ev)))}" : $"**{author}** added an internal note";

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
                return $"Priority {TicketRules.PriorityWithTag(Text(ev.PreviousValue))} → {TicketRules.PriorityWithTag(Text(ev.Value))} ({author})";

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

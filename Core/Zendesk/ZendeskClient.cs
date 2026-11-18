using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Net;
using System.Text.Json;

namespace ZendeskWatcher.Zendesk;

/// <summary>Thrown when there is no valid token and the user has to sign in again.</summary>
public sealed class AuthRequiredException() : Exception("Sign-in required");

/// <summary>The outcome of <see cref="ZendeskClient.TakeTicketAsync"/>.</summary>
public sealed record TakeResult(bool Taken, string? TakenBy = null);

/// <summary>
/// Zendesk API calls, made with the signed-in agent's own permissions. Everything is read-only except
/// <see cref="TakeTicketAsync"/>, which the user triggers explicitly.
/// </summary>
public sealed class ZendeskClient(Settings settings, IZendeskSession session)
{
    private const int MaxPages = 10;
    private const int MaxRateLimitRetries = 3;

    /// <summary>How often a read that hit a Zendesk server error (502/503/504) is tried again.</summary>
    private const int MaxServerErrorRetries = 2;

    /// <summary>
    /// Until when every request waits (UTC ticks): Zendesk answered "too many requests". Shared by all clients in the
    /// app, so the whole app pauses (the checks, Settings, the menus), not just the request that was limited.
    /// </summary>
    private static long _pausedUntilTicks;

    /// <summary>When the pause after Zendesk's last "too many requests" ends (in the past when there's none).</summary>
    public static DateTimeOffset PausedUntil => new(Interlocked.Read(ref _pausedUntilTicks), TimeSpan.Zero);

    /// <summary>Makes every request wait until <paramref name="until"/> (a later pause already set stays).</summary>
    internal static void PauseUntil(DateTimeOffset until)
    {
        var ticks = until.UtcTicks;
        for (var seen = Interlocked.Read(ref _pausedUntilTicks); ticks > seen;)
        {
            var was = Interlocked.CompareExchange(ref _pausedUntilTicks, ticks, seen);
            if (was == seen)
                return;
            seen = was;
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true
    };

    private readonly Dictionary<long, User?> _users = [];

    /// <summary>Users remembered across checks and restarts (set by the app; tests leave it null).</summary>
    public static UserStore? Users { get; set; }
    private static int _requestCount;

    /// <summary>When the requests of the last minute were sent (all clients), for the log when Zendesk says it's busy.</summary>
    private static readonly Queue<DateTimeOffset> RecentRequests = new();

    /// <summary>
    /// The app's own limit, also without Zendesk's rate-limit headers (the browser session gets none): at most this many
    /// requests in any minute, all clients together. Normal use is 3-4, the busiest real minute ~55 (start with Catch up
    /// and a full Clients load), so it only ever stops a runaway. A request over it waits, it isn't dropped.
    /// </summary>
    public const int LocalLimitPerMinute = 60;

    /// <summary>The limit in use: tests lift it (they send hundreds of requests to a fake Zendesk within seconds).</summary>
    internal static int LocalLimit { get; set; } = LocalLimitPerMinute;

    private static DateTime _localLimitLogged;

    /// <summary>
    /// Takes a place in the last minute's requests (<paramref name="recent"/>, oldest first) if there's room: null.
    /// Otherwise how long until the oldest is a minute old (nothing is taken).
    /// </summary>
    internal static TimeSpan? TakeLocalSlot(Queue<DateTimeOffset> recent, DateTimeOffset now, int limit)
    {
        while (recent.Count > 0 && now - recent.Peek() > TimeSpan.FromMinutes(1))
            recent.Dequeue();
        if (recent.Count < limit)
        {
            recent.Enqueue(now);
            return null;
        }
        return recent.Peek() + TimeSpan.FromMinutes(1) - now + TimeSpan.FromMilliseconds(50);
    }

    /// <summary>Waits while <see cref="LocalLimitPerMinute"/> requests went out in the last minute, then takes a place.</summary>
    private static async Task WaitForLocalLimitAsync(string caller, CancellationToken ct)
    {
        while (true)
        {
            TimeSpan? wait;
            lock (RecentRequests)
                wait = TakeLocalSlot(RecentRequests, DateTimeOffset.UtcNow, LocalLimit);
            if (wait is not { } delay)
                return;
            if (DateTime.UtcNow - _localLimitLogged > TimeSpan.FromMinutes(1))
            {
                _localLimitLogged = DateTime.UtcNow;
                Log.Info($"Local limit: {LocalLimitPerMinute} Zendesk requests in the last minute; {caller} waits {delay.TotalSeconds:0} s " +
                         $"(requests by purpose: {string.Join(", ", RequestsByPurpose.OrderByDescending(r => r.Value).Take(5).Select(r => $"{r.Key} {r.Value}"))})");
            }
            await Task.Delay(delay, ct);
        }
    }

    private static int RequestsInLastMinute()
    {
        lock (RecentRequests)
        {
            while (RecentRequests.Count > 0 && DateTimeOffset.UtcNow - RecentRequests.Peek() > TimeSpan.FromMinutes(1))
                RecentRequests.Dequeue();
            return RecentRequests.Count;
        }
    }

    /// <summary>Zendesk API requests made since the last call (all clients); for the request statistics in the log.</summary>
    public static int TakeRequestCount() => Interlocked.Exchange(ref _requestCount, 0);

    /// <summary>Since the app started: time spent waiting for answers, time converting them, and the text received (for measuring one job).</summary>
    public static (TimeSpan Waiting, TimeSpan Reading, long Chars) Timings =>
        (TimeSpan.FromTicks(Interlocked.Read(ref _waitTicks)), TimeSpan.FromTicks(Interlocked.Read(ref _parseTicks)), Interlocked.Read(ref _bodyChars));
    private static long _waitTicks, _parseTicks, _bodyChars;

    /// <summary>All requests since the app started (never reset): for measuring one job, e.g. Catch up.</summary>
    public static long RequestCount => Interlocked.Read(ref _requestTotal);
    private static long _requestTotal;

    /// <summary>Requests per purpose (the method that made them, e.g. "UnassignedTickets"), for the log.</summary>
    private static readonly ConcurrentDictionary<string, int> RequestsByPurpose = new();

    /// <summary>
    /// What the requests made from here on (in this async flow) are for: "catch up", "colleague activity" …, counted in
    /// <see cref="TakeRequestsByReason"/>. Set at the start of an async method: it's back to before when that returns.
    /// </summary>
    public static readonly AsyncLocal<string?> Reason = new();

    private static readonly ConcurrentDictionary<string, int> RequestsByReason = new();

    /// <summary>"catch up 180, checks 40, …" since the last call, most first; then starts counting again.</summary>
    public static string TakeRequestsByReason()
    {
        var counts = RequestsByReason.ToArray();
        RequestsByReason.Clear();
        return string.Join(", ", counts.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key} {kv.Value}"));
    }

    /// <summary>"UnassignedTickets 40, Audits 22, …" since the last call, most first; then starts counting again.</summary>
    public static string TakeRequestBreakdown()
    {
        var counts = RequestsByPurpose.ToArray();
        RequestsByPurpose.Clear();
        static string Name(string method) => method.Replace("Async", "").Replace("Get", "");
        return string.Join(", ", counts.OrderByDescending(kv => kv.Value).Select(kv => $"{Name(kv.Key)} {kv.Value}"));
    }
    private readonly HashSet<string> _unsupportedLists = [];

    public async Task<User> GetMeAsync(CancellationToken ct) =>
        (await GetAsync<UserResponse>("/api/v2/users/me.json", ct)).User
        ?? throw new InvalidOperationException("Could not read the signed-in user.");

    public async Task<List<Group>> GetGroupsAsync(CancellationToken ct)
    {
        try
        {
            return await GetAllPagesAsync<GroupsResponse, Group>("/api/v2/groups.json", r => r.Groups, ct);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.Forbidden)
        {
            // Some agent roles may only list the groups they can assign to.
            return await GetAllPagesAsync<GroupsResponse, Group>("/api/v2/groups/assignable.json", r => r.Groups, ct);
        }
    }

    /// <summary>The groups the user is a member of (used to suggest groups to monitor).</summary>
    public async Task<List<long>> GetMyGroupIdsAsync(long userId, CancellationToken ct)
    {
        try
        {
            return (await GetAllPagesAsync<GroupMembershipsResponse, GroupMembership>($"/api/v2/users/{userId}/group_memberships.json", r => r.GroupMemberships, ct))
                .Select(m => m.GroupId).ToList();
        }
        catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
        {
            return (await GetAllPagesAsync<GroupsResponse, Group>($"/api/v2/users/{userId}/groups.json", r => r.Groups, ct))
                .Select(g => g.Id).ToList();
        }
    }

    /// <summary>The members of a group (agents), with name and time zone. Used to pick colleagues for "Help colleagues".</summary>
    public async Task<List<User>> GetGroupMembersAsync(long groupId, CancellationToken ct) =>
        await GetAllPagesAsync<UsersResponse, User>($"/api/v2/groups/{groupId}/users.json?per_page=100", r => r.Users, ct);

    /// <summary>
    /// Open, unassigned tickets in a group, or with no group at all for <see cref="WatchedGroup.NoGroupId"/>.
    /// Uses the search index, which can lag 1-3 minutes.
    /// </summary>
    public Task<List<Ticket>> GetUnassignedTicketsAsync(long groupId, CancellationToken ct)
    {
        var group = groupId == WatchedGroup.NoGroupId ? "none" : groupId.ToString();
        return SearchTicketsAsync($"type:ticket group:{group} assignee:none status<solved", ct);
    }

    /// <summary>All open tickets assigned to the signed-in agent (used once to build a baseline).</summary>
    public Task<List<Ticket>> GetMyOpenTicketsAsync(CancellationToken ct) =>
        SearchTicketsAsync("type:ticket assignee:me status<solved", ct);

    /// <summary>
    /// The most recently updated tickets assigned to a user. Exact, no search lag. 30 is plenty between two checks
    /// (only a few change per minute) and keeps the response small: every ticket includes its full description.
    /// </summary>
    public async Task<List<Ticket>> GetRecentlyUpdatedAssignedAsync(long userId, CancellationToken ct) =>
        (await GetAsync<TicketsResponse>($"/api/v2/users/{userId}/tickets/assigned.json?sort_by=updated_at&sort_order=desc&per_page=30", ct)).Tickets;

    /// <summary>The last <see cref="GetRecentlyUpdatedInvolvedAsync"/> got a full page of a list: there may be more.</summary>
    public bool LastInvolvedListFull { get; private set; }

    /// <summary>
    /// The most recently updated tickets where the user is CC'd, or follows. Exact, no search lag.
    /// Returns an empty list if the account doesn't support the list (logged once).
    /// </summary>
    public async Task<List<Ticket>> GetRecentlyUpdatedInvolvedAsync(long userId, CancellationToken ct)
    {
        LastInvolvedListFull = false;
        var result = new List<Ticket>();
        foreach (var list in new[] { "ccd", "followed" })
        {
            if (_unsupportedLists.Contains(list))
                continue;
            try
            {
                var tickets = (await GetAsync<TicketsResponse>($"/api/v2/users/{userId}/tickets/{list}.json?sort_by=updated_at&sort_order=desc&per_page=30", ct)).Tickets;
                LastInvolvedListFull |= tickets.Count >= 30;
                result.AddRange(tickets);
            }
            catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden)
            {
                Log.Error($"Ticket list '{list}' is not available; skipping it", ex);
                _unsupportedLists.Add(list);
            }
        }
        return result.DistinctBy(t => t.Id).ToList();
    }

    /// <summary>
    /// Assigns an unassigned ticket to the user. If someone else got it first (or it changed in between),
    /// nothing is changed and the current assignee is returned.
    /// </summary>
    public async Task<TakeResult> TakeTicketAsync(long ticketId, long userId, CancellationToken ct)
    {
        var ticket = (await GetAsync<TicketResponse>($"/api/v2/tickets/{ticketId}.json", ct)).Ticket
            ?? throw new InvalidOperationException($"Ticket #{ticketId} not found.");
        if (ticket.AssigneeId == userId)
            return new TakeResult(true);
        if (ticket.AssigneeId is { } other)
            return new TakeResult(false, await GetUserNameAsync(other, ct));

        // safe_update: Zendesk rejects the change (409) if the ticket was updated after we read it.
        var body = JsonSerializer.Serialize(new
        {
            ticket = new { assignee_id = userId, safe_update = true, updated_stamp = ticket.UpdatedAt.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ") }
        });
        try
        {
            await RequestAsync<TicketResponse>($"/api/v2/tickets/{ticketId}.json", "PUT", body, ct);
            return new TakeResult(true);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.Conflict)
        {
            var now = (await GetAsync<TicketResponse>($"/api/v2/tickets/{ticketId}.json", ct)).Ticket;
            if (now?.AssigneeId == userId)
                return new TakeResult(true);
            if (now?.AssigneeId is { } taker)
                return new TakeResult(false, await GetUserNameAsync(taker, ct));
            throw new InvalidOperationException("The ticket changed while taking it. Try again.");
        }
    }

    /// <summary>
    /// Current state of specific tickets, with Zendesk's SLA timers (they teach the app the SLA targets). Tickets the
    /// agent can no longer see are simply missing.
    /// </summary>
    public async Task<List<Ticket>> GetTicketsAsync(IEnumerable<long> ids, CancellationToken ct)
    {
        var result = new List<Ticket>();
        foreach (var chunk in ids.Chunk(100))
        {
            var response = await GetAsync<TicketsResponse>($"/api/v2/tickets/show_many.json?ids={string.Join(',', chunk)}&include=slas", ct);
            result.AddRange(response.Tickets);
        }
        return result;
    }

    /// <summary>
    /// The audits (changes) of a ticket made after <paramref name="since"/>. Asks for newest first and stops at the
    /// first page that reaches already-seen changes, instead of downloading the whole history (with every comment)
    /// for each change. If Zendesk returns oldest first anyway, it reads all pages like before.
    /// </summary>
    /// <param name="ticketUpdatedAt">
    /// The ticket's "updated at" as just read, if known: then a history read earlier is reused while the ticket hasn't
    /// changed since (any comment or change moves it), so it's never out of date. In memory, for this connection.
    /// </param>
    public async Task<List<Audit>> GetAuditsSinceAsync(long ticketId, DateTimeOffset since, CancellationToken ct, DateTimeOffset? ticketUpdatedAt = null)
    {
        if (ticketUpdatedAt == default(DateTimeOffset))
            ticketUpdatedAt = null; // not sent: can't tell whether it changed
        if (ticketUpdatedAt is { } updated)
        {
            lock (AuditCache)
                if (AuditCache.TryGetValue(ticketId, out var known) && known.UpdatedAt == updated && known.Since <= since)
                    return [.. known.Audits.Where(a => a.CreatedAt > since)];
        }
        var result = await ReadAuditsSinceAsync(ticketId, since, ct);
        if (ticketUpdatedAt is { } read)
            lock (AuditCache)
            {
                if (AuditCache.Count > 2000)
                    AuditCache.Clear(); // a long-running app: old ones are long stale anyway
                AuditCache[ticketId] = (read, since, result);
            }
        return result;
    }

    /// <summary>Ticket id → its history as read: for which "updated at" and from when.</summary>
    private readonly Dictionary<long, (DateTimeOffset UpdatedAt, DateTimeOffset Since, List<Audit> Audits)> AuditCache = [];

    private async Task<List<Audit>> ReadAuditsSinceAsync(long ticketId, DateTimeOffset since, CancellationToken ct)
    {
        var result = new List<Audit>();
        string? next = $"/api/v2/tickets/{ticketId}/audits.json?sort_order=desc&include=users";
        for (var page = 0; next is not null && page < MaxPages; page++)
        {
            var response = await GetAsync<AuditsResponse>(next, ct, "GetAuditsSinceAsync"); // counted as before
            foreach (var author in response.Users ?? [])
                RememberUser(author); // the authors came along: looking them up later costs nothing
            var audits = response.Audits;
            result.AddRange(audits.Where(a => a.CreatedAt > since));

            var newestFirst = audits.Count < 2 || audits[0].CreatedAt >= audits[^1].CreatedAt;
            if (newestFirst && audits.Count > 0 && audits[^1].CreatedAt <= since)
                break;
            next = response.Next;
        }
        return result;
    }

    /// <summary>
    /// A ticket's comments (public replies and notes) after <paramref name="since"/>, as audits with one Comment event
    /// each, so the same rules read them. Much less than the full history (no status changes, trigger actions or
    /// metadata): for Catch up, which only asks who wrote last. Newest first, 20 per page, stopping once past
    /// <paramref name="since"/>.
    /// </summary>
    public async Task<List<Audit>> GetCommentsSinceAsync(long ticketId, DateTimeOffset since, CancellationToken ct)
    {
        var result = new List<Audit>();
        string? next = $"/api/v2/tickets/{ticketId}/comments.json?sort_order=desc&include=users&per_page=20";
        for (var page = 0; next is not null && page < MaxPages; page++)
        {
            var response = await GetAsync<CommentsResponse>(next, ct);
            foreach (var author in response.Users ?? [])
                RememberUser(author);
            var comments = response.Comments;
            result.AddRange(comments.Where(c => c.CreatedAt > since).Select(c => new Audit
            {
                Id = c.Id, AuthorId = c.AuthorId, CreatedAt = c.CreatedAt,
                Events = [new AuditEvent { Type = "Comment", Public = c.Public, PlainBody = c.PlainBody, Body = c.Body }]
            }));
            var newestFirst = comments.Count < 2 || comments[0].CreatedAt >= comments[^1].CreatedAt;
            if (newestFirst && comments.Count > 0 && comments[^1].CreatedAt <= since)
                break;
            next = response.Next;
        }
        return result;
    }

    private readonly Dictionary<long, Organization?> _organizations = [];

    /// <summary>The account's custom ticket statuses ("In progress", "Escalated", …); empty if it doesn't use them.</summary>
    public async Task<List<CustomStatus>> GetCustomStatusesAsync(CancellationToken ct)
    {
        try
        {
            return (await GetAsync<CustomStatusesResponse>("/api/v2/custom_statuses.json", ct)).CustomStatuses;
        }
        catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden)
        {
            Log.Info("Custom ticket statuses are not available; using the basic ones");
            return [];
        }
    }

    /// <summary>
    /// When Zendesk's own SLA timer for this metric ("first_reply_time", "next_reply_time") breaches, if it's running.
    /// Agents can read the timers but not the policies. Null without a running timer, or if it can't be read.
    /// </summary>
    public async Task<DateTimeOffset?> GetSlaBreachAsync(long ticketId, string metric, CancellationToken ct)
    {
        try
        {
            var response = await GetAsync<TicketsResponse>($"/api/v2/tickets/show_many.json?ids={ticketId}&include=slas", ct);
            return response.Tickets.FirstOrDefault()?.Slas?.PolicyMetrics
                .FirstOrDefault(m => m.Metric == metric && m.Stage == "active" && m.BreachAt is not null)?.BreachAt;
        }
        catch (HttpRequestException)
        {
            return null; // the team target is used instead
        }
    }

    /// <summary>
    /// The SLA types' names as Zendesk shows them (value → name, from the organization field "sla_type"); empty if they
    /// can't be read. Never asks to sign in again: an agent may not be allowed to read organization fields.
    /// </summary>
    public async Task<Dictionary<string, string>> GetSlaTypeNamesAsync(CancellationToken ct)
    {
        try
        {
            var field = (await GetOrganizationFieldsAsync(ct)).FirstOrDefault(f => f.Key == "sla_type");
            return field?.CustomFieldOptions?.Where(o => !string.IsNullOrEmpty(o.Value) && !string.IsNullOrEmpty(o.Name))
                .ToDictionary(o => o.Value!.ToLowerInvariant(), o => o.Name!) ?? [];
        }
        catch (Exception ex) when (ex is HttpRequestException or AuthRequiredException)
        {
            return [];
        }
    }

    /// <summary>An organization with its fields (cached for this session). Null if it can't be read.</summary>
    public async Task<Organization?> GetOrganizationAsync(long organizationId, CancellationToken ct)
    {
        if (_organizations.TryGetValue(organizationId, out var cached))
            return cached;
        Organization? org;
        try
        {
            org = (await GetAsync<OrganizationResponse>($"/api/v2/organizations/{organizationId}.json", ct)).Organization;
        }
        catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden)
        {
            org = null; // gone or not allowed: that won't change, remember it
        }
        catch (HttpRequestException)
        {
            return null; // e.g. the session expired mid-check: try again next time (its name and SLA type matter)
        }
        _organizations[organizationId] = org;
        return org;
    }

    /// <summary>The account's active organization fields (for the client label on the Clients page); empty if none or not allowed.</summary>
    public async Task<List<OrganizationField>> GetOrganizationFieldsAsync(CancellationToken ct)
    {
        try
        {
            return (await GetAllPagesAsync<OrganizationFieldsResponse, OrganizationField>("/api/v2/organization_fields.json", r => r.OrganizationFields, ct))
                .Where(f => f.Active && f.Key.Length > 0).ToList();
        }
        catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden)
        {
            return [];
        }
    }

    /// <summary>How far back <see cref="GetGroupClientsAsync"/> looks for a group's clients.</summary>
    public const int ClientLookBackDays = 90;

    /// <summary>
    /// The clients (organizations) of a group's tickets created in the last <see cref="ClientLookBackDays"/> days, with
    /// how many tickets each had, most first. Zendesk doesn't tie organizations to groups, so the tickets tell.
    /// <see cref="ClientChoice.NoClientId"/> stands for tickets without one. Reads them all with search export (1,000 per
    /// request; the normal search stopped at 500, so busy groups' lists were incomplete). With each client's products
    /// (<see cref="Organization.Products"/>, from the same request as the names), and who the tickets are assigned to
    /// (<see cref="ClientHandler"/>, most first; names from the user cache, the unknown ones looked up 100 at a time).
    /// </summary>
    public async Task<List<(long Id, string Name, int Tickets, List<string> Products, List<ClientHandler> Handlers)>> GetGroupClientsAsync(long groupId, CancellationToken ct) =>
        (await GetGroupClientsAsync(groupId, null, null, ct)).Clients;

    /// <summary>
    /// A group's tickets of the last 90 days as records (no content): with <paramref name="previous"/> (at most
    /// <see cref="DigestMaxAge"/> old) only those updated since then (minus 10 minutes: search lags a little), merged in;
    /// those older than the look-back dropped. Read: how many tickets came from Zendesk.
    /// </summary>
    public async Task<(GroupDigest Digest, int Read, bool Incremental)> ReadGroupDigestAsync(long groupId, GroupDigest? previous, CancellationToken ct)
    {
        var group = groupId == WatchedGroup.NoGroupId ? "none" : groupId.ToString();
        var readAt = DateTimeOffset.UtcNow;
        var cutoff = readAt.AddDays(-ClientLookBackDays);
        var incremental = previous is not null && readAt - previous.ReadAt <= DigestMaxAge;
        var read = incremental
            ? await ExportTicketsAsync($"group:{group} updated>{previous!.ReadAt.AddMinutes(-10).UtcDateTime:yyyy-MM-ddTHH:mm:ssZ}", ct)
            : await ExportTicketsAsync($"group:{group} created>{cutoff.UtcDateTime:yyyy-MM-dd}", ct);
        var byId = incremental ? previous!.Tickets.ToDictionary(t => t.Id) : [];
        foreach (var t in read)
            byId[t.Id] = new TicketDigest(t.Id, t.OrganizationId, t.AssigneeId, t.CreatedAt);
        return (new GroupDigest(readAt, byId.Values.Where(t => t.CreatedAt > cutoff).ToList()), read.Count, incremental);
    }

    /// <summary>
    /// A full read is due when the last one is older than this. Reading what changed is never more than a full read
    /// (people open Clients seldom, maybe once a month), and past the look-back nothing of the old read is left anyway.
    /// </summary>
    public static readonly TimeSpan DigestMaxAge = TimeSpan.FromDays(ClientLookBackDays);

    /// <summary>
    /// The same, reading only what changed: with <paramref name="previous"/> (the group's tickets as last read, at most
    /// <see cref="DigestMaxAge"/> old) only the tickets updated since then (minus 10 minutes: search lags a little) are
    /// read, usually 1 request instead of several pages, merged in, and those older than the look-back dropped. New
    /// tickets and reassignments are updates, so they come along. Returns the new digest for next time.
    /// <paramref name="known"/>: clients' names and products as last loaded, so only new clients are looked up (Reload
    /// passes none: everything fresh).
    /// </summary>
    public async Task<(List<(long Id, string Name, int Tickets, List<string> Products, List<ClientHandler> Handlers)> Clients, GroupDigest Digest)>
        GetGroupClientsAsync(long groupId, GroupDigest? previous, IReadOnlyDictionary<long, (string Name, List<string> Products)>? known, CancellationToken ct)
    {
        known ??= new Dictionary<long, (string, List<string>)>();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var (digest, read, incremental) = await ReadGroupDigestAsync(groupId, previous, ct);
        var tickets = digest.Tickets;
        var readAt = digest.ReadAt;
        var exported = clock.Elapsed;
        var counts = tickets.GroupBy(t => t.OrganizationId ?? ClientChoice.NoClientId).ToDictionary(g => g.Key, g => g.Count());
        // Per client: assignee -> tickets (the current assignee; unassigned ones don't count).
        var assigned = tickets.Where(t => t.AssigneeId is not null).GroupBy(t => t.OrganizationId ?? ClientChoice.NoClientId)
            .ToDictionary(g => g.Key, g => g.GroupBy(t => t.AssigneeId!.Value).ToDictionary(a => a.Key, a => a.Count()));
        var assignees = assigned.Values.SelectMany(a => a.Keys).Distinct().ToList();
        var agentsFrom = clock.Elapsed;
        await PrefetchUsersAsync(assignees, ct);
        var names = new Dictionary<long, string>();
        foreach (var id in assignees)
            names[id] = await GetUserNameAsync(id, ct); // cached by now
        var agentsTime = clock.Elapsed - agentsFrom;

        // Names in batches of 100 (show_many); cached with the ones notifications use.
        var unknown = counts.Keys.Where(id => id != ClientChoice.NoClientId && !_organizations.ContainsKey(id) && !known.ContainsKey(id)).ToList();
        var namesFrom = clock.Elapsed;
        foreach (var chunk in unknown.Chunk(100))
        {
            try
            {
                var orgs = (await GetAsync<OrganizationsResponse>($"/api/v2/organizations/show_many.json?ids={string.Join(",", chunk)}", ct)).Organizations;
                foreach (var org in orgs)
                    _organizations[org.Id] = org;
            }
            catch (HttpRequestException ex)
            {
                Log.Error("Could not read organization names", ex);
            }
        }
        // Where the time goes (a big group takes ~40 s): the export pages, the client names, the agents' names.
        Log.Info($"Clients of group {groupId}: {tickets.Count} tickets, " +
                 (incremental ? $"{read} updated since {previous!.ReadAt.ToLocalTime():g} read" : $"all read in {Math.Max(1, (read + 999) / 1000)} export page(s)") +
                 $" {exported.TotalSeconds:0.0} s · " +
                 $"{counts.Count} clients, {unknown.Count} names looked up ({(unknown.Count + 99) / 100} request(s)) {(clock.Elapsed - namesFrom).TotalSeconds:0.0} s · " +
                 $"{assignees.Count} agents {agentsTime.TotalSeconds:0.0} s · total {clock.Elapsed.TotalSeconds:0.0} s");

        var clients = counts
            .Select(kv => (Id: kv.Key, Name: kv.Key == ClientChoice.NoClientId ? ClientChoice.NoClientName
                : _organizations.GetValueOrDefault(kv.Key)?.Name is { Length: > 0 } name ? name
                : known.TryGetValue(kv.Key, out var was) && was.Name.Length > 0 ? was.Name : $"Organization {kv.Key}", Tickets: kv.Value,
                Products: _organizations.GetValueOrDefault(kv.Key)?.Products ?? (known.TryGetValue(kv.Key, out var had) ? had.Products : []),
                Handlers: assigned.GetValueOrDefault(kv.Key) is { } by
                    ? by.Select(a => new ClientHandler(a.Key, names[a.Key], a.Value)).OrderByDescending(h => h.Tickets)
                        .ThenBy(h => h.Name, StringComparer.CurrentCultureIgnoreCase).ToList()
                    : []))
            .OrderByDescending(c => c.Tickets).ThenBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        return (clients, new GroupDigest(readAt, tickets));
    }

    /// <summary>
    /// A client's tickets in a group created in the last <see cref="ClientLookBackDays"/> days, any status (search
    /// export, 1,000 per request): who handled them, for "handled 12 of their tickets" in Take it ▾.
    /// </summary>
    public Task<List<Ticket>> GetClientTicketsInGroupAsync(long groupId, long organizationId, CancellationToken ct) =>
        ExportTicketsAsync($"group:{groupId} organization:{organizationId} created>{DateTime.UtcNow.AddDays(-ClientLookBackDays):yyyy-MM-dd}", ct);

    /// <summary>A group's tickets created in the last <paramref name="days"/> days, any status (up to 500, newest first).</summary>
    public Task<List<Ticket>> GetRecentGroupTicketsAsync(long groupId, int days, CancellationToken ct)
    {
        var group = groupId == WatchedGroup.NoGroupId ? "none" : groupId.ToString();
        var query = $"type:ticket group:{group} created>{DateTime.UtcNow.AddDays(-days):yyyy-MM-dd}";
        return GetAllPagesAsync<SearchResponse, Ticket>($"/api/v2/search.json?query={Uri.EscapeDataString(query)}&per_page=100&sort_by=created_at&sort_order=desc",
            r => r.Results, ct, maxPages: 5);
    }

    public async Task<string> GetUserNameAsync(long userId, CancellationToken ct) =>
        (await GetUserAsync(userId, ct))?.Name ?? $"User {userId}";

    /// <summary>A user's name, role and time zone (cached for this session). Null if it can't be read.</summary>
    public async Task<User?> GetUserAsync(long userId, CancellationToken ct)
    {
        if (_users.TryGetValue(userId, out var cached))
            return cached;
        if (Users is { } store && store.Subdomain == settings.Subdomain && store.TryGet(userId, out var remembered))
            return _users[userId] = remembered;

        User? user;
        try
        {
            user = (await GetAsync<UserResponse>($"/api/v2/users/{userId}.json", ct)).User;
        }
        catch (HttpRequestException)
        {
            user = null;
        }

        _users[userId] = user;
        if (user is not null)
            RememberUser(user);
        return user;
    }

    private void RememberUser(User user)
    {
        _users[user.Id] = user;
        if (Users is { } users && users.Subdomain == settings.Subdomain)
            users.Remember(user);
    }

    /// <summary>
    /// Looks up the users not known yet, 100 per request (instead of one request each), so the
    /// <see cref="GetUserAsync"/> calls that follow cost nothing. Fails quietly: those then look them up one by one.
    /// </summary>
    public async Task PrefetchUsersAsync(IEnumerable<long> userIds, CancellationToken ct)
    {
        var unknown = userIds.Distinct()
            .Where(id => !_users.ContainsKey(id) && !(Users is { } store && store.Subdomain == settings.Subdomain && store.TryGet(id, out _)))
            .ToList();
        foreach (var chunk in unknown.Chunk(100))
        {
            try
            {
                foreach (var user in (await GetAsync<UsersResponse>($"/api/v2/users/show_many.json?ids={string.Join(",", chunk)}", ct)).Users)
                    RememberUser(user);
            }
            catch (HttpRequestException ex)
            {
                Log.Error("Could not look up users together; one by one instead", ex);
                return;
            }
        }
    }

    /// <summary>
    /// Open tickets in a group, assigned to someone, updated after <paramref name="since"/>. Uses the search index,
    /// which can lag 1-3 minutes, so callers should look back a little further than their last check.
    /// </summary>
    public Task<List<Ticket>> GetAssignedOpenTicketsUpdatedSinceAsync(long groupId, DateTimeOffset since, CancellationToken ct) =>
        SearchTicketsAsync($"type:ticket group:{groupId} -assignee:none status<solved updated>{since.UtcDateTime:yyyy-MM-ddTHH:mm:ssZ}", ct);

    /// <summary>
    /// An agent's tickets (any status) updated since <paramref name="since"/>, newest first, up to 10: where to look for
    /// what they did lately (Colleagues: are they active in Zendesk?).
    /// </summary>
    public async Task<List<Ticket>> GetAssigneeTicketsUpdatedSinceAsync(long userId, DateTimeOffset since, CancellationToken ct)
    {
        var query = $"type:ticket assignee:{userId} updated>{since.UtcDateTime:yyyy-MM-ddTHH:mm:ssZ}";
        return (await GetAsync<SearchResponse>($"/api/v2/search.json?query={Uri.EscapeDataString(query)}&sort_by=updated_at&sort_order=desc&per_page=10", ct)).Results;
    }

    /// <summary>
    /// When an agent last changed or commented on one of their 5 most recently updated tickets, from Zendesk's ticket
    /// metrics ("assignee updated at"): one request instead of reading each ticket's history. Null when Zendesk doesn't
    /// send the metrics (then the caller reads the histories); no tickets: an empty list.
    /// </summary>
    public async Task<List<DateTimeOffset>?> GetAssigneeLastUpdatesAsync(long userId, CancellationToken ct)
    {
        var response = await GetAsync<TicketsWithMetricsResponse>(
            $"/api/v2/users/{userId}/tickets/assigned.json?sort_by=updated_at&sort_order=desc&per_page=5&include=metric_sets", ct);
        if (response.Tickets.Count == 0)
            return [];
        // Only trusted newest first: in another order the 5 could be old ones, and the colleague would look inactive.
        if (response.Tickets.Zip(response.Tickets.Skip(1)).Any(p => p.First.UpdatedAt < p.Second.UpdatedAt))
            return null;
        return response.MetricSets is { Count: > 0 } metrics
            ? [.. metrics.Select(m => m.AssigneeUpdatedAt).OfType<DateTimeOffset>()]
            : null;
    }

    /// <summary>All open tickets in a group that are assigned to someone (Insights → Team workload, and who could take a ticket).</summary>
    public Task<List<Ticket>> GetAssignedOpenTicketsAsync(long groupId, CancellationToken ct) =>
        ExportTicketsAsync($"group:{groupId} -assignee:none status<solved", ct);

    /// <summary>All open tickets assigned to an agent, in any group (who could take a ticket: their whole workload).</summary>
    public Task<List<Ticket>> GetOpenTicketsAssignedToAsync(long userId, CancellationToken ct) =>
        ExportTicketsAsync($"assignee:{userId} status<solved", ct);

    /// <summary>
    /// Search export: up to 1,000 tickets per request instead of the normal search's 100 (and not capped at 1,000 in
    /// all), for searches that return many tickets. Same index and lag as the normal search; allowed for agents.
    /// </summary>
    private async Task<List<Ticket>> ExportTicketsAsync(string query, CancellationToken ct, [CallerMemberName] string caller = "") =>
        await GetAllPagesAsync<SearchResponse, Ticket>(
            $"/api/v2/search/export.json?query={Uri.EscapeDataString(query)}&filter%5Btype%5D=ticket&page%5Bsize%5D=1000", r => r.Results, ct, caller: caller);

    /// <summary>
    /// Tickets in a group created after <paramref name="since"/> that have an assignee now (any status): the ones someone
    /// took quickly. Uses the search index, which lags 1-3 minutes.
    /// </summary>
    public Task<List<Ticket>> GetAssignedTicketsCreatedSinceAsync(long groupId, DateTimeOffset since, CancellationToken ct) =>
        SearchTicketsAsync($"type:ticket group:{groupId} -assignee:none created>{since.UtcDateTime:yyyy-MM-ddTHH:mm:ssZ}", ct);

    private async Task<List<Ticket>> SearchTicketsAsync(string query, CancellationToken ct, [CallerMemberName] string caller = "") =>
        await GetAllPagesAsync<SearchResponse, Ticket>($"/api/v2/search.json?query={Uri.EscapeDataString(query)}&per_page=100", r => r.Results, ct, caller: caller);

    private async Task<List<TItem>> GetAllPagesAsync<TResponse, TItem>(string path, Func<TResponse, List<TItem>> items, CancellationToken ct, int maxPages = MaxPages,
        [CallerMemberName] string caller = "")
        where TResponse : PagedResponse
    {
        var result = new List<TItem>();
        string? next = path;

        for (var page = 0; next is not null && page < maxPages; page++)
        {
            var response = await GetAsync<TResponse>(next, ct, caller);
            result.AddRange(items(response));
            next = response.Next;
        }

        return result;
    }

    private Task<T> GetAsync<T>(string pathOrUrl, CancellationToken ct, [CallerMemberName] string caller = "") =>
        RequestAsync<T>(pathOrUrl, null, null, ct, caller);

    private async Task<T> RequestAsync<T>(string pathOrUrl, string? method, string? jsonBody, CancellationToken ct, [CallerMemberName] string caller = "")
    {
        var url = pathOrUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? pathOrUrl : settings.BaseUrl + pathOrUrl;
        var recovered = false;

        for (var attempt = 0; ; attempt++)
        {
            // Zendesk said it's busy (here or in another request): nothing is sent until its wait time is over.
            if (PausedUntil - DateTimeOffset.UtcNow is { Ticks: > 0 } pause)
                await Task.Delay(pause, ct);
            await WaitForLocalLimitAsync(caller, ct); // also notes this request in the last minute's
            Interlocked.Increment(ref _requestCount);
            Interlocked.Increment(ref _requestTotal);
            RequestsByPurpose.AddOrUpdate(caller, 1, (_, n) => n + 1);
            RequestsByReason.AddOrUpdate(Reason.Value ?? "other", 1, (_, n) => n + 1);
            var waited = System.Diagnostics.Stopwatch.StartNew();
            var response = method is null
                ? await session.GetAsync(url, ct)
                : await session.SendAsync(method, url, jsonBody ?? "", ct);
            Interlocked.Add(ref _waitTicks, waited.Elapsed.Ticks);
            Interlocked.Add(ref _bodyChars, response.Body.Length);

            if (response.Status == (int)HttpStatusCode.Unauthorized)
            {
                if (!recovered && await session.TryRecoverAsync(ct))
                {
                    recovered = true;
                    continue;
                }
                throw new AuthRequiredException();
            }

            if (response.Status == (int)HttpStatusCode.TooManyRequests && attempt < MaxRateLimitRetries)
            {
                var wait = TimeSpan.FromSeconds(Math.Clamp((response.RetryAfter ?? TimeSpan.FromSeconds(30)).TotalSeconds, 1, 120));
                Log.Info($"Zendesk is busy (rate limited, {caller}): all requests pause for {wait.TotalSeconds:0} s · " +
                         $"{RequestsInLastMinute()} requests in the last minute · {response.LimitInfo ?? "no limit headers"} · {Truncate(response.Body, 200)}");
                PauseUntil(DateTimeOffset.UtcNow + wait);
                continue; // waits at the top of the loop, like every other request
            }

            // Zendesk's own hiccup ("503 Database timeout" deep in a big export, 502/504): a read is tried again after
            // 3 s, then 6 s, instead of failing the whole list. Changes (assigning) are never repeated.
            if (method is null && response.Status is 502 or 503 or 504 && attempt < MaxServerErrorRetries)
            {
                var wait = TimeSpan.FromSeconds(3 * (attempt + 1));
                Log.Info($"Zendesk had a hiccup ({response.Status}, {caller}): trying again in {wait.TotalSeconds:0} s · {Truncate(response.Body, 120)}");
                await Task.Delay(wait, ct);
                continue;
            }

            if (!response.IsSuccess)
            {
                throw new HttpRequestException($"{method ?? "GET"} {url} failed: {response.Status} {Truncate(response.Body, 300)}", null, (HttpStatusCode)response.Status);
            }

            var parsing = System.Diagnostics.Stopwatch.StartNew();
            var parsed = JsonSerializer.Deserialize<T>(response.Body, JsonOptions)
                ?? throw new InvalidOperationException($"Empty response from {url}");
            Interlocked.Add(ref _parseTicks, parsing.Elapsed.Ticks);
            return parsed;
        }
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max] + "...";
}

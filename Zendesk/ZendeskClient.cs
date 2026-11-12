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

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true
    };

    private readonly Dictionary<long, string> _userNames = [];
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

    /// <summary>The most recently updated tickets assigned to a user. Exact, no search lag.</summary>
    public async Task<List<Ticket>> GetRecentlyUpdatedAssignedAsync(long userId, CancellationToken ct) =>
        (await GetAsync<TicketsResponse>($"/api/v2/users/{userId}/tickets/assigned.json?sort_by=updated_at&sort_order=desc&per_page=100", ct)).Tickets;

    /// <summary>
    /// The most recently updated tickets where the user is CC'd, or follows. Exact, no search lag.
    /// Returns an empty list if the account doesn't support the list (logged once).
    /// </summary>
    public async Task<List<Ticket>> GetRecentlyUpdatedInvolvedAsync(long userId, CancellationToken ct)
    {
        var result = new List<Ticket>();
        foreach (var list in new[] { "ccd", "followed" })
        {
            if (_unsupportedLists.Contains(list))
                continue;
            try
            {
                result.AddRange((await GetAsync<TicketsResponse>($"/api/v2/users/{userId}/tickets/{list}.json?sort_by=updated_at&sort_order=desc&per_page=100", ct)).Tickets);
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

    /// <summary>Current state of specific tickets. Tickets the agent can no longer see are simply missing.</summary>
    public async Task<List<Ticket>> GetTicketsAsync(IEnumerable<long> ids, CancellationToken ct)
    {
        var result = new List<Ticket>();
        foreach (var chunk in ids.Chunk(100))
        {
            var response = await GetAsync<TicketsResponse>($"/api/v2/tickets/show_many.json?ids={string.Join(',', chunk)}", ct);
            result.AddRange(response.Tickets);
        }
        return result;
    }

    /// <summary>
    /// The audits (changes) of a ticket made after <paramref name="since"/>. Asks for newest first and stops at the
    /// first page that reaches already-seen changes, instead of downloading the whole history (with every comment)
    /// for each change. If Zendesk returns oldest first anyway, it reads all pages like before.
    /// </summary>
    public async Task<List<Audit>> GetAuditsSinceAsync(long ticketId, DateTimeOffset since, CancellationToken ct)
    {
        var result = new List<Audit>();
        string? next = $"/api/v2/tickets/{ticketId}/audits.json?sort_order=desc";
        for (var page = 0; next is not null && page < MaxPages; page++)
        {
            var response = await GetAsync<AuditsResponse>(next, ct);
            var audits = response.Audits;
            result.AddRange(audits.Where(a => a.CreatedAt > since));

            var newestFirst = audits.Count < 2 || audits[0].CreatedAt >= audits[^1].CreatedAt;
            if (newestFirst && audits.Count > 0 && audits[^1].CreatedAt <= since)
                break;
            next = response.Next;
        }
        return result;
    }

    public async Task<string> GetUserNameAsync(long userId, CancellationToken ct)
    {
        if (_userNames.TryGetValue(userId, out var cached))
            return cached;

        string name;
        try
        {
            name = (await GetAsync<UserResponse>($"/api/v2/users/{userId}.json", ct)).User?.Name ?? $"User {userId}";
        }
        catch (HttpRequestException)
        {
            name = $"User {userId}";
        }

        _userNames[userId] = name;
        return name;
    }

    private async Task<List<Ticket>> SearchTicketsAsync(string query, CancellationToken ct) =>
        await GetAllPagesAsync<SearchResponse, Ticket>($"/api/v2/search.json?query={Uri.EscapeDataString(query)}&per_page=100", r => r.Results, ct);

    private async Task<List<TItem>> GetAllPagesAsync<TResponse, TItem>(string path, Func<TResponse, List<TItem>> items, CancellationToken ct)
        where TResponse : PagedResponse
    {
        var result = new List<TItem>();
        string? next = path;

        for (var page = 0; next is not null && page < MaxPages; page++)
        {
            var response = await GetAsync<TResponse>(next, ct);
            result.AddRange(items(response));
            next = response.Next;
        }

        return result;
    }

    private Task<T> GetAsync<T>(string pathOrUrl, CancellationToken ct) => RequestAsync<T>(pathOrUrl, null, null, ct);

    private async Task<T> RequestAsync<T>(string pathOrUrl, string? method, string? jsonBody, CancellationToken ct)
    {
        var url = pathOrUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? pathOrUrl : settings.BaseUrl + pathOrUrl;
        var recovered = false;

        for (var attempt = 0; ; attempt++)
        {
            var response = method is null
                ? await session.GetAsync(url, ct)
                : await session.SendAsync(method, url, jsonBody ?? "", ct);

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
                Log.Info($"Rate limited, waiting {wait.TotalSeconds:0}s");
                await Task.Delay(wait, ct);
                continue;
            }

            if (!response.IsSuccess)
            {
                throw new HttpRequestException($"{method ?? "GET"} {url} failed: {response.Status} {Truncate(response.Body, 300)}", null, (HttpStatusCode)response.Status);
            }

            return JsonSerializer.Deserialize<T>(response.Body, JsonOptions)
                ?? throw new InvalidOperationException($"Empty response from {url}");
        }
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max] + "...";
}

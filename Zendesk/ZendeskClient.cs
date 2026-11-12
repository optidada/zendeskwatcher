using System.Net;
using System.Text.Json;

namespace ZendeskWatcher.Zendesk;

/// <summary>Thrown when there is no valid token and the user has to sign in again.</summary>
public sealed class AuthRequiredException() : Exception("Sign-in required");

/// <summary>Read-only Zendesk API calls, made with the signed-in agent's own permissions.</summary>
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

    public Task<List<Audit>> GetAuditsAsync(long ticketId, CancellationToken ct) =>
        GetAllPagesAsync<AuditsResponse, Audit>($"/api/v2/tickets/{ticketId}/audits.json", r => r.Audits, ct);

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

    private async Task<T> GetAsync<T>(string pathOrUrl, CancellationToken ct)
    {
        var url = pathOrUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? pathOrUrl : settings.BaseUrl + pathOrUrl;
        var recovered = false;

        for (var attempt = 0; ; attempt++)
        {
            var response = await session.GetAsync(url, ct);

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
                throw new HttpRequestException($"GET {url} failed: {response.Status} {Truncate(response.Body, 300)}", null, (HttpStatusCode)response.Status);
            }

            return JsonSerializer.Deserialize<T>(response.Body, JsonOptions)
                ?? throw new InvalidOperationException($"Empty response from {url}");
        }
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max] + "...";
}

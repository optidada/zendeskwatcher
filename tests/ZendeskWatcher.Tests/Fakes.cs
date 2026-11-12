using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;

// The watcher saves state.json; tests share one temporary data folder, so they must not run at the same time.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace ZendeskWatcher.Tests;

internal static class TestSetup
{
    /// <summary>
    /// Points the app's data folder (state.json, log) at a temp folder before anything touches it, so tests never
    /// read or change the real %LOCALAPPDATA%\ZendeskWatcher. Emptied at the start of each run.
    /// </summary>
    [ModuleInitializer]
    internal static void UseTempDataDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ZendeskWatcher.Tests");
        if (Directory.Exists(dir))
            Directory.Delete(dir, recursive: true);
        Directory.CreateDirectory(dir);
        Environment.SetEnvironmentVariable(AppPaths.DataDirVariable, dir);
    }
}

/// <summary>Records what would have been shown.</summary>
internal sealed class RecordingNotifier : INotificationSink
{
    public List<NotificationContent> Shown { get; } = [];
    public List<string> MissingGroups { get; } = [];

    public void Show(NotificationContent content) => Shown.Add(content);
    public void ShowMissingGroups(IReadOnlyCollection<string> names) => MissingGroups.AddRange(names);
}

/// <summary>
/// A tiny Zendesk: tickets as they really are (show_many), and a search index that can lag behind, like the real
/// one. Only the requests the unassigned check needs; anything else is an empty list or 404.
/// </summary>
internal sealed partial class FakeZendesk : IZendeskSession
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    public User Me { get; } = new() { Id = 1, Name = "Test Agent", Role = "agent" };
    public List<ZendeskWatcher.Zendesk.Group> Groups { get; } = [new() { Id = 10, Name = "Tier 2" }];

    /// <summary>The tickets as they really are.</summary>
    public Dictionary<long, Ticket> Tickets { get; } = [];

    /// <summary>Extra ticket ids the (stale) search index still returns as unassigned, per group.</summary>
    public Dictionary<long, List<long>> StaleSearchHits { get; } = [];

    private DateTimeOffset _clock = DateTimeOffset.UtcNow.AddHours(-2);

    public Ticket AddUnassigned(long id, long group = 10, string subject = "Help!")
    {
        _clock = _clock.AddMinutes(1);
        var ticket = new Ticket { Id = id, Subject = subject, Status = "new", GroupId = group, CreatedAt = _clock, UpdatedAt = _clock };
        Tickets[id] = ticket;
        return ticket;
    }

    public bool MightBeSignedIn => true;

    public Task<ApiResponse> GetAsync(string url, CancellationToken ct)
    {
        var uri = new Uri(url);
        var path = uri.AbsolutePath;
        var query = Uri.UnescapeDataString(uri.Query);

        object? body = path switch
        {
            "/api/v2/users/me.json" => new { user = Me },
            "/api/v2/groups.json" => new { groups = Groups },
            "/api/v2/search.json" => new { results = Search(query) },
            "/api/v2/tickets/show_many.json" => new { tickets = ShowMany(query) },
            _ when path.Contains("/tickets/") => new { tickets = Array.Empty<Ticket>() },
            _ => null
        };
        return Task.FromResult(body is null
            ? new ApiResponse(404, "{}", null)
            : new ApiResponse(200, JsonSerializer.Serialize(body, Json), null));
    }

    private List<Ticket> Search(string query)
    {
        var match = GroupQuery().Match(query);
        if (!match.Success || !query.Contains("assignee:none"))
            return [];
        long? group = match.Groups[1].Value == "none" ? null : long.Parse(match.Groups[1].Value);
        var hits = Tickets.Values.Where(t => t.AssigneeId is null && t.IsOpen && t.GroupId == group).ToList();
        if (group is { } g && StaleSearchHits.TryGetValue(g, out var stale))
            hits.AddRange(stale.Where(Tickets.ContainsKey).Select(id => Tickets[id]));
        // Search results are partial objects in reality; the watcher must re-read them, so don't leak live state.
        return hits.DistinctBy(t => t.Id).Select(t => new Ticket { Id = t.Id, Subject = t.Subject, Status = "new", GroupId = group }).ToList();
    }

    private List<Ticket> ShowMany(string query)
    {
        var ids = query.Split("ids=")[1].Split(',').Select(long.Parse);
        return ids.Where(Tickets.ContainsKey).Select(id => Tickets[id]).ToList();
    }

    [GeneratedRegex(@"group:(\d+|none)")]
    private static partial Regex GroupQuery();

    public Task<ApiResponse> SendAsync(string method, string url, string jsonBody, CancellationToken ct) =>
        Task.FromResult(new ApiResponse(404, "{}", null));

    public Task<bool> TryRecoverAsync(CancellationToken ct) => Task.FromResult(false);
    public Task SignInAsync(IWin32Window? owner, CancellationToken ct) => Task.CompletedTask;
    public void SignOut() { }
}

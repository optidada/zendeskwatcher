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
/// A tiny Zendesk: tickets as they really are (show_many), a search index that can lag behind like the real one,
/// the user's assigned / CC'd / followed lists, ticket changes (audits) and users. Anything else is a 404.
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

    /// <summary>Ticket id -> its changes.</summary>
    public Dictionary<long, List<Audit>> Audits { get; } = [];

    public Dictionary<long, User> Users { get; } = [];
    public Dictionary<long, string> Organizations { get; } = [];

    private DateTimeOffset _clock = DateTimeOffset.UtcNow.AddHours(-2);

    private DateTimeOffset Tick() => _clock = _clock.AddMinutes(1);

    public Ticket AddUnassigned(long id, long group = 10, string subject = "Help!")
    {
        var at = Tick();
        var ticket = new Ticket { Id = id, Subject = subject, Status = "new", GroupId = group, CreatedAt = at, UpdatedAt = at };
        Tickets[id] = ticket;
        return ticket;
    }

    /// <summary>An open ticket assigned to <paramref name="assignee"/> (me by default).</summary>
    public Ticket AddAssigned(long id, string? priority, long? assignee = null, string subject = "Something broke")
    {
        var at = Tick();
        var ticket = new Ticket { Id = id, Subject = subject, Status = "open", Priority = priority, GroupId = 10, AssigneeId = assignee ?? Me.Id, CreatedAt = at, UpdatedAt = at };
        Tickets[id] = ticket;
        return ticket;
    }

    /// <summary>A public reply by <paramref name="authorId"/>; the ticket's updated_at moves on.</summary>
    public void Reply(long ticketId, long authorId, string text) =>
        AddAudit(ticketId, authorId, new AuditEvent { Type = "Comment", Public = true, PlainBody = text });

    /// <summary>A field change (e.g. "priority" from "low" to "urgent") by <paramref name="authorId"/>.</summary>
    public void Change(long ticketId, long authorId, string field, string from, string to)
    {
        AddAudit(ticketId, authorId, new AuditEvent
        {
            Type = "Change", FieldName = field,
            PreviousValue = JsonSerializer.SerializeToElement(from), Value = JsonSerializer.SerializeToElement(to)
        });
        if (field == "priority")
            Tickets[ticketId].Priority = to;
    }

    private void AddAudit(long ticketId, long authorId, AuditEvent ev)
    {
        var at = Tick();
        if (!Audits.TryGetValue(ticketId, out var audits))
            Audits[ticketId] = audits = [];
        audits.Add(new Audit { Id = audits.Count + 1, AuthorId = authorId, CreatedAt = at, Events = [ev] });
        Tickets[ticketId].UpdatedAt = at;
    }

    public bool MightBeSignedIn => true;

    public Task<ApiResponse> GetAsync(string url, CancellationToken ct)
    {
        var uri = new Uri(url);
        var path = uri.AbsolutePath;
        var query = Uri.UnescapeDataString(uri.Query);

        var me = Me.Id;
        object? body = path switch
        {
            "/api/v2/users/me.json" => new { user = Me },
            "/api/v2/groups.json" => new { groups = Groups },
            "/api/v2/search.json" => new { results = Search(query) },
            "/api/v2/tickets/show_many.json" => new { tickets = ShowMany(query) },
            _ when path == $"/api/v2/users/{me}/tickets/assigned.json" => new { tickets = Recent(t => t.AssigneeId == me) },
            _ when path == $"/api/v2/users/{me}/tickets/ccd.json" => new { tickets = Recent(t => t.EmailCcIds.Contains(me)) },
            _ when path == $"/api/v2/users/{me}/tickets/followed.json" => new { tickets = Recent(t => t.FollowerIds.Contains(me)) },
            _ when AuditsPath().Match(path) is { Success: true } m =>
                new { audits = Audits.GetValueOrDefault(long.Parse(m.Groups[1].Value), []).OrderByDescending(a => a.CreatedAt).ToList() },
            _ when UserPath().Match(path) is { Success: true } m && Users.TryGetValue(long.Parse(m.Groups[1].Value), out var user) => new { user },
            _ when OrgPath().Match(path) is { Success: true } m && Organizations.TryGetValue(long.Parse(m.Groups[1].Value), out var org) =>
                new { organization = new { id = long.Parse(m.Groups[1].Value), name = org } },
            _ => null
        };
        return Task.FromResult(body is null
            ? new ApiResponse(404, "{}", null)
            : new ApiResponse(200, JsonSerializer.Serialize(body, Json), null));
    }

    /// <summary>The 30 most recently updated tickets matching <paramref name="filter"/>, newest first.</summary>
    private List<Ticket> Recent(Func<Ticket, bool> filter) =>
        Tickets.Values.Where(filter).OrderByDescending(t => t.UpdatedAt).Take(30).ToList();

    private List<Ticket> Search(string query)
    {
        if (query.Contains("assignee:me"))
            return Tickets.Values.Where(t => t.AssigneeId == Me.Id && t.IsOpen).ToList();
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

    [GeneratedRegex(@"^/api/v2/tickets/(\d+)/audits\.json$")]
    private static partial Regex AuditsPath();

    [GeneratedRegex(@"^/api/v2/users/(\d+)\.json$")]
    private static partial Regex UserPath();

    [GeneratedRegex(@"^/api/v2/organizations/(\d+)\.json$")]
    private static partial Regex OrgPath();

    public Task<ApiResponse> SendAsync(string method, string url, string jsonBody, CancellationToken ct) =>
        Task.FromResult(new ApiResponse(404, "{}", null));

    public Task<bool> TryRecoverAsync(CancellationToken ct) => Task.FromResult(false);
    public Task SignInAsync(IWin32Window? owner, CancellationToken ct) => Task.CompletedTask;
    public void SignOut() { }
}

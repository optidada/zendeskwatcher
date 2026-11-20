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
        ZendeskWatcher.Tracking.TicketWatcher.TestMode = true; // no minute's wait before the ticket history is read
        ZendeskClient.LocalLimit = int.MaxValue; // tests send hundreds of requests a second to the fake Zendesk
    }

    /// <summary>
    /// Notifications on screen don't depend on the real PC: an idle mouse, a locked screen or a presentation would make
    /// them wait in line, and tests that show them failed now and then when run while nobody touched the PC.
    /// </summary>
    [ModuleInitializer]
    internal static void AssumeUserAtThePc()
    {
        ZendeskWatcher.UI.Popups.PopupForm.AssumeUserPresent = true;
        // Off the visible screen: a click or hover on a test's notification (by whoever uses the PC) would change the result.
        ZendeskWatcher.UI.Popups.PopupForm.TestWorkArea = new Rectangle(-8000, -8000, 1920, 1040);
    }
}

/// <summary>Records what would have been shown.</summary>
internal sealed class RecordingNotifier : INotificationSink
{
    public List<NotificationContent> Shown { get; } = [];
    public List<string> MissingGroups { get; } = [];
    public List<long> ClosedTaken { get; } = [];

    /// <summary>How many more notifications fail to show (something going wrong while announcing them).</summary>
    public int FailNextShows { get; set; }

    public void Show(NotificationContent content)
    {
        if (FailNextShows > 0)
        {
            FailNextShows--;
            throw new HttpRequestException("test failure while showing");
        }
        Shown.Add(content);
    }
    public void ShowMissingGroups(IReadOnlyCollection<string> names) => MissingGroups.AddRange(names);
    public void CloseTaken(long ticketId, string? note = null)
    {
        ClosedTaken.Add(ticketId);
        TakenNotes[ticketId] = note;
    }

    public Dictionary<long, string?> TakenNotes { get; } = [];

    public void NoteTaken(long ticketId, string note) => NotedTaken[ticketId] = note;

    /// <summary>Taken, but shown anyway (Drop notifications): the note it shows while it stays.</summary>
    public Dictionary<long, string> NotedTaken { get; } = [];
}

/// <summary>
/// A tiny Zendesk: tickets as they really are (show_many), a search index that can lag behind like the real one,
/// the user's assigned / CC'd / followed lists, ticket changes (audits) and users. Anything else is a 404.
/// </summary>
internal sealed partial class FakeZendesk : IZendeskSession, IHasOwnActivity
{
    /// <summary>Its own memory of who was active, so tests running side by side don't see each other's colleagues.</summary>
    public ColleagueActivity Activity { get; } = new();

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    public User Me { get; } = new() { Id = 1, Name = "Test Agent", Role = "agent" };
    public List<ZendeskWatcher.Zendesk.Group> Groups { get; } = [new() { Id = 10, Name = "Tier 2" }];

    /// <summary>The tickets as they really are.</summary>
    public Dictionary<long, Ticket> Tickets { get; } = [];

    /// <summary>Extra ticket ids the (stale) search index still returns as unassigned, per group.</summary>
    public Dictionary<long, List<long>> StaleSearchHits { get; } = [];

    /// <summary>Ticket id -> its changes.</summary>
    public Dictionary<long, List<Audit>> Audits { get; } = [];

    /// <summary>Group id → its members (agents), for the group's users list.</summary>
    public Dictionary<long, List<long>> GroupMembers { get; } = [];

    public Dictionary<long, User> Users { get; } = [];
    public List<CustomStatus> CustomStatuses { get; } = [];
    public Dictionary<long, string> Organizations { get; } = [];

    /// <summary>The account's organization fields (anonymous objects as Zendesk returns them), and values per organization.</summary>
    public List<object> OrganizationFields { get; } = [];
    public Dictionary<long, Dictionary<string, object?>> OrganizationFieldValues { get; } = [];

    private DateTimeOffset _clock = DateTimeOffset.UtcNow.AddHours(-2);

    private DateTimeOffset Tick() => _clock = _clock.AddMinutes(1);

    public Ticket AddUnassigned(long id, long group = 10, string subject = "Help!")
    {
        var at = Tick();
        var ticket = new Ticket { Id = id, Subject = subject, Status = "new", GroupId = group, CreatedAt = at, UpdatedAt = at };
        Tickets[id] = ticket;
        return ticket;
    }

    /// <summary>
    /// A ticket that came in <paramref name="minutesAgo"/> minutes ago in group 10 and that <paramref name="by"/> assigned to
    /// <paramref name="to"/> a minute later, before any check saw it unassigned (with its history). With
    /// <paramref name="createdAssigned"/> it had its assignee from the start.
    /// </summary>
    public Ticket AddTakenQuickly(long id, string priority, long by, long to, int minutesAgo = 3, bool createdAssigned = false)
    {
        var created = DateTimeOffset.UtcNow.AddMinutes(-minutesAgo);
        var ticket = new Ticket { Id = id, Subject = "Site down", Status = "open", Priority = priority, GroupId = 10, AssigneeId = to, CreatedAt = created, UpdatedAt = created.AddMinutes(1) };
        Tickets[id] = ticket;
        var create = new AuditEvent { Type = "Create", FieldName = "assignee_id", Value = createdAssigned ? JsonSerializer.SerializeToElement(to.ToString()) : null };
        Audits[id] = [new Audit { Id = 1, AuthorId = by, CreatedAt = created, Events = [create] }];
        if (!createdAssigned)
            Audits[id].Add(new Audit
            {
                Id = 2, AuthorId = by, CreatedAt = created.AddMinutes(1),
                Events = [new AuditEvent { Type = "Change", FieldName = "assignee_id", PreviousValue = null, Value = JsonSerializer.SerializeToElement(to.ToString()) }]
            });
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

    /// <summary>A public reply and field changes in one update (e.g. a customer reply that reopens the ticket).</summary>
    public void ReplyAndChange(long ticketId, long authorId, string text, params (string Field, string From, string To)[] changes) =>
        AddAudit(ticketId, authorId, [new AuditEvent { Type = "Comment", Public = true, PlainBody = text },
            .. changes.Select(c => new AuditEvent
            {
                Type = "Change", FieldName = c.Field,
                PreviousValue = JsonSerializer.SerializeToElement(c.From), Value = JsonSerializer.SerializeToElement(c.To)
            })]);

    /// <summary>Several field changes in one update, e.g. "status" and "custom_status_id" together.</summary>
    public void ChangeTogether(long ticketId, long authorId, params (string Field, string From, string To)[] changes)
    {
        AddAudit(ticketId, authorId, changes.Select(c => new AuditEvent
        {
            Type = "Change", FieldName = c.Field,
            PreviousValue = JsonSerializer.SerializeToElement(c.From), Value = JsonSerializer.SerializeToElement(c.To)
        }).ToArray());
    }

    private void AddAudit(long ticketId, long authorId, params AuditEvent[] events)
    {
        var at = Tick();
        if (!Audits.TryGetValue(ticketId, out var audits))
            Audits[ticketId] = audits = [];
        audits.Add(new Audit { Id = audits.Count + 1, AuthorId = authorId, CreatedAt = at, Events = [.. events] });
        Tickets[ticketId].UpdatedAt = at;
    }

    public bool MightBeSignedIn => true;

    /// <summary>Answers to give before the normal ones, e.g. a 429 "too many requests".</summary>
    public Queue<ApiResponse> NextAnswers { get; } = new();

    /// <summary>Every URL requested, in order.</summary>
    public System.Collections.Concurrent.ConcurrentQueue<string> Requested { get; } = new(); // requests may run side by side

    /// <summary>URL part → how many more requests containing it fail with a server error (Zendesk having a bad moment).</summary>
    private readonly Dictionary<string, int> _failing = [];

    /// <summary>The next <paramref name="times"/> requests whose URL contains <paramref name="urlPart"/> fail (500).</summary>
    public void FailNext(string urlPart, int times = 1) => _failing[urlPart] = times;

    public Task<ApiResponse> GetAsync(string url, CancellationToken ct)
    {
        Requested.Enqueue(url);
        if (NextAnswers.TryDequeue(out var answer))
            return Task.FromResult(answer);
        lock (_failing)
            if (_failing.FirstOrDefault(f => f.Value > 0 && url.Contains(f.Key, StringComparison.Ordinal)) is { Key: { } part })
            {
                _failing[part]--;
                return Task.FromResult(new ApiResponse(500, "{\"error\":\"test failure\"}", null));
            }
        var uri = new Uri(url);
        var path = uri.AbsolutePath;
        var query = Uri.UnescapeDataString(uri.Query);

        var me = Me.Id;
        object? body = path switch
        {
            "/api/v2/users/me.json" => new { user = Me },
            "/api/v2/groups.json" => new { groups = Groups },
            _ when GroupUsersPath().Match(path) is { Success: true } gm =>
                new { users = GroupMembers.GetValueOrDefault(long.Parse(gm.Groups[1].Value), []).Where(Users.ContainsKey).Select(id => Users[id]).ToList() },
            "/api/v2/custom_statuses.json" => new { custom_statuses = CustomStatuses },
            "/api/v2/search.json" => new { results = Search(query) },
            "/api/v2/search/export.json" => new { results = Search(query), meta = new { has_more = false } }, // Insights → Team workload
            "/api/v2/tickets/show_many.json" => new { tickets = ShowMany(query) },
            "/api/v2/organization_fields.json" => new { organization_fields = OrganizationFields },
            "/api/v2/organizations/show_many.json" => new
            {
                organizations = query.Split("ids=")[1].Split(',').Select(long.Parse).Where(Organizations.ContainsKey)
                    .Select(id => new { id, name = Organizations[id] }).ToList()
            },
            _ when path == $"/api/v2/users/{me}/tickets/assigned.json" => new { tickets = Recent(t => t.AssigneeId == me) },
            _ when path == $"/api/v2/users/{me}/tickets/ccd.json" => new { tickets = Recent(t => t.EmailCcIds.Contains(me)) },
            _ when path == $"/api/v2/users/{me}/tickets/followed.json" => new { tickets = Recent(t => t.FollowerIds.Contains(me)) },
            _ when AuditsPath().Match(path) is { Success: true } m =>
                new { audits = Audits.GetValueOrDefault(long.Parse(m.Groups[1].Value), []).OrderByDescending(a => a.CreatedAt).ToList() },
            _ when CommentsPath().Match(path) is { Success: true } m => new // the comments list: the Comment events of the audits
            {
                comments = Audits.GetValueOrDefault(long.Parse(m.Groups[1].Value), []).OrderByDescending(a => a.CreatedAt)
                    .SelectMany(a => a.Events.Where(e => e.Type == "Comment").Select(e => new
                    {
                        id = a.Id, type = "Comment", author_id = a.AuthorId, created_at = a.CreatedAt, @public = e.Public,
                        plain_body = e.PlainBody, body = e.Body
                    })).ToList()
            },
            _ when UserPath().Match(path) is { Success: true } m && Users.TryGetValue(long.Parse(m.Groups[1].Value), out var user) => new { user },
            _ when OrgPath().Match(path) is { Success: true } m && Organizations.TryGetValue(long.Parse(m.Groups[1].Value), out var org) =>
                new { organization = new { id = long.Parse(m.Groups[1].Value), name = org, organization_fields = OrganizationFieldValues.GetValueOrDefault(long.Parse(m.Groups[1].Value)) } },
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
        if (query.Contains("assignee:me") && query.Contains("status>=solved")) // Experimental: tickets I solved
            return Tickets.Values.Where(t => t.AssigneeId == Me.Id && t.Status is "solved" or "closed").ToList();
        if (query.Contains("assignee:me"))
            return Tickets.Values.Where(t => t.AssigneeId == Me.Id && t.IsOpen).ToList();
        if (AssigneeQuery().Match(query) is { Success: true } assignee) // Colleagues: an agent's recently updated tickets
            return Tickets.Values.Where(t => t.AssigneeId == long.Parse(assignee.Groups[1].Value) && (t.IsOpen || !query.Contains("status<solved")))
                .OrderByDescending(t => t.UpdatedAt).ToList();
        var match = GroupQuery().Match(query);
        if (query.Contains("-assignee:none") && CreatedQuery().Match(query) is { Success: true } created && match.Success) // tickets taken quickly
        {
            var since = DateTimeOffset.Parse(created.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            return Tickets.Values.Where(t => t.AssigneeId is not null && t.CreatedAt > since && t.GroupId == long.Parse(match.Groups[1].Value)).ToList();
        }
        if (UpdatedQuery().Match(query) is { Success: true } updated && match.Success && !query.Contains("assignee")) // Clients: what changed since the last read
        {
            var since = DateTimeOffset.Parse(updated.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            return Tickets.Values.Where(t => t.GroupId == long.Parse(match.Groups[1].Value) && t.UpdatedAt > since).ToList();
        }
        if (query.Contains("created>") && match.Success) // Settings → Groups → Clients: a group's recent tickets (Take it ▾: one client's)
            return Tickets.Values.Where(t => t.GroupId == (match.Groups[1].Value == "none" ? null : long.Parse(match.Groups[1].Value))
                && (OrganizationQuery().Match(query) is not { Success: true } org || t.OrganizationId == long.Parse(org.Groups[1].Value))).ToList();
        if (query.Contains("-assignee:none") && match.Success) // Help colleagues: assigned tickets before solved, in a group
            return Tickets.Values.Where(t => t.AssigneeId is not null && t.IsOpen && t.GroupId == long.Parse(match.Groups[1].Value)).ToList();
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
        var ids = query.Split("ids=")[1].Split('&')[0].Split(',').Select(long.Parse);
        return ids.Where(Tickets.ContainsKey).Select(id => Tickets[id]).ToList();
    }

    [GeneratedRegex(@"created>([^\s&]+)")]
    private static partial Regex CreatedQuery();

    [GeneratedRegex(@"group:(\d+|none)")]
    private static partial Regex GroupQuery();

    [GeneratedRegex(@"organization:(\d+)")]
    private static partial Regex OrganizationQuery();

    [GeneratedRegex(@"updated>([^\s&]+)")]
    private static partial Regex UpdatedQuery();

    [GeneratedRegex(@"(?<!-)assignee:(\d+)")]
    private static partial Regex AssigneeQuery();

    [GeneratedRegex(@"^/api/v2/tickets/(\d+)/audits\.json$")]
    private static partial Regex AuditsPath();

    [GeneratedRegex(@"^/api/v2/tickets/(\d+)/comments\.json$")]
    private static partial Regex CommentsPath();

    [GeneratedRegex(@"^/api/v2/groups/(\d+)/users\.json$")]
    private static partial Regex GroupUsersPath();

    [GeneratedRegex(@"^/api/v2/users/(\d+)\.json$")]
    private static partial Regex UserPath();

    [GeneratedRegex(@"^/api/v2/organizations/(\d+)\.json$")]
    private static partial Regex OrgPath();

    public Task<ApiResponse> SendAsync(string method, string url, string jsonBody, CancellationToken ct) =>
        Task.FromResult(new ApiResponse(404, "{}", null));

    public Task<bool> TryRecoverAsync(CancellationToken ct) => Task.FromResult(false);
    public Task SignInAsync(object? owner, CancellationToken ct) => Task.CompletedTask;
    public void SignOut() { }
}

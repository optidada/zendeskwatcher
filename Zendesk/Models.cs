using System.Text.Json;

namespace ZendeskWatcher.Zendesk;

public sealed class User
{
    public long Id { get; set; }
    public string? Name { get; set; }
    public string? Email { get; set; }
    public string? Role { get; set; }

    /// <summary>Time zone as set on the profile, e.g. "Europe/Stockholm" (IANA) and "Stockholm" (Zendesk name).</summary>
    public string? IanaTimeZone { get; set; }
    public string? TimeZone { get; set; }
}

public sealed class Group
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
}

public sealed class Ticket
{
    public long Id { get; set; }
    public string? Subject { get; set; }
    public string? Status { get; set; }
    public string? Priority { get; set; }
    public long? AssigneeId { get; set; }
    public long? GroupId { get; set; }
    public long? RequesterId { get; set; }
    public long? OrganizationId { get; set; }
    public List<long> FollowerIds { get; set; } = [];
    public List<long> EmailCcIds { get; set; } = [];
    public List<long> CollaboratorIds { get; set; } = [];
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public bool IsOpen => Status is "new" or "open" or "pending" or "hold";
}

public sealed class Audit
{
    public long Id { get; set; }
    public long AuthorId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public List<AuditEvent> Events { get; set; } = [];
}

public sealed class AuditEvent
{
    public string? Type { get; set; }
    public string? FieldName { get; set; }
    public JsonElement? Value { get; set; }
    public JsonElement? PreviousValue { get; set; }
    public bool? Public { get; set; }
    public string? PlainBody { get; set; }
    public string? Body { get; set; }
}

// ---- Response envelopes ----

internal sealed class UserResponse { public User? User { get; set; } }
internal sealed class Organization { public long Id { get; set; } public string? Name { get; set; } }
internal sealed class OrganizationResponse { public Organization? Organization { get; set; } }
internal sealed class UsersResponse : PagedResponse { public List<User> Users { get; set; } = []; }
internal sealed class GroupsResponse : PagedResponse { public List<Group> Groups { get; set; } = []; }
internal sealed class GroupMembership { public long GroupId { get; set; } }
internal sealed class GroupMembershipsResponse : PagedResponse { public List<GroupMembership> GroupMemberships { get; set; } = []; }
internal sealed class TicketResponse { public Ticket? Ticket { get; set; } }
internal sealed class TicketsResponse : PagedResponse { public List<Ticket> Tickets { get; set; } = []; }
internal sealed class SearchResponse : PagedResponse { public List<Ticket> Results { get; set; } = []; }
internal sealed class AuditsResponse : PagedResponse { public List<Audit> Audits { get; set; } = []; }

/// <summary>Zendesk uses either offset paging (next_page) or cursor paging (meta.has_more + links.next).</summary>
internal abstract class PagedResponse
{
    public string? NextPage { get; set; }
    public PageMeta? Meta { get; set; }
    public PageLinks? Links { get; set; }

    public string? Next => NextPage ?? (Meta?.HasMore == true ? Links?.Next : null);
}

internal sealed class PageMeta { public bool HasMore { get; set; } }
internal sealed class PageLinks { public string? Next { get; set; } }

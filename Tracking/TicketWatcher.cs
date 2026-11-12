using System.Text.Json;
using System.Text.RegularExpressions;
using ZendeskWatcher.UI.Popups;
using ZendeskWatcher.Zendesk;

namespace ZendeskWatcher.Tracking;

/// <summary>
/// Polls Zendesk and decides what is new:
/// <list type="bullet">
/// <item>new unassigned tickets in the watched groups (via search),</item>
/// <item>changes made by others on tickets assigned to the user (via ticket lists and audits, no search lag).</item>
/// </list>
/// The first poll of each list only records the current tickets, so starting the app does not flood notifications.
/// </summary>
public sealed partial class TicketWatcher(Settings settings, ZendeskClient zendesk, Notifier notifier)
{
    private const long SystemUserId = -1;
    private const int MaxSeparateUnassignedToasts = 5;
    private const int MaxLinesPerToast = 4;
    private static readonly TimeSpan ForgetUnassignedAfter = TimeSpan.FromDays(1);

    private WatchState _state = new();
    private Dictionary<long, string> _groupNames = [];

    public User? Me { get; private set; }
    public int UnassignedCount { get; private set; }
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

        _groupNames = settings.WatchedGroups.ToDictionary(g => g.Id, g => g.Name);
        try
        {
            foreach (var group in await zendesk.GetGroupsAsync(ct))
                _groupNames[group.Id] = group.Name;
        }
        catch (HttpRequestException ex)
        {
            Log.Error("Could not load group names", ex);
        }
    }

    public async Task PollAsync(CancellationToken ct)
    {
        await CheckUnassignedAsync(ct);

        if (settings.TrackMyTickets)
        {
            await CheckMyTicketsAsync(ct);
        }
        else if (_state.MyTicketsBaselined)
        {
            // Rebuild the baseline when tracking is switched on again, instead of reporting stale changes.
            _state.MyTickets.Clear();
            _state.MyTicketsBaselined = false;
        }

        _state.Save();
    }

    private async Task CheckUnassignedAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var total = 0;

        foreach (var group in settings.WatchedGroups)
        {
            var tickets = await zendesk.GetUnassignedTicketsAsync(group.Id, ct);
            total += tickets.Count;

            var isBaseline = _state.BaselinedGroups.Add(group.Id);
            var fresh = tickets.Where(t => !_state.SeenUnassigned.ContainsKey(t.Id)).OrderBy(t => t.CreatedAt).ToList();

            foreach (var ticket in tickets)
                _state.SeenUnassigned[ticket.Id] = now;

            // New tickets are always recorded, so switching notifications on later does not report old ones.
            if (isBaseline || fresh.Count == 0 || !settings.NotifyUnassigned)
                continue;

            Log.Info($"{fresh.Count} new unassigned ticket(s) {group.Describe()}: {string.Join(",", fresh.Select(t => t.Id))}");

            if (fresh.Count > MaxSeparateUnassignedToasts)
            {
                notifier.Show(new NotificationContent(NotificationKind.NewTicket, "New tickets",
                    $"{fresh.Count} new unassigned tickets",
                    string.Join("\n", fresh.Take(3).Select(t => $"#{t.Id} {t.Subject}")),
                    Context: GroupLabel(group)));
                continue;
            }

            foreach (var ticket in fresh)
            {
                notifier.Show(new NotificationContent(NotificationKind.NewTicket, "New ticket", $"#{ticket.Id} {ticket.Subject}",
                    $"Created {ticket.CreatedAt.ToLocalTime():HH:mm}", ticket.Id, ticket.Priority, GroupLabel(group)));
            }
        }

        // Forget tickets that have not been unassigned for a while, so they notify again if they come back.
        foreach (var id in _state.SeenUnassigned.Where(kv => now - kv.Value > ForgetUnassignedAfter).Select(kv => kv.Key).ToList())
            _state.SeenUnassigned.Remove(id);

        UnassignedCount = total;
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

    /// <summary>Reads the audits since the last known update and notifies about changes made by someone else.</summary>
    private async Task ReportChangesAsync(Ticket ticket, DateTimeOffset since, CancellationToken ct)
    {
        var me = Me!.Id;
        var audits = (await zendesk.GetAuditsAsync(ticket.Id, ct))
            .Where(a => a.CreatedAt > since && a.AuthorId != me)
            .OrderBy(a => a.CreatedAt);

        var lines = new List<string>();
        foreach (var audit in audits)
        {
            var author = audit.AuthorId == SystemUserId ? "Zendesk" : await zendesk.GetUserNameAsync(audit.AuthorId, ct);
            foreach (var ev in audit.Events)
            {
                if (await DescribeAsync(ev, author, ct) is { } line)
                    lines.Add(line);
            }
        }

        if (lines.Count == 0)
            return;

        Log.Info($"Ticket #{ticket.Id}: {string.Join(" | ", lines)}");

        var shown = lines.TakeLast(MaxLinesPerToast).ToList();
        if (lines.Count > shown.Count)
            shown.Insert(0, $"(+{lines.Count - shown.Count} earlier changes)");

        notifier.Show(new NotificationContent(NotificationKind.MyTicket, "Ticket updated", $"#{ticket.Id} {ticket.Subject}",
            string.Join("\n", shown), ticket.Id, ticket.Priority));
    }

    private static string GroupLabel(WatchedGroup group) => group.IsNoGroup ? "No group" : group.Name;

    private async Task<string?> DescribeAsync(AuditEvent ev, string author, CancellationToken ct)
    {
        switch (ev.Type)
        {
            case "Comment" when ev.Public == true && settings.NotifyPublicReplies:
                return $"{author} replied: {Snippet(ev)}";

            case "Comment" when ev.Public == false && settings.NotifyInternalNotes:
                return $"{author} added an internal note: {Snippet(ev)}";

            case "Change" when ev.FieldName == "status" && settings.NotifyStatusChanges:
                return $"Status {Text(ev.PreviousValue) ?? "?"} → {Text(ev.Value)} ({author})";

            case "Change" when ev.FieldName == "priority" && settings.NotifyPriorityChanges:
                return $"Priority {Text(ev.PreviousValue) ?? "none"} → {Text(ev.Value) ?? "none"} ({author})";

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

    private static TrackedTicket ToTracked(Ticket t) => new() { Subject = t.Subject, Status = t.Status, UpdatedAt = t.UpdatedAt };

    private static string? Text(JsonElement? value) => value?.ValueKind switch
    {
        JsonValueKind.String => value.Value.GetString(),
        JsonValueKind.Number => value.Value.GetRawText(),
        _ => null
    };

    private static long? Id(JsonElement? value) => long.TryParse(Text(value), out var id) ? id : null;

    private static string Snippet(AuditEvent ev)
    {
        var text = Whitespace().Replace(ev.PlainBody ?? ev.Body ?? "", " ").Trim();
        return text.Length <= 140 ? text : text[..140] + "…";
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}

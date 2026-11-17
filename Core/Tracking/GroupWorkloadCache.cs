using ZendeskWatcher.Zendesk;

namespace ZendeskWatcher.Tracking;

/// <summary>
/// A group's members and assigned open tickets, shared by Insights → Team workload and "Who could take it" (▾ next to
/// Take it): both need exactly this, and a big group takes a while to read (all its open tickets in full). Kept in memory
/// only (ticket content never goes to disk); each caller says how old it may be. A read already under way is shared, not
/// started twice. Dropped after an assign, so the next look shows the new count.
/// </summary>
public static class GroupWorkloadCache
{
    public sealed record Entry(DateTime At, List<User> Members, List<Ticket> Tickets);

    private static readonly object Gate = new();
    private static readonly Dictionary<(string Subdomain, long Group), Task<Entry>> Entries = [];

    /// <summary>The group's members (agents, not end users) and assigned open tickets, read at most <paramref name="maxAge"/> ago.</summary>
    public static async Task<Entry> GetAsync(ZendeskClient client, string subdomain, long groupId, TimeSpan maxAge, CancellationToken ct)
    {
        Task<Entry> task;
        lock (Gate)
        {
            var key = (subdomain, groupId);
            if (!Entries.TryGetValue(key, out task!) || task.IsFaulted || task.IsCanceled ||
                task.IsCompletedSuccessfully && DateTime.UtcNow - task.Result.At > maxAge)
            {
                task = ReadAsync(client, groupId);
                Entries[key] = task;
            }
        }
        return await task.WaitAsync(ct);
    }

    /// <summary>Forgets every group (after an assign: someone's count changed).</summary>
    public static void Clear()
    {
        lock (Gate)
            Entries.Clear();
    }

    /// <summary>Not tied to one caller's cancellation: the read is shared, so it runs to the end.</summary>
    private static async Task<Entry> ReadAsync(ZendeskClient client, long groupId)
    {
        var members = client.GetGroupMembersAsync(groupId, CancellationToken.None);
        var tickets = client.GetAssignedOpenTicketsAsync(groupId, CancellationToken.None);
        await Task.WhenAll(members, tickets);
        return new Entry(DateTime.UtcNow, [.. (await members).Where(u => u.Role != "end-user").DistinctBy(u => u.Id)], await tickets);
    }
}

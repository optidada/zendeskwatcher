using ZendeskWatcher.Zendesk;

namespace ZendeskWatcher.Tests;

/// <summary>A ticket's history is read again only when the ticket changed (its "updated at" moved): never out of date.</summary>
public class AuditCacheTests
{
    [Fact]
    public async Task History_is_reused_while_the_ticket_is_unchanged_and_read_again_once_it_changes()
    {
        var settings = new Settings { Subdomain = "test" };
        var zendesk = new FakeZendesk();
        zendesk.AddAssigned(300, "normal", assignee: 2);
        zendesk.Reply(300, 9, "First");
        var client = new ZendeskClient(settings, zendesk);
        var since = DateTimeOffset.UtcNow.AddDays(-1);
        int Reads() => zendesk.Requested.Count(u => u.Contains("/tickets/300/audits"));

        var first = await client.GetAuditsSinceAsync(300, since, CancellationToken.None, zendesk.Tickets[300].UpdatedAt);
        var again = await client.GetAuditsSinceAsync(300, since, CancellationToken.None, zendesk.Tickets[300].UpdatedAt);
        Assert.Equal(1, Reads()); // unchanged: the same history, not read twice
        Assert.Equal(first.Count, again.Count);

        zendesk.Reply(300, 9, "Second"); // a new comment moves "updated at"
        var changed = await client.GetAuditsSinceAsync(300, since, CancellationToken.None, zendesk.Tickets[300].UpdatedAt);
        Assert.Equal(2, Reads());
        Assert.Equal(first.Count + 1, changed.Count);

        await client.GetAuditsSinceAsync(300, since.AddDays(-1), CancellationToken.None, zendesk.Tickets[300].UpdatedAt);
        Assert.Equal(3, Reads()); // asked from further back than what was read: read again
        await client.GetAuditsSinceAsync(300, since, CancellationToken.None);
        Assert.Equal(4, Reads()); // without "updated at" it can't tell: always read
    }
}
